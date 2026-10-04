using System.Collections.ObjectModel;
using LeaveMeAlone.Services;

namespace LeaveMeAlone.ViewModels;

/// <summary>One line on the Logs page.</summary>
public sealed record LogRow(LogEntry Entry)
{
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public string Level => Entry.Level.ToString().ToUpperInvariant();
    public string Source => Entry.Source;
    public string Message => Entry.Message;
    public string? Details => string.IsNullOrWhiteSpace(Entry.Details) ? null : Entry.Details;
    public bool IsError => Entry.Level == LogLevel.Error;
    public bool IsWarn => Entry.Level == LogLevel.Warn;
    public bool IsDebug => Entry.Level == LogLevel.Debug;
}

/// <summary>Live view over <see cref="AppLog"/>: newest first, filterable to problems only.</summary>
public sealed class LogViewModel : ObservableObject
{
    private readonly AppLog _log;
    private readonly Action<Action> _post;
    private bool _problemsOnly;

    /// <param name="post">Runs an action on the UI thread (entries arrive from any thread).</param>
    public LogViewModel(AppLog log, Action<Action> post)
    {
        _log = log;
        _post = post;
        _log.EntryAdded += entry => _post(() => Add(entry));
        Rebuild();
    }

    public ObservableCollection<LogRow> Rows { get; } = new();
    public string? Folder => _log.Directory;
    public bool IsEmpty => Rows.Count == 0;

    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set { if (Set(ref _problemsOnly, value)) Rebuild(); }
    }

    public int ProblemCount => _log.Snapshot().Count(e => e.Level >= LogLevel.Warn);
    public string ProblemsLabel => ProblemCount == 0 ? "Problems" : $"Problems ({ProblemCount})";

    public void Clear()
    {
        _log.Clear();
        Rebuild();
    }

    /// <summary>The visible rows as plain text (oldest first), for the clipboard.</summary>
    public string ToText() => string.Join(Environment.NewLine, Rows.Reverse().Select(r => AppLog.Format(r.Entry)));

    private void Add(LogEntry entry)
    {
        if (Shows(entry))
        {
            Rows.Insert(0, new LogRow(entry));
            while (Rows.Count > AppLog.DefaultCapacity) Rows.RemoveAt(Rows.Count - 1);
        }
        RaiseCounts();
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var entry in _log.Snapshot().Where(Shows).Reverse())
            Rows.Add(new LogRow(entry));
        RaiseCounts();
    }

    private bool Shows(LogEntry entry) => !_problemsOnly || entry.Level >= LogLevel.Warn;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(ProblemsLabel));
    }
}
