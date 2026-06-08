using MediatR;

namespace AlCellControl.Events;

public class AlarmTriggeredEvent : INotification
{
    public int CellId { get; set; }
    public int AlarmLevel { get; set; }
    public string AlarmType { get; set; } = string.Empty;
    public string AlarmMessage { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
}
