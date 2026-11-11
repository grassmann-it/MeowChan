using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

internal sealed class StabilityFatalException(string code, string? detail = null) : Exception(code + (detail == null ? "" : ": " + detail))
{
    internal string Code { get; } = code;
}

internal sealed class StabilityRunOptions
{
    /// <summary>Holds the throwaway profiles (and with them every downloaded byte). Deleted at the end of the offline phase.</summary>
    public required string WorkDirectory { get; init; }
    /// <summary>Receives the raw HTTP audit files. Meant to stay outside Git; evidence files reference them by SHA-256.</summary>
    public required string EvidenceDirectory { get; init; }
    /// <summary>Null means the process has no network client at all: dry run and offline phase.</summary>
    public Func<HttpClient>? NetworkFactory { get; init; }
    public int RequiredBoards { get; init; } = 3;
    public TimeSpan HostStart { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long the harness waits for a fetched thread to reach the archive. A 600 KB thread needed more than 45 s once.</summary>
    public TimeSpan Capture { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan Media { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>Names the host log of this phase (online, offline, plan, replay).</summary>
    public string Phase { get; init; } = "online";
    /// <summary>Replay only: stop each board after the thread has been parsed and archived; the original was not saved.</summary>
    public bool SkipOriginal { get; init; }
}

/// <summary>A plain-text host log in the evidence directory (outside Git). Serilog events of one phase of one chan.</summary>
internal sealed class StabilityLog
{
    private readonly object _gate = new();
    internal string Path { get; }
    internal Serilog.ILogger Logger { get; }
    private StabilityLog(string path)
    {
        Path = path;
        Logger = new Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(new Sink(this)).CreateLogger();
    }

    internal static StabilityLog Create(StabilityRunOptions options, string phase, string sourceId)
    {
        string directory = System.IO.Path.Combine(options.EvidenceDirectory, "logs");
        Directory.CreateDirectory(directory);
        return new(System.IO.Path.Combine(directory, $"{phase}-{sourceId}.log"));
    }

    internal void Add(StabilityChanRun run, StabilityRunOptions options)
    {
        if (!File.Exists(Path)) return;
        run.Logs.Add(new(System.IO.Path.GetRelativePath(options.EvidenceDirectory, Path).Replace('\\', '/'), StabilityRunner.Sha256(File.ReadAllBytes(Path))));
    }

    private sealed class Sink(StabilityLog owner) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            string line = $"{logEvent.Timestamp:O} [{logEvent.Level}] {logEvent.RenderMessage()}"
                + (logEvent.Exception == null ? "" : Environment.NewLine + logEvent.Exception) + Environment.NewLine;
            try { lock (owner._gate) File.AppendAllText(owner.Path, line); }
            catch (IOException) { /* a missing diagnostic line must not change the run */ }
        }
    }
}

internal sealed record StabilityPlannedBoard(string Board, string? CatalogUrl, string? ThreadUrlShape, string? UserThread);
internal sealed record StabilityPlannedChan(string PluginId, string SourceId, string Approval, IReadOnlyList<string> Hosts,
    string? BoardListUrl, IReadOnlyList<StabilityPlannedBoard> Boards, int MaximumGets, string? Problem);

/// <summary>
/// Runs one approved chan through the source-stability contract: catalog, thread, one original, archive, reader and gallery
/// (online phase) and the same checks again in a new host over the same profile without any network (offline phase).
/// </summary>
internal static class StabilityRunner
{
    private const string FirstThreadShapeId = "1";
    private const string SampleBoard = "demo";

    // ---- dry run ------------------------------------------------------------------------------------------------------

