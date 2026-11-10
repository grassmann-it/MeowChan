using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

internal enum NativeTransferImportScope
{
    New, Merge
}
internal enum NativeTransferImportState
{
    Idle, Reading, Ready, Unsupported, Applying, Completed, NeedsReview, Canceled,
    Failed, RecoveryRequired, Closed
}
internal enum NativeTransferImportError
{
    None, InvalidSource, InvalidWorkDirectory, InvalidPackage, UnsupportedPackage,
    PrivateMetadataUnsupported, LimitExceeded, TargetUnavailable, ProfileUnavailable,
    ReadFailure, ApplyFailure, ScopeTargetMismatch
}
internal sealed record NativeTransferImportPlanSummary(ResourceId Resource, bool IsMerge,
    int NewPosts, int UpdatedPosts, int NewAttachments, int Conflicts,
    int EvidenceAdditions = 0, int DerivedDescriptorAdditions = 0, int ContainedDerivedAdditions = 0,
    int ThreadCount = 1, int NewThreads = 0, int MergedThreads = 0);
internal sealed record NativeTransferImportResult(NativeMediaImportCommitStatus Status, int NewThreads,
    int NewPosts, int UpdatedPosts, int NewAttachments, int Conflicts, bool IsReplay,
    int EvidenceAdditions = 0, int DerivedDescriptorAdditions = 0, int ContainedDerivedAdditions = 0,
    int ThreadsApplied = 0, int ThreadsTotal = 0);
internal sealed record NativeTransferImportSnapshot(long Generation, NativeTransferImportState State,
    NativeTransferImportError Error, TransferImportPreview? Inventory,
    NativeTransferImportPlanSummary? Plan, NativeTransferImportResult? Result, bool CleanupRetained)
{
    // Content-free private offer. The full component remains in the owned stage only.
    internal NativeTransferPrivateOffer? PrivateOffer { get; init; }
}

