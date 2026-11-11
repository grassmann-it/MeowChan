using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    public TimeSpan Capture { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan Media { get; init; } = TimeSpan.FromSeconds(90);
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
            host = await StartHostAsync(chan, profile, transport, scratch, options);
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
        StabilityAuditTransport transport = new(new(chan.SourceId, [], []), options.NetworkFactory?.Invoke());
        DesktopSessionHost? host = null;
        List<StabilityFailure> own = [];
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            host = await StartHostAsync(chan, run.ProfileDirectory, transport, run, options);
            PluginSourceImageBoard adapter = Adapter(host, chan.SourceId);
            IPluginWorkerClient client = await StabilityWorkerAccess.GetAsync(adapter, CancellationToken.None);
            await host.SaveRulesAsync([new MediaRule(MediaRuleScope.Global,
                types: new(MediaTypeSelectionMode.Selected, StabilityRules.ImageExtensions),
                sizeLimit: new(MediaSizeLimitMode.Limited, StabilityAuditTransport.MaximumOriginalBytes))], host.MediaRules.Revision);
            IReadOnlyList<StabilityBoardRequest> boards = await ResolveBoardsAsync(chan, adapter, transport, run, own);
            foreach (StabilityBoardRequest request in boards)
            {
                if (transport.StopMode != null || own.Any(row => row.Mode.StartsWith("fatal:", StringComparison.Ordinal))) break;
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
                try { await StopAsync(host); }
                catch (Exception error) { run.Fatal ??= "host-stop: " + error.GetType().Name; }
            }
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
            SourceThreadReference? chosen;
            if (threads.Any(row => row.AttachmentCount.HasValue))
            {
                chosen = threads.FirstOrDefault(row => row.AttachmentCount > 0);
                board.SelectionNote = "first catalog entry with at least one attachment";
            }
            else
            {
                chosen = threads.FirstOrDefault();
                board.SelectionNote = "the catalog reports no attachment counts; first catalog entry used";
            }
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
        ArchivedThreadView? view = await WaitForThreadAsync(host, resource, transport, options.Capture);
        if (transport.StopMode != null) return;
        if (view == null) { Fatal(own, board, "thread", "thread-not-archived-in-time"); return; }
        board.PostCount = view.PostCount;

        // 4. Original: the first image attachment within the cap, picked from what the production parser stored.
        StabilityRules.OriginalChoice? choice = StabilityRules.ChooseOriginal(view);
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
        MediaWork? work = await WaitForMediaAsync(host, threadUri.AbsoluteUri, choice.Attachment.MediaId!, transport, options.Media);
        if (transport.StopMode != null) return;
        StabilityDigests? digests = transport.OriginalDigests(request.Board);
        if (digests == null)
        {
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
        DesktopSessionHost? host = null;
        try
        {
            if (chan.Boards.Any(board => board.Archive == "pass"))
            {
                host = CreateHost(profile, transport);
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
                try { await StopAsync(host); }
                catch (Exception error) { offline.Error ??= "offline-host-stop: " + error.GetType().Name; }
            transport.Dispose();
            offline.ProfileDeleted = TryDelete(profile);
        }
    }

    // ---- shared helpers -----------------------------------------------------------------------------------------------

    internal static DesktopSessionHost CreateHost(string profile, StabilityAuditTransport transport) => new(profile, providers: null,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(new RejectUpdates()), disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);

    private static async Task<DesktopSessionHost> StartHostAsync(StabilityChanApproval chan, string profile, StabilityAuditTransport transport,
        StabilityChanRun run, StabilityRunOptions options)
    {
        Directory.CreateDirectory(profile);
        new Config
        {
            DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
            AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile);
        DesktopSessionHost host = CreateHost(profile, transport);
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
