using LeaveMeAlone.Services;
using Xunit;

namespace LeaveMeAlone.Tests;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Apps\LeaveMeAlone.exe";
    private const string User = @"PC\alice";

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Scripts { get; } = new();
        public string? RegisteredExe { get; set; }
        public bool FailRegister { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
        {
            Scripts.Add(arguments);
            if (arguments.Contains("Register-ScheduledTask") && !arguments.Contains("Unregister"))
            {
                if (FailRegister) return Task.FromResult(new ProcessResult(1, "", "Access is denied."));
                RegisteredExe = Exe;
            }
            else if (arguments.Contains("Unregister-ScheduledTask"))
            {
                RegisteredExe = null;
            }
            else if (arguments.Contains("Get-ScheduledTask"))
            {
                return Task.FromResult(new ProcessResult(0, RegisteredExe ?? "", ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public bool Registered => Scripts.Any(s => s.Contains("Register-ScheduledTask") && !s.Contains("Unregister"));
    }

    private sealed class FakeRunKey : IRunKey
    {
        public HashSet<string> Values { get; } = new();
        public bool Contains(string valueName) => Values.Contains(valueName);
        public void Remove(string valueName) => Values.Remove(valueName);
    }

    private static (StartupRegistration, FakeRunner, FakeRunKey) Create()
    {
        var runner = new FakeRunner();
        var runKey = new FakeRunKey();
        return (new StartupRegistration(runner, runKey, Exe, User), runner, runKey);
    }

    [Fact]
    public async Task SetEnabled_RegistersElevatedLogonTaskForCurrentUser()
    {
        var (startup, runner, _) = Create();

        await startup.SetEnabledAsync(true);

        var script = runner.Scripts.Single();
        Assert.Contains("-AtLogOn -User 'PC\\alice'", script);
        Assert.Contains("-RunLevel Highest", script);
        Assert.Contains($"-Execute '{Exe}'", script);
        Assert.Contains("-AllowStartIfOnBatteries", script);
        Assert.Contains("-ExecutionTimeLimit ([TimeSpan]::Zero)", script);
        Assert.True(await startup.IsEnabledAsync());
    }

    [Fact]
    public async Task SetDisabled_UnregistersTask()
    {
        var (startup, runner, _) = Create();
        runner.RegisteredExe = Exe;

        await startup.SetEnabledAsync(false);

        Assert.False(await startup.IsEnabledAsync());
    }

    [Fact]
    public async Task SetEnabled_Throws_WhenSchedulerFails()
    {
        var (startup, runner, _) = Create();
        runner.FailRegister = true;

        var ex = await Assert.ThrowsAsync<StartupException>(() => startup.SetEnabledAsync(true));
        Assert.Equal("Access is denied.", ex.Message);
    }

    [Fact]
    public void TaskName_IsPerUserAndHasNoBackslash()
    {
        var (startup, _, _) = Create();
        Assert.Equal("Start at sign-in (PC_alice)", startup.TaskName);
    }

    [Theory]
    [InlineData("LeaveMeAlone")]
    [InlineData("FirewallBundleBlocker")]
    public async Task Initialize_MovesRunKeyEntryToTask(string legacyName)
    {
        var (startup, runner, runKey) = Create();
        runKey.Values.Add(legacyName);

        var enabled = await startup.InitializeAsync();

        Assert.True(enabled);
        Assert.True(runner.Registered);
        Assert.Empty(runKey.Values);
    }

    [Fact]
    public async Task Initialize_KeepsRunKeyEntry_WhenTaskCannotBeCreated()
    {
        var (startup, runner, runKey) = Create();
        runKey.Values.Add("LeaveMeAlone");
        runner.FailRegister = true;

        await Assert.ThrowsAsync<StartupException>(() => startup.InitializeAsync());
        Assert.Contains("LeaveMeAlone", runKey.Values);
    }

    [Fact]
    public async Task Initialize_RepointsTask_WhenExeMoved()
    {
        var (startup, runner, _) = Create();
        runner.RegisteredExe = @"D:\Old\LeaveMeAlone.exe";

        var enabled = await startup.InitializeAsync();

        Assert.True(enabled);
        Assert.True(runner.Registered);
        Assert.Equal(Exe, runner.RegisteredExe);
    }

    [Fact]
    public async Task Initialize_DoesNothing_WhenTaskIsCurrent()
    {
        var (startup, runner, _) = Create();
        runner.RegisteredExe = Exe.ToUpperInvariant();

        Assert.True(await startup.InitializeAsync());
        Assert.False(runner.Registered);
    }

    [Fact]
    public async Task Initialize_ReportsDisabled_WhenNothingRegistered()
    {
        var (startup, runner, _) = Create();

        Assert.False(await startup.InitializeAsync());
        Assert.False(runner.Registered);
    }

    [Fact]
    public void Quote_DoublesSingleQuotes()
        => Assert.Equal("'O''Brien'", PowerShellScript.Quote("O'Brien"));
}
