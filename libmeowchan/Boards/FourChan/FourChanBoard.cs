using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Http;

namespace MeowChan.Boards.FourChan;

public class FourChanBoard : BaseBoard
{
    private readonly BoardRequestGate _requestGate = new();
    private bool _metadataLoaded;
    private string _name = string.Empty;
    private DateTimeOffset? _catalogLastModified;
    private IReadOnlyList<IThread>? _lastCatalog;

    public override IImageBoard ImageBoard { get; }
    public override string BoardCode { get; }
    public override string Name
    {
        get { lock (StateLock) { return _name; } }
        protected set
        {
            lock (StateLock)
            {
                if (_name == value) return;
                _name = value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(NiceName));
        }
    }
    public override string NiceName => $"/{BoardCode}/ - {Name}";
    public override Uri BoardUri { get; }

    public FourChanBoard(IImageBoard imageBoard, Uri boardUri)
    {
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
        BoardUri = FourChanImageBoard.CanonicalizeBoardUri(boardUri);
        BoardCode = BoardUri.AbsolutePath.Trim('/');
        Name = BoardCode;
    }

    internal async Task EnsureMetadataAsync(CancellationToken cancellationToken = default, bool revalidate = false)
    {
        await _requestGate.EnterAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_metadataLoaded) return;
            const string endpoint = "https://a.4cdn.org/boards.json";
            IReadOnlyDictionary<string, string> names = ImageBoard is BaseImageBoard provider
                ? await provider.GetBoardNamesAsync(endpoint, "board", "boards", cancellationToken, revalidate)
                : (await BaseImageBoard.FetchBoardNamesAsync(ImageBoard.GetHttpClient(), endpoint, "board", "boards", cancellationToken)).Names;
            cancellationToken.ThrowIfCancellationRequested();
            if (names.TryGetValue(BoardCode, out string? name)) Name = WebUtility.HtmlDecode(name);
            _metadataLoaded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or IOException or OperationCanceledException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A display name is optional; failed metadata must not block thread downloads.
            ImageBoard.Logger.Warning(exception, "Could not fetch the name of 4chan board /{BoardCode}/; using its board code", BoardCode);
        }
        finally
        {
            _requestGate.Exit();
        }
    }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMetadataAsync(cancellationToken, revalidate: true);
        await _requestGate.EnterAsync(cancellationToken);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://a.4cdn.org/{BoardCode}/catalog.json");
            request.Options.Set(RequestPolicyHandler.ObserverKey, SetRequestProgress);
            request.Headers.IfModifiedSince = _catalogLastModified;
            using HttpResponseMessage response = await ImageBoard.GetHttpClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotModified)
                return _lastCatalog ?? throw new InvalidDataException("A catalog returned 304 before a successful snapshot was available.");
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("A catalog snapshot requires a complete HTTP 200 response.");
            IReadOnlyList<FourChanParser.CatalogEntry> catalog;
            try
            {
                catalog = FourChanParser.ParseCatalog(await BaseImageBoard.GetJsonReader(ImageBoard).ReadAsync(response.Content, cancellationToken), cancellationToken);
            }
            catch (System.Text.Json.JsonException exception)
            {
                // Keep the established library exception contract for invalid JSON.
                throw new JsonReaderException(exception.Message, exception);
            }
            List<IThread> threads = new List<IThread>();
            foreach (FourChanParser.CatalogEntry entry in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FourChanThread thread = new FourChanThread(this, FourChanParser.ThreadUri(BoardCode, entry.Id));
                thread.Title = entry.Title ?? string.Empty;
                thread.CatalogOpeningText = MeowChan.Archive.ArchiveText.PlainText(entry.Text);
                threads.Add(thread);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _catalogLastModified = response.Content.Headers.LastModified;
            _lastCatalog = threads.AsReadOnly();
            return _lastCatalog;
        }
        finally
        {
            SetRequestProgress(null);
            _requestGate.Exit();
        }
    }
}
