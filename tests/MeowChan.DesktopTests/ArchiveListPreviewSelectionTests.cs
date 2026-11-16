using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>The thread-list row shows the opening post's first saved medium (attachment position order).</summary>
public sealed class ArchiveListPreviewSelectionTests
{
    private static readonly ResourceId ThreadId = new("endchan", "porn", "532");

    private static CapturedAttachment Attachment(int position, string name, bool deleted = false) =>
        new(position, "m" + position, "original", "https://fixture.invalid/" + name, name, null, null, null, deleted);

    // One legacy manifest holding the named files; an attachment matches by media identity, variant and source URL.
    private static async Task<MediaAssetSnapshot> ManifestAsync(string directory, params (int Position, string Name)[] saved)
    {
        Dictionary<string, object> entries = new();
        foreach ((int position, string name) in saved)
        {
            byte[] bytes = [1, 2, 3, (byte)position];
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
            entries[name] = new
            {
                MediaIdentifier = "m" + position, Variant = "original", SourceUrl = "https://fixture.invalid/" + name, FileName = name,
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes)), OriginalFileName = name
            };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        return await MediaAssetReader.OpenAsync(directory);
    }

    [Fact]
    public async Task TheFirstMediumByPositionIsShownWhenSeveralAreSaved()
    {
        using TempDirectory temp = new();
        MediaAssetSnapshot manifest = await ManifestAsync(temp.Path, (0, "IMG_4328.jpeg"), (1, "IMG_4327.jpeg"), (2, "IMG_4332.jpeg"));
        // Handed over out of order: the position decides, not the list order.
        CapturedAttachment[] candidates = [Attachment(2, "IMG_4332.jpeg"), Attachment(0, "IMG_4328.jpeg"), Attachment(1, "IMG_4327.jpeg")];

        var chosen = ArchivePage.SelectListPreview([manifest], ThreadId, "532", candidates);

        Assert.NotNull(chosen);
        Assert.Equal(0, chosen.Value.Attachment.Position);
        Assert.Equal("IMG_4328.jpeg", chosen.Value.Resolved.Asset.OriginalFileName);
    }

    [Fact]
    public async Task AFirstMediumThatIsNotSavedGivesWayToTheFirstSavedOne()
    {
        using TempDirectory temp = new();
        MediaAssetSnapshot manifest = await ManifestAsync(temp.Path, (1, "IMG_4327.jpeg"), (2, "IMG_4332.jpeg"));
        CapturedAttachment[] candidates = [Attachment(0, "IMG_4328.jpeg"), Attachment(1, "IMG_4327.jpeg"), Attachment(2, "IMG_4332.jpeg")];

        var chosen = ArchivePage.SelectListPreview([manifest], ThreadId, "532", candidates);

        Assert.NotNull(chosen);
        Assert.Equal(1, chosen.Value.Attachment.Position);
        Assert.Equal("IMG_4327.jpeg", chosen.Value.Resolved.Asset.OriginalFileName);
    }

    [Fact]
    public async Task ADeletedFirstMarkerIsSkipped()
    {
        using TempDirectory temp = new();
        MediaAssetSnapshot manifest = await ManifestAsync(temp.Path, (0, "IMG_4328.jpeg"), (1, "IMG_4327.jpeg"));
        CapturedAttachment[] candidates = [Attachment(0, "IMG_4328.jpeg", deleted: true), Attachment(1, "IMG_4327.jpeg")];

        Assert.Equal(1, ArchivePage.SelectListPreview([manifest], ThreadId, "532", candidates)!.Value.Attachment.Position);
    }

    [Fact]
    public async Task APreviewableMediumIsPreferredOverAnEarlierSavedFileThatCannotBeDrawn()
    {
        using TempDirectory temp = new();
        MediaAssetSnapshot manifest = await ManifestAsync(temp.Path, (0, "notes.txt"), (1, "IMG_4327.jpeg"));
        CapturedAttachment[] candidates = [Attachment(0, "notes.txt"), Attachment(1, "IMG_4327.jpeg")];

        Assert.Equal(1, ArchivePage.SelectListPreview([manifest], ThreadId, "532", candidates)!.Value.Attachment.Position);

        // Nothing previewable at all: the first saved one is still the row's medium (it just has no picture).
        Assert.Equal(0, ArchivePage.SelectListPreview([manifest], ThreadId, "532", [Attachment(0, "notes.txt")])!.Value.Attachment.Position);
    }

    [Fact]
    public async Task OneMediumBehavesAsBeforeSavedIsShownUnsavedIsNothing()
    {
        using TempDirectory temp = new();
        MediaAssetSnapshot saved = await ManifestAsync(temp.Path, (0, "one.png"));
        Assert.Equal(0, ArchivePage.SelectListPreview([saved], ThreadId, "532", [Attachment(0, "one.png")])!.Value.Attachment.Position);

        using TempDirectory other = new();
        MediaAssetSnapshot empty = await ManifestAsync(other.Path);
        Assert.Null(ArchivePage.SelectListPreview([empty], ThreadId, "532", [Attachment(0, "one.png")]));
        Assert.Null(ArchivePage.SelectListPreview([saved], ThreadId, "532", [Attachment(0, "one.png", deleted: true)]));
    }

    [Fact]
    public void NoMediaMeansNoPreview()
    {
        Assert.Null(ArchivePage.SelectListPreview([], ThreadId, "532", []));
    }

    [Fact]
    public void ARowFollowsTheMediumItActuallyShowsAndTheNoteDoesNotHideThePicture()
    {
        ArchiveAttachmentRow row = new(Attachment(0, "IMG_4328.jpeg"));
        Assert.StartsWith("IMG_4328.jpeg", row.FileLine);
        row.ShownAttachment = Attachment(1, "IMG_4327.jpeg");
        Assert.StartsWith("IMG_4327.jpeg", row.FileLine);
        Assert.StartsWith("IMG_4327.jpeg", row.Label);
        Assert.Equal(0, row.Attachment.Position); // the identity of the row stays the first medium
    }

    [Fact]
    public void TheNoteUnderTheThumbnailIsOnlyTextAndNeverDecidesWhetherTheImageIsDrawn()
    {
        string view = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop/ArchiveAttachmentView.cs"));
        // The image visibility is set from the decoded bitmap alone; the provenance line has its own visibility.
        Assert.Contains("_image.IsVisible = bitmap != null;", view);
        Assert.Contains("_provenance.IsVisible = !string.IsNullOrEmpty(_provenance.Text);", view);
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
