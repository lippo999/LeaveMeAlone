using System.IO;
using System.Windows;
using FirewallBundleBlocker.Services;
using FirewallBundleBlocker.Services.Unblock;
using FirewallBundleBlocker.ViewModels;
using FirewallBundleBlocker.Views;

namespace FirewallBundleBlocker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool elevated = Platform.IsElevated();
        if (!elevated && TryElevate())
        {
            // App relaunched (or was cancelled) — this instance is shutting down.
            return;
        }

        var runner = new ProcessRunner();
        var firewall = new PowerShellFirewallService(runner);
        var store = new JsonBundleStore();
        var manager = new BundleManager(store, firewall);
        var viewModel = new MainViewModel(manager, Platform.IsElevated());

        _relay = new SniRelayHost();
        _unblock = new UnblockManager(
            new JsonUnblockStore(),
            new WindowsHostsFile(runner),
            new DomainDiagnoser(new SystemDnsResolver(), new DohResolver(), new ConnectivityProbe()),
            _relay);
        var unblockViewModel = new UnblockViewModel(_unblock, Platform.IsElevated());

        var window = new MainWindow(viewModel, unblockViewModel);
        MainWindow = window;
        window.Show();

        _tray = new TrayIcon(window, viewModel);
        _ = RestoreUnblocksAsync(unblockViewModel, viewModel);
    }

    private TrayIcon? _tray;
    private UnblockManager? _unblock;
    private SniRelayHost? _relay;

    // Re-applies saved unblocks (relays + hosts block); problems surface in the main status bar.
    private static async Task RestoreUnblocksAsync(UnblockViewModel unblock, MainViewModel main)
    {
        await unblock.InitializeAsync();
        if (!string.IsNullOrEmpty(unblock.StatusMessage)) main.StatusMessage = unblock.StatusMessage;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        ShutdownUnblocks();
        base.OnExit(e);
    }

    // Relays die with the process, so their hosts lines are removed now (re-added on next launch);
    // otherwise those domains would point at a dead loopback address while the app is closed.
    private void ShutdownUnblocks()
    {
        if (_unblock is null) return;
        try
        {
            _unblock.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is UnblockException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Couldn't remove the relay entries from the hosts file on exit:\n{ex.Message}\n\n" +
                "They are fixed automatically the next time the app starts.",
                "Firewall Bundle Blocker", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _relay?.Dispose();
        }
    }

    /// Returns true if this instance should stop starting up (relaunched elevated or cancelled).
    private bool TryElevate()
    {
        var choice = MessageBox.Show(
            "Firewall Bundle Blocker needs administrator rights to create or toggle firewall rules.\n\n" +
            "Relaunch as administrator now?",
            "Administrator required",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
        {
            Shutdown();
            return true;
        }
        if (choice == MessageBoxResult.Yes && Platform.TryRelaunchAsAdmin())
        {
            Shutdown();
            return true;
        }
        // "No", or relaunch failed: continue in read-only mode.
        return false;
    }
}
