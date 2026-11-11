using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>What was measured about the one downloaded original of a board.</summary>
internal sealed class StabilityOriginalRun
{
    public string MediaId { get; set; } = "";
    public string PostId { get; set; } = "";
    public int Position { get; set; }
    public string Url { get; set; } = "";
    public string Extension { get; set; } = "";
    /// <summary>Exact size declared by the provider; null when the source declares none (for example a size hint only).</summary>
    public long? ExpectedBytes { get; set; }
    public long ReceivedBytes { get; set; }
    /// <summary>Strongest hash the provider supplied as "algorithm:hex"; all of them are in <see cref="ProviderHashes"/>.</summary>
    public string? ProviderHash { get; set; }
    public List<string> ProviderHashes { get; set; } = [];
    /// <summary>True when every supplied hash matches; null when the provider supplied none.</summary>
    public bool? HashMatched { get; set; }
    public bool? SizeMatched { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>"size+hash", "size", "hash" or "none". "none" means the provider declared nothing to verify.</summary>
    public string IntegrityBasis { get; set; } = "none";
    public string? ProductionFailure { get; set; }

    internal bool IntegrityOk => Sha256 != null && IntegrityBasis != "none" && SizeMatched != false && HashMatched != false;
}

internal sealed class StabilityBoardRun
{
    public string Board { get; set; } = "";
    public string? ThreadId { get; set; }
    public string? ThreadUrl { get; set; }
    public bool UserChosenThread { get; set; }
    public int CatalogThreadCount { get; set; }
    public string SelectionNote { get; set; } = "";
    /// <summary>Pinned/sticky catalog threads passed over before the chosen one.</summary>
    public int PinnedSkipped { get; set; }
    /// <summary>Image attachments passed over because the source's displayed (rounded) size, with a rounding margin, exceeds the original cap.</summary>
    public int OriginalsSkippedByDisplayedSize { get; set; }
    public int PostCount { get; set; }
    public List<StabilityRequestRecord> Requests { get; set; } = [];
    public List<StabilityRequestRecord> Refused { get; set; } = [];
    public StabilityOriginalRun? Original { get; set; }
    public string Archive { get; set; } = "skipped";
    public string Reader { get; set; } = "skipped";
    public string Gallery { get; set; } = "skipped";
    public string OfflineAfterRestart { get; set; } = "skipped";
    public List<string> Notes { get; set; } = [];
    public bool Attempted { get; set; }
    /// <summary>Time the harness actually waited for the thread to reach the archive, and for the original, with their limits.</summary>
    public long? CaptureWaitedMs { get; set; }
    public long? CaptureLimitMs { get; set; }
    public long? MediaWaitedMs { get; set; }
    /// <summary>Position of the chosen thread in the catalog, its sticky/pinned flags as the source delivered them and its age.</summary>
    public JsonObject? ThreadInfo { get; set; }
    /// <summary>Set when a step failed: the host's own view of the thread (activity, last check failure, worker state and log tail).</summary>
    public JsonObject? HostView { get; set; }
    public bool WatchRemoved { get; set; }
}

internal sealed record StabilityFileRef(string Path, string Sha256);

internal sealed class StabilityOfflineRun
{
    public int ProcessId { get; set; }
    public string? NetworkNamespace { get; set; }
    public bool HostStartedPaused { get; set; }
    public int TransportAttempts { get; set; }
    public int NetworkSends { get; set; }
    public bool ProfileDeleted { get; set; }
    public string? Error { get; set; }
}

internal sealed class StabilityChanRun
{
    public string PluginId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Approval { get; set; } = "";
    public string? ExecutionRevision { get; set; }
    public string? AssemblySha256 { get; set; }
    public string? DescriptionSha256 { get; set; }
    public string? Package { get; set; }
    public string? BundleFile { get; set; }
    public bool EnabledByHarness { get; set; }
    public DateTimeOffset? WindowStart { get; set; }
    public DateTimeOffset? WindowEnd { get; set; }
    public List<StabilityRequestRecord> BoardList { get; set; } = [];
    public List<StabilityBoardRun> Boards { get; set; } = [];
    public bool IntegrityExceptionUsed { get; set; }
    public List<StabilityFailure> Failures { get; set; } = [];
    public string? Fatal { get; set; }
    public string? ProfileDirectory { get; set; }
    public int NetworkSends { get; set; }
    public string? ReceiptFile { get; set; }
    public string? ReceiptSha256 { get; set; }
    public StabilityOfflineRun? Offline { get; set; }
    public int RequiredBoards { get; set; } = 3;
    /// <summary>Host and worker logs of the phases, relative to the evidence directory (outside Git).</summary>
    public List<StabilityFileRef> Logs { get; set; } = [];
}

internal sealed class StabilityPhaseOne
{
    public int SchemaVersion { get; set; } = 1;
    public string Network { get; set; } = "";
    public int ProcessId { get; set; }
    public string? NetworkNamespace { get; set; }
    public string? PackageReportSha256 { get; set; }
    public string? PackageRoot { get; set; }
    public string? ManifestSha256 { get; set; }
    public string? Configuration { get; set; }
    public string? TestAssemblySha256 { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<StabilityChanRun> Chans { get; set; } = [];
}

/// <summary>Pure helpers: choosing the original, judging integrity and reading a board list.</summary>
internal static class StabilityRules
{
    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    internal sealed record OriginalChoice(CapturedPost Post, CapturedAttachment Attachment, string Extension);

