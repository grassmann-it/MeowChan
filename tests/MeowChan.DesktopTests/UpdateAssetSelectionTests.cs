using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateAssetSelectionTests
{
    private const string Release113 = "https://github.com/grassmann-it/MeowChan/releases/tag/v1.1.3";
    private const string Download113 = "https://github.com/grassmann-it/MeowChan/releases/download/v1.1.3/";
    private static readonly string[] AssetNames113 =
    [
        "01-MeowChan-1.1.3-win-x64.zip",
        "02-MeowChan-1.1.3-osx-arm64.dmg",
        "03-MeowChan-1.1.3-osx-x64.dmg",
        "04-MeowChan-1.1.3-linux-x64.AppImage",
        "05-MeowChan-1.1.3-linux-arm64.AppImage",
        "06-MeowChan-1.1.3-linux-x64-bundled-vlc.tar.gz",
        "07-MeowChan-1.1.3-linux-x64-system-vlc.tar.gz",
        "08-MeowChan-1.1.3-linux-arm64-bundled-vlc.tar.gz",
        "09-MeowChan-1.1.3-linux-arm64-system-vlc.tar.gz"
    ];

    [Theory]
    [InlineData("windows-portable-zip", "win-x64", 1)]
    [InlineData("macos-app", "osx-arm64", 2)]
    [InlineData("macos-app", "osx-x64", 3)]
    [InlineData("appimage", "linux-x64", 4)]
    [InlineData("appimage", "linux-arm64", 5)]
    [InlineData("linux-tarball-bundled-vlc", "linux-x64", 6)]
    [InlineData("linux-tarball-system-vlc", "linux-x64", 7)]
    [InlineData("linux-tarball-bundled-vlc", "linux-arm64", 8)]
    [InlineData("linux-tarball-system-vlc", "linux-arm64", 9)]
    [InlineData("winget", "win-x64", null)]
    [InlineData("homebrew", "osx-arm64", null)]
    [InlineData("aur", "linux-x64", null)]
    [InlineData("distro", "linux-x64", null)]
    [InlineData("flatpak", "linux-x64", null)]
    [InlineData("source", "linux-x64", null)]
    [InlineData("ci-fork", "linux-x64", null)]
    [InlineData("ci-acceptance", "linux-x64", null)]
    [InlineData("notify", "linux-x64", null)]
    [InlineData("none", "linux-x64", null)]
    [InlineData("appimage", "win-x64", null)]
    public async Task PublicRelease113AssetIsSelectedByChannelAndArchitecture(string channel, string rid, int? assetNumber)
    {
        object[] assets = AssetNames113.Select(name => (object)new { name, browser_download_url = Download113 + name }).ToArray();
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v1.1.3", assets: assets),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = rid, Channel = channel });

        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(Release113, result.Update?.ReleaseUri.AbsoluteUri);
        Assert.Equal(assetNumber is { } number ? Download113 + AssetNames113[number - 1] : Release113,
            result.Update?.DownloadUri.AbsoluteUri);
        Assert.Equal(result.Update, fixture.Store.Load().FoundUpdate);
    }

    [Theory]
    [InlineData("4-")]
    [InlineData("004-")]
    [InlineData("０４-")]
    public async Task OnlyTwoAsciiDigitsAreAllowedAsAnAssetPrefix(string prefix)
    {
        string name = prefix + "MeowChan-1.1.3-linux-x64.AppImage";
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v1.1.3",
            assets: [new { name, browser_download_url = Download113 + Uri.EscapeDataString(name) }]),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = "linux-x64", Channel = "appimage" });

        UpdateInfo? update = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.Equal(Release113, update?.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task TwoAssetsForTheSameChannelFallBackToTheReleasePage()
    {
        object[] assets = new[] { "04-MeowChan-1.1.3-linux-x64.AppImage", "MeowChan-1.1.3-linux-x64.AppImage" }
            .Select(name => (object)new { name, browser_download_url = Download113 + name }).ToArray();
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v1.1.3", assets: assets),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = "linux-x64", Channel = "appimage" });

        UpdateInfo? update = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.Equal(Release113, update?.DownloadUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("win-x64", "MeowChan-2.0.0-win-x64.zip")]
    [InlineData("osx-arm64", "MeowChan-2.0.0-osx-arm64.dmg")]
    [InlineData("linux-x64", "MeowChan-2.0.0-linux-x64.AppImage")]
    public async Task SingleExactOfficialRidAssetIsSelectedAndPersists(string rid, string name)
    {
        string url = "https://github.com/grassmann-it/MeowChan/releases/download/v2.0.0/" + name;
        object[] assets = [new { name, browser_download_url = url }];
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v2.0.0", assets: assets),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = rid });
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(url, result.Update?.DownloadUri.AbsoluteUri);
        Assert.Equal("https://github.com/grassmann-it/MeowChan/releases/tag/v2.0.0", result.Update?.ReleaseUri.AbsoluteUri);
        Assert.Equal(result.Update, fixture.Store.Load().FoundUpdate);
    }

    [Fact]
    public async Task CurrentDistributionRuntimeUsesPortableReleaseAssetNames()
    {
        string rid = UpdateServiceOptions.CurrentRuntimeIdentifier();
        string extension = OperatingSystem.IsWindows() ? ".zip" : OperatingSystem.IsMacOS() ? ".dmg" : ".AppImage";
        string name = "MeowChan-2.0.0-" + rid + extension;
        string url = "https://github.com/grassmann-it/MeowChan/releases/download/v2.0.0/" + name;
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v2.0.0",
            assets: [new { name, browser_download_url = url }]),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0") });
        UpdateInfo? result = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.Equal(url, result?.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task MultipleLinuxPackagesFallBackToTheOfficialReleasePage()
    {
        string[] names = ["MeowChan-2.0.0-linux-x64.AppImage", "MeowChan-2.0.0-linux-x64-bundled-vlc.tar.gz",
            "MeowChan-2.0.0-linux-x64-system-vlc.tar.gz"];
        object[] assets = names.Select(name => (object)new
            { name, browser_download_url = "https://github.com/grassmann-it/MeowChan/releases/download/2.0.0/" + name }).ToArray();
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0", assets: assets));
        UpdateInfo? result = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.NotNull(result);
        Assert.Equal(result.ReleaseUri, result.DownloadUri);
    }

    [Theory]
    [InlineData("MeowChan-2.0.0-linux-arm64.AppImage", "https://github.com/grassmann-it/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-arm64.AppImage")]
    [InlineData("Other-2.0.0-linux-x64.AppImage", "https://github.com/grassmann-it/MeowChan/releases/download/2.0.0/Other-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://evil.invalid/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "http://github.com/grassmann-it/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/other/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/grassmann-it/MeowChan/releases/download/1.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/grassmann-it/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage?tracking=1")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://user@github.com/grassmann-it/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("MeowChan-2.0.0-linux-x64.AppImage", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    public async Task UnmatchedOrUntrustedAssetsFallBackToAnOfficialReleasePage(string name, string url)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0",
            assets: [new { name, browser_download_url = url }]));
        UpdateInfo? result = (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Update;
        Assert.NotNull(result);
        Assert.Equal("https://github.com/grassmann-it/MeowChan/releases/tag/2.0.0", result.ReleaseUri.AbsoluteUri);
        Assert.Equal(result.ReleaseUri, result.DownloadUri);
    }

    [Theory]
    [InlineData("2.0.0", false)]
    [InlineData("v2.0.0", false)]
    [InlineData("2.0.0", true)]
    [InlineData("v2.0.0", true)]
    public async Task LegacyProfileDestinationsMigrateWithoutBlockingChecksOrLosingState(string tag, bool asset)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.1"));
        string release = "https://github.com/tfgrass/MeowChan/releases/tag/" + tag;
        string download = asset ? "https://github.com/tfgrass/MeowChan/releases/download/" + tag
            + "/MeowChan-2.0.0-linux-x64.AppImage" : release;
        DateTimeOffset checkedAt = fixture.Time.GetUtcNow() - TimeSpan.FromHours(25);
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, FoundVersion = "2.0.0", ReleaseUrl = release, DownloadUrl = download,
            LastCheckedUtc = checkedAt, SkippedVersion = "2.0.0"
        });
        File.WriteAllText(fixture.Store.FilePath, json);

        UpdateState loaded = fixture.Store.Load();
        Assert.Equal(checkedAt, loaded.LastCheckedUtc);
        Assert.Equal("2.0.0", loaded.SkippedVersion?.ToString());
        Assert.Equal("2.0.0", loaded.FoundUpdate?.Version.ToString());
        Assert.Equal(release.Replace("/tfgrass/", "/grassmann-it/"), loaded.FoundUpdate?.ReleaseUri.AbsoluteUri);
        Assert.Equal(download.Replace("/tfgrass/", "/grassmann-it/"), loaded.FoundUpdate?.DownloadUri.AbsoluteUri);
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));

        fixture.Store.Save(loaded);
        Assert.Equal(loaded, fixture.Store.Load());
        Assert.DoesNotContain("tfgrass", File.ReadAllText(fixture.Store.FilePath));
        // A check must also migrate the original cache through its normal save path.
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.DoesNotContain("tfgrass", File.ReadAllText(fixture.Store.FilePath));
        Assert.Equal("2.0.0", fixture.Store.Load().SkippedVersion?.ToString());
        Assert.Equal("2.0.1", fixture.Store.Load().FoundUpdate?.Version.ToString());
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.invalid/", "https://evil.invalid/")]
    [InlineData("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0", "https://github.com/grassmann-it/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("https://github.com/grassmann-it/MeowChan/releases/tag/2.0.0", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0?tracking=1", "https://github.com/tfgrass/MeowChan/releases/tag/2.0.0?tracking=1")]
    [InlineData("https://user@github.com/tfgrass/MeowChan/releases/tag/2.0.0", "https://user@github.com/tfgrass/MeowChan/releases/tag/2.0.0")]
    [InlineData("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0", "https://github.com/tfgrass/MeowChan/releases/download/1.0.0/MeowChan-2.0.0-linux-x64.AppImage")]
    [InlineData("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/Other-2.0.0-linux-x64.AppImage")]
    [InlineData("https://github.com/tfgrass/MeowChan/releases/tag/2.0.0", "https://github.com/tfgrass/MeowChan/releases/download/2.0.0/MeowChan-2.0.0-linux-x64.AppImage?tracking=1")]
    public void UnsafeDestinationsInProfileCannotBeUsedOrSilentlyReplaced(string release, string download)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("2.0.0"));
        string json = System.Text.Json.JsonSerializer.Serialize(new
            { SchemaVersion = 1, FoundVersion = "2.0.0", ReleaseUrl = release, DownloadUrl = download });
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(new UpdateState()));
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
    }
}
