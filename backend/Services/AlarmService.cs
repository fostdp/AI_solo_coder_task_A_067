using AluminumCellControl.Data;
using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class AlarmService
{
    private readonly AppDbContext _db;
    private readonly MqttService _mqttService;
    private readonly ILogger<AlarmService> _logger;

    public AlarmService(AppDbContext db, MqttService mqttService, ILogger<AlarmService> logger)
    {
        _db = db;
        _mqttService = mqttService;
        _logger = logger;
    }

    public async Task CheckConcentrationAlarmAsync(int cellId, decimal concentration)
    {
        var tracker = await _db.ConcentrationAlarmTrackers.FindAsync(cellId);
        if (tracker == null)
        {
            tracker = new ConcentrationAlarmTracker { CellId = cellId };
            _db.ConcentrationAlarmTrackers.Add(tracker);
        }

        if (concentration < 1.5m)
        {
            if (tracker.LowStartTime == null)
            {
                tracker.LowStartTime = DateTime.UtcNow;
                _logger.LogWarning("Cell {CellId}: Concentration dropped below 1.5%, tracking start time", cellId);
            }
            else
            {
                var duration = DateTime.UtcNow - tracker.LowStartTime.Value;
                if (duration.TotalMinutes >= 5 && !tracker.IsAlarmActive)
                {
                    tracker.IsAlarmActive = true;
                    await CreateAlarmAsync(cellId, "浓度告警", 1,
                        $"电解槽{cellId}号氧化铝浓度{concentration}%低于1.5%已持续{Math.Floor(duration.TotalMinutes)}分钟，触发一级浓度告警");

                    var existing = await _db.Alarms
                        .Where(a => a.CellId == cellId && a.AlarmType == "浓度告警" && !a.IsResolved)
                        .FirstOrDefaultAsync();
                    if (existing == null)
                    {
                        await _mqttService.PublishAlarmAsync(cellId, "一级浓度告警",
                            $"电解槽{cellId}号氧化铝浓度{concentration}%低于1.5%持续超过5分钟");
                    }
                }
            }
        }
        else
        {
            if (tracker.IsAlarmActive)
            {
                var activeAlarm = await _db.Alarms
                    .Where(a => a.CellId == cellId && a.AlarmType == "浓度告警" && !a.IsResolved)
                    .FirstOrDefaultAsync();
                if (activeAlarm != null)
                {
                    activeAlarm.IsResolved = true;
                    activeAlarm.ResolvedAt = DateTime.UtcNow;
                }
            }
            tracker.LowStartTime = null;
            tracker.IsAlarmActive = false;
        }

        await _db.SaveChangesAsync();
    }

    public async Task TriggerAnodeEffectAlarmAsync(int cellId)
    {
        var existing = await _db.Alarms
            .Where(a => a.CellId == cellId && a.AlarmType == "效应告警" && !a.IsResolved)
            .FirstOrDefaultAsync();

        if (existing != null) return;

        await CreateAlarmAsync(cellId, "效应告警", 2,
            $"电解槽{cellId}号发生阳极效应，触发二级效应告警，自动执行效应熄灭程序");

        await _mqttService.PublishAlarmAsync(cellId, "二级效应告警",
            $"电解槽{cellId}号阳极效应已触发，正在自动执行效应熄灭程序");
    }

    private async Task CreateAlarmAsync(int cellId, string alarmType, int alarmLevel, string message)
    {
        var alarm = new Alarm
        {
            CellId = cellId,
            Timestamp = DateTime.UtcNow,
            AlarmType = alarmType,
            AlarmLevel = alarmLevel,
            Message = message,
            IsResolved = false
        };
        _db.Alarms.Add(alarm);
        await _db.SaveChangesAsync();
        _logger.LogError("ALARM Level {Level}: {Message}", alarmLevel, message);
    }
}
