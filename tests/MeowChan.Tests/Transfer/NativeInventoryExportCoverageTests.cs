using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Repro A/B from the independent W32b4 review. Real admitted import first;
// controlled manifest edits affect only the synthetic fixture, never import authority.
public sealed class NativeInventoryExportCoverageTests
{
    private static readonly ResourceId Other = new("4chan", "b", "7002");
    private const string ManifestName = ".meowchan-manifest.json";

    [Fact]
    public async Task UnselectedBindingCannotHideBehindSelectedThreadsSharedAsset()
    {
        await using NativeV2ImportFixture f = await CreateCommittedAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = Assert.Single(Assert.IsType<ArchivedThreadView>(
            await reader.ReadThreadAsync(NativeV2ImportFixture.Id)).MediaDirectories);
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(root);
        MediaAttachmentBinding selected = Assert.Single(before.AttachmentBindings.Values);
        string otherKey = MediaRevisionKeys.CreateAttachmentKey(Other, "999", 0, selected.Provider);
        MediaAttachmentBinding unselected = selected with
        {
            Reference = new(Other, "999", 0, otherKey),
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Other, selected.Provider),
            PassiveImport = selected.PassiveImport! with { IncomingRevisionKey = otherKey }
        };
        string path = Path.Combine(root, ManifestName);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        // Binding-bearing v5: no passive evidence/derived rows to trigger the old gate.
        manifest["Version"] = 5;
        manifest.Remove("OriginalEvidence");
        manifest.Remove("DerivedRepresentations");
        manifest["AttachmentBindings"]!.AsObject().Add(otherKey, JsonSerializer.SerializeToNode(unselected));
        await File.WriteAllTextAsync(path, manifest.ToJsonString());

        MediaAssetSnapshot admitted = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(MediaManifestStatus.Available, admitted.Status);
        Assert.Equal(5, admitted.ManifestVersion);
        Assert.Empty(admitted.OriginalEvidence);
        Assert.Empty(admitted.DerivedRepresentations);
        Assert.Equal(2, admitted.AttachmentBindings.Count);
        MediaAssetReference asset = Assert.Single(admitted.GetPage());
        Assert.All(admitted.AttachmentBindings.Values, b => Assert.Equal(asset.AssetId, b.AssetId));
        Assert.NotEqual(selected.Reference.RevisionKey, otherKey);
        Assert.Equal(unselected, admitted.FindAttachmentBinding(otherKey));
        Assert.Null(await reader.ReadThreadAsync(Other));

        // If projection/verification wins before coverage, this yields InvalidMedia,
        // not UnrepresentedAssets. No hash twin may mask the unselected occurrence.
        await File.WriteAllBytesAsync(asset.Path, [0xff]);
        byte[] frozenManifest = await File.ReadAllBytesAsync(path);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            NativeV2ImportFixture.Id, NativeV2ImportFixture.At, Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.UnrepresentedAssets, result.Error);
        Assert.False(result.IsFrozen);
        Assert.Null(result.Snapshot);
        Assert.Equal(frozenManifest, await File.ReadAllBytesAsync(path));
        Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(asset.Path));
    }

    [Fact]
    public async Task SelectedResourcesEvidenceInAnotherThreadsRootCannotBeSilentlySkipped()
    {
        await using NativeV2ImportFixture f = await CreateCommittedAsync();
        string otherRoot = Directory.CreateDirectory(Path.Combine(f.Files.Root, "other-media")).FullName;
        await f.Catalog.CaptureAsync(new CapturedThreadSnapshot(Other,
            "https://boards.4chan.org/b/thread/7002", "synthetic second thread", ThreadSourceState.NotFound,
            NativeV2ImportFixture.At, otherRoot,
            [new("7002", 0, PostFieldPresence.Html, Html: "synthetic second post")]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = Assert.Single(Assert.IsType<ArchivedThreadView>(
            await reader.ReadThreadAsync(NativeV2ImportFixture.Id)).MediaDirectories);
        Assert.Equal(otherRoot, Assert.Single(Assert.IsType<ArchivedThreadView>(
            await reader.ReadThreadAsync(Other)).MediaDirectories));
        Assert.NotEqual(root, otherRoot);
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(root);
        MediaOriginalEvidence current = Assert.Single(before.OriginalEvidence);
        MediaProviderMetadata provider = Assert.Single(before.AttachmentBindings.Values).Provider;
        string key = MediaRevisionKeys.CreateAttachmentKey(Other, "999", 0, provider);
        MediaOccurrence occurrence = new(Other, "999", 0, key);
        MediaOriginalEvidence misplaced = new(occurrence,
            current.Metadata with { AttachmentRevisionKey = key }, MediaObservedContentStatus.Unavailable,
            current.OriginalByteVersion, current.Origin);
        MediaDerivedRepresentation preview = new(occurrence, current.OriginalByteVersion!, MediaDerivedKind.Preview,
            MediaDerivedStatus.Unavailable, null, current.Origin);
        string path = Path.Combine(root, ManifestName);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(new[] { current, misplaced });
        manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(new[] { preview });
        await File.WriteAllTextAsync(path, manifest.ToJsonString());

        MediaAssetSnapshot admitted = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(MediaManifestStatus.Available, admitted.Status);
        Assert.Equal(2, admitted.OriginalEvidence.Length);
        Assert.Contains(misplaced, admitted.OriginalEvidence);
        Assert.Equal(preview, Assert.Single(admitted.DerivedRepresentations));
        Assert.Null(admitted.FindAttachmentBinding(key)); // Absence-only evidence has no asset coverage entry.
        Assert.Null(preview.Blob);
        MediaAssetSnapshot other = await MediaAssetReader.OpenAsync(otherRoot);
        Assert.Equal(MediaManifestStatus.Missing, other.Status);
        Assert.Empty(other.OriginalEvidence);
        Assert.Empty(other.DerivedRepresentations);

        MediaAssetReference asset = Assert.Single(admitted.GetPage());
        await File.WriteAllBytesAsync(asset.Path, [0xff]);
        byte[] frozenManifest = await File.ReadAllBytesAsync(path);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([NativeV2ImportFixture.Id, Other]), NativeV2ImportFixture.At,
            Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.UnrepresentedAssets, result.Error);
        Assert.False(result.IsFrozen);
        Assert.Null(result.Snapshot);
        Assert.Equal(frozenManifest, await File.ReadAllBytesAsync(path));
        Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(asset.Path));
        Assert.Empty(Directory.GetFiles(otherRoot));
    }

    private static async Task<NativeV2ImportFixture> CreateCommittedAsync()
    {
        NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(derived: false, presence: false), [NativeV2ImportFixture.Original]);
        try
        {
            using ProfileAdmissionToken admission = await f.ExclusiveAsync();
            var outcome = await f.CommitAsync(admission);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            await f.Stage.DisposeAsync();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
}
