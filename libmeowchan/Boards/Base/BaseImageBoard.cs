using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MeowChan.Http;
using MeowChan.Interfaces;
using Serilog;

namespace MeowChan.Boards.Base;

/// <summary>Provider identity and its shared HTTP transport.</summary>
public abstract class BaseImageBoard : IImageBoard
{
    private static readonly JsonResponseReader DefaultJsonReader = new JsonResponseReader();
    private readonly HttpClient? _httpClient;
    private readonly JsonResponseReader _jsonReader;
    private readonly BoardRequestGate _metadataGate = new();
    private readonly Dictionary<string, BoardNamesSnapshot> _boardMetadata = new(StringComparer.Ordinal);

    internal sealed record BoardNamesSnapshot(IReadOnlyDictionary<string, string> Names, DateTimeOffset? LastModified);

    /// <summary>
    /// A supplied client is borrowed unchanged; its caller owns request limits, deadlines and redirect policy.
    /// Use ProviderHttpClientFactory.CreatePolicyClient when a custom transport should use the shared policy.
    /// </summary>
    protected BaseImageBoard(HttpClient? httpClient = null, JsonResponseReader? jsonReader = null, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _jsonReader = jsonReader ?? DefaultJsonReader;
        Logger = logger ?? Serilog.Core.Logger.None;
    }

    /// <summary>Borrowed from the caller; the provider never configures or disposes it.</summary>
    public ILogger Logger { get; }

    public abstract string Name { get; }
    public abstract string NiceName { get; }
    public abstract Uri BaseUri { get; }
    // Optional host adapters can carry opaque SDK identities without weakening the public layout contract.
    internal virtual bool UsesOpaqueStorageIdentifiers => false;
    public abstract bool CanHandle(Uri uri);
    public abstract bool IsThreadUri(Uri uri);
    public abstract bool IsBoardUri(Uri uri);
    public abstract IBoard GetBoard(Uri boardUri);
    public abstract IThread GetThread(Uri threadUri);

    public virtual HttpClient GetHttpClient() => _httpClient ?? ProviderHttpClientFactory.GetDefaultClient(Name, Logger);

    internal static JsonResponseReader GetJsonReader(IImageBoard provider) =>
        provider is BaseImageBoard imageBoard ? imageBoard._jsonReader : DefaultJsonReader;

    internal async Task<IReadOnlyDictionary<string, string>> GetBoardNamesAsync(
        string endpoint, string idField, string? collectionField = null, CancellationToken cancellationToken = default,
        bool revalidate = false)
    {
        await _metadataGate.EnterAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _boardMetadata.TryGetValue(endpoint, out BoardNamesSnapshot? cached);
            if (cached != null && (!revalidate || cached.LastModified == null)) return cached.Names;
            BoardNamesSnapshot snapshot = await FetchBoardNamesAsync(
                GetHttpClient(), endpoint, idField, collectionField, cancellationToken, _jsonReader, cached);
            cancellationToken.ThrowIfCancellationRequested();
            _boardMetadata[endpoint] = snapshot;
            return snapshot.Names;
        }
        finally
        {
            _metadataGate.Exit();
        }
    }

    internal static async Task<BoardNamesSnapshot> FetchBoardNamesAsync(
        HttpClient client, string endpoint, string idField, string? collectionField = null,
        CancellationToken cancellationToken = default, JsonResponseReader? jsonReader = null, BoardNamesSnapshot? previous = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.IfModifiedSince = previous?.LastModified;
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.StatusCode == HttpStatusCode.NotModified)
            return previous ?? throw new InvalidDataException("Board metadata returned 304 before a successful snapshot was available.");
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("A board metadata snapshot requires a complete HTTP 200 response.");
        JToken data = JToken.Parse(await (jsonReader ?? DefaultJsonReader).ReadAsync(response.Content, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        JArray boards = (collectionField == null ? data : (data as JObject)?[collectionField]) as JArray
            ?? throw new InvalidDataException("Board metadata does not contain a board list.");
        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JToken board in boards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (board is not JObject record)
                throw new InvalidDataException("Board metadata contains an invalid board entry.");
            if ((record[idField] != null && record[idField]!.Type != JTokenType.String) ||
                (record["title"] != null && record["title"]!.Type != JTokenType.String))
                throw new InvalidDataException("Board metadata identifiers and titles must be strings.");
            string? code = board.Value<string>(idField);
            string? name = board.Value<string>("title");
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name)) names[code] = name;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new BoardNamesSnapshot(names, response.Content.Headers.LastModified);
    }

    protected static bool IsWebUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

}
