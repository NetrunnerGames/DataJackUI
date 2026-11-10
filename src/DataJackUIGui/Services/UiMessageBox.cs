using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Markdig.Wpf;
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

        bool hasCancel = !string.IsNullOrWhiteSpace(cancelText);

        object content;
        if (!string.IsNullOrWhiteSpace(body) && (body.Contains('[') || body.Contains("http") || body.Contains('\n') || body.Contains('#') || body.Contains('*') || body.Contains('•')))
        {
            var markdownViewer = new MarkdownViewer
            {
                Markdown = body,
                MaxWidth = 480,
                Margin = new Thickness(0, 4, 0, 8)
            };

            markdownViewer.CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, (_, e) =>
            {
                if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
                {
                    try { SteamService.OpenUrl(url); } catch { }
                }
                e.Handled = true;
            }));

            var scroll = new ScrollViewer
            {
                Content = markdownViewer,
                MaxHeight = 350,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            content = scroll;
        }
        else
        {
            content = body;
        }

        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = hasCancel ? cancelText : "",
            IsCloseButtonEnabled = hasCancel,
        };

        if (Application.Current?.MainWindow is Window mainWindow && mainWindow.IsVisible)
        {
            msgBox.Owner = mainWindow;
        }

        var result = await msgBox.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
