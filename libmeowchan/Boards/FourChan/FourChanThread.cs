using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Http;
using MeowChan.Storage;
using MeowChan.Archive;

namespace MeowChan.Boards.FourChan;

public class FourChanThread : BaseThread
{
    private readonly Dictionary<string, DownloadItem> _knownMedia = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset? _lastModified;
    private bool _hasSnapshot;

    public override bool SupportsContent => true;
    public override IBoard Board { get; }
    public override string ThreadId { get; }
    public override string NiceName => $"{Title} ({ThreadId})";
    public override Uri ThreadUri { get; }
    protected override TimeSpan MinimumRecheckInterval => TimeSpan.FromSeconds(10);

    public FourChanThread(IBoard board, Uri threadUri)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        ThreadUri = FourChanImageBoard.CanonicalizeThreadUri(threadUri);
        if (!string.Equals(Board.BoardCode, ThreadUri.Segments[1].Trim('/'), StringComparison.Ordinal))
            throw new ArgumentException("Thread URI does not belong to the supplied board.", nameof(threadUri));
        ThreadId = ThreadUri.Segments[3];
        Title = $"Thread {ThreadId}";
    }

    public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            await CheckCoreAsync(queue, cancellationToken);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task CheckCoreAsync(DownloadQueue queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await base.checkThreadAsync(queue, cancellationToken);
        await RestoreContentCandidatesAsync(_knownMedia, cancellationToken);
        await CaptureContentAsync(null, cancellationToken);
        if (SourceState != ThreadSourceState.Active)
        {
            if (SourceState == ThreadSourceState.NotFound)
                await CaptureSourceStateAsync(ThreadSourceState.NotFound, cancellationToken);
            await EnqueueKnownMediaAsync(queue, _knownMedia, cancellationToken);
            await SettleTerminalWorkAsync();
            return;
        }
        if (Board is FourChanBoard board) await board.EnsureMetadataAsync(cancellationToken);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
            $"https://a.4cdn.org/{Board.BoardCode}/thread/{ThreadId}.json");
        request.Options.Set(RequestPolicyHandler.ObserverKey, SetRequestProgress);
        try
        {
            request.Headers.IfModifiedSince = _lastModified;
            using HttpResponseMessage response = await Board.ImageBoard.GetHttpClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!_hasSnapshot) throw new InvalidDataException("A thread returned 304 before a successful snapshot was available.");
                await EnqueueKnownMediaAsync(queue, _knownMedia, cancellationToken);
                return;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await ObserveSourceEndAsync(ThreadSourceState.NotFound);
                await CaptureSourceStateAsync(ThreadSourceState.NotFound, cancellationToken);
                await EnqueueKnownMediaAsync(queue, _knownMedia, cancellationToken);
                await SettleTerminalWorkAsync();
                if (TotalMediaCount == 0 && MediaFailures.Count == 0) Activity = ThreadActivity.NotFound;
                return;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("A thread snapshot requires a complete HTTP 200 response.");
            JObject data = JObject.Parse(await BaseImageBoard.GetJsonReader(Board.ImageBoard).ReadAsync(response.Content, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            JArray posts = data["posts"] as JArray
                ?? throw new InvalidDataException("Thread data does not contain posts.");
            string? subject = posts.Count > 0 ? WebUtility.HtmlDecode(posts[0].Value<string>("sub")) : null;
            Title = string.IsNullOrWhiteSpace(subject) ? $"Thread {ThreadId}" : subject;

            Dictionary<string, DownloadItem> media = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
            List<CapturedPost> capturedPosts = new List<CapturedPost>();
            int ordinal = 0;
            foreach (JToken post in posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<CapturedAttachment> attachments = new List<CapturedAttachment>();
                DownloadItem? item = ParseMedia(post);
                if (item != null)
                {
                    media.TryAdd(item.MediaIdentifier, item);
                    attachments.Add(ProviderPostCapture.Attachment(item, 0));
                }
                else if (post.Value<int?>("filedeleted") == 1)
                    attachments.Add(new CapturedAttachment(0, null, IsDeleted: true));
                CapturedPost? captured = ProviderPostCapture.Parse(post, ordinal++, ThreadId, attachments.AsReadOnly());
                if (captured != null) capturedPosts.Add(captured);
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool archived = posts.Count > 0 && posts[0].Value<int?>("archived") == 1;
            CapturedThreadSnapshot capturedSnapshot = new(
                new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId), Url, Title,
                archived ? ThreadSourceState.Archived : SourceState, DateTimeOffset.UtcNow, DownloadDirectory,
                capturedPosts.AsReadOnly());
            if (archived) await ObserveSourceEndAsync(ThreadSourceState.Archived, capturedSnapshot);
            foreach (KeyValuePair<string, DownloadItem> item in media) _knownMedia[item.Key] = item.Value;
            await CaptureContentAsync(capturedSnapshot, cancellationToken);
            _lastModified = response.Content.Headers.LastModified;
            _hasSnapshot = true;
            await EnqueueKnownMediaAsync(queue, _knownMedia, cancellationToken);
            if (archived) await SettleTerminalWorkAsync();
        }
        finally
        {
            SetRequestProgress(null);
        }
    }

    private DownloadItem? ParseMedia(JToken post)
    {
        if (post.Value<int?>("filedeleted") == 1 || post["tim"] == null || post["ext"] == null || post["filename"] == null)
            return null;
        long timestamp = post.Value<long>("tim");
        string? extension = post.Value<string>("ext");
        if (timestamp <= 0 || string.IsNullOrWhiteSpace(extension)) return null;
        string mediaId = timestamp.ToString(CultureInfo.InvariantCulture);
        string originalFileName = (post.Value<string>("filename") ?? "unknown") + extension;
        Uri uri = new Uri($"https://i.4cdn.org/{Board.BoardCode}/{mediaId}{extension}");
        string destination = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
            Board.BoardCode, ThreadId, mediaId, extension);
        return new DownloadItem(uri, destination, Board.ImageBoard, this, mediaId,
            post.Value<long?>("fsize"), post.Value<string>("md5"), originalFileName, GetLegacyPath(originalFileName));
    }

    private string? GetLegacyPath(string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Contains('/') ||
            originalFileName.Contains('\\') || originalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.IsPathRooted(originalFileName)) return null;
        string filename = Path.GetFileName(originalFileName);
        if (filename is "." or ".." || filename != originalFileName) return null;
        return Path.Combine(OutputDirectory, Board.BoardCode, ThreadId, filename);
    }
}
