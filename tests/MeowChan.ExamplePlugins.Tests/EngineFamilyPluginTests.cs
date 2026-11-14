using System.Text;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.JschanFamily;
using MeowChan.Plugins.Examples.Kohlchan;
using MeowChan.Plugins.Examples.VichanFamily;

namespace MeowChan.ExamplePlugins.Tests;

/// <summary>
/// One engine-family plugin serves several sites. Source IDs are persistent identities (watches, archives,
/// rules and per-source permissions use them), so every site keeps exactly the declaration it had as a
/// single-site plugin; only the owning plugin identity changes.
/// </summary>
public sealed class EngineFamilyPluginTests
{
    public static TheoryData<string> VichanSites => new() { "lainchan", "leftypol", "sushichan", "uboachan", "wizchan" };
    public static TheoryData<string> AllSites => new() { "lainchan", "leftypol", "sushichan", "uboachan", "wizchan", "zzzchan" };

    [Fact]
    public void FamiliesDeclareExactlyTheFormerSingleSiteSourceIdentities()
    {
        PluginDescription vichan = new VichanFamilyPlugin().Describe();
        PluginDescription jschan = new JschanFamilyPlugin().Describe();
        Assert.Equal("meowchan.source.vichan", vichan.PluginId);
        Assert.Equal("meowchan.source.jschan", jschan.PluginId);
        Assert.Equal(new[] { "lainchan", "leftypol", "sushichan", "uboachan", "wizchan" }, vichan.Sources.Select(source => source.SourceId));
        Assert.Equal(new[] { "zzzchan" }, jschan.Sources.Select(source => source.SourceId));
        Assert.Equal(new[] { "Lainchan", "Leftypol", "SushiChan", "Uboachan", "Wizchan" }, vichan.Sources.Select(source => source.Name));
        Assert.Equal("ZZZchan", Assert.Single(jschan.Sources).Name);
        // Both families together with an unrelated single-site plugin are free of identity and route conflicts.
        Assert.Empty(SdkValidation.ValidateDescriptions([vichan, jschan, new KohlchanPlugin().Describe()]));
        foreach (PluginDescription description in new[] { vichan, jschan })
        {
            Assert.Empty(description.Settings.Fields);
            Assert.All(description.Sources, source =>
            {
                // Each site keeps its own host: no family-wide or cross-site transport grant.
                HttpHostDeclaration host = Assert.Single(source.HttpHosts);
                Assert.Equal(source.BaseUri.Host, host.Host);
                Assert.All(source.Routes, route => Assert.Equal(host.Host, route.Host));
                InformationEntry site = Assert.Single(description.Information, entry => entry.SourceId == source.SourceId);
                Assert.Equal(source.Name, site.Label);
                Assert.Contains(host.Host, site.Value);
            });
        }
        Assert.Equal(1, typeof(VichanFamilyPlugin).Assembly.GetExportedTypes().Length);
        Assert.Equal(1, typeof(JschanFamilyPlugin).Assembly.GetExportedTypes().Length);
    }

