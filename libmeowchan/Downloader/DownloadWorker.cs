namespace MeowChan.Downloader;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;
using MeowChan.Persistence;
using Serilog;

/// <summary>Publishes a medium only after its contents and manifest are committed.</summary>
public sealed class DownloadWorker
{
    private static readonly MediaStore DefaultStore = new MediaStore();
    private readonly DownloadItem _item;
    private readonly BandwidthLimiter _limiter;
    private readonly MediaStore _store;
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly bool _notifyThread;
    internal Action? AuthorizePublication { get; init; }
    internal Func<CancellationToken, ValueTask<IDisposable>>? AcquirePublicationFence { get; init; }
    internal Action<int>? BytesReceived { get; init; }
    internal MediaCommitResult? CompletedResult { get; private set; }

    public DownloadWorker(DownloadItem downloadItem, BandwidthLimiter bandwidthLimiter, MediaStore? store = null,
        TimeSpan? bodyIdleTimeout = null, TimeProvider? timeProvider = null, ILogger? logger = null, bool notifyThread = true)
    {
        _item = downloadItem ?? throw new ArgumentNullException(nameof(downloadItem));
        _limiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
        _store = store ?? DefaultStore;
        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        IdleTimeoutReadStream.ValidateTimeout(_bodyIdleTimeout, nameof(bodyIdleTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Serilog.Core.Logger.None;
        _notifyThread = notifyThread;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_item.ContentProfileLease is not ProfileLease expected)
            return ExecuteCoreAsync(_item, cancellationToken, drainLateBody: false);
        // Direct public compatibility is a new execution, not a continuation of a
        // token once borrowed by an item. Acquire synchronously, before any I/O or
        // async scheduling, and own it until all execution/disposal work has ended.
        ProfileAdmissionToken admission = expected.Admission.EnterOrdinaryWriter(cancellationToken);
        return ExecuteOwnedAsync(expected, admission, cancellationToken);
    }

    private async Task ExecuteOwnedAsync(ProfileLease expected, ProfileAdmissionToken admission,
        CancellationToken cancellationToken)
    {
        using (admission)
            await ExecuteAdmittedAsync(expected, admission, cancellationToken).ConfigureAwait(false);
    }

    // The queue owns this token through worker disposal AND its own result finally.
    // Never enter Ordinary here: accepted jobs must finish behind pending Exclusive.
    internal Task ExecuteAdmittedAsync(ProfileLease? expected, ProfileAdmissionToken? admission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected == null)
        {
            if (_item.ContentProfileLease != null || admission != null)
                throw new InvalidOperationException("Profile media execution requires the actual expected profile lease; bind it from Root/runtime wiring.");
            return ExecuteCoreAsync(_item, cancellationToken, drainLateBody: false);
        }
        DownloadItem execution = _item.WithProfileWriteAdmission(admission
            ?? throw new InvalidOperationException("Profile media execution requires the queue job's producer admission."), expected);
        execution.RequireProfileWriteAdmission(expected);
        return ExecuteCoreAsync(execution, cancellationToken, drainLateBody: true);
    }