/// <summary>
/// Owns the exact verified ingress bytes from inspection through an explicit host apply.
/// Current is descriptive only; no source path, stage or executable plan escapes it.
/// Public single-thread v4 uses the existing versioned import/merge pipeline. Core/stage
/// unsupported-shape guards remain authoritative; private/multithread import and full
/// apply/consent/conflict UI remain future work, not part of this backend increment.
/// </summary>
internal sealed class NativeTransferImportSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly DesktopSessionHost _host;
    private readonly string _parent;
    private NativeTransferImportSnapshot _current = new(0, NativeTransferImportState.Idle,
        NativeTransferImportError.None, null, null, null, false);
    private NativeTransferImportStage? _stage;
    private NativeTransferPreparedImport? _prepared;
    private string? _directory;
    private Task<NativeTransferImportSnapshot>? _active;
    private CancellationTokenSource? _cancellation;
    private Task? _disposal;
    private bool _closed;
    private bool _retained;
    private bool _set;
    private long _generation;

    internal NativeTransferImportSession(DesktopSessionHost host, string workParentDirectory)
    { _host = host; _parent = workParentDirectory; }

    internal Task<MediaConflictPage> ListMediaConflictsAsync(int offset, int limit, CancellationToken token)
        => _host.ListMediaConflictsAsync(offset, limit, token);
    internal Task<MediaConflictActionOutcome> DecideMediaConflictAsync(string id, MediaConflictUserAction action,
        CancellationToken token) => _host.DecideMediaConflictAsync(id, action, token);

    internal Func<CancellationToken, Task>? BeforeSourceOpenForTests { get; set; }
    internal Func<CancellationToken, Task>? AfterPreparationForTests { get; set; }
    internal Action<string>? BeforeDirectoryCleanupForTests { get; set; }
    internal NativeTransferImportSnapshot Current { get { lock (_sync) return _current; } }

    // Compatibility overload for the existing core/lifetime tests. The production dialog
    // uses the explicit overload below; this legacy entry point retains the old automatic
    // planner only for callers that have not yet selected a scope.
    internal Task<NativeTransferImportSnapshot> InspectAsync(string sourcePath, CancellationToken token = default)
        => InspectAsyncCore(sourcePath, scope: null, token);

    internal Task<NativeTransferImportSnapshot> InspectAsync(string sourcePath, NativeTransferImportScope scope,
        CancellationToken token = default)
        => InspectAsyncCore(sourcePath, scope, token);

    private Task<NativeTransferImportSnapshot> InspectAsyncCore(string sourcePath, NativeTransferImportScope? scope,
        CancellationToken token)
    {
        lock (_sync)
        {
            if (_closed || _active is { IsCompleted: false }) return Task.FromResult(_current);
            long generation = ++_generation;
            _current = new(generation, NativeTransferImportState.Reading, NativeTransferImportError.None,
                null, null, null, _retained);
            var cancellation = _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Regular-file open, hashing, SQLite and fsync are never on the UI dispatcher.
            return _active = Task.Run(() => InspectCoreAsync(sourcePath, scope, generation, cancellation));
        }
    }

    internal Task<NativeTransferImportSnapshot> ApplyAsync(long expectedGeneration, CancellationToken token = default)
    {
        lock (_sync)
        {
            if (!_closed && _current.State == NativeTransferImportState.Applying
                && expectedGeneration == _generation && _active != null) return _active;
            if (_closed || _active is { IsCompleted: false } || _current.State != NativeTransferImportState.Ready
                || expectedGeneration != _generation || (_prepared == null && !_set) || _stage == null)
                return Task.FromResult(_current);
            _current = _current with { State = NativeTransferImportState.Applying, Error = NativeTransferImportError.None };
            var cancellation = _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (_set)
            {
                NativeTransferImportStage setStage = _stage;
                return _active = Task.Run(() => ApplySetCoreAsync(setStage, expectedGeneration, cancellation));
            }
            NativeTransferPreparedImport prepared = _prepared!;
            return _active = Task.Run(() => ApplyCoreAsync(prepared, expectedGeneration, cancellation));
        }
    }

    private async Task<NativeTransferImportSnapshot> InspectCoreAsync(string path, NativeTransferImportScope? scope,
        long generation, CancellationTokenSource cancellation)
    {
        NativeTransferImportState state = NativeTransferImportState.Failed;
        NativeTransferImportError error = NativeTransferImportError.ReadFailure;
        TransferImportPreview? inventory = null;
        NativeTransferImportPlanSummary? summary = null;
        bool keep = false;
        CancellationToken token = cancellation.Token;
        try { await ReadStageAsync().ConfigureAwait(false); }
        catch (NativeTransferStageCanceledException cancelled)
        { _retained |= cancelled.StageRetained; state = NativeTransferImportState.Canceled; error = NativeTransferImportError.None; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { state = NativeTransferImportState.Canceled; error = NativeTransferImportError.None; }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidDataException)
        { error = NativeTransferImportError.InvalidSource; }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        { error = NativeTransferImportError.ProfileUnavailable; }
        catch (Exception) { error = NativeTransferImportError.ReadFailure; }
        finally
        {
            // Publish Ready and choose retention at one linearization boundary. An
            // external cancellation between two checks must not publish Canceled while
            // secretly keeping a live stage. A later Cancel/Dispose joins this task.
            lock (_sync)
            {
                keep &= !_closed && generation == _generation && !token.IsCancellationRequested;
                if (keep)
                    _current = new(generation, state, error, inventory, summary, null, _retained)
                    { PrivateOffer = inventory?.PrivateOffer };
                else if (token.IsCancellationRequested)
                { state = NativeTransferImportState.Canceled; error = NativeTransferImportError.None; }
                if (state == NativeTransferImportState.Canceled) { inventory = null; summary = null; }
            }
            if (!keep)
            {
                await CleanupAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    if (!_closed && generation == _generation)
                        _current = new(generation, state, error, inventory, summary, null, _retained)
                        { PrivateOffer = inventory?.PrivateOffer };
                    else _current = _current with { CleanupRetained = _retained };
                }
            }
            RetireCancellation(cancellation);
        }
        return Current;

        async Task ReadStageAsync()
        {
            await CleanupAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (BeforeSourceOpenForTests is { } before) await before(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            { error = NativeTransferImportError.InvalidSource; return; }
            ArchiveExportMetadataFile.RejectLinks(path);
            using (FileStream source = RegularProfileFile.OpenRead(path))
            {
                try { _directory = CreateWorkDirectory(); }
                catch (Exception) { error = NativeTransferImportError.InvalidWorkDirectory; return; }
                var staged = await NativeTransferImportStage.CreateAsync(source, _directory, token).ConfigureAwait(false);
                _retained |= staged.StageRetained;
                _stage = staged.Stage;
                if (_stage == null)
                {
                    error = Map(staged.Error);
                    if (error == NativeTransferImportError.PrivateMetadataUnsupported) state = NativeTransferImportState.Unsupported;
                    return;
                }
            } // The original source is never consulted again, including during apply.
            // A private package is inspected as a content-free offer. PublicOnly remains
            // the default preparation decision; the selected categories are not applied
            // until an explicit owner-issued consent path is wired through the dialog.
            // Inventory supports public v4 too, but is not apply authority. Preparation
            // below still enforces the core's single-thread and supported-shape gates.
            var preview = NativeTransferImportPreview.Create(_stage.Package, token);
            if (!preview.HasPreview) { error = NativeTransferImportError.InvalidPackage; return; }
            inventory = preview.Preview;
            if (_stage.Package.Catalog.Threads.Length > 1)
            {
                // A selected set is validated per thread against the current target; nothing is applied and no
                // plan is retained, because the first commit changes the target fence. Apply re-prepares each thread.
                var set = await _host.PrepareNativeImportSetAsync(_stage, token).ConfigureAwait(false);
                if (set.Summary == null)
                {
                    error = set.Error == NativeTransferImportError.UnsupportedPackage && _stage.Package.PrivateMetadata != null
                        ? NativeTransferImportError.PrivateMetadataUnsupported : set.Error;
                    state = error is NativeTransferImportError.UnsupportedPackage or NativeTransferImportError.PrivateMetadataUnsupported
                        ? NativeTransferImportState.Unsupported : NativeTransferImportState.Failed;
                    return;
                }
                _set = true; summary = set.Summary;
                if (AfterPreparationForTests is { } afterSet) await afterSet(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                state = NativeTransferImportState.Ready; error = NativeTransferImportError.None; keep = true;
                return;
            }
            var plan = scope is { } selectedScope
                ? await _host.PrepareNativeImportAsync(_stage, selectedScope, token).ConfigureAwait(false)
                : await _host.PrepareNativeImportAsync(_stage, token).ConfigureAwait(false);
            if (plan.Plan == null)
            {
                // A private component is not applied by this path; keep the specific, user-visible reason.
                error = plan.Error == NativeTransferImportError.UnsupportedPackage && _stage.Package.PrivateMetadata != null
                    ? NativeTransferImportError.PrivateMetadataUnsupported : plan.Error;
                state = error is NativeTransferImportError.UnsupportedPackage or NativeTransferImportError.PrivateMetadataUnsupported
                    ? NativeTransferImportState.Unsupported : NativeTransferImportState.Failed;
                return;
            }
            _prepared = plan.Plan;
            summary = plan.Plan.Summary;
            if (AfterPreparationForTests is { } after) await after(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            state = NativeTransferImportState.Ready;
            error = NativeTransferImportError.None;
            keep = true;
        }
    }

    private async Task<NativeTransferImportSnapshot> ApplyCoreAsync(NativeTransferPreparedImport prepared,
        long generation, CancellationTokenSource cancellation)
    {
        NativeTransferImportState state = NativeTransferImportState.Failed;
        NativeTransferImportError error = NativeTransferImportError.ApplyFailure;
        NativeTransferImportResult? result = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var outcome = await _host.ApplyNativeImportAsync(prepared, cancellation.Token).ConfigureAwait(false);
            result = outcome.Result;
            if (outcome.Disposition != ProfileExclusiveDisposition.SafeToResume)
                state = NativeTransferImportState.RecoveryRequired;
            else state = result.Status switch
            {
                NativeMediaImportCommitStatus.Committed => NativeTransferImportState.Completed,
                NativeMediaImportCommitStatus.StalePlan => NativeTransferImportState.NeedsReview,
                NativeMediaImportCommitStatus.Inactive when cancellation.IsCancellationRequested => NativeTransferImportState.Canceled,
                _ => NativeTransferImportState.Failed
            };
            error = state is NativeTransferImportState.Completed or NativeTransferImportState.NeedsReview or NativeTransferImportState.Canceled
                ? NativeTransferImportError.None : NativeTransferImportError.ApplyFailure;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { state = NativeTransferImportState.Canceled; error = NativeTransferImportError.None; }
        catch (Exception) { error = NativeTransferImportError.ProfileUnavailable; }
        finally
        {
            // The actual exclusive consumer (including reconciliation) has unwound before
            // ingress cleanup. A requested cancel never substitutes for that task's result.
            await CleanupAsync().ConfigureAwait(false);
            lock (_sync)
            {
                // A closing session still retains the historical result for its owner.
                _current = _current with { State = _closed ? NativeTransferImportState.Closed : state,
                    Error = error, Result = result, CleanupRetained = _retained };
            }
            RetireCancellation(cancellation);
        }
        return Current;
    }

    // Sequential composition of the unchanged single-thread pipeline. Each thread is prepared just before its
    // own commit (the previous commit invalidates earlier fences). Not atomic across threads: a stop leaves an
    // honest partial receipt, and a re-run is idempotent because applied threads re-prepare as no-addition merges.
    private async Task<NativeTransferImportSnapshot> ApplySetCoreAsync(NativeTransferImportStage stage,
        long generation, CancellationTokenSource cancellation)
    {
        NativeTransferImportState state = NativeTransferImportState.Failed;
        NativeTransferImportError error = NativeTransferImportError.ApplyFailure;
        NativeMediaImportCommitStatus status = NativeMediaImportCommitStatus.Unknown;
        int threads = 0, newThreads = 0, posts = 0, updated = 0, attachments = 0, conflicts = 0, evidence = 0, derived = 0, contained = 0;
        int applied = 0, total = stage.Package.Catalog.Threads.Length;
        bool replay = total > 0;
        NativeTransferImportResult Result() => new(status, newThreads, posts, updated, attachments, conflicts, replay,
            evidence, derived, contained, applied, total);
        try
        {
            foreach (ResourceId resource in stage.Package.Catalog.Threads.Select(thread => thread.Id).ToArray())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                NativeTransferImportStage? slice = stage.CreateThreadSlice(resource);
                if (slice == null) { error = NativeTransferImportError.UnsupportedPackage; state = NativeTransferImportState.Unsupported; break; }
                try
                {
                    (NativeTransferPreparedImport? Plan, NativeTransferImportError Error) prepared;
                    try { prepared = await _host.PrepareNativeImportAsync(slice, cancellation.Token).ConfigureAwait(false); }
                    catch (InvalidDataException) { prepared = (null, NativeTransferImportError.UnsupportedPackage); }
                    if (prepared.Plan == null) { error = prepared.Error; state = NativeTransferImportState.Failed; break; }
                    var outcome = await _host.ApplyNativeImportAsync(prepared.Plan, cancellation.Token).ConfigureAwait(false);
                    NativeTransferImportResult one = outcome.Result;
                    status = one.Status; newThreads += one.NewThreads; posts += one.NewPosts; updated += one.UpdatedPosts;
                    attachments += one.NewAttachments; conflicts += one.Conflicts; evidence += one.EvidenceAdditions;
                    derived += one.DerivedDescriptorAdditions; contained += one.ContainedDerivedAdditions;
                    replay &= one.IsReplay; threads++;
                    if (outcome.Disposition != ProfileExclusiveDisposition.SafeToResume)
                    { state = NativeTransferImportState.RecoveryRequired; break; }
                    if (one.Status == NativeMediaImportCommitStatus.StalePlan)
                    { state = NativeTransferImportState.NeedsReview; error = NativeTransferImportError.None; break; }
                    if (one.Status != NativeMediaImportCommitStatus.Committed) break;
                    applied++;
                }
                finally { await slice.DisposeAsync().ConfigureAwait(false); }
            }
            if (applied == total) { state = NativeTransferImportState.Completed; error = NativeTransferImportError.None; status = NativeMediaImportCommitStatus.Committed; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { state = NativeTransferImportState.Canceled; error = NativeTransferImportError.None; }
        catch (Exception) { error = NativeTransferImportError.ProfileUnavailable; }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _current = _current with { State = _closed ? NativeTransferImportState.Closed : state,
                    Error = error, Result = Result(), CleanupRetained = _retained };
            }
            RetireCancellation(cancellation);
        }
        return Current;
    }

    internal void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_closed || _current.State is not (NativeTransferImportState.Idle or NativeTransferImportState.Reading
                or NativeTransferImportState.Ready or NativeTransferImportState.Applying)) return;
            cancellation = _cancellation;
            if (_current.State == NativeTransferImportState.Applying)
            {
                // Keep Applying until the real reconciled outcome is known.
            }
            else
            {
                _current = _current with { Generation = ++_generation, State = NativeTransferImportState.Canceled, Error = NativeTransferImportError.None, Inventory = null, Plan = null };
                // Chain cleanup even if inspection is handing off Ready concurrently.
                // Its finally may already have decided to retain the stage, but this
                // continuation joins that actual task before revoking its capability.
                Task? previous = _active;
                _active = Task.Run(async () =>
                {
                    if (previous != null) await previous.ConfigureAwait(false);
                    await CleanupAsync().ConfigureAwait(false);
                    return PublishCleanup();
                });
            }
        }
        CancelOwned(cancellation);
    }

    public ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        Task task;
        lock (_sync)
        {
            if (_disposal != null) return new(_disposal);
            _closed = true;
            _current = _current with { Generation = ++_generation, State = NativeTransferImportState.Closed, Plan = null };
            cancellation = _cancellation;
            Task? active = _active;
            task = _disposal = Task.Run(async () =>
            {
                if (active != null) await active.ConfigureAwait(false);
                await CleanupAsync().ConfigureAwait(false);
                PublishCleanup();
            });
        }
        CancelOwned(cancellation);
        return new(task);
    }

    private NativeTransferImportSnapshot PublishCleanup()
    { lock (_sync) return _current = _current with { CleanupRetained = _retained }; }

    private void RetireCancellation(CancellationTokenSource cancellation)
    {
        lock (_sync) if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
        cancellation.Dispose();
    }

    private static void CancelOwned(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* A completed owned operation retired this CTS. */ }
        catch (AggregateException) { /* Observers cannot abort the actual drain. */ }
    }

    private async Task CleanupAsync()
    {
        _prepared = null; _set = false;
        NativeTransferImportStage? stage = _stage;
        _stage = null;
        if (stage != null)
        {
            try { await stage.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { _retained = true; }
            _retained |= stage.StageRetained;
        }
        string? directory = _directory;
        _directory = null;
        if (directory == null) return;
        try
        {
            BeforeDirectoryCleanupForTests?.Invoke(directory);
            ArchiveExportMetadataFile.RejectLinks(directory);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception) { _retained = true; }
    }

    private string CreateWorkDirectory()
    {
        if (string.IsNullOrWhiteSpace(_parent) || !Path.IsPathFullyQualified(_parent)) throw new InvalidDataException();
        ArchiveExportMetadataFile.RejectLinks(_parent);
        if ((File.GetAttributes(_parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.Directory)
            throw new InvalidDataException();
        string directory = Path.Combine(_parent, ".meowchan-native-import-" + Guid.NewGuid().ToString("N"));
        ArchiveExportMetadataFile.RejectLinks(directory);
        if (File.Exists(directory) || Directory.Exists(directory)) throw new InvalidDataException();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static NativeTransferImportError Map(NativeTransferStageError error) => error switch
    {
        NativeTransferStageError.PrivateMetadataNotYetSupported => NativeTransferImportError.PrivateMetadataUnsupported,
        NativeTransferStageError.ContainerLimit => NativeTransferImportError.LimitExceeded,
        NativeTransferStageError.InvalidPackage => NativeTransferImportError.InvalidPackage,
        NativeTransferStageError.InvalidStagingDirectory => NativeTransferImportError.InvalidWorkDirectory,
        _ => NativeTransferImportError.ReadFailure
    };
}
