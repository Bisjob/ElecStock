using ElecStock.Contracts;
using ElecStock.Data.Files;
using ElecStock.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElecStock.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Database, file storage and <see cref="StockService"/> (also registered as <see cref="IStockApi"/>).</summary>
    public static IServiceCollection AddStockData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Stock")
            ?? throw new InvalidOperationException("ConnectionStrings:Stock is not configured.");
        services.AddDbContextFactory<StockDb>(options => options.UseNpgsql(connectionString));
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.Configure<ServerOptions>(configuration.GetSection("Server"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FileStore>();
        services.AddSingleton<ImageProcessor>();
        services.AddSingleton<StockImporter>();
        services.AddScoped<CallerContext>();
        services.AddScoped<StockService>();
        services.AddScoped<IStockApi>(sp => sp.GetRequiredService<StockService>());
        services.AddHttpClient(StockService.DatasheetClient, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            // Some manufacturer sites refuse requests without a browser user agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ElecStock/2.0");
        });
        return services;
    }
}
