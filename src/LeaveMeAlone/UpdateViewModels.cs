using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Update;

namespace LeaveMeAlone.ViewModels;

/// <summary>
/// Sidebar "update available" card. Checks quietly (a failed check is only logged), installs only when
/// the user asks, then hands the new exe path to <c>restart</c> so the app can shut down cleanly first.
/// </summary>
public sealed class UpdateViewModel : GuardedViewModel
{
    private readonly UpdateChecker _checker;
    private readonly UpdateInstaller? _installer;
    private readonly Action<string> _restart;
    private readonly AppLog? _log;
    private AvailableUpdate? _available;

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

    public async Task CheckAsync()
    {
        if (_installer is null) return;
        try
        {
            SetAvailable(await _checker.CheckAsync());
            if (_available != null)
                _log?.Info("Update", $"{_available.Tag} is available (running {UpdateSource.Display(_checker.CurrentVersion)}).");
        }
        catch (UpdateException ex)
        {
            _log?.Warn("Update", ex.Message, ex.InnerException?.ToString());
        }
    }

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

    private void SetAvailable(AvailableUpdate? update)
    {
        _available = update;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReleasePage));
    }
}
