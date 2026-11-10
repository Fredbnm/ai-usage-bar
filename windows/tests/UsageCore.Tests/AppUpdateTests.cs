using System.Text;
using AIUsageBar.Core;

namespace UsageCore.Tests;

public class AppUpdateTests
{
    private const string Download = "https://github.com/matheusvelloso/ai-usage-bar/releases/download";

    private static byte[] Release(string tag = "v0.8.0", bool draft = false, bool prerelease = false, string? assets = null) =>
        Encoding.UTF8.GetBytes($$"""
        {"tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
         "html_url": "https://github.com/matheusvelloso/ai-usage-bar/releases/tag/{{tag}}",
         "assets": {{assets ?? $$"""
           [
             {"name": "AIUsageBar-macOS.dmg", "size": 1606117, "browser_download_url": "{{Download}}/{{tag}}/AIUsageBar-macOS.dmg"},
             {"name": "AIUsageBar-Windows-Setup.exe", "size": 75831738, "browser_download_url": "{{Download}}/{{tag}}/AIUsageBar-Windows-Setup.exe"},
             {"name": "AIUsageBar-Windows-Portable.exe", "size": 81088441, "browser_download_url": "{{Download}}/{{tag}}/AIUsageBar-Windows-Portable.exe"}
           ]
           """}}}
        """);

    private static AppVersion V(string text) => AppVersion.Parse(text) ?? throw new ArgumentException(text);

    [Fact]
    public void VersionsCompareNumberByNumber()
    {
        Assert.True(V("0.9.0") < V("0.10.0"));
        Assert.True(V("0.7.0") < V("v0.7.1"));
        Assert.True(V("0.7.9") < V("1.0"));
        Assert.Equal(V("0.7"), V("0.7.0"));
        Assert.Equal(V("1.2.3"), V("v1.2.3-beta+abc"));
        Assert.False(V("0.7.0") < V("0.7.0"));
        Assert.Equal("2.0.1", V("V2.0.1").ToString());
        Assert.Equal(V("0.7.0"), AppVersion.From(new Version(0, 7, 0, 0)));
        Assert.Equal(V("0.7.0"), AppVersion.From(new Version(0, 7)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1..2")]
    [InlineData("1.2.x")]
    [InlineData("-1.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData(null)]
    public void RejectsTextThatIsNotAVersion(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void ParsesTheLatestRelease()
    {
        var release = AppUpdates.Parse(Release());
        Assert.Equal(V("0.8.0"), release.Version);
        Assert.Equal("https://github.com/matheusvelloso/ai-usage-bar/releases/tag/v0.8.0", release.Page.ToString());
        var setup = Assert.IsType<ReleaseAsset>(release.Asset(AppUpdates.SetupAsset));
        Assert.Equal(75_831_738, setup.Size);
        Assert.Equal($"{Download}/v0.8.0/AIUsageBar-Windows-Setup.exe", setup.Url.ToString());
        Assert.NotNull(release.Asset(AppUpdates.PortableAsset));
    }

    [Fact]
    public void OffersOnlyANewerReleaseWithTheRightFile()
    {
        var release = AppUpdates.Parse(Release());
        Assert.NotNull(AppUpdates.Update(release, V("0.7.0"), AppUpdates.SetupAsset));
        Assert.NotNull(AppUpdates.Update(release, V("0.7.0"), AppUpdates.PortableAsset));
        Assert.Null(AppUpdates.Update(release, V("0.8.0"), AppUpdates.SetupAsset));
        Assert.Null(AppUpdates.Update(release, V("0.9.0"), AppUpdates.SetupAsset));

        var macOnly = AppUpdates.Parse(Release(assets: $$"""
            [{"name": "AIUsageBar-macOS.dmg", "size": 1, "browser_download_url": "{{Download}}/v0.8.0/AIUsageBar-macOS.dmg"}]
            """));
        Assert.Null(AppUpdates.Update(macOnly, V("0.7.0"), AppUpdates.SetupAsset));
    }

    [Fact]
    public void IgnoresDownloadsOutsideThisRepository()
    {
        var release = AppUpdates.Parse(Release(assets: """
            [
              {"name": "AIUsageBar-Windows-Setup.exe", "size": 1, "browser_download_url": "https://example.com/AIUsageBar-Windows-Setup.exe"},
              {"name": "AIUsageBar-Windows-Setup.exe", "size": 1, "browser_download_url": "http://github.com/matheusvelloso/ai-usage-bar/releases/download/v0.8.0/AIUsageBar-Windows-Setup.exe"},
              {"name": "AIUsageBar-Windows-Setup.exe", "size": 1, "browser_download_url": "https://github.com/someone/fork/releases/download/v0.8.0/AIUsageBar-Windows-Setup.exe"}
            ]
            """));
        Assert.Empty(release.Assets);
    }

    [Fact]
    public void RejectsDraftsPreReleasesAndOddTags()
    {
        Assert.Throws<UsageException>(() => AppUpdates.Parse(Release(draft: true)));
        Assert.Throws<UsageException>(() => AppUpdates.Parse(Release(prerelease: true)));
        Assert.Throws<UsageException>(() => AppUpdates.Parse(Release(tag: "nightly")));
        Assert.Throws<UsageException>(() => AppUpdates.Parse(Encoding.UTF8.GetBytes("not json")));
    }
}