    [Theory]
    [MemberData(nameof(AllSites))]
    public void EverySiteKeepsItsFormerSingleSiteDeclarationByteForByte(string sourceId)
    {
        // The literal declarations of the former single-site plugins (1.0.2/1.0.3), kept here as the
        // stable-identity contract: ID, name, origin, capabilities, routes, hosts, order and maturity.
        SourceCapabilities capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia;
        SettingsSchema empty = new SettingsSchema("1", Array.Empty<SettingDefinition>());
        SourceDescription Legacy(string id, string name, string host, UrlRoute[] routes, SourceMaturity maturity) =>
            new SourceDescription(id, name, new Uri("https://" + host + "/"), capabilities, routes,
                [new HttpHostDeclaration(host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)], empty, PostOrderKind.DecimalPostId) { Maturity = maturity };
        UrlRoute Board(string host) => new UrlRoute(SourceResourceKind.Board, host, "/{board}/", IsCanonical: true);
        UrlRoute Index(string host) => new UrlRoute(SourceResourceKind.Board, host, "/{board}/index.html", AllowTrailingSlash: false);
        UrlRoute Thread(string host, string path = "/{board}/res/{thread}.html") =>
            new UrlRoute(SourceResourceKind.Thread, host, path, IsCanonical: true, AllowTrailingSlash: false) { ThreadIdentifierKind = RouteIdentifierKind.Decimal };
        SourceDescription expected = sourceId switch
        {
            "lainchan" => Legacy("lainchan", "Lainchan", "lainchan.org", [Board("lainchan.org"),
                new UrlRoute(SourceResourceKind.Thread, "lainchan.org", "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false)], SourceMaturity.Unspecified),
            "leftypol" => Legacy("leftypol", "Leftypol", "leftypol.org", [Board("leftypol.org"), Thread("leftypol.org")], SourceMaturity.Experimental),
            "sushichan" => Legacy("sushichan", "SushiChan", "sushigirl.cafe", [Board("sushigirl.cafe"), Thread("sushigirl.cafe")], SourceMaturity.Experimental),
            "uboachan" => Legacy("uboachan", "Uboachan", "uboachan.net", [Board("uboachan.net"), Thread("uboachan.net")], SourceMaturity.Experimental),
            "wizchan" => Legacy("wizchan", "Wizchan", "wizchan.org", [Board("wizchan.org"), Index("wizchan.org"), Thread("wizchan.org")], SourceMaturity.Experimental),
            "zzzchan" => Legacy("zzzchan", "ZZZchan", "zzzchan.xyz", [Board("zzzchan.xyz"), Index("zzzchan.xyz"), Thread("zzzchan.xyz", "/{board}/thread/{thread}.html")], SourceMaturity.Experimental),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceId))
        };
        Assert.Equal(Encoding.UTF8.GetString(SdkJson.Serialize(expected)), Encoding.UTF8.GetString(SdkJson.Serialize(Site(sourceId).Source)));
    }

    [Theory]
    [MemberData(nameof(AllSites))]
    public async Task EachSiteReadsItsThreadFixtureThroughTheFamilyOnItsOwnHost(string sourceId)
    {
        (ISourcePlugin plugin, SourceDescription source) = Site(sourceId);
        Http http = new(File.ReadAllBytes(Fixture(sourceId, "thread")));
        ThreadReadRequest request = new(new SourceThreadId(sourceId, "demo", "123"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await plugin.ReadThreadAsync(request, Context(sourceId, http)));
        Assert.Empty(SdkValidation.ValidateThreadResult(request, result, source));
        Assert.Equal(sourceId, result.Snapshot.Thread.SourceId);
        Assert.Equal("123", result.Snapshot.Posts[0].PostId);
        Assert.StartsWith("https://" + source.BaseUri.Host + "/demo/", result.Snapshot.Url.AbsoluteUri);
        Assert.All(result.Snapshot.Posts.SelectMany(post => post.Attachments ?? []).Where(file => file.Url is not null),
            file => Assert.Equal(source.BaseUri.Host, file.Url!.Host));
        HostHttpRequest sent = Assert.Single(http.Requests);
        Assert.Equal(sourceId, sent.SourceId);
        Assert.Equal(source.BaseUri.Host, sent.Url.Host);
    }

    [Theory]
    [MemberData(nameof(AllSites))]
    public async Task EachSiteReadsItsCatalogFixtureThroughTheFamily(string sourceId)
    {
        (ISourcePlugin plugin, SourceDescription source) = Site(sourceId);
        Http http = new(File.ReadAllBytes(Fixture(sourceId, "catalog")));
        CatalogReadRequest request = new(new SourceBoardId(sourceId, "demo"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await plugin.ReadCatalogAsync(request, Context(sourceId, http)));
        Assert.Empty(SdkValidation.ValidateCatalogResult(request, result, source));
        Assert.NotEmpty(result.Snapshot.Threads);
        Assert.All(result.Snapshot.Threads, thread => Assert.Equal(sourceId, thread.Id.SourceId));
        Assert.Equal("https://" + source.BaseUri.Host + "/demo/catalog.json", Assert.Single(http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(VichanSites))]
    public async Task ASiteQuirkStaysWithItsSite(string sourceId)
    {
        // The Uboachan thread fixture uses the MD5-or-SHA-1 digest field and ext:"deleted" slots. Only Uboachan reads
        // them that way; on any other site the 20-byte digest stays an opaque provider MD5 and the slot is a file.
        (ISourcePlugin plugin, _) = Site(sourceId);
        ThreadReadRequest request = new(new SourceThreadId(sourceId, "demo", "123"));
        ThreadReadResult result = await plugin.ReadThreadAsync(request, Context(sourceId, new Http(File.ReadAllBytes(Fixture("uboachan", "thread")))));
        if (sourceId == "leftypol")
        {
            // Leftypol reads files[]; the classic tim/ext fields are not attachments there.
            Assert.Null(Assert.IsType<ThreadSnapshot>(result).Snapshot.Posts[0].Attachments);
            return;
        }
        if (sourceId != "uboachan")
        {
            // "tim":"" in the deleted slot is not a valid provider token outside Uboachan's marker rule.
            Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(result).Failure.Code);
            return;
        }
        SourceAttachment[] files = Assert.IsType<ThreadSnapshot>(result).Snapshot.Posts[0].Attachments!.ToArray();
        Assert.Null(files[0].OriginalFileName);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAA=", files[1].ExpectedSha1);
        Assert.Null(files[1].ExpectedMd5);
        Assert.True(files[2].IsDeleted);
    }

    [Theory]
    [InlineData("kohlchan")]
    [InlineData("4chan")]
    [InlineData("LAINCHAN")]
    [InlineData("")]
    public async Task AFamilyRejectsSourcesOutsideItsTableWithoutAnyRequest(string sourceId)
    {
        foreach (ISourcePlugin plugin in new ISourcePlugin[] { new VichanFamilyPlugin(), new JschanFamilyPlugin() })
        {
            Http http = new(Encoding.UTF8.GetBytes("{}"));
            Assert.Equal("invalid_resource", Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(
                new ThreadReadRequest(new SourceThreadId(sourceId.Length == 0 ? "x" : sourceId, "demo", "123")), Context(sourceId, http))).Failure.Code);
            Assert.Equal("invalid_resource", Assert.IsType<CatalogFailure>(await plugin.ReadCatalogAsync(
                new CatalogReadRequest(new SourceBoardId(sourceId.Length == 0 ? "x" : sourceId, "demo")), Context(sourceId, http))).Failure.Code);
            Assert.Empty(http.Requests);
        }
        // A jschan site is not a vichan site and vice versa.
        Http other = new(Encoding.UTF8.GetBytes("{}"));
        Assert.IsType<ThreadFailure>(await new VichanFamilyPlugin().ReadThreadAsync(new ThreadReadRequest(new SourceThreadId("zzzchan", "demo", "123")), Context("zzzchan", other)));
        Assert.IsType<ThreadFailure>(await new JschanFamilyPlugin().ReadThreadAsync(new ThreadReadRequest(new SourceThreadId("lainchan", "demo", "123")), Context("lainchan", other)));
        Assert.Empty(other.Requests);
    }

    private static (ISourcePlugin Plugin, SourceDescription Source) Site(string sourceId)
    {
        ISourcePlugin plugin = sourceId == "zzzchan" ? new JschanFamilyPlugin() : new VichanFamilyPlugin();
        return (plugin, Assert.Single(plugin.Describe().Sources, source => source.SourceId == sourceId));
    }

    private static string Fixture(string sourceId, string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", sourceId + "-" + name + ".json");

    private static PluginCallContext Context(string sourceId, IHostHttp http)
    {
        SettingsSnapshot empty = new("1", Array.Empty<SettingValue>());
        NoOtherServices none = new();
        return new(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { [sourceId] = empty }), http, none, none, none);
    }

    private sealed class Http(byte[] body) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = new();
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), body,
                new SourceValidators(EntityTag: "\"fixture\"")));
        }
    }

    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source parsers never read original bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Single-file family plugins need no resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Do not log source content.");
    }
}
