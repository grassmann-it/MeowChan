using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Lainchan;
using MeowChan.Plugins.Examples.AnonImageboards;
using MeowChan.Plugins.Examples.BbwChan;
using MeowChan.Plugins.Examples.CapybaraChan;
using MeowChan.Plugins.Examples.EightchanMoe;
using MeowChan.Plugins.Examples.Fchan;
using MeowChan.Plugins.Examples.GuroCx;
using MeowChan.Plugins.Examples.Kohlchan;
using MeowChan.Plugins.Examples.SevenChan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class ExamplePluginTests
{
    [Fact]
    public void DescriptionsAreInertDistinctSdkOnlySources()
    {
        PluginDescription[] descriptions = [new LainchanPlugin().Describe(), new KohlchanPlugin().Describe()];
        Assert.Empty(SdkValidation.ValidateDescriptions(descriptions));
        Dictionary<string, string> expectedNames = new(StringComparer.Ordinal)
        { ["lainchan"] = "Lainchan", ["kohlchan"] = "Kohlchan" };
        Assert.All(descriptions, description =>
        {
            Assert.Empty(description.Settings.Fields);
            Assert.Single(description.Sources);
            SourceDescription source = description.Sources[0];
            Assert.Contains(source.SourceId, expectedNames.Keys);
            Assert.Equal(expectedNames[source.SourceId], description.Name);
            Assert.Equal(expectedNames[source.SourceId], source.Name);
            Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
            Assert.False(source.Capabilities.HasFlag(SourceCapabilities.Previews));
            Assert.Equal("/{board}/res/{thread}.html", Assert.Single(source.Routes, route => route.Kind == SourceResourceKind.Thread).PathTemplate);
        });
        foreach (Type type in new[] { typeof(LainchanPlugin), typeof(KohlchanPlugin) })
        {
            string[] references = type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
            Assert.Contains("MeowChan.PluginSdk", references);
            Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
            Assert.Single(type.Assembly.GetExportedTypes(), item => typeof(IPlugin).IsAssignableFrom(item));
        }
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ThreadsTransportOrderedFilesAndIndependentPostIdentity(string source)
    {
        Harness h = new Harness(source, Fixture(source, "thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState); // locked and cyclic are not terminal.
        Assert.Equal(new[] { "123", "124" }, result.Snapshot.Posts.Select(post => post.PostId));
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal("<p>Synthetic text</p>", op.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), op.PostedAt);
        Assert.Equal(new[] { 0, 1, 2 }, op.Attachments!.Select(file => file.Position));
        Assert.Equal(new[] { "123-0", "123-1", "123-2" }, op.Attachments!.Select(file => file.MediaId));
        Assert.Equal(3, op.Attachments!.Select(file => file.MediaId).Distinct().Count());
        Assert.All(op.Attachments!, file => Assert.Equal(h.Host, file.Url!.Host));
        Assert.Equal(h.Validators, result.Snapshot.Validators);
        Assert.Equal("https://" + h.Host + "/demo/res/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(3, Assert.IsType<ThreadSnapshot>(copy).Snapshot.Posts[0].Attachments!.Count);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task CatalogUsesExplicitBoardAndCanonicalThreadsWithoutGlobalDiscovery(string source)
    {
        Harness h = new Harness(source, Fixture(source, "catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Null(result.Snapshot.NextPage);
        Assert.Equal(new[] { "123", "456" }, result.Snapshot.Threads.Select(thread => thread.Id.ThreadId));
        Assert.Equal("https://" + h.Host + "/demo/res/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
        Assert.Equal("https://" + h.Host + "/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task MissingAndExplicitEmptyFieldsStayDistinctInPartialUpdates(string source)
    {
        Harness h = new Harness(source, Fixture(source, "partial"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        SourcePost reply = Assert.Single(result.Snapshot.Posts, post => post.PostId == "124");
        Assert.Equal(SourceFieldPresence.Text, reply.Fields);
        Assert.Equal("", reply.Text);
        Assert.Null(reply.Subject);
        Assert.Null(reply.Attachments);
        Assert.False(reply.IsDeleted);
        Assert.Null(result.Snapshot.Title);
        Assert.All(result.Snapshot.Posts, post => Assert.Equal(0, post.Ordinal)); // DecimalPostId, never response index.
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task UnchangedReferencesAcceptedValidatorAndNeverProducesEmptySnapshot(string source)
    {
        Harness h = new Harness(source, "not JSON", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators accepted = new SourceValidators(EntityTag: "\"accepted\"");
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = accepted };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = accepted };
        ThreadUnchanged unchanged = Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context));
        Assert.Equal(accepted, unchanged.Validators); // The response's unaccepted validator does not replace the baseline.
        Assert.Empty(SdkValidation.ValidateThreadResult(thread, unchanged, h.Source));
        Assert.Equal(accepted, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(accepted, h.Http.Requests[^1].Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new SourceValidators(Revision: "local") }, h.Context));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task OnlyExactThreadEndpoint404MeansNotFoundWithoutInventedPosts(string source)
    {
        Harness h = new Harness(source, "<html>missing</html>", 404);
        ThreadNotFound result = Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://" + h.Host + "/login.html");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
    }

    [Theory]
    [InlineData("lainchan", 206)] [InlineData("kohlchan", 206)]
    [InlineData("lainchan", 403)] [InlineData("kohlchan", 403)]
    [InlineData("lainchan", 410)] [InlineData("kohlchan", 410)]
    [InlineData("lainchan", 429)] [InlineData("kohlchan", 429)]
    [InlineData("lainchan", 503)] [InlineData("kohlchan", 503)]
    public async Task OtherStatusesNeverBecomeTerminalOrPartialSnapshots(string source, int status)
    {
        Harness h = new Harness(source, Fixture(source, "thread"), status);
        Assert.Equal(status, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.HttpStatusCode);
        Assert.Equal(status, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure.HttpStatusCode);
        Assert.Equal(2, h.Http.Requests.Count); // one request per operation; no private retry loop.
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task DeferredAndHostFailurePreserveTheirMeaning(string source)
    {
        Harness h = new Harness(source, "");
        DateTimeOffset due = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        h.Http.Result = new HostHttpDeferred(due, "provider_cooldown", 429);
        Assert.Equal(due, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(due, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        FailureDetail failure = new FailureDetail("timeout", "Bounded fixture timeout.");
        h.Http.Result = new HostHttpFailure(failure);
        Assert.Equal(failure, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure);
        Assert.Equal(failure, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure);
    }

    [Theory]
    [InlineData("lainchan", "{")]
    [InlineData("kohlchan", "{")]
    [InlineData("lainchan", "{}")]
    [InlineData("kohlchan", "{}")]
    [InlineData("lainchan", "{\"posts\":null}")]
    [InlineData("kohlchan", "{\"threadId\":123,\"boardUri\":\"demo\",\"posts\":null}")]
    public async Task MalformedResponseCannotCommitValidators(string source, string json)
    {
        Harness h = new Harness(source, json);
        ThreadFailure result = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal("invalid_payload", result.Failure.Code);
        Assert.DoesNotContain("entityTag", Encoding.UTF8.GetString(SdkJson.Serialize<ThreadReadResult>(result)));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task DuplicatePostsAndNegativeFileSizesRejectWholeSnapshot(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        JsonArray posts = root["posts"]!.AsArray();
        posts.Add(posts[0]!.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture(source, "thread"))!;
        if (source == "lainchan") root["posts"]![0]!["fsize"] = -1;
        else root["files"]![0]!["size"] = -1;
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task KohlchanArchivedIncludesFinalFilesAndIgnoresTrashAsDeletion()
    {
        JsonNode root = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
        root["archived"] = true;
        root["trash"] = true;
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness("kohlchan", root.ToJsonString()).ReadThread());
        Assert.Equal(SourceThreadState.Archived, result.Snapshot.SourceState);
        Assert.Equal(3, result.Snapshot.Posts[0].Attachments!.Count);
        Assert.False(result.Snapshot.Posts[0].IsDeleted);
    }

    [Fact]
    public async Task LainchanDoesNotInventArchiveMarkerOrCompactExtraPositions()
    {
        string json = """{"posts":[{"no":123,"archived":1,"omitted_posts":20,"extra_files":[{"tim":"1001-1","ext":".png"}]}]}""";
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness("lainchan", json).ReadThread());
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        SourceAttachment file = Assert.Single(result.Snapshot.Posts[0].Attachments!);
        Assert.Equal(1, file.Position);
        Assert.Equal("123-1", file.MediaId);
        Assert.EndsWith("/demo/src/1001-1.png", file.Url!.AbsoluteUri);
    }

    [Fact]
    public async Task KohlchanRejectsCrossThreadAndUntrustedMediaPaths()
    {
        foreach (string path in new[] { "https://foreign.test/file.png", "//foreign.test/file.png", "/.media/../../file.png", "/.media/%2e%2e/file.png", "/.media/nested/file.png", "/.media/file.png?secret=1" })
        {
            JsonNode root = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
            root["files"]![0]!["path"] = path;
            Assert.IsType<ThreadFailure>(await new Harness("kohlchan", root.ToJsonString()).ReadThread());
        }
        JsonNode other = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
        other["threadId"] = 999;
        Assert.IsType<ThreadFailure>(await new Harness("kohlchan", other.ToJsonString()).ReadThread());
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task EmptyAttachmentArrayIsExplicitButMissingArrayIsNotDeletion(string source)
    {
        string json = source == "lainchan" ? """{"posts":[{"no":123,"extra_files":[]}]}"""
            : """{"threadId":123,"boardUri":"demo","files":[],"posts":[]}""";
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(source, json).ReadThread()).Snapshot.Posts);
        Assert.True(post.Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Empty(post.Attachments!);
        Assert.False(post.IsDeleted);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task UnsupportedPaginationTraversalOversizeAndCancellationAreBounded(string source)
    {
        Harness h = new Harness(source, Fixture(source, "thread"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId(source, "../escape", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cancellation.Token));
        Assert.Single(h.Http.Requests);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task MediaIdentityDoesNotChangeWithFilenameOrDownloadPath(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        SourceAttachment before = Assert.IsType<ThreadSnapshot>(await new Harness(source, root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        if (source == "lainchan") { root["posts"]![0]!["tim"] = "2000-0"; root["posts"]![0]!["filename"] = "renamed"; }
        else { root["files"]![0]!["path"] = "/.media/replacement.png"; root["files"]![0]!["originalName"] = "renamed.png"; }
        SourceAttachment after = Assert.IsType<ThreadSnapshot>(await new Harness(source, root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(before.MediaId, after.MediaId);
        Assert.NotEqual(before.Url, after.Url);
        Assert.NotEqual(before.OriginalFileName, after.OriginalFileName);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExplicitNullIsPresentAndDoesNotFallBackToAnotherTextField(string source)
    {
        string json = source == "lainchan" ? """{"posts":[{"no":123,"com":null,"name":null,"sub":null,"time":null}]}"""
            : """{"threadId":123,"boardUri":"demo","markdown":null,"message":"not selected","name":null,"subject":null,"creation":null,"posts":[]}""";
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(source, json).ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text | SourceFieldPresence.Author | SourceFieldPresence.Subject | SourceFieldPresence.PostedAt, post.Fields);
        Assert.Null(post.Text);
        Assert.Null(post.Author);
        Assert.Null(post.Subject);
        Assert.Null(post.PostedAt);
        Assert.Equal(SourceTextFormat.Html, post.TextFormat);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task CatalogRejectsDuplicateIdentitiesAndMalformedShape(string source)
    {
        JsonArray root = JsonNode.Parse(Fixture(source, "catalog"))!.AsArray();
        if (source == "lainchan") root[1]!["threads"]![0]!["no"] = 123;
        else root[1]!["threadId"] = 123;
        Assert.IsType<CatalogFailure>(await new Harness(source, root.ToJsonString()).ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness(source, "[{}]").ReadCatalog());
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExplicitUnicodeBoardIsEscapedOnceAndRetainsItsIdentity(string source)
    {
        Harness h = new Harness(source, Fixture(source, "catalog"));
        CatalogReadRequest request = new CatalogReadRequest(new SourceBoardId(source, "Kühl"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.Plugin.ReadCatalogAsync(request, h.Context));
        Assert.Equal("Kühl", result.Snapshot.Board.BoardId);
        Assert.Equal("/K%C3%BChl/catalog.json", Assert.Single(h.Http.Requests).Url.AbsolutePath);
        Assert.Empty(SdkValidation.ValidateCatalogResult(request, result, h.Source));
        Assert.Equal("/K%C3%BChl/res/123.html", result.Snapshot.Threads[0].Url.AbsolutePath);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExcessiveAttachmentCountRejectsTheSnapshotWithoutTruncation(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        JsonArray files = source == "lainchan" ? root["posts"]![0]!["extra_files"]!.AsArray() : root["files"]!.AsArray();
        JsonNode file = files[0]!.DeepClone();
        files.Clear();
        for (int index = 0; index < 1025; index++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
    }

    [Fact]
    public void AllSevenExperimentalChanPluginsKeepDistinctSdkOnlyIdentities()
    {
        (string PluginId, string Name, string SourceId, Uri? Origin, string Maturity)[] expected =
        [
            ("meowchan.source.sevenchan", "7chan", "sevenchan", new Uri("https://7chan.org/"),
                "MeowChan · experimental; HTML contract and current availability are not a stability promise."),
            ("meowchan.source.bbwchan", "BBW-Chan", "bbwchan", new Uri("https://bbw-chan.link/"),
                "MeowChan · experimental; the .link host is the only configured host."),
            ("meowchan.source.anonimageboards", "Anon Imageboards", "anonimageboards", null,
                "MeowChan · experimental · public entrance unavailable"),
            ("meowchan.source.capybarachan", "CapybaraChan", "capybarachan", new Uri("https://capybarachan.org/"),
                "MeowChan · experimental; the board menu is not treated as an automatic webring."),
            ("meowchan.source.eightchanmoe", "8chan.moe", "eightchanmoe", new Uri("https://8chan.moe/"),
                "MeowChan · experimental · not live-verified; live JSON is currently challenge-gated."),
            ("meowchan.source.fchan", "Fchan", "fchan", new Uri("http://fchan.us/"),
                "MeowChan · experimental; HTTP-only source."),
            ("meowchan.source.gurocx", "Guro.cx", "gurocx", null,
                "MeowChan · experimental · technically blocked"),
        ];
        IPlugin[] plugins = [new SevenChanPlugin(), new BbwChanPlugin(), new AnonImageboardsPlugin(),
            new CapybaraChanPlugin(), new EightchanMoePlugin(), new FchanPlugin(), new GuroCxPlugin()];
        PluginDescription[] descriptions = plugins.Select(plugin => plugin.Describe()).ToArray();
        PluginDescription[] supportedDescriptions = descriptions.Where(description => description.Sources.Count != 0).ToArray();
        Assert.Empty(SdkValidation.ValidateDescriptions(supportedDescriptions));
        ValidationIssue[] blockedValidationIssues = SdkValidation.ValidateDescriptions(descriptions).ToArray();
        Assert.Equal(new[] { "plugins[2]", "plugins[6]" }, blockedValidationIssues.Select(issue => issue.Path));
        Assert.All(blockedValidationIssues, issue => Assert.Equal("missing_capability", issue.Code));
        Assert.Equal(7, descriptions.Select(description => description.PluginId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, descriptions.Select(description => description.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, descriptions.SelectMany(description => description.Sources).Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count());

        for (int index = 0; index < expected.Length; index++)
        {
            PluginDescription description = descriptions[index];
            Assert.Equal(expected[index].PluginId, description.PluginId);
            Assert.Equal(expected[index].Name, description.Name);
            Assert.Equal(SdkContract.Version, description.SdkVersion);
            Assert.Empty(description.Settings.Fields);
            Assert.Empty(description.Filters);
            Assert.Equal(expected[index].Maturity, Assert.Single(description.Information, entry => entry.Key == "maturity").Value);

            if (expected[index].Origin is null)
            {
                Assert.Empty(description.Sources);
                continue;
            }

            SourceDescription source = Assert.Single(description.Sources);
            Assert.Equal(expected[index].SourceId, source.SourceId);
            Assert.Equal(description.Name, source.Name);
            Assert.Equal(expected[index].Origin, source.BaseUri);
            Assert.Equal(SourceMaturity.Experimental, source.Maturity);
            Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
            Assert.False(source.Capabilities.HasFlag(SourceCapabilities.Previews));
            Assert.Equal("/{board}/", Assert.Single(source.Routes, route => route.Kind == SourceResourceKind.Board).PathTemplate);
            UrlRoute threadRoute = Assert.Single(source.Routes, route => route.Kind == SourceResourceKind.Thread);
            Assert.Equal("/{board}/res/{thread}.html", threadRoute.PathTemplate);
            Assert.Equal(RouteIdentifierKind.AsciiWord, threadRoute.BoardIdentifierKind);
            Assert.Equal(RouteIdentifierKind.Decimal, threadRoute.ThreadIdentifierKind);
            Assert.Equal(source.BaseUri.Authority, Assert.Single(source.HttpHosts).Host);
            Assert.True(source.HttpHosts[0].Purpose.HasFlag(HttpHostPurpose.Metadata));
            Assert.True(source.HttpHosts[0].Purpose.HasFlag(HttpHostPurpose.OriginalMedia));
            Assert.False(source.HttpHosts[0].Purpose.HasFlag(HttpHostPurpose.PreviewMedia));
        }

        string identityText = string.Join('\n', descriptions.SelectMany(description =>
            new[] { description.PluginId, description.Name, description.Description }
                .Concat(description.Information.Select(entry => entry.Value ?? ""))));
        Assert.False(identityText.Contains("official", StringComparison.OrdinalIgnoreCase));
        Assert.False(identityText.Contains("stable promise", StringComparison.OrdinalIgnoreCase));

        Type[] types = [typeof(SevenChanPlugin), typeof(BbwChanPlugin), typeof(AnonImageboardsPlugin),
            typeof(CapybaraChanPlugin), typeof(EightchanMoePlugin), typeof(FchanPlugin), typeof(GuroCxPlugin)];
        foreach (Type type in types)
        {
            string[] references = type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
            Assert.Contains("MeowChan.PluginSdk", references);
            Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
            Assert.Single(type.Assembly.GetExportedTypes(), exported => typeof(IPlugin).IsAssignableFrom(exported));
        }
    }

    [Theory]
    [InlineData("anonimageboards", "public_entrance_unavailable")]
    [InlineData("gurocx", "technical_block")]
    public async Task BlockedExperimentalSourcesFailExactlyAndNeverTouchNetworkOrCredentials(string source, string code)
    {
        Harness h = new(source, "unreachable fixture body");
        CatalogFailure catalog = Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        ThreadFailure thread = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal(code, catalog.Failure.Code);
        Assert.Equal(code, thread.Failure.Code);
        Assert.Null(catalog.Failure.HttpStatusCode);
        Assert.Null(thread.Failure.HttpStatusCode);
        Assert.Empty(h.Http.Requests);
        CatalogReadResult catalogCopy = SdkJson.Deserialize<CatalogReadResult>(SdkJson.Serialize<CatalogReadResult>(catalog));
        ThreadReadResult threadCopy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(thread));
        Assert.Equal(code, Assert.IsType<CatalogFailure>(catalogCopy).Failure.Code);
        Assert.Equal(code, Assert.IsType<ThreadFailure>(threadCopy).Failure.Code);
    }

    [Fact]
    public async Task SevenChanParsesBoundedCatalogAndThreadHtml()
    {
        Harness catalogHarness = new("sevenchan", PluginFixture("SevenChan", "catalog.html"), board: "x");
        CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await catalogHarness.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(catalogHarness.CatalogRequest, catalog, catalogHarness.Source));
        Assert.Equal(new[] { "100", "101" }, catalog.Snapshot.Threads.Select(thread => thread.Id.ThreadId));
        Assert.Equal("<escaped> subject", catalog.Snapshot.Threads[0].Title);
        Assert.Equal("A <em>bounded</em> teaser", catalog.Snapshot.Threads[0].OpeningText);
        Assert.Equal(12, catalog.Snapshot.Threads[0].ReplyCount);
        Assert.Equal(3, catalog.Snapshot.Threads[0].AttachmentCount);
        Assert.Equal("https://7chan.org/x/catalog.html", Assert.Single(catalogHarness.Http.Requests).Url.AbsoluteUri);

        Harness threadHarness = new("sevenchan", PluginFixture("SevenChan", "thread-single.html"), board: "x", thread: "100");
        ThreadSnapshot thread = Assert.IsType<ThreadSnapshot>(await threadHarness.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, thread, threadHarness.Source));
        Assert.Equal(new[] { "100", "101" }, thread.Snapshot.Posts.Select(post => post.PostId));
        Assert.Equal(new[] { 0L, 1L }, thread.Snapshot.Posts.Select(post => post.Ordinal));
        Assert.Equal("Opening <a href=\"#101\">&gt;&gt;101</a>", thread.Snapshot.Posts[0].Text);
        SourceAttachment attachment = Assert.Single(thread.Snapshot.Posts[0].Attachments!);
        Assert.Equal("100-0", attachment.MediaId);
        Assert.Equal("https://7chan.org/x/src/opening.jpg", attachment.Url!.AbsoluteUri);
        Assert.Equal("opening.jpg", attachment.OriginalFileName);
        Assert.Equal(".jpg", attachment.Extension);
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(thread));
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, copy, threadHarness.Source));
        Assert.Single(Assert.IsType<ThreadSnapshot>(copy).Snapshot.Posts[0].Attachments!);
    }

    [Theory]
    [InlineData("bbwchan", "bbw", "123", "BbwChan", "thread.json", new[] { "123", "124", "125" }, new[] { "123", "124", "125" }, SourceTextFormat.PlainText, 2)]
    [InlineData("capybarachan", "site", "101", "CapybaraChan", "media-thread.json", new[] { "101", "102" }, new[] { "101", "102" }, SourceTextFormat.Html, 1)]
    [InlineData("eightchanmoe", "board", "201", "EightchanMoe", "lynxchan-thread.json", new[] { "201" }, new[] { "201", "202" }, SourceTextFormat.Html, 1)]
    public async Task LynxChanExperimentalSourcesParseTheirOwnCatalogAndThreadFixtures(
        string source, string board, string threadId, string fixtureDirectory, string threadFixture,
        string[] catalogIds, string[] threadIds, SourceTextFormat openerFormat, int openerAttachmentCount)
    {
        Harness catalogHarness = new(source, PluginFixture(fixtureDirectory,
            source == "bbwchan" ? "board-catalog.json" : source == "capybarachan" ? "site-catalog.json" : "lynxchan-catalog.json"),
            board: board);
        CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await catalogHarness.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(catalogHarness.CatalogRequest, catalog, catalogHarness.Source));
        Assert.Equal(catalogIds, catalog.Snapshot.Threads.Select(item => item.Id.ThreadId));
        Assert.Equal("https://" + catalogHarness.Host + "/" + board + "/catalog.json",
            Assert.Single(catalogHarness.Http.Requests).Url.AbsoluteUri);
        Assert.Equal("application/json", Assert.Single(catalogHarness.Http.Requests).Headers!["Accept"]);

        Harness threadHarness = new(source, PluginFixture(fixtureDirectory, threadFixture), board: board, thread: threadId);
        ThreadSnapshot thread = Assert.IsType<ThreadSnapshot>(await threadHarness.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, thread, threadHarness.Source));
        Assert.Equal(openerAttachmentCount, thread.Snapshot.Posts[0].Attachments!.Count);
        Assert.Equal(openerFormat, thread.Snapshot.Posts[0].TextFormat);
        Assert.All(thread.Snapshot.Posts[0].Attachments!, file =>
        {
            Assert.StartsWith("https://" + threadHarness.Host + "/.media/", file.Url!.AbsoluteUri);
            Assert.DoesNotContain("..", file.Url.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(thread.Snapshot.Posts[0].PostId + "-" + file.Position, file.MediaId);
        });
        SourcePost emptyListReply = Assert.Single(thread.Snapshot.Posts, post => post.PostId == threadIds[1]);
        Assert.True(emptyListReply.Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Empty(emptyListReply.Attachments!);
        Assert.Equal("https://" + threadHarness.Host + "/" + board + "/res/" + threadId + ".json",
            Assert.Single(threadHarness.Http.Requests).Url.AbsoluteUri);

        CatalogReadResult catalogCopy = SdkJson.Deserialize<CatalogReadResult>(SdkJson.Serialize<CatalogReadResult>(catalog));
        ThreadReadResult threadCopy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(thread));
        Assert.Empty(SdkValidation.ValidateCatalogResult(catalogHarness.CatalogRequest, catalogCopy, catalogHarness.Source));
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, threadCopy, threadHarness.Source));
    }

    [Fact]
    public async Task BbwChanNumberedPageUsesOneExplicitNextPage()
    {
        Harness h = new("bbwchan", PluginFixture("BbwChan", "board-page.json"), status: 200, board: "bbw");
        CatalogReadRequest request = h.CatalogRequest with { Page = 1 };
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.Plugin.ReadCatalogAsync(request, h.Context));
        Assert.Empty(SdkValidation.ValidateCatalogResult(request, result, h.Source));
        Assert.Equal(2, result.Snapshot.NextPage);
        Assert.False(result.Snapshot.IsComplete);
        Assert.Equal("https://bbw-chan.link/bbw/1.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task FchanRemainsHttpOnlyAndParsesCatalogThreadAndGlobalMedia()
    {
        Harness catalogHarness = new("fchan", PluginFixture("Fchan", "board-index.html"), board: "f");
        CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await catalogHarness.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(catalogHarness.CatalogRequest, catalog, catalogHarness.Source));
        Assert.Equal(new[] { "301", "302" }, catalog.Snapshot.Threads.Select(thread => thread.Id.ThreadId));
        Assert.Equal("http://fchan.us/f/index.html", Assert.Single(catalogHarness.Http.Requests).Url.AbsoluteUri);

        Harness threadHarness = new("fchan", Encoding.Latin1.GetBytes(PluginFixture("Fchan", "thread-current.html")),
            board: "f", thread: "301");
        ThreadSnapshot thread = Assert.IsType<ThreadSnapshot>(await threadHarness.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, thread, threadHarness.Source));
        Assert.Equal(new[] { "301", "302" }, thread.Snapshot.Posts.Select(post => post.PostId));
        Assert.Equal("Synthetic & subject", thread.Snapshot.Posts[0].Subject);
        SourceAttachment attachment = Assert.Single(thread.Snapshot.Posts[0].Attachments!);
        Assert.Equal("http://fchan.us/src/sample%5B1%5D.png", attachment.Url!.AbsoluteUri);
        Assert.Equal(".png", attachment.Extension);
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(thread));
        Assert.Empty(SdkValidation.ValidateThreadResult(threadHarness.ThreadRequest, copy, threadHarness.Source));
        Assert.Equal("sample.png", Assert.IsType<ThreadSnapshot>(copy).Snapshot.Posts[0].Attachments![0].OriginalFileName);
    }

    [Theory]
    [InlineData("sevenchan")]
    [InlineData("bbwchan")]
    [InlineData("capybarachan")]
    [InlineData("eightchanmoe")]
    [InlineData("fchan")]
    public async Task SupportedExperimentalSourcesSeparateExact404FromUnexpectedRedirects(string source)
    {
        (string Board, string Thread) identity = source switch
        {
            "sevenchan" => ("x", "100"),
            "bbwchan" => ("bbw", "123"),
            "capybarachan" => ("site", "101"),
            "eightchanmoe" => ("board", "201"),
            _ => ("f", "301"),
        };
        string missing = source is "sevenchan" or "bbwchan"
            ? RecordedHttpResponseBody(source == "sevenchan" ? "SevenChan" : "BbwChan",
                source == "sevenchan" ? "404.html" : "404.json-or-html.http")
            : "missing";
        Harness h = new(source, missing, 404, identity.Board, identity.Thread);
        ThreadNotFound thread = Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.Equal(404, thread.HttpStatusCode);
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, thread, h.Source));
        CatalogFailure board = Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        Assert.Equal("board_unavailable", board.Failure.Code);
        Assert.Equal(404, board.Failure.HttpStatusCode);
        Assert.Equal(2, h.Http.Requests.Count);

        h.Http.FinalUri = new Uri("https://login.example.test/redirected");
        h.Http.Status = 301;
        ThreadFailure redirectedThread = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal("unexpected_endpoint", redirectedThread.Failure.Code);
        Assert.Equal(301, redirectedThread.Failure.HttpStatusCode);
        CatalogFailure redirectedCatalog = Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        Assert.Equal("unexpected_endpoint", redirectedCatalog.Failure.Code);
        Assert.Equal(301, redirectedCatalog.Failure.HttpStatusCode);
        Assert.Equal(4, h.Http.Requests.Count);
        Assert.All(h.Http.Requests, request => Assert.Equal(h.Host, request.Url.Host));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    public async Task EightchanMoeChallengeIsExactAndNeverSendsCredentialsOrUsesMirrors(int status)
    {
        Harness h = new("eightchanmoe", PluginFixture("EightchanMoe", "powblock-challenge.html"),
            status, "board", "201");
        ThreadFailure thread = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal("challenge_required", thread.Failure.Code);
        Assert.Equal(status, thread.Failure.HttpStatusCode);
        CatalogFailure catalog = Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        Assert.Equal("challenge_required", catalog.Failure.Code);
        Assert.Equal(status, catalog.Failure.HttpStatusCode);
        Assert.Equal(2, h.Http.Requests.Count);
        Assert.All(h.Http.Requests, request =>
        {
            Assert.Equal("8chan.moe", request.Url.Host);
            Assert.DoesNotContain("Cookie", request.Headers!.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", request.Headers!.Keys, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task FchanDisclaimerRequiresUserActionWithoutWritingACookie()
    {
        Harness h = new("fchan", "<!doctype html><title>disclaimer</title><p>Confirmation required.</p>", board: "f", thread: "301");
        ThreadFailure thread = Assert.IsType<ThreadFailure>(await h.ReadThread());
        CatalogFailure catalog = Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        Assert.Equal("user_action_required", thread.Failure.Code);
        Assert.Equal("user_action_required", catalog.Failure.Code);
        Assert.Equal(2, h.Http.Requests.Count);
        Assert.All(h.Http.Requests, request =>
        {
            Assert.Equal("http", request.Url.Scheme);
            Assert.DoesNotContain("Cookie", request.Headers!.Keys, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("sevenchan", "<html><body></body></html>")]
    [InlineData("bbwchan", "{")]
    [InlineData("capybarachan", "{}")]
    [InlineData("eightchanmoe", "[]")]
    [InlineData("fchan", "<html><body></body></html>")]
    public async Task MalformedExperimentalPayloadsCannotCommitValidators(string source, string payload)
    {
        Harness h = new(source, payload, board: "demo", thread: "1");
        ThreadFailure result = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal("invalid_payload", result.Failure.Code);
        Assert.Equal(200, result.Failure.HttpStatusCode);
        Assert.DoesNotContain("entityTag", Encoding.UTF8.GetString(SdkJson.Serialize<ThreadReadResult>(result)));
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result));
        Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(copy).Failure.Code);
    }

    [Theory]
    [InlineData("sevenchan")]
    [InlineData("fchan")]
    public async Task HtmlExperimentalSourcesIgnoreUntrustedMediaPathsWithoutHostUpgrades(string source)
    {
        foreach (string path in new[] { "https://foreign.test/file.png", "//foreign.test/file.png",
            "/other/src/file.png", "/src/file.png?secret=1" })
        {
            string html = source == "sevenchan"
                ? """<html><body><div id="1" class="post"><a href="/x/src/safe.png">safe</a><a href="UNSAFE">unsafe</a></div></body></html>""".Replace("UNSAFE", path)
                : """<html><body><div id="thread1"><span class="subject">s</span><a href="/src/safe.png">safe</a><a href="UNSAFE">unsafe</a></div></body></html>""".Replace("UNSAFE", path);
            Harness h = new(source, html, board: source == "sevenchan" ? "x" : "f", thread: "1");
            ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
            SourceAttachment attachment = Assert.Single(result.Snapshot.Posts[0].Attachments!);
            Assert.Equal(source == "sevenchan" ? "https://7chan.org/x/src/safe.png" : "http://fchan.us/src/safe.png", attachment.Url!.AbsoluteUri);
            Assert.Equal(h.Source.BaseUri.Scheme, attachment.Url.Scheme);
        }
    }

    [Theory]
    [InlineData("bbwchan", "BbwChan", "thread.json", "bbw", "123")]
    [InlineData("capybarachan", "CapybaraChan", "media-thread.json", "site", "101")]
    [InlineData("eightchanmoe", "EightchanMoe", "lynxchan-thread.json", "board", "201")]
    public async Task LynxChanExperimentalSourcesRejectForeignTraversalOrQueriedMedia(
        string source, string directory, string fixture, string board, string threadId)
    {
        foreach (string path in new[] { "https://foreign.test/file.png", "/.media/%2e%2e/file.png", "/.media/file.png?secret=1" })
        {
            JsonNode root = JsonNode.Parse(PluginFixture(directory, fixture))!;
            root["files"]![0]!["path"] = path;
            Harness h = new(source, root.ToJsonString(), board: board, thread: threadId);
            ThreadFailure result = Assert.IsType<ThreadFailure>(await h.ReadThread());
            Assert.Equal("invalid_payload", result.Failure.Code);
        }
    }

    [Fact]
    public async Task ExperimentalSourcesRejectInvalidIdentitiesBeforeNetworkAndEnforceBodyBudget()
    {
        foreach (string source in new[] { "sevenchan", "bbwchan", "capybarachan", "eightchanmoe", "fchan" })
        {
            Harness invalid = new(source, "unused");
            Assert.Equal("invalid_resource", Assert.IsType<ThreadFailure>(await invalid.Plugin.ReadThreadAsync(
                invalid.ThreadRequest with { Thread = new SourceThreadId(source, "../escape", "123") }, invalid.Context)).Failure.Code);
            Assert.Equal("invalid_resource", Assert.IsType<CatalogFailure>(await invalid.Plugin.ReadCatalogAsync(
                invalid.CatalogRequest with { Board = new SourceBoardId(source, "../escape") }, invalid.Context)).Failure.Code);
            Assert.Empty(invalid.Http.Requests);

            Harness oversized = new(source, new byte[SdkLimits.MaximumMetadataBytes + 1]);
            Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await oversized.ReadThread()).Failure.Code);
        }
    }

    private static string Fixture(string source, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", source + "-" + name + ".json"));

    private static string PluginFixture(string directory, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PluginFixtures", directory, name));

    private static string RecordedHttpResponseBody(string directory, string name)
    {
        string text = PluginFixture(directory, name);
        int boundary = text.IndexOf("\n\n", StringComparison.Ordinal);
        return boundary < 0 ? text : text[(boundary + 2)..];
    }

    private static ISourcePlugin CreatePlugin(string source) => source switch
    {
        "lainchan" => new LainchanPlugin(),
        "kohlchan" => new KohlchanPlugin(),
        "sevenchan" => new SevenChanPlugin(),
        "bbwchan" => new BbwChanPlugin(),
        "anonimageboards" => new AnonImageboardsPlugin(),
        "capybarachan" => new CapybaraChanPlugin(),
        "eightchanmoe" => new EightchanMoePlugin(),
        "fchan" => new FchanPlugin(),
        "gurocx" => new GuroCxPlugin(),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private sealed class Harness
    {
        internal ISourcePlugin Plugin { get; }
        internal SourceDescription Source { get; }
        internal string Host => Source.BaseUri.Host;
        internal SourceValidators Validators { get; } = new SourceValidators(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; }
        internal CatalogReadRequest CatalogRequest { get; }
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string source, string body, int status = 200, string board = "demo", string thread = "123")
            : this(source, Encoding.UTF8.GetBytes(body), status, board, thread) { }

        internal Harness(string source, byte[] body, int status = 200, string board = "demo", string thread = "123")
        {
            Plugin = CreatePlugin(source);
            PluginDescription description = Plugin.Describe();
            Source = description.Sources.Count == 0 ? null! : description.Sources[0];
            ThreadRequest = new ThreadReadRequest(new SourceThreadId(description.Sources.Count == 0 ? source : Source.SourceId, board, thread));
            CatalogRequest = new CatalogReadRequest(new SourceBoardId(description.Sources.Count == 0 ? source : Source.SourceId, board));
            Http = new StubHttp(body, status, Validators);
            SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
            Context = new PluginCallContext(new PluginConfiguration("fixture", empty,
                new Dictionary<string, SettingsSnapshot> { [description.Sources.Count == 0 ? source : Source.SourceId] = empty }),
                Http, new NoOtherServices(), new NoOtherServices(), new NoOtherServices());
        }
        internal Task<ThreadReadResult> ReadThread() => Plugin.ReadThreadAsync(ThreadRequest, Context);
        internal Task<CatalogReadResult> ReadCatalog() => Plugin.ReadCatalogAsync(CatalogRequest, Context);
    }

    private sealed class StubHttp(byte[] body, int status, SourceValidators validators) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = new List<HostHttpRequest>();
        internal byte[] Body { get; set; } = body;
        internal int Status { get; set; } = status;
        internal Uri? FinalUri { get; set; }
        internal HostHttpResult? Result { get; set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result ?? new HostHttpResponse(Status, FinalUri ?? request.Url, new Dictionary<string, string>(), Body, validators));
        }
    }

    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source must not read image bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Single-file source needs no resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Fixture source must not log source content.");
    }
}
