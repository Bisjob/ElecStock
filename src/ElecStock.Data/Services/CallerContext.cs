using ElecStock.Contracts;

namespace ElecStock.Data.Services;

/// <summary>Who is calling the stock service in this scope; written to the history of every change.</summary>
public sealed class CallerContext
{
    public ActivitySource Source { get; set; } = ActivitySource.Web;
}

/// <summary>Pushes a change to every open client (Blazor circuits, MAUI apps over SignalR).</summary>
public interface IChangeNotifier
{
    void Publish(ChangeEvent change);
}

public sealed class ServerOptions
{
    /// <summary>Address of the web UI from the phone, e.g. <c>http://my-server:4002</c>: QR labels point there.</summary>
    public string PublicBaseUrl { get; set; } = "";
}
