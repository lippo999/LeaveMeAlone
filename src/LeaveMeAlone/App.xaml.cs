using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.Services.Unblock.Scan;
using LeaveMeAlone.Services.Update;
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

        WaitForPreviousInstance(e.Args);
        MigrateFromLegacyName();

        var log = new AppLog(AppLog.DefaultDirectory);
        WatchCrashes(log);
        log.Info("App", $"LeaveMeAlone {typeof(App).Assembly.GetName().Version} started " +
                        $"({(Platform.IsElevated() ? "administrator" : "read-only, not administrator")}).");

        var updateViewModel = CreateUpdater(log);

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
        var unblockViewModel = new UnblockViewModel(_unblock, Platform.IsElevated(), log, NewSteamScanner(_unblock, runner));
        var logViewModel = new LogViewModel(log, action => Dispatcher.BeginInvoke(action));

        var window = new MainWindow(viewModel, unblockViewModel, logViewModel, updateViewModel);
        MainWindow = window;
        window.Show();

        _tray = new TrayIcon(window, viewModel);
        _ = RestoreUnblocksAsync(unblockViewModel, viewModel);
        StartUpdateChecks(updateViewModel);
    }

    private const string UpdatedFromArg = "--updated-from";
    private static readonly TimeSpan PreviousInstanceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private GitHubUpdateHttp? _updateHttp;
    private DispatcherTimer? _updateTimer;
    private string? _restartInto;

    // Only a published single-file exe updates itself; under `dotnet run` the "exe" is a build output.
    private UpdateViewModel CreateUpdater(AppLog log)
    {
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
        var exePath = Environment.ProcessPath;
#pragma warning disable IL3000 // Location is empty exactly when running as a single-file bundle — that's the check.
        var isPublished = string.IsNullOrEmpty(typeof(App).Assembly.Location) && exePath != null;
#pragma warning restore IL3000

        _updateHttp = new GitHubUpdateHttp(version);
        UpdateInstaller? installer = null;
        if (isPublished)
        {
            installer = new UpdateInstaller(_updateHttp, exePath!);
            if (UpdateInstaller.CleanupLeftovers(exePath!) > 0) log.Info("Update", $"Updated to {UpdateSource.Display(version)}.");
        }
        else
        {
            log.Debug("Update", "Development build: automatic updates are off.");
        }
        return new UpdateViewModel(new UpdateChecker(_updateHttp, version), installer, RestartInto, log);
    }

    private void StartUpdateChecks(UpdateViewModel updates)
    {
        _ = updates.CheckAsync();
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += async (_, _) => await updates.CheckAsync();
        _updateTimer.Start();
    }

    // The new exe is started from OnExit, after relays and hosts lines are released, so the two
    // instances never fight over 127.77.0.x:443 or the hosts file.
    private void RestartInto(string exePath)
    {
        _restartInto = exePath;
        Shutdown();
    }

    private void LaunchUpdatedExe()
    {
        if (_restartInto is null) return;
        try
        {
            // Inherits this process's administrator token, so no second UAC prompt.
            var start = new ProcessStartInfo(_restartInto) { UseShellExecute = false };
            start.ArgumentList.Add(UpdatedFromArg);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"The update was installed but the app couldn't restart itself:\n{ex.Message}\n\n" +
                            "Start LeaveMeAlone again to use the new version.",
                            "LeaveMeAlone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // Started by an update: let the old instance finish exiting (it was releasing relays and the hosts file).
    private static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, UpdatedFromArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(PreviousInstanceTimeout);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    // Discovery sources for "Scan & fix"; everything they return is limited to Steam domains.
    private static SteamScanner NewSteamScanner(UnblockManager unblock, IProcessRunner runner)
    {
        var http = new HttpClient { Timeout = PageFetchTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LeaveMeAlone");
        return new SteamScanner(unblock, new IDomainSource[]
        {
            new DnsCacheSource(runner), // first: unblocking flushes the cache
            new SteamLogSource(SteamLogSource.FindSteamDirectory),
            new SteamPageSource(http),
        });
    }

    private static readonly TimeSpan PageFetchTimeout = TimeSpan.FromSeconds(15);

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
        _updateTimer?.Stop();
        _tray?.Dispose();
        ShutdownUnblocks();
        _updateHttp?.Dispose();
        LaunchUpdatedExe();
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
