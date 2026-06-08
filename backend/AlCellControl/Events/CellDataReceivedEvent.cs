using MediatR;

namespace AlCellControl.Events;

public class CellDataReceivedEvent : INotification
{
    public int CellId { get; set; }
    public double Voltage { get; set; }
    public string AnodeCurrentDistribution { get; set; } = string.Empty;
    public double CellTemperature { get; set; }
    public double BathTemperature { get; set; }
    public double AluminumLevel { get; set; }
    public double BathLevel { get; set; }
    public DateTime ReceivedAt { get; set; }
}
