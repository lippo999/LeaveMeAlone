using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LeaveMeAlone.Models;
using LeaveMeAlone.Services;

namespace LeaveMeAlone.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>Display wrapper for a single firewall target.</summary>
public sealed class TargetViewModel : ObservableObject
{
    public TargetViewModel(FirewallTarget model) => Model = model;

    public FirewallTarget Model { get; }

    public string TypeLabel => Model.Type == TargetType.Program ? "PROGRAM" : "REMOTE IP";

    public string Title => !string.IsNullOrWhiteSpace(Model.DisplayName)
        ? Model.DisplayName
        : Model.Type == TargetType.Program ? Path.GetFileName(Model.Value) : Model.Value;

    public string Detail
    {
        get
        {
            if (Model.Type == TargetType.Program) return Model.Value;
            var port = string.IsNullOrWhiteSpace(Model.RemotePort) ? "all ports" : $"port {Model.RemotePort}";
            var proto = string.IsNullOrWhiteSpace(Model.Protocol) ? "Any" : Model.Protocol;
            return $"{Model.Value}  ·  {proto}  ·  {port}";
        }
    }
}

/// <summary>Display wrapper for a bundle and its targets.</summary>
public sealed class BundleViewModel : ObservableObject
{
    private Bundle _model;

    public BundleViewModel(Bundle model)
    {
        _model = model;
        Rebuild();
    }

    public string Id => _model.Id;
    public Bundle Model => _model;
    public ObservableCollection<TargetViewModel> Targets { get; } = new();

    public string Name => _model.Name;
    public bool Enabled => _model.Enabled;

    public string StatusText => _model.Enabled
        ? (Targets.Count == 0 ? "On · no targets yet" : $"Blocking · {Targets.Count} target(s)")
        : (Targets.Count == 0 ? "Off · no targets yet" : $"Off · {Targets.Count} target(s)");

    public void Update(Bundle model)
    {
        _model = model;
        Rebuild();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StatusText));
    }

    private void Rebuild()
    {
        Targets.Clear();
        foreach (var target in _model.Targets)
            Targets.Add(new TargetViewModel(target));
    }
}

/// <summary>One switch on the Quick block screen (a detected launcher or a user bundle).</summary>
public sealed class QuickSwitchViewModel : ObservableObject
{
    private QuickBlockItem _model;

    public QuickSwitchViewModel(QuickBlockItem model) => _model = model;

    public QuickBlockItem Model => _model;
    public string Title => _model.Title;
    public string Initial => _model.Title.Length > 0 ? _model.Title[..1].ToUpperInvariant() : "?";
    public string KindLabel => _model.Launcher != null ? "LAUNCHER" : "BUNDLE";
    public bool IsOn => _model.IsOn;
    public bool CanCustomize => _model.Bundle != null;

    public string StatusText
    {
        get
        {
            var count = _model.Bundle?.Targets.Count ?? _model.Launcher?.ExistingPaths.Count ?? 0;
            var what = _model.Launcher != null ? $"{count} program(s)" : $"{count} rule(s)";
            if (count == 0) return IsOn ? "On · nothing to block yet" : "Off · nothing to block yet";
            return IsOn ? $"Offline · {what} blocked" : $"Online · {what}";
        }
    }

    /// <summary>Always raises change events so a switch the user flipped snaps back if the change failed.</summary>
    public void Update(QuickBlockItem model)
    {
        _model = model;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCustomize));
    }
}

/// <summary>Status line + busy flag + error-to-status guard shared by the window view models.</summary>
public abstract class GuardedViewModel : ObservableObject
{
    private readonly AppLog? _log;
    private readonly string _logSource;
    private string _statusMessage = "";
    private bool _busy;
    private bool _reportingError; // the error is logged with details already; skip the plain Info copy

