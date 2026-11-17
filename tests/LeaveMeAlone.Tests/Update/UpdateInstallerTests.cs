using System.IO;
using System.Net.Http;
using LeaveMeAlone.Services.Update;
using Xunit;

namespace LeaveMeAlone.Tests.Update;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lma-update-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly FakeUpdateHttp _http = new();

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "LeaveMeAlone.exe");
        File.WriteAllText(_exe, "old build");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private UpdateInstaller Installer() => new(_http, _exe);

    [Fact]
    public async Task Install_replaces_the_exe_and_keeps_the_old_one_aside()
    {
        var update = Releases.Update();
        _http.Serve(update, Releases.Bytes("new build"));

        await Installer().InstallAsync(update);

        Assert.Equal("new build", File.ReadAllText(_exe));
        Assert.Equal("old build", File.ReadAllText(_exe + UpdateInstaller.OldSuffix));
        Assert.False(File.Exists(_exe + UpdateInstaller.NewSuffix));
    }

    [Fact]
    public async Task Checksum_mismatch_leaves_the_current_exe_untouched()
    {
        var update = Releases.Update();
        _http.Serve(update, Releases.Bytes("tampered"), checksumOverride: new string('0', 64));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Installer().InstallAsync(update));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old build", File.ReadAllText(_exe));
        Assert.False(File.Exists(_exe + UpdateInstaller.NewSuffix));
        Assert.False(File.Exists(_exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public async Task Download_failure_leaves_the_current_exe_untouched()
    {
        var update = Releases.Update();
        _http.Failure = new HttpRequestException("connection reset");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Installer().InstallAsync(update));

        Assert.Contains("connection reset", ex.Message);
        Assert.Equal("old build", File.ReadAllText(_exe));
        Assert.False(File.Exists(_exe + UpdateInstaller.NewSuffix));
    }

    [Fact]
    public void Failed_swap_restores_the_original_exe()
    {
        // No staged file: moving it into place fails after the current exe was already moved aside.
        Assert.Throws<UpdateException>(() => Installer().SwapIn(_exe + UpdateInstaller.NewSuffix));

        Assert.Equal("old build", File.ReadAllText(_exe));
        Assert.False(File.Exists(_exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public async Task Leftover_old_exe_from_a_previous_update_is_replaced()
    {
        File.WriteAllText(_exe + UpdateInstaller.OldSuffix, "ancient build");
        var update = Releases.Update();
        _http.Serve(update, Releases.Bytes("new build"));

        await Installer().InstallAsync(update);

        Assert.Equal("old build", File.ReadAllText(_exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public void Cleanup_removes_leftovers_and_reports_them()
    {
        File.WriteAllText(_exe + UpdateInstaller.OldSuffix, "previous");
        File.WriteAllText(_exe + UpdateInstaller.NewSuffix, "half download");

        var removed = UpdateInstaller.CleanupLeftovers(_exe);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(_exe + UpdateInstaller.OldSuffix));
        Assert.False(File.Exists(_exe + UpdateInstaller.NewSuffix));
        Assert.True(File.Exists(_exe));
    }

    [Fact]
    public void Cleanup_with_nothing_to_remove_returns_zero()
    {
        Assert.Equal(0, UpdateInstaller.CleanupLeftovers(_exe));
    }
}
