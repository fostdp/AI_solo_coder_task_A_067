using MediatR;

namespace AlCellControl.Events;

public class ConcentrationEstimatedEvent : INotification
{
    public int CellId { get; set; }
    public double EstimatedConcentration { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public DateTime EstimatedAt { get; set; }
    public double Voltage { get; set; }
    public double CellTemperature { get; set; }
}
