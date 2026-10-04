using System.IO;
using LeaveMeAlone.Services;
using Xunit;

namespace LeaveMeAlone.Tests;

public class AppLogTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(7));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lma-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_KeepsEntryInMemoryAndRaisesEvent()
    {
        var log = new AppLog(directory: null, clock: () => Noon);
        LogEntry? raised = null;
        log.EntryAdded += e => raised = e;

        log.Info("Block", "Steam is offline.");

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("Block", entry.Source);
        Assert.Equal("Steam is offline.", entry.Message);
        Assert.Equal(Noon, entry.Time);
        Assert.Same(entry, raised);
    }

    [Fact]
    public void Memory_IsCappedAndDropsOldestFirst()
    {
        var log = new AppLog(directory: null, capacity: 3);

        for (var i = 1; i <= 5; i++) log.Info("t", $"m{i}");

        Assert.Equal(new[] { "m3", "m4", "m5" }, log.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void Write_AppendsToDailyFile_WithDetailsIndented()
    {
        var log = new AppLog(_dir, clock: () => Noon);

        log.Info("Unblock", "Checking store.steampowered.com");
        log.Error("Block", "Couldn't change Steam", "line one\nline two");

        var file = Path.Combine(_dir, "app-20261004.log");
        Assert.Equal(file, log.CurrentFile);
        var text = File.ReadAllText(file);
        Assert.Contains("INFO  [Unblock] Checking store.steampowered.com", text);
        Assert.Contains("ERROR [Block] Couldn't change Steam", text);
        Assert.Contains("    line one", text);
        Assert.Contains("    line two", text);
    }

    [Fact]
    public void Clear_EmptiesMemoryButKeepsFile()
    {
        var log = new AppLog(_dir, clock: () => Noon);
        log.Warn("t", "kept on disk");

        log.Clear();

        Assert.Empty(log.Snapshot());
        Assert.Contains("kept on disk", File.ReadAllText(log.CurrentFile!));
    }

    [Fact]
    public void Write_DoesNotThrow_WhenFileCannotBeWritten()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(blocker, "");
        var log = new AppLog(blocker, clock: () => Noon); // directory path is a file → writes fail

        log.Info("t", "still in memory");

        Assert.Single(log.Snapshot());
    }
}
