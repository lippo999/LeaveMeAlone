using LeaveMeAlone.Services;
using LeaveMeAlone.ViewModels;
using Xunit;

namespace LeaveMeAlone.Tests;

public class LogViewModelTests
{
    private readonly AppLog _log = new(directory: null);

    private LogViewModel NewViewModel() => new(_log, post: action => action());

    [Fact]
    public void Shows_existing_and_new_entries_newest_first()
    {
        _log.Info("Block", "first");
        var vm = NewViewModel();

        _log.Error("Unblock", "second");

        Assert.Equal(new[] { "second", "first" }, vm.Rows.Select(r => r.Message));
        Assert.True(vm.Rows[0].IsError);
    }

    [Fact]
    public void ProblemsOnly_filters_to_warnings_and_errors_and_counts_them()
    {
        var vm = NewViewModel();
        _log.Info("t", "ok");
        _log.Warn("t", "hmm");
        _log.Error("t", "bad");

        vm.ProblemsOnly = true;

        Assert.Equal(new[] { "bad", "hmm" }, vm.Rows.Select(r => r.Message));
        Assert.Equal("Problems (2)", vm.ProblemsLabel);

        _log.Info("t", "hidden while filtered");
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void Clear_empties_the_view()
    {
        var vm = NewViewModel();
        _log.Info("t", "x");

        vm.Clear();

        Assert.True(vm.IsEmpty);
        Assert.Empty(_log.Snapshot());
    }

    [Fact]
    public void ToText_is_oldest_first_with_details()
    {
        var vm = NewViewModel();
        _log.Info("t", "one");
        _log.Error("t", "two", "stack");

        var text = vm.ToText();

        Assert.True(text.IndexOf("one", StringComparison.Ordinal) < text.IndexOf("two", StringComparison.Ordinal));
        Assert.Contains("    stack", text);
    }
}
