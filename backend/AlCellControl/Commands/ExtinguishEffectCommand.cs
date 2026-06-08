using MediatR;

namespace AlCellControl.Commands;

public class ExtinguishEffectCommand : IRequest<ExtinguishEffectResult>
{
    public int CellId { get; set; }
    public double AluminaAmount { get; set; }
}
