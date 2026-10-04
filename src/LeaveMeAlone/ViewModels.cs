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

/// <summary>Status line + busy flag + error-to-status guard shared by the window view models.</summary>
public abstract class GuardedViewModel : ObservableObject
{
    private string _statusMessage = "";
    private bool _busy;

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
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
            StatusMessage = $"{errorTitle}: {ex.Message}";
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
    private BundleViewModel? _selectedBundle;

    public MainViewModel(BundleManager manager, bool isElevated)
    {
        _manager = manager;
        IsElevated = isElevated;
    }

    public bool IsElevated { get; }
    public ObservableCollection<BundleViewModel> Bundles { get; } = new();

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
            var bundles = await _manager.InitializeAsync();
            Bundles.Clear();
            foreach (var bundle in bundles)
                Bundles.Add(new BundleViewModel(bundle));

            StatusMessage = IsElevated
                ? $"{Bundles.Count} bundle(s) loaded."
                : "Running without administrator rights — changes are disabled.";
        }, "Failed to load bundles");
    }

    public async Task<BundleViewModel?> CreateBundleAsync(string name)
    {
        BundleViewModel? created = null;
        await RunGuardedAsync(async () =>
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

    public Task ToggleAsync(BundleViewModel vm, bool enabled) => RunGuardedAsync(async () =>
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

    public Task ImportAsync(string path) => RunGuardedAsync(async () =>
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
        await RunGuardedAsync(() => action(vm), errorTitle);
    }
}
