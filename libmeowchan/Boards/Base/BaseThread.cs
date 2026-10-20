namespace MeowChan.Boards.Base;

using System;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Http;
using MeowChan.Storage;
using MeowChan.Notifications;
using MeowChan.Archive;
using MeowChan.Persistence;
using System.Text.Json;
using Serilog;

/// <summary>
/// Passive thread state. The session explicitly starts and stops its polling lifetime.
/// Property notifications are asynchronous, coalesced invalidations. UI clients must dispatch them.
/// </summary>
public abstract class BaseThread : IThread, ICheckRequestSource
{
    private readonly object _monitoringLock = new object();
    private PollingTrigger _checkTrigger = new PollingTrigger();
    private DownloadQueue? _monitoringQueue;
    private readonly object _stateLock = new object();
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private TimeSpan _monitoringInterval = TimeSpan.FromSeconds(60);
    private HashSet<string> _downloadedMedia = new HashSet<string>(StringComparer.Ordinal);
    private static readonly StringComparer DestinationComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, HashSet<string>> _verifiedDestinations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, FailureInfo>> _destinationFailures = new Dictionary<string, Dictionary<string, FailureInfo>>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mediaFailures = new Dictionary<string, string>(StringComparer.Ordinal);
    private ThreadSourceState _sourceState = ThreadSourceState.Active;
    private RequestPolicyProgress? _requestProgress;
    private string _title = "Unknown";
    private ThreadActivity _activity = ThreadActivity.Pending;
    private FailureInfo? _lastCheckFailure;
    private readonly CoalescedEvent<PropertyChangedEventHandler> _propertyChanges;
    private readonly SingleOwnerCallback<Action<IThread>> _removal = new SingleOwnerCallback<Action<IThread>>();
    private int _totalMediaCount;
    private ThreadContentContext? _content;
    private bool _contentCandidatesRestored;
    private Func<ThreadSourceState, DateTimeOffset, CapturedThreadSnapshot?, Task>? _endSink;
    private Func<Task>? _captureSettledSink;
    private DateTimeOffset? _firstEndObservation;
    private TimeProvider _timeProvider = TimeProvider.System;
    private CapturedThreadSnapshot? _restoredFinalSnapshot;
    /// <summary>Providers opt in when their poll path supplies captured posts and uses durable selection.</summary>
    public virtual bool SupportsContent => false;

    protected BaseThread()
    {
        _propertyChanges = new CoalescedEvent<PropertyChangedEventHandler>(
            handler => handler(this, new PropertyChangedEventArgs(string.Empty)),
            exception => Logger.Error(exception, "Thread property observer failed for {ThreadId}", ThreadId));
    }

