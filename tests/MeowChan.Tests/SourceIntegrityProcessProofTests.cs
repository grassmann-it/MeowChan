using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Invoked twice by scripts/source-integrity-process-proof.py, never by the normal suite.
// This is a Core persistence fixture with a test policy, not Desktop policy/GUI/live proof.
public sealed class SourceIntegrityProcessProofTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly byte[] Payload = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");

    [SourceIntegrityProcessFact]
    public async Task ExactAcceptedRevisionSurvivesARealOfflineProcessRestart()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_PROCESS_ROOT")!);
        string token = Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_PROCESS_TOKEN")!;
        string phase = Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_PROCESS_PHASE")!;
        Assert.True(phase is "acquire" or "offline");
        Assert.Equal(token, await File.ReadAllTextAsync(Path.Combine(root, ".source-integrity-proof-owner")));
        string profile = Path.Combine(root, "profile");
        string receipt = Path.Combine(root, phase + ".json");
        Assert.False(File.Exists(receipt));
        if (phase == "acquire") Assert.False(Directory.Exists(profile));
        else Assert.True(Directory.Exists(profile));

        RejectAll transport = new();
        using StubImageBoard board = new(transport);
        TestPolicy policy = new(profile);
        object proof;
        // Returning from this scope releases every profile/catalog handle before receipt/exit.
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            if (phase == "acquire") await AcquireAsync(profile, board, policy, lease);
            Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
            proof = await VerifyAsync(root, profile, phase, board, policy);
        }
        Assert.Equal(0, transport.Attempts);
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, phase, token, profile, completed = true,
            processId = Environment.ProcessId, workerProcessIds = Array.Empty<int>(),
            networkNamespace = new FileInfo("/proc/self/ns/net").LinkTarget,
            testAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(SourceIntegrityProcessProofTests).Assembly.Location)),
            coreAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(MediaStore).Assembly.Location)),
            actualNetworkSends = 0, httpAttempts = transport.Attempts, profileLeaseReleased = true,
            freshPolicyAllowsMismatch = policy.GetSnapshot().AllowProviderSizeMd5Mismatch, proof,
            scope = "Production Core MediaStore, SQLite capture, resolver, Gallery selection and HTML export; synthetic bytes and test host policy. No Desktop host policy, plugin worker, decoded rendering or live-source claim."
        }, Json));
    }

    private static async Task AcquireAsync(string profile, StubImageBoard board, TestPolicy policy, ProfileLease lease)
    {
        DownloadItem first = Item(profile, board, policy, "a", Payload);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommitAsync(first, Payload));
        Assert.False(File.Exists(first.DestinationPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(first.DestinationPath)!, ".meowchan-manifest.json")));

        policy.SetAllowed(true);
        MediaCommitResult saved = await CommitAsync(first, Payload);
        Assert.Equal(Hash(Payload), saved.Sha256, ignoreCase: true);
        // A later, separately bound revision becomes current. The archived post still names A.
        byte[] later = [.. Payload, 42];
        await CommitAsync(Item(profile, board, policy, "b", later), later);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
        {
            await catalog.CaptureAsync(new CapturedThreadSnapshot(Resource, "https://stub.test/g/thread/123",
                "Exact accepted fixture", ThreadSourceState.Archived, DateTimeOffset.UtcNow,
                Path.GetDirectoryName(first.DestinationPath)!,
                [new CapturedPost("123", 0, PostFieldPresence.All, Html: "Synthetic persistence fixture",
                    Attachments: [Attachment(first)])]));
        }
        policy.SetAllowed(false);
    }

    private static async Task<object> VerifyAsync(string root, string profile, string phase, StubImageBoard board, TestPolicy policy)
    {
        DownloadItem first = Item(profile, board, policy, "a", Payload);
        MediaCommitResult restored = Assert.IsType<MediaCommitResult>(
            await new MediaStore().TryRestoreAsync(first, CancellationToken.None));
        Assert.True(restored.Reused);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(restored.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource));
        CapturedPost post = Assert.Single(thread.GetPosts());
        CapturedAttachment attachment = Assert.Single(post.Attachments!);
        Assert.Equal(Attachment(first), attachment);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(2, snapshot.AttachmentBindings.Count);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshot, Resource, post.PostId, attachment));
        Assert.False(resolved.IsLegacy);
        MediaAttachmentBinding binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.Equal(restored.AssetId, binding.AssetId);
        Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, binding.Acceptance.Kind);
        Assert.True(binding.Acceptance.SizeMismatch);
        Assert.True(binding.Acceptance.Md5Mismatch);
        Assert.Equal(1L, binding.Acceptance.PolicyRevision);
        Assert.Equal(profile, binding.Acceptance.ProfileId);
        Assert.Equal("test.source.owner", binding.Acceptance.PluginOwnerId);
        Assert.Equal(Resource.Source, binding.Acceptance.SourceId);
        Assert.Equal(Payload.LongLength + 86, binding.Provider.ExpectedSize);
        Assert.Equal(Payload.LongLength, binding.Actual.Length);
        Assert.Equal(Hash(Payload), binding.Actual.Sha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Payload)), binding.Actual.Md5);
        Assert.NotEqual(binding.Provider.ExpectedMd5, binding.Actual.Md5);
        Assert.False(resolved.Asset.IsCurrent);
        byte[] later = [.. Payload, 42];
        MediaAssetReference current = Assert.Single(snapshot.GetPage(), asset => asset.IsCurrent);
        Assert.NotEqual(binding.AssetId, current.AssetId);
        Assert.NotEqual(binding.Actual.Sha256, current.Sha256);
        Assert.Equal(Hash(later), current.Sha256, ignoreCase: true);
        Assert.Equal(later.LongLength, current.Length);
        await using (Stream currentBytes = await snapshot.OpenVerifiedReadAsync(current.AssetId))
        {
            using MemoryStream copy = new();
            await currentBytes.CopyToAsync(copy);
            Assert.Equal(later, copy.ToArray());
        }
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(binding.AssetId))
        {
            using MemoryStream copy = new();
            await verified.CopyToAsync(copy);
            Assert.Equal(Payload, copy.ToArray());
        }
        GalleryEntry gallery = Assert.Single(GallerySelection.Build(thread, [snapshot]).Entries,
            entry => entry.Post?.PostId == "123");
        Assert.Equal(binding.AssetId, gallery.Asset.AssetId);
        Assert.Equal(binding, gallery.AttachmentResolution!.Binding);

        CapturedAttachment newUnbound = attachment with
        {
            ExpectedSize = attachment.ExpectedSize + 1,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0,
                binding.Provider with { ExpectedSize = binding.Provider.ExpectedSize + 1 })
        };
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "123", newUnbound));
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Resource, Path.Combine(root, "export-" + phase));
        Assert.Equal(1, export.FileCount);
        Assert.Empty(export.Omissions);
        byte[] exported = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(export.Directory, "media"))));
        Assert.Equal(Payload, exported);
        Assert.Equal(0, policy.LeaseAcquisitions);
        return new
        {
            binding, attachmentRevisionKey = attachment.AttachmentRevisionKey,
            galleryAssetId = gallery.Asset.AssetId, exportSha256 = Hash(exported), restoredSha256 = Hash(Payload),
            manifestSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(snapshot.Directory, ".meowchan-manifest.json"))),
            currentAssetId = current.AssetId, currentSha256 = Hash(later),
            newUnboundRevisionRejected = true, retainedRevisionSelected = true
        };
    }

    private static DownloadItem Item(string profile, StubImageBoard board, TestPolicy policy, string revision, byte[] bytes)
    {
        MediaProviderMetadata provider = new("456", "original", "https://stub.test/media/456-" + revision + ".png",
            "fixture.png", bytes.LongLength + 86, Convert.ToBase64String(MD5.HashData([9, 9, 9])), Hash(bytes));
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0, provider);
        IThread thread = board.GetThread(new Uri("https://stub.test/g/thread/123"));
        return new DownloadItem(new Uri(provider.Url), Path.Combine(profile, "media", "stub", "g", "123", "456.png"),
            board, thread, provider.MediaId, provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = [new(Resource, "123", 0, key)], MediaIntegrityPolicy = policy
        };
    }

    private static CapturedAttachment Attachment(DownloadItem item) => new(0, item.MediaIdentifier, item.Variant,
        item.DownloadUri.AbsoluteUri, item.OriginalFileName, item.DestinationPath, item.ExpectedSize, item.ExpectedMd5)
    { ExpectedSha256 = item.ExpectedSha256, AttachmentRevisionKey = Assert.Single(item.AttachmentReferences).RevisionKey };
    private static async Task<MediaCommitResult> CommitAsync(DownloadItem item, byte[] bytes)
    {
        using MemoryStream content = new(bytes, writable: false);
        return await new MediaStore().CommitAsync(item, content, CancellationToken.None);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class RejectAll : HttpMessageHandler
    {
        internal int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("No HTTP is permitted in this fixture, including offline restart.");
        }
    }
    private sealed class TestPolicy(string profile) : ISourceMediaIntegrityPolicy
    {
        private SourceMediaIntegrityPolicySnapshot _snapshot = new(profile, "test.source.owner", "stub", false, 0);
        internal int LeaseAcquisitions;
        public SourceMediaIntegrityPolicySnapshot GetSnapshot() => _snapshot;
        internal void SetAllowed(bool allow)
        {
            _snapshot = _snapshot with { AllowProviderSizeMd5Mismatch = allow, Revision = _snapshot.Revision + 1 };
            LeaseAcquisitions = 0;
        }
        public ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_snapshot.AllowProviderSizeMd5Mismatch || _snapshot.Revision != expectedRevision)
                throw new InvalidOperationException("Test policy denied a fresh exception commit.");
            Interlocked.Increment(ref LeaseAcquisitions);
            return ValueTask.FromResult<MediaAcceptanceLease>(new TestLease(_snapshot));
        }
    }
    private sealed class TestLease(SourceMediaIntegrityPolicySnapshot snapshot) : MediaAcceptanceLease(snapshot)
    { public override ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}

public sealed class SourceIntegrityProcessFactAttribute : FactAttribute
{
    public SourceIntegrityProcessFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_PROCESS_PROOF") != "1")
            Skip = "Requires the explicit private, two-process Linux fixture driver.";
    }
}
