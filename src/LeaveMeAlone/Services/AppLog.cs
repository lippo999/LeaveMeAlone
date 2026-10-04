using System.IO;
using System.Text;

namespace LeaveMeAlone.Services;

public enum LogLevel { Debug, Info, Warn, Error }

/// <param name="Details">Extra lines (exception text, command output); file + tooltip only.</param>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message, string? Details = null);

/// <summary>
/// App activity log: the last <c>capacity</c> entries in memory (for the Logs page) plus one text
/// file per day under <c>%APPDATA%\LeaveMeAlone\logs</c>. Thread-safe; writing never throws, so a
/// broken log can't break the action being logged.
/// </summary>
public sealed class AppLog
{
    public const int DefaultCapacity = 1000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly string? _directory;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="directory">Folder for the daily files; null keeps the log in memory only.</param>
    public AppLog(string? directory, int capacity = DefaultCapacity, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _capacity = capacity;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory => JsonFile.AppDataPath("logs");

    /// <summary>Raised on the writing thread after an entry is stored.</summary>
    public event Action<LogEntry>? EntryAdded;

    public string? Directory => _directory;
    public string? CurrentFile => _directory is null ? null : Path.Combine(_directory, $"app-{_clock():yyyyMMdd}.log");

    public void Debug(string source, string message, string? details = null) => Write(LogLevel.Debug, source, message, details);
    public void Info(string source, string message, string? details = null) => Write(LogLevel.Info, source, message, details);
    public void Warn(string source, string message, string? details = null) => Write(LogLevel.Warn, source, message, details);
    public void Error(string source, string message, string? details = null) => Write(LogLevel.Error, source, message, details);

    public void Write(LogLevel level, string source, string message, string? details = null)
    {
        var entry = new LogEntry(_clock(), level, source, message, details);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
            AppendToFile(entry);
        }
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>Clears the in-memory view; files on disk are kept.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public static string Format(LogEntry e)
    {
        var line = new StringBuilder()
            .Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(e.Level.ToString().ToUpperInvariant().PadRight(5)).Append(' ')
            .Append('[').Append(e.Source).Append("] ")
            .Append(e.Message);
        if (!string.IsNullOrWhiteSpace(e.Details))
        {
            foreach (var detail in e.Details.Replace("\r\n", "\n").TrimEnd().Split('\n'))
                line.AppendLine().Append("    ").Append(detail);
        }
        return line.ToString();
    }

    // Called under _gate, so lines from different threads never interleave.
    private void AppendToFile(LogEntry entry)
    {
        var file = CurrentFile;
        if (file is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(_directory!);
            File.AppendAllText(file, Format(entry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down; the entry is still kept in memory.
        }
    }
}
