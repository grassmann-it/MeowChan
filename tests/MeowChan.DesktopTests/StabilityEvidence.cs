using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

/// <summary>
/// Builds the per-chan run file of docs/plugins/source-stability.md section 4. Only hashes, counts and URLs
/// without query go in; media bytes never do. Fields the contract does not list are extras and live in "harness"
/// or in a board's "details", so the contract keys keep their documented shape.
/// </summary>
internal static class StabilityEvidence
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ContractReference = new(@"^docs/plugins/source-stability\.md@[0-9a-f]{7,40}$", RegexOptions.CultureInvariant);

    internal static bool IsContractReference(string value) => ContractReference.IsMatch(value);

    internal static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The run succeeded only if every board passed every step and nothing unexpected or excepted happened.</summary>
    internal static string Outcome(StabilityChanRun chan)
    {
        bool boardsPass = chan.Boards.Count >= chan.RequiredBoards && chan.Boards.All(BoardPasses);
        return chan.Fatal == null && boardsPass && chan.Failures.Count == 0 && !chan.IntegrityExceptionUsed
            && chan.Offline is { Error: null, TransportAttempts: 0, NetworkSends: 0 } ? "pass" : "fail";
    }

    private static bool BoardPasses(StabilityBoardRun board) => board.Archive == "pass" && board.Reader == "pass"
        && board.OfflineAfterRestart == "pass" && board.Original is { IntegrityOk: true };

    internal static JsonObject Compose(StabilityChanRun chan, StabilityPhaseOne phase, string contractReference)
    {
        if (!IsContractReference(contractReference)) throw new ArgumentException("The contract reference must be docs/plugins/source-stability.md@<commit>.", nameof(contractReference));
        JsonArray boards = [];
        foreach (StabilityBoardRun board in chan.Boards)
            boards.Add(new JsonObject
            {
                ["board"] = board.Board,
                ["thread"] = board.ThreadId,
                ["requests"] = new JsonArray(board.Requests.Where(row => row.Sent).Select(row => (JsonNode)new JsonObject
                {
                    ["url"] = row.Url, ["status"] = row.Status, ["bytes"] = row.Bytes, ["step"] = row.Step, ["sha256"] = row.Sha256,
                    ["bodyFile"] = row.Step != "original" && row.Sha256 != null && row.Bytes > 0 ? "bodies/" + row.Sha256 + ".body" : null
                }).ToArray()),
                ["original"] = board.Original == null ? null : new JsonObject
                {
                    ["expectedBytes"] = board.Original.ExpectedBytes,
                    ["receivedBytes"] = board.Original.ReceivedBytes,
                    ["providerHash"] = board.Original.ProviderHash,
                    ["hashMatched"] = board.Original.HashMatched,
                    ["sha256"] = board.Original.Sha256,
                    ["integrityBasis"] = board.Original.IntegrityBasis
                },
                ["archive"] = board.Archive,
                ["reader"] = board.Reader,
                ["offlineAfterRestart"] = board.OfflineAfterRestart,
                ["details"] = new JsonObject
                {
                    ["userChosenThread"] = board.UserChosenThread,
                    ["catalogThreads"] = board.CatalogThreadCount,
                    ["selection"] = board.SelectionNote,
                    ["pinnedThreadsSkipped"] = board.PinnedSkipped,
                    ["originalsSkippedByDisplayedSize"] = board.OriginalsSkippedByDisplayedSize,
                    ["gallery"] = board.Gallery,
                    ["postCount"] = board.PostCount,
                    ["refusedRequests"] = board.Refused.Count,
                    ["providerHashes"] = new JsonArray((board.Original?.ProviderHashes ?? []).Select(value => (JsonNode)value).ToArray()),
                    ["sizeMatched"] = board.Original?.SizeMatched,
                    ["mediaId"] = board.Original?.MediaId,
                    ["extension"] = board.Original?.Extension,
                    ["productionFailure"] = board.Original?.ProductionFailure,
                    ["notes"] = new JsonArray(board.Notes.Select(value => (JsonNode)value).ToArray()),
                    ["captureWaitedMs"] = board.CaptureWaitedMs,
                    ["captureLimitMs"] = board.CaptureLimitMs,
                    ["mediaWaitedMs"] = board.MediaWaitedMs,
                    ["watchRemoved"] = board.WatchRemoved,
                    ["thread"] = board.ThreadInfo?.DeepClone(),
                    ["hostView"] = board.HostView?.DeepClone()
                }
            });
        JsonArray failures = new(chan.Failures.Select(row => (JsonNode)new JsonObject
        {
            ["board"] = row.Board, ["step"] = row.Step, ["mode"] = row.Mode, ["status"] = row.Status, ["url"] = row.Url, ["detail"] = row.Detail
        }).ToArray());
        JsonArray receipts = [];
        if (chan.ReceiptFile != null) receipts.Add(new JsonObject { ["path"] = chan.ReceiptFile, ["sha256"] = chan.ReceiptSha256 });
        foreach (StabilityFileRef log in chan.Logs) receipts.Add(new JsonObject { ["path"] = log.Path, ["sha256"] = log.Sha256 });
        DateTimeOffset start = chan.WindowStart ?? phase.StartedUtc, end = chan.WindowEnd ?? phase.FinishedUtc;
        return new JsonObject
        {
            ["kind"] = "source-stability-run",
            ["contract"] = contractReference,
            ["pluginId"] = chan.PluginId,
            ["sourceId"] = chan.SourceId,
            ["executionRevision"] = chan.ExecutionRevision,
            ["assemblySha256"] = chan.AssemblySha256,
            ["descriptionSha256"] = chan.DescriptionSha256,
            ["package"] = chan.Package,
            ["approvedByUser"] = chan.Approval,
            ["windowUtc"] = new JsonArray(Timestamp(start), Timestamp(end)),
            ["boards"] = boards,
            ["integrityExceptionUsed"] = chan.IntegrityExceptionUsed,
            ["failuresObserved"] = failures,
            ["receipts"] = receipts,
            ["outcome"] = Outcome(chan),
            ["harness"] = new JsonObject
            {
                ["network"] = phase.Network,
                ["configuration"] = phase.Configuration,
                ["testAssemblySha256"] = phase.TestAssemblySha256,
                ["packageReportSha256"] = phase.PackageReportSha256,
                ["bundleFile"] = chan.BundleFile,
                ["enabledByHarness"] = chan.EnabledByHarness,
                ["requiredBoards"] = chan.RequiredBoards,
                ["networkSends"] = chan.NetworkSends,
                ["fatal"] = chan.Fatal,
                ["boardList"] = new JsonArray(chan.BoardList.Select(row => (JsonNode)new JsonObject
                {
                    ["url"] = row.Url, ["status"] = row.Status, ["bytes"] = row.Bytes, ["outcome"] = row.Outcome
                }).ToArray()),
                ["offline"] = chan.Offline == null ? null : new JsonObject
                {
                    ["processId"] = chan.Offline.ProcessId,
                    ["onlineProcessId"] = phase.ProcessId,
                    ["networkNamespace"] = chan.Offline.NetworkNamespace,
                    ["onlineNetworkNamespace"] = phase.NetworkNamespace,
                    ["hostStartedPaused"] = chan.Offline.HostStartedPaused,
                    ["transportAttempts"] = chan.Offline.TransportAttempts,
                    ["networkSends"] = chan.Offline.NetworkSends,
                    ["profileDeleted"] = chan.Offline.ProfileDeleted,
                    ["error"] = chan.Offline.Error
                }
            }
        };
    }

    /// <summary>Checks the documented shape. Returns the problems found; empty means the file matches section 4.</summary>
    internal static IReadOnlyList<string> ValidateShape(JsonElement root)
    {
        List<string> problems = [];
        if (root.ValueKind != JsonValueKind.Object) return ["root is not an object"];
        void Require(JsonElement element, string name, JsonValueKind kind, string path, bool nullable = false)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) problems.Add(path + name + " missing");
            else if (value.ValueKind == JsonValueKind.Null ? !nullable : value.ValueKind != kind && !(kind == JsonValueKind.True && value.ValueKind == JsonValueKind.False))
                problems.Add(path + name + " has kind " + value.ValueKind);
        }
        foreach (string name in new[] { "kind", "contract", "pluginId", "sourceId", "executionRevision", "assemblySha256", "descriptionSha256", "package", "approvedByUser", "outcome" })
            Require(root, name, JsonValueKind.String, "");
        Require(root, "windowUtc", JsonValueKind.Array, "");
        Require(root, "boards", JsonValueKind.Array, "");
        Require(root, "integrityExceptionUsed", JsonValueKind.True, "");
        Require(root, "failuresObserved", JsonValueKind.Array, "");
        Require(root, "receipts", JsonValueKind.Array, "");
        if (root.TryGetProperty("kind", out JsonElement kind) && kind.GetString() != "source-stability-run") problems.Add("kind differs");
        if (root.TryGetProperty("contract", out JsonElement contract) && contract.ValueKind == JsonValueKind.String && !IsContractReference(contract.GetString()!)) problems.Add("contract is not path@commit");
        if (root.TryGetProperty("outcome", out JsonElement outcome) && outcome.ValueKind == JsonValueKind.String && outcome.GetString() is not ("pass" or "fail")) problems.Add("outcome is neither pass nor fail");
        if (root.TryGetProperty("approvedByUser", out JsonElement approval) && approval.ValueKind == JsonValueKind.String
            && !Regex.IsMatch(approval.GetString()!, @"^\d{4}-\d{2}-\d{2}, chat$")) problems.Add("approvedByUser is not '<date>, chat'");
        if (root.TryGetProperty("windowUtc", out JsonElement window) && window.ValueKind == JsonValueKind.Array)
            if (window.GetArrayLength() != 2 || window.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(value.GetString()!, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"))) problems.Add("windowUtc is not two UTC timestamps");
        if (root.TryGetProperty("receipts", out JsonElement receipts) && receipts.ValueKind == JsonValueKind.Array)
            foreach (JsonElement receipt in receipts.EnumerateArray())
            {
                Require(receipt, "path", JsonValueKind.String, "receipts[].");
                Require(receipt, "sha256", JsonValueKind.String, "receipts[].");
            }
        if (root.TryGetProperty("boards", out JsonElement boards) && boards.ValueKind == JsonValueKind.Array)
            foreach (JsonElement board in boards.EnumerateArray())
            {
                Require(board, "board", JsonValueKind.String, "boards[].");
                Require(board, "thread", JsonValueKind.String, "boards[].", nullable: true);
                Require(board, "requests", JsonValueKind.Array, "boards[].");
                Require(board, "original", JsonValueKind.Object, "boards[].", nullable: true);
                foreach (string name in new[] { "archive", "reader", "offlineAfterRestart" })
                    Require(board, name, JsonValueKind.String, "boards[].");
                if (board.TryGetProperty("requests", out JsonElement requests) && requests.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement request in requests.EnumerateArray())
                    {
                        Require(request, "url", JsonValueKind.String, "requests[].");
                        Require(request, "status", JsonValueKind.Number, "requests[].", nullable: true);
                        Require(request, "bytes", JsonValueKind.Number, "requests[].");
                    }
                if (board.TryGetProperty("original", out JsonElement original) && original.ValueKind == JsonValueKind.Object)
                {
                    Require(original, "expectedBytes", JsonValueKind.Number, "original.", nullable: true);
                    Require(original, "receivedBytes", JsonValueKind.Number, "original.");
                    Require(original, "providerHash", JsonValueKind.String, "original.", nullable: true);
                    Require(original, "hashMatched", JsonValueKind.True, "original.", nullable: true);
                    Require(original, "sha256", JsonValueKind.String, "original.");
                }
            }
        return problems;
    }

    /// <summary>plugins/Examples/&lt;Dir&gt;/Evidence/stability-&lt;YYYYMMDD&gt;.json, next to the existing research files.</summary>
    internal static string EvidencePath(string repositoryRoot, string? bundleFile, string pluginId, DateTimeOffset date)
    {
        string name = bundleFile != null && bundleFile.StartsWith("MeowChan.Plugin.", StringComparison.Ordinal) && bundleFile.EndsWith(".plugin.json", StringComparison.Ordinal)
            ? bundleFile["MeowChan.Plugin.".Length..^".plugin.json".Length] : "";
        foreach (string group in new[] { "Examples", "Official" })
        {
            string directory = Path.Combine(repositoryRoot, "plugins", group, name);
            if (name.Length > 0 && Directory.Exists(directory))
                return Path.Combine(directory, "Evidence", "stability-" + date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
        }
        throw new InvalidOperationException("No plugin directory for " + pluginId + " (" + bundleFile + ") under " + repositoryRoot);
    }
}
