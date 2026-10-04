using System.IO;
using LeaveMeAlone.Services;
using Xunit;

namespace LeaveMeAlone.Tests;

public class JsonFileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lma-migrate-{Guid.NewGuid():N}");
    private string Legacy => Path.Combine(_root, "FirewallBundleBlocker");
    private string Current => Path.Combine(_root, JsonFile.AppFolderName);

    public JsonFileMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MovesLegacyFolder_WhenNewOneDoesNotExist()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "bundles.json"), "[]");

        var warning = JsonFile.MigrateLegacyAppData(_root);

        Assert.Null(warning);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Current, "bundles.json")));
    }

    [Fact]
    public void LeavesBothAlone_WhenNewFolderAlreadyExists()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);

        Assert.Null(JsonFile.MigrateLegacyAppData(_root));
        Assert.True(Directory.Exists(Legacy));
    }

    [Fact]
    public void DoesNothing_WhenThereIsNoLegacyFolder()
    {
        Assert.Null(JsonFile.MigrateLegacyAppData(_root));
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void ReturnsWarningAndKeepsData_WhenMoveFails()
    {
        Directory.CreateDirectory(Legacy);
        var locked = Path.Combine(Legacy, "bundles.json");
        File.WriteAllText(locked, "[]");

        string? warning;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            warning = JsonFile.MigrateLegacyAppData(_root);

        Assert.NotNull(warning);
        Assert.True(File.Exists(locked));
    }
}
