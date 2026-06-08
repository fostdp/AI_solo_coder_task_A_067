using AlCellControl.Data;
using AlCellControl.Events;
using AlCellControl.Models;
using MediatR;

namespace AlCellControl.Services;

public record CellDataDto(
    int CellId,
    double Voltage,
    string AnodeCurrentDistribution,
    double CellTemperature,
    double BathTemperature,
    double AluminumLevel,
    double BathLevel
);

public class ZigBeeReceiver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CellBufferService _cellBufferService;
    private readonly IMediator _mediator;
    private readonly ILogger<ZigBeeReceiver> _logger;

    public ZigBeeReceiver(
        IServiceProvider serviceProvider,
        CellBufferService cellBufferService,
        IMediator mediator,
        ILogger<ZigBeeReceiver> logger)
    {
        _serviceProvider = serviceProvider;
        _cellBufferService = cellBufferService;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ReceiveBatchAsync(List<CellDataDto> data)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var item in data)
        {
            var currentAvg = ParseCurrentAvg(item.AnodeCurrentDistribution);

            _cellBufferService.Write(item.CellId, item.Voltage, currentAvg);

            var realtimeData = new CellRealtimeData
            {
                CellId = item.CellId,
                Voltage = item.Voltage,
                AnodeCurrentDistribution = item.AnodeCurrentDistribution,
                CellTemperature = item.CellTemperature,
                BathTemperature = item.BathTemperature,
                AluminumLevel = item.AluminumLevel,
                BathLevel = item.BathLevel,
                ReceivedAt = DateTime.UtcNow
            };

            db.CellRealtimeData.Add(realtimeData);
            await db.SaveChangesAsync();

            var evt = new CellDataReceivedEvent
            {
                CellId = item.CellId,
                Voltage = item.Voltage,
                AnodeCurrentDistribution = item.AnodeCurrentDistribution,
                CellTemperature = item.CellTemperature,
                BathTemperature = item.BathTemperature,
                AluminumLevel = item.AluminumLevel,
                BathLevel = item.BathLevel,
                ReceivedAt = realtimeData.ReceivedAt
            };

            await _mediator.Publish(evt);
        }
    }

    private static double ParseCurrentAvg(string distribution)
    {
        if (string.IsNullOrWhiteSpace(distribution))
            return 0;

        var cleaned = distribution.Trim().TrimStart('[').TrimEnd(']');
        var parts = cleaned.Split(',');
        if (parts.Length == 0)
            return 0;

        double sum = 0;
        int count = 0;
        foreach (var part in parts)
        {
            if (double.TryParse(part.Trim(), out var val))
            {
                sum += val;
                count++;
            }
        }

        return count > 0 ? sum / count : 0;
    }
}
