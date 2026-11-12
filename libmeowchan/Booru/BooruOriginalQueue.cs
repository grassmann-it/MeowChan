using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Booru;

/// <summary>How one original-download request was admitted.</summary>
internal enum BooruOriginalAdmissionStatus
{
    /// <summary>A new job for (danbooru, post, original) was admitted to the shared queue.</summary>
    Queued,
    /// <summary>The same provider post original is already owned by an active queue job.</summary>
    AlreadyQueued,
    /// <summary>The catalog already links the post to a committed local asset; nothing is downloaded.</summary>
    AlreadyLinked,
    /// <summary>No acceptable original exists; sample/preview are never substituted.</summary>
    Unavailable,
    /// <summary>The queue refused the job (stopping, cancelled identity still draining, or closed admission).</summary>
    NotAdmitted
}

/// <summary>Observable lifecycle of an original download. Only <see cref="Linked"/> is a success.</summary>
internal enum BooruOriginalDownloadState
{
    Queued,
    Linked,
    /// <summary>The media is committed, but the catalog link failed. Retry relinks without a new transfer.</summary>
    LinkFailed,
    DownloadFailed,
    Cancelled,
    Unavailable,
    NotAdmitted
}

internal sealed record BooruOriginalDownloadOutcome(
    BooruOriginalDownloadState State,
    string PostId,
    BooruOriginalSelectionError SelectionError = BooruOriginalSelectionError.None,
    string? AssetId = null,
    string? LocalPath = null,
    string? Sha256 = null,
    long? Length = null,
    string? Failure = null);

internal sealed record BooruOriginalAdmission(
    BooruOriginalAdmissionStatus Status,
    string PostId,
    Task<BooruOriginalDownloadOutcome> Completion,
    BooruOriginalSelectionError SelectionError = BooruOriginalSelectionError.None);

/// <summary>
/// Connects explicit Danbooru original requests to the existing <see cref="DownloadQueue"/>. There is
/// no second queue: each post gets a runtime-only queue owner whose job identity is exactly
/// (danbooru, ProviderPostId, original), and the shared queue provides capacity, fairness, pause,
/// cancellation, bandwidth, target-space gating and the verified MediaStore commit (provider size and
/// MD5, transport length, measured SHA-256) before any file is published.
///
/// Durability is the B3 <see cref="BooruCatalogStore"/>, not the chan work journal: an admission first
/// stores the provider claim and its finding; a post with at least one finding and no
/// <see cref="BooruPostRecord.LocalAssetId"/> is a pending original. Only after the queue reports an
/// atomic <see cref="MediaCommitResult.AssetId"/> is <see cref="BooruCatalogStore.LinkLocalAssetAdmitted"/>
/// called, once, under the job's own producer admission. A failed link stays visible as
/// <see cref="BooruOriginalDownloadState.LinkFailed"/>; a retry finds the committed file through the
/// store's strict restore and relinks without a second transfer.
///
/// The runtime owner is never registered with the Registry, the session, the work journal or the
/// archive, never claims a URL, and never writes private tags.
/// </summary>
internal sealed class BooruOriginalQueue
{
    internal const string ProviderMediaHost = "cdn.donmai.us";
    internal const string PostsBoardCode = "posts";
    internal const string ProviderDirectoryName = "danbooru";

    private readonly object _sync = new();
    private readonly DownloadQueue _queue;
    private readonly BooruCatalogStore _catalog;
    private readonly string _outputRoot;
    private readonly HashSet<string> _allowedMediaHosts;
    private readonly TimeProvider _timeProvider;
    private readonly OwnerProvider _provider;
    private readonly Dictionary<string, PostOwner> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BooruOriginalDownloadOutcome> _outcomes = new(StringComparer.Ordinal);

