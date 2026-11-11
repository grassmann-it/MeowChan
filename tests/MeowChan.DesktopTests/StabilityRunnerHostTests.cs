using System.Net;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

// Host-level tests of the stability harness. Every request is answered by an in-memory stub;
// no test here can reach a socket, and the stub site throws for every URL it does not know.
[Collection("Delivered plugin hosts")]
public sealed class StabilityRunnerHostTests(ITestOutputHelper output)
{
    private const string Contract = "docs/plugins/source-stability.md@0123456789abcdef0123456789abcdef01234567";

    private static StabilityChanApproval Chan(string sourceId, string[]? boards = null, int fromList = 0, string? listUrl = null,
        StabilityRedirect[]? redirects = null, string? assembly = null) => new("meowchan.source." + sourceId, sourceId, "2026-11-11, chat",
        (boards ?? StabilityStubSite.Boards).Select(board => new StabilityBoardRequest(board, null)).ToArray(), fromList, listUrl,
        redirects ?? [], assembly);

    private sealed class Run : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "stability-test-" + Guid.NewGuid().ToString("N")[..12]);
        internal StabilityStubSite Site { get; } = new();
        internal int NetworkClients;
        internal StabilityRunOptions Options(int boards = 3) => new()
        {
            WorkDirectory = Path.Combine(Root, "work"), EvidenceDirectory = Path.Combine(Root, "evidence"), RequiredBoards = boards,
            NetworkFactory = () => { NetworkClients++; return Site.Client(); }
        };
        internal StabilityRunOptions NoNetwork(int boards = 3) => new()
        {
            WorkDirectory = Path.Combine(Root, "work"), EvidenceDirectory = Path.Combine(Root, "evidence"), RequiredBoards = boards
        };
        public void Dispose() { try { Directory.Delete(Root, true); } catch (Exception) { } }
    }

    [Theory]
    [InlineData("lainchan", "lainchan.org")]
    [InlineData("kohlchan", "kohlchan.net")]
    public async Task HappyPathRunsThreeBoardsWithNineGetsAndSurvivesTheOfflinePhase(string sourceId, string host)
    {
        using Run run = new();
        StabilityChanApproval chan = Chan(sourceId);
        StabilityChanRun online = await StabilityRunner.RunOnlineAsync(chan, run.Options());
        output.WriteLine(JsonSerializer.Serialize(online, StabilityEvidence.Json));
        Assert.Null(online.Fatal);
        Assert.Empty(online.Failures);
        Assert.Equal(9, run.Site.Requests.Count);
        Assert.Equal(9, online.NetworkSends);
        Assert.False(online.IntegrityExceptionUsed);
        Assert.Equal(3, online.Boards.Count);
        foreach (StabilityBoardRun board in online.Boards)
        {
            Assert.Equal("123", board.ThreadId);
            Assert.Equal(["catalog", "thread", "original"], board.Requests.Select(row => row.Step));
            Assert.All(board.Requests, row => Assert.Equal(200, row.Status));
            Assert.Equal("pass", board.Archive);
            Assert.Equal("pass", board.Reader);
            Assert.Equal("pass", board.Gallery);
            Assert.NotNull(board.Original);
            Assert.True(board.Original!.IntegrityOk);
            Assert.Equal(board.Original.ExpectedBytes, board.Original.ReceivedBytes);
            Assert.Equal(StabilityStubSite.Original(host, board.Board, 0), run.Site.Requests.Single(url => url.StartsWith("GET " + StabilityStubSite.Original(host, board.Board, 0))).Substring(4));
        }
        Assert.All(online.Boards.Where(board => board.Requests.Count == 3), board => Assert.Equal(sourceId == "lainchan" ? "size+hash" : "size", board.Original!.IntegrityBasis));
        int before = run.Site.Requests.Count;

        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        Assert.Equal(before, run.Site.Requests.Count);
        Assert.NotNull(online.Offline);
        Assert.Null(online.Offline!.Error);
        Assert.Equal(0, online.Offline.TransportAttempts);
        Assert.Equal(0, online.Offline.NetworkSends);
        Assert.True(online.Offline.HostStartedPaused);
        Assert.True(online.Offline.ProfileDeleted);
        Assert.False(Directory.Exists(online.ProfileDirectory));
        Assert.All(online.Boards, board => Assert.Equal("pass", board.OfflineAfterRestart));
        Assert.Equal("pass", StabilityEvidence.Outcome(online));
    }

    // ---- scenarios ----------------------------------------------------------------------------------------------------

    private static HttpResponseMessage Redirect(string target) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(target) }, Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Html(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        StabilityStubSite.Response(null!, Encoding.UTF8.GetBytes(text), "text/html", status);

    private static IEnumerable<StabilityRequestRecord> Sent(StabilityChanRun run) => run.Boards.SelectMany(board => board.Requests);

    private async Task<StabilityChanRun> Online(Run run, StabilityChanApproval chan, int boards = 3)
    {
        StabilityChanRun result = await StabilityRunner.RunOnlineAsync(chan, run.Options(boards));
        output.WriteLine(JsonSerializer.Serialize(new { result.Fatal, result.Failures, boards = result.Boards.Select(row => new { row.Board, row.Archive, row.Notes, requests = row.Requests.Select(request => request.Step + " " + request.Url + " " + request.Status) }) }, StabilityEvidence.Json));
        return result;
    }

    [Fact]
    public async Task BoardsFromTheListCostOneExtraGetAndSkipIdsTheSourceRejects()
    {
        using Run run = new();
        StabilityChanRun online = await Online(run, Chan("capybarachan", [], 3, "https://capybarachan.org/boards.json"));
        Assert.Null(online.Fatal);
        Assert.Empty(online.Failures);
        Assert.Equal(10, run.Site.Requests.Count);
        Assert.Equal("GET https://capybarachan.org/boards.json", run.Site.Requests.First());
        Assert.Equal(StabilityStubSite.Boards, online.Boards.Select(board => board.Board));
        Assert.Equal("boardlist", Assert.Single(online.BoardList).Step);
        Assert.All(online.Boards, board => Assert.Equal("pass", board.Archive));
        Assert.Equal(10, online.NetworkSends);
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        Assert.Equal("pass", StabilityEvidence.Outcome(online));
    }

    [Fact]
    public async Task AnUnplannedRedirectIsRefusedStopsTheChanAndTheTargetIsNeverRequested()
    {
        using Run run = new();
        run.Site.Override = (url, _) => url == "https://lainchan.org/demo2/catalog.json" ? Redirect("https://www.lainchan.org/demo2/catalog.json") : null;
        StabilityChanRun online = await Online(run, Chan("lainchan"));
        Assert.Equal("pass", online.Boards[0].Archive);
        StabilityFailure failure = Assert.Single(online.Failures);
        Assert.Equal(("demo2", "catalog", "unplanned-redirect"), (failure.Board, failure.Step, failure.Mode));
        Assert.DoesNotContain(run.Site.Requests, url => url.Contains("www.lainchan.org"));
        Assert.Equal(4, run.Site.Requests.Count);
        Assert.Contains("not-run", online.Boards[2].Notes.Single());
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
        Assert.False(online.IntegrityExceptionUsed);
    }

    [Fact]
    public async Task APlannedRedirectOfTheOriginalIsFollowedOnceAndWithoutThePlanItStopsTheChan()
    {
        MakabaUboachanWaveFixtures site = new();
        string root = Path.Combine(AppContext.BaseDirectory, "stability-test-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            StabilityRunOptions Options() => new()
            {
                WorkDirectory = Path.Combine(root, "work"), EvidenceDirectory = Path.Combine(root, "evidence"), RequiredBoards = 1, NetworkFactory = site.Client
            };
            StabilityChanApproval planned = Chan("2ch", ["demo"], redirects: [new("2ch.hk", "2ch.su")]);
            StabilityChanRun online = await StabilityRunner.RunOnlineAsync(planned, Options());
            output.WriteLine(JsonSerializer.Serialize(online.Failures, StabilityEvidence.Json));
            Assert.Null(online.Fatal);
            Assert.Empty(online.Failures);
            Assert.Equal(["redirect-planned", "http-200"], Sent(online).Where(row => row.Step == "original").Select(row => row.Outcome));
            Assert.Equal(4, online.NetworkSends);
            Assert.Equal(4, site.Requests.Count);
            Assert.Equal("pass", online.Boards.Single().Archive);
            Assert.Equal("hash", online.Boards.Single().Original!.IntegrityBasis);
            await StabilityRunner.RunOfflineAsync(online, Options());
            Assert.Equal("pass", StabilityEvidence.Outcome(online));

            MakabaUboachanWaveFixtures other = new();
            StabilityRunOptions unplanned = new()
            {
                WorkDirectory = Path.Combine(root, "work2"), EvidenceDirectory = Path.Combine(root, "evidence2"), RequiredBoards = 1, NetworkFactory = other.Client
            };
            StabilityChanRun stopped = await StabilityRunner.RunOnlineAsync(Chan("2ch", ["demo"]), unplanned);
            StabilityFailure failure = Assert.Single(stopped.Failures);
            Assert.Equal(("original", "unplanned-redirect"), (failure.Step, failure.Mode));
            Assert.DoesNotContain(other.Requests, url => url.StartsWith("https://2ch.su/demo/src"));
            Assert.Equal(3, other.Requests.Count);
            await StabilityRunner.RunOfflineAsync(stopped, unplanned);
            Assert.Equal("fail", StabilityEvidence.Outcome(stopped));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    [Theory]
    [InlineData("<html><head><title>Just a moment...</title></head><body>Checking your browser</body></html>", 200, "challenge-page")]
    [InlineData("<html>Please complete the captcha</html>", 200, "challenge-page")]
    [InlineData("busy", 429, "http-429")]
    [InlineData("<html>gateway</html>", 503, "http-503")]
    public async Task AnUnexpectedCatalogResponseStopsTheChanAfterOneRequestAndLaterBoardsAreNotRun(string body, int status, string mode)
    {
        using Run run = new();
        run.Site.Override = (url, _) => url == "https://lainchan.org/demo2/catalog.json" ? Html(body, (HttpStatusCode)status) : null;
        StabilityChanRun online = await Online(run, Chan("lainchan"));
        StabilityFailure failure = Assert.Single(online.Failures);
        Assert.Equal(("demo2", "catalog", mode, status), (failure.Board, failure.Step, failure.Mode, failure.Status));
        Assert.Equal(4, run.Site.Requests.Count);
        Assert.Equal("pass", online.Boards[0].Archive);
        Assert.Equal("skipped", online.Boards[1].Archive);
        Assert.StartsWith("not-run", online.Boards[2].Notes.Single());
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        Assert.Equal("pass", online.Boards[0].OfflineAfterRestart);
        Assert.Null(online.Offline!.Error);
    }

    [Fact]
    public async Task AnOriginalThatIsHtmlInsteadOfAnImageIsAWrongContentTypeFailure()
    {
        using Run run = new();
        run.Site.Override = (url, _) => url == StabilityStubSite.Original("lainchan.org", "demo", 0) ? Html("<html>not an image</html>") : null;
        StabilityChanRun online = await Online(run, Chan("lainchan"));
        StabilityFailure failure = Assert.Single(online.Failures);
        Assert.Equal(("demo", "original", "wrong-content-type"), (failure.Board, failure.Step, failure.Mode));
        Assert.Equal(3, run.Site.Requests.Count);
        Assert.Null(online.Boards[0].Original);
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
    }

    [Theory]
    [InlineData("lainchan", "lainchan.org", false)]
    [InlineData("kohlchan", "kohlchan.net", true)]
    public async Task AnIntegrityMismatchIsAFailureWithoutAnExceptionAndOtherBoardsStillRun(string sourceId, string host, bool changeSize)
    {
        using Run run = new();
        string target = StabilityStubSite.Original(host, "demo2", 0);
        run.Site.Override = (url, request) =>
        {
            if (url != target) return null;
            byte[] bytes = run.Site.OriginalBytes(host, "demo2", 0).ToArray();
            // Same length with one changed byte breaks only the hash; an appended byte breaks the exact size too.
            if (changeSize) bytes = [.. bytes, 0];
            else bytes[^1] ^= 0xFF;
            return StabilityStubSite.Response(request, bytes, "image/png");
        };
        StabilityChanRun online = await Online(run, Chan(sourceId));
        Assert.Null(online.Fatal);
        Assert.False(online.IntegrityExceptionUsed);
        StabilityFailure failure = Assert.Single(online.Failures);
        Assert.Equal(("demo2", "original", "integrity-mismatch"), (failure.Board, failure.Step, failure.Mode));
        StabilityBoardRun bad = online.Boards[1];
        Assert.False(bad.Original!.IntegrityOk);
        if (changeSize) Assert.False(bad.Original.SizeMatched);
        else Assert.False(bad.Original.HashMatched);
        Assert.Equal("fail", bad.Archive);
        Assert.Equal("pass", online.Boards[0].Archive);
        Assert.Equal("pass", online.Boards[2].Archive);
        Assert.Equal(9, run.Site.Requests.Count);
        Assert.Equal(3, bad.Requests.Count);
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        Assert.Equal(["pass", "skipped", "pass"], online.Boards.Select(board => board.OfflineAfterRestart));
    }

    [Fact]
    public async Task OnlyOriginalsOverTheCapLeaveNoOriginalAndStopTheBoardAfterTwoRequests()
    {
        using Run run = new();
        run.Site.Override = (url, _) => url != "https://lainchan.org/demo/res/123.json" ? null : StabilityStubSite.Response(null!, Encoding.UTF8.GetBytes(
            """{"posts":[{"no":123,"resto":0,"sub":"Big","com":"<p>x</p>","time":1700000000,"tim":"demo-1000-0","ext":".png","filename":"big","fsize":3145728,"md5":"AAAAAAAAAAAAAAAAAAAAAA=="}]}"""), "application/json");
        StabilityChanRun online = await Online(run, Chan("lainchan", ["demo"]), boards: 1);
        StabilityFailure failure = Assert.Single(online.Failures);
        Assert.Equal(("demo", "original", "no-original-within-cap"), (failure.Board, failure.Step, failure.Mode));
        Assert.Equal(2, run.Site.Requests.Count);
        Assert.Null(online.Boards.Single().Original);
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
    }

    [Fact]
    public async Task AUserChosenThreadIsUsedAndOneFromAnotherBoardIsRefusedWithoutThreadRequest()
    {
        using Run run = new();
        StabilityChanApproval chosen = new("meowchan.source.lainchan", "lainchan", "2026-11-11, chat",
            [new("demo", "https://lainchan.org/demo/res/123.html")], 0, null, [], null);
        StabilityChanRun online = await Online(run, chosen, boards: 1);
        Assert.Empty(online.Failures);
        StabilityBoardRun board = online.Boards.Single();
        Assert.True(board.UserChosenThread);
        Assert.Equal("123", board.ThreadId);
        Assert.Contains("user-chosen", board.SelectionNote);
        Assert.Equal(3, run.Site.Requests.Count);
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());

        using Run second = new();
        StabilityChanApproval wrong = chosen with { Boards = [new("demo", "https://lainchan.org/demo2/res/123.html")] };
        StabilityChanRun refused = await Online(second, wrong, boards: 1);
        Assert.Equal("user-thread-not-on-board", Assert.Single(refused.Failures).Mode);
        Assert.Single(second.Site.Requests);
        Assert.Equal("fail", StabilityEvidence.Outcome(refused));
    }

    [Fact]
    public async Task AManifestAssemblyHashThatDiffersEndsTheChanBeforeAnyRequest()
    {
        using Run run = new();
        StabilityChanRun online = await Online(run, Chan("lainchan", assembly: new string('a', 64)));
        Assert.StartsWith("assembly-differs-from-manifest", online.Fatal);
        Assert.Empty(run.Site.Requests);
        Assert.Equal(0, online.NetworkSends);
        Assert.Equal("fail", StabilityEvidence.Outcome(online));
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        Assert.True(online.Offline!.ProfileDeleted);
        Assert.False(Directory.Exists(online.ProfileDirectory));
    }

    [Fact]
    public async Task APluginThatIsNotDeliveredSendsNothing()
    {
        using Run run = new();
        StabilityChanRun online = await Online(run, Chan("nonexistentchan"));
        Assert.StartsWith("plugin-not-delivered", online.Fatal);
        Assert.Empty(run.Site.Requests);
    }

    [Fact]
    public async Task AChanOutsideTheManifestOrTheOnlyListGetsNoHostAndNoRequest()
    {
        using Run run = new();
        string text = """
            {"schemaVersion":1,"sources":[
            {"pluginId":"meowchan.source.lainchan","sourceId":"lainchan","approval":"2026-11-11, chat","boards":[{"board":"demo"}]},
            {"pluginId":"meowchan.source.kohlchan","sourceId":"kohlchan","approval":"2026-11-11, chat","boards":[{"board":"demo"}]}]}
            """;
        StabilityRunManifest manifest = StabilityRunManifest.Parse(Encoding.UTF8.GetBytes(text));
        foreach (StabilityChanApproval chan in manifest.Select("lainchan"))
            await StabilityRunner.RunOnlineAsync(chan, run.Options(1));
        Assert.Equal(3, run.Site.Requests.Count);
        Assert.Equal(0, run.Site.RequestsTo("kohlchan.net"));
        Assert.Equal(1, run.NetworkClients);
        Assert.False(Directory.Exists(Path.Combine(run.Root, "work", "profile-kohlchan")));
    }

    [Fact]
    public async Task ThePlanNamesTheExactCatalogUrlsAndNeverCreatesANetworkClient()
    {
        using Run run = new();
        StabilityRunOptions options = new()
        {
            WorkDirectory = Path.Combine(run.Root, "work"), EvidenceDirectory = Path.Combine(run.Root, "evidence"),
            NetworkFactory = () => throw new InvalidOperationException("a plan must not create a network client")
        };
        StabilityPlannedChan plan = await StabilityRunner.PlanAsync(Chan("lainchan"), options);
        Assert.Null(plan.Problem);
        Assert.Equal(9, plan.MaximumGets);
        Assert.Equal(["https://lainchan.org/demo/catalog.json", "https://lainchan.org/demo2/catalog.json", "https://lainchan.org/demo3/catalog.json"],
            plan.Boards.Select(board => board.CatalogUrl));
        Assert.Contains("lainchan.org", plan.Hosts);
        Assert.Empty(run.Site.Requests);
        string text = StabilityRunLiveTests.RenderPlan([plan]);
        Assert.Contains("nothing was sent", text);
        Assert.Contains("GET https://lainchan.org/demo/catalog.json", text);
        Assert.False(Directory.Exists(Path.Combine(options.WorkDirectory, "plan-lainchan")), "the plan profile is deleted");

        StabilityPlannedChan listed = await StabilityRunner.PlanAsync(Chan("capybarachan", [], 3, "https://capybarachan.org/boards.json"), options);
        Assert.Null(listed.Problem);
        Assert.Equal(10, listed.MaximumGets);
        Assert.Equal("https://capybarachan.org/boards.json", listed.BoardListUrl);
        Assert.Contains("GET https://capybarachan.org/boards.json", StabilityRunLiveTests.RenderPlan([listed]));
    }

    [Fact]
    public async Task EvidenceMatchesTheContractShapeAndCarriesNoMediaBytes()
    {
        using Run run = new();
        StabilityChanRun online = await Online(run, Chan("lainchan", ["demo"]), boards: 1);
        await StabilityRunner.RunOfflineAsync(online, run.NoNetwork());
        StabilityPhaseOne phase = new()
        {
            Network = "stub", ProcessId = 1, StartedUtc = DateTimeOffset.UtcNow, FinishedUtc = DateTimeOffset.UtcNow, Configuration = "Debug",
            PackageRoot = AppContext.BaseDirectory
        };
        phase.Chans.Add(online);
        string text = StabilityEvidence.Compose(online, phase, Contract).ToJsonString(StabilityEvidence.Json);
        using JsonDocument document = JsonDocument.Parse(text);
        Assert.Empty(StabilityEvidence.ValidateShape(document.RootElement));
        JsonElement root = document.RootElement;
        Assert.Equal(Contract, root.GetProperty("contract").GetString());
        Assert.Equal("meowchan.source.lainchan", root.GetProperty("pluginId").GetString());
        Assert.Equal("2026-11-11, chat", root.GetProperty("approvedByUser").GetString());
        Assert.False(root.GetProperty("integrityExceptionUsed").GetBoolean());
        Assert.Equal("pass", root.GetProperty("outcome").GetString());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("assemblySha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("descriptionSha256").GetString());
        Assert.StartsWith("MeowChan.Plugin.Lainchan.plugin.json sha256:", root.GetProperty("package").GetString());
        JsonElement board = root.GetProperty("boards")[0];
        Assert.Equal(3, board.GetProperty("requests").GetArrayLength());
        JsonElement original = board.GetProperty("original");
        Assert.Equal(original.GetProperty("expectedBytes").GetInt64(), original.GetProperty("receivedBytes").GetInt64());
        Assert.True(original.GetProperty("hashMatched").GetBoolean());
        Assert.StartsWith("md5:", original.GetProperty("providerHash").GetString());
        Assert.Equal("pass", board.GetProperty("offlineAfterRestart").GetString());
        string[] keys = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["kind", "contract", "pluginId", "sourceId", "executionRevision", "assemblySha256", "descriptionSha256", "package",
            "approvedByUser", "windowUtc", "boards", "integrityExceptionUsed", "failuresObserved", "receipts", "outcome", "harness"], keys);
        foreach (string slot in new[] { 0, 1 }.Select(index => Convert.ToBase64String(run.Site.OriginalBytes("lainchan.org", "demo", index))))
            Assert.DoesNotContain(slot, text);
        Assert.True(text.Length < 32 * 1024, "evidence is metadata, not content");
        // The raw HTTP audit stays outside the evidence file and is referenced by hash.
        Assert.True(File.Exists(Path.Combine(run.Root, "evidence", "audit-lainchan.json")));
        Assert.Equal(StabilityRunner.Sha256(File.ReadAllBytes(Path.Combine(run.Root, "evidence", "audit-lainchan.json"))),
            root.GetProperty("receipts")[0].GetProperty("sha256").GetString());
    }
}
