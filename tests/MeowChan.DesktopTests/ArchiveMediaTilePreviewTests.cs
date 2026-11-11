using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Saved-media tiles route videos to the reader's local poster and everything
// else to the image decoder; the original name wins over the stored path.
public sealed class ArchiveMediaTilePreviewTests
{
    private static ArchiveAssetRow Row(string? original, string storedFile)
    {
        string directory = Path.Combine(Path.GetTempPath(), "saved-media-tile-fixture");
        string sha = new('a', 64);
        var asset = new MediaAssetReference("asset-" + storedFile, storedFile, "original", storedFile, Path.Combine(directory, storedFile),
            3, sha, "https://offline.invalid/" + storedFile, OriginalFileName: original, IsCurrent: true);
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new ArchiveAssetRow(snapshot, asset);
    }

    [Theory]
    [InlineData("clip.webm", "0d614e68.webm", true)]
    [InlineData("clip.mp4", "stored.bin", true)]
    [InlineData("still.png", "still.png", false)]
    [InlineData("animation.gif", "animation.gif", false)]
    [InlineData(null, "creep_209717.webm", true)]
    public void VideosUseThePosterAndOtherFilesTheImageDecoder(string? original, string stored, bool poster) =>
        Assert.Equal(poster, ArchiveMediaTileView.UsesVideoPoster(Row(original, stored)));

    // The tile has no path line, so its fallback must not point at one (UX103 tile text).
    [Theory]
    [InlineData("clip.webm", "0d614e68.webm", "archive.tileVideoPosterUnavailable")]
    [InlineData("notes.pdf", "notes.pdf", "archive.tilePreviewUnavailable")]
    public void MissingTilePreviewNamesOnlyWhatIsMissing(string? original, string stored, string key) =>
        Assert.Equal(key, ArchiveMediaTileView.UnavailableKey(Row(original, stored)));
}

[Collection("Localization")]
public sealed class ArchiveMediaTileTextTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public ArchiveMediaTileTextTests() => Texts.Initialize();

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Theory]
    [InlineData("en"), InlineData("de"), InlineData("es"), InlineData("fr"), InlineData("ru")]
    public void TileFallbacksAreTranslatedAndDoNotMentionAPathBelow(string language)
    {
        Texts.SetLanguage(language);
        string path = Texts.T("archive.previewUnavailable");
        foreach (string key in new[] { "archive.tileVideoPosterUnavailable", "archive.tilePreviewUnavailable" })
        {
            string text = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
            Assert.NotEqual(path, text);
        }
        if (language != "en")
        {
            Texts.SetLanguage("en");
            string english = Texts.T("archive.tileVideoPosterUnavailable");
            Texts.SetLanguage(language);
            Assert.NotEqual(english, Texts.T("archive.tileVideoPosterUnavailable"));
        }
    }
}
