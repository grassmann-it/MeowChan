using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests.GoalFixtures;

/// <summary>
/// A real, disposable local archive + media-store seed for native GP11 tests. Canonical
/// URLs are inert metadata only. No bundled provider, HTTP client, session or exporter is used.
/// Payloads intentionally are synthetic bytes, not decodable JPEG fixtures.
/// </summary>
internal sealed class NativeTransferArchiveFixture : IAsyncDisposable
{
    public const string ThreadUrl = "https://boards.4chan.org/b/thread/7001";
    public const string StrictPostId = "7001";
    public const string LegacyPostId = "7002";
    public const string UnavailablePostId = "7003";
    private static readonly DateTimeOffset CapturedTime = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _profile = new();
    private readonly OfflineDescriptor _descriptor = new();
    private readonly byte[] _strictBytes = Encoding.ASCII.GetBytes("GP11 synthetic strict original\n\0\x01\x02");
    private readonly byte[] _legacyBytes = Encoding.ASCII.GetBytes("GP11 synthetic legacy manifest original\n\0\x03\x04");
    private bool _disposed;

    private NativeTransferArchiveFixture(ThreadSourceState sourceState, bool? decimalPostOrder)
    {
        MediaDirectory = Path.Combine(ProfilePath, "media", "4chan", "b", "7001");
        StrictAttachment = new CapturedAttachment(0, "111", Url: "https://i.4cdn.org/b/111.jpg",
            OriginalFileName: "strict-synthetic.jpg", PlannedPath: Path.Combine(MediaDirectory, "111.jpg"),
            ExpectedSize: _strictBytes.Length, ExpectedMd5: Md5(_strictBytes))
        { ExpectedSha1 = Sha1(_strictBytes), ExpectedSha256 = Sha256(_strictBytes) };
        StrictAttachment = Bind(StrictPostId, StrictAttachment);
        // Deliberately lacks provider expectations and a host revision. Exact manifest media ID,
        // original variant and unchanged source URL must resolve it; a loose file is not evidence.
        LegacyAttachment = new(0, "112", Url: "https://i.4cdn.org/b/112.jpg",
            OriginalFileName: "legacy-synthetic.jpg", PlannedPath: Path.Combine(MediaDirectory, "112.jpg"));
        UnavailableAttachment = Bind(UnavailablePostId, new(0, "113", Url: "https://i.4cdn.org/b/113.jpg",
            OriginalFileName: "unavailable-synthetic.jpg", PlannedPath: Path.Combine(MediaDirectory, "113.jpg")));
        ImmutableArray<CapturedPost> posts =
        [
            new(StrictPostId, long.MaxValue, PostFieldPresence.All, CapturedTime, "Synthetic GP11", "synthetic",
                "<p>Synthetic <b>strict original</b>.</p>", Attachments: [StrictAttachment]),
            new(LegacyPostId, 0, PostFieldPresence.All, CapturedTime.AddSeconds(1), "", null,
                "<p>Legacy original <a href=\"#p7001\" class=\"quotelink\">&gt;&gt;7001</a></p>", Attachments: [LegacyAttachment]),
            new(UnavailablePostId, 1, PostFieldPresence.All, CapturedTime.AddSeconds(2), null, "synthetic",
                "<p>Metadata only; <em>original unavailable</em>.</p>", Attachments: [UnavailableAttachment])
        ];
        Snapshot = new(ThreadId, ThreadUrl, "Synthetic native transfer archive", sourceState,
            CapturedTime, MediaDirectory, posts) { DecimalPostOrder = decimalPostOrder };
    }

    public ResourceId ThreadId { get; } = new("4chan", "b", "7001");
    public string ProfilePath => _profile.Path;
    public string ProfileDirectory => ProfilePath;
    public string MediaDirectory { get; }
    public CapturedThreadSnapshot Snapshot { get; }
    /// <summary>Captured input order; use ExpectedPostIds for the reader's ordering contract.</summary>
    public IReadOnlyList<CapturedPost> Posts => Snapshot.Posts;
    public bool? DecimalPostOrder => Snapshot.DecimalPostOrder;
    public IReadOnlyList<string> ExpectedPostIds => DecimalPostOrder == false
        ? [LegacyPostId, UnavailablePostId, StrictPostId] : [StrictPostId, LegacyPostId, UnavailablePostId];
    public CapturedAttachment StrictAttachment { get; }
    public CapturedAttachment LegacyAttachment { get; }
    public CapturedAttachment UnavailableAttachment { get; }
    public ReadOnlyMemory<byte> StrictBytes => _strictBytes;
    public ReadOnlyMemory<byte> LegacyBytes => _legacyBytes;
    public string StrictRevision => StrictAttachment.AttachmentRevisionKey!;
    public string? LegacyRevision => LegacyAttachment.AttachmentRevisionKey;
    public string UnavailableRevision => UnavailableAttachment.AttachmentRevisionKey!;
    public string UnavailablePath => UnavailableAttachment.PlannedPath!;
    public MediaCommitResult StrictCommit { get; private set; } = null!;
    public MediaCommitResult LegacyCommit { get; private set; } = null!;
    public string StrictAssetId => StrictCommit.AssetId!;
    public string LegacyAssetId => LegacyCommit.AssetId!;
    public MediaAttachmentBinding StrictBinding { get; private set; } = null!;
    public ArchiveThreadSummary Summary { get; private set; } = null!;
    public long CaptureRevision => Summary.Revision;
    public string ManifestGeneration { get; private set; } = null!;
    public int UnexpectedProviderCalls => _descriptor.Calls;

