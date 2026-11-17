using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Update;

namespace LeaveMeAlone.ViewModels;

/// <summary>
/// Sidebar update UI: the version line with its "Check for updates" link, and the "update available"
/// card. Background checks are quiet (a failure is only logged); a check the user asked for always
/// reports its result. Installs only on request, then hands the new exe path to <c>restart</c> so the
/// app shuts down cleanly first.
/// </summary>
public sealed class UpdateViewModel : GuardedViewModel
{
    private readonly UpdateChecker _checker;
    private readonly UpdateInstaller? _installer;
    private readonly Action<string> _restart;
    private readonly AppLog? _log;
    private AvailableUpdate? _available;
    private bool _checking;
    private string _checkStatus = "";

    /// <param name="installer">Null for development builds (dotnet run): updates are disabled entirely.</param>
    /// <param name="restart">Called with the swapped-in exe path once the update is installed.</param>
    public UpdateViewModel(UpdateChecker checker, UpdateInstaller? installer, Action<string> restart, AppLog? log = null)
        : base(log, "Update")
    {
        _checker = checker;
        _installer = installer;
        _restart = restart;
        _log = log;
    }

    public bool HasUpdate => _available != null;
    public string Title => _available is null ? "" : $"Version {UpdateSource.Display(_available.Version)} is available";
    public string Subtitle => $"You have {UpdateSource.Display(_checker.CurrentVersion)}";
    public Uri? ReleasePage => _available?.PageUrl;

    /// <summary>"v0.3.0", or "v0.3.0 (dev build)" when self-update is off.</summary>
    public string VersionText => $"v{UpdateSource.Display(_checker.CurrentVersion)}{(_installer is null ? " (dev build)" : "")}";

    public bool CanUpdate => _installer != null;

    /// <summary>False for dev builds and while a check is running (the link is disabled).</summary>
    public bool CanCheckNow => _installer != null && !_checking;

    /// <summary>Result of the last check the user asked for ("You're up to date.", an error…).</summary>
    public string CheckStatus
    {
        get => _checkStatus;
        private set => Set(ref _checkStatus, value);
    }

    /// <summary>Background check (startup / every 24 h): failures are logged, never shown.</summary>
    public Task CheckAsync() => RunCheckAsync(userAsked: false);

    /// <summary>"Check for updates" link: the same check, but the outcome is always shown.</summary>
    public Task CheckNowAsync() => RunCheckAsync(userAsked: true);

    public async Task InstallAsync()
    {
        if (_installer is null || _available is not { } update) return;
        var installed = false;
        await RunGuardedAsync(async () =>
        {
            StatusMessage = $"Downloading {update.Tag}…";
            await _installer.InstallAsync(update);
            StatusMessage = $"Installed {update.Tag}. Restarting…";
            installed = true;
        }, "Update failed");
        if (installed) _restart(_installer.ExePath);
    }

    private async Task RunCheckAsync(bool userAsked)
    {
        if (!CanCheckNow) return;
        SetChecking(true);
        if (userAsked) CheckStatus = "Checking…";
        try
        {
            SetAvailable(await _checker.CheckAsync());
            var result = _available is null
                ? "You're up to date."
                : $"{UpdateSource.Display(_available.Version)} is ready to install.";
            if (userAsked) CheckStatus = result;
            if (userAsked || _available != null) _log?.Info("Update", $"Checked for updates: {result}");
        }
        catch (UpdateException ex)
        {
            if (userAsked) CheckStatus = ex.Message;
            _log?.Warn("Update", ex.Message, ex.InnerException?.ToString());
        }
        finally
        {
            SetChecking(false);
        }
    }

    private void SetChecking(bool checking)
    {
        _checking = checking;
        OnPropertyChanged(nameof(CanCheckNow));
    }

    private void SetAvailable(AvailableUpdate? update)
    {
        _available = update;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReleasePage));
    }
}
