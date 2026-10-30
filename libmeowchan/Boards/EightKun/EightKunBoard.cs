using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MeowChan.OfficialSources.EightKun;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Http;

namespace MeowChan.Boards.EightKun;

public class EightKunBoard : BaseBoard
{
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private bool _metadataLoaded;
    private string _name = string.Empty;

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

    public EightKunBoard(IImageBoard imageBoard, Uri boardUri)
    {
        ImageBoard = imageBoard ?? throw new ArgumentNullException(nameof(imageBoard));
        BoardUri = EightKunImageBoard.CanonicalizeBoardUri(boardUri);
        BoardCode = BoardUri.AbsolutePath.Trim('/');
        Name = BoardCode;
    }

    internal async Task EnsureMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_metadataLoaded) return;
            const string endpoint = "https://8kun.top/boards.json";
            IReadOnlyDictionary<string, string> names = ImageBoard is BaseImageBoard provider
                ? await provider.GetBoardNamesAsync(endpoint, "uri", cancellationToken: cancellationToken)
                : await BaseImageBoard.FetchBoardNamesAsync(ImageBoard.GetHttpClient(), endpoint, "uri", cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (names.TryGetValue(BoardCode, out string? name)) Name = WebUtility.HtmlDecode(name);
            _metadataLoaded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or IOException or OperationCanceledException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A display name is optional; failed metadata must not block thread downloads.
            ImageBoard.Logger.Warning(exception, "Could not fetch the name of 8kun board /{BoardCode}/; using its board code", BoardCode);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMetadataAsync(cancellationToken);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"https://8kun.top/{BoardCode}/catalog.json");
        request.Options.Set(RequestPolicyHandler.ObserverKey, SetRequestProgress);
        try
        {
            using HttpResponseMessage response = await ImageBoard.GetHttpClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            IReadOnlyList<EightKunParser.CatalogEntry> catalog;
            try
            {
                catalog = EightKunParser.ParseCatalog(await BaseImageBoard.GetJsonReader(ImageBoard).ReadAsync(response.Content, cancellationToken), cancellationToken);
            }
            catch (System.Text.Json.JsonException exception)
            {
                // Keep the established library exception contract for invalid JSON.
                throw new JsonReaderException(exception.Message, exception);
            }
            List<IThread> threads = new List<IThread>();
            foreach (EightKunParser.CatalogEntry entry in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EightKunThread thread = new EightKunThread(this, EightKunParser.ThreadUri(BoardCode, entry.Id));
                thread.Title = entry.Title ?? string.Empty;
                thread.CatalogOpeningText = MeowChan.Archive.ArchiveText.PlainText(entry.Text);
                threads.Add(thread);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return threads;
        }
        finally
        {
            SetRequestProgress(null);
        }
    }
}
