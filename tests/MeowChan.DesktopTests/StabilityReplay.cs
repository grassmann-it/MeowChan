using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace MeowChan.DesktopTests;

/// <summary>
/// Serves the metadata responses (board list, catalog, thread) that an earlier run saved in its evidence directory
/// (audit-&lt;source&gt;.json rows plus bodies/&lt;sha256&gt;.body), by exact URL. It is the replay's whole network: every other URL,
/// and every media URL, is an error. With it a failed run can be reproduced through the production host and parser without network.
/// </summary>
internal sealed class StabilityReplayHandler : HttpMessageHandler
{
    private sealed record Entry(int Status, string? ContentType, byte[] Body, string? Location);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal List<string> Missing { get; } = [];
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal int Saved => _entries.Count;

    internal static StabilityReplayHandler Load(string evidenceDirectory, string sourceId)
    {
        string audit = Path.Combine(evidenceDirectory, "audit-" + sourceId + ".json");
        if (!File.Exists(audit)) throw new FileNotFoundException("The run to replay has no audit file for " + sourceId, audit);
        StabilityReplayHandler handler = new();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(audit));
        foreach (JsonElement row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (!row.GetProperty("sent").GetBoolean() || row.GetProperty("step").GetString() == "original") continue;
            string url = row.GetProperty("url").GetString()!;
            if (handler._entries.ContainsKey(url)) continue;
            int status = row.TryGetProperty("status", out JsonElement code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0;
            string? hash = row.TryGetProperty("sha256", out JsonElement sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
            long bytes = row.GetProperty("bytes").GetInt64();
            byte[] body = [];
            if (hash != null && bytes > 0)
            {
                string path = Path.Combine(evidenceDirectory, "bodies", hash + ".body");
                if (!File.Exists(path)) { handler.Missing.Add(url + " (body " + hash + " was not saved)"); continue; }
                body = File.ReadAllBytes(path);
            }
            string? location = row.TryGetProperty("responseHeaders", out JsonElement headers) && headers.ValueKind == JsonValueKind.Object
                && headers.TryGetProperty("Location", out JsonElement value) ? value.GetString() : null;
            handler._entries[url] = new(status, row.TryGetProperty("contentType", out JsonElement type) ? type.GetString() : null, body, location);
        }
        return handler;
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = StabilityAuditTransport.AuditUrl(request.RequestUri!.AbsoluteUri);
        Requests.Enqueue(request.Method + " " + url);
        if (request.Method != HttpMethod.Get || !_entries.TryGetValue(url, out Entry? entry))
            throw new InvalidOperationException("Replay has no saved response for " + request.Method + " " + url);
        HttpResponseMessage response = new((HttpStatusCode)entry.Status) { RequestMessage = request, Content = new ByteArrayContent(entry.Body) };
        if (entry.ContentType != null && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(entry.ContentType, out var parsed)) response.Content.Headers.ContentType = parsed;
        if (entry.Location != null && Uri.TryCreate(entry.Location, UriKind.Absolute, out Uri? location)) response.Headers.Location = location;
        return Task.FromResult(response);
    }
}
