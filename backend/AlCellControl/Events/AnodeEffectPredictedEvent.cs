using MediatR;

namespace AlCellControl.Events;

public class AnodeEffectPredictedEvent : INotification
{
    public int CellId { get; set; }
    public double Probability { get; set; }
    public DateTime PredictedAt { get; set; }
}