    internal BooruOriginalQueue(DownloadQueue queue, BooruCatalogStore catalog, string outputRoot,
        HttpClient? mediaClient = null, IEnumerable<string>? allowedMediaHosts = null, TimeProvider? timeProvider = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        _outputRoot = Path.GetFullPath(outputRoot);
        _allowedMediaHosts = new HashSet<string>(allowedMediaHosts ?? [ProviderMediaHost], StringComparer.OrdinalIgnoreCase);
        if (_allowedMediaHosts.Count == 0 || _allowedMediaHosts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one explicit provider media host is required.", nameof(allowedMediaHosts));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // The existing media transport: no redirects, shared provider admission and cooldown.
        _provider = new OwnerProvider(mediaClient ?? ProviderHttpClientFactory.GetDefaultClient("danbooru-media"));
    }

    /// <summary>Raised outside locks whenever a post's visible download state changes.</summary>
    internal event Action<BooruOriginalDownloadOutcome>? OutcomeChanged;

    // Deterministic test observer: counts catalog links performed for a committed receipt.
    internal Action<string, string>? LinkingForTests { get; set; }

    /// <summary>The latest in-process state per post; durable success is the catalog link only.</summary>
    internal IReadOnlyDictionary<string, BooruOriginalDownloadOutcome> Snapshot()
    {
        lock (_sync) return new Dictionary<string, BooruOriginalDownloadOutcome>(_outcomes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Persists the provider claim and the finding that explains this explicit request, then admits the
    /// original into the shared queue. A one-shot never creates a watcher; a watcher finding never marks
    /// anything but this single post.
    /// <para>
    /// Order matters: an unacceptable original is recorded as <see cref="BooruOriginalIntent.Unavailable"/>
    /// instead of a finding that would look like a requested download after a restart. The claim itself is
    /// kept so a later, richer provider answer can still be evaluated.
    /// </para>
    /// </summary>
    internal async Task<BooruOriginalAdmission> AdmitAsync(BooruPostClaim post, BooruFindingKind kind,
        string? watcherId = null, string? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _catalog.UpsertPost(post, now, cancellationToken);
        if (!TryPlan(post, out BooruOriginalDownloadPlan? plan, out BooruOriginalSelectionError error))
            return RecordUnavailable(post, kind, watcherId, query, error, cancellationToken);
        BooruPostRecord record = _catalog.AddFinding(post.SourceId, post.PostId, kind, watcherId, query, now, cancellationToken);
        return await AdmitRecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private BooruOriginalAdmission RecordUnavailable(BooruPostClaim post, BooruFindingKind kind,
        string? watcherId, string? query, BooruOriginalSelectionError error, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        BooruPostRecord record = _catalog.AddFinding(post.SourceId, post.PostId, kind, watcherId, query, now, cancellationToken);
        BooruPostRecord marked = _catalog.SetOriginalIntent(post.SourceId, post.PostId,
            BooruOriginalIntent.Unavailable, cancellationToken);
        BooruOriginalDownloadOutcome outcome = new(BooruOriginalDownloadState.Unavailable, post.PostId, error);
        Publish(outcome);
        _ = record;
        _ = marked;
        return new(BooruOriginalAdmissionStatus.Unavailable, post.PostId, Task.FromResult(outcome), error);
    }

    /// <summary>Re-admits a stored post without adding a finding, e.g. after a visible failure or cancel.</summary>
    internal Task<BooruOriginalAdmission> RetryAsync(string postId, CancellationToken cancellationToken = default)
    {
        if (!DanbooruPostReference.IsValidPostId(postId)) throw new ArgumentException("The Danbooru post id is invalid.", nameof(postId));
        BooruPostRecord record = _catalog.Read().Posts.FirstOrDefault(item =>
                item.SourceId == DanbooruPostReference.ProviderId && item.PostId == postId)
            ?? throw new InvalidOperationException("The provider post is not stored locally.");
        // An explicit retry overrides a previously recorded terminal outcome (unavailable/not admitted).
        if (record.OriginalIntent is BooruOriginalIntent.Unavailable or BooruOriginalIntent.NotAdmitted
            or BooruOriginalIntent.Cancelled)
            record = _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                BooruOriginalIntent.None, cancellationToken);
        return AdmitRecordAsync(record, cancellationToken);
    }

    /// <summary>
    /// Stored posts whose original was explicitly requested and is still awaiting a committed local asset.
    /// A cancelled, unavailable or queue-refused request is deliberately not pending, so a restart never
    /// silently re-admits work the user did not ask for.
    /// </summary>
    internal IReadOnlyList<BooruPostRecord> GetPendingOriginals() => _catalog.Read().Posts
        .Where(record => record.HasPendingOriginal).ToArray();

    /// <summary>
    /// Restart recovery: re-admits every pending stored original from local data only. A medium that was
    /// committed before a crash is restored and linked without a network transfer.
    /// </summary>
    internal async Task<IReadOnlyList<BooruOriginalAdmission>> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        List<BooruOriginalAdmission> admitted = new();
        foreach (BooruPostRecord record in GetPendingOriginals())
        {
            cancellationToken.ThrowIfCancellationRequested();
            admitted.Add(await AdmitRecordAsync(record, cancellationToken).ConfigureAwait(false));
        }
        return admitted;
    }

    /// <summary>Cancels the active job for a post. A completed link is never turned into a cancellation.</summary>
    internal async Task<bool> CancelAsync(string postId)
    {
        PostOwner? owner;
        lock (_sync) _active.TryGetValue(postId, out owner);
        // The queue cancels the thread state and reports back through OnMediaCancelled, which
        // already completes the owner and removes it from _active. Re-read before declaring "no job".
        if (owner == null)
        {
            // No live job. A second cancel of the same post is a no-op, not a fresh cancellation,
            // even though the last recorded outcome for it is Cancelled.
            return false;
        }
        // The queue reports the cancellation back through OnMediaCancelled, which completes the
        // owner exactly once and records the durable Cancelled intent. The local Complete is the
        // fallback for an owner the queue never notified (already finished or never admitted).
        await _queue.CancelDownloadsForThreadAsync(owner).ConfigureAwait(false);
        bool completed = Complete(owner, new BooruOriginalDownloadOutcome(BooruOriginalDownloadState.Cancelled, postId));
        // Either path is a real cancellation; make the durable intent explicit in both cases.
        MarkCancelled(postId);
        return completed || IsCancelled(postId);
    }

    private bool IsCancelled(string postId)
    {
        _outcomes.TryGetValue(postId, out BooruOriginalDownloadOutcome? outcome);
        return outcome is { State: BooruOriginalDownloadState.Cancelled };
    }

    private async Task<BooruOriginalAdmission> AdmitRecordAsync(BooruPostRecord record, CancellationToken cancellationToken)
    {
        string postId = record.PostId;
        if (record.LocalAssetId != null)
        {
            BooruOriginalDownloadOutcome linked = new(BooruOriginalDownloadState.Linked, postId,
                AssetId: record.LocalAssetId, LocalPath: record.LocalPath);
            return new(BooruOriginalAdmissionStatus.AlreadyLinked, postId, Task.FromResult(linked));
        }
        if (!TryPlan(record.Post, out BooruOriginalDownloadPlan? plan, out BooruOriginalSelectionError error))
        {
            _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                BooruOriginalIntent.Unavailable, cancellationToken);
            BooruOriginalDownloadOutcome unavailable = new(BooruOriginalDownloadState.Unavailable, postId, error);
            Publish(unavailable);
            return new(BooruOriginalAdmissionStatus.Unavailable, postId, Task.FromResult(unavailable), error);
        }

        PostOwner owner;
        lock (_sync)
        {
            if (_active.TryGetValue(postId, out PostOwner? existing))
                return new(BooruOriginalAdmissionStatus.AlreadyQueued, postId, existing.Completion);
            owner = new PostOwner(this, _provider, plan!);
            _active.Add(postId, owner);
            _outcomes[postId] = new(BooruOriginalDownloadState.Queued, postId);
        }
        RaiseOutcome(new(BooruOriginalDownloadState.Queued, postId));

        bool accepted;
        try
        {
            accepted = await _queue.EnqueueAsync(owner.CreateItem(_outputRoot, _catalog.ProfileLease), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                BooruOriginalIntent.NotAdmitted, CancellationToken.None);
            Complete(owner, new(BooruOriginalDownloadState.NotAdmitted, postId, Failure: exception.Message));
            throw;
        }
        if (!accepted)
        {
            // A refusal is terminal until the user asks again; it must not look pending after a restart.
            _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                BooruOriginalIntent.NotAdmitted, CancellationToken.None);
            Complete(owner, new(BooruOriginalDownloadState.NotAdmitted, postId, Failure: "The download queue refused the original."));
            return new(BooruOriginalAdmissionStatus.NotAdmitted, postId, owner.Completion);
        }
        // Only a genuinely accepted queue job is a durable, recoverable request.
        _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
            BooruOriginalIntent.Requested, CancellationToken.None);
        return new(BooruOriginalAdmissionStatus.Queued, postId, owner.Completion);
    }

    private bool TryPlan(BooruPostClaim post, out BooruOriginalDownloadPlan? plan, out BooruOriginalSelectionError error)
    {
        if (!BooruOriginalDownloadSelector.TrySelect(post, out plan, out error)) return false;
        if (plan!.ExpectedSize is null || plan.ExpectedMd5 is null)
            error = BooruOriginalSelectionError.MissingIntegrityClaims;
        else if (!_allowedMediaHosts.Contains(plan.DownloadUri.IdnHost))
            error = BooruOriginalSelectionError.MediaHostNotAllowed;
        else return true;
        plan = null;
        return false;
    }

    internal void MarkCancelled(string postId)
    {
        // Idempotent: the queue may report the cancellation and a local Complete may follow.
        try
        {
            _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                BooruOriginalIntent.Cancelled, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The post record is gone (profile closed); the in-memory outcome already says Cancelled.
        }
    }

    private void OnCommitted(PostOwner owner, MediaCommitResult committed, ProfileAdmissionToken? admission)
    {
        string postId = owner.Plan.PostId;
        BooruOriginalDownloadOutcome outcome;
        try
        {
            if (committed.AssetId is not { Length: > 0 } assetId || !MediaIntegrityValidation.IsSha256(committed.Sha256)
                || committed.Length != owner.Plan.ExpectedSize)
                throw new InvalidDataException("The media commit lacks an exact verified asset receipt.");
            // The queue job's own Ordinary producer admission is the only write authority here.
            ProfileAdmissionToken token = admission
                ?? throw new InvalidOperationException("A Booru catalog link requires the queue job's profile admission.");
            LinkingForTests?.Invoke(postId, assetId);
            BooruPostRecord linked = _catalog.LinkLocalAssetAdmitted(DanbooruPostReference.ProviderId, postId,
                assetId, committed.Path, token);
            outcome = new(BooruOriginalDownloadState.Linked, postId, AssetId: linked.LocalAssetId,
                LocalPath: linked.LocalPath, Sha256: committed.Sha256, Length: committed.Length);
        }
        catch (Exception exception)
        {
            outcome = new(BooruOriginalDownloadState.LinkFailed, postId, AssetId: committed.AssetId,
                LocalPath: committed.Path, Sha256: committed.Sha256, Length: committed.Length, Failure: exception.Message);
        }
        Complete(owner, outcome);
    }

    private void OnFailed(PostOwner owner, Exception exception) => Complete(owner,
        new(BooruOriginalDownloadState.DownloadFailed, owner.Plan.PostId, Failure: exception.Message));

    private bool Complete(PostOwner owner, BooruOriginalDownloadOutcome outcome)
    {
        if (!owner.TryComplete(outcome)) return false;
        lock (_sync)
        {
            if (_active.TryGetValue(owner.Plan.PostId, out PostOwner? current) && ReferenceEquals(current, owner))
                _active.Remove(owner.Plan.PostId);
            _outcomes[owner.Plan.PostId] = outcome;
        }
        RaiseOutcome(outcome);
        return true;
    }

    private void Publish(BooruOriginalDownloadOutcome outcome)
    {
        lock (_sync)
        {
            // An unavailable re-read never hides a concurrently active job for the same post.
            if (_active.ContainsKey(outcome.PostId)) return;
            _outcomes[outcome.PostId] = outcome;
        }
        RaiseOutcome(outcome);
    }

    private void RaiseOutcome(BooruOriginalDownloadOutcome outcome)
    {
        try { OutcomeChanged?.Invoke(outcome); }
        catch (Exception) { /* Observers cannot change the admission or download result. */ }
    }

    /// <summary>
    /// Runtime-only queue job owner for exactly one provider post. It implements the queue's owner
    /// contract, but it is not a chan thread: it never claims a URL, cannot be monitored or checked and
    /// is never given to the Registry, the session, the work journal or the archive.
    /// </summary>
    private sealed class PostOwner : IThread, IExternalMediaWorkOwner
    {
        private readonly BooruOriginalQueue _owner;
        private readonly TaskCompletionSource<BooruOriginalDownloadOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _committed;

        internal PostOwner(BooruOriginalQueue owner, OwnerProvider provider, BooruOriginalDownloadPlan plan)
        {
            _owner = owner;
            Plan = plan;
            Board = provider.Board;
            ThreadUri = new Uri($"https://{DanbooruPostReference.MetadataHost}/posts/{plan.PostId}", UriKind.Absolute);
        }

        internal BooruOriginalDownloadPlan Plan { get; }
        internal Task<BooruOriginalDownloadOutcome> Completion => _completion.Task;
        internal bool TryComplete(BooruOriginalDownloadOutcome outcome) => _completion.TrySetResult(outcome);

        internal DownloadItem CreateItem(string outputRoot, ProfileLease lease)
        {
            string directory = Path.Combine(outputRoot, ProviderDirectoryName, Plan.PostId);
            string destination = Path.Combine(directory, Plan.PostId + Plan.Extension);
            return new DownloadItem(Plan.DownloadUri, destination, Board.ImageBoard, this, Plan.MediaIdentifier,
                Plan.ExpectedSize, Plan.ExpectedMd5, originalFileName: null, legacyPath: null, BooruOriginalDownloadPlan.Variant)
            {
                // The catalog's exact lease is the consumer identity; a different queue profile fails closed.
                ContentProfileLease = lease
            };
        }

        void IExternalMediaWorkOwner.OnMediaCommitted(DownloadItem item, MediaCommitResult committed,
            ProfileAdmissionToken? admission)
        {
            if (!ReferenceEquals(item.Thread, this) || item.MediaIdentifier != Plan.MediaIdentifier
                || item.Variant != BooruOriginalDownloadPlan.Variant) return;
            // Exactly one link per owner, even if a queue path ever reported twice.
            if (Interlocked.Exchange(ref _committed, 1) != 0) return;
            _owner.OnCommitted(this, committed, admission);
        }

        public void ReportMediaFailure(string mediaIdentifier, Exception exception, string? destinationPath = null)
        {
            if (mediaIdentifier == Plan.MediaIdentifier && Volatile.Read(ref _committed) == 0) _owner.OnFailed(this, exception);
        }

        void IExternalMediaWorkOwner.OnMediaCancelled(DownloadItem item)
        {
            if (!ReferenceEquals(item.Thread, this) || item.MediaIdentifier != Plan.MediaIdentifier) return;
            // A cancelled job is never a success and never a pending original after a restart.
            // Pause/interruption re-queues the same job and reports no cancellation, so only a
            // genuinely terminal cancellation reaches this path.
            if (_owner.Complete(this, new BooruOriginalDownloadOutcome(BooruOriginalDownloadState.Cancelled, Plan.PostId)))
                _owner.MarkCancelled(Plan.PostId);
        }

        public IBoard Board { get; }
        public Uri ThreadUri { get; }
        public string ThreadId => Plan.PostId;
        public string Url => ThreadUri.AbsoluteUri;
        public string Title { get => $"Danbooru #{Plan.PostId}"; set { } }
        public string NiceName => Title;
        public string DownloadDirectory => string.Empty;
        public ThreadSourceState SourceState => ThreadSourceState.Active;
        public ThreadActivity Activity { get => ThreadActivity.Downloading; set { } }
        public FailureInfo? LastCheckFailure => null;
        public RequestPolicyProgress? RequestProgress => null;
        public IReadOnlyDictionary<string, string> MediaFailures => new Dictionary<string, string>();
        public string Status => string.Empty;
        public string Progress => string.Empty;
        public int TotalMediaCount => 1;
        public int DownloadedMediaCount => 0;
        public HashSet<string> DownloadedMedia { get => new(); set { } }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsMediaDownloaded(string mediaIdentifier) => false;
        public void MarkMediaAsDownloaded(string mediaIdentifier, string? destinationPath = null) { }
        public void MarkMediaPending(string mediaIdentifier, string? destinationPath = null) { }
        public IDisposable RegisterRemovalSink(Action<IThread> sink) => throw NotAChanThread();
        public ThreadSnapshot GetSnapshot(DownloadCounts downloads) => throw NotAChanThread();
        public void StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval) => throw NotAChanThread();
        public void UpdateMonitoringInterval(TimeSpan interval) => throw NotAChanThread();
        public Task StopMonitoringAsync() => Task.CompletedTask;
        public Task EnqueueMediaDownloadsAsync(DownloadQueue queue, CancellationToken cancellationToken = default) => throw NotAChanThread();
        public Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default) => throw NotAChanThread();
        public void NotifyThreadRemoval(bool abort) { }
    }

    private static NotSupportedException NotAChanThread() =>
        new("A Booru original download owner is not a chan thread and cannot be watched or registered.");

    /// <summary>Provider identity for queue owners; it never resolves or claims a URL.</summary>
    private sealed class OwnerProvider : IImageBoard
    {
        private readonly HttpClient _client;
        internal OwnerProvider(HttpClient client)
        {
            _client = client;
            Board = new OwnerBoard(this);
        }
        internal OwnerBoard Board { get; }
        public string Name => DanbooruPostReference.ProviderId;
        public string NiceName => "Danbooru";
        public Uri BaseUri { get; } = new($"https://{DanbooruPostReference.MetadataHost}/", UriKind.Absolute);
        public bool CanHandle(Uri uri) => false;
        public bool IsThreadUri(Uri uri) => false;
        public bool IsBoardUri(Uri uri) => false;
        public IBoard GetBoard(Uri boardUri) => throw NotAChanThread();
        public IThread GetThread(Uri threadUri) => throw NotAChanThread();
        public HttpClient GetHttpClient() => _client;
    }

    private sealed class OwnerBoard(OwnerProvider provider) : IBoard
    {
        public IImageBoard ImageBoard => provider;
        public string BoardCode => PostsBoardCode;
        public string Name => PostsBoardCode;
        public string NiceName => "Danbooru posts";
        public Uri BoardUri { get; } = new($"https://{DanbooruPostReference.MetadataHost}/posts", UriKind.Absolute);
        public RequestPolicyProgress? RequestProgress => null;
        public FailureInfo? LastCheckFailure => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default) => throw NotAChanThread();
        public void StartMonitoring(int intervalInSeconds) => throw NotAChanThread();
        public void UpdateMonitoringInterval(TimeSpan interval) => throw NotAChanThread();
        public void StopMonitoring() { }
        public Task StopMonitoringAsync() => Task.CompletedTask;
        public IDisposable RegisterDiscoverySink(Func<IThread, CancellationToken, ValueTask> sink) => throw NotAChanThread();
        public BoardSnapshot GetSnapshot(string downloadDirectory) => throw NotAChanThread();
    }
}
