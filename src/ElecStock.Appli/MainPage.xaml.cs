using ElecStock.Appli.Services;

namespace ElecStock.Appli;

public partial class MainPage : ContentPage
{
    readonly ServerConnection _server;

    public MainPage(ServerConnection server)
    {
        _server = server;
        InitializeComponent();
        blazorWebView.WebResourceRequested += OnWebResourceRequested;
    }

    /// <summary>
    /// The UI runs on https://0.0.0.1 while the server is plain http: the WebView blocks its photos and PDFs as
    /// mixed content. They are requested as <c>_server/files/…</c> on the app origin instead, fetched here.
    /// </summary>
    void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
    {
        var prefix = "/" + HttpStockApi.ProxyPrefix;
        if (e.Method != "GET" || !e.Uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        var target = e.Uri.AbsolutePath[prefix.Length..] + e.Uri.Query;
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = ContentType(target),
            // stored files have GUID names and never change
            ["Cache-Control"] = "public, max-age=31536000, immutable",
        };
        e.Handled = true;
        e.SetResponse(200, "OK", headers, FetchAsync(target));
    }

    async Task<Stream?> FetchAsync(string target)
    {
        try
        {
            var bytes = await _server.Http.GetByteArrayAsync(target);
            return new MemoryStream(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new MemoryStream();
        }
    }

    static string ContentType(string path) => Path.GetExtension(path.Split('?')[0]).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        _ => "image/jpeg",
    };
}
