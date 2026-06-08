using AluminumCellControl.Data;
using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class FeedingService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FeedingService> _logger;

    public FeedingService(AppDbContext db, ILogger<FeedingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AutoFeedAsync(int cellId, decimal concentration, string reason)
    {
        var feedAmount = concentration switch
        {
            < 1.0m => 50m,
            < 1.5m => 40m,
            < 1.8m => 30m,
            _ => 25m
        };

        var record = new FeedingRecord
        {
            CellId = cellId,
            Timestamp = DateTime.UtcNow,
            FeedAmountKg = feedAmount,
            FeedType = "自动",
            TriggerReason = reason
        };
        _db.FeedingRecords.Add(record);

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell != null)
        {
            cell.Concentration = Math.Min(cell.Concentration ?? 0 + feedAmount * 0.04m, 6.0m);
            cell.ConcentrationStatus = cell.Concentration >= 2.5m ? "正常" :
                cell.Concentration >= 1.8m ? "偏低" : "极低";
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Cell {CellId}: Auto feeding {Amount}kg, Reason: {Reason}", cellId, feedAmount, reason);
    }

    public async Task ManualFeedAsync(int cellId, decimal amountKg, string reason)
    {
        var record = new FeedingRecord
        {
            CellId = cellId,
            Timestamp = DateTime.UtcNow,
            FeedAmountKg = amountKg,
            FeedType = "手动",
            TriggerReason = reason
        };
        _db.FeedingRecords.Add(record);

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell != null)
        {
            cell.Concentration = Math.Min((cell.Concentration ?? 0) + amountKg * 0.04m, 6.0m);
            cell.ConcentrationStatus = cell.Concentration >= 2.5m ? "正常" :
                cell.Concentration >= 1.8m ? "偏低" : "极低";
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Cell {CellId}: Manual feeding {Amount}kg, Reason: {Reason}", cellId, amountKg, reason);
    }

    public async Task ExecuteEffectQuenchAsync(int cellId)
    {
        var record = new FeedingRecord
        {
            CellId = cellId,
            Timestamp = DateTime.UtcNow,
            FeedAmountKg = 80m,
            FeedType = "效应熄灭",
            TriggerReason = "阳极效应自动熄灭程序：插入木棒短路+大剂量下料"
        };
        _db.FeedingRecords.Add(record);

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell != null)
        {
            cell.Concentration = 4.0m;
            cell.ConcentrationStatus = "正常";
            cell.Status = "正常";
            cell.AnodeEffectProbability = 0;
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("Cell {CellId}: Anode effect quench executed - emergency feeding 80kg + rod insertion", cellId);
    }
}
