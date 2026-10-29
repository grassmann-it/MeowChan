using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Http;
using MeowChan.Storage;
using MeowChan.Archive;

namespace MeowChan.Boards.FourChan;

public class FourChanThread : BaseThread, IThread
{
    private readonly Dictionary<string, DownloadItem> _knownMedia = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset? _lastModified;
    private bool _hasSnapshot;

    private string _catalogOpeningText = string.Empty;
    public string CatalogOpeningText
    {
        get => _catalogOpeningText;
        set { _catalogOpeningText = value; SetOpeningTitle(value); }
    }
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
            FourChanParser.ThreadData parsed;
            try
            {
                parsed = FourChanParser.ParseThread(await BaseImageBoard.GetJsonReader(Board.ImageBoard).ReadAsync(response.Content, cancellationToken),
                    Board.BoardCode, ThreadId, cancellationToken);
            }
            catch (System.Text.Json.JsonException exception)
            {
                // Keep the established library exception contract for invalid JSON.
                throw new JsonReaderException(exception.Message, exception);
            }
            Dictionary<string, DownloadItem> media = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
            List<CapturedPost> capturedPosts = new List<CapturedPost>();
            foreach (FourChanParser.Post post in parsed.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<CapturedAttachment> attachments = new List<CapturedAttachment>();
                foreach (FourChanParser.Attachment file in post.Attachments)
                {
                    if (file.Deleted) { attachments.Add(new CapturedAttachment(file.Position, null, IsDeleted: true)); continue; }
                    DownloadItem item = CreateMedia(file);
                    media.TryAdd(item.MediaIdentifier, item);
                    attachments.Add(ProviderPostCapture.Attachment(item, file.Position));
                }
                capturedPosts.Add(new CapturedPost(post.Id, post.Ordinal, (PostFieldPresence)post.Fields,
                    post.PostedAt, post.Subject, post.Author, post.Text, post.Deleted, attachments.AsReadOnly()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A partial response can start with a reply. Only the identified OP
            // supplies a source title; missing subjects retain known metadata.
            string? sourceTitle = capturedPosts.Find(post => post.PostId == ThreadId
                && post.Fields.HasFlag(PostFieldPresence.Subject))?.Subject;
            bool archived = parsed.Archived;
            CapturedThreadSnapshot capturedSnapshot = new(
                new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId), Url,
                string.IsNullOrWhiteSpace(sourceTitle) ? SourceTitle : sourceTitle,
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

    private DownloadItem CreateMedia(FourChanParser.Attachment file)
    {
        string destination = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
            Board.BoardCode, ThreadId, file.Id!, file.Extension!);
        return new DownloadItem(file.Url!, destination, Board.ImageBoard, this, file.Id!,
            file.Size, file.Md5, file.FileName, GetLegacyPath(file.FileName!));
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
