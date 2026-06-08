using System.Text.Json;
using AlCellControl.Data;
using AlCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AlCellControl.Services;

public class AnodeEffectMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AnodeEffectMonitorService> _logger;
    private readonly HashSet<int> _extinguishCommandIssued = new();
    private const double HighProbabilityThreshold = 0.80;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    public AnodeEffectMonitorService(IServiceProvider serviceProvider, ILogger<AnodeEffectMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Anode effect monitor service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAnodeEffectPredictionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in anode effect monitoring cycle");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAnodeEffectPredictionsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mqttPublisher = scope.ServiceProvider.GetRequiredService<MqttPublisherService>();

        var cells = await dbContext.CellInfos.ToListAsync();

        foreach (var cell in cells)
        {
            var latestPrediction = await dbContext.AnodeEffectPredictions
                .Where(p => p.CellId == cell.CellId)
                .OrderByDescending(p => p.PredictedAt)
                .FirstOrDefaultAsync();

            if (latestPrediction == null || latestPrediction.Probability <= HighProbabilityThreshold)
                continue;

            var hasActiveAlarm = await dbContext.AlarmRecords
                .AnyAsync(a => a.CellId == cell.CellId
                    && a.AlarmLevel == 2
                    && a.AlarmType == "AnodeEffect"
                    && !a.IsAcknowledged);

            if (!hasActiveAlarm)
            {
                var alarm = new AlarmRecord
                {
                    CellId = cell.CellId,
                    AlarmLevel = 2,
                    AlarmType = "AnodeEffect",
                    AlarmMessage = $"Cell {cell.CellName} anode effect predicted: probability {latestPrediction.Probability:P0}",
                    IsAcknowledged = false,
                    TriggeredAt = DateTime.UtcNow
                };

                dbContext.AlarmRecords.Add(alarm);

                var alarmMessage = new AlarmMessage(
                    cell.CellId,
                    2,
                    "AnodeEffect",
                    alarm.AlarmMessage,
                    alarm.TriggeredAt
                );

                await mqttPublisher.PublishAlarmAsync(alarmMessage);
                _logger.LogInformation("Created level-2 alarm for cell {CellId}: anode effect probability {Probability:P0}",
                    cell.CellId, latestPrediction.Probability);
            }

            if (!_extinguishCommandIssued.Contains(cell.CellId))
            {
                var commandParams = new
                {
                    CellId = cell.CellId,
                    Procedure = "InsertAlumina",
                    Amount = 2.5
                };

                var command = new CellControlCommand
                {
                    CellId = cell.CellId,
                    CommandType = "ExtinguishEffect",
                    CommandParams = JsonSerializer.Serialize(commandParams),
                    Status = "Pending",
                    IssuedAt = DateTime.UtcNow
                };

                dbContext.CellControlCommands.Add(command);
                _extinguishCommandIssued.Add(cell.CellId);

                _logger.LogInformation("Issued ExtinguishEffect command for cell {CellId}", cell.CellId);
            }

            await dbContext.SaveChangesAsync();
        }

        var acknowledgedCellIds = await dbContext.AlarmRecords
            .Where(a => a.AlarmLevel == 2 && a.AlarmType == "AnodeEffect" && a.IsAcknowledged)
            .Select(a => a.CellId)
            .Distinct()
            .ToListAsync();

        foreach (var cellId in acknowledgedCellIds)
        {
            _extinguishCommandIssued.Remove(cellId);
        }
    }
}