    /// <summary>Computes, without any network client, which requests the plugin's own code would make for each board.</summary>
    internal static async Task<StabilityPlannedChan> PlanAsync(StabilityChanApproval chan, StabilityRunOptions options)
    {
        string profile = Path.Combine(options.WorkDirectory, "plan-" + chan.SourceId);
        StabilityAuditTransport transport = new(new(chan.SourceId, [], []), network: null);
        DesktopSessionHost? host = null;
        StabilityChanRun scratch = new() { PluginId = chan.PluginId, SourceId = chan.SourceId };
        try
        {
            host = await StartHostAsync(chan, profile, transport, scratch, options, StabilityLog.Create(options, "plan", chan.SourceId).Logger);
            PluginSourceImageBoard adapter = Adapter(host, chan.SourceId);
            IPluginWorkerClient client = await StabilityWorkerAccess.GetAsync(adapter, CancellationToken.None);
            List<StabilityPlannedBoard> boards = [];
            // Boards taken from a list are unknown until the run; the shape is computed for a sample id and shown as {board}.
            foreach (StabilityBoardRequest board in chan.UsesBoardList ? [new StabilityBoardRequest(SampleBoard, null)] : chan.Boards)
            {
                Uri boardUri = adapter.Router.Canonical(SourceResourceKind.Board, board.Board);
                string? catalog = (await transport.PlanAsync(() => client.ReadCatalogAsync(new(new(chan.SourceId, board.Board)),
                    new(boardUri.AbsoluteUri, 0)))).FirstOrDefault();
                string? shape = (await transport.PlanAsync(() => client.ReadThreadAsync(new(new(chan.SourceId, board.Board, FirstThreadShapeId)),
                    new(boardUri.AbsoluteUri, 0)))).FirstOrDefault();
                if (chan.UsesBoardList)
                    boards.Add(new("{board} (the first " + chan.BoardsFromList + " listed boards the router accepts)",
                        catalog?.Replace("/" + SampleBoard + "/", "/{board}/"), shape?.Replace("/" + SampleBoard + "/", "/{board}/"), null));
                else boards.Add(new(board.Board, catalog, shape, board.ThreadUrl));
            }
            // Upper bound: every step may need one planned redirect hop when the user listed a redirect for this chan.
            int gets = (chan.UsesBoardList ? 1 : 0) + chan.BoardCount * 3 * (chan.PlannedRedirects.Count == 0 ? 1 : 2);
            return new(chan.PluginId, chan.SourceId, chan.Approval, HostsOf(adapter, chan), chan.BoardListUrl, boards, gets, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(chan.PluginId, chan.SourceId, chan.Approval, [], chan.BoardListUrl, [], 0, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (host != null) try { await StopAsync(host); } catch (Exception) { /* plan hosts hold no data */ }
            transport.Dispose();
            TryDelete(profile);
        }
    }

    // ---- online phase -------------------------------------------------------------------------------------------------

    internal static async Task<StabilityChanRun> RunOnlineAsync(StabilityChanApproval chan, StabilityRunOptions options)
    {
        StabilityChanRun run = new()
        {
            PluginId = chan.PluginId, SourceId = chan.SourceId, Approval = chan.Approval, RequiredBoards = options.RequiredBoards,
            ProfileDirectory = Path.Combine(options.WorkDirectory, "profile-" + chan.SourceId)
        };
        StabilityAuditTransport transport = new(new(chan.SourceId, [], []), options.NetworkFactory?.Invoke())
        {
            BodyDirectory = Path.Combine(options.EvidenceDirectory, "bodies")
        };
        StabilityLog log = StabilityLog.Create(options, options.Phase, chan.SourceId);
        DesktopSessionHost? host = null;
        List<StabilityFailure> own = [];
        DateTimeOffset started = DateTimeOffset.UtcNow;
        int swept = -1;
        HashSet<string> priorUrls = new(StringComparer.Ordinal);
        // After a board's watch was removed, nothing may ask for anything again: no request outside an armed step and none for an earlier board's URLs.
        void Sweep()
        {
            if (swept < 0) return;
            foreach (StabilityRequestRecord row in FindLeftovers(transport.Rows, swept, priorUrls))
                own.Add(new(run.Boards.LastOrDefault()?.Board, "leftover", "leftover-watch-request", null, row.Url, $"{row.Outcome} after the watch of {run.Boards.LastOrDefault()?.Board} was removed"));
            swept = -1;
        }
        try
        {
            host = await StartHostAsync(chan, run.ProfileDirectory, transport, run, options, log.Logger);
            PluginSourceImageBoard adapter = Adapter(host, chan.SourceId);
            IPluginWorkerClient client = await StabilityWorkerAccess.GetAsync(adapter, CancellationToken.None);
            await host.SaveRulesAsync([new MediaRule(MediaRuleScope.Global,
                types: new(MediaTypeSelectionMode.Selected, StabilityRules.ImageExtensions),
                sizeLimit: new(MediaSizeLimitMode.Limited, StabilityAuditTransport.MaximumOriginalBytes))], host.MediaRules.Revision);
            IReadOnlyList<StabilityBoardRequest> boards = await ResolveBoardsAsync(chan, adapter, transport, run, own);
            foreach (StabilityBoardRequest request in boards)
            {
                if (transport.StopMode != null || own.Any(row => row.Mode.StartsWith("fatal:", StringComparison.Ordinal))) break;
                Sweep();
                int firstSequence = transport.Rows.Count == 0 ? 1 : transport.Rows[^1].Sequence + 1;
                StabilityBoardRun board = new() { Board = request.Board, Attempted = true, UserChosenThread = request.ThreadUrl != null };
                run.Boards.Add(board);
                try { await RunBoardAsync(chan, request, board, host, adapter, client, transport, own, options); }
                catch (Exception error) when (error is not OperationCanceledException || !transport.Rows.Any())
                {
                    board.Notes.Add("board-aborted: " + error.GetType().Name + ": " + error.Message);
                    own.Add(new(request.Board, board.Original == null ? "board" : "original", "board-aborted", null, null, error.GetType().Name));
                }
                finally
                {
                    transport.CloseSteps();
                    try { await host.SetPausedAsync(true).WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { board.Notes.Add("pause-failed"); }
                    foreach (StabilityRequestRecord row in transport.Rows.Where(row => row.Sequence >= firstSequence))
                        (row.Sent ? board.Requests : board.Refused).Add(row);
                    // The production watch of this board's thread must not outlive the board: a recheck of it would hit the next board's gate.
                    await RemoveWatchesAsync(host, board);
                    foreach (StabilityRequestRecord row in board.Requests.Concat(board.Refused)) priorUrls.Add(row.Url);
                    swept = transport.Rows.Count == 0 ? 0 : transport.Rows[^1].Sequence;
                }
            }
            foreach (string id in boards.Select(row => row.Board).Except(run.Boards.Select(row => row.Board)))
                run.Boards.Add(new() { Board = id, Notes = { "not-run: " + (transport.StopMode ?? "earlier-fatal-failure") } });
        }
        catch (StabilityFatalException error) { run.Fatal = error.Message; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            run.Fatal = "unexpected: " + error.GetType().Name + ": " + error.Message;
        }
        finally
        {
            transport.CancelPending();
            if (host != null)
            {
                await WriteWorkerLogAsync(host, options, options.Phase, chan.SourceId, run);
                try { await StopAsync(host); }
                catch (Exception error) { run.Fatal ??= "host-stop: " + error.GetType().Name; }
            }
            Sweep();
            log.Add(run, options);
            IReadOnlyList<StabilityRequestRecord> rows = transport.Rows;
            run.NetworkSends = transport.ActualNetworkSends;
            run.WindowStart = rows.FirstOrDefault(row => row.Sent)?.AtUtc ?? started;
            run.WindowEnd = rows.LastOrDefault(row => row.Sent)?.AtUtc ?? DateTimeOffset.UtcNow;
            run.Failures = transport.Failures.Concat(own.Where(row => !row.Mode.StartsWith("fatal:", StringComparison.Ordinal))
                .Concat(own.Where(row => row.Mode.StartsWith("fatal:", StringComparison.Ordinal)).Select(row => row with { Mode = row.Mode["fatal:".Length..] }))).ToList();
            await WriteReceiptAsync(run, transport, options);
            transport.Dispose();
        }
        return run;
    }

    private static async Task RunBoardAsync(StabilityChanApproval chan, StabilityBoardRequest request, StabilityBoardRun board, DesktopSessionHost host,
        PluginSourceImageBoard adapter, IPluginWorkerClient client, StabilityAuditTransport transport, List<StabilityFailure> own,
        StabilityRunOptions options)
    {
        string source = chan.SourceId;
        Uri boardUri = adapter.Router.Canonical(SourceResourceKind.Board, request.Board);
        PluginOperationContext boardOperation = new(boardUri.AbsoluteUri, 0);

        // 1. Catalog: the plugin's own code names the URL (computed without network); exactly that one GET is then allowed.
        string? catalogUrl = (await transport.PlanAsync(() => client.ReadCatalogAsync(new(new(source, request.Board)), boardOperation))).FirstOrDefault();
        if (catalogUrl == null) { Fatal(own, board, "catalog", "catalog-url-unknown"); return; }
        transport.Arm(StabilityStep.Catalog, request.Board, catalogUrl);
        CatalogReadResult catalog;
        using (CancellationTokenSource limit = new(TimeSpan.FromSeconds(45)))
        {
            try { catalog = await client.ReadCatalogAsync(new(new(source, request.Board)), boardOperation, limit.Token); }
            catch (Exception error) when (error is not OperationCanceledException || transport.StopMode != null)
            {
                if (transport.StopMode == null) Fatal(own, board, "catalog", "catalog-exception:" + error.GetType().Name);
                return;
            }
        }
        if (transport.StopMode != null) return;
        if (catalog is not CatalogSnapshot snapshot)
        {
            Fatal(own, board, "catalog", "catalog-" + catalog.GetType().Name.ToLowerInvariant() + (catalog is CatalogFailure failure ? ":" + failure.Failure.Code : ""));
            return;
        }
        IReadOnlyList<SourceThreadReference> threads = snapshot.Snapshot.Threads;
        board.CatalogThreadCount = threads.Count;
        foreach (SourceThreadReference reference in threads)
            if (!adapter.Router.TryResolve(reference.Url, out SourceResourceKind kind, out string routedBoard, out string? routedThread, out _)
                || kind != SourceResourceKind.Thread || routedBoard != request.Board || routedThread != reference.Id.ThreadId)
            {
                Fatal(own, board, "catalog", "catalog-identity-mismatch");
                return;
            }

        // 2. Choose the thread in the run itself: the user's thread, or the first catalog entry with an image attachment.
        string threadId;
        Uri threadUri;
        if (request.ThreadUrl != null)
        {
            if (!adapter.Router.TryResolve(new Uri(request.ThreadUrl), out SourceResourceKind kind, out string routedBoard, out string? routedThread, out Uri? canonical)
                || kind != SourceResourceKind.Thread || routedBoard != request.Board)
            {
                board.Notes.Add("user-thread-not-on-board");
                own.Add(new(request.Board, "catalog", "user-thread-not-on-board", null, null, null));
                return;
            }
            threadId = routedThread!;
            threadUri = canonical!;
            board.SelectionNote = threads.Any(row => row.Id.ThreadId == threadId) ? "user-chosen thread, present in the catalog" : "user-chosen thread, not in the fetched catalog";
        }
        else
        {
            ThreadChoice picked = ChooseThread(threads, transport.Body(request.Board, StabilityStep.Catalog));
            SourceThreadReference? chosen = picked.Chosen;
            board.SelectionNote = picked.Note;
            board.PinnedSkipped = picked.PinnedSkipped;
            if (chosen == null)
            {
                board.Notes.Add(threads.Count == 0 ? "empty-catalog" : "no-catalog-thread-with-attachment");
                own.Add(new(request.Board, "catalog", threads.Count == 0 ? "empty-catalog" : "no-catalog-thread-with-attachment", null, null, null));
                return;
            }
            threadId = chosen.Id.ThreadId;
            threadUri = adapter.Router.Canonical(SourceResourceKind.Thread, request.Board, threadId);
        }
        board.ThreadId = threadId;
        board.ThreadUrl = threadUri.AbsoluteUri;
        board.ThreadInfo = CatalogInfo(threads, threadId, transport.Body(request.Board, StabilityStep.Catalog));

        // 3. Thread: one GET through the production watch (archive, media planning), again at the plugin-named URL.
        PluginOperationContext threadOperation = new(threadUri.AbsoluteUri, 0);
        string? threadUrl = (await transport.PlanAsync(() => client.ReadThreadAsync(new(new(source, request.Board, threadId)), threadOperation))).FirstOrDefault();
        if (threadUrl == null) { Fatal(own, board, "thread", "thread-url-unknown"); return; }
        transport.Arm(StabilityStep.Thread, request.Board, threadUrl);
        await host.SetPausedAsync(false);
        DesktopAdmissionResult added = await host.AddAsync(threadUri.AbsoluteUri);
        if (added.Status != AdmissionStatus.Added)
        {
            board.Notes.Add("thread-not-admitted: " + added.Status);
            own.Add(new(request.Board, "thread", "thread-not-admitted", null, null, added.Status.ToString()));
            return;
        }
        ResourceId resource = new(source, request.Board, threadId);
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        ArchivedThreadView? view = await WaitForThreadAsync(host, resource, transport, options.Capture);
        board.CaptureWaitedMs = (long)waited.Elapsed.TotalMilliseconds;
        board.CaptureLimitMs = (long)options.Capture.TotalMilliseconds;
        if (transport.StopMode != null) return;
        if (view == null)
        {
            board.HostView = CaptureHostView(host, threadUri.AbsoluteUri);
            Fatal(own, board, "thread", "thread-not-archived-in-time");
            return;
        }
        board.PostCount = view.PostCount;
        AddThreadInfo(board, view, transport.Body(request.Board, StabilityStep.Thread), threadId);
        if (options.SkipOriginal)
        {
            // Let the production watch plan its downloads: media it asks for is refused by the transport and shows in the audit,
            // and the host view shows whether it queued anything at all.
            await Task.Delay(TimeSpan.FromSeconds(10));
            board.HostView = CaptureHostView(host, threadUri.AbsoluteUri);
            StabilityRules.OriginalChoice? wanted = StabilityRules.ChooseOriginal(view).Choice;
            board.Notes.Add("replay: the harness would arm " + (wanted == null ? "nothing" : StabilityAuditTransport.AuditUrl(wanted.Attachment.Url!)));
            transport.CloseSteps();
            board.Archive = "thread-only";
            board.Notes.Add("replay: the thread was parsed and archived; the original step is skipped");
            return;
        }

        // 4. Original: the first image attachment within the cap, picked from what the production parser stored.
        StabilityRules.OriginalSelection selection = StabilityRules.ChooseOriginal(view);
        StabilityRules.OriginalChoice? choice = selection.Choice;
        board.OriginalsSkippedByDisplayedSize = selection.SkippedByDisplayedSize;
        if (selection.SkippedByDisplayedSize > 0)
            board.SelectionNote += "; original: skipped " + selection.SkippedByDisplayedSize + " image attachment" + (selection.SkippedByDisplayedSize == 1 ? "" : "s")
                + " whose displayed size already exceeds the 2 MiB cap";
        if (choice == null)
        {
            transport.CloseSteps();
            board.Notes.Add("no-original-within-cap");
            own.Add(new(request.Board, "original", "no-original-within-cap", null, null, "no image attachment of at most 2 MiB in the thread"));
            return;
        }
        try { transport.Arm(StabilityStep.Original, request.Board, choice.Attachment.Url!); }
        catch (LiveHarnessLimitException error)
        {
            transport.CloseSteps();
            board.Notes.Add("original-not-allowed: " + error.Code);
            own.Add(new(request.Board, "original", "original-url-not-allowed", null, StabilityAuditTransport.AuditUrl(choice.Attachment.Url!), error.Code));
            return;
        }
        waited.Restart();
        MediaWork? work = await WaitForMediaAsync(host, threadUri.AbsoluteUri, choice.Attachment.MediaId!, transport, options.Media);
        board.MediaWaitedMs = (long)waited.Elapsed.TotalMilliseconds;
        if (transport.StopMode != null) return;
        StabilityDigests? digests = transport.OriginalDigests(request.Board);
        if (digests == null)
        {
            board.HostView = CaptureHostView(host, threadUri.AbsoluteUri);
            board.Notes.Add("original-not-received");
            own.Add(new(request.Board, "original", "original-not-received", null, StabilityAuditTransport.AuditUrl(choice.Attachment.Url!), work?.Failure));
            return;
        }
        board.Original = StabilityRules.Evaluate(choice, digests);
        board.Original.ProductionFailure = work?.Status == MediaWorkStatus.Succeeded ? null : work?.Failure ?? "not-completed";
        if (board.Original.SizeMatched == false || board.Original.HashMatched == false)
        {
            string mode = "integrity-mismatch";
            string detail = (board.Original.SizeMatched == false ? $"size expected {board.Original.ExpectedBytes} received {board.Original.ReceivedBytes}" : "")
                + (board.Original.HashMatched == false ? " hash differs" : "");
            own.Add(new(request.Board, "original", mode, null, board.Original.Url, detail.Trim()));
        }
        else if (board.Original.IntegrityBasis == "none")
            own.Add(new(request.Board, "original", "no-provider-integrity-data", null, board.Original.Url, "neither a size nor a hash was declared"));
        if (work?.Status != MediaWorkStatus.Succeeded)
        {
            board.HostView = CaptureHostView(host, threadUri.AbsoluteUri);
            board.Archive = "fail";
            board.Notes.Add("production-did-not-save-the-original: " + (work?.Failure ?? "no result in time"));
            return;
        }

        // 5. Archive, reader and gallery projection at host level.
        StabilityVerification verification = await StabilityVerification.VerifyAsync(host, source, board.Board, threadId, board.Original, board.PostCount);
        board.Archive = verification.Archive;
        board.Reader = verification.Reader;
        board.Gallery = verification.Gallery;
        board.Notes.AddRange(verification.Notes);
        if (verification.ExceptionBinding)
        {
            own.Add(new(request.Board, "archive", "integrity-exception-in-binding", null, null, "the stored binding is not strict"));
            board.Notes.Add("integrity-exception-in-binding");
        }
    }

    /// <summary>Requests after a board's watch was removed that no armed step asked for, or that repeat an earlier board's URL.</summary>
    internal static IReadOnlyList<StabilityRequestRecord> FindLeftovers(IReadOnlyList<StabilityRequestRecord> rows, int afterSequence, ISet<string> priorUrls)
        => rows.Where(row => row.Sequence > afterSequence && row.Step != "plan" && (row.Step == "none" || priorUrls.Contains(row.Url))).ToList();

    /// <summary>The thread the run takes from a catalog, how it was chosen and how many pinned threads were passed over.</summary>
    internal sealed record ThreadChoice(SourceThreadReference? Chosen, string Note, int PinnedSkipped);

    /// <summary>
    /// The first catalog entry with at least one attachment that the source does not mark pinned/sticky: a pinned
    /// "endless" thread is neither representative nor small. Flags come from the saved catalog JSON; where they are unknown
    /// (an HTML catalog) the first entry with an attachment is taken. When every candidate is pinned the first one is used.
    /// </summary>
    internal static ThreadChoice ChooseThread(IReadOnlyList<SourceThreadReference> threads, byte[]? catalogBody)
    {
        bool counts = threads.Any(row => row.AttachmentCount.HasValue);
        string rule = counts ? "first catalog entry with at least one attachment" : "the catalog reports no attachment counts; first catalog entry used";
        JsonDocument? document = StabilityRules.Parse(catalogBody);
        try
        {
            List<SourceThreadReference> pinned = [];
            foreach (SourceThreadReference row in threads)
            {
                if (counts && !(row.AttachmentCount > 0)) continue;
                JsonObject? flags = document == null ? null : StabilityRules.ThreadFlags(document.RootElement, row.Id.ThreadId);
                if (Sticky(flags) != true)
                    return new(row, pinned.Count == 0 ? rule : rule + "; skipped " + pinned.Count + " pinned thread" + (pinned.Count == 1 ? "" : "s"), pinned.Count);
                pinned.Add(row);
            }
            return pinned.Count == 0 ? new(null, rule, 0)
                : new(pinned[0], rule + "; every candidate is pinned, the first pinned thread is used", 0);
        }
        finally { document?.Dispose(); }
    }

    /// <summary>Where the chosen thread stood in the catalog and what the source said about it (sticky, pinned, locked).</summary>
    private static JsonObject CatalogInfo(IReadOnlyList<SourceThreadReference> threads, string threadId, byte[]? catalogBody)
    {
        int index = threads.ToList().FindIndex(row => row.Id.ThreadId == threadId);
        SourceThreadReference? reference = index < 0 ? null : threads[index];
        JsonObject? flags = StabilityRules.ThreadFlags(catalogBody, threadId);
        return new JsonObject
        {
            ["catalogPosition"] = index < 0 ? null : index,
            ["catalogThreads"] = threads.Count,
            ["catalogCreatedAt"] = reference?.CreatedAt is { } created ? StabilityEvidence.Timestamp(created) : null,
            ["catalogReplyCount"] = reference?.ReplyCount,
            ["catalogAttachmentCount"] = reference?.AttachmentCount,
            ["catalogFlags"] = flags,
            ["flagsKnown"] = flags != null,
            ["sticky"] = Sticky(flags)
        };
    }

    internal static bool? Sticky(JsonObject? flags)
    {
        if (flags == null) return null;
        bool? found = null;
        foreach (string name in new[] { "sticky", "pinned", "pin" })
            if (flags[name] is JsonValue value && value.TryGetValue(out bool flag)) found = (found ?? false) | flag;
        return found;
    }

    private static void AddThreadInfo(StabilityBoardRun board, ArchivedThreadView view, byte[]? threadBody, string threadId)
    {
        JsonObject info = board.ThreadInfo ?? [];
        JsonObject? flags = StabilityRules.ThreadFlags(threadBody, threadId);
        info["threadFlags"] = flags;
        if (flags != null)
        {
            info["flagsKnown"] = true;
            if (Sticky(flags) is { } fromThread) info["sticky"] = (info["sticky"] as JsonValue)?.TryGetValue(out bool earlier) == true ? earlier | fromThread : fromThread;
        }
        DateTimeOffset? opened = null;
        for (int offset = 0; offset < Math.Max(1, view.PostCount) && opened == null; offset += 500)
            opened = view.GetPosts(offset, 500).Where(post => post.PostedAt.HasValue).OrderBy(post => post.Ordinal).Select(post => post.PostedAt).FirstOrDefault();
        info["openingPostedAt"] = opened is { } at ? StabilityEvidence.Timestamp(at) : null;
        info["ageDays"] = opened is { } age ? Math.Round((DateTimeOffset.UtcNow - age).TotalDays, 1) : null;
        board.ThreadInfo = info;
    }

    /// <summary>The host's own view of the watched thread and the plugin workers, for a step that did not complete.</summary>
    private static string? FailureText(FailureInfo? failure) => failure == null ? null : failure.Kind + ": " + Trim(failure.Message, 400);

    private static JsonObject CaptureHostView(DesktopSessionHost host, string threadUrl)
    {
        JsonObject result = [];
        try
        {
            DesktopSessionView view = host.GetView();
            result["state"] = view.State.ToString();
            result["startupError"] = view.StartupError;
            result["warnings"] = new JsonArray(view.Warnings.TakeLast(20).Select(text => (JsonNode)Trim(text, 400)).ToArray());
            JsonArray threads = [];
            foreach (DesktopThreadEntry entry in view.Threads)
            {
                ThreadSnapshot snapshot = entry.Snapshot;
                ThreadWorkSnapshot? work = snapshot.Work ?? host.GetThreadWorkSnapshots().FirstOrDefault(row => row.Url == snapshot.Url);
                threads.Add(new JsonObject
                {
                    ["url"] = StabilityAuditTransport.AuditUrl(snapshot.Url),
                    ["isChosenThread"] = snapshot.Url == threadUrl,
                    ["activity"] = snapshot.Activity.ToString(),
                    ["sourceState"] = snapshot.SourceState.ToString(),
                    ["lastCheckFailure"] = snapshot.LastCheckFailure == null ? null : new JsonObject
                    {
                        ["kind"] = snapshot.LastCheckFailure.Kind.ToString(), ["message"] = Trim(snapshot.LastCheckFailure.Message, 600),
                        ["diagnosticCode"] = snapshot.LastCheckFailure.DiagnosticCode, ["httpStatus"] = (int?)snapshot.LastCheckFailure.HttpStatusCode
                    },
                    ["requestProgress"] = snapshot.RequestProgress == null ? null : new JsonObject
                    {
                        ["phase"] = snapshot.RequestProgress.Phase.ToString(), ["attempt"] = snapshot.RequestProgress.Attempt,
                        ["delaySeconds"] = snapshot.RequestProgress.Delay?.TotalSeconds
                    },
                    ["contentCaptureFailure"] = FailureText(snapshot.Content?.CaptureFailure),
                    ["contentSelectionFailure"] = FailureText(snapshot.Content?.SelectionFailure),
                    ["knownMedia"] = snapshot.KnownMedia,
                    ["completedMedia"] = snapshot.CompletedMedia,
                    ["work"] = work == null ? null : new JsonObject
                    {
                        ["intent"] = work.Intent.ToString(), ["source"] = work.Source.ToString(), ["capturePending"] = work.CapturePending, ["media"] = work.Media.Count
                    }
                });
            }
            result["threads"] = threads;
        }
        catch (Exception error) when (error is not OperationCanceledException) { result["viewError"] = error.GetType().Name + ": " + Trim(error.Message, 300); }
        result["workers"] = Workers(host, 30);
        return result;
    }

    private static JsonArray Workers(DesktopSessionHost host, int logLines)
    {
        JsonArray workers = [];
        try
        {
            foreach (PluginWorkerSnapshot worker in host.Plugins?.Runtime.GetSnapshots() ?? [])
                workers.Add(new JsonObject
                {
                    ["state"] = worker.State, ["processId"] = worker.ProcessId, ["failureCode"] = worker.FailureCode,
                    ["failureMessage"] = worker.FailureMessage is { } message ? Trim(message, 600) : null,
                    ["logs"] = new JsonArray(worker.Logs.TakeLast(logLines).Select(line => (JsonNode)Trim(line, 500)).ToArray())
                });
        }
        catch (Exception error) when (error is not OperationCanceledException) { workers.Add(new JsonObject { ["error"] = error.GetType().Name }); }
        return workers;
    }

    private static string Trim(string text, int length) => text.Length <= length ? text : text[..length] + "...";

    /// <summary>Full worker log of the phase, written before the host stops. Outside Git, referenced by SHA-256.</summary>
    private static async Task WriteWorkerLogAsync(DesktopSessionHost host, StabilityRunOptions options, string phase, string sourceId, StabilityChanRun run)
    {
        try
        {
            string directory = Path.Combine(options.EvidenceDirectory, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"workers-{phase}-{sourceId}.json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, phase, workers = Workers(host, 400) }, StabilityEvidence.Json);
            await File.WriteAllBytesAsync(path, bytes);
            run.Logs.Add(new($"logs/workers-{phase}-{sourceId}.json", Sha256(bytes)));
        }
        catch (Exception) { /* diagnostics only */ }
    }

    /// <summary>Stops the production watch of every thread of this board through the host's own removal path.</summary>
    private static async Task RemoveWatchesAsync(DesktopSessionHost host, StabilityBoardRun board)
    {
        try
        {
            DesktopThreadEntry[] entries = host.GetView().Threads.ToArray();
            foreach (DesktopThreadEntry entry in entries) await host.RemoveThreadAsync(entry.WatchId).WaitAsync(TimeSpan.FromSeconds(20));
            board.WatchRemoved = entries.Length > 0;
            if (entries.Length > 0 && host.GetView().Threads.Count != 0) board.Notes.Add("watch-still-listed-after-removal");
        }
        catch (Exception error) when (error is not OperationCanceledException) { board.Notes.Add("watch-removal-failed: " + error.GetType().Name); }
    }

    private static void Fatal(List<StabilityFailure> own, StabilityBoardRun board, string step, string mode)
    {
        board.Notes.Add(mode);
        own.Add(new(board.Board, step, "fatal:" + mode, null, null, null));
    }

    private static async Task<IReadOnlyList<StabilityBoardRequest>> ResolveBoardsAsync(StabilityChanApproval chan, PluginSourceImageBoard adapter,
        StabilityAuditTransport transport, StabilityChanRun run, List<StabilityFailure> own)
    {
        if (!chan.UsesBoardList) return chan.Boards;
        // The one extra GET: the board list the manifest names. The first boards that the source's router accepts are used.
        transport.Arm(StabilityStep.BoardList, null, chan.BoardListUrl!);
        byte[] body;
        using (HttpClient client = transport.Client())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MeowChan");
            using HttpRequestMessage request = new(HttpMethod.Get, chan.BoardListUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.8");
            try
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(45));
                using HttpResponseMessage response = await client.SendAsync(request, limit.Token);
                body = await response.Content.ReadAsByteArrayAsync(limit.Token);
                if (!response.IsSuccessStatusCode) body = [];
            }
            catch (Exception error) when (error is HttpRequestException or LiveHarnessLimitException or OperationCanceledException)
            {
                body = [];
            }
        }
        run.BoardList = transport.Rows.Where(row => row.Step == "boardlist").ToList();
        if (transport.StopMode != null || body.Length == 0)
        {
            if (transport.StopMode == null) own.Add(new(null, "boardlist", "fatal:board-list-unavailable", null, null, null));
            return [];
        }
        List<StabilityBoardRequest> selected = [];
        try
        {
            foreach (string id in StabilityRules.ParseBoardList(body))
            {
                if (selected.Count == chan.BoardsFromList) break;
                if (id.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]*$")) continue;
                if (adapter.Router.TryResolve(new Uri(adapter.BaseUri, "/" + id + "/"), out SourceResourceKind kind, out string board, out _, out _)
                    && kind == SourceResourceKind.Board && board == id)
                    selected.Add(new(id, null));
            }
        }
        catch (JsonException) { own.Add(new(null, "boardlist", "fatal:board-list-unreadable", null, null, null)); return []; }
        if (selected.Count == 0) own.Add(new(null, "boardlist", "fatal:board-list-has-no-accepted-board", null, null, null));
        return selected;
    }

    private static async Task<ArchivedThreadView?> WaitForThreadAsync(DesktopSessionHost host, ResourceId resource,
        StabilityAuditTransport transport, TimeSpan limit)
    {
        using CancellationTokenSource deadline = new(limit);
        try
        {
            while (true)
            {
                if (transport.StopMode != null) return null;
                await using ArchiveReader reader = await host.OpenArchiveReaderAsync(deadline.Token);
                ArchivedThreadView? view = await reader.ReadThreadAsync(resource, deadline.Token);
                if (view is { PostCount: > 0 }) return view;
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<MediaWork?> WaitForMediaAsync(DesktopSessionHost host, string threadUrl, string mediaId,
        StabilityAuditTransport transport, TimeSpan limit)
    {
        using CancellationTokenSource deadline = new(limit);
        MediaWork? last = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                last = host.GetThreadWorkSnapshots().Where(row => row.Url == threadUrl).SelectMany(row => row.Media)
                    .SingleOrDefault(row => row.MediaIdentifier == mediaId && row.Variant == "original");
                if (last?.Status == MediaWorkStatus.Succeeded) return last;
                if (transport.StopMode != null) return last;
                if (last?.Failure != null && last.LastAttemptAt != null)
                {
                    // The failure is durable before the worker retires; one bounded grace period lets queue counters settle.
                    await Task.Delay(300, deadline.Token);
                    return host.GetThreadWorkSnapshots().Where(row => row.Url == threadUrl).SelectMany(row => row.Media)
                        .SingleOrDefault(row => row.MediaIdentifier == mediaId && row.Variant == "original") ?? last;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) { return last; }
    }

    // ---- offline phase ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the same profile in a host that has no network client and a transport that rejects everything.
    /// Run this in a different OS process than <see cref="RunOnlineAsync"/> to prove the restart.
    /// </summary>
    internal static async Task RunOfflineAsync(StabilityChanRun chan, StabilityRunOptions options)
    {
        StabilityOfflineRun offline = new() { ProcessId = Environment.ProcessId, NetworkNamespace = NetworkNamespace() };
        chan.Offline = offline;
        string? profile = chan.ProfileDirectory;
        if (profile == null || !Directory.Exists(profile))
        {
            // A chan that failed before it stored anything has no profile to reopen; one that did is an error.
            offline.ProfileDeleted = true;
            if (chan.Boards.Any(board => board.Archive == "pass")) offline.Error = "profile-missing";
            return;
        }
        StabilityAuditTransport transport = new(new(chan.SourceId, [], []), network: null) { RejectAll = true };
        StabilityLog log = StabilityLog.Create(options, "offline", chan.SourceId);
        DesktopSessionHost? host = null;
        try
        {
            if (chan.Boards.Any(board => board.Archive == "pass"))
            {
                host = CreateHost(profile, transport, log.Logger);
                await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
                if (host.State != DesktopSessionState.Ready) throw new StabilityFatalException("offline-host-not-ready", host.StartupError);
                offline.HostStartedPaused = host.IsPaused;
                PluginProfileSnapshot installed = await host.Plugins!.ProfileStore.ReadAsync();
                PluginActivationSnapshot plugin = installed.Plugins.Single(row => row.PluginId == chan.PluginId);
                if (plugin.Active?.ExecutionRevision != chan.ExecutionRevision || plugin.Active?.Artifact.AssemblySha256 != chan.AssemblySha256)
                    throw new StabilityFatalException("offline-build-differs-from-online");
                foreach (StabilityBoardRun board in chan.Boards.Where(row => row.Archive == "pass"))
                {
                    StabilityVerification verification = await StabilityVerification.VerifyAsync(host, chan.SourceId, board.Board, board.ThreadId!, board.Original!, board.PostCount);
                    board.OfflineAfterRestart = verification.Archive == "pass" && verification.Reader == "pass" && verification.Gallery == "pass" ? "pass" : "fail";
                    board.Notes.AddRange(verification.Notes.Select(note => "offline: " + note));
                }
            }
            offline.TransportAttempts = transport.Rows.Count;
            offline.NetworkSends = transport.ActualNetworkSends;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            offline.Error = error.GetType().Name + ": " + error.Message;
            foreach (StabilityBoardRun board in chan.Boards.Where(row => row.Archive == "pass" && row.OfflineAfterRestart != "pass")) board.OfflineAfterRestart = "fail";
        }
        finally
        {
            if (host != null)
            {
                await WriteWorkerLogAsync(host, options, "offline", chan.SourceId, chan);
                try { await StopAsync(host); }
                catch (Exception error) { offline.Error ??= "offline-host-stop: " + error.GetType().Name; }
            }
            log.Add(chan, options);
            transport.Dispose();
            offline.ProfileDeleted = TryDelete(profile);
        }
    }

    // ---- shared helpers -----------------------------------------------------------------------------------------------

    internal static DesktopSessionHost CreateHost(string profile, StabilityAuditTransport transport, Serilog.ILogger? logger = null) => new(profile, providers: null,
        logger: logger,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(new RejectUpdates()), disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);

    private static async Task<DesktopSessionHost> StartHostAsync(StabilityChanApproval chan, string profile, StabilityAuditTransport transport,
        StabilityChanRun run, StabilityRunOptions options, Serilog.ILogger? logger)
    {
        Directory.CreateDirectory(profile);
        new Config
        {
            DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
            AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile);
        DesktopSessionHost host = CreateHost(profile, transport, logger);
        try
        {
            await host.StartAsync().WaitAsync(options.HostStart);
            if (host.State != DesktopSessionState.Ready) throw new StabilityFatalException("host-not-ready", host.StartupError);
            PluginProfileSnapshot installed = await host.Plugins!.ProfileStore.ReadAsync();
            PluginActivationSnapshot plugin = installed.Plugins.SingleOrDefault(row => row.PluginId == chan.PluginId)
                ?? throw new StabilityFatalException("plugin-not-delivered", chan.PluginId);
            if (plugin.Active == null) throw new StabilityFatalException("plugin-has-no-active-release", plugin.Problem);
            if (!plugin.Enabled)
            {
                // The ordinary strict enable path: it grants no integrity exception and records nothing beyond the on/off switch.
                installed = await host.Plugins.ProfileStore.SetEnabledAsync(chan.PluginId, true, installed.Revision);
                plugin = installed.Plugins.Single(row => row.PluginId == chan.PluginId);
                run.EnabledByHarness = true;
            }
            PluginReleaseSnapshot active = plugin.Active!;
            PluginCandidateSnapshot candidate = plugin.Candidates.Single();
            byte[] bundleBytes = await File.ReadAllBytesAsync(candidate.CandidatePath!);
            PluginBundleManifest bundle = JsonSerializer.Deserialize<PluginBundleManifest>(bundleBytes)!;
            string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", bundle.AssemblyRelativePath);
            string deliveredHash = Sha256(await File.ReadAllBytesAsync(delivered));
            if (!string.Equals(deliveredHash, active.Artifact.AssemblySha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(deliveredHash, Sha256(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), StringComparison.OrdinalIgnoreCase))
                throw new StabilityFatalException("active-assembly-differs-from-delivered-bundle");
            run.ExecutionRevision = active.ExecutionRevision;
            run.AssemblySha256 = deliveredHash;
            run.DescriptionSha256 = SourceWording.DescriptionFingerprint(active.Description);
            run.BundleFile = Path.GetFileName(candidate.CandidatePath);
            run.Package = run.BundleFile + " sha256:" + Sha256(bundleBytes);
            if (chan.ExpectedAssemblySha256 != null && !string.Equals(chan.ExpectedAssemblySha256, deliveredHash, StringComparison.OrdinalIgnoreCase))
                throw new StabilityFatalException("assembly-differs-from-manifest", deliveredHash);
            if (host.Config.SourceMediaIntegrityPermissions.Count != 0 || host.GetSourceMediaIntegrityPolicies().Any(row => row.IsGranted
                || row.Snapshot.AllowProviderSizeMd5Mismatch))
                throw new StabilityFatalException("integrity-exception-present");
            PluginSourceImageBoard adapter = await WaitForAdapterAsync(host, chan.SourceId);
            List<string> hosts = HostsOf(adapter, chan).ToList();
            transport.UpdatePolicy(new(chan.SourceId, hosts, chan.PlannedRedirects));
            return host;
        }
        catch
        {
            try { await StopAsync(host); } catch (Exception) { /* the original failure is the useful one */ }
            throw;
        }
    }

    private static IReadOnlyList<string> HostsOf(PluginSourceImageBoard adapter, StabilityChanApproval chan)
    {
        HashSet<string> hosts = new(adapter.Description.HttpHosts.Select(row => row.Host), StringComparer.OrdinalIgnoreCase);
        foreach (StabilityRedirect redirect in chan.PlannedRedirects) { hosts.Add(redirect.From); hosts.Add(redirect.To); }
        if (chan.BoardListUrl != null) hosts.Add(new Uri(chan.BoardListUrl).IdnHost);
        return hosts.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<PluginSourceImageBoard> WaitForAdapterAsync(DesktopSessionHost host, string sourceId)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            PluginSourceImageBoard? adapter = host.Plugins!.Sources.FirstOrDefault(row => row.Name == sourceId && row.Enabled);
            if (adapter != null) return adapter;
            try { await Task.Delay(100, deadline.Token); }
            catch (OperationCanceledException) { throw new StabilityFatalException("source-adapter-not-available", sourceId); }
        }
    }

    private static PluginSourceImageBoard Adapter(DesktopSessionHost host, string sourceId)
        => host.Plugins!.Sources.Single(row => row.Name == sourceId);

    private static async Task WriteReceiptAsync(StabilityChanRun run, StabilityAuditTransport transport, StabilityRunOptions options)
    {
        try
        {
            Directory.CreateDirectory(options.EvidenceDirectory);
            string name = "audit-" + run.SourceId + ".json";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, run.SourceId, run.PluginId, run.ExecutionRevision, run.AssemblySha256,
                networkSends = transport.ActualNetworkSends, failures = transport.Failures, rows = transport.Rows
            }, StabilityEvidence.Json);
            await File.WriteAllBytesAsync(Path.Combine(options.EvidenceDirectory, name), bytes);
            run.ReceiptFile = name;
            run.ReceiptSha256 = Sha256(bytes);
        }
        catch (Exception error) { run.Fatal ??= "receipt-write: " + error.GetType().Name; }
    }

    internal static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(worker => worker.ProcessId.HasValue).Select(worker => worker.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(25));
        if (host.State != DesktopSessionState.Stopped) throw new InvalidOperationException("Host did not stop.");
        foreach (int pid in pids)
            try { using Process process = Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("A plugin worker survived the host."); }
            catch (ArgumentException) { }
    }

    internal static bool TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception) { /* reported through the missing deletion flag */ }
        return !Directory.Exists(directory);
    }

