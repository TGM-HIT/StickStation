using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StickStation.Core.ViewModels;

namespace StickStation;

public sealed partial class MainPage : Page
{
    private CancellationTokenSource? monitor;

    public MainViewModel ViewModel { get; } = App.ViewModel;

    public MainPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        monitor?.Cancel();
        monitor = new CancellationTokenSource();
        _ = ViewModel.RunMonitorAsync(monitor.Token);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => monitor?.Cancel();

    private void ShowBackup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is StickRowViewModel row) ViewModel.OpenBackupCommand.Execute(row);
    }
}
