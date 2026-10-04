using System.IO;
using LeaveMeAlone.Models;
using LeaveMeAlone.Services;
using Xunit;

namespace LeaveMeAlone.Tests;

public class BundleStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"fbb-store-{Guid.NewGuid():N}", "bundles.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        var store = new JsonBundleStore(_path);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        var store = new JsonBundleStore(_path);
        var bundle = new Bundle
        {
            Name = "Steam",
            Enabled = true,
            Targets = new[]
            {
                new FirewallTarget { Type = TargetType.Program, Value = @"C:\steam.exe", DisplayName = "steam" },
                new FirewallTarget { Type = TargetType.RemoteAddress, Value = "203.0.113.0/24", RemotePort = "27015", Protocol = "TCP" },
            },
        };

        await store.SaveAsync(new[] { bundle });
        var loaded = await store.LoadAsync();

        var only = Assert.Single(loaded);
        Assert.Equal("Steam", only.Name);
        Assert.True(only.Enabled);
        Assert.Equal(2, only.Targets.Count);
        Assert.Equal(TargetType.RemoteAddress, only.Targets[1].Type);
        Assert.Equal("27015", only.Targets[1].RemotePort);
    }

    [Fact]
    public async Task Save_WritesEnumsAsStrings()
    {
        var store = new JsonBundleStore(_path);
        await store.SaveAsync(new[]
        {
            new Bundle { Name = "X", Targets = new[] { new FirewallTarget { Type = TargetType.Program, Value = "a" } } },
        });

        var json = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"Program\"", json);
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsEmptyAndBacksUp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not valid json");

        var store = new JsonBundleStore(_path);
        var result = await store.LoadAsync();

        Assert.Empty(result);
        Assert.True(File.Exists(_path + ".corrupt"));
    }
}
