using MediatR;

namespace AlCellControl.Commands;

public class AutoFeedCommand : IRequest<AutoFeedResult>
{
    public int CellId { get; set; }
    public double FeedAmountKg { get; set; }
}
