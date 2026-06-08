using Microsoft.AspNetCore.SignalR;

namespace AluminumCellControl.Hubs;

public class CellHub : Hub
{
    public async Task SubscribeCell(int cellId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"cell-{cellId}");
    }

    public async Task UnsubscribeCell(int cellId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"cell-{cellId}");
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "all-cells");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "all-cells");
        await base.OnDisconnectedAsync(exception);
    }
}