    /// <summary>The chosen original (or none) and how many image attachments were passed over because their displayed size already exceeds the cap.</summary>
    internal sealed record OriginalSelection(OriginalChoice? Choice, int SkippedByDisplayedSize);

    /// <summary>A displayed size is rounded (7chan prints "(355.92KB, ...)"); the estimate is this much above it so rounding cannot hide an oversize file.</summary>
    internal const decimal DisplayedSizeMargin = 1.01m;

    /// <summary>
    /// The first image attachment, in thread order, whose size is not known to exceed the original cap.
    /// A source that declares no exact size is still eligible when its displayed (approximate) size, times
    /// <see cref="DisplayedSizeMargin"/>, does not exceed the cap or when it shows none; the transport enforces the
    /// 2 MiB cap on the real body either way.
    /// </summary>
    internal static OriginalSelection ChooseOriginal(ArchivedThreadView view, long capBytes = StabilityAuditTransport.MaximumOriginalBytes)
    {
        int skipped = 0;
        for (int offset = 0; offset < Math.Max(1, view.PostCount); offset += 500)
            foreach (CapturedPost post in view.GetPosts(offset, 500))
                foreach (CapturedAttachment attachment in (post.Attachments ?? []).OrderBy(row => row.Position))
                {
                    if (attachment.IsDeleted || attachment.Variant != "original" || attachment.MediaId == null || attachment.Url == null) continue;
                    string extension = ExtensionOf(attachment);
                    if (!ImageExtensions.Contains(extension)) continue;
                    if (attachment.ExpectedSize is long size)
                    {
                        if (size <= 0 || size > capBytes) continue;
                    }
                    else if (ApproximateBytes(attachment.ProviderSizeHint) is long approximate && approximate * DisplayedSizeMargin > capBytes)
                    {
                        skipped++;
                        continue;
                    }
                    return new(new(post, attachment, extension.ToLowerInvariant()), skipped);
                }
        return new(null, skipped);
    }

    internal static string ExtensionOf(CapturedAttachment attachment)
    {
        string fromUrl = Uri.TryCreate(attachment.Url, UriKind.Absolute, out Uri? uri) ? Path.GetExtension(uri.AbsolutePath) : "";
        return fromUrl.Length > 0 ? fromUrl : Path.GetExtension(attachment.OriginalFileName ?? "");
    }

    internal static long? ApproximateBytes(MediaProviderSizeHint? hint)
    {
        if (hint == null || !decimal.TryParse(hint.RawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) || value < 0) return null;
        decimal? factor = hint.UnitLabel?.Trim().ToLowerInvariant() switch
        {
            "b" or "б" or "bytes" or "байт" => 1m,
            "kb" or "kib" or "кб" or "к" => 1024m,
            "mb" or "mib" or "мб" => 1048576m,
            "gb" or "gib" or "гб" => 1073741824m,
            _ => null
        };
        return factor == null ? null : (long)Math.Ceiling(value * factor.Value);
    }

    /// <summary>Compares the received bytes with everything the provider declared. This is independent of the production verdict.</summary>
    internal static StabilityOriginalRun Evaluate(OriginalChoice choice, StabilityDigests digests)
    {
        CapturedAttachment attachment = choice.Attachment;
        StabilityOriginalRun result = new()
        {
            MediaId = attachment.MediaId!, PostId = choice.Post.PostId, Position = attachment.Position, Url = StabilityAuditTransport.AuditUrl(attachment.Url!),
            Extension = choice.Extension, ExpectedBytes = attachment.ExpectedSize, ReceivedBytes = digests.Length, Sha256 = digests.Sha256
        };
        List<(string Name, string Expected, string Actual)> hashes = [];
        if (attachment.ExpectedSha256 is { Length: > 0 } sha256) hashes.Add(("sha256", sha256.ToLowerInvariant(), digests.Sha256));
        if (attachment.ExpectedSha1 is { Length: > 0 } sha1) hashes.Add(("sha1", Hex(sha1), digests.Sha1Hex));
        if (attachment.ExpectedMd5 is { Length: > 0 } md5) hashes.Add(("md5", Hex(md5), digests.Md5Hex));
        result.ProviderHashes = hashes.Select(row => row.Name + ":" + row.Expected).ToList();
        result.ProviderHash = result.ProviderHashes.FirstOrDefault();
        result.HashMatched = hashes.Count == 0 ? null : hashes.All(row => string.Equals(row.Expected, row.Actual, StringComparison.OrdinalIgnoreCase));
        result.SizeMatched = attachment.ExpectedSize is long expected ? expected == digests.Length : null;
        result.IntegrityBasis = (result.SizeMatched.HasValue, result.HashMatched.HasValue) switch
        {
            (true, true) => "size+hash", (true, false) => "size", (false, true) => "hash", _ => "none"
        };
        return result;
    }

