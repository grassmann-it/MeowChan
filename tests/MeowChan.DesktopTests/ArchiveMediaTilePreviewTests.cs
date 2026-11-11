using MeowChan.Desktop;
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
}
