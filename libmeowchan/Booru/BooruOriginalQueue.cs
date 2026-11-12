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
    /// <summary>The user cancelled the original. Durable and terminal: restart recovery never re-admits it.</summary>
    Cancelled,
    Unavailable,
    NotAdmitted,
    /// <summary>
    /// The queue stopped (shutdown, profile close) before the original completed. This is not a
    /// cancellation: the durable request is kept and <see cref="BooruOriginalQueue.RecoverPendingAsync"/>
    /// re-admits it after a restart.
    /// </summary>
    Interrupted
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
/// stores the provider claim and its finding (provenance only); a plannable explicit request is then
/// recorded as <see cref="BooruOriginalIntent.Requested"/> (write-ahead, before it is visible as queued or
/// offered to the queue), the single pending state that restart recovery re-admits. A queue refusal replaces
/// it with <see cref="BooruOriginalIntent.NotAdmitted"/> before the admission returns, so only a request the
/// queue accepted, or one whose admission a process crash interrupted, stays pending. A user cancel is
/// recorded as <see cref="BooruOriginalIntent.Cancelled"/> before <see cref="CancelAsync"/> returns; a queue
/// stop keeps the request. Only after the queue reports an atomic <see cref="MediaCommitResult.AssetId"/> is <see cref="BooruCatalogStore.LinkLocalAssetAdmitted"/>
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
    // Serializes the two decisions that read a stored intent and act on it: starting an admission
    // (re-read the record, register the runtime owner) and cancelling a stored request that has no
    // runtime owner. It is held only across short catalog I/O, never across a queue wait or a queue
    // callback, so a queue notification can never wait on it.
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly DownloadQueue _queue;
    private readonly BooruCatalogStore _catalog;
    private readonly string _outputRoot;
    private readonly HashSet<string> _allowedMediaHosts;
    private readonly TimeProvider _timeProvider;
    private readonly OwnerProvider _provider;
    private readonly Dictionary<string, PostOwner> _active = new(StringComparer.Ordinal);
    // Accepted queue work whose acknowledgement (RecordAccepted) has not completed yet. Requested is already
    // durable (write-ahead), so this only keeps the owner visible for AlreadyQueued and routes a cancel of a
    // fast, already finished job through the durable stored-cancel path.
    private readonly Dictionary<string, PostOwner> _acceptedAwaitingIntent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BooruOriginalDownloadOutcome> _outcomes = new(StringComparer.Ordinal);

    private enum AdmissionMode
    {
        /// <summary>A new explicit request (one-shot or search).</summary>
        Request,
        /// <summary>
        /// A watcher finding. Unlike an explicit request it never overrides a user cancellation, even if the
        /// cancel lands between the watcher's own check and this admission.
        /// </summary>
        WatcherRequest,
        /// <summary>An explicit user retry; it overrides a terminal cancelled/refused/unavailable intent.</summary>
        Retry,
        /// <summary>Restart recovery; only a still-Requested record without an asset is admitted.</summary>
        Recover
    }

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

    // Deterministic test hook: runs after the queue accepted a job and before its Requested intent is
    // recorded, i.e. exactly in the window where a cancel or a fast completion can overtake admission.
    internal Func<string, Task>? AfterQueueAcceptedForTests { get; set; }

    /// <summary>The latest in-process state per post; durable success is the catalog link only.</summary>
    internal IReadOnlyDictionary<string, BooruOriginalDownloadOutcome> Snapshot()
    {
        lock (_sync) return new Dictionary<string, BooruOriginalDownloadOutcome>(_outcomes, StringComparer.Ordinal);
    }

    /// <summary>Number of runtime owners that have not reached a terminal outcome yet.</summary>
    internal int ActiveCount
    {
        get { lock (_sync) return _active.Count; }
    }

    /// <summary>
    /// Persists the provider claim and the finding that explains this explicit request, then admits the
    /// original into the shared queue. A one-shot never creates a watcher; a watcher finding never marks
    /// anything but this single post.
    /// <para>
    /// The finding is provenance only and never makes the post pending: the durable
    /// <see cref="BooruOriginalIntent.Requested"/> is written ahead of the queue offer and replaced by
    /// <see cref="BooruOriginalIntent.NotAdmitted"/> if the queue refuses the job.
    /// An unacceptable original is recorded as <see cref="BooruOriginalIntent.Unavailable"/>; the claim
    /// itself is kept so a later, richer provider answer can still be evaluated.
    /// </para>
    /// </summary>
    internal async Task<BooruOriginalAdmission> AdmitAsync(BooruPostClaim post, BooruFindingKind kind,
        string? watcherId = null, string? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        cancellationToken.ThrowIfCancellationRequested();
        AdmissionMode mode = kind == BooruFindingKind.Watcher ? AdmissionMode.WatcherRequest : AdmissionMode.Request;
        Observation observation = new(post, kind, watcherId, query, _timeProvider.GetUtcNow());
        return (await AdmitStoredAsync(post.PostId, mode, cancellationToken, observation).ConfigureAwait(false))!;
    }

    /// <summary>Re-admits a stored post without adding a finding, e.g. after a visible failure or cancel.</summary>
    internal async Task<BooruOriginalAdmission> RetryAsync(string postId, CancellationToken cancellationToken = default)
    {
        if (!DanbooruPostReference.IsValidPostId(postId)) throw new ArgumentException("The Danbooru post id is invalid.", nameof(postId));
        return (await AdmitStoredAsync(postId, AdmissionMode.Retry, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Stored posts whose original was explicitly requested and is still awaiting a committed local asset.
    /// A cancelled, unavailable or queue-refused request is deliberately not pending, so a restart never
    /// silently re-admits work the user did not ask for. A queue stop is not a cancellation: an
    /// interrupted request stays pending.
    /// </summary>
    internal IReadOnlyList<BooruPostRecord> GetPendingOriginals() => _catalog.Read().Posts
        .Where(record => record.HasPendingOriginal).ToArray();

    /// <summary>
    /// Restart recovery: re-admits every pending stored original from local data only. A medium that was
    /// committed before a crash is restored and linked without a network transfer. Each record is re-read
    /// under the admission gate, so a post cancelled or linked after the pending scan is skipped. One
    /// record's admission failure never prevents the others; it keeps its durable request. A cancellation ends
    /// the recovery early but still returns the admissions handed over so far (they keep their queue jobs),
    /// with the record that was being admitted as an interrupted one; the remaining records stay pending.
    /// </summary>
    internal async Task<IReadOnlyList<BooruOriginalAdmission>> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        List<BooruOriginalAdmission> admitted = new();
        foreach (BooruPostRecord record in GetPendingOriginals())
        {
            if (cancellationToken.IsCancellationRequested) break;
            BooruOriginalAdmission? admission;
            try
            {
                admission = await AdmitStoredAsync(record.PostId, AdmissionMode.Recover, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // This record keeps its stored intent; the others are still recovered.
                BooruOriginalDownloadOutcome interrupted = new(BooruOriginalDownloadState.Interrupted, record.PostId,
                    Failure: exception.Message);
                admission = new(BooruOriginalAdmissionStatus.NotAdmitted, record.PostId, Task.FromResult(interrupted));
            }
            if (admission != null) admitted.Add(admission);
        }
        return admitted;
    }

    /// <summary>
    /// Cancels the original of a post. The decision is durable before this returns: the post is recorded as
    /// <see cref="BooruOriginalIntent.Cancelled"/> and is never re-admitted by restart recovery. This holds
    /// for a running, queued or still-admitting job and for a stored request without a runtime owner (for
    /// example after a failed transfer or before recovery ran). A completed link is never turned into a
    /// cancellation. True means a pending original became cancelled by this call.
    /// </summary>
    internal async Task<bool> CancelAsync(string postId)
    {
        if (!DanbooruPostReference.IsValidPostId(postId)) throw new ArgumentException("The Danbooru post id is invalid.", nameof(postId));
        bool cancelled = false;
        while (true)
        {
            PostOwner? owner;
            bool storedCancellation = false;
            await _admissionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    _active.TryGetValue(postId, out owner);
                    if (owner is null) _acceptedAwaitingIntent.TryGetValue(postId, out owner);
                }
                // Without a runtime owner the stored request is cancelled here. An accepted owner that has
                // already finished but is still waiting for RecordAccepted is handled by the same durable
                // path, even though its record may still have OriginalIntent.None.
                if (owner == null)
                    storedCancellation = CancelStoredRequest(postId, acceptedWithoutIntent: false);
                else if (!IsActive(owner) && owner.Completion.IsCompleted)
                    storedCancellation = CancelStoredRequest(postId, acceptedWithoutIntent: true);
                else
                    // Set before the queue cancel: a refused admission or a stop that races with this cancel
                    // then settles as Cancelled instead of NotAdmitted/Interrupted.
                    owner.RequestCancel();
            }
            finally { _admissionGate.Release(); }

            if (owner == null || storedCancellation)
            {
                // Publish only after releasing _admissionGate. Observers are allowed to synchronously issue
                // another admission/cancel and must never deadlock behind this stored cancellation.
                if (storedCancellation)
                    Publish(new BooruOriginalDownloadOutcome(BooruOriginalDownloadState.Cancelled, postId));
                return storedCancellation | cancelled;
            }

            await _queue.CancelDownloadsForThreadAsync(owner).ConfigureAwait(false);
            // The queue normally reported the cancellation already; this is the fallback for an owner it
            // never notified (still admitting, or the job had just finished).
            Settle(owner, new BooruOriginalDownloadOutcome(BooruOriginalDownloadState.Cancelled, postId),
                BooruOriginalIntent.Cancelled);
            BooruOriginalDownloadOutcome final = await owner.Completion.ConfigureAwait(false);
            if (final.State == BooruOriginalDownloadState.Linked) return cancelled;
            cancelled |= final.State == BooruOriginalDownloadState.Cancelled;
            // Otherwise the job reached a non-success state first (failed download or link) and its request
            // may still be durable: the next pass cancels that stored request, or a newer owner.
        }
    }

    private bool IsActive(PostOwner owner)
    {
        lock (_sync)
            return _active.TryGetValue(owner.Plan.PostId, out PostOwner? current) && ReferenceEquals(current, owner);
    }

    /// <summary>Caller holds the admission gate and has verified that no runtime owner is active.</summary>
    private bool CancelStoredRequest(string postId, bool acceptedWithoutIntent)
    {
        BooruPostRecord? record = FindRecord(postId);
        if (record is not { } || record.LocalAssetId is not null
            || record.OriginalIntent == BooruOriginalIntent.Linked)
            return false;
        if (!record.HasPendingOriginal && !acceptedWithoutIntent) return false;
        _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId, BooruOriginalIntent.Cancelled,
            CancellationToken.None);
        return true;
    }

    private BooruPostRecord? FindRecord(string postId) => _catalog.TryGetPost(DanbooruPostReference.ProviderId, postId);

    /// <summary>The claim and finding an explicit request stores in the same write as its intent.</summary>
    private sealed record Observation(BooruPostClaim Post, BooruFindingKind Kind, string? WatcherId, string? Query,
        DateTimeOffset ObservedAtUtc);

    private enum AdmissionStep { AlreadyLinked, AlreadyQueued, NotPending, KeptCancelled, Unavailable, Admit }

    /// <summary>
    /// The admission decision for a stored (or just merged) record. Pure: it reads only its arguments, so it may run
    /// inside the catalog's write lock. A linked record wins over an owner that is just finishing its link.
    /// </summary>
    private AdmissionStep Decide(BooruPostRecord record, AdmissionMode mode, PostOwner? existing,
        out BooruOriginalDownloadPlan? plan, out BooruOriginalSelectionError error)
    {
        plan = null;
        error = default;
        if (record.LocalAssetId != null) return AdmissionStep.AlreadyLinked;
        if (existing != null) return AdmissionStep.AlreadyQueued;
        if (mode == AdmissionMode.Recover && !record.HasPendingOriginal) return AdmissionStep.NotPending;
        // Nothing is written for a kept cancellation: the stored cancellation stays the durable decision.
        if (mode == AdmissionMode.WatcherRequest && record.OriginalIntent == BooruOriginalIntent.Cancelled)
            return AdmissionStep.KeptCancelled;
        // An explicit retry overrides a previously recorded terminal outcome (unavailable/not admitted/cancelled):
        // the plan below records either Unavailable or the write-ahead Requested over it.
        return TryPlan(record.Post, out plan, out error) ? AdmissionStep.Admit : AdmissionStep.Unavailable;
    }

    /// <summary>
    /// Starts one admission from the current stored record. Null only for <see cref="AdmissionMode.Recover"/>
    /// when the record is no longer pending.
    /// </summary>
    private async Task<BooruOriginalAdmission?> AdmitStoredAsync(string postId, AdmissionMode mode,
        CancellationToken cancellationToken, Observation? observation = null)
    {
        PostOwner? owner = null;
        BooruOriginalAdmission? immediate = null;
        BooruOriginalDownloadOutcome? publishAfterGate = null;
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PostOwner? existing;
            lock (_sync)
            {
                if (!_active.TryGetValue(postId, out existing)) _acceptedAwaitingIntent.TryGetValue(postId, out existing);
            }
            AdmissionStep step = AdmissionStep.NotPending;
            BooruOriginalDownloadPlan? plan = null;
            BooruOriginalSelectionError error = default;
            bool wasRequested = false;
            BooruPostRecord record;
            if (observation is { } seen)
            {
                // One durable write for the claim, its finding and the decided intent (write-ahead Requested or
                // Unavailable). The decision runs on the merged record inside the catalog write.
                record = _catalog.RecordObservation(seen.Post, seen.ObservedAtUtc, seen.Kind, seen.WatcherId, seen.Query,
                    merged =>
                    {
                        wasRequested = merged.OriginalIntent == BooruOriginalIntent.Requested;
                        step = Decide(merged, mode, existing, out plan, out error);
                        return step switch
                        {
                            AdmissionStep.Unavailable => BooruOriginalIntent.Unavailable,
                            AdmissionStep.Admit when !wasRequested => BooruOriginalIntent.Requested,
                            _ => null
                        };
                    }, cancellationToken);
            }
            else
            {
                record = FindRecord(postId) ?? throw new InvalidOperationException("The provider post is not stored locally.");
                wasRequested = record.OriginalIntent == BooruOriginalIntent.Requested;
                step = Decide(record, mode, existing, out plan, out error);
                if (step == AdmissionStep.Unavailable)
                    _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                        BooruOriginalIntent.Unavailable, cancellationToken);
                // Write-ahead: the explicit request is durable before it becomes visible as Queued and before
                // the queue may start the transfer. A process crash between the queue accept and the
                // acknowledgement therefore leaves a Requested record that recovery re-admits, exactly like an
                // interrupted request, instead of an accepted (possibly already transferring) job whose request
                // silently vanishes. Every decision this admission still reaches replaces it: a refusal records
                // NotAdmitted, a cancel Cancelled, a link Linked, before the admission or cancel returns.
                else if (step == AdmissionStep.Admit && !wasRequested)
                    _catalog.SetOriginalIntent(DanbooruPostReference.ProviderId, postId,
                        BooruOriginalIntent.Requested, cancellationToken);
            }

            switch (step)
            {
                case AdmissionStep.AlreadyLinked:
                    BooruOriginalDownloadOutcome linked = new(BooruOriginalDownloadState.Linked, postId,
                        AssetId: record.LocalAssetId, LocalPath: record.LocalPath);
                    return new(BooruOriginalAdmissionStatus.AlreadyLinked, postId, Task.FromResult(linked));
                case AdmissionStep.AlreadyQueued:
                    return new(BooruOriginalAdmissionStatus.AlreadyQueued, postId, existing!.Completion);
                case AdmissionStep.NotPending:
                    return null;
                case AdmissionStep.KeptCancelled:
                    BooruOriginalDownloadOutcome kept = new(BooruOriginalDownloadState.Cancelled, postId);
                    return new(BooruOriginalAdmissionStatus.NotAdmitted, postId, Task.FromResult(kept));
                case AdmissionStep.Unavailable:
                    BooruOriginalDownloadOutcome unavailable = new(BooruOriginalDownloadState.Unavailable, postId, error);
                    immediate = new(BooruOriginalAdmissionStatus.Unavailable, postId, Task.FromResult(unavailable), error);
                    // Do not invoke observers while _admissionGate is held; an observer may retry synchronously.
                    publishAfterGate = unavailable;
                    break;
                default:
                    owner = new PostOwner(this, _provider, plan!, wasRequested);
                    lock (_sync)
                    {
                        _active.Add(postId, owner);
                        _outcomes[postId] = new(BooruOriginalDownloadState.Queued, postId);
                    }
                    break;
            }
        }
        finally { _admissionGate.Release(); }
        if (publishAfterGate is not null) Publish(publishAfterGate);
        if (immediate is not null) return immediate;
        if (owner is null) throw new InvalidOperationException("The original admission did not produce an owner.");
        RaiseOutcome(new(BooruOriginalDownloadState.Queued, postId));
        return await EnqueueAsync(owner, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BooruOriginalAdmission> EnqueueAsync(PostOwner owner, CancellationToken cancellationToken)
    {
        string postId = owner.Plan.PostId;
        bool accepted;
        string? failure = null;
        try
        {
            accepted = await _queue.EnqueueAsync(owner.CreateItem(_outputRoot, _catalog.ProfileLease), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller abandoned the admission wait; nothing was admitted.
            SettleRefused(owner, "The original admission was cancelled by its caller.");
            throw;
        }
        catch (Exception exception)
        {
            // A queue that stops during the admission wait may surface that as an exception; it is a
            // refusal like any other and must not escape as a fault of this request.
            accepted = false;
            failure = exception.Message;
        }
        if (!accepted)
        {
            SettleRefused(owner, failure ?? (_queue.IsStopping
                ? "The download queue is stopping."
                : "The download queue refused the original."));
            return new(BooruOriginalAdmissionStatus.NotAdmitted, postId, owner.Completion);
        }

        lock (_sync) _acceptedAwaitingIntent[postId] = owner;
        if (AfterQueueAcceptedForTests is { } hook) await hook(postId).ConfigureAwait(false);
        await RecordAcceptedAsync(owner).ConfigureAwait(false);
        return new(BooruOriginalAdmissionStatus.Queued, postId, owner.Completion);
    }

    /// <summary>
    /// Acknowledges a genuinely accepted queue job: re-asserts the write-ahead Requested. The job may already
    /// have ended while the accept was returning: a cancel or link has recorded its own terminal intent and is
    /// never overwritten, while a failure or stop still leaves the accepted request pending. The re-assert is
    /// only a confirmation: the write-ahead Requested is already durable, so a profile that cannot take a
    /// write now (closing, or an exclusive admission reserved) skips it. The owner always leaves the
    /// accepted-awaiting set; a stale entry would answer every later admission of the post as AlreadyQueued.
    /// </summary>
    private async Task RecordAcceptedAsync(PostOwner owner)
    {
        // Serialize the durable acknowledgement with CancelStoredRequest. Without this fence, a fast failure
        // can remove the owner, a later cancel can persist Cancelled, and this delayed acknowledgement can
        // resurrect the request as Requested after the cancel has returned. The gate is awaited, never blocked on.
        await _admissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using ProfileAdmissionToken? admission = TryEnterStoreWriter();
            if (admission is null) return;
            lock (owner.Gate)
            {
                if (owner.Completion.IsCompleted
                    && owner.Completion.Result.State is BooruOriginalDownloadState.Cancelled or BooruOriginalDownloadState.Linked)
                    return;
                try
                {
                    BooruPostRecord? current = FindRecord(owner.Plan.PostId);
                    if (current is null || current.OriginalIntent is BooruOriginalIntent.Cancelled
                        or BooruOriginalIntent.Linked)
                        return;
                    _catalog.SetOriginalIntentAdmitted(DanbooruPostReference.ProviderId, owner.Plan.PostId,
                        BooruOriginalIntent.Requested, admission);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException
                    or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // The queue already accepted the job and the write-ahead Requested is durable, so a storage
                    // failure of this confirmation must not turn an admitted original into a faulted admission
                    // (InvalidDataException: a full or unreadable catalog).
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_acceptedAwaitingIntent.TryGetValue(owner.Plan.PostId, out PostOwner? current)
                    && ReferenceEquals(current, owner))
                    _acceptedAwaitingIntent.Remove(owner.Plan.PostId);
            }
            _admissionGate.Release();
        }
    }

    /// <summary>The queue did not admit the job (refusal, stop, caller cancellation or failure).</summary>
    private void SettleRefused(PostOwner owner, string failure)
    {
        string postId = owner.Plan.PostId;
        if (owner.CancelRequested)
            Settle(owner, new(BooruOriginalDownloadState.Cancelled, postId), BooruOriginalIntent.Cancelled);
        else if (owner.WasRequested)
            // A recovered or re-requested original that could not be re-admitted (typically a stop during
            // startup) keeps its durable request; it is not a refusal of a new request.
            Settle(owner, new(BooruOriginalDownloadState.Interrupted, postId, Failure: failure), durable: null);
        else
            // A refusal is terminal until the user asks again; it must not look pending after a restart, so it
            // replaces the write-ahead Requested. Only a profile that is already closing cannot take this write;
            // the request then stays pending like one interrupted by a crash.
            Settle(owner, new(BooruOriginalDownloadState.NotAdmitted, postId, Failure: failure), BooruOriginalIntent.NotAdmitted);
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

    private void OnCommitted(PostOwner owner, MediaCommitResult committed, ProfileAdmissionToken? admission)
    {
        string postId = owner.Plan.PostId;
        BooruOriginalDownloadOutcome outcome;
        bool completed;
        lock (owner.Gate)
        {
            try
            {
                if (committed.AssetId is not { Length: > 0 } assetId || !MediaIntegrityValidation.IsSha256(committed.Sha256)
                    || committed.Length != owner.Plan.ExpectedSize)
                    throw new InvalidDataException("The media commit lacks an exact verified asset receipt.");
                // The queue job's own Ordinary producer admission is the only write authority here.
                ProfileAdmissionToken token = admission
                    ?? throw new InvalidOperationException("A Booru catalog link requires the queue job's profile admission.");
                LinkingForTests?.Invoke(postId, assetId);
                // A verified committed file is linked even if a cancel overtook the commit: the media exists
                // and an unlinked asset would only be relinked by a later request.
                BooruPostRecord linked = _catalog.LinkLocalAssetAdmitted(DanbooruPostReference.ProviderId, postId,
                    assetId, committed.Path, token);
                outcome = new(BooruOriginalDownloadState.Linked, postId, AssetId: linked.LocalAssetId,
                    LocalPath: linked.LocalPath, Sha256: committed.Sha256, Length: committed.Length);
            }
            catch (Exception exception)
            {
                // The durable request stays Requested: recovery finds the committed file and relinks it.
                outcome = new(BooruOriginalDownloadState.LinkFailed, postId, AssetId: committed.AssetId,
                    LocalPath: committed.Path, Sha256: committed.Sha256, Length: committed.Length, Failure: exception.Message);
            }
            completed = owner.TryComplete(outcome);
        }
        if (completed) Finish(owner, outcome);
        else if (outcome.State == BooruOriginalDownloadState.Linked) Publish(outcome);
    }

    private void OnFailed(PostOwner owner, Exception exception) => Settle(owner,
        // A failed transfer keeps the durable request: a retry or restart recovery may try again.
        new(BooruOriginalDownloadState.DownloadFailed, owner.Plan.PostId, Failure: exception.Message), durable: null);

    private void OnCancelled(PostOwner owner, ExternalMediaCancellation reason, ProfileAdmissionToken? admission)
    {
        string postId = owner.Plan.PostId;
        if (reason == ExternalMediaCancellation.Cancelled || owner.CancelRequested)
            // A cancelled job is never a success and never a pending original after a restart.
            Settle(owner, new(BooruOriginalDownloadState.Cancelled, postId), BooruOriginalIntent.Cancelled, admission);
        else
            // A queue stop is not a user decision: the accepted request stays durable for recovery.
            Settle(owner, new(BooruOriginalDownloadState.Interrupted, postId,
                Failure: "The download queue stopped before the original completed."), durable: null);
    }

    /// <summary>
    /// Brings an owner to its single terminal outcome and records the matching durable intent under the
    /// owner's gate, so concurrent terminal events and the post-accept Requested write are ordered. A
    /// <paramref name="durable"/> of null keeps the stored intent. The profile token is entered before the
    /// gate; entering never blocks, so the gate is never held while waiting for admission.
    /// </summary>
    private bool Settle(PostOwner owner, BooruOriginalDownloadOutcome outcome, BooruOriginalIntent? durable,
        ProfileAdmissionToken? admission = null)
    {
        if (owner.Completion.IsCompleted) return false;
        ProfileAdmissionToken? entered = null;
        bool completed;
        try
        {
            if (durable != null && admission == null) entered = TryEnterStoreWriter();
            lock (owner.Gate)
            {
                if (owner.Completion.IsCompleted) return false;
                if (durable is BooruOriginalIntent intent && (admission ?? entered) is { } token)
                    TryWriteIntent(owner.Plan.PostId, intent, token);
                completed = owner.TryComplete(outcome);
            }
        }
        finally { entered?.Dispose(); }
        if (completed) Finish(owner, outcome);
        return completed;
    }

    private ProfileAdmissionToken? TryEnterStoreWriter()
    {
        try { return _catalog.ProfileLease.Admission.EnterStoreWriter(); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The profile is closing; the in-memory outcome is still terminal.
            return null;
        }
    }

    private void TryWriteIntent(string postId, BooruOriginalIntent intent, ProfileAdmissionToken admission)
    {
        try
        {
            _catalog.SetOriginalIntentAdmitted(DanbooruPostReference.ProviderId, postId, intent, admission);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException
            or IOException or UnauthorizedAccessException)
        {
            // The post record or profile is gone; the in-memory outcome is still terminal.
        }
    }

    private void Finish(PostOwner owner, BooruOriginalDownloadOutcome outcome)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(owner.Plan.PostId, out PostOwner? current) && ReferenceEquals(current, owner))
                _active.Remove(owner.Plan.PostId);
            else if (current != null) return; // A newer owner for the same post owns the visible state.
            _outcomes[owner.Plan.PostId] = outcome;
        }
        RaiseOutcome(outcome);
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
        private volatile bool _cancelRequested;

        internal PostOwner(BooruOriginalQueue owner, OwnerProvider provider, BooruOriginalDownloadPlan plan, bool wasRequested)
        {
            _owner = owner;
            Plan = plan;
            WasRequested = wasRequested;
            Board = provider.Board;
            ThreadUri = new Uri($"https://{DanbooruPostReference.MetadataHost}/posts/{plan.PostId}", UriKind.Absolute);
        }

        internal BooruOriginalDownloadPlan Plan { get; }
        /// <summary>Orders terminal outcomes and their durable intent writes for this owner.</summary>
        internal object Gate { get; } = new();
        /// <summary>The stored intent was already Requested when this owner started (recovery or retry).</summary>
        internal bool WasRequested { get; }
        internal bool CancelRequested => _cancelRequested;
        internal void RequestCancel() => _cancelRequested = true;
        internal Task<BooruOriginalDownloadOutcome> Completion => _completion.Task;
        /// <summary>Caller holds <see cref="Gate"/>.</summary>
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

        void IExternalMediaWorkOwner.OnMediaCancelled(DownloadItem item, ExternalMediaCancellation reason,
            ProfileAdmissionToken? admission)
        {
            if (!ReferenceEquals(item.Thread, this) || item.MediaIdentifier != Plan.MediaIdentifier) return;
            // Pause/interruption re-queues the same job and reports nothing, so only a job that left the
            // queue reaches this path. A stop keeps the request; only an owner cancel is terminal.
            if (Volatile.Read(ref _committed) == 0) _owner.OnCancelled(this, reason, admission);
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
