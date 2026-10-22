namespace MeowChan.Boards.Base;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Http;
using MeowChan.Notifications;
using Serilog;

/// <summary>A passive board with one explicitly owned, nonoverlapping polling lifetime.</summary>
public abstract class BaseBoard : IBoard, INotifyPropertyChanged, ICheckRequestSource
{
    private readonly object _monitoringLock = new object();
    private PollingTrigger _checkTrigger = new PollingTrigger();
    protected object StateLock { get; } = new object();
    private FailureInfo? _lastCheckFailure;
    private readonly CoalescedEvent<PropertyChangedEventHandler> _propertyChanges;
    private readonly SingleOwnerCallback<Func<IThread, CancellationToken, ValueTask>> _discovery = new SingleOwnerCallback<Func<IThread, CancellationToken, ValueTask>>();
    private RequestPolicyProgress? _requestProgress;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private DownloadQueue? _sessionQueue;
    internal void AttachSessionQueue(DownloadQueue queue) => _sessionQueue = queue;
    internal long WatchGeneration { get; set; }
    internal DownloadQueue? SessionQueue => _sessionQueue;
    private TimeSpan _monitoringInterval = TimeSpan.FromSeconds(60);

    public abstract IImageBoard ImageBoard { get; }
    public abstract string BoardCode { get; }
    public abstract string Name { get; protected set; }
    public abstract string NiceName { get; }
    public abstract Uri BoardUri { get; }
    protected BaseBoard()
    {
        _propertyChanges = new CoalescedEvent<PropertyChangedEventHandler>(
            handler => handler(this, new PropertyChangedEventArgs(string.Empty)),
            exception => Logger.Error(exception, "Board property observer failed for {BoardCode}", BoardCode));
    }

    protected ILogger Logger => ImageBoard.Logger;
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _propertyChanges.Add(value);
        remove => _propertyChanges.Remove(value);
    }
    public IDisposable RegisterDiscoverySink(Func<IThread, CancellationToken, ValueTask> sink) => _discovery.Register(sink);
    public FailureInfo? LastCheckFailure { get { lock (StateLock) { return _lastCheckFailure; } } }
    public BoardSnapshot GetSnapshot(string downloadDirectory)
    {
        lock (StateLock)
        {
            return new BoardSnapshot(new ResourceId(ImageBoard.Name, BoardCode), BoardUri.AbsoluteUri,
                Name, downloadDirectory, _requestProgress, _lastCheckFailure);
        }
    }

    private void SetCheckFailure(FailureInfo? failure)
    {
        lock (StateLock) { _lastCheckFailure = failure; }
        OnPropertyChanged(nameof(LastCheckFailure));
    }

    public RequestPolicyProgress? RequestProgress
    {
        get { lock (StateLock) { return _requestProgress; } }
    }

    /// <summary>Providers clear this in finally after request and body processing finish.</summary>
    protected void SetRequestProgress(RequestPolicyProgress? progress)
    {
        lock (StateLock)
        {
            if (_requestProgress == progress) return;
            _requestProgress = progress;
        }
        OnPropertyChanged(nameof(RequestProgress));
    }

    public abstract Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default);


    public void StartMonitoring(int intervalInSeconds)
    {
        TimeSpan interval = TimeSpan.FromSeconds(intervalInSeconds);
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(intervalInSeconds));

        lock (_monitoringLock)
        {
            if (_monitoringTask != null) return;
            _checkTrigger = new PollingTrigger();
            _monitoringInterval = interval;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            _monitoringCancellation = cancellation;
            _monitoringTask = Task.Run(() => MonitorAsync(token));
        }
    }

    public CheckRequestStatus RequestCheck()
    {
        lock (_monitoringLock)
        {
            if (_sessionQueue?.IsPaused == true) return CheckRequestStatus.Paused;
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

    /// <summary>Requests a stop. Await StopMonitoringAsync before releasing or restarting a board.</summary>
    public void StopMonitoring()
    {
        CancellationTokenSource? cancellation;
        lock (_monitoringLock) { cancellation = _monitoringCancellation; }
        RequestStop(cancellation);
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

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _checkTrigger.Consume();
                try
                {
                    if (_sessionQueue != null) await _sessionQueue.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await CheckForNewThreadsAsync(cancellationToken).ConfigureAwait(false);
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
                    SetCheckFailure(FailureInfo.FromException(exception, media: false));
                    Logger.Error(exception, "Error checking for new threads on board {BoardCode}", BoardCode);
                }
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan interval;
                lock (_monitoringLock) { interval = _monitoringInterval; }
                await _checkTrigger.WaitOrDelayAsync(token => WaitForNextCheckAsync(interval, token), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A requested stop cancels catalog requests and suppresses late discoveries.
        }
        catch (Exception exception)
        {
            SetCheckFailure(FailureInfo.FromException(exception, media: false));
            Logger.Error(exception, "Monitoring stopped unexpectedly for board {BoardCode}", BoardCode);
        }
    }

    protected virtual Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        return Task.Delay(interval, cancellationToken);
    }

    protected virtual async Task CheckForNewThreadsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<IThread> threads = await GetThreadsAsync(cancellationToken).ConfigureAwait(false);
        foreach (IThread thread in threads)
        {
            if (cancellationToken.IsCancellationRequested) return;
            Func<IThread, CancellationToken, ValueTask>? sink = _discovery.Current;
            if (sink != null) await sink(thread, cancellationToken).ConfigureAwait(false);
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => _propertyChanges.Signal();

    private static void RequestStop(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // Another stop has already drained and released the same lifetime.
        }
    }
}
