using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using oChan.Boards.Base;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;
using oChan.Rules;
using oChan.Storage;

namespace oChan.Archive;

/// <summary>Optional profile-owned content services. The same contracts serve desktop and library consumers.</summary>
public sealed class ThreadContentRuntime : IAsyncDisposable
{
    private readonly string _profileDirectory;
    private readonly ProfileLease _lease;
    private ArchiveCatalog? _catalog;
    public MediaRuleStore Rules { get; }
    public ArchiveCatalog Catalog => _catalog ?? throw new InvalidOperationException("Initialize content services before starting watches.");
    public string? LoadWarning { get; private set; }

    public ThreadContentRuntime(string profileDirectory, ProfileLease lease)
    {
        _profileDirectory = profileDirectory;
        _lease = lease;
        Rules = new MediaRuleStore(profileDirectory, lease);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog != null) return;
        MediaRuleLoadResult rules = await Rules.LoadAsync(cancellationToken).ConfigureAwait(false);
        LoadWarning = rules.Warning;
        _catalog = await ArchiveCatalog.OpenAsync(_profileDirectory, _lease, cancellationToken).ConfigureAwait(false);
    }

    internal async Task BindAsync(BaseThread thread, string outputRoot, bool restored, CancellationToken cancellationToken = default)
    {
        ResourceId id = new ResourceId(thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);
        string directory = StorageLayout.GetThreadDirectory(outputRoot, id.Source, id.Board, id.Thread!);
        ArchiveThreadSummary summary = await Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(id, thread.Url,
            thread.Title, directory, !restored), cancellationToken).ConfigureAwait(false);
        thread.AttachContent(new ThreadContentContext(this, summary));
    }

    public Task<ArchiveReader> OpenReaderAsync(CancellationToken cancellationToken = default) =>
        ArchiveReader.OpenAsync(_profileDirectory, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_catalog != null) await _catalog.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A watch's awaited capture/selection path. Source polling owns these calls; no persistence listeners.</summary>
internal sealed class ThreadContentContext
{
    private readonly ThreadContentRuntime _runtime;
    private readonly ResourceId _id;
    private readonly string _directory;
    private CapturedThreadSnapshot? _pending;
    private CapturedThreadSnapshot? _latest;
    private readonly SemaphoreSlim _captureGate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, CandidateDecision> _decisions = new Dictionary<string, CandidateDecision>(StringComparer.Ordinal);
    private bool _enabled;
    private DateTimeOffset? _capturedAt;
    private FailureInfo? _captureFailure;
    private FailureInfo? _selectionFailure;
    private FailureInfo? _sourceFailure;
    private ThreadSourceState? _sourceStateToSave;
    private ThreadSourceState? _lastObservedSourceState;
    private int _excluded;
    private readonly object _state = new object();
    private HashSet<string>? _previouslySaved;

    internal ThreadContentContext(ThreadContentRuntime runtime, ArchiveThreadSummary summary)
    {
        _runtime = runtime;
        _id = summary.Id;
        _directory = summary.MediaDirectory;
        _enabled = summary.CaptureEnabled;
        _capturedAt = summary.LastCapturedAt;
    }

    internal ThreadContentStatus Snapshot
    {
        get { lock (_state) return new ThreadContentStatus(_enabled, _capturedAt, _captureFailure ?? _sourceFailure, _selectionFailure, _excluded); }
    }
    internal bool NeedsRetry { get { lock (_state) return _captureFailure != null || _sourceFailure != null || _selectionFailure != null; } }