    private async Task ExecuteCoreAsync(DownloadItem execution, CancellationToken cancellationToken, bool drainLateBody)
    {
        DownloadItem acquiringItem = execution.WithPublicationAuthorization(AuthorizePublication).WithPublicationFence(AcquirePublicationFence);
        MediaCommitResult? restored = await _store.TryRestoreAsync(acquiringItem, cancellationToken).ConfigureAwait(false);
        if (restored != null)
        {
            CompletedResult = restored;
            if (_notifyThread) NotifyDownloaded();
            return;
        }
        if (_notifyThread)
        {
            if (_item.Thread is MeowChan.Boards.Base.BaseThread thread) thread.MarkMediaPending(_item.MediaIdentifier, _item.DestinationPath, _item.Variant);
            else _item.Thread.MarkMediaPending(_item.MediaIdentifier, _item.DestinationPath);
        }

        await using IOriginalMediaFilterLease? filterLease = acquiringItem.Filter == null ? null
            : await acquiringItem.Filter.AcquireAsync(acquiringItem, cancellationToken).ConfigureAwait(false);
        HttpClient client = acquiringItem.ImageBoard.GetHttpClient();
        using HttpResponseMessage response = await client.GetAsync(acquiringItem.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // An unavailable media file is a failed media job, never a thread-removal signal.
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
            throw new InvalidDataException("A partial media response cannot be accepted without range-resume support.");
        long? contentLength = response.Content.Headers.ContentLength;
        // Transport completeness is independent of provider metadata, including absent provider size.
        DownloadItem verifiedItem = acquiringItem.WithTransportLength(contentLength);
        Stream opened = await OpenBodyAsync(response.Content, cancellationToken, drainLateBody).ConfigureAwait(false);
        await using Stream source = drainLateBody ? new ProfileBodyStream(opened) : opened;
        using IdleTimeoutReadStream timedSource = new IdleTimeoutReadStream(source, _bodyIdleTimeout, _timeProvider);
        MediaCommitResult completed = filterLease == null
            ? await _store.CommitAsync(verifiedItem, timedSource, cancellationToken, RecordAndThrottleAsync).ConfigureAwait(false)
            : await filterLease.CommitAsync(verifiedItem, timedSource, _store, RecordAndThrottleAsync, cancellationToken).ConfigureAwait(false);
        CompletedResult = completed;
        // CommitAsync deliberately finishes its short manifest commit after publishing the file,
        // even when cancellation arrives then. This is a verified completion, not a partial file.
        if (_notifyThread) NotifyDownloaded();
        _logger.Debug("Saved verified media {MediaId} to {Path} ({Length} bytes)",
            _item.MediaIdentifier, completed.Path, completed.Length);
    }

    private void NotifyDownloaded()
    {
        if (_item.Thread is MeowChan.Boards.Base.BaseThread thread) thread.MarkMediaAsDownloaded(_item.MediaIdentifier, _item.DestinationPath, _item.Variant);
        else _item.Thread.MarkMediaAsDownloaded(_item.MediaIdentifier, _item.DestinationPath);
    }

    private Task RecordAndThrottleAsync(int bytes, CancellationToken cancellationToken)
    {
        // Count body bytes as they are read, including interrupted or failed attempts.
        // Restoring an existing file never enters this path.
        BytesReceived?.Invoke(bytes);
        return _limiter.ThrottleAsync(bytes, cancellationToken);
    }

    private async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken cancellationToken, bool drainLateBody)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = new CancellationTokenSource(_bodyIdleTimeout, _timeProvider);
        using AsyncCancellationLink linked = new AsyncCancellationLink(cancellationToken, deadline.Token);
        Task<Stream>? opening = null;
        try
        {
            opening = content.ReadAsStreamAsync(linked.Token);
            return await opening.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            linked.RequestCancellation();
            if (opening != null)
            {
                // A profile owner cannot release while a late body can still
                // create a stream needing disposal. Unbound callers retain the
                // existing bounded standalone timeout behavior.
                Task disposal = DisposeLateBodyAsync(opening);
                if (drainLateBody) await disposal.ConfigureAwait(false);
                else _ = disposal;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("The media response did not provide its body stream within the idle timeout.", exception);
            throw;
        }
    }

    // IdleTimeoutReadStream keeps standalone timeouts bounded by detaching an
    // uncooperative read. Profile execution must additionally join that actual
    // read after disposing its stream, before its counted async owner can end.
    private sealed class ProfileBodyStream(Stream inner) : Stream
    {
        private Task<int>? _pending;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> read = inner.ReadAsync(buffer, cancellationToken);
            if (read.IsCompletedSuccessfully) return read;
            _pending = read.AsTask();
            return new ValueTask<int>(_pending);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (_pending != null)
                {
                    try { await _pending.ConfigureAwait(false); }
                    catch (Exception) { } // The read/timeout failure is already owned by the worker.
                }
            }
            GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task DisposeLateBodyAsync(Task<Stream> opening)
    {
        // HttpContent implementations may ignore the supplied token. A stream
        // produced after the bounded wait still needs an owner to dispose it.
        try
        {
            await using Stream stream = await opening.ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
