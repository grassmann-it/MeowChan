using System.Net;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.PluginHost.Sources;

internal sealed class PluginSourceThread(PluginSourceBoard board, string threadId, Uri canonical) : BaseThread
{
    private readonly Dictionary<string, DownloadItem> _knownMedia = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private SourceValidators? _validators;
    private bool _hasSnapshot;
    private DateTimeOffset? _retryAt;
    private PluginSourceImageBoard Source => (PluginSourceImageBoard)Board.ImageBoard;
    public override IBoard Board { get; } = board;
    public override string ThreadId { get; } = threadId;
    public override Uri ThreadUri { get; } = canonical;
    public override string NiceName => $"{Title} ({ThreadId})";
    public override bool SupportsContent => true;
    public string CatalogOpeningText { get; set; } = string.Empty;
    protected override TimeSpan MinimumRecheckInterval => TimeSpan.FromSeconds(10);

    public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CheckCoreAsync(queue, cancellationToken).ConfigureAwait(false); }
        finally { _readGate.Release(); }
    }

    private async Task CheckCoreAsync(DownloadQueue queue, CancellationToken token)
    {
        await base.checkThreadAsync(queue, token).ConfigureAwait(false);
        await RestorePluginContentCandidatesAsync(_knownMedia, token).ConfigureAwait(false);
        await CaptureContentAsync(null, token).ConfigureAwait(false);
        if (SourceState != ThreadSourceState.Active)
        {
            if (SourceState == ThreadSourceState.NotFound) await CaptureSourceStateAsync(SourceState, token).ConfigureAwait(false);
            await EnqueueKnownMediaAsync(queue, _knownMedia, token).ConfigureAwait(false);
            await SettleTerminalWorkAsync().ConfigureAwait(false);
            return;
        }
        if (_retryAt is DateTimeOffset due && due > DateTimeOffset.UtcNow)
            throw new PluginWorkerException("source_deferred", $"Source is waiting until {due:O}.");
        using CancellationTokenSource lifetime = Source.LinkLifetime(token);
        token = lifetime.Token;
        ThreadReadRequest request = new(new SourceThreadId(Source.Name, Board.BoardCode, ThreadId), _validators, _hasSnapshot);
        ThreadReadResult result;
        using (IDisposable admission = await queue.AcquireMetadataAsync(Source.Name, queue.GetThreadPriority(this), token).ConfigureAwait(false))
            result = await Source.InvokeAsync((worker, cancellation) => worker.ReadThreadAsync(request,
                new PluginOperationContext(Url, WatchGeneration), cancellation), token).ConfigureAwait(false);
        PluginSourceImageBoard.Validate(SdkValidation.ValidateThreadResult(request, result, Source.Description));
        token.ThrowIfCancellationRequested();
        switch (result)
        {
            case PluginSdk.ThreadSnapshot observed:
                await ApplyAsync(observed.Snapshot, queue, token).ConfigureAwait(false);
                return;
            case ThreadUnchanged unchanged:
                _validators = unchanged.Validators;
                await EnqueueKnownMediaAsync(queue, _knownMedia, token).ConfigureAwait(false);
                return;
            case ThreadNotFound:
                await ObserveSourceEndWithCancellationAsync(ThreadSourceState.NotFound, null, token).ConfigureAwait(false);
                await CaptureSourceStateAsync(ThreadSourceState.NotFound, token).ConfigureAwait(false);
                await EnqueueKnownMediaAsync(queue, _knownMedia, token).ConfigureAwait(false);
                await SettleTerminalWorkAsync().ConfigureAwait(false);
                return;
            case ThreadDeferred deferred:
                _retryAt = deferred.RetryAt;
                throw new PluginWorkerException(deferred.ReasonCode, $"Source is waiting until {deferred.RetryAt:O}.");
            case ThreadFailure failure:
                _retryAt = failure.Failure.RetryAt;
                throw new PluginWorkerException(failure.Failure.Code, failure.Failure.Message);
            default: throw new PluginWorkerException("invalid_source_result", "Unsupported thread result.");
        }
    }

    private async Task ApplyAsync(SourceThreadSnapshot snapshot, DownloadQueue queue, CancellationToken token)
    {
        if (!Source.Router.TryResolve(snapshot.Url, out SourceResourceKind kind, out string boardId, out string? id, out _)
            || kind != SourceResourceKind.Thread || boardId != Board.BoardCode || id != ThreadId)
            throw new PluginWorkerException("invalid_source_result", "Snapshot URL does not identify the requested thread.");
        ResourceId resource = new(Source.Name, Board.BoardCode, ThreadId);
        List<CapturedPost> posts = new();
        Dictionary<string, (SourceAttachment Attachment, string Destination, string? Legacy, string AcquisitionKey)> candidates = new(StringComparer.Ordinal);
        Dictionary<string, List<AttachmentRevisionReference>> references = new(StringComparer.Ordinal);
        foreach (SourcePost post in snapshot.Posts)
        {
            List<CapturedAttachment> attachments = new();
            foreach (SourceAttachment attachment in post.Attachments ?? Array.Empty<SourceAttachment>())
            {
                string? destination = null;
                string? attachmentRevisionKey = null;
                MediaProviderMetadata? provider = attachment.MediaId == null ? null
                    : new(attachment.MediaId, attachment.Variant, attachment.Url?.AbsoluteUri!, attachment.OriginalFileName,
                        attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256);
                // New incomplete metadata also gets a key; only deletion markers without an identity have none.
                if (provider != null)
                    attachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(resource, post.PostId, attachment.Position, provider);
                if (!attachment.IsDeleted && attachment.MediaId != null && attachment.Url != null)
                {
                    // Original source identities remain byte-for-byte compatible with 0.9.
                    string mediaId = attachment.MediaId;
                    string extension = attachment.Extension ?? Path.GetExtension(attachment.Url.AbsolutePath);
                    destination = StorageLayout.GetPluginMediaPath(OutputDirectory, Source.Name, Board.BoardCode, ThreadId, mediaId, extension, attachment.Variant);
                    string? legacy = attachment.Variant != "original" ? null : Source.Name switch
                    {
                        "4chan" => LegacyPath(attachment.OriginalFileName),
                        "8kun" => LegacyPath(mediaId + extension),
                        _ => null
                    };
                    string acquisitionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, provider!);
                    if (!references.TryGetValue(acquisitionKey, out List<AttachmentRevisionReference>? acquisitionReferences))
                        references[acquisitionKey] = acquisitionReferences = new();
                    acquisitionReferences.Add(new(resource, post.PostId, attachment.Position, attachmentRevisionKey!));
                    candidates[MediaKey(mediaId, attachment.Variant)] = (attachment, destination, legacy, acquisitionKey);
                }
                attachments.Add(new CapturedAttachment(attachment.Position, attachment.MediaId, attachment.Variant,
                    attachment.Url?.AbsoluteUri, attachment.OriginalFileName, destination, attachment.ExpectedSize,
                    attachment.ExpectedMd5, attachment.IsDeleted)
                {
                    ExpectedSha256 = attachment.ExpectedSha256,
                    AttachmentRevisionKey = attachmentRevisionKey
                });
            }
            string? html = post.TextFormat == SourceTextFormat.Html ? post.Text
                : post.Text == null ? null : WebUtility.HtmlEncode(post.Text).Replace("\n", "<br>", StringComparison.Ordinal);
            posts.Add(new CapturedPost(post.PostId, post.Ordinal, (PostFieldPresence)(int)post.Fields,
                post.PostedAt, post.Subject, post.Author, html, post.IsDeleted, attachments.AsReadOnly()));
        }
        Dictionary<string, DownloadItem> observedMedia = new(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var (attachment, destination, legacy, acquisitionKey) = candidate.Value;
            observedMedia[candidate.Key] = new DownloadItem(attachment.Url!, destination, Source, this, attachment.MediaId!,
                attachment.ExpectedSize, attachment.ExpectedMd5, attachment.OriginalFileName, legacy, attachment.Variant)
            {
                ExpectedSha256 = attachment.ExpectedSha256,
                AcquisitionRevisionKey = acquisitionKey,
                AttachmentReferences = Array.AsReadOnly(references[acquisitionKey].ToArray()),
                MediaIntegrityPolicy = Source.MediaIntegrityPolicy
            };
        }
        token.ThrowIfCancellationRequested();
        string title = snapshot.Title ?? Title;
        ThreadSourceState state = snapshot.SourceState == SourceThreadState.Archived ? ThreadSourceState.Archived : ThreadSourceState.Active;
        CapturedThreadSnapshot capture = new(resource, Url, title,
            state, DateTimeOffset.UtcNow, DownloadDirectory, posts.AsReadOnly())
            { DecimalPostOrder = Source.Description.PostOrder == PostOrderKind.DecimalPostId };
        if (state == ThreadSourceState.Archived) await ObserveSourceEndWithCancellationAsync(state, capture, token).ConfigureAwait(false);
        Title = title;
        foreach (KeyValuePair<string, DownloadItem> item in observedMedia) _knownMedia[item.Key] = item.Value;
        await CaptureContentAsync(capture, token).ConfigureAwait(false);
        _validators = snapshot.Validators;
        _hasSnapshot = true;
        _retryAt = null;
        await EnqueueKnownMediaAsync(queue, _knownMedia, token).ConfigureAwait(false);
        if (state != ThreadSourceState.Active) await SettleTerminalWorkAsync().ConfigureAwait(false);
    }

    private string? LegacyPath(string? filename)
    {
        if (string.IsNullOrEmpty(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || filename.Contains('/') || filename.Contains('\\') || filename is "." or ".." || Path.IsPathRooted(filename)) return null;
        // Legacy providers only emitted portable board/thread IDs. Opaque plugin IDs have no legacy layout.
        try { _ = StorageLayout.GetThreadDirectory(OutputDirectory, Source.Name, Board.BoardCode, ThreadId); }
        catch (ArgumentException) { return null; }
        return Path.Combine(OutputDirectory, Board.BoardCode, ThreadId, filename);
    }

    protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (_retryAt is DateTimeOffset due && due - DateTimeOffset.UtcNow > interval) interval = due - DateTimeOffset.UtcNow;
        return Task.Delay(interval, cancellationToken);
    }
}
