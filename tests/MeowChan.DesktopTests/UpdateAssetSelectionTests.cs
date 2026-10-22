using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateAssetSelectionTests
{
    [Theory]
    [InlineData("win-x64", "MeowChan-2.0.0-win-x64.zip")]
    [InlineData("osx-arm64", "MeowChan-2.0.0-osx-arm64.dmg")]
    [InlineData("linux-x64", "MeowChan-2.0.0-linux-x64.AppImage")]
    public async Task SingleExactOfficialRidAssetIsSelectedAndPersists(string rid, string name)
    {
        string url = "https://github.com/tfgrass/MeowChan/releases/download/v2.0.0/" + name;
        object[] assets = [new { name, browser_download_url = url }];
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v2.0.0", assets: assets),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = rid });
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(url, result.Update?.DownloadUri.AbsoluteUri);
        Assert.Equal("https://github.com/tfgrass/MeowChan/releases/tag/v2.0.0", result.Update?.ReleaseUri.AbsoluteUri);
        Assert.Equal(result.Update, fixture.Store.Load().FoundUpdate);
    }

    [Fact]
    public async Task MultipleLinuxPackagesFallBackToTheOfficialReleasePage()
    {
        string[] names = ["MeowChan-2.0.0-linux-x64.AppImage", "MeowChan-2.0.0-linux-x64-bundled-vlc.tar.gz",
            "MeowChan-2.0.0-linux-x64-system-vlc.tar.gz"];
        object[] assets = names.Select(name => (object)new
            { name, browser_download_url = "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/" + name }).ToArray();
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0", assets: assets));
        UpdateInfo? result = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.NotNull(result);
        Assert.Equal(result.ReleaseUri, result.DownloadUri);
    }

    [Theory]
    [InlineData("MeowChan-2.0.0-linux-arm64.AppImage", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-arm64.AppImage")]
    [InlineData("Other-2.0.0-linux-x64.AppImage", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/Other-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://evil.invalid/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "http://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/other/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/tfgrass/MeowChan/releases/download/1.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage?tracking=1")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://user@github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    public async Task UnmatchedOrUntrustedAssetsFallBackToAnOfficialReleasePage(string name, string url)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0",
            assets: [new { name, browser_download_url = url }]));
        UpdateInfo? result = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.NotNull(result);
        Assert.Equal("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0", result.ReleaseUri.AbsoluteUri);
        Assert.Equal(result.ReleaseUri, result.DownloadUri);
    }

    [Fact]
    public void UnsafeDestinationsInProfileCannotBeUsedOrSilentlyReplaced()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0"));
        string json = "{\"SchemaVersion\":1,\"FoundVersion\":\"2.0.0\",\"ReleaseUrl\":\"https://evil.invalid/\",\"DownloadUrl\":\"https://evil.invalid/\"}";
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(new UpdateState()));
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
    }
}
