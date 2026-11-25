using ElecStock.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ElecStock.UI;

public static class UiServiceCollectionExtensions
{
    /// <summary>Services of the shared UI. The host registers <c>IStockApi</c> and <c>IStockEvents</c>.</summary>
    public static IServiceCollection AddStockUI(this IServiceCollection services)
    {
        services.AddScoped<Toasts>();
        services.AddScoped<UiJs>();
        return services;
    }
}
