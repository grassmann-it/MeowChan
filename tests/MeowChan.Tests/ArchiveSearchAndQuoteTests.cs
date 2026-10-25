using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveSearchAndQuoteTests
{
    private static readonly ResourceId Id = new ResourceId("4chan", "g", "123");
    private static CapturedPost Post(string id, int ordinal, string html) => new CapturedPost(id, ordinal, PostFieldPresence.All, Html: html);
    private static CapturedThreadSnapshot Snapshot(string media, ResourceId? id = null, params CapturedPost[] posts)
    {
        ResourceId thread = id ?? Id;
        return new CapturedThreadSnapshot(thread, $"https://boards.4chan.org/{thread.Board}/thread/{thread.Thread}", "Ärger &amp; title",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts);
    }

    [Fact]
    public async Task FailedQuoteCaptureRollsBackPostsAndBothSearchIndexes()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("123", 0, "oldbodyneedle")) with { Title = "oldtitleneedle" });
        using (SqliteConnection connection = Open(catalog.DatabasePath))
        using (SqliteCommand failure = connection.CreateCommand())
        {
            failure.CommandText = "CREATE TRIGGER fail_quotes BEFORE INSERT ON archive_quotes BEGIN SELECT RAISE(ABORT,'synthetic quote failure'); END";
            failure.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => catalog.CaptureAsync(Snapshot(directory.Path, Id,
            Post("123", 0, "<a href='#p123'>&gt;&gt;123</a> newbodyneedle")) with { Title = "newtitleneedle" }));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("oldbodyneedle"))).Items);
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("oldtitleneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("newbodyneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("newtitleneedle"))).Items);
        Assert.Empty((await reader.GetQuotesAsync(new ArchivePostId(Id, "123"))).Items);
    }

    [Fact]
    public async Task IndexUsesMergedFieldsAndRegistrationDoesNotOverwriteObservedTitles()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("123", 0, "body") with
            { Subject = "subjectneedle", Author = "authorneedle" }) with { Title = "observedtitleneedle" });
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Id, "https://boards.4chan.org/g/thread/123",
            "pendingtitleneedle", directory.Path, false));
        await catalog.CaptureAsync(Snapshot(directory.Path, Id,
            new CapturedPost("123", 123, PostFieldPresence.Html, Html: "newbodyneedle")) with { Title = "updatedtitleneedle" });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        foreach (string query in new[] { "subjectneedle", "authorneedle", "newbodyneedle", "updatedtitleneedle" })
            Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery(query))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("pendingtitleneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("observedtitleneedle"))).Items);
    }

    [Fact]
    public async Task BacklinksAreBoundedAndPagesStayImmutableAcrossLaterCaptures()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("123", 0, "target"),
            Post("124", 1, "<a href='#p123'>&gt;&gt;123</a>"), Post("125", 2, "<a href='#p123'>&gt;&gt;123</a>")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivePostId target = new ArchivePostId(Id, "123");
        ArchiveQuotePage first = await reader.GetBacklinksAsync(target, limit: 1);
        IReadOnlyDictionary<string, int> counts = await reader.GetBacklinkCountsAsync(Id, ["123", "124", "absent"]);
        Assert.Equal(2, counts["123"]);
        Assert.Equal(first.TotalCount, counts["123"]);
        Assert.Equal(0, counts["124"]);
        Assert.Equal(0, counts["absent"]);
        ArchiveQuotePage second = await reader.GetBacklinksAsync(target, offset: 1, limit: 1);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal("124", first.Items.Single().Origin.PostId);
        Assert.Equal("125", second.Items.Single().Origin.PostId);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("124", 1, "no reference")));
        Assert.Equal(2, first.TotalCount);
        Assert.Equal("124", first.Items.Single().Origin.PostId);
        Assert.Equal(1, (await reader.GetBacklinksAsync(target)).TotalCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetBacklinksAsync(target, limit: 501));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SearchThreadsAsync(new ArchiveThreadQuery(), offset: -1));
    }

    [Fact]
    public async Task LegacyReaderRemainsReadOnlyAndWriterAtomicallyMigratesPostsReferencesAndDecisions()
    {
        using TempDirectory directory = new TempDirectory();
        string path = CreateLegacyFixture(directory.Path);
        byte[] before = await File.ReadAllBytesAsync(path);
        await using ArchiveReader legacy = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Single((await legacy.SearchThreadsAsync(new ArchiveThreadQuery("legacyneedle"))).Items);
        Assert.Single((await legacy.GetBacklinksAsync(new ArchivePostId(Id, "123"))).Items);
        Assert.Equal(1, (await legacy.GetBacklinkCountsAsync(Id, ["123", "absent"]))["123"]);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + "-journal"));
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        using (SqliteConnection connection = Open(path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(5L, command.ExecuteScalar());
        }
        await using ArchiveReader indexed = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Equal(2, (await indexed.ReadThreadAsync(Id))!.PostCount);
        Assert.False((await indexed.ReadThreadAsync(Id))!.Summary.CaptureEnabled);
        Assert.Single((await indexed.SearchThreadsAsync(new ArchiveThreadQuery("LEGACYNEEDLE"))).Items);
        Assert.Single((await indexed.GetBacklinksAsync(new ArchivePostId(Id, "123"))).Items);
        MeowChan.Archive.CandidateDecision decision = Assert.Single(await catalog.GetDecisionsAsync(Id));
        Assert.Equal(MeowChan.Rules.MediaSelectionOutcome.Excluded, decision.Selection.Outcome);
        Assert.Equal("legacy-rules", decision.Selection.EffectiveRule.Revision);
        byte[] indexedBefore = await File.ReadAllBytesAsync(path);
        await indexed.SearchThreadsAsync(new ArchiveThreadQuery("legacyneedle"));
        await indexed.GetQuotesAsync(new ArchivePostId(Id, "124"));
        Assert.Equal(indexedBefore, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedMigrationRollsBackDdlAndPreservesTheLegacyCatalogByteForByte()
    {
        using TempDirectory directory = new TempDirectory();
        string path = CreateLegacyFixture(directory.Path);
        using (SqliteConnection connection = Open(path))
        using (SqliteCommand sabotage = connection.CreateCommand())
        {
            sabotage.CommandText = "CREATE TABLE archive_quotes(occupied TEXT)";
            sabotage.ExecuteNonQuery();
        }
        byte[] before = await File.ReadAllBytesAsync(path);
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await Assert.ThrowsAsync<SqliteException>(() => ArchiveCatalog.OpenAsync(directory.Path, lease));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("legacyneedle"))).Items);
        using SqliteConnection verify = Open(path);
        using SqliteCommand version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, version.ExecuteScalar());
    }

    [Fact]
    public void ParserRequiresActualHrefAttributesAndLeavesSameSiteUnknownFormatsUnresolved()
    {
        CapturedPost post = Post("123", 0,
            "<a data-href='#p123'>&gt;&gt;fake</a><a title=\"href='#p123'\">&gt;&gt;fake2</a>" +
            "<a title='a>b' href='#p123'>&gt;&gt;123</a><a href='/unsupported#p123'>&gt;&gt;unknown</a>");
        IReadOnlyList<ArchiveQuote> quotes = ArchiveQuoteParser.Parse(Id, "https://boards.4chan.org/g/thread/123", post);
        Assert.Equal(2, quotes.Count);
        Assert.Equal(new ArchivePostId(Id, "123"), quotes[0].Target);
        Assert.Null(quotes[1].Target);
        Assert.False(quotes[1].IsExternal);
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string CreateLegacyFixture(string directory)
    {
        string path = Path.Combine(directory, "archive.db");
        using SqliteConnection connection = Open(path);
        using SqliteCommand command = connection.CreateCommand();
        string name = typeof(ArchiveSearchAndQuoteTests).Assembly.GetManifestResourceNames().Single(name => name.EndsWith("archive-schema-1.sql", StringComparison.Ordinal));
        using Stream stream = typeof(ArchiveSearchAndQuoteTests).Assembly.GetManifestResourceStream(name)!;
        using StreamReader schema = new StreamReader(stream);
        command.CommandText = schema.ReadToEnd();
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO archive_threads VALUES('4chan','g','123','https://boards.4chan.org/g/thread/123','legacy title',$directory,1,0,$date,$date,9);
            INSERT INTO archive_posts VALUES('4chan','g','123','123',123,63,$date,'subject','author','legacyneedle',0);
            INSERT INTO archive_posts VALUES('4chan','g','123','124',124,63,$date,NULL,NULL,'<a href="#p123">&gt;&gt;123</a>',0);
            INSERT INTO archive_media_directories VALUES('4chan','g','123',$directory);
            INSERT INTO media_decisions VALUES('4chan','g','123','777','original','https://i.4cdn.org/g/777.png',$file,4,NULL,'name',NULL,'.png',$selection,$date);
            """;
        command.Parameters.AddWithValue("$directory", Path.Combine(directory, "disconnected-nas"));
        command.Parameters.AddWithValue("$file", Path.Combine(directory, "disconnected-nas", "777.png"));
        command.Parameters.AddWithValue("$date", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).ToString("O"));
        command.Parameters.AddWithValue("$selection", """
            {"Version":1,"Outcome":1,"Reason":2,"Revision":"legacy-rules","TypesMode":2,"Extensions":[],"SizeMode":1,"MaximumBytes":null,
             "TypesOrigin":{"Revision":"legacy-rules","Kind":0},"SizeOrigin":{"Revision":"legacy-rules","Kind":null},"ObservedExtension":".png","ObservedSize":4}
            """);
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public async Task LiteralSearchDecodesTextIgnoresScriptsAndSupportsShortTermsFiltersAndStablePages()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("123", 0, "<p>Green &ouml;l foo\" OR * &amp; bar</p><script>secretneedle</script>")));
        ResourceId other = new ResourceId("8kun", "test", "456");
        await catalog.CaptureAsync(Snapshot(directory.Path, other, Post("456", 0, "other green text")));
        await catalog.SetCaptureEnabledAsync(other, false);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("4chan", "x", "789"),
            "https://boards.4chan.org/x/thread/789", "media-only", directory.Path, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        foreach (string text in new[] { "GREEN", "öl", "ÄRGER", "foo\" OR *", "& bar" })
            Assert.Contains((await reader.SearchThreadsAsync(new ArchiveThreadQuery(text))).Items, item => item.Id == Id);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("secretneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("* NOT green"))).Items);
        ArchiveThreadPage first = await reader.SearchThreadsAsync(new ArchiveThreadQuery("green"), limit: 1);
        ArchiveThreadPage second = await reader.SearchThreadsAsync(new ArchiveThreadQuery("green"), offset: 1, limit: 1);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.NotEqual(first.Items.Single().Id, second.Items.Single().Id);
        Assert.Equal(Id, Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("green", Source: "4chan", Board: "g",
            SourceState: ThreadSourceState.Archived, CaptureEnabled: true, HasPosts: true))).Items).Id);
        Assert.Equal("789", Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery(HasPosts: false))).Items).Id.Thread);
    }

    [Fact]
    public async Task SearchAndBacklinksRetainMissingPostsButReplaceTheirLastObservedTextAndQuotes()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id,
            Post("123", 0, "retained token"), Post("124", 1, "<a class='quotelink' href='#p123'>&gt;&gt;123</a> originalneedle")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivePostId target = new ArchivePostId(Id, "123");
        Assert.Single((await reader.GetBacklinksAsync(target)).Items);
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("124", 0, "updatedneedle")));
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("retained token"))).Items);
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("updatedneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("originalneedle"))).Items);
        Assert.Empty((await reader.GetBacklinksAsync(target)).Items);
    }

    [Fact]
    public async Task QuoteResolutionUsesLocalCapturedIdentityAcrossBoardsAndReturnsActualReaderPageIndex()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        ResourceId targetId = new ResourceId("4chan", "x", "456");
        await catalog.CaptureAsync(Snapshot(directory.Path, targetId,
            Enumerable.Range(456, 81).Select((id, ordinal) => Post(id.ToString(), ordinal, "target")).ToArray()));
        await catalog.CaptureAsync(Snapshot(directory.Path, Id, Post("123", 0,
            "<a href='/x/thread/456#p530'>&gt;&gt;&gt;/x/530</a> " +
            "<a href='#p999'>&gt;&gt;999</a> <a href='/x/thread/999#p999'>&gt;&gt;999</a> " +
            "<a href='https://other.invalid/g/thread/123#p123'>&gt;&gt;external</a> " +
            "<a href='javascript:alert(1)'>&gt;&gt;bad</a> <script><a href='#p123'>&gt;&gt;hidden</a></script>")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        IReadOnlyList<ArchiveQuote> quotes = (await reader.GetQuotesAsync(new ArchivePostId(Id, "123"))).Items;
        Assert.Equal(5, quotes.Count);
        ArchiveQuoteResolution resolved = await reader.ResolveQuoteAsync(quotes[0]);
        Assert.Equal(ArchiveQuoteState.Captured, resolved.State);
        Assert.Equal(74, resolved.PostIndex);
        Assert.Equal("530", resolved.Post!.PostId);
        Assert.Equal("530", resolved.View!.GetPosts(74, 1).Single().PostId);
        await catalog.CaptureAsync(Snapshot(directory.Path, targetId, Post("455", 0, "inserted earlier")));
        Assert.Equal("530", resolved.View.GetPosts(74, 1).Single().PostId);
        Assert.Equal(75, (await reader.ResolveQuoteAsync(quotes[0])).PostIndex);
        Assert.Equal(ArchiveQuoteState.MissingPost, (await reader.ResolveQuoteAsync(quotes[1])).State);
        Assert.Equal(ArchiveQuoteState.MissingThread, (await reader.ResolveQuoteAsync(quotes[2])).State);
        Assert.Equal(ArchiveQuoteState.External, (await reader.ResolveQuoteAsync(quotes[3])).State);
        Assert.Equal(ArchiveQuoteState.Unresolved, (await reader.ResolveQuoteAsync(quotes[4])).State);
        ArchiveQuote backlink = Assert.Single((await reader.GetBacklinksAsync(new ArchivePostId(targetId, "530"))).Items);
        Assert.Equal(new ArchivePostId(Id, "123"), backlink.Origin);
    }

    [Theory]
    [InlineData("8kun", "https://8kun.top/test/res/123.html", "456.html#789", "test", "456", "789")]
    [InlineData("4chan", "https://boards.4chan.org/g/thread/123", "https://boards.4channel.org/x/thread/456#p789", "x", "456", "789")]
    [InlineData("custom", "https://custom.test/opaque", "#opaque-post", "g", "123", "opaque-post")]
    public void QuoteParserUnderstandsSupportedAliasesRelativePathsAndOpaqueSameThreadFragments(string source, string url, string href,
        string board, string thread, string post)
    {
        ResourceId origin = new ResourceId(source, source == "8kun" ? "test" : "g", "123");
        ArchiveQuote quote = Assert.Single(ArchiveQuoteParser.Parse(origin, url, Post("123", 0, $"<a href='{href}'>&gt;&gt;ref</a>")));
        Assert.Equal(new ArchivePostId(new ResourceId(source, board, thread), post), quote.Target);
        Assert.False(quote.IsExternal);
    }

    [Fact]
    public void PlainTextHandlesQuotedTagDelimitersLargeBodiesAndUnclosedActiveElements()
    {
        Assert.Equal("2 < 3 & text\nend", ArchiveText.PlainText("2 < 3 &amp; <span title='a>b'>text</span><br>end"));
        Assert.Equal("visible", ArchiveText.PlainText("visible<script><a href='#p1'>hidden"));
        Assert.Equal("<script>literal</script>", ArchiveText.PlainText("&lt;script&gt;literal&lt;/script&gt;"));
        Assert.Equal(1_000_000, ArchiveText.PlainText("<p>" + new string('x', 1_000_000) + "</p>").Length);
    }
}
