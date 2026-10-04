using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using FirewallBundleBlocker.Models;
using FirewallBundleBlocker.Services;
using FirewallBundleBlocker.ViewModels;

namespace FirewallBundleBlocker.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UnblockViewModel _unblockViewModel;
    private UnblockWindow? _unblockWindow;
    private bool _suppressStartupEvent;

    public MainWindow(MainViewModel viewModel, UnblockViewModel unblockViewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _unblockViewModel = unblockViewModel;
        DataContext = viewModel;
        BannerPanel.Visibility = viewModel.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        Loaded += OnLoaded;
        StateChanged += MainWindow_StateChanged;

        // Reflect the current startup registration without re-triggering the change handler.
        _suppressStartupEvent = true;
        StartupCheck.IsChecked = StartupRegistration.IsEnabled();
        _suppressStartupEvent = false;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Minimize hides to the tray; the tray menu (or its "Show window" item) restores it.
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupEvent) return;
        try
        {
            StartupRegistration.SetEnabled(StartupCheck.IsChecked == true);
            _viewModel.StatusMessage = StartupCheck.IsChecked == true
                ? "Will start with Windows."
                : "Will no longer start with Windows.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Failed to update startup setting: {ex.Message}";
        }
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        if (_unblockWindow is { IsLoaded: true })
        {
            _unblockWindow.Activate();
            return;
        }
        _unblockWindow = new UnblockWindow(_unblockViewModel) { Owner = this };
        _unblockWindow.Show();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import bundles",
            Filter = "Bundle files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        await _viewModel.ImportAsync(dlg.FileName);
        BundlesList.SelectedItem = _viewModel.SelectedBundle;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export bundles",
            Filter = "Bundle files (*.json)|*.json",
            FileName = "bundles.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        await _viewModel.ExportAsync(dlg.FileName);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        if (BundlesList.Items.Count > 0 && BundlesList.SelectedItem == null)
            BundlesList.SelectedIndex = 0;
    }

    private void BundlesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = BundlesList.SelectedItem as BundleViewModel;
        _viewModel.SelectedBundle = vm;
        DetailPanel.DataContext = vm;
        DetailPanel.Visibility = vm != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = vm != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.DataContext is not BundleViewModel vm) return;
        await _viewModel.ToggleAsync(vm, toggle.IsChecked == true);
        toggle.IsChecked = vm.Enabled; // re-sync in case the firewall call failed
    }

    private async void NewBundle_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.PromptText(this, "New bundle", "Bundle name");
        if (name == null) return;
        var created = await _viewModel.CreateBundleAsync(name);
        if (created != null) BundlesList.SelectedItem = created;
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var vm = _viewModel.SelectedBundle;
        if (vm == null) return;
        var name = Dialogs.PromptText(this, "Rename bundle", "Bundle name", vm.Name);
        if (name == null) return;
        await _viewModel.RenameSelectedAsync(name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var vm = _viewModel.SelectedBundle;
        if (vm == null) return;
        var confirm = MessageBox.Show(
            $"Delete bundle '{vm.Name}' and remove its firewall rules?",
            "Delete bundle", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        await _viewModel.DeleteSelectedAsync();
        BundlesList.SelectedItem = _viewModel.SelectedBundle;
    }

    private async void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBundle == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose a program to block",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = new FirewallTarget
        {
            Type = TargetType.Program,
            Value = dlg.FileName,
            DisplayName = Path.GetFileName(dlg.FileName),
        };
        await _viewModel.AddTargetToSelectedAsync(target);
    }

    private async void AddIp_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBundle == null) return;
        var target = Dialogs.PromptIpTarget(this);
        if (target == null) return;
        await _viewModel.AddTargetToSelectedAsync(target);
    }

    private async void AddCommon_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBundle == null) return;
        var detected = CommonLaunchers.Detect();
        if (detected.Count == 0)
        {
            MessageBox.Show(
                "No known launchers were found in their default install locations.\n" +
                "Use \"Add program\" to browse for an executable instead.",
                "Common launchers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = Dialogs.PromptCommonLaunchers(this, detected);
        if (targets == null || targets.Count == 0) return;
        await _viewModel.AddTargetsToSelectedAsync(targets);
    }

    private async void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TargetViewModel target) return;
        await _viewModel.RemoveTargetFromSelectedAsync(target);
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
        => await _viewModel.VerifySelectedAsync();
}
