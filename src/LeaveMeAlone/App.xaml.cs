using System.IO;
using System.Windows;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.ViewModels;
using LeaveMeAlone.Views;

namespace LeaveMeAlone;

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

        MigrateFromLegacyName();

        var log = new AppLog(AppLog.DefaultDirectory);
        WatchCrashes(log);
        log.Info("App", $"LeaveMeAlone {typeof(App).Assembly.GetName().Version} started " +
                        $"({(Platform.IsElevated() ? "administrator" : "read-only, not administrator")}).");

        var runner = new ProcessRunner(log);
        var firewall = new PowerShellFirewallService(runner);
        var store = new JsonBundleStore();
        var manager = new BundleManager(store, firewall);
        var viewModel = new MainViewModel(manager, Platform.IsElevated(), log: log);

        _relay = new SniRelayHost();
        _unblock = new UnblockManager(
            new JsonUnblockStore(),
            new WindowsHostsFile(runner),
            new DomainDiagnoser(new SystemDnsResolver(), new DohResolver(), new ConnectivityProbe()),
            _relay);
        var unblockViewModel = new UnblockViewModel(_unblock, Platform.IsElevated(), log);
        var logViewModel = new LogViewModel(log, action => Dispatcher.BeginInvoke(action));

        var window = new MainWindow(viewModel, unblockViewModel, logViewModel);
        MainWindow = window;
        window.Show();

        _tray = new TrayIcon(window, viewModel);
        _ = RestoreUnblocksAsync(unblockViewModel, viewModel);
    }

    // Unexpected exceptions go to the log file before anything else happens, so a crash leaves a trace.
    private void WatchCrashes(AppLog log)
    {
        DispatcherUnhandledException += (_, e) => log.Error("App", $"Unhandled error: {e.Exception.Message}", e.Exception.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.Error("App", $"Unobserved task error: {e.Exception.GetBaseException().Message}", e.Exception.ToString());
    }

    // The app used to be called "Firewall Bundle Blocker"; carry its data and autostart over.
    private static void MigrateFromLegacyName()
    {
        var warning = JsonFile.MigrateLegacyAppData();
        try
        {
            StartupRegistration.MigrateLegacy();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            warning = (warning is null ? "" : warning + "\n\n") + $"Couldn't update the 'Start with Windows' entry: {ex.Message}";
        }

        if (warning != null)
            MessageBox.Show(warning, "LeaveMeAlone", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                "LeaveMeAlone", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            "LeaveMeAlone needs administrator rights to create or toggle firewall rules.\n\n" +
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