    protected ILogger Logger => Board.ImageBoard.Logger;
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _propertyChanges.Add(value);
        remove => _propertyChanges.Remove(value);
    }
    public IDisposable RegisterRemovalSink(Action<IThread> sink) => _removal.Register(sink);
    internal void SetLifecycleSinks(Func<ThreadSourceState, DateTimeOffset, CapturedThreadSnapshot?, Task> endSink, Func<Task> captureSettledSink)
    {
        _endSink = endSink;
        _captureSettledSink = captureSettledSink;
    }
    internal void SetTimeProvider(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal void RestoreDurableState(WorkResource resource)
    {
        OutputDirectory = resource.OutputRoot;
        _firstEndObservation = resource.EndObservedAt;
        if (resource.FinalSnapshotJson != null)
            _restoredFinalSnapshot = JsonSerializer.Deserialize<CapturedThreadSnapshot>(resource.FinalSnapshotJson)
                ?? throw new InvalidDataException("The durable final snapshot contains null.");
        if (resource.Source != ThreadSourceStateValue.Active)
            MarkPollingTerminal(resource.Source == ThreadSourceStateValue.Archived ? ThreadSourceState.Archived : ThreadSourceState.NotFound);
    }

    internal void RestoreDurableMedia(IReadOnlyList<MediaWork> media)
    {
        lock (_stateLock)
        {
            _totalMediaCount = Math.Max(_totalMediaCount, media.Select(item => item.MediaIdentifier).Distinct(StringComparer.Ordinal).Count());
            foreach (MediaWork item in media)
            {
                if (item.Status == MediaWorkStatus.Succeeded) _downloadedMedia.Add(item.MediaIdentifier);
                else if (item.Failure != null)
                {
                    if (!_destinationFailures.TryGetValue(item.MediaIdentifier, out Dictionary<string, FailureInfo>? failures))
                        _destinationFailures[item.MediaIdentifier] = failures = new Dictionary<string, FailureInfo>(DestinationComparer);
                    failures[item.DestinationPath] = new FailureInfo(item.HttpStatus != null ? FailureKind.Http : FailureKind.Unknown,
                        item.Failure, item.HttpStatus is int status ? (System.Net.HttpStatusCode)status : null);
                    RefreshMediaFailureLocked(item.MediaIdentifier);
                }
            }
        }
        OnPropertyChanged();
    }
    public FailureInfo? LastCheckFailure { get { lock (_stateLock) { return _lastCheckFailure; } } }

    public ThreadSnapshot GetSnapshot(DownloadCounts downloads)
    {
        lock (_stateLock)
        {
            Dictionary<string, FailureInfo> failures = new Dictionary<string, FailureInfo>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, FailureInfo>> entry in _destinationFailures)
                failures.Add(entry.Key, entry.Value.Values.Last());
            return new ThreadSnapshot(new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId),
                Url, _title, DownloadDirectory, _activity, _sourceState, _requestProgress,
                _totalMediaCount, _downloadedMedia.Count,
                new ReadOnlyDictionary<string, FailureInfo>(failures), _lastCheckFailure, downloads) { Content = _content?.Snapshot };
        }
    }

    private void SetCheckFailure(FailureInfo? failure)
    {
        lock (_stateLock) { _lastCheckFailure = failure; }
        OnPropertyChanged(nameof(LastCheckFailure));
    }

    public abstract IBoard Board { get; }
    public abstract string ThreadId { get; }
    public abstract string NiceName { get; }
    public abstract Uri ThreadUri { get; }
    public string Url => ThreadUri.ToString();
    public string DownloadDirectory => StorageLayout.GetThreadDirectory(OutputDirectory, Board.ImageBoard.Name, Board.BoardCode, ThreadId);
    protected string OutputDirectory { get; private set; } = "Downloads";
    protected virtual TimeSpan MinimumRecheckInterval => TimeSpan.FromMilliseconds(1);

    public ThreadSourceState SourceState
    {
        get { lock (_stateLock) { return _sourceState; } }
    }

    public RequestPolicyProgress? RequestProgress
    {
        get { lock (_stateLock) { return _requestProgress; } }
    }

    /// <summary>Providers clear this in finally after request and body processing finish.</summary>
    protected void SetRequestProgress(RequestPolicyProgress? progress)
    {
        lock (_stateLock)
        {
            if (_requestProgress == progress) return;
            _requestProgress = progress;
        }
        OnPropertyChanged(nameof(RequestProgress));
    }

    public IReadOnlyDictionary<string, string> MediaFailures
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<string, string>(_mediaFailures, StringComparer.Ordinal);
            }
        }
    }

    public virtual string Title
    {
        get { lock (_stateLock) { return _title; } }
        set
        {
            lock (_stateLock)
            {
                if (_title == value) return;
                _title = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(NiceName));
        }
    }

    public string Status
    {
        get
        {
            ThreadActivity activity = Activity;
            return activity switch
            {
                ThreadActivity.NoMedia => "No media found",
                ThreadActivity.NotFound => "Not found",
                _ => activity.ToString()
            };
        }
    }

    public ThreadActivity Activity
    {
        get { lock (_stateLock) { return _activity; } }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_stateLock)
            {
                if (_mediaFailures.Count > 0 && value is ThreadActivity.Finished or ThreadActivity.Downloading or ThreadActivity.Pending)
                    value = ThreadActivity.Error;
                if (_activity == value) return;
                _activity = value;
            }
            OnPropertyChanged();
            Logger.Information("Thread {ThreadId} activity changed to: {Activity}", ThreadId, value);
        }
    }

    public int TotalMediaCount
    {
        get { lock (_stateLock) { return _totalMediaCount; } }
        set
        {
            lock (_stateLock)
            {
                if (_totalMediaCount == value) return;
                _totalMediaCount = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
        }
    }

    public int DownloadedMediaCount
    {
        get { lock (_stateLock) { return _downloadedMedia.Count; } }
    }

    public string Progress
    {
        get
        {
            lock (_stateLock)
            {
                if (_totalMediaCount == 0) return "0 / 0 (0%)";
                double percent = (double)_downloadedMedia.Count / _totalMediaCount * 100;
                return $"{_downloadedMedia.Count} / {_totalMediaCount} ({percent:0.##}%)";
            }
        }
    }

    /// <summary>Gets a snapshot, or replaces the completed-media set with a copy.</summary>
    public HashSet<string> DownloadedMedia
    {
        get
        {
            lock (_stateLock)
            {
                return new HashSet<string>(_downloadedMedia, StringComparer.Ordinal);
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            HashSet<string> snapshot = new HashSet<string>(value, StringComparer.Ordinal);
            lock (_stateLock)
            {
                _downloadedMedia = snapshot;
                _verifiedDestinations.Clear();
                foreach (string mediaIdentifier in snapshot)
                    _verifiedDestinations[mediaIdentifier] = new HashSet<string>(DestinationComparer) { string.Empty };
            }
            OnPropertyChanged(nameof(DownloadedMedia));
            OnPropertyChanged(nameof(DownloadedMediaCount));
            OnPropertyChanged(nameof(Progress));
        }
    }

    public void StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));

        lock (_monitoringLock)
        {
            if (_monitoringTask != null) return;
            _checkTrigger = new PollingTrigger();
            _monitoringQueue = queue;
            OutputDirectory = outputDirectory;
            _monitoringInterval = interval;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            _monitoringCancellation = cancellation;
            _monitoringTask = Task.Run(() => MonitorAsync(queue, token));
        }
    }

    public CheckRequestStatus RequestCheck()
    {
        lock (_monitoringLock)
        {
            if (_monitoringQueue?.IsPaused == true) return CheckRequestStatus.Paused;
            if (_monitoringCancellation == null || _monitoringCancellation.IsCancellationRequested
                || _monitoringTask == null || _monitoringTask.IsCompleted) return CheckRequestStatus.Stopping;
            return _checkTrigger.Request();
        }
    }

    public void UpdateMonitoringInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_monitoringLock) { _monitoringInterval = interval; }
    }

    public async Task StopMonitoringAsync()
    {
        Task? monitoringTask;
        CancellationTokenSource? cancellation;
        lock (_monitoringLock)
        {
            monitoringTask = _monitoringTask;
            cancellation = _monitoringCancellation;
        }
        RequestStop(cancellation);
        if (monitoringTask == null) return;
        await monitoringTask.ConfigureAwait(false);
        lock (_monitoringLock)
        {
            if (ReferenceEquals(_monitoringTask, monitoringTask))
            {
                _monitoringTask = null;
                _monitoringCancellation = null;
                cancellation?.Dispose();
            }
        }
    }

    private async Task MonitorAsync(DownloadQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _checkTrigger.Consume();
                try
                {
                    await queue.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await checkThreadAsync(queue, cancellationToken).ConfigureAwait(false);
                    SetCheckFailure(null);
                }
                catch (QueueStoppingException)
                {
                    // The queue can close its source gate before this watch's own token is cancelled.
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Activity = ThreadActivity.Error;
                    SetCheckFailure(FailureInfo.FromException(exception, media: false));
                    Logger.Error(exception, "Error checking thread {ThreadId}", ThreadId);
                }
                cancellationToken.ThrowIfCancellationRequested();
                long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                if (SourceState != ThreadSourceState.Active && !(_content?.NeedsRetry ?? false))
                {
                    // Keep the owned lifetime available for explicit retry of known terminal media.
                    await _checkTrigger.WaitForRequestAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                TimeSpan interval;
                lock (_monitoringLock) { interval = _monitoringInterval; }
                TimeSpan minimumInterval = MinimumRecheckInterval;
                if (interval < minimumInterval) interval = minimumInterval;
                bool requested = await _checkTrigger.WaitOrDelayAsync(
                    token => WaitForNextCheckAsync(interval, token), cancellationToken).ConfigureAwait(false);
                if (requested)
                {
                    TimeSpan remaining = minimumInterval - System.Diagnostics.Stopwatch.GetElapsedTime(finished);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A requested stop cancels both the provider request and the polling delay.
        }
        catch (Exception exception)
        {
            Activity = ThreadActivity.Error;
            SetCheckFailure(FailureInfo.FromException(exception, media: false));
            Logger.Error(exception, "Monitoring stopped unexpectedly for thread {ThreadId}", ThreadId);
        }
    }

    protected virtual Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        return Task.Delay(interval, cancellationToken);
    }

    /// <summary>Ends source polling after the current snapshot without stopping queued media jobs.</summary>
    protected void MarkPollingTerminal(ThreadSourceState state)
    {
        if (state != ThreadSourceState.Archived && state != ThreadSourceState.NotFound)
            throw new ArgumentOutOfRangeException(nameof(state));
        lock (_stateLock)
        {
            if (_sourceState == state) return;
            _sourceState = state;
        }
        OnPropertyChanged(nameof(SourceState));
    }

    protected async Task ObserveSourceEndAsync(ThreadSourceState state, CapturedThreadSnapshot? finalSnapshot = null)
    {
        if (state == ThreadSourceState.Active) throw new ArgumentOutOfRangeException(nameof(state));
        DateTimeOffset first = _firstEndObservation ??= _timeProvider.GetUtcNow();
        if (_endSink != null) await _endSink(state, first, finalSnapshot).ConfigureAwait(false);
        _restoredFinalSnapshot = finalSnapshot;
        MarkPollingTerminal(state);
    }


    public virtual Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        cancellationToken.ThrowIfCancellationRequested();
        Activity = ThreadActivity.Checking;
        return Task.CompletedTask;
    }

    public virtual Task EnqueueMediaDownloadsAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        return checkThreadAsync(queue, cancellationToken);
    }

    internal void AttachContent(ThreadContentContext content)
    {
        lock (_monitoringLock)
        {
            if (_monitoringTask != null) throw new InvalidOperationException("Attach content services before starting monitoring.");
            _content = content;
            _contentCandidatesRestored = false;
        }
    }

    /// <summary>Explicitly opts a current watch in/out; previously captured content remains available.</summary>
    public async Task SetCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_content == null) throw new NotSupportedException("This watch has no content services.");
        await _content.SetCaptureEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        OnPropertyChanged();
    }

    protected async Task RestoreContentCandidatesAsync(Dictionary<string, DownloadItem> media, CancellationToken cancellationToken)
    {
        if (_restoredFinalSnapshot != null)
            foreach (CapturedPost post in _restoredFinalSnapshot.Posts)
                foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
                {
                    if (attachment.MediaId == null || attachment.Url == null || attachment.PlannedPath == null) continue;
                    media.TryAdd(attachment.MediaId, new DownloadItem(new Uri(attachment.Url), attachment.PlannedPath,
                        Board.ImageBoard, this, attachment.MediaId, attachment.ExpectedSize, attachment.ExpectedMd5,
                        attachment.OriginalFileName));
                }
        if (_content == null || _contentCandidatesRestored) return;
        IReadOnlyList<DownloadItem> restored = await _content.RestoreCandidatesAsync(this, cancellationToken).ConfigureAwait(false);
        foreach (DownloadItem item in restored) media.TryAdd(item.MediaIdentifier, item);
        _contentCandidatesRestored = true;
    }

    protected async Task CaptureContentAsync(CapturedThreadSnapshot? observed, CancellationToken cancellationToken)
    {
        observed ??= _restoredFinalSnapshot;
        if (_content != null) await _content.CaptureAsync(observed, cancellationToken).ConfigureAwait(false);
        OnPropertyChanged();
    }

    protected async Task CaptureSourceStateAsync(ThreadSourceState state, CancellationToken cancellationToken)
    {
        if (_content != null) await _content.RecordSourceStateAsync(state, cancellationToken).ConfigureAwait(false);
        OnPropertyChanged();
    }

    /// <summary>Closes the terminal capture barrier only after final media metadata is admitted.</summary>
    protected async Task SettleTerminalWorkAsync()
    {
        if (SourceState == ThreadSourceState.Active || _content?.NeedsRetry == true ||
            SourceState == ThreadSourceState.Archived && _restoredFinalSnapshot == null) return;
        if (_captureSettledSink != null) await _captureSettledSink().ConfigureAwait(false);
    }

    protected async Task EnqueueKnownMediaAsync(DownloadQueue queue, IReadOnlyDictionary<string, DownloadItem> media,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string> known = new HashSet<string>(media.Keys, StringComparer.Ordinal);
        known.UnionWith(DownloadedMedia);
        TotalMediaCount = known.Count;
        IReadOnlyList<DownloadItem>? selected = _content == null ? media.Values.ToArray()
            : await _content.SelectAsync(this, media.Values, cancellationToken).ConfigureAwait(false);
        if (selected == null) { Activity = ThreadActivity.Error; OnPropertyChanged(); return; }
        int selectedCount = TotalMediaCount - (_content?.Snapshot.ExcludedMedia ?? 0);
        Activity = MediaFailures.Count > 0 ? ThreadActivity.Error : TotalMediaCount == 0
            ? SourceState == ThreadSourceState.NotFound ? ThreadActivity.NotFound : ThreadActivity.NoMedia
            : selectedCount == 0 || DownloadedMediaCount >= selectedCount ? ThreadActivity.Finished : ThreadActivity.Downloading;
        foreach (DownloadItem item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await queue.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        }
        OnPropertyChanged();
    }

    public bool IsMediaDownloaded(string mediaIdentifier)
    {
        lock (_stateLock) { return _downloadedMedia.Contains(mediaIdentifier); }
    }

    public void MarkMediaAsDownloaded(string mediaIdentifier, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        string destinationKey = GetDestinationKey(destinationPath);
        bool mediaChanged;
        bool failuresChanged;
        bool statusChanged;
        lock (_stateLock)
        {
            if (!_verifiedDestinations.TryGetValue(mediaIdentifier, out HashSet<string>? destinations))
            {
                destinations = new HashSet<string>(DestinationComparer);
                _verifiedDestinations.Add(mediaIdentifier, destinations);
            }
            destinations.Add(destinationKey);
            mediaChanged = _downloadedMedia.Add(mediaIdentifier);
            failuresChanged = _destinationFailures.TryGetValue(mediaIdentifier, out Dictionary<string, FailureInfo>? failures)
                && failures.Remove(destinationKey);
            if (failuresChanged) RefreshMediaFailureLocked(mediaIdentifier);
            if (!mediaChanged && !failuresChanged) return;
            ThreadActivity nextStatus = _activity;
            if (_mediaFailures.Count > 0) nextStatus = ThreadActivity.Error;
            else if (_downloadedMedia.Count >= _totalMediaCount - (_content?.Snapshot.ExcludedMedia ?? 0)) nextStatus = ThreadActivity.Finished;
            else if (failuresChanged && _activity == ThreadActivity.Error) nextStatus = ThreadActivity.Downloading;
            statusChanged = _activity != nextStatus;
            _activity = nextStatus;
        }
        NotifyMediaStateChanged(mediaChanged, failuresChanged, statusChanged);
    }

    public void MarkMediaPending(string mediaIdentifier, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        string destinationKey = GetDestinationKey(destinationPath);
        bool statusChanged;
        lock (_stateLock)
        {
            if (!InvalidateDestinationLocked(mediaIdentifier, destinationKey, destinationPath == null)) return;
            statusChanged = _activity == ThreadActivity.Finished;
            if (statusChanged) _activity = _mediaFailures.Count > 0 ? ThreadActivity.Error : ThreadActivity.Pending;
        }
        NotifyMediaStateChanged(true, false, statusChanged);
    }

    public void ReportMediaFailure(string mediaIdentifier, Exception exception, string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentifier);
        ArgumentNullException.ThrowIfNull(exception);
        string destinationKey = GetDestinationKey(destinationPath);
        bool mediaChanged;
        bool statusChanged;
        lock (_stateLock)
        {
            mediaChanged = InvalidateDestinationLocked(mediaIdentifier, destinationKey, destinationPath == null);
            if (!_destinationFailures.TryGetValue(mediaIdentifier, out Dictionary<string, FailureInfo>? failures))
            {
                failures = new Dictionary<string, FailureInfo>(DestinationComparer);
                _destinationFailures.Add(mediaIdentifier, failures);
            }
            failures[destinationKey] = FailureInfo.FromException(exception, media: true);
            RefreshMediaFailureLocked(mediaIdentifier);
            statusChanged = _activity != ThreadActivity.Error;
            _activity = ThreadActivity.Error;
        }
        NotifyMediaStateChanged(mediaChanged, true, statusChanged);
    }

    // The empty key represents an old completion/failure without a known destination.
    private static string GetDestinationKey(string? destinationPath)
    {
        if (destinationPath == null) return string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return Path.GetFullPath(destinationPath);
    }

    private bool InvalidateDestinationLocked(string mediaIdentifier, string destinationKey, bool allDestinations)
    {
        if (!allDestinations && _verifiedDestinations.TryGetValue(mediaIdentifier, out HashSet<string>? destinations))
        {
            destinations.Remove(destinationKey);
            // An unspecified old marker cannot prove that a different destination is intact.
            destinations.Remove(string.Empty);
            if (destinations.Count > 0) return false;
        }
        _verifiedDestinations.Remove(mediaIdentifier);
        return _downloadedMedia.Remove(mediaIdentifier);
    }

    private void RefreshMediaFailureLocked(string mediaIdentifier)
    {
        Dictionary<string, FailureInfo> failures = _destinationFailures[mediaIdentifier];
        if (failures.Count == 0)
        {
            _destinationFailures.Remove(mediaIdentifier);
            _mediaFailures.Remove(mediaIdentifier);
            return;
        }
        _mediaFailures[mediaIdentifier] = string.Join("; ", failures.Values.Select(failure => failure.Message));
    }

    private void NotifyMediaStateChanged(bool mediaChanged, bool failuresChanged, bool statusChanged)
    {
        if (mediaChanged)
        {
            OnPropertyChanged(nameof(DownloadedMedia));
            OnPropertyChanged(nameof(DownloadedMediaCount));
            OnPropertyChanged(nameof(Progress));
        }
        if (failuresChanged) OnPropertyChanged(nameof(MediaFailures));
        if (statusChanged) OnPropertyChanged(nameof(Status));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => _propertyChanges.Signal();

    public void NotifyThreadRemoval(bool abort)
    {
        CancellationTokenSource? cancellation;
        lock (_monitoringLock) { cancellation = _monitoringCancellation; }
        RequestStop(cancellation);
        _removal.Current?.Invoke(this);
    }

    private static void RequestStop(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A concurrent stop already drained this lifetime and released its token source.
        }
    }
}
