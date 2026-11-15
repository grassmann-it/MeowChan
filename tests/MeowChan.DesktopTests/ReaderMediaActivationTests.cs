using System.Security.Cryptography;
using System.Text;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class ReaderMediaActivationTests
{
    private static readonly ResourceId Thread = new("fixture", "g", "42");

    [Theory]
    [InlineData("image.png")]
    [InlineData("clip.webm")]
    [InlineData("clip.mp4")]
    public void LaterPostSelectsItsExactAssetAndAssociationRatherThanFirstThreadAsset(string file)
    {
        var first = Bound("first", 0, "first.png");
        var later = Bound("later", 3, file);
        var rows = Assets(first, later);
        var target = ArchivePage.FindReaderMediaTarget(Thread, Post("later", later.Attachment), rows);
        Assert.NotNull(target);
        Assert.Equal(later.Asset.AssetId, target.Key.Asset.AssetId);
        Assert.Equal(later.Snapshot.Directory, target.Key.Asset.Directory);
        Assert.Equal(Thread, target.Key.Asset.Thread);
        Assert.Equal("later", target.Key.PostId);
        Assert.Equal(3, target.Key.AttachmentPosition);
    }

    [Fact]
    public void SharedAssetKeepsSelectedPostAndPositionRatherThanFirstAssociation()
    {
        var first = Bound("first", 0, "shared.png");
        var later = Bound("later", 7, "shared.png");
        var rows = Assets(first, later);
        var target = ArchivePage.FindReaderMediaTarget(Thread, Post("later", later.Attachment), rows);
        Assert.NotNull(target);
        Assert.Equal(first.Asset.AssetId, target.Key.Asset.AssetId);
        Assert.Equal("later", target.Key.PostId);
        Assert.Equal(7, target.Key.AttachmentPosition);
    }

    [Fact]
    public void AttachmentPositionOrderSkipsDeletedUnmappedAndNonVisualAttachments()
    {
        var other = Bound("later", 0, "document.pdf");
        var deleted = Bound("later", 1, "deleted.png");
        var video = Bound("later", 5, "chosen.webm");
        var image = Bound("later", 9, "later.png");
        var missing = new CapturedAttachment(3, "missing", Url: "https://offline.invalid/missing.png");
        var selected = Post("later", image.Attachment, video.Attachment, missing,
            deleted.Attachment with { IsDeleted = true }, other.Attachment);
        var target = ArchivePage.FindReaderMediaTarget(Thread, selected, Assets(other, deleted, video, image));
        Assert.NotNull(target);
        Assert.Equal(video.Asset.AssetId, target.Key.Asset.AssetId);
        Assert.Equal(5, target.Key.AttachmentPosition);
    }

    [Fact]
    public void MissingFirstOriginalUsesNextAvailableAssociationInTheSamePostOnly()
    {
        var unrelated = Bound("first", 0, "unrelated.png");
        var missing = Bound("later", 0, "gone.png");
        var available = Bound("later", 2, "available.webm");
        var selected = Post("later", missing.Attachment, available.Attachment);
        var rows = Assets(unrelated, missing, available);
        var target = ArchivePage.FindReaderMediaTarget(Thread, selected, rows,
            row => row.Asset.AssetId == available.Asset.AssetId);
        Assert.Equal(available.Asset.AssetId, target!.Key.Asset.AssetId);
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, selected, rows,
            row => row.Asset.AssetId == unrelated.Asset.AssetId));
    }

    [Fact]
    public void PublishedManifestAloneDoesNotProveTheOriginalStillExists()
    {
        string directory = Path.Combine(Path.GetTempPath(), "reader-media-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var gone = Bound("later", 0, "gone.png");
            var saved = Bound("later", 1, "saved.png");
            gone = gone with { Asset = gone.Asset with { Path = Path.Combine(directory, "gone.png") } };
            saved = saved with { Asset = saved.Asset with { Path = Path.Combine(directory, "saved.png") } };
            File.WriteAllBytes(saved.Asset.Path, [1, 2, 3]);
            var rows = Assets(gone, saved);
            var post = Post("later", gone.Attachment, saved.Attachment);
            Assert.Equal(saved.Asset.AssetId, ArchivePage.FindReaderMediaTarget(Thread, post, rows,
                row => File.Exists(row.Asset.Path))!.Key.Asset.AssetId);
            File.Delete(saved.Asset.Path);
            Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, post, rows, row => File.Exists(row.Asset.Path)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExactHistoricalBindingRemainsEligibleWithoutSelectingTheNewCurrentAsset()
    {
        var historical = Bound("later", 4, "old.png");
        historical = historical with { Asset = historical.Asset with { IsCurrent = false } };
        var current = Bound("first", 0, "new.png");
        var target = ArchivePage.FindReaderMediaTarget(Thread, Post("later", historical.Attachment), Assets(current, historical));
        Assert.Equal(historical.Asset.AssetId, target!.Key.Asset.AssetId);
        Assert.False(target.Row.Asset.IsCurrent);
    }

    [Fact]
    public void NoSelectionNoAttachmentsAndUnmappedPostNeverFallBackToThreadMedia()
    {
        var other = Bound("first", 0, "first.png");
        var rows = Assets(other);
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, null, rows));
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, Post("later"), rows));
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, Post("later", other.Attachment), rows));
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread with { Thread = "foreign" },
            Post("first", other.Attachment), rows));
    }

    [Theory]
    [InlineData(MediaManifestStatus.Missing)]
    [InlineData(MediaManifestStatus.Corrupt)]
    public void MissingOrCorruptManifestCannotOpenAnEmptyGallery(MediaManifestStatus status)
    {
        var bound = Bound("later", 0, "saved.png");
        var snapshot = new MediaAssetSnapshot(bound.Snapshot.Directory, status, 3, "invalid", [bound.Asset]);
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, Post("later", bound.Attachment),
            [new(snapshot, bound.Asset)]));
    }

    [Theory]
    [InlineData("clip.ogg")]
    [InlineData("archive.zip")]
    [InlineData("unknown.bin")]
    public void OnlyAvailableImagesAndVideosAreEligible(string file)
    {
        var bound = Bound("later", 0, file);
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, Post("later", bound.Attachment), Assets(bound)));
    }

    [Fact]
    public void StaleRevisionCannotDriftToTheCurrentAsset()
    {
        var bound = Bound("later", 0, "saved.png");
        var selected = Post("later", bound.Attachment with { Url = bound.Attachment.Url + "?changed" });
        Assert.Null(ArchivePage.FindReaderMediaTarget(Thread, selected, Assets(bound)));
    }

    // Space is the fixed content key (and the video's play / pause); Enter is the command "Open medium of the post", so its gesture is read from the bindings.
    [Fact]
    public void FixedContentGestureIsUnmodifiedOnlyAndPreservesEffectiveReaderAndShellRemaps()
    {
        Key key = Key.Space;
        var e = new KeyEventArgs { Key = key };
        var defaults = EffectiveShortcutBindings.Build();
        Assert.True(ArchivePage.IsReaderMediaGesture(e, defaults)); // thread.read is an archive action.
        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Meta, KeyModifiers.Shift, KeyModifiers.Alt })
            Assert.False(ArchivePage.IsReaderMediaGesture(new() { Key = key, KeyModifiers = modifier }, defaults));
        var bindings = new ShortcutBindingsSection().Set("thread.read", Array.Empty<ShortcutGesture>())
            .Set("reader.next-post", [ShortcutGesture.Parse(key.ToString())]);
        Assert.False(ArchivePage.IsReaderMediaGesture(e, EffectiveShortcutBindings.Build(bindings)));
        var shell = new ShortcutBindingsSection();
        // Shell bindings overlap all nonmodal surfaces, so explicitly remove
        // conflicting defaults from this draft rather than testing a rejected remap.
        foreach (var command in CommandCatalog.All.Where(command => defaults.Matches(e, command.Id)))
            shell = shell.Set(command.Id, Array.Empty<ShortcutGesture>());
        shell = shell.Set("window.fullscreen", [ShortcutGesture.Parse(key.ToString())]);
        var shellBindings = EffectiveShortcutBindings.Build(shell);
        Assert.True(shellBindings.Matches(e, "window.fullscreen"));
        Assert.False(ArchivePage.IsReaderMediaGesture(e, shellBindings));
        // An unrelated gallery transport binding must not erase reader activation.
        Assert.True(ArchivePage.IsReaderMediaGesture(e, EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("gallery.next", [ShortcutGesture.Parse(key.ToString())]))));
    }

    // Enter is only the command "Open medium of the post": the fixed content path knows Space alone, so rebinding or unbinding the command takes Enter away.
    [Fact]
    public void EnterIsNeverAFixedContentGestureSoUnbindingTheCommandLeavesItDead()
    {
        KeyEventArgs enter = new() { Key = Key.Enter };
        Assert.False(ArchivePage.IsReaderMediaGesture(enter, EffectiveShortcutBindings.Build()));
        EffectiveShortcutBindings unbound = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(ArchivePage.ActivateMediumCommandId, Array.Empty<ShortcutGesture>()));
        Assert.False(unbound.Matches(enter, ArchivePage.ActivateMediumCommandId));
        Assert.False(ArchivePage.IsReaderMediaGesture(enter, unbound));
        EffectiveShortcutBindings moved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(ArchivePage.ActivateMediumCommandId, [ShortcutGesture.Parse("F2")]));
        Assert.False(ArchivePage.IsReaderMediaGesture(enter, moved));
        Assert.True(ArchivePage.IsReaderMediaGesture(new() { Key = Key.Space }, unbound)); // Space keeps its fixed meaning
    }

    [Fact]
    public void AKeyChosenMediumOpensExactlyThatAttachmentNotAnotherOneOfThePost()
    {
        var first = Bound("later", 0, "first.png");
        var second = Bound("later", 1, "second.webm");
        var rows = Assets(first, second);
        ArchivePostRow post = Post("later", first.Attachment, second.Attachment);
        ArchiveAttachmentRow chosen = post.Attachments.Single(row => row.Attachment.Position == 1);
        var target = ArchivePage.FindReaderMediumTarget(Thread, post, chosen, rows);
        Assert.NotNull(target);
        Assert.Equal(second.Asset.AssetId, target.Key.Asset.AssetId);
        Assert.Equal(1, target.Key.AttachmentPosition);
        Assert.Equal("later", target.Key.PostId);
        Assert.Null(ArchivePage.FindReaderMediumTarget(Thread, post, chosen, rows, _ => false)); // the saved file is gone: nothing opens, no other medium takes its place
        Assert.Null(ArchivePage.FindReaderMediumTarget(Thread, post, chosen, Assets(first)));    // not in the manifest
    }

    [Fact]
    public void TheKeyPathOpensTheGalleryUnderTheReadersGuardNotAfterABareManifestWait()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../..", "Desktop/Views/Archive/ArchivePage.MediaActivation.cs"));
        string source = File.ReadAllText(path);
        Assert.DoesNotContain("OpenMediumInGalleryAsync", source);
        Assert.Contains("_readerMediaActivationCancellation = cancellation;", source);   // a repeat or a second key does nothing while it runs
        Assert.Contains("ActivateReaderMediaAsync(origin, post, _thread.Summary.Id, _mediaReady.Task, cancellation, row)", source); // the same Current() recheck after every await
        Assert.DoesNotContain("ReaderMediaActivationTask = Open", source);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void BothMediaKeysGiveWayToASavedReaderOrShellBindingOnTheSameGesture(Key key)
    {
        var e = new KeyEventArgs { Key = key };
        var defaults = EffectiveShortcutBindings.Build();
        Assert.False(ArchivePage.ClaimedByOtherReaderCommand(e, defaults));
        // The command "Open medium of the post" has Enter by default and no other gesture.
        Assert.Equal(key == Key.Enter, defaults.Matches(e, ArchivePage.ActivateMediumCommandId));
        Assert.True(ReaderMediaActivation.IsMediaKey(key, KeyModifiers.None, defaults.Matches(e, ArchivePage.ActivateMediumCommandId), claimedByOtherCommand: false));
        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Meta, KeyModifiers.Shift, KeyModifiers.Alt })
            Assert.False(ReaderMediaActivation.IsMediaKey(key, modifier, false, false)); // a modifier never activates a medium
        var reader = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("thread.read", Array.Empty<ShortcutGesture>())
            .Set("reader.next-post", [ShortcutGesture.Parse(key.ToString())]));
        Assert.True(ArchivePage.ClaimedByOtherReaderCommand(e, reader));
        Assert.False(ReaderMediaActivation.IsMediaKey(key, KeyModifiers.None, reader.Matches(e, ArchivePage.ActivateMediumCommandId), claimedByOtherCommand: true));
        // An unrelated gallery binding changes nothing.
        Assert.False(ArchivePage.ClaimedByOtherReaderCommand(e, EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("gallery.next", [ShortcutGesture.Parse(key.ToString())]))));
    }

    private sealed record BoundAsset(MediaAssetSnapshot Snapshot, MediaAssetReference Asset, CapturedAttachment Attachment);

    private static BoundAsset Bound(string post, int position, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "reader-media-fixture");
        var provider = new MediaProviderMetadata(file, "original", "https://offline.invalid/" + file, file, 3, null, null);
        var actual = new MediaActualIntegrity(3, Convert.ToBase64String(MD5.HashData(new byte[] { 1, 2, 3 })),
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant());
        string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file))).ToLowerInvariant();
        var asset = new MediaAssetReference(assetId, file, "original", file, Path.Combine(directory, file),
            3, actual.Sha256, provider.Url, file, true) { Md5 = actual.Md5 };
        string revision = MediaRevisionKeys.CreateAttachmentKey(Thread, post, position, provider);
        var binding = new MediaAttachmentBinding(new(Thread, post, position, revision),
            MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), assetId, provider, actual,
            new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch, "fixture", "owner", Thread.Source, 1));
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [revision] = binding });
        var attachment = new CapturedAttachment(position, file, "original", provider.Url, file, ExpectedSize: 3)
        { AttachmentRevisionKey = revision };
        return new(snapshot, asset, attachment);
    }

    private static IReadOnlyList<ArchiveAssetRow> Assets(params BoundAsset[] assets)
    {
        // Shared assets have multiple bindings in ONE snapshot, as in real manifests.
        var snapshot = new MediaAssetSnapshot(assets[0].Snapshot.Directory, MediaManifestStatus.Available, 3, "fixture",
            assets.Select(row => row.Asset).DistinctBy(asset => asset.AssetId),
            attachmentBindings: assets.SelectMany(row => row.Snapshot.AttachmentBindings).ToDictionary(pair => pair.Key, pair => pair.Value));
        return snapshot.GetPage().Select(asset => new ArchiveAssetRow(snapshot, asset)).ToArray();
    }

    private static ArchivePostRow Post(string id, params CapturedAttachment[] attachments) =>
        new(id, string.Empty, "body", attachments.Select(attachment => new ArchiveAttachmentRow(attachment)).ToArray());
}
