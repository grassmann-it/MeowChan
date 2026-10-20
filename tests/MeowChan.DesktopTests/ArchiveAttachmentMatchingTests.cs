using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class ArchiveAttachmentMatchingTests
{
    [Fact]
    public async Task InlineAttachmentNeverSelectsAnUnrelatedVariantOrRevision()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            MediaAssetReference original = new MediaAssetReference("a", "777", "original", "a.png",
                Path.Combine(directory, "a.png"), 100, "sha", "https://example.test/a.png", "a.png", false) { Md5 = "captured" };
            ArchiveAssetRow matching = new ArchiveAssetRow(manifest, original);
            ArchiveAssetRow wrongHash = new ArchiveAssetRow(manifest, original with { AssetId = "b", Md5 = "changed", IsCurrent = true });
            ArchiveAssetRow wrongSize = new ArchiveAssetRow(manifest, original with { AssetId = "c", Length = 101 });
            ArchiveAssetRow thumbnail = new ArchiveAssetRow(manifest, original with { AssetId = "d", Variant = "thumbnail" });
            ArchiveAssetRow unrelated = new ArchiveAssetRow(manifest, original with { AssetId = "e", MediaIdentifier = "888" });
            CapturedAttachment captured = new CapturedAttachment(0, "777", ExpectedSize: 100, ExpectedMd5: "captured");
            Assert.Same(matching, ArchivePage.FindMatchingAsset(captured,
                new[] { matching, wrongHash, wrongSize, thumbnail, unrelated }));
            Assert.Null(ArchivePage.FindMatchingAsset(captured, new[] { wrongHash, wrongSize, thumbnail, unrelated }));
            Assert.Null(ArchivePage.FindMatchingAsset(captured with { MediaId = null }, new[] { matching }));
        }
        finally { Directory.Delete(directory, true); }
    }
}
