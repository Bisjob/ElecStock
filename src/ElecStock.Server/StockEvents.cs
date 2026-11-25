using ElecStock.Contracts;
using ElecStock.Data.Services;
using Microsoft.AspNetCore.SignalR;

namespace ElecStock.Server;

/// <summary>Server → client change feed for the MAUI apps (the web circuits use <see cref="StockEvents"/> directly).</summary>
public sealed class StockHub : Hub
{
    public const string Path = "/hubs/stock";
    public const string ChangedMethod = "Changed";
}

/// <summary>Fans every change out to the Blazor circuits of this server and to the SignalR clients.</summary>
public sealed class StockEvents(IHubContext<StockHub> hub, ILogger<StockEvents> logger) : IStockEvents, IChangeNotifier
{
    public event Action<ChangeEvent>? Changed;

    public void Publish(ChangeEvent change)
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<ChangeEvent>>() ?? [])
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A change listener failed");
            }
        }
        _ = hub.Clients.All.SendAsync(StockHub.ChangedMethod, change);
    }
}
