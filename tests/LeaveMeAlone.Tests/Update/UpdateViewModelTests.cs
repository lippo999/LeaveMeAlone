using System.IO;
using System.Net.Http;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Update;
using LeaveMeAlone.ViewModels;
using Xunit;

namespace LeaveMeAlone.Tests.Update;

public class UpdateViewModelTests
{
    private static readonly Version Current = new(0, 2, 0, 0);

    private sealed class Fixture
    {
        public FakeUpdateHttp Http { get; } = new();
        public List<string> Restarts { get; } = new();
        public AppLog Log { get; } = new(null);
        public string Exe { get; } = Path.Combine(Path.GetTempPath(), "lma-vm-" + Guid.NewGuid().ToString("N"), "LeaveMeAlone.exe");

        public UpdateViewModel Create(bool canInstall = true) => new(
            new UpdateChecker(Http, Current),
            canInstall ? new UpdateInstaller(Http, Exe) : null,
            Restarts.Add,
            Log);
    }

    [Fact]
    public async Task Check_exposes_the_available_version()
    {
        var f = new Fixture();
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");
        var vm = f.Create();

        await vm.CheckAsync();

        Assert.True(vm.HasUpdate);
        Assert.Equal("Version 0.3.0 is available", vm.Title);
    }

    [Fact]
    public async Task Check_with_no_newer_release_shows_nothing()
    {
        var f = new Fixture();
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.2.0");
        var vm = f.Create();

        await vm.CheckAsync();

        Assert.False(vm.HasUpdate);
    }

    [Fact]
    public async Task Check_failure_is_logged_quietly_without_showing_an_update()
    {
        var f = new Fixture();
        f.Http.Failure = new HttpRequestException("offline");
        var vm = f.Create();

        await vm.CheckAsync();

        Assert.False(vm.HasUpdate);
        Assert.Contains(f.Log.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("offline"));
    }

    [Fact]
    public async Task Install_swaps_the_exe_then_asks_for_a_restart()
    {
        var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Exe)!);
        File.WriteAllText(f.Exe, "old");
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");
        f.Http.Serve(Releases.Update("v0.3.0"), Releases.Bytes("new"));
        var vm = f.Create();
        await vm.CheckAsync();

        await vm.InstallAsync();

        Assert.Equal(new[] { f.Exe }, f.Restarts);
        Assert.Equal("new", File.ReadAllText(f.Exe));
        Directory.Delete(Path.GetDirectoryName(f.Exe)!, recursive: true);
    }

    [Fact]
    public async Task Install_failure_reports_the_error_and_does_not_restart()
    {
        var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Exe)!);
        File.WriteAllText(f.Exe, "old");
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");
        f.Http.Serve(Releases.Update("v0.3.0"), Releases.Bytes("new"), checksumOverride: new string('0', 64));
        var vm = f.Create();
        await vm.CheckAsync();

        await vm.InstallAsync();

        Assert.Empty(f.Restarts);
        Assert.StartsWith("Update failed", vm.StatusMessage);
        Assert.True(vm.HasUpdate); // still offered, so the user can retry
        Directory.Delete(Path.GetDirectoryName(f.Exe)!, recursive: true);
    }

    [Fact]
    public async Task Development_builds_never_check_or_install()
    {
        var f = new Fixture();
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");
        var vm = f.Create(canInstall: false);

        await vm.CheckAsync();

        Assert.False(vm.HasUpdate);
        Assert.Empty(f.Http.Requested);
    }

    [Fact]
    public async Task Check_now_reports_up_to_date()
    {
        var f = new Fixture();
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.2.0");
        var vm = f.Create();

        await vm.CheckNowAsync();

        Assert.Equal("You're up to date.", vm.CheckStatus);
        Assert.False(vm.HasUpdate);
        Assert.True(vm.CanCheckNow); // link re-enabled afterwards
    }

    [Fact]
    public async Task Check_now_shows_the_card_when_a_release_is_newer()
    {
        var f = new Fixture();
        f.Http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");
        var vm = f.Create();

        await vm.CheckNowAsync();

        Assert.True(vm.HasUpdate);
        Assert.Equal("0.3.0 is ready to install.", vm.CheckStatus);
    }

    [Fact]
    public async Task Check_now_shows_the_error_instead_of_hiding_it()
    {
        var f = new Fixture();
        f.Http.Failure = new HttpRequestException("offline");
        var vm = f.Create();

        await vm.CheckNowAsync();

        Assert.Contains("offline", vm.CheckStatus);
        Assert.True(vm.CanCheckNow);
    }

    [Fact]
    public async Task Background_check_never_writes_the_check_status()
    {
        var f = new Fixture();
        f.Http.Failure = new HttpRequestException("offline");
        var vm = f.Create();

        await vm.CheckAsync();

        Assert.Equal("", vm.CheckStatus);
    }

    [Fact]
    public void Version_text_shows_the_running_version()
    {
        var f = new Fixture();

        Assert.Equal("v0.2.0", f.Create().VersionText);
        Assert.Equal("v0.2.0 (dev build)", f.Create(canInstall: false).VersionText);
        Assert.False(f.Create(canInstall: false).CanCheckNow);
    }
}
