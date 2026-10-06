using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Models;

namespace DataJackUIGui.Views;

public partial class ToastPresenter : UserControl
{
    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public ToastPresenter()
    {
        InitializeComponent();
    }

    public void AddToast(string title, string message, bool error, string? actionLabel = null, Action? onAction = null)
    {
        var item = new ToastItem
        {
            Title = title,
            Message = message,
            IsError = error,
            ActionLabel = actionLabel,
            OnAction = onAction,
        };

        item.DismissCommand = new RelayCommand(() => Dismiss(item));
        item.ActionCommand = new RelayCommand(() =>
        {
            Dismiss(item);
            onAction?.Invoke();
        });

        // Keep maximum 4 concurrent toasts
        while (Toasts.Count >= 4)
        {
            var oldest = Toasts[0];
            oldest.Timer?.Stop();
            Toasts.RemoveAt(0);
        }

        Toasts.Add(item);

        // Auto-dismiss transient toasts after 3.5s
        if (!item.HasAction)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(item);
            };
            item.Timer = timer;
            timer.Start();
        }
    }

    public void Dismiss(ToastItem item)
    {
        item.Timer?.Stop();
        Toasts.Remove(item);
    }

    private void OnToastMouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastItem item && item.Timer is not null)
        {
            item.Timer.Stop();
        }
    }

    private void OnToastMouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastItem item && item.Timer is not null && !item.HasAction)
        {
            item.Timer.Interval = TimeSpan.FromSeconds(2.0);
            item.Timer.Start();
        }
    }
}
