namespace SmartX.Client.Services;

/// <summary>One transient notification.</summary>
/// <param name="Id">Key used to remove it once it expires.</param>
/// <param name="Message">Text shown to the operator.</param>
/// <param name="IsError">Whether it reports a failure.</param>
public sealed record Toast(Guid Id, string Message, bool IsError);

/// <summary>
/// Transient notifications shown in the corner of every page.
/// <para>
/// Held in a service rather than in a page, so a toast raised just before
/// navigating - "Registered Hydro Bay 3" followed by a jump to that node's profile -
/// survives the route change instead of vanishing with the page that raised it.
/// </para>
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly List<Toast> _toasts = [];

    /// <summary>The toasts currently showing, oldest first.</summary>
    public IReadOnlyList<Toast> Current => _toasts;

    /// <summary>Raised whenever a toast is added or expires.</summary>
    public event Action? Changed;

    /// <summary>Shows a success message.</summary>
    public void Success(string message) => Show(message, isError: false);

    /// <summary>Shows a failure message.</summary>
    public void Error(string message) => Show(message, isError: true);

    private void Show(string message, bool isError)
    {
        var toast = new Toast(Guid.NewGuid(), message, isError);
        _toasts.Add(toast);
        Changed?.Invoke();

        _ = ExpireAsync(toast);
    }

    private async Task ExpireAsync(Toast toast)
    {
        await Task.Delay(Lifetime);
        _toasts.Remove(toast);
        Changed?.Invoke();
    }
}
