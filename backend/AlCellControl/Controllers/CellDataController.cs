using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Models;
using AlCellControl.Services;

namespace AlCellControl.Controllers;

[ApiController]
[Route("api/celldata")]
public class CellDataController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AluminaConcentrationEstimator _concentrationEstimator;
    private readonly AnodeEffectPredictor _anodeEffectPredictor;
    private readonly CellBufferService _cellBufferService;

    public CellDataController(
        IServiceProvider serviceProvider,
        AluminaConcentrationEstimator concentrationEstimator,
        AnodeEffectPredictor anodeEffectPredictor,
        CellBufferService cellBufferService)
    {
        _serviceProvider = serviceProvider;
        _concentrationEstimator = concentrationEstimator;
        _anodeEffectPredictor = anodeEffectPredictor;
        _cellBufferService = cellBufferService;
    }

    [HttpPost("batch")]
    public async Task<IActionResult> Batch([FromBody] List<CellDataDto> data)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var item in data)
        {
            var currentAvg = ParseCurrentDistribution(item.AnodeCurrentDistribution);

            _cellBufferService.Write(item.CellId, item.Voltage, currentAvg);

            var realtimeData = new CellRealtimeData
            {
                CellId = item.CellId,
                Voltage = item.Voltage,
                AnodeCurrentDistribution = item.AnodeCurrentDistribution,
                CellTemperature = item.CellTemperature,
                BathTemperature = item.BathTemperature,
                AluminumLevel = item.AluminumLevel,
                BathLevel = item.BathLevel,
                ReceivedAt = DateTime.UtcNow
            };

            db.CellRealtimeData.Add(realtimeData);
            await db.SaveChangesAsync();

            var voltages = _cellBufferService.ReadVoltages(item.CellId, 20);
            var currents = _cellBufferService.ReadCurrents(item.CellId, 20);

            if (voltages.Length >= 2)
            {
                var concentration = _concentrationEstimator.Estimate(voltages, currents);

                var concentrationHistory = new AluminaConcentrationHistory
                {
                    CellId = item.CellId,
                    EstimatedConcentration = concentration,
                    ModelVersion = "SVM-v2-FFT",
                    EstimatedAt = DateTime.UtcNow
                };
                db.AluminaConcentrationHistory.Add(concentrationHistory);

                realtimeData.AluminaConcentration = concentration;
                db.CellRealtimeData.Update(realtimeData);

                if (concentration < 1.8)
                {
                    var feedingRecord = new FeedingRecord
                    {
                        CellId = item.CellId,
                        FeedType = "CrustBreak",
                        FeedAmountKg = 1.8,
                        FedAt = DateTime.UtcNow
                    };
                    db.FeedingRecords.Add(feedingRecord);

                    var controlCommand = new CellControlCommand
                    {
                        CellId = item.CellId,
                        CommandType = "AutoFeed",
                        CommandParams = $"{{\"feedType\":\"CrustBreak\",\"feedAmountKg\":1.8}}",
                        IssuedAt = DateTime.UtcNow,
                        Status = "Pending"
                    };
                    db.CellControlCommands.Add(controlCommand);
                }

                await db.SaveChangesAsync();
            }

            var last60Voltages = _cellBufferService.ReadVoltages(item.CellId, 60);

            if (last60Voltages.Length >= 2)
            {
                var probability = _anodeEffectPredictor.Predict(last60Voltages, item.CellTemperature);

                var prediction = new AnodeEffectPrediction
                {
                    CellId = item.CellId,
                    Probability = probability,
                    PredictedAt = DateTime.UtcNow
                };
                db.AnodeEffectPredictions.Add(prediction);

                _anodeEffectPredictor.RecordPrediction(item.CellId, probability);

                if (probability > 0.8)
                {
                    var alarm = new AlarmRecord
                    {
                        CellId = item.CellId,
                        AlarmLevel = 2,
                        AlarmType = "AnodeEffect",
                        AlarmMessage = $"Anode effect probability {probability:P1} exceeds threshold",
                        TriggeredAt = DateTime.UtcNow,
                        IsAcknowledged = false
                    };
                    db.AlarmRecords.Add(alarm);
                }

                await db.SaveChangesAsync();
            }
        }

        return Ok();
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cells = await db.CellInfos.ToListAsync();
        var result = new List<CellOverviewDto>();

        foreach (var cell in cells)
        {
            var latestData = await db.CellRealtimeData
                .Where(d => d.CellId == cell.CellId)
                .OrderByDescending(d => d.ReceivedAt)
                .FirstOrDefaultAsync();

            var latestPrediction = await db.AnodeEffectPredictions
                .Where(p => p.CellId == cell.CellId)
                .OrderByDescending(p => p.PredictedAt)
                .FirstOrDefaultAsync();

            var latestAlarm = await db.AlarmRecords
                .Where(a => a.CellId == cell.CellId && !a.IsAcknowledged)
                .OrderByDescending(a => a.TriggeredAt)
                .FirstOrDefaultAsync();

            result.Add(new CellOverviewDto(
                cell.CellId,
                cell.CellName,
                cell.RowIndex,
                cell.ColIndex,
                cell.Zone,
                latestData?.Voltage ?? 0,
                latestData?.AluminaConcentration ?? 0,
                latestPrediction?.Probability ?? 0,
                latestAlarm != null ? latestAlarm.AlarmType : null,
                latestAlarm != null ? latestAlarm.AlarmLevel : (int?)null
            ));
        }

        return Ok(result);
    }

    [HttpGet("{cellId}/trend")]
    public async Task<IActionResult> Trend(int cellId, [FromQuery] int hours = 8)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.AddHours(-hours);

        var voltageData = await db.CellRealtimeData
            .Where(d => d.CellId == cellId && d.ReceivedAt >= since)
            .OrderBy(d => d.ReceivedAt)
            .Select(d => new { d.Voltage, d.AnodeCurrentDistribution, d.ReceivedAt })
            .ToListAsync();

        var feedingRecords = await db.FeedingRecords
            .Where(f => f.CellId == cellId)
            .OrderByDescending(f => f.FedAt)
            .Take(10)
            .Select(f => new FeedingRecordDto(f.CellId, f.FeedType, f.FeedAmountKg, f.FedAt))
            .ToListAsync();

        var trendData = voltageData.Select(d => new
        {
            d.Voltage,
            Current = ParseCurrentAverage(d.AnodeCurrentDistribution),
            d.ReceivedAt
        }).ToList();

        return Ok(new CellTrendDto(trendData, feedingRecords));
    }

    [HttpGet("{cellId}/latest")]
    public async Task<IActionResult> Latest(int cellId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var latest = await db.CellRealtimeData
            .Where(d => d.CellId == cellId)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync();

        if (latest == null)
            return NotFound();

        return Ok(latest);
    }

    private static double ParseCurrentDistribution(string distribution)
    {
        if (string.IsNullOrWhiteSpace(distribution))
            return 0;

        var cleaned = distribution.Trim().TrimStart('[').TrimEnd(']');
        var parts = cleaned.Split(',');
        if (parts.Length == 0)
            return 0;

        double sum = 0;
        int count = 0;
        foreach (var part in parts)
        {
            if (double.TryParse(part.Trim(), out var val))
            {
                sum += val;
                count++;
            }
        }

        return count > 0 ? sum / count : 0;
    }

    private static double ParseCurrentAverage(string distribution)
    {
        return ParseCurrentDistribution(distribution);
    }
}

public record CellDataDto(
    int CellId,
    double Voltage,
    string AnodeCurrentDistribution,
    double CellTemperature,
    double BathTemperature,
    double AluminumLevel,
    double BathLevel
);

public record CellOverviewDto(
    int CellId,
    string CellName,
    int RowIndex,
    int ColIndex,
    string Zone,
    double Voltage,
    double AluminaConcentration,
    double AnodeEffectProbability,
    string? LatestAlarmType,
    int? LatestAlarmLevel
);

public record FeedingRecordDto(
    int CellId,
    string FeedType,
    double FeedAmountKg,
    DateTime FedAt
);

public record CellTrendDto(
    object VoltageCurrentData,
    List<FeedingRecordDto> FeedingRecords
);
