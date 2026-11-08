using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace DataJackUIGui.Services;

public static class UiMessageBox
{
    public static async Task<bool> ShowConfirmAsync(string title, string body, string confirmText = "OK", string cancelText = "Cancel")
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(() => ShowConfirmAsync(title, body, confirmText, cancelText)).Task.Unwrap();
        }

        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = body,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
        };

        if (Application.Current?.MainWindow is Window mainWindow && mainWindow.IsVisible)
        {
            msgBox.Owner = mainWindow;
        }

        var result = await msgBox.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
