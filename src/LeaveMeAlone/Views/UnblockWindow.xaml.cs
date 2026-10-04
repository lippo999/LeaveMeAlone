using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

public partial class UnblockWindow : Window
{
    private static readonly TimeSpan HealthRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly UnblockViewModel _viewModel;
    private readonly DispatcherTimer _healthTimer;

    public UnblockWindow(UnblockViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _healthTimer = new DispatcherTimer { Interval = HealthRefreshInterval };
        _healthTimer.Tick += (_, _) => _viewModel.RefreshHealth();
        Loaded += (_, _) =>
        {
            _viewModel.RefreshHealth();
            _healthTimer.Start();
            DomainBox.Focus();
        };
        Closed += (_, _) => _healthTimer.Stop();
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e) => await _viewModel.DiagnoseAsync();

    private async void Unblock_Click(object sender, RoutedEventArgs e) => await _viewModel.UnblockAsync();

    private async void Steam_Click(object sender, RoutedEventArgs e) => await _viewModel.UnblockPresetAsync(UnblockPresets.Steam);

    private async void SteamDownloads_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.UnblockPresetAsync(UnblockPresets.SteamDownloads);

    private async void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await _viewModel.DiagnoseAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UnblockedDomainViewModel vm })
            await _viewModel.RemoveAsync(vm);
    }

    private async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Remove every unblock? The app's lines are deleted from the hosts file and all relays stop.",
            "Remove all unblocks", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) await _viewModel.RemoveAllAsync();
    }
}
