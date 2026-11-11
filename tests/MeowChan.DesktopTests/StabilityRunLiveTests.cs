using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Http;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

/// <summary>
/// The opt-in entry point driven by scripts/run-stability-live.py. Three modes, each its own OS process:
/// plan (no network client exists), online (the only mode that can send, and only with the live switch) and offline
/// (new process, same profile, no network client). Gate: MEOWCHAN_STABILITY_RUN_MANIFEST.
/// </summary>
[Collection("Delivered plugin hosts")]
public sealed class StabilityRunLiveTests(ITestOutputHelper output)
{
    internal const string ManifestVariable = "MEOWCHAN_STABILITY_RUN_MANIFEST";
    private static string? Env(string name) => Environment.GetEnvironmentVariable("MEOWCHAN_STABILITY_RUN_" + name) is { Length: > 0 } value ? value : null;
    private static string Require(string name) => Env(name) ?? throw new InvalidOperationException("MEOWCHAN_STABILITY_RUN_" + name + " is required for this mode.");

    [StabilityRunFact]
    public async Task RunTheRequestedPhase()
    {
        string manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable(ManifestVariable)!);
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        StabilityRunManifest manifest = StabilityRunManifest.Parse(manifestBytes);
        IReadOnlyList<StabilityChanApproval> chans = manifest.Select(Env("ONLY"));
        string mode = Require("MODE");
        string work = Path.GetFullPath(Require("WORK")), evidence = Path.GetFullPath(Require("EVIDENCE"));
        string manifestSha = StabilityRunner.Sha256(manifestBytes);
        switch (mode)
        {
            case "plan": await PlanAsync(chans, work, evidence); break;
            case "online": await OnlineAsync(chans, work, evidence, manifestSha); break;
            case "offline": await OfflineAsync(chans, work, evidence, manifestSha); break;
            case "replay": await ReplayAsync(chans, work, evidence); break;
            default: throw new InvalidOperationException("MEOWCHAN_STABILITY_RUN_MODE must be plan, online, offline or replay.");
        }
    }

    /// <summary>The harness waits this long for a fetched thread to reach the archive (default 120 s).</summary>
    private static TimeSpan CaptureWait() => Env("CAPTURE_SECONDS") is { } text && int.TryParse(text, out int seconds) && seconds is >= 10 and <= 900
        ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(120);

    private async Task ReplayAsync(IReadOnlyList<StabilityChanApproval> chans, string work, string evidence)
    {
        if (chans.Count != 1) throw new InvalidOperationException("A replay needs exactly one chan (MEOWCHAN_STABILITY_RUN_ONLY).");
        string source = Path.GetFullPath(Require("REPLAY_DIR"));
        StabilityChanApproval chan = chans[0];
        if (Env("REPLAY_BOARD") is { } board)
            chan = chan with { Boards = [new(board, null)], BoardsFromList = 0, BoardListUrl = null };
        StabilityReplayHandler handler = StabilityReplayHandler.Load(source, chan.SourceId);
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(evidence);
        // The only network of a replay is the saved responses; there is no real client and no original is requested.
        StabilityRunOptions options = new()
        {
            WorkDirectory = work, EvidenceDirectory = evidence, NetworkFactory = handler.Client, Phase = "replay", SkipOriginal = true,
            RequiredBoards = Math.Max(1, chan.BoardCount), Capture = CaptureWait()
        };
        StabilityChanRun run = await StabilityRunner.RunOnlineAsync(chan, options);
        StabilityRunner.TryDelete(work);
        var boards = run.Boards.Select(row => new
        {
            row.Board, row.ThreadId, row.Archive, row.PostCount, row.CaptureWaitedMs, row.CaptureLimitMs, row.ThreadInfo, row.HostView, row.Notes
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(evidence, "replay.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, replayedFrom = source, run.SourceId, savedResponses = handler.Saved, missing = handler.Missing,
            requests = handler.Requests.ToArray(), run.Fatal, failures = run.Failures, boards
        }, StabilityEvidence.Json));
        foreach (var row in boards)
            output.WriteLine($"STABILITY_REPLAY {run.SourceId} board={row.Board} thread={row.ThreadId} archive={row.Archive} posts={row.PostCount} waitedMs={row.CaptureWaitedMs}");
        output.WriteLine($"STABILITY_REPLAY fatal={run.Fatal} failures={run.Failures.Count} missing={handler.Missing.Count}");
    }

    private async Task PlanAsync(IReadOnlyList<StabilityChanApproval> chans, string work, string evidence)
    {
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(evidence);
        // No NetworkFactory: this process has no network client, so a plan cannot send even by mistake.
        StabilityRunOptions options = new() { WorkDirectory = work, EvidenceDirectory = evidence };
        List<StabilityPlannedChan> plans = [];
        foreach (StabilityChanApproval chan in chans) plans.Add(await StabilityRunner.PlanAsync(chan, options));
        await File.WriteAllTextAsync(Path.Combine(evidence, "plan.json"), JsonSerializer.Serialize(plans, StabilityEvidence.Json));
        string text = RenderPlan(plans);
        await File.WriteAllTextAsync(Path.Combine(evidence, "plan.txt"), text);
        output.WriteLine(text);
        StabilityRunner.TryDelete(work);
        Assert.All(plans, plan => Assert.Null(plan.Problem));
    }

    private async Task OnlineAsync(IReadOnlyList<StabilityChanApproval> chans, string work, string evidence, string manifestSha)
    {
        string network = Require("NETWORK");
        StabilityStubSite? stub = null;
        Func<HttpClient> factory;
        if (network == "live")
        {
            // The single place a real network client can come into existence: only with the explicit live switch.
            if (Env("LIVE") != "1") throw new InvalidOperationException("Online mode needs MEOWCHAN_STABILITY_RUN_LIVE=1 for a live run.");
            factory = PluginHostHttp.CreateTransport;
        }
        else if (network == "stub")
        {
            stub = new();
            factory = stub.Client;
        }
        else throw new InvalidOperationException("MEOWCHAN_STABILITY_RUN_NETWORK must be live or stub.");
        Assert.False(File.Exists(Path.Combine(evidence, "phase1.json")), "An online phase never reuses the evidence directory of an earlier one.");
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(work);
        StabilityRunOptions options = new() { WorkDirectory = work, EvidenceDirectory = evidence, NetworkFactory = factory, Capture = CaptureWait() };
        StabilityPhaseOne phase = new()
        {
            Network = network, ProcessId = Environment.ProcessId, NetworkNamespace = StabilityRunner.NetworkNamespace(), StartedUtc = DateTimeOffset.UtcNow,
            Configuration = Env("CONFIGURATION"), PackageRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), ManifestSha256 = manifestSha,
            TestAssemblySha256 = StabilityRunner.Sha256(await File.ReadAllBytesAsync(typeof(StabilityRunLiveTests).Assembly.Location)),
            PackageReportSha256 = Env("PACKAGE_REPORT") is { } report ? StabilityRunner.Sha256(await File.ReadAllBytesAsync(report)) : null
        };
        foreach (StabilityChanApproval chan in chans)
        {
            StabilityChanRun run = await StabilityRunner.RunOnlineAsync(chan, options);
            phase.Chans.Add(run);
            output.WriteLine($"STABILITY_ONLINE {chan.SourceId} sends={run.NetworkSends} fatal={run.Fatal} failures={run.Failures.Count} boards={string.Join(",", run.Boards.Select(row => row.Board + ":" + row.Archive))}");
            // Each chan is written as soon as it is done: a crash later still leaves the earlier receipts.
            phase.FinishedUtc = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(evidence, "phase1.json"), JsonSerializer.Serialize(phase, StabilityEvidence.Json));
        }
        phase.FinishedUtc = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(evidence, "phase1.json"), JsonSerializer.Serialize(phase, StabilityEvidence.Json));
    }

    private async Task OfflineAsync(IReadOnlyList<StabilityChanApproval> chans, string work, string evidence, string manifestSha)
    {
        string contract = Require("CONTRACT"), repository = Path.GetFullPath(Require("REPO"));
        string phasePath = Path.Combine(evidence, "phase1.json");
        StabilityPhaseOne phase = JsonSerializer.Deserialize<StabilityPhaseOne>(await File.ReadAllTextAsync(phasePath), StabilityEvidence.Json)!;
        Assert.Equal(manifestSha, phase.ManifestSha256);
        Assert.NotEqual(Environment.ProcessId, phase.ProcessId);
        Assert.Equal(chans.Select(row => row.SourceId).Order(), phase.Chans.Select(row => row.SourceId).Order());
        // No NetworkFactory: this process cannot send. The transport inside also rejects every request.
        StabilityRunOptions options = new() { WorkDirectory = work, EvidenceDirectory = evidence };
        List<object> summary = [];
        foreach (StabilityChanRun chan in phase.Chans)
        {
            await StabilityRunner.RunOfflineAsync(chan, options);
            WrittenEvidence written = await WriteEvidenceAsync(repository, contract, phase, chan);
            summary.Add(new { chan.SourceId, outcome = StabilityEvidence.Outcome(chan), evidenceFile = written.Path, written.Problems, chan.Offline!.ProfileDeleted });
            output.WriteLine($"STABILITY_OFFLINE {chan.SourceId} outcome={StabilityEvidence.Outcome(chan)} file={written.Path}");
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "phase2.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, completedUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId, networkNamespace = StabilityRunner.NetworkNamespace(),
            onlineProcessId = phase.ProcessId, onlineNetworkNamespace = phase.NetworkNamespace, chans = summary
        }, StabilityEvidence.Json));
        // Profiles and media are gone; remove the (now empty) work directory too, but never anything the harness did not create.
        foreach (string directory in Directory.Exists(work) ? Directory.GetDirectories(work) : [])
            if (Path.GetFileName(directory).StartsWith("profile-", StringComparison.Ordinal)) StabilityRunner.TryDelete(directory);
        if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        Assert.All(phase.Chans, chan => Assert.True(chan.Offline!.ProfileDeleted, "profile of " + chan.SourceId + " was not deleted"));
    }

    private sealed record WrittenEvidence(string Path, IReadOnlyList<string> Problems);

    private static async Task<WrittenEvidence> WriteEvidenceAsync(string repository, string contract, StabilityPhaseOne phase, StabilityChanRun chan)
    {
        var node = StabilityEvidence.Compose(chan, phase, contract);
        string text = node.ToJsonString(StabilityEvidence.Json) + "\n";
        using JsonDocument document = JsonDocument.Parse(text);
        IReadOnlyList<string> problems = StabilityEvidence.ValidateShape(document.RootElement);
        string path = StabilityEvidence.EvidencePath(repository, chan.BundleFile, chan.PluginId, chan.WindowStart ?? phase.StartedUtc);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new InvalidOperationException("A run file for this day already exists and is not overwritten: " + path);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return new(path, problems);
    }

    internal static string RenderPlan(IReadOnlyList<StabilityPlannedChan> plans)
    {
        StringBuilder text = new();
        int total = 0;
        foreach (StabilityPlannedChan plan in plans)
        {
            text.AppendLine($"{plan.SourceId} ({plan.PluginId}), approved {plan.Approval}");
            if (plan.Problem != null) { text.AppendLine("  PLAN FAILED: " + plan.Problem); continue; }
            text.AppendLine("  hosts allowed: " + string.Join(", ", plan.Hosts));
            if (plan.BoardListUrl != null) text.AppendLine($"  GET {plan.BoardListUrl}  (board list, <= 8 MiB; the first accepted boards are used)");
            foreach (StabilityPlannedBoard board in plan.Boards)
            {
                text.AppendLine($"  board {board.Board}");
                text.AppendLine($"    GET {board.CatalogUrl ?? "(unknown)"}  (catalog, <= 8 MiB)");
                text.AppendLine(board.UserThread == null
                    ? $"    GET <thread endpoint of the first catalog thread with an image, shape {board.ThreadUrlShape ?? "unknown"}>  (<= 8 MiB)"
                    : $"    GET <thread endpoint of {board.UserThread}>  (<= 8 MiB)");
                text.AppendLine("    GET <first image attachment of at most 2 MiB>  (original, <= 2 MiB)");
            }
            if (plan.BoardListUrl != null) text.AppendLine("  boards: chosen from the list at run time (" + plan.Boards.Count + " planned shapes)");
            text.AppendLine($"  at most {plan.MaximumGets} GET in total (planned redirect hops included); nothing was sent");
            total += plan.MaximumGets;
        }
        text.AppendLine($"Total: at most {total} GET over {plans.Count} chans. Dry run: no request was sent.");
        return text.ToString();
    }
}

public sealed class StabilityRunFactAttribute : FactAttribute
{
    public StabilityRunFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StabilityRunLiveTests.ManifestVariable)))
            Skip = "Opt-in source-stability run requires an explicit manifest; no network requested.";
    }
}
