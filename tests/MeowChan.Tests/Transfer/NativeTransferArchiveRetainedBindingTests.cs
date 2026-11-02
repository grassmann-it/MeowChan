using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real local store/capture transactions; all bytes and identities are synthetic.
public sealed class NativeTransferArchiveRetainedBindingTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("df125c6f-8821-4e4c-a8ed-0662164c0122");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MediaProviderMetadata Provider(byte[] bytes, string file = "first.jpg", string? url = null) =>
        new("111", "original", url ?? "https://i.4cdn.org/b/111.jpg", file, bytes.Length,
            Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes));
    private static CapturedAttachment Attachment(MediaProviderMetadata p, string post) => new(0, p.MediaId, p.Variant,
        p.Url, p.OriginalFileName, ExpectedSize: p.ExpectedSize, ExpectedMd5: p.ExpectedMd5)
    {
        ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256,
        ProviderSizeHint = p.ProviderSizeHint, AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, p)
    };
    private static async Task CaptureAsync(string profile, string root, params CapturedPost[] posts)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CapturePreservingRevisionsAsync(new(Id, "https://boards.4chan.org/b/thread/7001", "synthetic 404",
            ThreadSourceState.NotFound, At, root, posts));
    }
    private static CapturedPost Post(string id, CapturedAttachment attachment) =>
        new(id, 0, PostFieldPresence.Attachments, Attachments: [attachment]);
    private static async Task<MediaCommitResult> CommitAsync(string root, MediaProviderMetadata p, byte[] bytes,
        params string[] posts) => await CommitWithPolicyAsync(root, p, bytes, null, posts);
    private static async Task<MediaCommitResult> CommitWithPolicyAsync(string root, MediaProviderMetadata p, byte[] bytes,
        ISourceMediaIntegrityPolicy? policy, params string[] posts)
    {
        Descriptor descriptor = new();
        DownloadItem item = new(new Uri(p.Url), Path.Combine(root, "111.jpg"), descriptor, descriptor.Thread,
            p.MediaId, p.ExpectedSize, p.ExpectedMd5, p.OriginalFileName)
        {
            ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256, ProviderSizeHint = p.ProviderSizeHint,
            MediaIntegrityPolicy = policy,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Id, p),
            AttachmentReferences = posts.Select(post => new AttachmentRevisionReference(Id, post, 0,
                MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, p))).ToArray()
        };
        using MemoryStream input = new(bytes, writable: false);
        return await new MediaStore().CommitAsync(item, input, CancellationToken.None);
    }
    private static async Task<NativeTransferPackage> WriteAsync(NativeTransferArchiveSnapshot snapshot)
    {
        using MemoryStream stage = new();
        TransferArchiveWriteResult write = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.True(write.IsComplete, write.Error + "/" + write.WriteError);
        TransferPackageInspection read = await NativeTransferPackageValidator.InspectAsync(stage);
        Assert.True(read.IsPackageValid, read.ToString());
        return read.Package!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReplacementKeepsExactOldBindingEvenWhenBytesAreShared(bool sameBytes)
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media");
        byte[] oldBytes = [1, 2, 3, 4], latestBytes = sameBytes ? oldBytes : [5, 6, 7, 8, 9];
        MediaProviderMetadata old = Provider(oldBytes), latest = Provider(latestBytes, "later.jpg");
        CapturedAttachment oldAttachment = Attachment(old, "7001"), latestAttachment = Attachment(latest, "7001");
        await CaptureAsync(profile.Path, root, Post("7001", oldAttachment));
        MediaCommitResult oldCommit = await CommitAsync(root, old, oldBytes, "7001");
        await CaptureAsync(profile.Path, root, Post("7001", latestAttachment));
        await CommitAsync(root, latest, latestBytes, "7001");
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(root);
        string generation = before.Generation!;
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView source = (await reader.ReadThreadAsync(Id))!;
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        NativeTransferPackage package = await WriteAsync(freeze.Snapshot!);
        Assert.Equal("chan-v2", package.Envelope.ContentSchema);
        TransferThread thread = package.Catalog.Threads.Single();
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Single(thread.Posts);
        TransferAttachment current = thread.Posts.Single().Attachments.Single();
        Assert.Equal(latestAttachment, current.Metadata);
        TransferRetainedBinding retained = thread.RetainedBindings.Single();
        Assert.Equal("7001", retained.PostId); Assert.Equal(oldAttachment, retained.Attachment.Metadata);
        Assert.Equal(Hash(oldBytes), retained.Attachment.PayloadSha256);
        Assert.Equal(Hash(latestBytes), current.PayloadSha256);
        Assert.Equal("strict", retained.Attachment.History!.Kind);
        Assert.NotEqual(current.Metadata.AttachmentRevisionKey, retained.Attachment.Metadata.AttachmentRevisionKey);
        Assert.Equal(sameBytes ? 1 : 2, package.Envelope.Payloads.Length);
        Assert.Equal(generation, (await MediaAssetReader.OpenAsync(root)).Generation);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldCommit.Path));
        Assert.Equal(source.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
        Assert.Equal(latestAttachment, (await reader.ReadThreadAsync(Id))!.Posts.Single().Attachments!.Single());
        using MemoryStream stage = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(freeze.Snapshot!, stage)).IsComplete);
        using ZipArchive zip = new(stage, ZipArchiveMode.Read);
        foreach (byte[] expected in new[] { oldBytes, latestBytes })
        {
            using Stream entry = zip.GetEntry("payload/" + Hash(expected))!.Open(); using MemoryStream actual = new();
            await entry.CopyToAsync(actual); Assert.Equal(expected, actual.ToArray());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BindingForUncapturedPostDoesNotInventPostAndMissingFileIsUnavailable(bool missing)
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media");
        byte[] bytes = [1, 5, 7, 11]; MediaProviderMetadata p = Provider(bytes);
        await CaptureAsync(profile.Path, root); // known thread, deliberately no post observations
        MediaCommitResult committed = await CommitAsync(root, p, bytes, "7999");
        MediaAttachmentBinding binding = (await MediaAssetReader.OpenAsync(root)).AttachmentBindings.Values.Single();
        if (missing) File.Delete(committed.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        NativeTransferPackage package = await WriteAsync(freeze.Snapshot!);
        TransferThread thread = package.Catalog.Threads.Single(); Assert.Empty(thread.Posts);
        TransferRetainedBinding retained = thread.RetainedBindings.Single();
        Assert.Equal("7999", retained.PostId); Assert.Equal(binding.Reference.RevisionKey, retained.Attachment.Metadata.AttachmentRevisionKey);
        Assert.Equal(missing ? TransferContentStatus.Unavailable : TransferContentStatus.Contained, retained.Attachment.Status);
        if (missing)
        {
            Assert.Null(retained.Attachment.History); Assert.Null(retained.Attachment.PayloadSha256);
            Assert.Empty(package.Envelope.Payloads); Assert.False(File.Exists(committed.Path));
        }
        else Assert.Equal(binding.Acceptance.AcceptedAtUtc, retained.Attachment.History!.AcceptedAtUtc);
        string json = Encoding.UTF8.GetString(package.Envelope.CatalogBytes.AsSpan());
        Assert.DoesNotContain("isDeleted", json); Assert.DoesNotContain(profile.Path, json);
        Assert.Empty((await reader.ReadThreadAsync(Id))!.Posts);
    }

    [Fact]
    public async Task PrivateRetainedUrlRejectsBeforeAnyCurrentOrRetainedMediaOpen()
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media");
        byte[] bytes = [2, 3, 5, 7]; MediaProviderMetadata secret = Provider(bytes, url: "https://i.4cdn.org/b/111.jpg?token=SYNTHETIC-SECRET");
        MediaProviderMetadata safe = Provider(bytes, "later.jpg");
        MediaCommitResult committed = await CommitAsync(root, secret, bytes, "7999");
        await CommitAsync(root, safe, bytes, "7001");
        await CaptureAsync(profile.Path, root, Post("7001", Attachment(safe, "7001")));
        Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(root)).Status);
        // Either verification path would reject this symlink. InvalidCatalog instead
        // proves privacy admission precedes verification even for a shared payload hash.
        File.Delete(committed.Path); File.CreateSymbolicLink(committed.Path, committed.Path + ".missing");
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.InvalidCatalog, freeze.Error); Assert.Null(freeze.Snapshot);
        Assert.DoesNotContain("SYNTHETIC-SECRET", freeze.ToString()); Assert.DoesNotContain(profile.Path, freeze.ToString());
        Assert.NotNull(new FileInfo(committed.Path).LinkTarget);
    }

    [Fact]
    public async Task ConflictingCurrentKeyFailsEvenWhenOtherBindingsCanCoverBothAssets()
    {
        using TempDirectory profile = new(); string a = Path.Combine(profile.Path, "a"), b = Path.Combine(profile.Path, "b");
        MediaProviderMetadata p = new("111", "original", "https://i.4cdn.org/b/111.jpg", "no-provider-expectations.jpg", null, null, null);
        await CommitAsync(a, p, [1, 2, 3], "7001", "7002");
        await CommitAsync(b, p, [4, 5, 6], "7001", "7003");
        await CaptureAsync(profile.Path, a, Post("7001", Attachment(p, "7001")));
        await CaptureAsync(profile.Path, b, Post("7001", Attachment(p, "7001")));
        MediaAssetSnapshot first = await MediaAssetReader.OpenAsync(a), second = await MediaAssetReader.OpenAsync(b);
        Assert.Equal(MediaManifestStatus.Available, first.Status); Assert.Equal(MediaManifestStatus.Available, second.Status);
        Assert.Null(MediaAttachmentResolver.Resolve([first, second], Id, "7001", Attachment(p, "7001")));
        Assert.Equal(a, MediaAttachmentResolver.Resolve([first, second], Id, "7002", Attachment(p, "7002"))!.Snapshot.Directory);
        Assert.Equal(b, MediaAttachmentResolver.Resolve([first, second], Id, "7003", Attachment(p, "7003"))!.Snapshot.Directory);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.InvalidMedia, freeze.Error); Assert.Null(freeze.Snapshot);
    }

    [Theory]
    [InlineData("identical")] [InlineData("missing")] [InlineData("modified")]
    public async Task RedundantRootOriginsAreConservativelyRejectedNeverTrustedByHash(string other)
    {
        using TempDirectory profile = new(); string a = Path.Combine(profile.Path, "a"), b = Path.Combine(profile.Path, "b");
        byte[] bytes = [11, 13, 17]; MediaProviderMetadata p = Provider(bytes);
        MediaCommitResult committed = await CommitAsync(a, p, bytes, "7999"); Directory.CreateDirectory(b);
        foreach (string file in Directory.EnumerateFiles(a)) File.Copy(file, Path.Combine(b, Path.GetFileName(file)));
        string secondFile = Path.Combine(b, Path.GetFileName(committed.Path));
        if (other == "missing") File.Delete(secondFile);
        if (other == "modified") await File.WriteAllBytesAsync(secondFile, [19, 23, 29]);
        await CaptureAsync(profile.Path, a); await CaptureAsync(profile.Path, b);
        Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(b)).Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.UnrepresentedAssets, freeze.Error); Assert.Null(freeze.Snapshot);
    }

    [Fact]
    public async Task RetainedManifestDriftStillPreventsWritingAndDoesNotSilentlyReplan()
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media");
        byte[] bytes = [31, 37, 41]; MediaProviderMetadata p = Provider(bytes);
        await CommitAsync(root, p, bytes, "7999"); await CaptureAsync(profile.Path, root);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        await File.AppendAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "\n ");
        using MemoryStream stage = new(); TransferArchiveWriteResult write = await NativeTransferArchiveExporter.WriteAsync(freeze.Snapshot!, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, write.Error); Assert.Null(write.Package); Assert.Equal(0, stage.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealRetainedMismatchIsPassiveAndUnavailableDoesNotInventEvidence(bool missing)
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media");
        byte[] bytes = [43, 47, 53];
        MediaProviderMetadata p = Provider(bytes) with { ExpectedSize = 99, ExpectedMd5 = Convert.ToBase64String(MD5.HashData(new byte[] { 59 })) };
        MediaCommitResult committed = await CommitWithPolicyAsync(root, p, bytes, new ExplicitSyntheticPolicy(), "7999");
        await CaptureAsync(profile.Path, root);
        MediaAttachmentBinding before = (await MediaAssetReader.OpenAsync(root)).AttachmentBindings.Values.Single();
        Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, before.Acceptance.Kind);
        if (missing) File.Delete(committed.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        NativeTransferPackage package = await WriteAsync(freeze.Snapshot!);
        TransferAttachment retained = package.Catalog.Threads.Single().RetainedBindings.Single().Attachment;
        Assert.Equal(before.Reference.RevisionKey, retained.Metadata.AttachmentRevisionKey);
        Assert.Equal(p.ExpectedMd5, retained.Metadata.ExpectedMd5); Assert.Equal(99, retained.Metadata.ExpectedSize);
        if (missing)
        {
            Assert.Equal(TransferContentStatus.Unavailable, retained.Status); Assert.Null(retained.History); Assert.Null(retained.PayloadSha256);
        }
        else
        {
            Assert.Equal("provider-mismatch", retained.History!.Kind);
            Assert.True(retained.History.SizeMismatch); Assert.True(retained.History.Md5Mismatch);
            Assert.Equal(before.Acceptance.AcceptedAtUtc, retained.History.AcceptedAtUtc);
        }
        string wire = Encoding.UTF8.GetString(package.Envelope.CatalogBytes.AsSpan());
        foreach (string secret in new[] { "SYNTHETIC-PRIVATE-PROFILE", "SYNTHETIC-PRIVATE-OWNER", "policyRevision", "pluginOwnerId", "profileId" })
            Assert.DoesNotContain(secret, wire);
        Assert.Equal(before, (await MediaAssetReader.OpenAsync(root)).AttachmentBindings.Values.Single());
    }

    [Fact]
    public async Task ThreeSameSlotRevisionsKeepExactExtendedRawMetadataAndSharedPayloads()
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media"); byte[] bytes = [61, 67, 71], other = [73, 79, 83];
        MediaProviderMetadata one = Provider(bytes) with { ExpectedMd5 = " " + Convert.ToBase64String(MD5.HashData(bytes)) + "\n",
            ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(bytes)), ProviderSizeHint = new("1.50", "KiB", MediaSizePrecision.Truncated) };
        MediaProviderMetadata two = one with { OriginalFileName = "second.jpg", ProviderSizeHint = new("1.500", "KiB", MediaSizePrecision.Rounded) };
        MediaProviderMetadata latest = Provider(other, "third.jpg");
        foreach ((MediaProviderMetadata p, byte[] content) in new[] { (one, bytes), (two, bytes), (latest, other) })
        {
            await CaptureAsync(profile.Path, root, Post("7001", Attachment(p, "7001")));
            await CommitAsync(root, p, content, "7001");
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString()); NativeTransferPackage package = await WriteAsync(freeze.Snapshot!);
        TransferThread thread = package.Catalog.Threads.Single(); Assert.Single(thread.Posts); Assert.Equal(2, package.Envelope.Payloads.Length);
        Assert.Equal(Attachment(latest, "7001"), thread.Posts.Single().Attachments.Single().Metadata);
        Assert.Equal(2, thread.RetainedBindings.Length);
        foreach (MediaProviderMetadata p in new[] { one, two })
        {
            CapturedAttachment exact = Attachment(p, "7001");
            TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings, b => b.Attachment.Metadata.AttachmentRevisionKey == exact.AttachmentRevisionKey);
            Assert.Equal(exact, retained.Attachment.Metadata); Assert.Equal("7001", retained.PostId);
            Assert.StartsWith("att-v2:", exact.AttachmentRevisionKey); Assert.Equal(Hash(bytes), retained.Attachment.PayloadSha256);
        }
    }

    [Theory]
    [InlineData("deleted")] [InlineData("changed")] [InlineData("manifest-during-write")]
    public async Task RetainedOnlyExactReopenAndAfterWriteRecheckRemainMandatory(string failure)
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media"); byte[] bytes = [89, 97, 101];
        MediaCommitResult committed = await CommitAsync(root, Provider(bytes), bytes, "7999"); await CaptureAsync(profile.Path, root);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        if (failure == "deleted") File.Delete(committed.Path);
        if (failure == "changed") await File.WriteAllBytesAsync(committed.Path, [103, 107, 109]);
        using OnFirstWrite stage = new(() => { if (failure == "manifest-during-write") File.AppendAllText(Path.Combine(root, ".meowchan-manifest.json"), "\n "); });
        TransferArchiveWriteResult write = await NativeTransferArchiveExporter.WriteAsync(freeze.Snapshot!, stage);
        Assert.False(write.IsComplete); Assert.Null(write.Package); Assert.Null(write.BytesWritten);
        if (failure == "manifest-during-write")
        {
            Assert.Equal(TransferArchiveError.StaleSnapshot, write.Error);
            Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid); // valid stage still must not publish
        }
    }

    [Fact]
    public async Task SameBytesAndKeyButDifferentAcceptanceNeverDisappearBehindCurrentKeySuppression()
    {
        using TempDirectory profile = new(); string a = Path.Combine(profile.Path, "a"), b = Path.Combine(profile.Path, "b");
        byte[] bytes = [113, 127, 131]; MediaProviderMetadata p = Provider(bytes);
        await CommitAsync(a, p, bytes, "7001"); Directory.CreateDirectory(b);
        foreach (string file in Directory.EnumerateFiles(a)) File.Copy(file, Path.Combine(b, Path.GetFileName(file)));
        string manifest = Path.Combine(b, ".meowchan-manifest.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        foreach (var binding in json["AttachmentBindings"]!.AsObject())
            binding.Value!["Acceptance"]!["AcceptedAtUtc"] = At.AddDays(-1).ToString("O");
        await File.WriteAllTextAsync(manifest, json.ToJsonString());
        Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(b)).Status);
        await CaptureAsync(profile.Path, a, Post("7001", Attachment(p, "7001"))); await CaptureAsync(profile.Path, b, Post("7001", Attachment(p, "7001")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.InvalidMedia, freeze.Error); Assert.Null(freeze.Snapshot);
    }

    [Fact]
    public async Task ExplicitMetadataOnlyDoesNotStartDiscoveringPrivateRetainedInventory()
    {
        using TempDirectory profile = new(); string root = Path.Combine(profile.Path, "media"); byte[] bytes = [137, 139, 149];
        MediaProviderMetadata p = Provider(bytes, url: "https://i.4cdn.org/b/111.jpg?SYNTHETIC-SECRET");
        await CommitAsync(root, p, bytes, "7999"); await CaptureAsync(profile.Path, root);
        // A malformed manifest is a deliberate tripwire: metadata-only must not read it.
        await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "not json");
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, includeOriginals: false);
        Assert.True(freeze.IsFrozen, freeze.Error.ToString()); NativeTransferPackage package = await WriteAsync(freeze.Snapshot!);
        Assert.Equal("chan-v1", package.Envelope.ContentSchema); Assert.Empty(package.Catalog.Threads.Single().RetainedBindings);
        Assert.Empty(package.Envelope.Payloads);
    }

    private sealed class OnFirstWrite(Action once) : MemoryStream
    {
        private Action? _once = once;
        private void Before() => Interlocked.Exchange(ref _once, null)?.Invoke();
        public override void Write(byte[] buffer, int offset, int count) { Before(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Before(); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Before(); return base.WriteAsync(buffer, cancellationToken); }
    }
    private sealed class ExplicitSyntheticPolicy : ISourceMediaIntegrityPolicy
    {
        private readonly SourceMediaIntegrityPolicySnapshot _snapshot = new("SYNTHETIC-PRIVATE-PROFILE", "SYNTHETIC-PRIVATE-OWNER", "4chan", true, 91);
        public SourceMediaIntegrityPolicySnapshot GetSnapshot() => _snapshot;
        public ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(_snapshot.Revision, expectedRevision);
            return ValueTask.FromResult<MediaAcceptanceLease>(new Lease(_snapshot));
        }
        private sealed class Lease(SourceMediaIntegrityPolicySnapshot snapshot) : MediaAcceptanceLease(snapshot)
        { public override ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class Descriptor : IImageBoard
    {
        internal ProbeThread Thread { get; }
        internal Descriptor() => Thread = new ProbeThread(new Board(this), new Uri("https://boards.4chan.org/b/thread/7001"));
        public string Name => "4chan";
        public string NiceName => "Synthetic identity only";
        public Uri BaseUri => new("https://boards.4chan.org/");
        public bool CanHandle(Uri uri) => throw new InvalidOperationException("No provider work allowed.");
        public bool IsThreadUri(Uri uri) => throw new InvalidOperationException("No provider work allowed.");
        public bool IsBoardUri(Uri uri) => throw new InvalidOperationException("No provider work allowed.");
        public IBoard GetBoard(Uri uri) => throw new InvalidOperationException("No provider work allowed.");
        public IThread GetThread(Uri uri) => throw new InvalidOperationException("No provider work allowed.");
        public HttpClient GetHttpClient() => throw new InvalidOperationException("No network allowed.");
        private sealed class Board(IImageBoard descriptor) : BaseBoard
        {
            public override IImageBoard ImageBoard => descriptor;
            public override string BoardCode => "b";
            public override string Name { get; protected set; } = "Synthetic";
            public override string NiceName => Name;
            public override Uri BoardUri => new("https://boards.4chan.org/b/");
            public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("No provider work allowed.");
        }
    }
}