    internal static string? NetworkNamespace()
    {
        try { return OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/net").LinkTarget : null; }
        catch (Exception) { return null; }
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class RejectUpdates : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new LiveHarnessLimitException("updates-disabled");
    }
}

/// <summary>The worker behind a source adapter, for catalog and thread reads that need the parsed attachment counts.</summary>
internal static class StabilityWorkerAccess
{
    private static readonly FieldInfo Field = typeof(PluginSourceImageBoard).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PluginSourceImageBoard no longer keeps its worker accessor in _worker; update the stability harness.");

    internal static Task<IPluginWorkerClient> GetAsync(PluginSourceImageBoard adapter, CancellationToken token)
        => ((Func<CancellationToken, Task<IPluginWorkerClient>>)Field.GetValue(adapter)!)(token);
}

internal sealed record StabilityVerification(string Archive, string Reader, string Gallery, bool ExceptionBinding, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Archive: the thread is archived and the original is a verified, readable asset with the received bytes.
    /// Reader: the archived thread still has its posts and the attachment resolves to that asset through a strict binding.
    /// Gallery: the gallery projection of the thread contains that attachment, bound to the same asset.
    /// </summary>
    internal static async Task<StabilityVerification> VerifyAsync(DesktopSessionHost host, string sourceId, string board, string threadId,
        StabilityOriginalRun original, int expectedPostCount)
    {
        List<string> notes = [];
        string archive = "fail", reader = "fail", gallery = "fail";
        bool exception = false;
        try
        {
            ResourceId id = new(sourceId, board, threadId);
            await using ArchiveReader archiveReader = await host.OpenArchiveReaderAsync();
            ArchivedThreadView? view = await archiveReader.ReadThreadAsync(id);
            if (view == null) { notes.Add("thread-not-archived"); return new(archive, reader, gallery, false, notes); }
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
            MediaAssetReference[] assets = snapshot.GetPage().Where(row => row.MediaIdentifier == original.MediaId && row.Variant == "original").ToArray();
            if (assets.Length != 1) notes.Add("saved-assets-for-the-original: " + assets.Length);
            else
            {
                MediaAssetReference saved = assets[0];
                await using Stream verified = await snapshot.OpenVerifiedReadAsync(saved.AssetId);
                using MemoryStream bytes = new();
                await verified.CopyToAsync(bytes);
                string hash = StabilityRunner.Sha256(bytes.ToArray());
                if (!string.Equals(saved.Sha256, original.Sha256, StringComparison.OrdinalIgnoreCase)) notes.Add("asset-hash-differs-from-received-bytes");
                else if (hash != original.Sha256) notes.Add("verified-read-differs-from-received-bytes");
                else if (bytes.Length != original.ReceivedBytes) notes.Add("verified-read-length-differs");
                else archive = "pass";
            }
            CapturedPost? post = null;
            CapturedAttachment? attachment = null;
            for (int offset = 0; offset < Math.Max(1, view.PostCount) && post == null; offset += 500)
                foreach (CapturedPost candidate in view.GetPosts(offset, 500))
                    if (candidate.PostId == original.PostId)
                    {
                        post = candidate;
                        attachment = candidate.Attachments?.SingleOrDefault(row => row.MediaId == original.MediaId && row.Variant == "original");
                    }
            if (view.PostCount != expectedPostCount) notes.Add($"post-count {view.PostCount} differs from {expectedPostCount}");
            else if (post == null || attachment == null) notes.Add("archived-post-or-attachment-missing");
            else if (StabilityAuditTransport.AuditUrl(attachment.Url ?? "") != original.Url) notes.Add("archived-attachment-url-differs");
            else
            {
                ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(snapshot, id, post.PostId, attachment) as ResolvedMediaAttachment;
                if (resolved?.Binding == null || resolved.IsLegacy) notes.Add("attachment-has-no-binding");
                else if (resolved.Binding.Acceptance.Kind != MediaAcceptanceKind.Strict) { exception = true; notes.Add("binding-is-not-strict"); }
                else if (!string.Equals(resolved.Asset.Sha256, original.Sha256, StringComparison.OrdinalIgnoreCase)) notes.Add("bound-asset-hash-differs");
                else
                {
                    reader = "pass";
                    GalleryEntry? entry = GallerySelection.Build(view, [snapshot]).Entries
                        .SingleOrDefault(row => row.Post?.PostId == post.PostId && row.Attachment?.Position == attachment.Position);
                    if (entry == null) notes.Add("gallery-entry-missing");
                    else if (entry.Asset.AssetId != resolved.Asset.AssetId) notes.Add("gallery-entry-bound-to-another-asset");
                    else gallery = "pass";
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            notes.Add("verification-error: " + error.GetType().Name + ": " + error.Message);
        }
        return new(archive, reader, gallery, exception, notes);
    }
}
