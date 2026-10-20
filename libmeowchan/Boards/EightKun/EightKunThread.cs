using System;
using System.Collections.Generic;
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

namespace MeowChan.Boards.EightKun;

public class EightKunThread : BaseThread
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

    public EightKunThread(IBoard board, Uri threadUri)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        ThreadUri = EightKunImageBoard.CanonicalizeThreadUri(threadUri);
        if (!string.Equals(Board.BoardCode, ThreadUri.Segments[1].Trim('/'), StringComparison.Ordinal))
            throw new ArgumentException("Thread URI does not belong to the supplied board.", nameof(threadUri));
        ThreadId = Path.GetFileNameWithoutExtension(ThreadUri.AbsolutePath);
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
        if (Board is EightKunBoard board) await board.EnsureMetadataAsync(cancellationToken);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
            $"https://8kun.top/{Board.BoardCode}/res/{ThreadId}.json");
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
                CaptureMedia(post, 0, media, attachments);
                if (post["extra_files"] is JArray extraFiles)
                {
                    int position = 1;
                    foreach (JToken extraFile in extraFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CaptureMedia(extraFile, position++, media, attachments);
                    }
                }
                CapturedPost? captured = ProviderPostCapture.Parse(post, ordinal++, ThreadId, attachments.AsReadOnly());
                if (captured != null) capturedPosts.Add(captured);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (KeyValuePair<string, DownloadItem> item in media) _knownMedia[item.Key] = item.Value;
            await CaptureContentAsync(new CapturedThreadSnapshot(
                new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId), Url, Title,
                SourceState, DateTimeOffset.UtcNow, DownloadDirectory,
                capturedPosts.AsReadOnly()), cancellationToken);
            _lastModified = response.Content.Headers.LastModified;
            _hasSnapshot = true;
            await EnqueueKnownMediaAsync(queue, _knownMedia, cancellationToken);
        }
        finally
        {
            SetRequestProgress(null);
        }
    }

    private void CaptureMedia(JToken file, int position, Dictionary<string, DownloadItem> media, List<CapturedAttachment> attachments)
    {
        DownloadItem? item = ParseMedia(file);
        if (item != null)
        {
            media.TryAdd(item.MediaIdentifier, item);
            attachments.Add(ProviderPostCapture.Attachment(item, position));
        }
        else if (file.Value<int?>("filedeleted") == 1)
            attachments.Add(new CapturedAttachment(position, null, IsDeleted: true));
    }

    private DownloadItem? ParseMedia(JToken file)
    {
        if (file.Value<int?>("filedeleted") == 1) return null;
        string? mediaId = file.Value<string>("tim");
        string? extension = file.Value<string>("ext");
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(extension)) return null;
        Uri uri = new Uri($"https://nerv.8kun.top/file_store/{mediaId}{extension}");
        string? filename = file.Value<string>("filename");
        string originalFileName = (string.IsNullOrWhiteSpace(filename) ? mediaId : filename) + extension;
        string destination = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
            Board.BoardCode, ThreadId, mediaId, extension);
        string? legacyPath = GetLegacyPath(mediaId + extension);
        return new DownloadItem(uri, destination, Board.ImageBoard, this, mediaId,
            expectedSize: file.Value<long?>("fsize"),
            expectedMd5: file.Value<string>("md5"),
            originalFileName: originalFileName,
            legacyPath: legacyPath);
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
