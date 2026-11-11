using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

internal sealed class StabilityManifestException(string message) : Exception(message);

/// <summary>An exact host-to-host redirect the user approved before the run (for example 2ch.hk to 2ch.su).</summary>
internal sealed record StabilityRedirect(string From, string To);

/// <summary>One board of a run. A user-chosen thread URL is only valid for that board.</summary>
internal sealed record StabilityBoardRequest(string Board, string? ThreadUrl);

/// <summary>
/// One approved chan. Exactly one of <see cref="Boards"/> and <see cref="BoardsFromList"/> is set.
/// The manifest is data: it never widens what the harness may request beyond the listed chan, boards,
/// the optional board-list URL and the listed redirects.
/// </summary>
internal sealed record StabilityChanApproval(string PluginId, string SourceId, string Approval,
    IReadOnlyList<StabilityBoardRequest> Boards, int BoardsFromList, string? BoardListUrl,
    IReadOnlyList<StabilityRedirect> PlannedRedirects, string? ExpectedAssemblySha256)
{
    internal bool UsesBoardList => BoardsFromList > 0;
    internal int BoardCount => UsesBoardList ? BoardsFromList : Boards.Count;
}

internal sealed record StabilityRunManifest(IReadOnlyList<StabilityChanApproval> Sources)
{
    internal const int SchemaVersion = 1;
    private static readonly Regex Approval = new(@"^(\d{4})-(\d{2})-(\d{2}), chat$", RegexOptions.CultureInvariant);
    private static readonly Regex PluginIdPattern = new(@"^[a-z0-9][a-z0-9._-]{2,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex SourceIdPattern = new(@"^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex BoardPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostPattern = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Strict parse: unknown fields, duplicate fields, wrong types and unknown values fail closed.</summary>
    internal static StabilityRunManifest Parse(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException error) { throw new StabilityManifestException("Manifest is not valid JSON: " + error.Message); }
        using (document)
        {
            JsonElement root = document.RootElement;
            Fields(root, "manifest", ["schemaVersion", "sources"]);
            if (!root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != SchemaVersion)
                throw new StabilityManifestException("Manifest schemaVersion must be " + SchemaVersion + ".");
            if (!root.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
                throw new StabilityManifestException("Manifest sources must be an array.");
            if (sources.GetArrayLength() is 0 or > 16)
                throw new StabilityManifestException("Manifest must approve between 1 and 16 chans.");
            List<StabilityChanApproval> approvals = [];
            foreach (JsonElement source in sources.EnumerateArray()) approvals.Add(ParseSource(source));
            if (approvals.Select(row => row.SourceId).Distinct(StringComparer.Ordinal).Count() != approvals.Count
                || approvals.Select(row => row.PluginId).Distinct(StringComparer.Ordinal).Count() != approvals.Count)
                throw new StabilityManifestException("Each chan may be approved only once.");
            return new(approvals.AsReadOnly());
        }
    }

    /// <summary>Applies the optional run-only list. It can only narrow the manifest, never add a chan.</summary>
    internal IReadOnlyList<StabilityChanApproval> Select(string? only)
    {
        if (only == null) return Sources;
        string[] requested = only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new StabilityManifestException("The run-only list must name a nonempty set of unique source IDs.");
        foreach (string id in requested)
            if (Sources.All(source => source.SourceId != id))
                throw new StabilityManifestException("The run-only list names a chan the manifest does not approve: " + id);
        return Sources.Where(source => requested.Contains(source.SourceId, StringComparer.Ordinal)).ToArray();
    }

    private static StabilityChanApproval ParseSource(JsonElement source)
    {
        Fields(source, "source", ["pluginId", "sourceId", "approval", "boards", "boardsFromList", "boardListUrl", "plannedRedirects", "expectedAssemblySha256"]);
        string pluginId = Text(source, "pluginId", PluginIdPattern);
        string sourceId = Text(source, "sourceId", SourceIdPattern);
        string approval = Text(source, "approval", Approval);
        Match approvalMatch = Approval.Match(approval);
        if (!DateOnly.TryParseExact($"{approvalMatch.Groups[1].Value}-{approvalMatch.Groups[2].Value}-{approvalMatch.Groups[3].Value}",
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new StabilityManifestException("approval of " + sourceId + " is not a real date.");
        bool hasBoards = source.TryGetProperty("boards", out JsonElement boards);
        bool hasList = source.TryGetProperty("boardsFromList", out JsonElement listCount);
        if (hasBoards == hasList)
            throw new StabilityManifestException(sourceId + " needs exactly one of boards and boardsFromList.");
        List<StabilityBoardRequest> boardRequests = [];
        int fromList = 0;
        string? listUrl = null;
        if (hasBoards)
        {
            if (source.TryGetProperty("boardListUrl", out _))
                throw new StabilityManifestException(sourceId + ": boardListUrl is only valid with boardsFromList.");
            if (boards.ValueKind != JsonValueKind.Array || boards.GetArrayLength() is < 1 or > 3)
                throw new StabilityManifestException(sourceId + " must list between 1 and 3 boards.");
            foreach (JsonElement board in boards.EnumerateArray())
            {
                Fields(board, sourceId + " board", ["board", "thread"]);
                string id = Text(board, "board", BoardPattern);
                string? thread = null;
                if (board.TryGetProperty("thread", out JsonElement threadElement))
                    thread = PublicUrl(threadElement, sourceId + " thread URL");
                boardRequests.Add(new(id, thread));
            }
            if (boardRequests.Select(row => row.Board).Distinct(StringComparer.Ordinal).Count() != boardRequests.Count)
                throw new StabilityManifestException(sourceId + " lists a board twice.");
        }
        else
        {
            if (listCount.ValueKind != JsonValueKind.Number || !listCount.TryGetInt32(out fromList) || fromList is < 1 or > 3)
                throw new StabilityManifestException(sourceId + ": boardsFromList must be a number from 1 to 3.");
            if (!source.TryGetProperty("boardListUrl", out JsonElement url))
                throw new StabilityManifestException(sourceId + ": boardsFromList needs the exact boardListUrl of the one extra GET.");
            listUrl = PublicUrl(url, sourceId + " boardListUrl", allowQuery: true);
        }
        List<StabilityRedirect> redirects = [];
        if (source.TryGetProperty("plannedRedirects", out JsonElement redirectArray))
        {
            if (redirectArray.ValueKind != JsonValueKind.Array || redirectArray.GetArrayLength() > 4)
                throw new StabilityManifestException(sourceId + ": plannedRedirects must be an array of at most four host pairs.");
            foreach (JsonElement redirect in redirectArray.EnumerateArray())
            {
                Fields(redirect, sourceId + " redirect", ["from", "to"]);
                StabilityRedirect pair = new(Text(redirect, "from", HostPattern), Text(redirect, "to", HostPattern));
                if (pair.From == pair.To) throw new StabilityManifestException(sourceId + ": a redirect must change the host.");
                if (redirects.Any(row => row.From == pair.From)) throw new StabilityManifestException(sourceId + ": one planned redirect per source host.");
                redirects.Add(pair);
            }
        }
        string? assemblyHash = source.TryGetProperty("expectedAssemblySha256", out JsonElement hash)
            ? Text(source, "expectedAssemblySha256", Sha256Pattern) : null;
        return new(pluginId, sourceId, approval, boardRequests.AsReadOnly(), fromList, listUrl, redirects.AsReadOnly(), assemblyHash);
    }

    private static void Fields(JsonElement element, string context, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new StabilityManifestException(context + " must be an object.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new StabilityManifestException("Unknown field in " + context + ": " + property.Name);
            if (!seen.Add(property.Name)) throw new StabilityManifestException("Duplicate field in " + context + ": " + property.Name);
        }
    }

    private static string Text(JsonElement element, string name, Regex pattern)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text || !pattern.IsMatch(text))
            throw new StabilityManifestException("Missing or invalid " + name + ".");
        return text;
    }

    private static string PublicUrl(JsonElement value, string context, bool allowQuery = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 512 } text
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort || (!allowQuery && uri.Query.Length != 0)
            || uri.HostNameType != UriHostNameType.Dns || !HostPattern.IsMatch(uri.IdnHost))
            throw new StabilityManifestException(context + " must be a plain public http(s) URL without credentials, fragment or custom port.");
        return uri.AbsoluteUri;
    }
}
