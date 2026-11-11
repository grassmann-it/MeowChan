using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// Offline unit tests of the stability harness pieces: manifest, audit transport, pure rules, evidence shape.
// Every network call here ends in an in-memory handler; nothing in this file can reach a socket.
public sealed class StabilityHarnessUnitTests
{
    // ---- manifest -----------------------------------------------------------------------------------------------------

    private const string Lainchan = """{"pluginId":"meowchan.source.lainchan","sourceId":"lainchan","approval":"2026-11-11, chat","boards":[{"board":"sec"},{"board":"lit","thread":"https://lainchan.org/lit/res/5.html"},{"board":"music"}]}""";
    private const string Capybara = """{"pluginId":"meowchan.source.capybarachan","sourceId":"capybarachan","approval":"2026-11-11, chat","boardsFromList":3,"boardListUrl":"https://capybarachan.org/boards.js"}""";

    private static string Manifest(params string[] sources) => $$"""{"schemaVersion":1,"sources":[{{string.Join(",", sources)}}]}""";
    private static StabilityRunManifest Parse(string text) => StabilityRunManifest.Parse(Encoding.UTF8.GetBytes(text));
    private static void Rejected(string text) => Assert.IsType<StabilityManifestException>(Record.Exception(() => Parse(text)));

    [Fact]
    public void ManifestParsesBoardsListsRedirectsAndTheUserThread()
    {
        StabilityRunManifest manifest = Parse(Manifest(Lainchan, Capybara.Replace("}", ",\"plannedRedirects\":[{\"from\":\"a.example\",\"to\":\"b.example\"}]}")));
        StabilityChanApproval lain = manifest.Sources[0];
        Assert.Equal(["sec", "lit", "music"], lain.Boards.Select(row => row.Board));
        Assert.Equal("https://lainchan.org/lit/res/5.html", lain.Boards[1].ThreadUrl);
        Assert.Null(lain.Boards[0].ThreadUrl);
        Assert.False(lain.UsesBoardList);
        StabilityChanApproval capy = manifest.Sources[1];
        Assert.True(capy.UsesBoardList);
        Assert.Equal(3, capy.BoardsFromList);
        Assert.Equal("https://capybarachan.org/boards.js", capy.BoardListUrl);
        Assert.Equal(new StabilityRedirect("a.example", "b.example"), Assert.Single(capy.PlannedRedirects));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"sources":[],"extra":1}""")]
    [InlineData("""{"schemaVersion":2,"sources":[{"pluginId":"meowchan.source.x","sourceId":"x","approval":"2026-11-11, chat","boards":[{"board":"a"}]}]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"sources":[]}""")]
    [InlineData("not json")]
    public void ManifestRejectsTheWrongEnvelope(string text) => Rejected(text);

    [Theory]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"retries\":2")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\",\"extra\":1}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\"")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"boardsFromList\":2,\"boardListUrl\":\"https://x.example/b\"")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boardsFromList\":2")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"boardListUrl\":\"https://x.example/b\"")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boardsFromList\":4,\"boardListUrl\":\"https://x.example/b\"")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"},{\"board\":\"b\"},{\"board\":\"c\"},{\"board\":\"d\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"},{\"board\":\"a\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11\",\"boards\":[{\"board\":\"a\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-13-40, chat\",\"boards\":[{\"board\":\"a\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, mail\",\"boards\":[{\"board\":\"a\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"../a\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\",\"thread\":\"https://user:pw@x.example/a/res/1.html\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\",\"thread\":\"https://x.example:8443/a/res/1.html\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\",\"thread\":\"https://127.0.0.1/a/res/1.html\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"plannedRedirects\":[{\"from\":\"a.example\",\"to\":\"a.example\"}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"plannedRedirects\":[{\"from\":\"a.example\",\"to\":\"b.example\",\"x\":1}]")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}],\"expectedAssemblySha256\":\"abc\"")]
    [InlineData("\"pluginId\":\"meowchan.source.x\",\"sourceId\":\"x\",\"sourceId\":\"y\",\"approval\":\"2026-11-11, chat\",\"boards\":[{\"board\":\"a\"}]")]
    public void ManifestFailsClosedOnAnythingUnknownOrOutOfRange(string source) => Rejected(Manifest("{" + source + "}"));

    [Fact]
    public void ManifestRejectsTheSameChanTwice() => Rejected(Manifest(Lainchan, Lainchan));

    [Fact]
    public void SelectOnlyNarrowsAndNeverAdds()
    {
        StabilityRunManifest manifest = Parse(Manifest(Lainchan, Capybara));
        Assert.Equal(2, manifest.Select(null).Count);
        Assert.Equal("capybarachan", Assert.Single(manifest.Select("capybarachan")).SourceId);
        Assert.Equal(2, manifest.Select("capybarachan, lainchan").Count);
        Assert.IsType<StabilityManifestException>(Record.Exception(() => manifest.Select("kohlchan")));
        Assert.IsType<StabilityManifestException>(Record.Exception(() => manifest.Select("lainchan,lainchan")));
        Assert.IsType<StabilityManifestException>(Record.Exception(() => manifest.Select(" , ")));
    }

    // ---- audit transport ----------------------------------------------------------------------------------------------

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        internal List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            lock (Urls) Urls.Add(request.RequestUri!.AbsoluteUri);
            HttpResponseMessage response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Reply(byte[] bytes, string type, HttpStatusCode status = HttpStatusCode.OK)
    {
        HttpResponseMessage response = new(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }

    private static HttpResponseMessage Json(string text = "[]") => Reply(Encoding.UTF8.GetBytes(text), "application/json");

    private static readonly StabilityChanPolicy Policy = new("lainchan", ["lainchan.org", "cdn.lainchan.org"], [new("lainchan.org", "cdn.lainchan.org")]);
    private const string Catalog = "https://lainchan.org/sec/catalog.json";
    private const string Thread = "https://lainchan.org/sec/res/1.json";
    private const string Media = "https://lainchan.org/sec/src/1.png";

    private static (StabilityAuditTransport Transport, Scripted Network, HttpClient Client) Build(Func<HttpRequestMessage, HttpResponseMessage> respond, StabilityChanPolicy? policy = null)
    {
        Scripted network = new(respond);
        StabilityAuditTransport transport = new(policy ?? Policy, new HttpClient(network, disposeHandler: false));
        return (transport, network, transport.Client());
    }

    private static async Task<string> Refusal(Task task) => (await Assert.ThrowsAsync<LiveHarnessLimitException>(() => task)).Code;

    [Fact]
    public async Task OneArmedGetPassesAndAnyRepeatOrOtherUrlIsRefusedBeforeTheSocket()
    {
        var (transport, network, client) = Build(_ => Json());
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        Assert.Equal("unplanned-url", await Refusal(client.GetAsync("https://lainchan.org/sec/catalog.json?x=1")));
        using HttpResponseMessage first = await client.GetAsync(Catalog);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-step-armed", await Refusal(client.GetAsync(Catalog)));
        Assert.Equal(1, network.Calls);
        Assert.Equal(1, transport.ActualNetworkSends);
        Assert.Equal(3, transport.Rows.Count);
        Assert.Single(transport.Rows, row => row.Sent);
        Assert.Null(transport.StopMode);
    }

    [Fact]
    public async Task NothingPassesWithoutAnArmedStepOrOnAHostTheChanDoesNotDeclare()
    {
        var (transport, network, client) = Build(_ => Json());
        Assert.Equal("no-step-armed", await Refusal(client.GetAsync(Catalog)));
        Assert.Throws<LiveHarnessLimitException>(() => transport.Arm(StabilityStep.Catalog, "sec", "https://kohlchan.net/sec/catalog.json"));
        Assert.Throws<LiveHarnessLimitException>(() => transport.Arm(StabilityStep.Catalog, "sec", "https://user@lainchan.org/sec/catalog.json"));
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        Assert.Equal("host-not-allowed", await Refusal(client.GetAsync("https://kohlchan.net/sec/catalog.json")));
        Assert.Equal(0, network.Calls);
        Assert.Equal(0, transport.ActualNetworkSends);
    }

    [Fact]
    public async Task HeadAndRangeRequestsAreRefused()
    {
        var (transport, network, client) = Build(_ => Json());
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        Assert.Equal("method-or-range", await Refusal(client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Catalog))));
        HttpRequestMessage ranged = new(HttpMethod.Get, Catalog);
        ranged.Headers.Range = new(0, 10);
        Assert.Equal("method-or-range", await Refusal(client.SendAsync(ranged)));
        Assert.Equal(0, network.Calls);
        using HttpResponseMessage allowed = await client.GetAsync(Catalog);
        Assert.Equal(1, network.Calls);
    }

    [Fact]
    public async Task APlannedRedirectHopIsAllowedOnceAndNothingElse()
    {
        var (transport, network, client) = Build(request => request.RequestUri!.Host == "lainchan.org"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://cdn.lainchan.org/sec/src/1.png") }, Content = new ByteArrayContent([]) }
            : Reply([137, 80, 78, 71], "image/png"));
        transport.Arm(StabilityStep.Original, "sec", Media);
        using HttpResponseMessage redirect = await client.GetAsync(Media);
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Null(transport.StopMode);
        Assert.Equal("extra-media", await Refusal(client.GetAsync("https://cdn.lainchan.org/other.png")));
        using HttpResponseMessage hop = await client.GetAsync("https://cdn.lainchan.org/sec/src/1.png");
        Assert.Equal(HttpStatusCode.OK, hop.StatusCode);
        Assert.Equal(2, network.Calls);
        Assert.Equal("no-step-armed", await Refusal(client.GetAsync("https://cdn.lainchan.org/sec/src/1.png")));
        Assert.NotNull(transport.OriginalDigests("sec"));
        Assert.Equal(["redirect-planned", "http-200"], transport.Rows.Where(row => row.Sent).Select(row => row.Outcome));
    }

    [Theory]
    [InlineData("https://www.lainchan.org/sec/catalog.json")]
    [InlineData("https://cdn.lainchan.org/elsewhere/")]
    public async Task AnUnplannedRedirectStopsTheChanAndIsRecorded(string location)
    {
        // The second case is planned host-wise only for the original step of lainchan.org; for the catalog from cdn it is not.
        var (transport, network, client) = Build(request => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location) }, Content = new ByteArrayContent([])
        }, location.Contains("cdn.") ? new("lainchan", ["lainchan.org", "cdn.lainchan.org"], []) : Policy);
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        using HttpResponseMessage response = await client.GetAsync(Catalog);
        Assert.Equal("unplanned-redirect", transport.StopMode);
        StabilityFailure failure = Assert.Single(transport.Failures);
        Assert.Equal(("sec", "catalog", "unplanned-redirect"), (failure.Board, failure.Step, failure.Mode));
        Assert.Equal("chan-stopped:unplanned-redirect", await Refusal(client.GetAsync(location)));
        Assert.Throws<LiveHarnessLimitException>(() => transport.Arm(StabilityStep.Thread, "sec", Thread));
        Assert.Equal(1, network.Calls);
    }

    [Theory]
    [InlineData(200, "text/html", "<html><title>Just a moment...</title><div id=\"cf-chl-widget\"></div></html>", "challenge-page")]
    [InlineData(200, "text/html", "<!doctype html><title>DDoS-Guard</title>", "challenge-page")]
    [InlineData(200, "text/html", "<html><head><TITLE>Attention Required! | Cloudflare</TITLE></head></html>", "challenge-page")]
    [InlineData(200, "text/html", "<html><script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1\"></script></html>", "challenge-page")]
    [InlineData(200, "text/html", "<html><body><div id=\"powblock\">Solve the proof-of-work to continue</div></body></html>", "challenge-page")]
    [InlineData(429, "text/plain", "slow down", "http-429")]
    [InlineData(503, "text/html", "<html>busy</html>", "http-503")]
    [InlineData(403, "text/html", "no", "http-403")]
    [InlineData(404, "application/json", "{}", "http-404")]
    [InlineData(200, "application/json", "", "empty-body")]
    public async Task TheFirstUnexpectedResponseStopsTheChanWithItsFailureMode(int status, string type, string body, string mode)
    {
        var (transport, network, client) = Build(_ =>
        {
            HttpResponseMessage response = Reply(Encoding.UTF8.GetBytes(body), type, (HttpStatusCode)status);
            if (status == 429) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return response;
        });
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        using HttpResponseMessage response = await client.GetAsync(Catalog);
        Assert.Equal(mode, transport.StopMode);
        StabilityFailure failure = Assert.Single(transport.Failures);
        Assert.Equal(status, failure.Status);
        if (status == 429) Assert.Contains("retry-after", failure.Detail);
        Assert.Equal("chan-stopped:" + mode, await Refusal(client.GetAsync(Catalog)));
        Assert.Equal(1, network.Calls);
    }

    [Fact]
    public async Task AnOrdinaryHtmlCatalogWithACaptchaFormAndANoscriptNoticePasses()
    {
        // 7chan-style: the catalog is HTML by contract and every ordinary page carries a captcha post form and a noscript notice.
        string page = "<!doctype html><html><head><title>/x/ - Catalog</title></head><body><noscript>Please enable JavaScript to use all features. "
            + "Are you a robot? No, you are not required to be.</noscript><form action=\"/post.php\"><label>Captcha</label><img src=\"/captcha.php\">"
            + "<input name=\"captcha_response\"></form><div id=\"catalog\">" + new string('x', 83 * 1024) + "</div></body></html>";
        var (transport, network, client) = Build(_ => Reply(Encoding.UTF8.GetBytes(page), "text/html"));
        const string HtmlCatalog = "https://lainchan.org/x/catalog.html";
        transport.Arm(StabilityStep.Catalog, "x", HtmlCatalog);
        using HttpResponseMessage response = await client.GetAsync(HtmlCatalog);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(transport.StopMode);
        Assert.Empty(transport.Failures);
        Assert.Equal(page.Length, (await response.Content.ReadAsStringAsync()).Length);
        Assert.Equal(1, network.Calls);
    }

    [Fact]
    public async Task MetadataBodiesAreSavedByHashOutsideGitIncludingAFailingOneButNeverTheOriginal()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "stability-bodies-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            byte[] challenge = Encoding.UTF8.GetBytes("<html><title>Just a moment...</title></html>");
            Scripted network = new(request => request.RequestUri!.AbsolutePath.EndsWith(".png") ? Reply([137, 80, 78, 71, 1], "image/png")
                : request.RequestUri.AbsolutePath.EndsWith("catalog.json") ? Json("[1]") : Reply(challenge, "text/html"));
            using StabilityAuditTransport transport = new(Policy, new HttpClient(network, disposeHandler: false)) { BodyDirectory = directory };
            using HttpClient client = transport.Client();
            transport.Arm(StabilityStep.Catalog, "sec", Catalog);
            using (await client.GetAsync(Catalog)) { }
            transport.Arm(StabilityStep.Thread, "sec", Thread);
            using (await client.GetAsync(Thread)) { }
            Assert.Equal("challenge-page", transport.StopMode);
            string[] files = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray()!;
            Assert.Equal(2, files.Length);
            foreach (StabilityRequestRecord row in transport.Rows.Where(row => row.Sent))
                Assert.Contains(row.Sha256 + ".body", files);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(challenge)).ToLowerInvariant() + ".body", files.Single(name => name != transport.Rows[0].Sha256 + ".body"));
            Assert.Equal("[1]", Encoding.UTF8.GetString(transport.Body("sec", StabilityStep.Catalog)!));

            string other = directory + "-original";
            using StabilityAuditTransport media = new(Policy, new HttpClient(new Scripted(_ => Reply([137, 80, 78, 71, 1], "image/png")), disposeHandler: false)) { BodyDirectory = other };
            media.Arm(StabilityStep.Original, "sec", Media);
            using (await media.Client().GetAsync(Media)) { }
            Assert.False(Directory.Exists(other), "media bytes are never written");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public void LeftoverRequestsAfterAWatchWasRemovedAreFound()
    {
        StabilityRequestRecord Row(int sequence, string step, string url) => new(sequence, "b", step, url, null, 0, null, null, "refused", false, DateTimeOffset.UtcNow);
        HashSet<string> prior = ["https://x.example/a/res/1.json"];
        StabilityRequestRecord[] rows =
        [
            Row(1, "thread", "https://x.example/a/res/1.json"), Row(2, "none", "https://x.example/other"), Row(3, "plan", "https://x.example/p"),
            Row(4, "catalog", "https://x.example/b/catalog.json"), Row(5, "thread", "https://x.example/a/res/1.json")
        ];
        Assert.Equal([2, 5], StabilityRunner.FindLeftovers(rows, 1, prior).Select(row => row.Sequence));
        Assert.Empty(StabilityRunner.FindLeftovers(rows, 5, prior));
    }

    [Fact]
    public void ThreadFlagsAreReadFromCatalogAndThreadBodiesAndAreUnknownForHtml()
    {
        static JsonObject? Flags(string text, string id) => StabilityRules.ThreadFlags(Encoding.UTF8.GetBytes(text), id);
        Assert.True((bool)Flags("""[{"page":1,"threads":[{"no":7,"sticky":1,"locked":0},{"no":8,"sticky":0}]}]""", "7")!["sticky"]!);
        Assert.False((bool)Flags("""[{"page":1,"threads":[{"no":7,"sticky":1},{"no":8,"sticky":0}]}]""", "8")!["sticky"]!);
        Assert.True((bool)Flags("""[{"threadId":55,"pinned":true,"locked":false}]""", "55")!["pinned"]!);
        Assert.True((bool)Flags("""{"threads":[{"posts":[{"num":"9","sticky":1,"closed":0}]}]}""", "9")!["sticky"]!);
        Assert.Empty(Flags("""[{"no":3}]""", "3")!);
        Assert.Null(Flags("""[{"no":3}]""", "4"));
        Assert.Null(Flags("<html><body>catalog</body></html>", "3"));
        Assert.Null(StabilityRules.ThreadFlags(null, "3"));
    }

    private static SourceThreadReference Ref(string id, int? files) => new(new("src", "b", id), new Uri("https://x.example/b/res/" + id + ".html"), AttachmentCount: files);

    [Fact]
    public void PinnedThreadsAreSkippedWhenChoosingTheThreadAndTheSkipIsRecorded()
    {
        byte[] catalog = Encoding.UTF8.GetBytes("""[{"page":1,"threads":[{"no":1,"sticky":1},{"no":2,"sticky":0},{"no":3,"sticky":1},{"no":4,"sticky":0},{"no":5}]}]""");
        SourceThreadReference[] threads = [Ref("1", 2), Ref("2", 0), Ref("3", 1), Ref("4", 3), Ref("5", 1)];
        StabilityRunner.ThreadChoice choice = StabilityRunner.ChooseThread(threads, catalog);
        Assert.Equal("4", choice.Chosen!.Id.ThreadId);
        Assert.Equal(2, choice.PinnedSkipped);
        Assert.Equal("first catalog entry with at least one attachment; skipped 2 pinned threads", choice.Note);
        // One skipped thread is singular; none skipped keeps the plain note.
        Assert.Equal("first catalog entry with at least one attachment; skipped 1 pinned thread", StabilityRunner.ChooseThread(threads[..2].Append(Ref("4", 3)).ToArray(), catalog).Note);
        StabilityRunner.ThreadChoice plain = StabilityRunner.ChooseThread([Ref("2", 0), Ref("4", 3), Ref("5", 1)], catalog);
        Assert.Equal("4", plain.Chosen!.Id.ThreadId);
        Assert.Equal(("first catalog entry with at least one attachment", 0), (plain.Note, plain.PinnedSkipped));
        // A pinned thread without an attachment is not a candidate, so it is not counted as skipped.
        Assert.Equal(0, StabilityRunner.ChooseThread([Ref("1", 0), Ref("2", 1)], catalog).PinnedSkipped);
    }

    [Fact]
    public void ThreadChoiceKeepsTheCurrentRuleWhenFlagsAreUnknownOrEverythingIsPinned()
    {
        SourceThreadReference[] threads = [Ref("1", 0), Ref("2", 4), Ref("3", 1)];
        foreach (byte[]? body in new byte[]?[] { null, Encoding.UTF8.GetBytes("<html><a href=\"/b/res/2.html\">sticky</a></html>"), Encoding.UTF8.GetBytes("""[{"no":2}]""") })
        {
            StabilityRunner.ThreadChoice choice = StabilityRunner.ChooseThread(threads, body);
            Assert.Equal("2", choice.Chosen!.Id.ThreadId);
            Assert.Equal(("first catalog entry with at least one attachment", 0), (choice.Note, choice.PinnedSkipped));
        }
        byte[] allPinned = Encoding.UTF8.GetBytes("""[{"no":2,"pinned":true},{"no":3,"sticky":1}]""");
        StabilityRunner.ThreadChoice fallback = StabilityRunner.ChooseThread(threads, allPinned);
        Assert.Equal("2", fallback.Chosen!.Id.ThreadId);
        Assert.Contains("every candidate is pinned", fallback.Note);
        Assert.Equal(0, fallback.PinnedSkipped);
        // Catalogs without attachment counts use the first entry, still skipping a pinned one.
        StabilityRunner.ThreadChoice noCounts = StabilityRunner.ChooseThread([Ref("1", null), Ref("2", null)], Encoding.UTF8.GetBytes("""[{"no":1,"sticky":1},{"no":2}]"""));
        Assert.Equal("2", noCounts.Chosen!.Id.ThreadId);
        Assert.Equal("the catalog reports no attachment counts; first catalog entry used; skipped 1 pinned thread", noCounts.Note);
        Assert.Null(StabilityRunner.ChooseThread([], null).Chosen);
        Assert.Null(StabilityRunner.ChooseThread([Ref("1", 0)], null).Chosen);
    }

    [Fact]
    public async Task TheReplayHandlerServesOnlySavedMetadataByUrlAndFailsOnEverythingElse()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "stability-replay-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "bodies"));
            byte[] body = Encoding.UTF8.GetBytes("""[{"no":1}]""");
            string hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(directory, "bodies", hash + ".body"), body);
            File.WriteAllText(Path.Combine(directory, "audit-lainchan.json"), JsonSerializer.Serialize(new
            {
                rows = new object[]
                {
                    new { sequence = 1, step = "catalog", url = Catalog, status = 200, bytes = body.Length, sha256 = hash, contentType = "application/json", sent = true },
                    new { sequence = 2, step = "thread", url = Thread, status = 200, bytes = 5, sha256 = new string('a', 64), contentType = "application/json", sent = true },
                    new { sequence = 3, step = "original", url = Media, status = 200, bytes = 5, sha256 = hash, contentType = "image/png", sent = true },
                    new { sequence = 4, step = "none", url = "https://lainchan.org/zzz", status = (int?)null, bytes = 0, sha256 = (string?)null, contentType = (string?)null, sent = false }
                }
            }));
            StabilityReplayHandler handler = StabilityReplayHandler.Load(directory, "lainchan");
            Assert.Equal(1, handler.Saved);
            Assert.Single(handler.Missing);
            using HttpClient client = handler.Client();
            Assert.Equal("""[{"no":1}]""", await client.GetStringAsync(Catalog));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStringAsync(Thread));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStringAsync(Media));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStringAsync("https://kohlchan.net/"));
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task AWrongContentTypeIsAFailureForTheOriginalAndForRequestedJson()
    {
        var (original, _, originalClient) = Build(_ => Reply(Encoding.UTF8.GetBytes("<html>nope</html>"), "text/html"));
        original.Arm(StabilityStep.Original, "sec", Media);
        using (await originalClient.GetAsync(Media)) { }
        Assert.Equal("wrong-content-type", original.StopMode);

        var (metadata, _, metadataClient) = Build(_ => Reply(Encoding.UTF8.GetBytes("plain words"), "text/plain"));
        metadata.Arm(StabilityStep.Catalog, "sec", Catalog);
        HttpRequestMessage request = new(HttpMethod.Get, Catalog);
        request.Headers.Accept.ParseAdd("application/json");
        using (await metadataClient.SendAsync(request)) { }
        Assert.Equal("wrong-content-type", metadata.StopMode);
    }

    [Fact]
    public async Task TheOriginalCapIsTwoMebibytesExactlyAndMetadataHasEightMebibytes()
    {
        byte[] exact = new byte[StabilityAuditTransport.MaximumOriginalBytes];
        exact[0] = 1;
        var (ok, _, okClient) = Build(_ => Reply(exact, "image/png"));
        ok.Arm(StabilityStep.Original, "sec", Media);
        using (HttpResponseMessage response = await okClient.GetAsync(Media)) Assert.Equal(exact.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
        StabilityDigests digests = ok.OriginalDigests("sec")!;
        Assert.Equal(exact.Length, digests.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(exact)).ToLowerInvariant(), digests.Sha256);
        Assert.Equal(Convert.ToHexString(MD5.HashData(exact)).ToLowerInvariant(), digests.Md5Hex);
        Assert.Null(ok.StopMode);

        var (big, _, bigClient) = Build(_ => Reply(new byte[exact.Length + 1], "image/png"));
        big.Arm(StabilityStep.Original, "sec", Media);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => bigClient.GetAsync(Media));
        Assert.StartsWith("size-cap:", big.StopMode);
        Assert.Null(big.OriginalDigests("sec"));

        var (meta, _, metaClient) = Build(_ => Reply(new byte[StabilityAuditTransport.MaximumMetadataBytes + 1], "application/json"));
        meta.Arm(StabilityStep.Catalog, "sec", Catalog);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => metaClient.GetAsync(Catalog));
        Assert.StartsWith("size-cap:", meta.StopMode);
    }

    [Fact]
    public async Task APlanRecordsTheUrlsWithoutAnyNetworkClient()
    {
        using StabilityAuditTransport transport = new(Policy, network: null);
        using HttpClient client = transport.Client();
        IReadOnlyList<string> urls = await transport.PlanAsync(async () => { using (await client.GetAsync(Catalog + "?secret=1")) { } });
        Assert.Equal([Catalog], urls);
        Assert.Equal(0, transport.ActualNetworkSends);
        Assert.All(transport.Rows, row => Assert.False(row.Sent));
        // After the plan the transport is as closed as before.
        Assert.Equal("no-step-armed", await Refusal(client.GetAsync(Catalog)));
    }

    [Fact]
    public async Task AnArmedStepWithoutANetworkClientCannotSend()
    {
        using StabilityAuditTransport transport = new(Policy, network: null);
        using HttpClient client = transport.Client();
        transport.Arm(StabilityStep.Catalog, "sec", Catalog);
        Assert.Equal("network-unavailable", await Refusal(client.GetAsync(Catalog)));
        Assert.Equal(0, transport.ActualNetworkSends);
        Assert.All(transport.Rows, row => Assert.False(row.Sent));
    }

    [Fact]
    public async Task TheOfflinePhaseRejectsEverythingEvenForAnArmedUrl()
    {
        var (_, network, _) = Build(_ => Json());
        using StabilityAuditTransport transport = new(Policy, new HttpClient(network, disposeHandler: false)) { RejectAll = true };
        using HttpClient client = transport.Client();
        Assert.Equal("offline-request", await Refusal(client.GetAsync(Catalog)));
        Assert.Equal("offline-request", await Refusal(client.GetAsync("https://kohlchan.net/")));
        Assert.Equal(0, network.Calls);
        Assert.Equal(0, transport.ActualNetworkSends);
        Assert.Equal(2, transport.Rows.Count);
        Assert.All(transport.Rows, row => Assert.False(row.Sent));
    }

    [Fact]
    public async Task MediaRequestsWaitAfterTheThreadUntilTheOriginalIsArmedThenOnlyThatOneGoesOut()
    {
        var (transport, network, client) = Build(request => request.RequestUri!.AbsolutePath.EndsWith(".png") ? Reply([137, 80, 78, 71], "image/png") : Json());
        transport.Arm(StabilityStep.Thread, "sec", Thread);
        using (await client.GetAsync(Thread)) { }
        Task<HttpResponseMessage> parked = client.GetAsync(Media);
        Task<HttpResponseMessage> other = client.GetAsync("https://lainchan.org/sec/src/2.png");
        await Task.Delay(250);
        Assert.False(parked.IsCompleted);
        Assert.Equal(1, network.Calls);
        transport.Arm(StabilityStep.Original, "sec", Media);
        using HttpResponseMessage response = await parked;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("extra-media", await Refusal(other));
        Assert.Equal(2, network.Calls);
        Assert.Equal(["https://lainchan.org/sec/res/1.json", Media], network.Urls);
    }

    [Fact]
    public async Task ParkedMediaIsRefusedWhenNoOriginalIsEligible()
    {
        var (transport, network, client) = Build(_ => Json());
        transport.Arm(StabilityStep.Thread, "sec", Thread);
        using (await client.GetAsync(Thread)) { }
        Task<HttpResponseMessage> parked = client.GetAsync(Media);
        await Task.Delay(100);
        transport.CloseSteps();
        Assert.Equal("no-eligible-original", await Refusal(parked));
        Assert.Equal(1, network.Calls);
    }

    // ---- pure rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BoardListsAreReadFromJsonShapesAndHtmlMenus()
    {
        Assert.Equal(["a", "b"], StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("""["a","b","a"]""")));
        Assert.Equal(["x", "y"], StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("""{"boards":[{"boardUri":"x"},{"uri":"y"}]}""")));
        Assert.Equal(["x"], StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("""{"data":{"boards":[{"board":"x"}]}}""")));
        Assert.Equal(["tech", "art"], StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("<a href=\"/tech/\">t</a><a href='/boards/'>x</a><a href=\"/art/\">a</a><a href=\"/static/\">s</a>")));
        Assert.Empty(StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("plain")));
        Assert.ThrowsAny<JsonException>(() => StabilityRules.ParseBoardList(Encoding.UTF8.GetBytes("[broken")));
    }

    [Theory]
    [InlineData("24.50", "KB", 25088L)]
    [InlineData("1", "MB", 1048576L)]
    [InlineData("12", "B", 12L)]
    [InlineData("3", "furlong", null)]
    [InlineData("abc", "KB", null)]
    public void ProviderSizeHintsBecomeAnUpperEstimateOrNothing(string raw, string unit, long? expected)
    {
        MeowChan.Storage.MediaProviderSizeHint hint = new(raw, unit);
        Assert.Equal(expected, StabilityRules.ApproximateBytes(hint));
        Assert.Null(StabilityRules.ApproximateBytes(null));
    }

    [Fact]
    public void ExtensionsFollowTheUrlThenTheOriginalName()
    {
        Assert.Equal(".png", StabilityRules.ExtensionOf(new(0, "m", Url: "https://x.example/a/b.png?x=1")));
        Assert.Equal(".webp", StabilityRules.ExtensionOf(new(0, "m", Url: "https://x.example/a/b", OriginalFileName: "c.webp")));
        Assert.True(StabilityRules.ImageExtensions.Contains(".PNG"));
        Assert.False(StabilityRules.ImageExtensions.Contains(".mp4"));
    }

    [Fact]
    public void EvaluationComparesEverythingTheProviderDeclared()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("synthetic original");
        StabilityDigests digests = new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        StabilityRules.OriginalChoice Choice(long? size, string? md5) => new(new MeowChan.Archive.CapturedPost("1", 1, default),
            new MeowChan.Archive.CapturedAttachment(0, "m", Url: "https://x.example/m.png", ExpectedSize: size, ExpectedMd5: md5), ".png");

        StabilityOriginalRun good = StabilityRules.Evaluate(Choice(bytes.Length, Convert.ToBase64String(MD5.HashData(bytes))), digests);
        Assert.Equal("size+hash", good.IntegrityBasis);
        Assert.True(good.IntegrityOk);
        Assert.True(good.HashMatched);

        StabilityOriginalRun wrongSize = StabilityRules.Evaluate(Choice(bytes.Length + 1, null), digests);
        Assert.False(wrongSize.SizeMatched);
        Assert.False(wrongSize.IntegrityOk);

        StabilityOriginalRun wrongHash = StabilityRules.Evaluate(Choice(bytes.Length, Convert.ToBase64String(new byte[16])), digests);
        Assert.False(wrongHash.HashMatched);
        Assert.False(wrongHash.IntegrityOk);

        StabilityOriginalRun nothing = StabilityRules.Evaluate(Choice(null, null), digests);
        Assert.Equal("none", nothing.IntegrityBasis);
        Assert.False(nothing.IntegrityOk, "a source that declares nothing cannot count as verified");
    }

    // ---- evidence -----------------------------------------------------------------------------------------------------

    private static JsonElement Parsed(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void ShapeValidationAcceptsTheContractExampleAndNamesWhatIsMissing()
    {
        const string Example = """
            {"kind":"source-stability-run","contract":"docs/plugins/source-stability.md@0123456","pluginId":"meowchan.source.sushichan","sourceId":"sushichan",
             "executionRevision":"r","assemblySha256":"a","descriptionSha256":"d","package":"p sha256:x","approvedByUser":"2026-11-12, chat",
             "windowUtc":["2026-11-12T10:00:00Z","2026-11-12T10:03:00Z"],
             "boards":[{"board":"lounge","thread":"12345","requests":[{"url":"https://x.example/","status":200,"bytes":5}],
               "original":{"expectedBytes":9,"receivedBytes":9,"providerHash":"md5:x","hashMatched":true,"sha256":"s"},
               "archive":"pass","reader":"pass","offlineAfterRestart":"pass"}],
             "integrityExceptionUsed":false,"failuresObserved":[],"receipts":[{"path":"a.json","sha256":"x"}],"outcome":"pass"}
            """;
        Assert.Empty(StabilityEvidence.ValidateShape(Parsed(Example)));
        Assert.NotEmpty(StabilityEvidence.ValidateShape(Parsed(Example.Replace("\"receipts\"", "\"other\""))));
        Assert.NotEmpty(StabilityEvidence.ValidateShape(Parsed(Example.Replace("\"outcome\":\"pass\"", "\"outcome\":\"maybe\""))));
    }

    [Theory]
    [InlineData("docs/plugins/source-stability.md@0123456", true)]
    [InlineData("docs/plugins/source-stability.md@0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("docs/plugins/source-stability.md", false)]
    [InlineData("docs/plugins/source-stability.md@HEAD", false)]
    [InlineData("other.md@0123456", false)]
    public void TheContractReferenceIsPathAtCommit(string value, bool valid) => Assert.Equal(valid, StabilityEvidence.IsContractReference(value));

    [Fact]
    public void EvidenceLandsNextToThePluginSourcesByBundleName()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "stability-path-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "plugins", "Examples", "Lainchan"));
            Directory.CreateDirectory(Path.Combine(root, "plugins", "Official", "Dvach"));
            DateTimeOffset day = new(2026, 11, 11, 23, 59, 0, TimeSpan.Zero);
            Assert.Equal(Path.Combine(root, "plugins", "Examples", "Lainchan", "Evidence", "stability-20261111.json"),
                StabilityEvidence.EvidencePath(root, "MeowChan.Plugin.Lainchan.plugin.json", "meowchan.source.lainchan", day));
            Assert.Equal(Path.Combine(root, "plugins", "Official", "Dvach", "Evidence", "stability-20261111.json"),
                StabilityEvidence.EvidencePath(root, "MeowChan.Plugin.Dvach.plugin.json", "meowchan.source.2ch", day));
            Assert.Throws<InvalidOperationException>(() => StabilityEvidence.EvidencePath(root, "MeowChan.Plugin.Missing.plugin.json", "x", day));
            Assert.Throws<InvalidOperationException>(() => StabilityEvidence.EvidencePath(root, "../../evil.plugin.json", "x", day));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
