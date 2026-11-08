using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic manifest6 wire is not a journalled v4 import. These tests keep the
// original-only consumers closed until their complete preservation path exists.
public sealed class NativeV6ConsumerBoundaryTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private const string LockName = ".meowchan-store.lock";
    private const string PostId = "7001";
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 7, 13, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("0cf38322-b0c4-471f-85a5-149dd83f88e6");
    private static readonly MediaPassiveObservation Origin = new("75e58680-9c27-490f-a376-06eae3132f66",
        new string('b', 64), At);
    private static readonly byte[] Bytes = [17, 19, 23, 29, 31];

    [Theory]
    [InlineData(0)] // Even an empty v6 must not be silently downlevelled.
    [InlineData(1)] // Evidence only, no payload at all.
    [InlineData(2)] // Evidence plus a contained preview, no Original asset.
    public async Task OriginalOnlyExporterRejectsAvailableV6WithoutDiscardingItsInventory(int inventory)
    {
        using TempDirectory profile = new();
        string root = Path.Combine(profile.Path, "media");
        await WriteInventoryAsync(root, inventory);
        await CaptureAsync(profile.Path, root);
        MediaAssetSnapshot media = await AssertAvailableV6Async(root, inventory);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Dictionary<string, string> filesBefore = MediaFiles(root);

        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(
            reader, Id, At, PackageId, includeOriginals: true);

        Assert.False(result.IsFrozen);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(profile.Path, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(filesBefore, MediaFiles(root));
        Assert.Equal(media.Generation, (await MediaAssetReader.OpenAsync(root)).Generation);
        Assert.Equal(before.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
        AssertOriginalUnavailable(media, Attachment());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MergeTargetAcquisitionRejectsAvailableV6BeforeCallbackAndReleasesExistingLease(int inventory)
    {
        using TempDirectory directory = new();
        await WriteInventoryAsync(directory.Path, inventory);
        MediaAssetSnapshot before = await AssertAvailableV6Async(directory.Path, inventory);
        Dictionary<string, string> filesBefore = MediaFiles(directory.Path);
        bool entered = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().WithPassiveMergeTargetRootsAsync(
            new[] { directory.Path }, snapshots =>
            {
                entered = true;
                return Task.FromResult(snapshots.Length);
            }));

        Assert.False(entered);
        Assert.Equal(filesBefore, MediaFiles(directory.Path));
        Assert.Equal(before.Generation, (await MediaAssetReader.OpenAsync(directory.Path)).Generation);
        AssertLeaseReleased(directory.Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task V6ConsumerGatesPrecedeOpeningEvenRealOriginalPayloads(int inventory)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        await WriteInventoryAsync(fixture.MediaDirectory, inventory);
        MediaAssetSnapshot snapshot = await AssertAvailableV6Async(fixture.MediaDirectory, inventory);
        Assert.Equal(2, snapshot.Count); // Real existing Original assets are not removed by the fixture.
        Assert.NotNull(MediaAttachmentResolver.Resolve(snapshot, fixture.ThreadId,
            NativeTransferArchiveFixture.StrictPostId, fixture.StrictAttachment));
        Dictionary<string, string> filesBefore = MediaFiles(fixture.MediaDirectory);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        bool entered = false;

        // FileShare.None is an IO sentinel: any original verification/open would
        // yield IOException, not the required explicit InvalidMedia/InvalidData gate.
        using (FileStream original = new(fixture.StrictCommit.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using FileStream? preview = inventory == 2
                ? new FileStream(DerivedPath(fixture.MediaDirectory), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : null;
            TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(
                reader, fixture.ThreadId, At, PackageId, includeOriginals: true);
            Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
            Assert.Null(result.Snapshot);
            await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().WithPassiveMergeTargetRootsAsync(
                new[] { fixture.MediaDirectory }, _ =>
                {
                    entered = true;
                    return Task.FromResult(true);
                }));
        }

        Assert.False(entered);
        Assert.Equal(filesBefore, MediaFiles(fixture.MediaDirectory));
        AssertLeaseReleased(fixture.MediaDirectory);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task V6InLaterRootRejectsBeforeEarlierV4RootPayloadVerification(int inventory)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        string v6Root = fixture.MediaDirectory + "-z-v6";
        await WriteInventoryAsync(v6Root, inventory);
        await AssertAvailableV6Async(v6Root, inventory);
        Assert.Equal(4, (await MediaAssetReader.OpenAsync(fixture.MediaDirectory)).ManifestVersion);
        Assert.True(StringComparer.Ordinal.Compare(fixture.MediaDirectory, v6Root) < 0);
        Dictionary<string, string> v4Before = MediaFiles(fixture.MediaDirectory);
        Dictionary<string, string> v6Before = MediaFiles(v6Root);
        bool entered = false;

        using (FileStream original = new(fixture.StrictCommit.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().WithPassiveMergeTargetRootsAsync(
                new[] { v6Root, fixture.MediaDirectory }, _ =>
                {
                    entered = true;
                    return Task.FromResult(true);
                }));

        Assert.False(entered);
        Assert.Equal(v4Before, MediaFiles(fixture.MediaDirectory));
        Assert.Equal(v6Before, MediaFiles(v6Root));
        AssertLeaseReleased(fixture.MediaDirectory);
        AssertLeaseReleased(v6Root);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Theory]
    [InlineData("v6")]
    [InlineData("corrupt")]
    [InlineData("unsupported")]
    public async Task MetadataOnlyFreezeAndWriteNeverReadTheMediaManifest(string wire)
    {
        using TempDirectory profile = new();
        string root = Path.Combine(profile.Path, "media");
        await WriteInventoryAsync(root, 2);
        await CaptureAsync(profile.Path, root);
        await AssertAvailableV6Async(root, 2);
        string path = Path.Combine(root, ManifestName);
        if (wire == "corrupt") await File.WriteAllTextAsync(path, "not a media manifest");
        if (wire == "unsupported") await File.WriteAllTextAsync(path, "{\"Version\":999,\"Entries\":{},\"Assets\":{}}");
        Dictionary<string, string> filesBefore = MediaFiles(root);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        using MemoryStream output = new();

        // A valid v6, malformed JSON and future wire all stay irrelevant when the
        // caller explicitly omits media. Lock both manifest and derived bytes.
        using (FileStream manifest = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream preview = new(DerivedPath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(
                reader, Id, At, PackageId, includeOriginals: false);
            Assert.True(frozen.IsFrozen, frozen.Error.ToString());
            NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
            Assert.Empty(snapshot.Manifests);
            Assert.Empty(snapshot.Payloads);
            TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
            Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
            TransferThread thread = Assert.Single(written.Package!.Catalog.Threads);
            TransferAttachment attachment = Assert.Single(Assert.Single(thread.Posts).Attachments);
            Assert.Equal(TransferContentStatus.Omitted, attachment.Status);
            Assert.Null(attachment.PayloadSha256);
            Assert.Null(attachment.OriginalByteVersion);
            Assert.Empty(attachment.DerivedRepresentations);
            Assert.Empty(thread.RetainedBindings);
            Assert.Empty(written.Package.Envelope.Payloads);
            Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
            Assert.Null(attachment.Metadata.PlannedPath);
        }

        Assert.True((await NativeTransferPackageValidator.InspectAsync(output)).IsPackageValid);
        Assert.Equal(filesBefore, MediaFiles(root));
        Assert.Equal(before.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
        AssertLeaseReleased(root);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task IncomingStatusAndEqualHashPreviewCannotResolveAsStoredOriginal(int status)
    {
        using TempDirectory directory = new();
        MediaOriginalEvidence evidence = Evidence((MediaObservedContentStatus)status);
        MediaDerivedRepresentation preview = Preview(evidence);
        await WriteInventoryAsync(directory.Path, 2, evidence);
        byte[] wireBefore = await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName));
        MediaAssetSnapshot snapshot = await AssertAvailableV6Async(directory.Path, 2);
        Assert.Equal(evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(preview, Assert.Single(snapshot.DerivedRepresentations));
        Assert.Equal(evidence.OriginalByteVersion, preview.Blob!.Actual); // Exact equal bytes do not confer Original identity.

        AssertOriginalUnavailable(snapshot, evidence.Metadata);
        AssertOriginalUnavailable(snapshot, evidence.Metadata with { AttachmentRevisionKey = null });
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "7999", evidence.Metadata));
        Assert.Null(MediaAttachmentResolver.Resolve(new[] { snapshot }, Id, PostId, evidence.Metadata));
        Assert.Null(snapshot.FindById(evidence.OriginalByteVersion!.Sha256));
        Assert.Null(snapshot.FindById(preview.Blob.RelativeFileName));
        await Assert.ThrowsAsync<ArgumentException>(() => snapshot.OpenVerifiedReadAsync(preview.Blob.Actual.Sha256));
        await using (Stream derived = await snapshot.OpenVerifiedDerivedReadAsync(preview))
        {
            Assert.Equal(0, derived.Position);
            using MemoryStream copied = new();
            await derived.CopyToAsync(copied);
            Assert.Equal(Bytes, copied.ToArray()); // It remains usable only through the semantic derived API.
        }
        Assert.Equal(wireBefore, await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName)));
        AssertOriginalUnavailable(await MediaAssetReader.OpenAsync(directory.Path), evidence.Metadata);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task OldOriginalOnlyManifestConsumersRemainUsable(int version)
    {
        using TempDirectory profile = new();
        string root = Path.Combine(profile.Path, "media");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ManifestName),
            "{\"Version\":" + version + ",\"Entries\":{},\"Assets\":{},\"AttachmentBindings\":{}}");
        await File.WriteAllBytesAsync(Path.Combine(root, LockName), []);
        await CaptureAsync(profile.Path, root);
        Dictionary<string, string> before = MediaFiles(root);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);

        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(result.IsFrozen, result.Error.ToString());
        using MemoryStream output = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(result.Snapshot!, output)).IsComplete);
        int callbacks = 0;
        int count = await new MediaStore().WithPassiveMergeTargetRootsAsync(new[] { root }, snapshots =>
        {
            callbacks++;
            Assert.Equal(version, Assert.Single(snapshots).ManifestVersion);
            return Task.FromResult(snapshots.Length);
        });
        Assert.Equal(1, callbacks);
        Assert.Equal(1, count);
        Assert.Equal(before, MediaFiles(root));
        AssertLeaseReleased(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V4WireStillCannotEnterOriginalOnlyImportOrMerge(bool merge)
    {
        using TempDirectory profile = new();
        using TempDirectory staging = new();
        string root = Path.Combine(profile.Path, "media");
        await CaptureAsync(profile.Path, root);
        MediaOriginalEvidence evidence = Evidence(MediaObservedContentStatus.Omitted);
        TransferAttachment attachment = new(evidence.Metadata, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(Bytes), null)
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Convert.ToBase64String(MD5.HashData(Bytes)),
                Convert.ToBase64String(SHA1.HashData(Bytes)))
        };
        NativeTransferCatalog content = new(At, [new(Id, NativeTransferArchiveFixture.ThreadUrl, "synthetic v4 gate",
            ThreadSourceState.NotFound, At, At, 1, false,
            [new(new(PostId, 0, PostFieldPresence.Attachments, Attachments: [attachment.Metadata]), [attachment])])]);
        using MemoryStream package = new();
        TransferPayloadSource payload = new(new(Bytes.Length, Hash(Bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(Bytes, writable: false)));
        Assert.True((await NativeTransferPackageWriter.WriteAsync(package, content, [payload], PackageId)).IsComplete);
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, staging.Path);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Single(stage.Package.Envelope.MeasuredPayloads);
        Assert.Equal(TransferContentStatus.Contained, stage.Package.Catalog.Threads[0].Posts[0].Attachments[0].Status);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.DisposeAsync(); // Sentinel: admission must precede any target read.
        Dictionary<string, string> before = NativeMediaImportTests.Files(profile.Path);

        if (merge)
        {
            NativeMediaMergePreparation result = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.UnsupportedPackage, result.Error);
            Assert.Null(result.Plan);
        }
        else
        {
            NativeMediaImportPreparation result = await catalog.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.UnsupportedPackage, result.Error);
            Assert.Null(result.Plan);
        }
        Assert.Equal(before, NativeMediaImportTests.Files(profile.Path));
        Assert.False(Directory.Exists(root));
    }

    private static CapturedAttachment Attachment()
    {
        MediaActualIntegrity actual = Actual();
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.bin",
            actual.Length, actual.Md5, actual.Sha256) { ExpectedSha1 = actual.Sha1 };
        return new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1,
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, PostId, 0, provider)
        };
    }

    private static MediaActualIntegrity Actual() => new(Bytes.Length, Convert.ToBase64String(MD5.HashData(Bytes)), Hash(Bytes))
        { Sha1 = Convert.ToBase64String(SHA1.HashData(Bytes)) };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string DerivedPath(string root) => Path.Combine(root, Hash(Bytes) + ".derived.bin");
    private static MediaOriginalEvidence Evidence(MediaObservedContentStatus status) => new(
        new(Id, PostId, 0, Attachment().AttachmentRevisionKey), Attachment(), status, Actual(), Origin);
    private static MediaDerivedRepresentation Preview(MediaOriginalEvidence parent) => new(parent.Occurrence,
        parent.OriginalByteVersion!, MediaDerivedKind.Preview, MediaDerivedStatus.Contained,
        new(Hash(Bytes) + ".derived.bin", Actual()), parent.Origin);

    private static async Task WriteInventoryAsync(string root, int inventory, MediaOriginalEvidence? evidence = null)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, ManifestName);
        JsonObject manifest = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject()
            : new JsonObject { ["Entries"] = new JsonObject(), ["Assets"] = new JsonObject(), ["AttachmentBindings"] = new JsonObject() };
        evidence ??= Evidence(MediaObservedContentStatus.Omitted);
        manifest["Version"] = 6;
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(inventory == 0 ? Array.Empty<MediaOriginalEvidence>() : new[] { evidence });
        manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(inventory == 2 ? new[] { Preview(evidence) } : Array.Empty<MediaDerivedRepresentation>());
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        if (!File.Exists(Path.Combine(root, LockName))) await File.WriteAllBytesAsync(Path.Combine(root, LockName), []);
        if (inventory == 2) await File.WriteAllBytesAsync(DerivedPath(root), Bytes);
    }

    private static async Task CaptureAsync(string profile, string root)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CapturePreservingRevisionsAsync(new(Id, NativeTransferArchiveFixture.ThreadUrl, "synthetic 404",
            ThreadSourceState.NotFound, At, root,
            [new(PostId, 0, PostFieldPresence.Attachments, Attachments: [Attachment()])]));
    }

    private static async Task<MediaAssetSnapshot> AssertAvailableV6Async(string root, int inventory)
    {
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.NotNull(snapshot.Generation);
        Assert.Equal(inventory == 0 ? 0 : 1, snapshot.OriginalEvidence.Length);
        Assert.Equal(inventory == 2 ? 1 : 0, snapshot.DerivedRepresentations.Length);
        return snapshot;
    }

    private static void AssertOriginalUnavailable(MediaAssetSnapshot snapshot, CapturedAttachment attachment)
    {
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.AttachmentBindings);
        if (attachment.AttachmentRevisionKey != null) Assert.Null(snapshot.FindAttachmentBinding(attachment.AttachmentRevisionKey));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, PostId, attachment));
    }

    private static Dictionary<string, string> MediaFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Hash(File.ReadAllBytes(path)), StringComparer.Ordinal);

    private static void AssertLeaseReleased(string root)
    {
        using FileStream lease = new(Path.Combine(root, LockName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, lease.Length);
    }
}
