using ElecStock.Appli.Services;
using ElecStock.Contracts;
using ElecStock.UI;
using ElecStock.UI.Services;
using Microsoft.Extensions.Logging;

namespace ElecStock.Appli;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Unpackaged, WebView2 keeps its data next to the exe (ElecStock.Appli.exe.WebView2): a blank window in a
        // read-only folder such as Program Files, and data shipped with a copied folder. Keep it in the user's profile.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElecStock", "WebView2"));
#endif
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Server photos and PDFs reach the WebView through the app origin (MainPage.OnWebResourceRequested).
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ServerConnection>();
        builder.Services.AddSingleton<IServerSettings>(sp => sp.GetRequiredService<ServerConnection>());
        builder.Services.AddSingleton<IStockEvents>(sp => sp.GetRequiredService<ServerConnection>());
        builder.Services.AddSingleton<IStockApi, HttpStockApi>();
        builder.Services.AddStockUI();

        return builder.Build();
    }
}
