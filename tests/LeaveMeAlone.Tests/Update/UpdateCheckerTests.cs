using System.Net.Http;
using LeaveMeAlone.Services.Update;
using Xunit;

namespace LeaveMeAlone.Tests.Update;

public class UpdateCheckerTests
{
    private static readonly Version Current = new(0, 2, 0, 0); // what Assembly.GetName().Version reports

    [Fact]
    public void Newer_release_is_offered_with_its_download_links()
    {
        var update = ReleaseParser.Parse(Releases.Json("v0.3.0"), Current);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update!.Version);
        Assert.Equal("v0.3.0", update.Tag);
        Assert.Equal(Releases.ExeUrl("v0.3.0"), update.ExeUrl);
        Assert.Equal(Releases.ShaUrl("v0.3.0"), update.ChecksumUrl);
        Assert.Equal(new Uri("https://github.com/lippo999/LeaveMeAlone/releases/tag/v0.3.0"), update.PageUrl);
    }

    [Theory]
    [InlineData("v0.2.0")] // same version: 0.2.0 must not count as newer than the assembly's 0.2.0.0
    [InlineData("0.2.0")]
    [InlineData("v0.1.9")]
    public void Same_or_older_release_is_not_offered(string tag)
    {
        Assert.Null(ReleaseParser.Parse(Releases.Json(tag), Current));
    }

    [Fact]
    public void Patch_and_revision_bumps_count_as_newer()
    {
        Assert.NotNull(ReleaseParser.Parse(Releases.Json("v0.2.1"), Current));
        Assert.NotNull(ReleaseParser.Parse(Releases.Json("v0.2.0.1"), Current));
    }

    [Fact]
    public void Two_part_tag_is_a_valid_version_and_displays_with_three_parts()
    {
        var update = ReleaseParser.Parse(Releases.Json("v1.2"), Current);

        Assert.Equal(new Version(1, 2), update?.Version);
        Assert.Equal("1.2.0", UpdateSource.Display(update!.Version));
        Assert.Equal("0.2.0", UpdateSource.Display(Current));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.0-beta")]
    [InlineData("")]
    public void Tags_that_are_not_plain_versions_are_ignored(string tag)
    {
        Assert.Null(ReleaseParser.Parse(Releases.Json(tag), Current));
    }

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(ReleaseParser.Parse(Releases.Json("v9.0.0", prerelease: true), Current));
        Assert.Null(ReleaseParser.Parse(Releases.Json("v9.0.0", draft: true), Current));
    }

    [Fact]
    public void Release_missing_the_exe_or_checksum_is_ignored()
    {
        Assert.Null(ReleaseParser.Parse(Releases.Json("v9.0.0", includeExe: false), Current));
        Assert.Null(ReleaseParser.Parse(Releases.Json("v9.0.0", includeSha: false), Current));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"assets\": []}")]
    public void Malformed_response_throws_update_exception(string json)
    {
        Assert.Throws<UpdateException>(() => ReleaseParser.Parse(json, Current));
    }

    [Fact]
    public async Task Checker_asks_the_github_latest_release_endpoint()
    {
        var http = new FakeUpdateHttp();
        http.Strings[UpdateSource.LatestReleaseApi] = Releases.Json("v0.3.0");

        var update = await new UpdateChecker(http, Current).CheckAsync();

        Assert.Equal(new Version(0, 3, 0), update?.Version);
    }

    [Fact]
    public async Task Checker_wraps_network_failures()
    {
        var http = new FakeUpdateHttp { Failure = new HttpRequestException("offline") };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => new UpdateChecker(http, Current).CheckAsync());
        Assert.Contains("offline", ex.Message);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  LeaveMeAlone.exe\r\n")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void Checksum_file_is_parsed_from_sha256sum_format(string text)
    {
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", Checksum.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc  LeaveMeAlone.exe")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Invalid_checksum_file_throws(string text)
    {
        Assert.Throws<UpdateException>(() => Checksum.Parse(text));
    }
}