    internal async Task RecordSourceStateAsync(ThreadSourceState state, CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastObservedSourceState = state;
            _sourceStateToSave = state;
            await SaveSourceStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    private async Task SaveSourceStateAsync(CancellationToken cancellationToken)
    {
        if (_sourceStateToSave is not ThreadSourceState state) return;
        try
        {
            await _runtime.Catalog.UpdateSourceStateAsync(_id, state, cancellationToken).ConfigureAwait(false);
            _sourceStateToSave = null;
            lock (_state) _sourceFailure = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { lock (_state) _sourceFailure = new FailureInfo(FailureKind.Storage, exception.Message); }
    }

    internal async Task SetCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _runtime.Catalog.SetCaptureEnabledAsync(_id, enabled, cancellationToken).ConfigureAwait(false);
        lock (_state) { _enabled = enabled; }
        if (enabled) await CaptureAsync(null, cancellationToken, forceLatest: true).ConfigureAwait(false);
    }

    internal async Task CaptureAsync(CapturedThreadSnapshot? observed, CancellationToken cancellationToken, bool forceLatest = false)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        bool enabled;
        lock (_state) enabled = _enabled;
        if (observed != null) { _latest = observed; _lastObservedSourceState = observed.SourceState; }
        observed ??= enabled && (forceLatest || _capturedAt == null) ? _latest : null;
        if (enabled && observed != null)
            _pending = _pending == null ? observed : ArchiveCaptureMerge.Merge(_pending, observed);
        if (_pending == null) { await SaveSourceStateAsync(cancellationToken).ConfigureAwait(false); return; }
        try
        {
            await _runtime.Catalog.CaptureAsync(_pending with { SourceState = _lastObservedSourceState ?? _pending.SourceState }, cancellationToken).ConfigureAwait(false);
            lock (_state) { _capturedAt = _pending.CapturedAt; _captureFailure = null; }
            _pending = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (_state) _captureFailure = new FailureInfo(FailureKind.Storage, exception.Message);
        }
        await SaveSourceStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    internal async Task<IReadOnlyList<DownloadItem>> RestoreCandidatesAsync(BaseThread owner, CancellationToken cancellationToken)
    {
        IReadOnlyList<CandidateDecision> decisions = await _runtime.Catalog.GetDecisionsAsync(_id, cancellationToken).ConfigureAwait(false);
        foreach (CandidateDecision decision in decisions) _decisions[decision.Metadata.Key.MediaId] = decision;
        return Array.AsReadOnly(decisions.Select(decision => ToItem(decision.Metadata, owner)).ToArray());
    }

    internal async Task<IReadOnlyList<DownloadItem>?> SelectAsync(BaseThread owner, IEnumerable<DownloadItem> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_previouslySaved == null)
            {
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(_directory, cancellationToken).ConfigureAwait(false);
                _previouslySaved = new HashSet<string>(StringComparer.Ordinal);
                for (int offset = 0; offset < manifest.Count; offset += 500)
                    _previouslySaved.UnionWith(manifest.GetPage(offset, 500).Select(asset => asset.MediaIdentifier));
            }
            List<DownloadItem> selected = new List<DownloadItem>();
            int excluded = 0;
            foreach (DownloadItem item in candidates)
            {
                CandidateMetadata metadata = new CandidateMetadata(new CandidateKey(_id, item.MediaIdentifier), item.DownloadUri,
                    item.DestinationPath, item.ExpectedSize, item.ExpectedMd5, item.OriginalFileName, item.LegacyPath,
                    Path.GetExtension(item.OriginalFileName ?? item.DestinationPath));
                if (!_decisions.TryGetValue(item.MediaIdentifier, out CandidateDecision? decision)
                    || !SameMetadata(decision.Metadata, metadata))
                {
                    decision = await _runtime.Catalog.GetOrCreateDecisionAsync(metadata, _runtime.Rules,
                        _previouslySaved.Contains(item.MediaIdentifier) || owner.IsMediaDownloaded(item.MediaIdentifier), cancellationToken).ConfigureAwait(false);
                    _decisions[item.MediaIdentifier] = decision;
                }
                if (decision.Selection.Outcome == MediaSelectionOutcome.Selected) selected.Add(ToItem(decision.Metadata, owner));
                else excluded++;
            }
            lock (_state) { _excluded = excluded; _selectionFailure = null; }
            return selected.AsReadOnly();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (_state) _selectionFailure = new FailureInfo(FailureKind.Storage, exception.Message);
            return null;
        }
    }

    private static DownloadItem ToItem(CandidateMetadata metadata, BaseThread owner) => new DownloadItem(metadata.DownloadUri,
        metadata.DestinationPath, owner.Board.ImageBoard, owner, metadata.Key.MediaId, metadata.ExpectedSize,
        metadata.ExpectedMd5, metadata.OriginalFileName, metadata.LegacyPath);

    private static bool SameMetadata(CandidateMetadata first, CandidateMetadata second) => first.DownloadUri == second.DownloadUri
        && first.ExpectedSize == second.ExpectedSize && first.ExpectedMd5 == second.ExpectedMd5
        && first.OriginalFileName == second.OriginalFileName;
}

public sealed record ThreadContentStatus(bool CaptureEnabled, DateTimeOffset? LastCapturedAt,
    FailureInfo? CaptureFailure, FailureInfo? SelectionFailure, int ExcludedMedia);