    /// <param name="log">Every status line is also written here (errors with full exception details).</param>
    protected GuardedViewModel(AppLog? log, string logSource)
    {
        _log = log;
        _logSource = logSource;
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (Set(ref _statusMessage, value) && !_reportingError && !string.IsNullOrWhiteSpace(value))
                _log?.Info(_logSource, value);
        }
    }

    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    protected async Task RunGuardedAsync(Func<Task> action, string errorTitle)
    {
        if (Busy) return;
        try
        {
            Busy = true;
            await action();
        }
        catch (Exception ex)
        {
            _log?.Error(_logSource, $"{errorTitle}: {ex.Message}", ex.ToString());
            _reportingError = true;
            try { StatusMessage = $"{errorTitle}: {ex.Message}"; }
            finally { _reportingError = false; }
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed class MainViewModel : GuardedViewModel
{
    private readonly BundleManager _manager;
    private readonly Func<IReadOnlyList<DetectedLauncher>> _detectLaunchers;
    private IReadOnlyList<DetectedLauncher> _launchers = Array.Empty<DetectedLauncher>();
    private BundleViewModel? _selectedBundle;
    private bool _loaded;

    public MainViewModel(BundleManager manager, bool isElevated,
        Func<IReadOnlyList<DetectedLauncher>>? detectLaunchers = null, AppLog? log = null)
        : base(log, "Block")
    {
        _manager = manager;
        IsElevated = isElevated;
        _detectLaunchers = detectLaunchers ?? CommonLaunchers.Detect;
    }

    public bool IsElevated { get; }
    public ObservableCollection<BundleViewModel> Bundles { get; } = new();

    /// <summary>The Quick screen: one switch per detected launcher, then the user's other bundles.</summary>
    public ObservableCollection<QuickSwitchViewModel> QuickItems { get; } = new();
    public bool HasQuickItems => QuickItems.Count > 0;

    /// <summary>"Nothing found" is only true once loading finished — not while the list is still empty.</summary>
    public bool ShowQuickEmpty => _loaded && QuickItems.Count == 0;

    public int BlockingCount => _manager.Bundles.Count(b => b.Enabled);
    public bool AnyBlocking => BlockingCount > 0;
    public string Summary => BlockingCount switch
    {
        0 => "Nothing blocked",
        1 => "1 blocking now",
        var n => $"{n} blocking now",
    };

    public BundleViewModel? SelectedBundle
    {
        get => _selectedBundle;
        set { if (Set(ref _selectedBundle, value)) OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => _selectedBundle != null;

    public async Task InitializeAsync()
    {
        await RunGuardedAsync(async () =>
        {
            _launchers = _detectLaunchers();
            var bundles = await _manager.InitializeAsync();
            Bundles.Clear();
            foreach (var bundle in bundles)
                Bundles.Add(new BundleViewModel(bundle));

            StatusMessage = IsElevated
                ? "Ready. Flip a switch to take something offline."
                : "Running without administrator rights — changes are disabled.";
        }, "Failed to load bundles");
        _loaded = true;
        RefreshQuick();
    }

    /// <summary>Quick-screen switch: blocks/unblocks a launcher (creating its bundle the first time) or a bundle.</summary>
    public async Task QuickToggleAsync(QuickSwitchViewModel item, bool on)
    {
        await RunGuardedAsync(async () =>
        {
            var model = item.Model;
            if (model.Bundle is { } existing)
            {
                var bundle = await _manager.SetEnabledAsync(existing.Id, on);
                Bundles.FirstOrDefault(b => b.Id == bundle.Id)?.Update(bundle);
            }
            else if (on && model.Launcher is { } launcher)
            {
                var bundle = await _manager.CreateBundleWithTargetsAsync(
                    launcher.Name, QuickBlock.TargetsFor(launcher), enabled: true);
                Bundles.Add(new BundleViewModel(bundle));
            }
            StatusMessage = on ? $"{item.Title} is offline — blocked by the firewall." : $"{item.Title} is back online.";
        }, $"Couldn't change {item.Title}");
        RefreshQuick();
    }

    /// <summary>Rebuilds the Quick switches and sidebar summary from the manager's bundles.</summary>
    public void RefreshQuick()
    {
        var items = QuickBlock.Build(_launchers, _manager.Bundles);
        if (items.Select(i => i.Title).SequenceEqual(QuickItems.Select(q => q.Title)))
        {
            // Same rows: update in place so switches animate instead of being re-created.
            for (var i = 0; i < items.Count; i++) QuickItems[i].Update(items[i]);
        }
        else
        {
            QuickItems.Clear();
            foreach (var item in items) QuickItems.Add(new QuickSwitchViewModel(item));
        }
        OnPropertyChanged(nameof(HasQuickItems));
        OnPropertyChanged(nameof(ShowQuickEmpty));
        OnPropertyChanged(nameof(BlockingCount));
        OnPropertyChanged(nameof(AnyBlocking));
        OnPropertyChanged(nameof(Summary));
    }

    public async Task<BundleViewModel?> CreateBundleAsync(string name)
    {
        BundleViewModel? created = null;
        await Mutate(async () =>
        {
            var bundle = await _manager.CreateBundleAsync(name);
            created = new BundleViewModel(bundle);
            Bundles.Add(created);
            SelectedBundle = created;
            StatusMessage = $"Created bundle '{bundle.Name}'.";
        }, "Failed to create bundle");
        return created;
    }

    public Task RenameSelectedAsync(string name) => WithSelected(async vm =>
    {
        var bundle = await _manager.RenameBundleAsync(vm.Id, name);
        vm.Update(bundle);
        StatusMessage = $"Renamed to '{bundle.Name}'.";
    }, "Failed to rename bundle");

    public Task DeleteSelectedAsync() => WithSelected(async vm =>
    {
        await _manager.DeleteBundleAsync(vm.Id);
        Bundles.Remove(vm);
        SelectedBundle = Bundles.FirstOrDefault();
        StatusMessage = "Bundle deleted.";
    }, "Failed to delete bundle");

    public Task ToggleAsync(BundleViewModel vm, bool enabled) => Mutate(async () =>
    {
        var bundle = await _manager.SetEnabledAsync(vm.Id, enabled);
        vm.Update(bundle);
        StatusMessage = enabled ? $"'{bundle.Name}' is now blocking." : $"'{bundle.Name}' is off.";
    }, "Failed to toggle bundle");

    public Task AddTargetToSelectedAsync(FirewallTarget target) => WithSelected(async vm =>
    {
        var bundle = await _manager.AddTargetAsync(vm.Id, target);
        vm.Update(bundle);
        StatusMessage = "Target added.";
    }, "Failed to add target");

    public Task AddTargetsToSelectedAsync(IEnumerable<FirewallTarget> targets) => WithSelected(async vm =>
    {
        Bundle bundle = vm.Model;
        foreach (var target in targets)
            bundle = await _manager.AddTargetAsync(vm.Id, target);
        vm.Update(bundle);
        StatusMessage = "Targets added.";
    }, "Failed to add targets");

    public Task RemoveTargetFromSelectedAsync(TargetViewModel target) => WithSelected(async vm =>
    {
        var bundle = await _manager.RemoveTargetAsync(vm.Id, target.Model.Id);
        vm.Update(bundle);
        StatusMessage = "Target removed.";
    }, "Failed to remove target");

    public Task VerifySelectedAsync() => WithSelected(async vm =>
    {
        var live = await _manager.GetLiveRuleNamesAsync(vm.Id);
        StatusMessage = $"{live.Count} live firewall rule(s) for '{vm.Name}'.";
    }, "Failed to read firewall");

    public Task ExportAsync(string path) => RunGuardedAsync(async () =>
    {
        await _manager.ExportAsync(path);
        StatusMessage = $"Exported {Bundles.Count} bundle(s).";
    }, "Failed to export");

    public Task ImportAsync(string path) => Mutate(async () =>
    {
        var count = await _manager.ImportAsync(path);
        Bundles.Clear();
        foreach (var bundle in _manager.Bundles)
            Bundles.Add(new BundleViewModel(bundle));
        SelectedBundle = Bundles.FirstOrDefault();
        StatusMessage = $"Imported {count} bundle(s).";
    }, "Failed to import");

    private async Task WithSelected(Func<BundleViewModel, Task> action, string errorTitle)
    {
        var vm = SelectedBundle;
        if (vm == null) return;
        await Mutate(() => action(vm), errorTitle);
    }

    private async Task Mutate(Func<Task> action, string errorTitle)
    {
        await RunGuardedAsync(action, errorTitle);
        RefreshQuick();
    }
}
