using System.Text.Json;
using System.Text.Json.Serialization;
using AlCellControl.Commands;
using AlCellControl.Data;
using AlCellControl.Events;
using AlCellControl.Models;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Statistics;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AlCellControl.Services;

public class SvrSupportVector
{
    public double Alpha { get; set; }
    public double[] Features { get; set; } = [];
}

public class SvrModelConfig
{
    public double Gamma { get; set; }
    public double Bias { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public double OutputMin { get; set; }
    public double OutputMax { get; set; }
    public double[] NormalizationScale { get; set; } = [];
    public double[] NormalizationOffset { get; set; } = [];
    public string[] FeatureNames { get; set; } = [];
    public List<SvrSupportVector> SupportVectors { get; set; } = [];
    public double FeedThreshold { get; set; }
    public double FeedAmountKg { get; set; }
    public int VoltageSampleCount { get; set; }
}

public class ConcentrationEstimator : INotificationHandler<CellDataReceivedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CellBufferService _cellBufferService;
    private readonly IMediator _mediator;
    private readonly ILogger<ConcentrationEstimator> _logger;
    private readonly SvrModelConfig _config;

    public ConcentrationEstimator(
        IServiceProvider serviceProvider,
        CellBufferService cellBufferService,
        IMediator mediator,
        IConfiguration configuration,
        ILogger<ConcentrationEstimator> logger)
    {
        _serviceProvider = serviceProvider;
        _cellBufferService = cellBufferService;
        _mediator = mediator;
        _logger = logger;

        var svrPath = configuration["ModelConfig:SvrPath"] ?? "Configuration/svr_model.json";
        var fullPath = Path.Combine(AppContext.BaseDirectory, svrPath);
        var json = File.ReadAllText(fullPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        _config = JsonSerializer.Deserialize<SvrModelConfig>(json, options)
            ?? throw new InvalidOperationException("Failed to load SVR model config");
    }

    public async Task Handle(CellDataReceivedEvent notification, CancellationToken cancellationToken)
    {
        var voltages = _cellBufferService.ReadVoltages(notification.CellId, _config.VoltageSampleCount);
        var currents = _cellBufferService.ReadCurrents(notification.CellId, _config.VoltageSampleCount);

        if (voltages.Length < 2 || currents.Length < 2)
            return;

        var concentration = Estimate(voltages, currents);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var historyRecord = new AluminaConcentrationHistory
        {
            CellId = notification.CellId,
            EstimatedConcentration = concentration,
            ModelVersion = _config.ModelVersion,
            EstimatedAt = DateTime.UtcNow
        };
        db.AluminaConcentrationHistory.Add(historyRecord);

        var realtimeData = await db.CellRealtimeData
            .Where(d => d.CellId == notification.CellId)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (realtimeData != null)
        {
            realtimeData.AluminaConcentration = concentration;
        }

        await db.SaveChangesAsync(cancellationToken);

        await _mediator.Publish(new ConcentrationEstimatedEvent
        {
            CellId = notification.CellId,
            EstimatedConcentration = concentration,
            ModelVersion = _config.ModelVersion,
            EstimatedAt = DateTime.UtcNow,
            Voltage = notification.Voltage,
            CellTemperature = notification.CellTemperature
        }, cancellationToken);

        if (concentration < _config.FeedThreshold)
        {
            await _mediator.Send(new AutoFeedCommand
            {
                CellId = notification.CellId,
                FeedAmountKg = _config.FeedAmountKg
            }, cancellationToken);
        }
    }

    private double Estimate(double[] voltages, double[] currents)
    {
        var features = ExtractFeatures(voltages, currents);
        var normalized = NormalizeFeatures(features);

        double result = _config.Bias;
        foreach (var sv in _config.SupportVectors)
        {
            double kernel = RbfKernel(normalized, sv.Features);
            result += sv.Alpha * kernel;
        }

        return Math.Clamp(result, _config.OutputMin, _config.OutputMax);
    }

    private double[] ExtractFeatures(double[] voltages, double[] currents)
    {
        double vMean = voltages.Average();
        double vStd = voltages.StandardDeviation();
        double vSlope = ComputeSlope(voltages);
        double cMean = currents.Average();
        double cStd = currents.StandardDeviation();
        double correlation = ComputeCorrelation(voltages, currents);
        double vRange = voltages.Max() - voltages.Min();
        double vCv = vStd / vMean;

        var (spectralEnergy, dominantFreqMagnitude, highFreqRatio) = ExtractFftFeatures(voltages);

        return new double[] { vMean, vStd, vSlope, cMean, cStd, correlation, vRange, vCv, spectralEnergy, dominantFreqMagnitude, highFreqRatio };
    }

    private (double SpectralEnergy, double DominantFreqMagnitude, double HighFreqRatio) ExtractFftFeatures(double[] voltages)
    {
        if (voltages.Length < 4)
            return (0, 0, 0);

        int fftSize = 1;
        while (fftSize < voltages.Length) fftSize *= 2;

        var padded = new double[fftSize];
        double mean = voltages.Average();
        for (int i = 0; i < voltages.Length; i++)
            padded[i] = voltages[i] - mean;
        for (int i = voltages.Length; i < fftSize; i++)
            padded[i] = 0;

        var complexData = new System.Numerics.Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1)));
            complexData[i] = new System.Numerics.Complex(padded[i] * window, 0);
        }

        Fourier.Forward(complexData, FourierOptions.Matlab);

        int halfN = fftSize / 2;
        double totalEnergy = 0;
        double maxMag = 0;
        double highFreqEnergy = 0;
        int lowBandEnd = Math.Max(1, halfN / 4);

        for (int i = 1; i < halfN; i++)
        {
            double mag = complexData[i].Magnitude / fftSize;
            totalEnergy += mag * mag;
            if (mag > maxMag) maxMag = mag;
            if (i > lowBandEnd) highFreqEnergy += mag * mag;
        }

        double highFreqRatio = totalEnergy > 1e-12 ? highFreqEnergy / totalEnergy : 0;

        return (totalEnergy, maxMag, highFreqRatio);
    }

    private double[] NormalizeFeatures(double[] features)
    {
        var result = new double[features.Length];
        for (int i = 0; i < features.Length; i++)
        {
            result[i] = (features[i] - _config.NormalizationOffset[i]) * _config.NormalizationScale[i];
        }
        return result;
    }

    private double RbfKernel(double[] x, double[] y)
    {
        int len = Math.Min(x.Length, y.Length);
        double sumSq = 0.0;
        for (int i = 0; i < len; i++)
        {
            double diff = x[i] - y[i];
            sumSq += diff * diff;
        }
        return Math.Exp(-_config.Gamma * sumSq);
    }

    private static double ComputeSlope(double[] values)
    {
        int n = values.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumX2 += (double)i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return 0.0;
        return (n * sumXY - sumX * sumY) / denom;
    }

    private static double ComputeCorrelation(double[] x, double[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n < 2) return 0.0;

        double meanX = x.Take(n).Average();
        double meanY = y.Take(n).Average();

        double sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }

        double denom = Math.Sqrt(sumX2 * sumY2);
        if (denom < 1e-12) return 0.0;
        return sumXY / denom;
    }
}
