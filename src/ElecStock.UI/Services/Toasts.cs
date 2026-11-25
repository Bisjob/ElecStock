namespace ElecStock.UI.Services;

public enum ToastKind { Info, Success, Error }

public sealed record Toast(Guid Id, string Message, ToastKind Kind);

/// <summary>Short messages shown at the bottom of the screen (saved, refused by a stock rule…).</summary>
public sealed class Toasts
{
    readonly List<Toast> _items = [];

    public IReadOnlyList<Toast> Items => _items;
    public event Action? Changed;

    public void Info(string message) => Show(message, ToastKind.Info);
    public void Success(string message) => Show(message, ToastKind.Success);
    public void Error(string message) => Show(message, ToastKind.Error);

    void Show(string message, ToastKind kind)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        _items.Add(toast);
        Changed?.Invoke();
        _ = DismissLater(toast, kind == ToastKind.Error ? 8000 : 3500);
    }

    async Task DismissLater(Toast toast, int ms)
    {
        await Task.Delay(ms);
        Dismiss(toast.Id);
    }

    public void Dismiss(Guid id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }
}
