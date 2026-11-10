using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig.Wpf;
using Wpf.Ui.Controls;

namespace DataJackUIGui.Services;

public static class UiMessageBox
{
    private static MarkdownViewer CreateMarkdownControl(string md)
    {
        var viewer = new MarkdownViewer
        {
            Markdown = md,
            MaxWidth = 480,
            Margin = new Thickness(0, 2, 0, 4)
        };

        viewer.CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, (_, e) =>
        {
            if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { SteamService.OpenUrl(url); } catch { }
            }
            e.Handled = true;
        }));

        return viewer;
    }

    public static async Task<bool> ShowConfirmAsync(string title, string body, string confirmText = "OK", string cancelText = "Cancel")
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(() => ShowConfirmAsync(title, body, confirmText, cancelText)).Task.Unwrap();
        }

        bool hasCancel = !string.IsNullOrWhiteSpace(cancelText);

        object content;
        if (!string.IsNullOrWhiteSpace(body) && (body.Contains(":::spoiler") || body.Contains('[') || body.Contains("http") || body.Contains('\n') || body.Contains('#') || body.Contains('*') || body.Contains('•')))
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                MaxWidth = 480
            };

            // Parse out :::spoiler Title \n Content ::: blocks
            var parts = Regex.Split(body, @"(:::spoiler[\s\S]*?:::)", RegexOptions.IgnoreCase);
            foreach (var part in parts)
            {
                if (part.StartsWith(":::spoiler", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(part, @":::spoiler\s*([^\r\n]*)\r?\n([\s\S]*?):::", RegexOptions.IgnoreCase);
                    string spoilerTitle = m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)
                        ? m.Groups[1].Value.Trim()
                        : "Spoiler";
                    string spoilerContent = m.Success ? m.Groups[2].Value.Trim() : part;

                    var expander = new Expander
                    {
                        Header = spoilerTitle,
                        IsExpanded = false,
                        Margin = new Thickness(0, 6, 0, 6),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6),
                        Content = CreateMarkdownControl(spoilerContent)
                    };
                    stack.Children.Add(expander);
                }
                else if (!string.IsNullOrWhiteSpace(part))
                {
                    stack.Children.Add(CreateMarkdownControl(part.Trim()));
                }
            }

            var scroll = new ScrollViewer
            {
                Content = stack,
                MaxHeight = 420,
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