    public static async Task<NativeTransferArchiveFixture> CreateAsync(
        ThreadSourceState sourceState = ThreadSourceState.Archived, bool? decimalPostOrder = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceState is not (ThreadSourceState.Archived or ThreadSourceState.NotFound))
            throw new ArgumentOutOfRangeException(nameof(sourceState));
        NativeTransferArchiveFixture fixture = new(sourceState, decimalPostOrder);
        try
        {
            await fixture.CommitAsync(cancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private CapturedAttachment Bind(string postId, CapturedAttachment attachment) => attachment with
    {
        AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(ThreadId, postId, attachment.Position,
            new(attachment.MediaId!, attachment.Variant, attachment.Url!, attachment.OriginalFileName,
                attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256)
            { ExpectedSha1 = attachment.ExpectedSha1 })
    };

    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        using (ProfileLease lease = ProfileLease.Acquire(ProfilePath))
        {
            await using ArchiveCatalog archive = await ArchiveCatalog.OpenAsync(ProfilePath, lease, cancellationToken);
            // This is an actual catalog transaction, using the existing replay path rather
            // than CaptureAsync, which would stamp the deliberately legacy-null attachment.
            await archive.CapturePreservingRevisionsAsync(Snapshot, cancellationToken);
            MediaStore store = new();
            StrictCommit = await CommitOriginalAsync(store, StrictAttachment, _strictBytes,
                new(ThreadId, StrictPostId, StrictAttachment.Position, StrictRevision), cancellationToken);
            LegacyCommit = await CommitOriginalAsync(store, LegacyAttachment, _legacyBytes, null, cancellationToken);
        }
        // Every writer/lease is closed before CreateAsync returns. Root can reopen either side.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(ProfilePath, cancellationToken);
        Summary = (await reader.ReadThreadAsync(ThreadId, cancellationToken))?.Summary
            ?? throw new InvalidOperationException("Synthetic archive commit was not readable.");
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(MediaDirectory, cancellationToken);
        StrictBinding = media.FindAttachmentBinding(StrictRevision)
            ?? throw new InvalidOperationException("Synthetic strict media binding was not committed.");
        ManifestGeneration = media.Generation
            ?? throw new InvalidOperationException("Synthetic media manifest was not readable.");
        if (StrictCommit.AssetId is null || LegacyCommit.AssetId is null)
            throw new InvalidOperationException("Synthetic media asset identities were not committed.");
    }

    private async Task<MediaCommitResult> CommitOriginalAsync(MediaStore store, CapturedAttachment attachment,
        byte[] bytes, AttachmentRevisionReference? reference, CancellationToken cancellationToken)
    {
        // Same DownloadItem + MemoryStream -> MediaStore pattern as MediaAssetReaderTests and
        // SourceMediaIntegrityTests. The descriptor supplies identity only, never provider work.
        MediaProviderMetadata provider = new(attachment.MediaId!, attachment.Variant, attachment.Url!,
            attachment.OriginalFileName, bytes.Length, Md5(bytes), Sha256(bytes)) { ExpectedSha1 = attachment.ExpectedSha1 };
        DownloadItem item = new(new Uri(provider.Url), attachment.PlannedPath!, _descriptor, _descriptor.Thread,
            provider.MediaId, provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            AcquisitionRevisionKey = reference == null ? null : MediaRevisionKeys.CreateAcquisitionKey(ThreadId, provider),
            AttachmentReferences = reference == null ? [] : [reference]
        };
        using MemoryStream source = new(bytes, writable: false);
        return await store.CommitAsync(item, source, cancellationToken);
    }

    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _profile.Dispose(); _disposed = true; }
        return ValueTask.CompletedTask;
    }

    // Not a source plugin. There is deliberately no HttpClient, network handler or source
    // implementation behind this passive test descriptor. Any provider entry point is a bug.
    private sealed class OfflineDescriptor : IImageBoard
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal ProbeThread Thread { get; }
        internal OfflineDescriptor()
        {
            OfflineBoard board = new(this);
            Thread = new(board, new Uri(ThreadUrl)) { OnCheck = () => throw Forbidden() };
        }
        internal InvalidOperationException Forbidden()
        {
            Interlocked.Increment(ref _calls);
            return new("Synthetic transfer fixture must not invoke a provider or network.");
        }
        public string Name => "4chan";
        public string NiceName => "Synthetic identity only";
        public Uri BaseUri => new("https://boards.4chan.org/");
        public bool CanHandle(Uri uri) => throw Forbidden();
        public bool IsThreadUri(Uri uri) => throw Forbidden();
        public bool IsBoardUri(Uri uri) => throw Forbidden();
        public IBoard GetBoard(Uri uri) => throw Forbidden();
        public IThread GetThread(Uri uri) => throw Forbidden();
        public HttpClient GetHttpClient() => throw Forbidden();
    }

    private sealed class OfflineBoard(OfflineDescriptor descriptor) : BaseBoard
    {
        public override IImageBoard ImageBoard => descriptor;
        public override string BoardCode => "b";
        public override string Name { get; protected set; } = "Synthetic board identity";
        public override string NiceName => Name;
        public override Uri BoardUri => new("https://boards.4chan.org/b/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => throw descriptor.Forbidden();
    }
}
