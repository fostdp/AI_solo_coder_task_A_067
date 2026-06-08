using AlCellControl.Data;
using AlCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AlCellControl.Services;

public class ConcentrationMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConcentrationMonitorService> _logger;
    private const double LowConcentrationThreshold = 1.5;
    private static readonly TimeSpan ContinuousDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public ConcentrationMonitorService(IServiceProvider serviceProvider, ILogger<ConcentrationMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Concentration monitor service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckConcentrationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in concentration monitoring cycle");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckConcentrationsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mqttPublisher = scope.ServiceProvider.GetRequiredService<MqttPublisherService>();

        var cells = await dbContext.CellInfos.ToListAsync();
        var cutoff = DateTime.UtcNow - ContinuousDuration;

        foreach (var cell in cells)
        {
            var recentData = await dbContext.AluminaConcentrationHistory
                .Where(h => h.CellId == cell.CellId && h.EstimatedAt >= cutoff)
                .OrderBy(h => h.EstimatedAt)
                .ToListAsync();

            if (recentData.Count == 0)
                continue;

            var latest = recentData.OrderByDescending(h => h.EstimatedAt).First();
            if (latest.EstimatedConcentration >= LowConcentrationThreshold)
                continue;

            var allBelowThreshold = recentData
                .All(h => h.EstimatedConcentration < LowConcentrationThreshold);

            if (!allBelowThreshold)
                continue;

            var earliestInWindow = recentData.Min(h => h.EstimatedAt);
            if (earliestInWindow > cutoff)
                continue;

            var hasActiveAlarm = await dbContext.AlarmRecords
                .AnyAsync(a => a.CellId == cell.CellId
                    && a.AlarmLevel == 1
                    && a.AlarmType == "ConcentrationLow"
                    && !a.IsAcknowledged);

            if (hasActiveAlarm)
                continue;

            var alarm = new AlarmRecord
            {
                CellId = cell.CellId,
                AlarmLevel = 1,
                AlarmType = "ConcentrationLow",
                AlarmMessage = $"Cell {cell.CellName} alumina concentration critically low: {latest.EstimatedConcentration:F2}%",
                IsAcknowledged = false,
                TriggeredAt = DateTime.UtcNow
            };

            dbContext.AlarmRecords.Add(alarm);
            await dbContext.SaveChangesAsync();

            var alarmMessage = new AlarmMessage(
                cell.CellId,
                1,
                "ConcentrationLow",
                alarm.AlarmMessage,
                alarm.TriggeredAt
            );

            await mqttPublisher.PublishAlarmAsync(alarmMessage);
            _logger.LogInformation("Created level-1 alarm for cell {CellId}: concentration {Concentration:F2}%",
                cell.CellId, latest.EstimatedConcentration);
        }
    }
}