    private static readonly string[] FlagNames = ["sticky", "pinned", "pin", "locked", "closed", "cyclic", "cyclical", "bumplocked", "endless", "archived", "autosage"];
    private static readonly string[] IdNames = ["no", "threadId", "num", "id", "postId", "thread"];

    /// <summary>
    /// Reads the flags the source delivered for one thread from a saved metadata body: the first JSON object whose id
    /// property names the thread. Returns null for a body that is not JSON (an HTML catalog) or has no such object.
    /// </summary>
    internal static JsonObject? ThreadFlags(byte[]? body, string threadId)
    {
        using JsonDocument? document = Parse(body);
        return document == null ? null : ThreadFlags(document.RootElement, threadId);
    }

    /// <summary>The saved body as JSON, or null for an empty body or one that is not JSON (an HTML catalog).</summary>
    internal static JsonDocument? Parse(byte[]? body)
    {
        if (body == null || body.Length == 0) return null;
        try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return null; }
    }

    internal static JsonObject? ThreadFlags(JsonElement root, string threadId)
    {
        JsonElement? found = Find(root, threadId);
        if (found == null) return null;
        JsonObject flags = [];
        foreach (JsonProperty property in found.Value.EnumerateObject())
            if (FlagNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                flags[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => property.Value.GetDouble() != 0,
                    _ => null
                };
        return flags;
    }

    private static JsonElement? Find(JsonElement element, string threadId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in IdNames)
                if (element.TryGetProperty(name, out JsonElement id)
                    && (id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.ValueKind == JsonValueKind.String ? id.GetString() : null) == threadId)
                    return element;
            foreach (JsonProperty property in element.EnumerateObject())
                if (Find(property.Value, threadId) is { } nested) return nested;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
                if (Find(item, threadId) is { } nested) return nested;
        return null;
    }

    /// <summary>Provider hashes are normalized by the host to base64; hex input is passed through.</summary>
    private static string Hex(string value)
    {
        if (Regex.IsMatch(value, "^[0-9a-fA-F]+$") && value.Length is 32 or 40 or 64) return value.ToLowerInvariant();
        try { return Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant(); }
        catch (FormatException) { return value; }
    }

    /// <summary>
    /// Reads the ids of a board list. Understands a JSON array or an object holding "boards"/"data" with strings or
    /// objects carrying boardUri/uri/board/id, and falls back to <c>href="/id/"</c> links of an HTML menu.
    /// </summary>
    internal static IReadOnlyList<string> ParseBoardList(byte[] body)
    {
        List<string> ids = [];
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && id.Length <= 64 && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id.Trim('/'));
        }
        string text = Encoding.UTF8.GetString(body).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            JsonElement list = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in new[] { "boards", "data", "result" })
                {
                    if (!root.TryGetProperty(name, out JsonElement candidate)) continue;
                    if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("boards", out JsonElement nested)) candidate = nested;
                    if (candidate.ValueKind == JsonValueKind.Array) { list = candidate; break; }
                }
            }
            if (list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String) Add(entry.GetString());
                    else if (entry.ValueKind == JsonValueKind.Object)
                        foreach (string name in new[] { "boardUri", "uri", "board", "id" })
                            if (entry.TryGetProperty(name, out JsonElement id) && id.ValueKind == JsonValueKind.String) { Add(id.GetString()); break; }
                }
            return ids;
        }
        HashSet<string> notBoards = new(StringComparer.OrdinalIgnoreCase) { "boards", "index", "static", "overboard", "catalog", "js", "css", "img", "res", "src", "rules", "faq", "contact" };
        foreach (Match match in Regex.Matches(text, "href\\s*=\\s*[\"']/([A-Za-z0-9_.-]{1,32})/[\"']"))
            if (!notBoards.Contains(match.Groups[1].Value)) Add(match.Groups[1].Value);
        return ids;
    }
}
