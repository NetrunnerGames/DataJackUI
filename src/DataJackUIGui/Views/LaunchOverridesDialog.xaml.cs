using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DataJackUIGui.Resources;
using DataJackUIGui.Services;
using DataJackUIGui.Services.AppInfo;
using DataJackUIGui.ViewModels;
using Wpf.Ui.Controls;

namespace DataJackUIGui.Views;

public partial class LaunchOverridesDialog : FluentWindow
{
    public sealed record Row(long AppId, string Name, IReadOnlyList<LaunchEntryViewModel> Entries);

    private readonly LaunchOptionsService _launch;
    private readonly SteamAppListCache _appList;
    private readonly ToastService _toast;
    private readonly Action<long, string> _openEditor;

    public LaunchOverridesDialog(LaunchOptionsService launch, SteamAppListCache appList, ToastService toast, Action<long, string> openEditor)
    {
        InitializeComponent();
        _launch = launch;
        _appList = appList;
        _toast = toast;
        _openEditor = openEditor;
        Reload();
    }

    private void Reload()
    {
        List<Row> list = _launch.Store.All.Select(m => new Row(m.AppId, _appList.GetName(m.AppId) ?? string.Format(Strings.Common_AppFallback, m.AppId), m.Desired.Select(o => new LaunchEntryViewModel(o)).ToList())).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Rows.ItemsSource = list;
        Empty.Visibility = list.Count != 0 ? Visibility.Collapsed : Visibility.Visible;
        RestoreAll.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row row)
        {
            _openEditor(row.AppId, row.Name);
            Reload();
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.MessageBox.Show(this, string.Format(Strings.Launch_RestoreAll_Body, _launch.Store.All.Count), Strings.Launch_RestoreAll_Title, System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) == System.Windows.MessageBoxResult.OK)
            {
                RestoreAll.IsEnabled = false;
                ApplyResult applyResult = await Task.Run(() => _launch.RestoreAll());
                RestoreAll.IsEnabled = true;
                if (applyResult.Ok)
                {
                    _toast.Show(Strings.Launch_Title, applyResult.SteamWasRunning ? Strings.Launch_Applied_Restarted : Strings.Launch_Applied);
                }
                else
                {
                    _toast.Show(Strings.Launch_Title, string.Format(Strings.Launch_ApplyFailed, applyResult.Error), error: true);
                }
                Reload();
            }
        }
        catch (Exception ex)
        {
            RestoreAll.IsEnabled = true;
            _toast.Show(Strings.Launch_Title, ex.Message, error: true);
        }
    }
}

