using System.Collections.Concurrent;
using System.Text.Json;
using AlCellControl.Commands;
using AlCellControl.Data;
using AlCellControl.Events;
using AlCellControl.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AlCellControl.Services;

public class AlarmOrchestrator :
    INotificationHandler<ConcentrationEstimatedEvent>,
    INotificationHandler<AnodeEffectPredictedEvent>,
    IRequestHandler<AutoFeedCommand, AutoFeedResult>,
    IRequestHandler<ExtinguishEffectCommand, ExtinguishEffectResult>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMediator _mediator;
    private readonly MqttPublisherService _mqttPublisher;
    private readonly ILogger<AlarmOrchestrator> _logger;
    private readonly ConcurrentDictionary<int, byte> _extinguishCommandIssued = new();

    private readonly double _feedThreshold;
    private readonly double _feedAmountKg;
    private readonly double _alarmThreshold;
    private readonly double _extinguishAluminaAmount;

    public AlarmOrchestrator(
        IServiceProvider serviceProvider,
        IMediator mediator,
        MqttPublisherService mqttPublisher,
        IConfiguration configuration,
        ILogger<AlarmOrchestrator> logger)
    {
        _serviceProvider = serviceProvider;
        _mediator = mediator;
        _mqttPublisher = mqttPublisher;
        _logger = logger;

        _feedThreshold = configuration.GetValue("svr_model:FeedThreshold", 1.8);
        _feedAmountKg = configuration.GetValue("svr_model:FeedAmountKg", 1.8);
        _alarmThreshold = configuration.GetValue("rf_model:AlarmThreshold", 0.80);
        _extinguishAluminaAmount = configuration.GetValue("rf_model:ExtinguishAluminaAmount", 2.5);
    }

    public async Task Handle(ConcentrationEstimatedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.EstimatedConcentration < _feedThreshold)
        {
            await _mediator.Send(new AutoFeedCommand
            {
                CellId = notification.CellId,
                FeedAmountKg = _feedAmountKg
            }, cancellationToken);
        }

        if (notification.EstimatedConcentration < 1.5)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            var history = await dbContext.AluminaConcentrationHistory
                .Where(h => h.CellId == notification.CellId && h.EstimatedAt >= cutoff)
                .ToListAsync(cancellationToken);

            if (history.Count > 0 && history.All(h => h.EstimatedConcentration < 1.5))
            {
                var hasActiveAlarm = await dbContext.AlarmRecords
                    .AnyAsync(a => a.CellId == notification.CellId
                        && a.AlarmLevel == 1
                        && a.AlarmType == "ConcentrationLow"
                        && !a.IsAcknowledged, cancellationToken);

                if (!hasActiveAlarm)
                {
                    var alarm = new AlarmRecord
                    {
                        CellId = notification.CellId,
                        AlarmLevel = 1,
                        AlarmType = "ConcentrationLow",
                        AlarmMessage = $"Cell {notification.CellId} alumina concentration critically low: {notification.EstimatedConcentration:F2}%",
                        IsAcknowledged = false,
                        TriggeredAt = DateTime.UtcNow
                    };

                    dbContext.AlarmRecords.Add(alarm);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    await _mediator.Publish(new AlarmTriggeredEvent
                    {
                        CellId = notification.CellId,
                        AlarmLevel = 1,
                        AlarmType = "ConcentrationLow",
                        AlarmMessage = alarm.AlarmMessage,
                        TriggeredAt = alarm.TriggeredAt
                    }, cancellationToken);

                    await _mqttPublisher.PublishAlarmAsync(new AlarmMessage(
                        notification.CellId,
                        1,
                        "ConcentrationLow",
                        alarm.AlarmMessage,
                        alarm.TriggeredAt
                    ));
                }
            }
        }
    }

    public async Task Handle(AnodeEffectPredictedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.Probability > _alarmThreshold)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var hasActiveAlarm = await dbContext.AlarmRecords
                .AnyAsync(a => a.CellId == notification.CellId
                    && a.AlarmLevel == 2
                    && a.AlarmType == "AnodeEffect"
                    && !a.IsAcknowledged, cancellationToken);

            if (!hasActiveAlarm)
            {
                var alarm = new AlarmRecord
                {
                    CellId = notification.CellId,
                    AlarmLevel = 2,
                    AlarmType = "AnodeEffect",
                    AlarmMessage = $"Cell {notification.CellId} anode effect predicted: probability {notification.Probability:P0}",
                    IsAcknowledged = false,
                    TriggeredAt = DateTime.UtcNow
                };

                dbContext.AlarmRecords.Add(alarm);
                await dbContext.SaveChangesAsync(cancellationToken);

                await _mediator.Publish(new AlarmTriggeredEvent
                {
                    CellId = notification.CellId,
                    AlarmLevel = 2,
                    AlarmType = "AnodeEffect",
                    AlarmMessage = alarm.AlarmMessage,
                    TriggeredAt = alarm.TriggeredAt
                }, cancellationToken);

                await _mqttPublisher.PublishAlarmAsync(new AlarmMessage(
                    notification.CellId,
                    2,
                    "AnodeEffect",
                    alarm.AlarmMessage,
                    alarm.TriggeredAt
                ));
            }

            if (!_extinguishCommandIssued.ContainsKey(notification.CellId))
            {
                await _mediator.Send(new ExtinguishEffectCommand
                {
                    CellId = notification.CellId,
                    AluminaAmount = _extinguishAluminaAmount
                }, cancellationToken);
            }
        }

        await CleanupExtinguishTrackingAsync(cancellationToken);
    }

    private async Task CleanupExtinguishTrackingAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var acknowledgedCellIds = await dbContext.AlarmRecords
            .Where(a => a.AlarmLevel == 2 && a.AlarmType == "AnodeEffect" && a.IsAcknowledged)
            .Select(a => a.CellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var cellId in acknowledgedCellIds)
        {
            _extinguishCommandIssued.TryRemove(cellId, out _);
        }
    }

    public async Task<AutoFeedResult> Handle(AutoFeedCommand request, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feedingRecord = new FeedingRecord
        {
            CellId = request.CellId,
            FeedType = "CrustBreak",
            FeedAmountKg = request.FeedAmountKg,
            FedAt = DateTime.UtcNow
        };

        dbContext.FeedingRecords.Add(feedingRecord);

        var commandParams = new
        {
            request.CellId,
            FeedAmount = request.FeedAmountKg
        };

        var controlCommand = new CellControlCommand
        {
            CellId = request.CellId,
            CommandType = "AutoFeed",
            CommandParams = JsonSerializer.Serialize(commandParams),
            Status = "Pending",
            IssuedAt = DateTime.UtcNow
        };

        dbContext.CellControlCommands.Add(controlCommand);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AutoFeedResult(true, feedingRecord.Id, controlCommand.Id);
    }

    public async Task<ExtinguishEffectResult> Handle(ExtinguishEffectCommand request, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var commandParams = new
        {
            request.CellId,
            Procedure = "InsertAlumina",
            Amount = request.AluminaAmount
        };

        var controlCommand = new CellControlCommand
        {
            CellId = request.CellId,
            CommandType = "ExtinguishEffect",
            CommandParams = JsonSerializer.Serialize(commandParams),
            Status = "Pending",
            IssuedAt = DateTime.UtcNow
        };

        dbContext.CellControlCommands.Add(controlCommand);
        await dbContext.SaveChangesAsync(cancellationToken);

        _extinguishCommandIssued.TryAdd(request.CellId, 0);

        return new ExtinguishEffectResult(true, controlCommand.Id);
    }
}
