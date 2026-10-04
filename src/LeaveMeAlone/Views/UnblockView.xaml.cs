using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

/// <summary>Unblock page: Quick (one switch per preset) and Custom (check / unblock any domain).</summary>
public partial class UnblockView : UserControl
{
    private static readonly TimeSpan HealthRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _healthTimer;

    public UnblockView()
    {
        InitializeComponent();
        _healthTimer = new DispatcherTimer { Interval = HealthRefreshInterval };
        _healthTimer.Tick += (_, _) => ViewModel?.RefreshHealth();
        // Relay health only matters while the page is on screen.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { ViewModel?.RefreshHealth(); _healthTimer.Start(); }
            else _healthTimer.Stop();
        };
    }

    private UnblockViewModel? ViewModel => DataContext as UnblockViewModel;
    private Window Owner => Window.GetWindow(this)!;

    // ----- Quick -----

    private async void PresetSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not ToggleButton { DataContext: UnblockPresetViewModel row } toggle) return;
        await ViewModel.TogglePresetAsync(row, toggle.IsChecked == true);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, row.IsOn); // snap back if it failed or nothing was blocked
    }

    private void Details_Click(object sender, RoutedEventArgs e) => CustomTab.IsChecked = true;

    // ----- Custom -----

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.DiagnoseAsync();
    }

    private async void Unblock_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.UnblockAsync();
    }

    private async void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel != null) await ViewModel.DiagnoseAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null && sender is FrameworkElement { DataContext: UnblockedDomainViewModel vm })
            await ViewModel.RemoveAsync(vm);
    }

    private async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var confirm = MessageBox.Show(Owner,
            "Remove every unblock? The app's lines are deleted from the hosts file and all relays stop.",
            "Remove all unblocks", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) await ViewModel.RemoveAllAsync();
    }
}
