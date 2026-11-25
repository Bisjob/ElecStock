using ElecStock.Contracts;
using ElecStock.Data;
using ElecStock.Data.Files;
using ElecStock.Data.Services;
using ElecStock.Server;
using ElecStock.Server.Api;
using ElecStock.Server.Components;
using ElecStock.Server.Mcp;
using ElecStock.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStockData(builder.Configuration);
builder.Services.AddSingleton<StockEvents>();
builder.Services.AddSingleton<IStockEvents>(sp => sp.GetRequiredService<StockEvents>());
builder.Services.AddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<StockEvents>());
builder.Services.AddScoped<StockExporter>();
builder.Services.AddSignalR();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddStockUI();
builder.Services.AddProblemDetails();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "elecstock", Title = "ElecStock", Version = "2.0" };
        options.ServerInstructions = StockTools.Instructions;
    })
    .WithHttpTransport()
    .WithTools<StockTools>();

// The stock import is a zip of the whole folder (photos and datasheets included).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StockDb>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.MapStaticAssets();

// Stored files have GUID names: they never change, so they can be cached for good.
var store = app.Services.GetRequiredService<FileStore>();
foreach (var dir in FileStore.ServedDirs)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.Combine(store.Root, dir)),
        RequestPath = $"/files/{dir}",
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            // An uploaded SVG opened on its own must not run scripts on the server's origin.
            if (ctx.File.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        },
    });
}

app.UseAntiforgery();

app.MapStockApi();
app.MapHub<StockHub>(StockHub.Path);

// Changes made by Claude are written to the history as "Mcp".
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
        context.RequestServices.GetRequiredService<CallerContext>().Source = ActivitySource.Mcp;
    await next();
});
app.MapMcp("/mcp");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();
