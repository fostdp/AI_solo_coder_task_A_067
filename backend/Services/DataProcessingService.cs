using AluminumCellControl.Data;
using AluminumCellControl.Hubs;
using AluminumCellControl.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class DataProcessingService
{
    private readonly AppDbContext _db;
    private readonly ConcentrationService _concentrationService;
    private readonly PredictionService _predictionService;
    private readonly AlarmService _alarmService;
    private readonly FeedingService _feedingService;
    private readonly MqttService _mqttService;
    private readonly DataBufferService _bufferService;
    private readonly IHubContext<CellHub> _hubContext;
    private readonly ILogger<DataProcessingService> _logger;
    private static int _globalProcessCounter = 0;

    public DataProcessingService(AppDbContext db, ConcentrationService concentrationService,
        PredictionService predictionService, AlarmService alarmService,
        FeedingService feedingService, MqttService mqttService,
        DataBufferService bufferService, IHubContext<CellHub> hubContext,
        ILogger<DataProcessingService> logger)
    {
        _db = db;
        _concentrationService = concentrationService;
        _predictionService = predictionService;
        _alarmService = alarmService;
        _feedingService = feedingService;
        _mqttService = mqttService;
        _bufferService = bufferService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task ProcessSensorDataAsync(SensorDataDto dto)
    {
        var data = new SensorData
        {
            CellId = dto.CellId,
            Timestamp = dto.Timestamp,
            Voltage = dto.Voltage,
            AnodeCurrentDistribution = dto.AnodeCurrentDistribution,
            CellTemp = dto.CellTemp,
            BathTemp = dto.BathTemp,
            AlLevel = dto.AlLevel,
            BathLevel = dto.BathLevel,
            VoltageNoise = CalculateVoltageNoise(dto.Voltage, dto.CellId),
            VoltageFluctuationFreq = EstimateFluctuationFreq(dto.CellId)
        };

        _db.SensorData.Add(data);

        var cell = await _db.Cells.FindAsync(dto.CellId);
        if (cell != null)
        {
            cell.LastDataTime = dto.Timestamp;
            cell.Status = cell.Status == "效应预警" ? "效应预警" : "正常";
        }

        UpdateBuffers(dto);
        UpdateFftFeatures(dto.CellId);

        var counter = Interlocked.Increment(ref _globalProcessCounter);
        var shouldEstimateConcentration = counter % 4 == 0;
        var shouldPredictEffect = counter % 8 == 0;

        if (shouldEstimateConcentration)
        {
            var concentration = await _concentrationService.EstimateConcentrationAsync(dto.CellId, data);

            if (concentration < 1.8m)
            {
                await _feedingService.AutoFeedAsync(dto.CellId, concentration,
                    $"氧化铝浓度{concentration}%低于1.8%，自动补料");
            }

            await _alarmService.CheckConcentrationAlarmAsync(dto.CellId, concentration);
        }

        if (shouldPredictEffect)
        {
            var probability = await _predictionService.PredictAnodeEffectAsync(dto.CellId, data);

            if (probability >= 0.8)
            {
                await _alarmService.TriggerAnodeEffectAlarmAsync(dto.CellId);
                await _feedingService.ExecuteEffectQuenchAsync(dto.CellId);
                await _mqttService.PublishEffectQuenchAsync(dto.CellId, "阳极效应自动熄灭程序已执行");
            }
        }

        await _db.SaveChangesAsync();

        await BroadcastCellStatusAsync(dto.CellId);
    }

    private void UpdateBuffers(SensorDataDto dto)
    {
        var voltage = (double)dto.Voltage;
        _bufferService.AddVoltage(dto.CellId, voltage);

        if (dto.AnodeCurrentDistribution != null)
        {
            try
            {
                var parts = dto.AnodeCurrentDistribution.Split(',');
                if (parts.Length > 0 && double.TryParse(parts[0], out var current))
                {
                    _bufferService.AddCurrent(dto.CellId, current);
                }
            }
            catch { }
        }

        var noise = (double)(dto.Voltage - 4.0m);
        _bufferService.AddNoise(dto.CellId, Math.Abs(noise));

        var voltages = _bufferService.GetVoltages(dto.CellId);
        if (voltages.Count >= 10)
        {
            var recent = voltages.TakeLast(10).ToList();
            var older = voltages.TakeLast(20).Take(10).ToList();
            if (older.Count >= 5)
            {
                var slope = (recent.Average() - older.Average()) / recent.Count;
                _bufferService.UpdateSlope(dto.CellId, slope);
            }
        }
    }

    private decimal? CalculateVoltageNoise(decimal voltage, int cellId)
    {
        var voltages = _bufferService.GetVoltages(cellId);
        if (voltages.Count < 5) return null;

        var recent = voltages.TakeLast(20).ToList();
        var mean = recent.Average();
        var variance = recent.Sum(v => (v - mean) * (v - mean)) / (recent.Count - 1);
        return (decimal)Math.Round(Math.Sqrt(variance), 4);
    }

    private decimal? EstimateFluctuationFreq(int cellId)
    {
        var voltages = _bufferService.GetVoltages(cellId);
        if (voltages.Count < 20) return 0.5m;

        var recent = voltages.TakeLast(60).ToList();
        int crossings = 0;
        var mean = recent.Average();
        for (int i = 1; i < recent.Count; i++)
        {
            if ((recent[i - 1] - mean) * (recent[i] - mean) < 0) crossings++;
        }

        var duration = recent.Count * 15.0 / 60.0;
        return (decimal)Math.Round(crossings / (2 * duration), 3);
    }

    private void UpdateFftFeatures(int cellId)
    {
        var voltages = _bufferService.GetVoltages(cellId);
        if (voltages.Count < 32) return;

        var voltageArray = voltages.TakeLast(64).ToArray();
        var mean = voltageArray.Average();
        var detrended = voltageArray.Select(v => v - mean).ToArray();
        var spectrum = SvrConcentrationModel.ComputeFft(detrended);
        var samplingRateHz = 1.0 / 15.0;
        var features = SvrConcentrationModel.ExtractFftFeatures(spectrum, samplingRateHz);
        _bufferService.UpdateFftFeatures(cellId, spectrum, features.DominantFreq, features.SpectralEnergy, features.HighFreqRatio);
    }

    private async Task BroadcastCellStatusAsync(int cellId)
    {
        var cell = await _db.Cells.FindAsync(cellId);
        if (cell == null) return;

        var status = new CellStatusDto
        {
            CellId = cell.CellId,
            CellName = cell.CellName,
            RowIndex = cell.RowIndex,
            ColIndex = cell.ColIndex,
            Status = cell.Status,
            Concentration = cell.Concentration,
            ConcentrationStatus = cell.ConcentrationStatus,
            AnodeEffectProbability = cell.AnodeEffectProbability,
            LastDataTime = cell.LastDataTime,
            Voltage = (await _db.SensorData
                .Where(s => s.CellId == cellId)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync())?.Voltage,
            IsFlashing = cell.AnodeEffectProbability >= 80
        };

        await _hubContext.Clients.All.SendAsync("CellStatusUpdate", status);
        await _mqttService.PublishCellStatusAsync(cellId, status);
    }
}
