using System.Windows;
using DataJackUIGui.Views;

namespace DataJackUIGui.Services;

/// <summary>
/// App-wide in-window toast feedback. Manages native WPF acrylic toast notifications
/// hosted inside ToastPresenter on MainWindow. Safe to call from any thread.
/// </summary>
public class ToastService
{
    private ToastPresenter? _presenter;

    /// <summary>Wire the presenter that hosts the toasts (called once after the window is built).</summary>
    public void Attach(ToastPresenter presenter)
    {
        _presenter = presenter;
    }

    /// <summary>Show a transient toast (auto-dismiss after ~3.5s). Marshals to the UI thread; no-ops if unattached.</summary>
    public void Show(string title, string message, bool error = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Post() => _presenter?.AddToast(title, message, error);

        if (dispatcher.CheckAccess()) Post();
        else dispatcher.Invoke(Post);
    }

    /// <summary>
    /// Show a persistent toast with an action button (user-closable or dismissed on action).
    /// Used for update ready prompts or drifted launch options notices.
    /// </summary>
    public void ShowAction(string title, string message, string actionLabel, Action onAction, bool error = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Post() => _presenter?.AddToast(title, message, error, actionLabel, onAction);

        if (dispatcher.CheckAccess()) Post();
        else dispatcher.Invoke(Post);
    }
}
