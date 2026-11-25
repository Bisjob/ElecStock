using Microsoft.JSInterop;

namespace ElecStock.UI.Services;

public sealed record ElementBox(double Left, double Top, double Width, double Height);

/// <summary>The few things Blazor cannot do alone: paste images, measure an element, theme, confirm.</summary>
public sealed class UiJs(IJSRuntime js) : IAsyncDisposable
{
    IJSObjectReference? _module;

    async ValueTask<IJSObjectReference> Module() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/ElecStock.UI/elecstock.js");

    public ValueTask<bool> ConfirmAsync(string message) => js.InvokeAsync<bool>("confirm", message);

    public async ValueTask<ElementBox> BoxAsync(Microsoft.AspNetCore.Components.ElementReference element) =>
        await (await Module()).InvokeAsync<ElementBox>("box", element);

    /// <summary>Calls <c>OnPasteImage(IJSStreamReference, string)</c> on the target when an image is pasted while it is shown.</summary>
    public async ValueTask<IJSObjectReference> ListenPasteAsync<T>(DotNetObjectReference<T> target) where T : class =>
        await (await Module()).InvokeAsync<IJSObjectReference>("listenPaste", target);

    public async ValueTask<string> GetThemeAsync() => await (await Module()).InvokeAsync<string>("getTheme");

    public async ValueTask SetThemeAsync(string theme) => await (await Module()).InvokeVoidAsync("setTheme", theme);

    public async ValueTask<string?> GetSettingAsync(string key) => await (await Module()).InvokeAsync<string?>("getSetting", key);

    public async ValueTask SetSettingAsync(string key, string value) => await (await Module()).InvokeVoidAsync("setSetting", key, value);

    public async ValueTask CopyAsync(string text) => await (await Module()).InvokeVoidAsync("copy", text);

    public async ValueTask PrintAsync() => await js.InvokeVoidAsync("print");

    public async ValueTask DisposeAsync()
    {
        if (_module == null) return;
        try
        {
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
