using ElecStock.Contracts;
using ElecStock.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ElecStock.UI.Components;

/// <summary>
/// Base of the pages: the stock API, toasts, and a reload when the server says something changed
/// (another client, Claude through MCP…), debounced so a burst of changes reloads once.
/// </summary>
public abstract class StockComponentBase : ComponentBase, IDisposable
{
    [Inject] protected IStockApi Api { get; set; } = null!;
    [Inject] protected IStockEvents Events { get; set; } = null!;
    [Inject] protected Toasts Toasts { get; set; } = null!;
    [Inject] protected UiJs Js { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;

    CancellationTokenSource? _reload;
    protected bool Busy { get; private set; }

    protected override void OnInitialized() => Events.Changed += OnChanged;

    void OnChanged(ChangeEvent change)
    {
        if (!IsRelevant(change)) return;
        _reload?.Cancel();
        var cts = _reload = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await ReloadAsync();
                StateHasChanged();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Toasts.Error(ex.Message);
            }
        });
    }

    /// <summary>Whether a change concerns this page (default: every change).</summary>
    protected virtual bool IsRelevant(ChangeEvent change) => true;

    /// <summary>Reloads the data of the page after a change elsewhere.</summary>
    protected virtual Task ReloadAsync() => Task.CompletedTask;

    /// <summary>Runs an action, showing a broken stock rule (or a lost connection) as a toast. Returns false on failure.</summary>
    protected async Task<bool> Try(Func<Task> action, string? success = null)
    {
        Busy = true;
        try
        {
            await action();
            if (success != null) Toasts.Success(success);
            return true;
        }
        catch (StockException ex)
        {
            Toasts.Error(ex.Message);
            return false;
        }
        catch (HttpRequestException ex)
        {
            Toasts.Error($"Server unreachable: {ex.Message}");
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Server file embedded in the page (img, iframe).</summary>
    protected string Url(string? relative) => relative == null ? "" : Api.ResolveUrl(relative);

    /// <summary>Server file opened outside the app (new tab, browser, download).</summary>
    protected string ExternalUrl(string? relative) => relative == null ? "" : Api.ExternalUrl(relative);

    public virtual void Dispose()
    {
        Events.Changed -= OnChanged;
        _reload?.Cancel();
        GC.SuppressFinalize(this);
    }
}
