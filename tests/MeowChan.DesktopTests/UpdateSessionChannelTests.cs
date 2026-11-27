using System.Text.Json;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateSessionChannelTests
{
    [Theory]
    [InlineData("tfgrass", "source", false)]
    [InlineData("grassmann-it", "source", false)]
    [InlineData("grassmann-it", "aur", false)]
    [InlineData("tfgrass", null, true)]
    [InlineData("grassmann-it", null, true)]
    public async Task CachedDownloadIsRoutedForTheCurrentChannelWithoutRewritingTheProfile(string owner,
        string? channel, bool keepAsset)
    {
        using TempDirectory profile = new();
        string rid = UpdateServiceOptions.CurrentRuntimeIdentifier();
        (string platformChannel, string suffix) = rid switch
        {
            "win-x64" or "win-arm64" => ("windows-portable-zip", ".zip"),
            "osx-x64" or "osx-arm64" => ("macos-app", ".dmg"),
            _ => ("appimage", ".AppImage")
        };
        string current = "https://github.com/grassmann-it/MeowChan/releases/";
        string original = $"https://github.com/{owner}/MeowChan/releases/";
        string release = "tag/v1.1.3";
        string asset = $"download/v1.1.3/MeowChan-1.1.3-{rid}{suffix}";
        DateTimeOffset checkedAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        string json = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, LastCheckedUtc = checkedAt, FoundVersion = "1.1.3", ReleaseUrl = original + release,
            DownloadUrl = original + asset, SkippedVersion = "1.1.2"
        });
        UpdateStateStore store = new(profile.Path);
        File.WriteAllText(store.FilePath, json);
        using UpdateServiceTests.FakeHandler handler = new();
        using HttpClient client = new(handler, disposeHandler: false);
        await using UpdateSession session = new(profile.Path, new Config(), client, new UpdateServiceTests.FakeTime(),
            disableEnvironment: () => null, currentVersion: SemanticVersion.Parse("1.1.0"), channel: channel ?? platformChannel);

        session.Start();
        Assert.Equal(current + release, session.View.Update?.ReleaseUri.AbsoluteUri);
        Assert.Equal(current + (keepAsset ? asset : release), session.View.Update?.DownloadUri.AbsoluteUri);
        Assert.Equal(checkedAt, session.View.LastCheckedUtc);
        Assert.Equal("1.1.2", store.Load().SkippedVersion?.ToString());
        Assert.Equal(json, File.ReadAllText(store.FilePath));
        Assert.Equal(0, handler.Requests);
    }
}
