using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Models;
using AlCellControl.Services;
using AlCellControl.Commands;
using MediatR;

namespace AlCellControl.Controllers;

[ApiController]
[Route("api/celldata")]
public class CellDataController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ZigBeeReceiver _zigBeeReceiver;
    private readonly CellBufferService _cellBufferService;

    public CellDataController(
        IServiceProvider serviceProvider,
        ZigBeeReceiver zigBeeReceiver,
        CellBufferService cellBufferService)
    {
        _serviceProvider = serviceProvider;
        _zigBeeReceiver = zigBeeReceiver;
        _cellBufferService = cellBufferService;
    }

    [HttpPost("batch")]
    public async Task<IActionResult> Batch([FromBody] List<CellDataDto> data)
    {
        await _zigBeeReceiver.ReceiveBatchAsync(data);
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
            Current = ParseCurrentAvg(d.AnodeCurrentDistribution),
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

    private static double ParseCurrentAvg(string distribution)
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
}

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
