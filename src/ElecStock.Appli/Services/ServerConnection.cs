using ElecStock.Contracts;
using ElecStock.UI.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace ElecStock.Appli.Services;

/// <summary>
/// The server the app talks to: its address (a preference), the HTTP client of the API and the
/// SignalR change feed, reconnected forever so the app catches up when the VPN comes back.
/// </summary>
public sealed class ServerConnection : IServerSettings, IStockEvents, IAsyncDisposable
{
    const string UrlKey = "server_url";
#if ANDROID
    // From the Android emulator the dev PC is 10.0.2.2 (localhost is the emulator itself); a phone uses Settings.
    public const string DefaultUrl = "http://10.0.2.2:5190/";
#else
    public const string DefaultUrl = "http://localhost:5190/";
#endif

    HubConnection? _hub;
    CancellationTokenSource _hubCts = new();

    public ServerConnection()
    {
        ServerUrl = Preferences.Default.Get(UrlKey, DefaultUrl);
        Http = CreateClient(ServerUrl);
        _ = ConnectHubAsync();
    }

    public event Action<ChangeEvent>? Changed;

    public string ServerUrl { get; private set; }
    public HttpClient Http { get; private set; }

    public Task SetServerUrlAsync(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        Preferences.Default.Set(UrlKey, url);
        ServerUrl = url;
        Http = CreateClient(url);
        // Not awaited: the change feed retries until this server answers, which may be never (wrong address).
        _ = ConnectHubAsync();
        Changed?.Invoke(new ChangeEvent(ChangeScope.All));
        return Task.CompletedTask;
    }

    static HttpClient CreateClient(string url) => new()
    {
        BaseAddress = new Uri(url.TrimEnd('/') + "/"),
        // imports of the whole stock folder take a while
        Timeout = TimeSpan.FromMinutes(10),
    };

    async Task ConnectHubAsync()
    {
        await _hubCts.CancelAsync();
        _hubCts = new CancellationTokenSource();
        if (_hub != null) await _hub.DisposeAsync();

        var hub = _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Http.BaseAddress!, "hubs/stock"))
            .WithAutomaticReconnect(new RetryForever())
            .Build();
        hub.On<ChangeEvent>("Changed", change => Changed?.Invoke(change));
        hub.Reconnected += _ =>
        {
            Changed?.Invoke(new ChangeEvent(ChangeScope.All));
            return Task.CompletedTask;
        };

        // Automatic reconnect only covers a connection that once succeeded: retry the first one by hand.
        var ct = _hubCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await hub.StartAsync(ct);
                Changed?.Invoke(new ChangeEvent(ChangeScope.All));
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (TaskCanceledException) { return; }
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(30, 2 + context.PreviousRetryCount * 3));
    }

    public async ValueTask DisposeAsync()
    {
        await _hubCts.CancelAsync();
        if (_hub != null) await _hub.DisposeAsync();
        Http.Dispose();
    }
}
