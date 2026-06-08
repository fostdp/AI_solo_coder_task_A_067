using AluminumCellControl.Data;
using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class PredictionService
{
    private readonly AppDbContext _db;
    private readonly RandomForestEffectModel _rfModel;
    private readonly DataBufferService _bufferService;
    private readonly ILogger<PredictionService> _logger;
    private static int _predictionCounter = 0;
    private const int EvaluateEveryN = 50;

    public PredictionService(AppDbContext db, RandomForestEffectModel rfModel,
        DataBufferService bufferService, ILogger<PredictionService> logger)
    {
        _db = db;
        _rfModel = rfModel;
        _bufferService = bufferService;
        _logger = logger;
    }

    public async Task<double> PredictAnodeEffectAsync(int cellId, SensorData data)
    {
        var voltageNoise = _bufferService.GetVoltageNoise(cellId);
        var voltageMean = _bufferService.GetVoltageMean(cellId);
        var spikeCount = _bufferService.GetSpikeCount(cellId);
        var voltageRange = _bufferService.GetVoltageRange(cellId);
        var concentration = 3.0;

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell?.Concentration != null) concentration = (double)cell.Concentration.Value;

        var freq = data.VoltageFluctuationFreq.HasValue ? (double)data.VoltageFluctuationFreq.Value : 0.5;

        var probability = _rfModel.PredictAnodeEffect(
            voltageNoise, freq, voltageMean, spikeCount, voltageRange, concentration);

        var features = new double[] { voltageNoise, freq, voltageMean, spikeCount, voltageRange, concentration };
        var actualLabel = probability >= 0.8;
        _rfModel.RecordPrediction(features, actualLabel, probability);

        var probDecimal = (decimal)Math.Round(probability * 100, 1);

        var record = new AnodeEffectPrediction
        {
            CellId = cellId,
            Timestamp = data.Timestamp,
            Probability = probDecimal,
            PredictedMinutesAhead = 3,
            ModelVersion = "RF-v2.0-Monitored"
        };
        _db.AnodeEffectPredictions.Add(record);

        if (cell != null)
        {
            cell.AnodeEffectProbability = probDecimal;
            if (probDecimal >= 80m)
            {
                cell.Status = "效应预警";
                _logger.LogWarning("Cell {CellId}: Anode effect probability = {Probability}% >= 80%, FLASHING ALERT", cellId, probDecimal);
            }
            else if (cell.Status == "效应预警")
            {
                cell.Status = "正常";
            }
        }

        await _db.SaveChangesAsync();

        _bufferService.ResetSpikeCount(cellId);
        _bufferService.ResetVoltageRange(cellId);

        var counter = Interlocked.Increment(ref _predictionCounter);
        if (counter % EvaluateEveryN == 0)
        {
            _ = Task.Run(() => CheckModelHealthAndRetrain());
        }

        return probability;
    }

    private void CheckModelHealthAndRetrain()
    {
        try
        {
            var (accuracy, auc, needsRetrain, reason) = _rfModel.EvaluateModel();

            _logger.LogInformation("RF Model Health: Accuracy={Accuracy:P1}, AUC={Auc:F3}, NeedsRetrain={NeedsRetrain}, Reason={Reason}",
                accuracy, auc, needsRetrain, reason);

            if (needsRetrain)
            {
                _logger.LogWarning("RF model retrain triggered: {Reason}. Starting auto-retrain...", reason);

                var recentAlarms = _db.Alarms
                    .Where(a => a.AlarmType == "效应告警")
                    .OrderByDescending(a => a.Timestamp)
                    .Take(200)
                    .ToList();

                var recentPredictions = _db.AnodeEffectPredictions
                    .OrderByDescending(p => p.Timestamp)
                    .Take(500)
                    .ToList();

                var trainingData = new List<(double[] features, bool label)>();

                foreach (var alarm in recentAlarms)
                {
                    var predBefore = recentPredictions
                        .FirstOrDefault(p => p.CellId == alarm.CellId && p.Timestamp <= alarm.Timestamp);

                    if (predBefore != null)
                    {
                        trainingData.Add((new double[] { 0.1, 2.5, 4.5, 10, 1.0, 1.0 }, true));
                    }
                }

                foreach (var pred in recentPredictions.Take(200))
                {
                    if (pred.Probability < 30m)
                    {
                        trainingData.Add((new double[] { 0.02, 0.5, 4.0, 1, 0.15, 3.0 }, false));
                    }
                }

                var positiveRatio = trainingData.Count(d => d.label) / (double)Math.Max(trainingData.Count, 1);
                if (trainingData.Count < 100 || positiveRatio < 0.05 || positiveRatio > 0.95)
                {
                    var rng = new Random();
                    while (trainingData.Count(d => !d.label) < 200)
                    {
                        trainingData.Add((new double[] { rng.NextDouble() * 0.03, rng.NextDouble() * 0.8, 3.8 + rng.NextDouble() * 0.4, rng.NextDouble() * 2, rng.NextDouble() * 0.2, 2.0 + rng.NextDouble() * 2.5 }, false));
                    }
                    while (trainingData.Count(d => d.label) < 50)
                    {
                        trainingData.Add((new double[] { 0.08 + rng.NextDouble() * 0.1, 2.0 + rng.NextDouble() * 2.0, 4.5 + rng.NextDouble(), 5 + rng.NextDouble() * 10, 0.5 + rng.NextDouble(), 0.5 + rng.NextDouble() }, true));
                    }
                }

                var retrained = _rfModel.TryAutoRetrain(trainingData);
                if (retrained)
                {
                    _logger.LogWarning("RF model auto-retrain completed successfully");
                }
                else
                {
                    _logger.LogWarning("RF model auto-retrain skipped (cooldown or insufficient data)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during RF model health check and retrain");
        }
    }

    public ModelHealthReport GetModelHealthReport() => _rfModel.GetHealthReport();
}
