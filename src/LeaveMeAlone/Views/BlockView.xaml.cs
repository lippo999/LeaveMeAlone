using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using LeaveMeAlone.Models;
using LeaveMeAlone.Services;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

/// <summary>Block page: Quick (one switch per launcher/bundle) and Custom (full bundle editor).</summary>
public partial class BlockView : UserControl
{
    public BlockView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private Window Owner => Window.GetWindow(this)!;

    public async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        if (BundlesList.Items.Count > 0 && BundlesList.SelectedItem == null)
            BundlesList.SelectedIndex = 0;
    }

    // ----- Quick -----

    private async void QuickSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: QuickSwitchViewModel item } toggle) return;
        await ViewModel.QuickToggleAsync(item, toggle.IsChecked == true);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, item.IsOn); // snap back if it failed
    }

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuickSwitchViewModel { Model.Bundle: { } bundle } }) return;
        ShowCustom(ViewModel.Bundles.FirstOrDefault(b => b.Id == bundle.Id));
    }

    private void GoCustomNew_Click(object sender, RoutedEventArgs e)
    {
        ShowCustom(null);
        NewBundle_Click(sender, e);
    }

    private void ShowCustom(BundleViewModel? select)
    {
        CustomTab.IsChecked = true;
        if (select != null) BundlesList.SelectedItem = select;
    }

    // ----- Custom: bundles -----

    private void BundlesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = BundlesList.SelectedItem as BundleViewModel;
        ViewModel.SelectedBundle = vm;
        DetailPanel.DataContext = vm;
        DetailPanel.Visibility = vm != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = vm != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: BundleViewModel vm } toggle) return;
        await ViewModel.ToggleAsync(vm, toggle.IsChecked == true);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, vm.Enabled); // re-sync in case the firewall call failed
    }

    private async void NewBundle_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.PromptText(Owner, "New bundle", "Bundle name");
        if (name == null) return;
        var created = await ViewModel.CreateBundleAsync(name);
        if (created != null) BundlesList.SelectedItem = created;
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel.SelectedBundle;
        if (vm == null) return;
        var name = Dialogs.PromptText(Owner, "Rename bundle", "Bundle name", vm.Name);
        if (name == null) return;
        await ViewModel.RenameSelectedAsync(name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel.SelectedBundle;
        if (vm == null) return;
        var confirm = MessageBox.Show(Owner,
            $"Delete bundle '{vm.Name}' and remove its firewall rules?",
            "Delete bundle", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        await ViewModel.DeleteSelectedAsync();
        BundlesList.SelectedItem = ViewModel.SelectedBundle;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import bundles",
            Filter = "Bundle files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Owner) != true) return;

        await ViewModel.ImportAsync(dlg.FileName);
        BundlesList.SelectedItem = ViewModel.SelectedBundle;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export bundles",
            Filter = "Bundle files (*.json)|*.json",
            FileName = "bundles.json",
        };
        if (dlg.ShowDialog(Owner) != true) return;

        await ViewModel.ExportAsync(dlg.FileName);
    }

    // ----- Custom: targets -----

    private async void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedBundle == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose a program to block",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Owner) != true) return;

        var target = new FirewallTarget
        {
            Type = TargetType.Program,
            Value = dlg.FileName,
            DisplayName = Path.GetFileName(dlg.FileName),
        };
        await ViewModel.AddTargetToSelectedAsync(target);
    }

    private async void AddIp_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedBundle == null) return;
        var target = Dialogs.PromptIpTarget(Owner);
        if (target == null) return;
        await ViewModel.AddTargetToSelectedAsync(target);
    }

    private async void AddCommon_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedBundle == null) return;
        var detected = CommonLaunchers.Detect();
        if (detected.Count == 0)
        {
            MessageBox.Show(Owner,
                "No known launchers were found in their default install locations.\n" +
                "Use \"+ Program\" to browse for an executable instead.",
                "Detected launchers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = Dialogs.PromptCommonLaunchers(Owner, detected);
        if (targets == null || targets.Count == 0) return;
        await ViewModel.AddTargetsToSelectedAsync(targets);
    }

    private async void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TargetViewModel target }) return;
        await ViewModel.RemoveTargetFromSelectedAsync(target);
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) => await ViewModel.VerifySelectedAsync();
}
