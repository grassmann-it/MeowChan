using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveOrdinalTests
{
    private static readonly ResourceId Id = new("ordinal-source", "b", "thread");
    private const long FirstOrdinal = (long)int.MaxValue + 1;
    private const long MiddleOrdinal = FirstOrdinal + 1;
    private const string Url = "https://ordinal.test/b/thread/thread";
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LargeOrdinalsSurviveSqliteJournalRestartMergeQuoteAndOfflineExport()
    {
        using TempDirectory profile = new();
        string media = Path.Combine(profile.Path, "media-not-created");
        CapturedThreadSnapshot first = Snapshot(media,
            new("first", FirstOrdinal, PostFieldPresence.Html | PostFieldPresence.Author,
                Html: "first-post-marker", Author: "retained author"),
            new("legacy", 0, PostFieldPresence.Html, Html: "legacy-post-marker"));
        CapturedThreadSnapshot final = Snapshot(media,
            new("last", long.MaxValue, PostFieldPresence.Html, Html: "last-post-marker <a href=\"#first\">&gt;&gt;first</a>"),
            new("middle", MiddleOrdinal, PostFieldPresence.Html, Html: "middle-post-marker"),
            new("first", FirstOrdinal, PostFieldPresence.Html, Html: "first-post-marker updated"))
            with { SourceState = ThreadSourceState.Archived, CapturedAt = ObservedAt.AddMinutes(1) };
        CapturedThreadSnapshot merged = ArchiveCaptureMerge.Merge(first, final);
        AssertOrder(merged.Posts);
        Assert.Equal("retained author", merged.Posts.Single(post => post.PostId == "first").Author);

        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            await catalog.CaptureAsync(first);
            WorkStateStore journal = new(profile.Path, lease);
            journal.CommitIntent(new WorkResource
            {
                Url = Url, OutputRoot = media, Generation = 7, Intent = WorkIntent.Watching,
                IsThread = true, Source = ThreadSourceStateValue.Active
            });
            Assert.NotNull(journal.CommitEnd(Url, 7, ThreadSourceStateValue.Archived,
                ObservedAt.AddMinutes(1), JsonSerializer.Serialize(final)));
        }
        // A new profile writer resumes a retained final capture. No source or network client exists.
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            WorkResource retained = Assert.Single(new WorkStateStore(profile.Path, lease).LoadResources());
            CapturedThreadSnapshot restored = JsonSerializer.Deserialize<CapturedThreadSnapshot>(retained.FinalSnapshotJson!)!;
            Assert.Equal(long.MaxValue, restored.Posts.Single(post => post.PostId == "last").Ordinal);
            Assert.Equal(FirstOrdinal, restored.Posts.Single(post => post.PostId == "first").Ordinal);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            await catalog.CaptureAsync(restored);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView view = (await reader.ReadThreadAsync(Id))!;
        AssertOrder(view.GetPosts());
        Assert.Equal("retained author", view.GetPosts().Single(post => post.PostId == "first").Author);
        Assert.Equal(2, view.FindPostIndex("middle"));
        Assert.Equal("middle", Assert.Single(view.GetPosts(offset: 2, limit: 1)).PostId);
        ArchiveQuote quote = Assert.Single((await reader.GetQuotesAsync(new(Id, "last"))).Items);
        ArchiveQuoteResolution resolved = await reader.ResolveQuoteAsync(quote);
        Assert.Equal(ArchiveQuoteState.Captured, resolved.State);
        Assert.Equal(FirstOrdinal, resolved.Post!.Ordinal);
        Assert.Equal(1, resolved.PostIndex);

        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id,
            Path.Combine(profile.Path, "offline-export"), new(false));
        string html = await File.ReadAllTextAsync(export.IndexPath);
        Assert.True(html.IndexOf("legacy-post-marker", StringComparison.Ordinal) < html.IndexOf("first-post-marker", StringComparison.Ordinal));
        Assert.True(html.IndexOf("first-post-marker", StringComparison.Ordinal) < html.IndexOf("middle-post-marker", StringComparison.Ordinal));
        Assert.True(html.IndexOf("middle-post-marker", StringComparison.Ordinal) < html.IndexOf("last-post-marker", StringComparison.Ordinal));
        Assert.Equal(4, export.PostCount);
        Assert.False(Directory.Exists(media));

        using SqliteConnection sql = new(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(profile.Path, "archive.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        sql.Open();
        using SqliteCommand command = sql.CreateCommand();
        command.CommandText = "SELECT ordinal,typeof(ordinal) FROM archive_posts WHERE post='last'";
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read()); Assert.Equal(long.MaxValue, row.GetInt64(0)); Assert.Equal("integer", row.GetString(1));
    }

    [Fact]
    public async Task ExistingInt32JsonAndSqliteValuesRemainReadable()
    {
        using TempDirectory profile = new();
        // Shape written by the old record: no new fields, and Ordinal encoded from Int32.
        string legacyJson = JsonSerializer.Serialize(new
        {
            Id, Url, Title = "legacy", SourceState = ThreadSourceState.Archived, CapturedAt = ObservedAt,
            MediaDirectory = Path.Combine(profile.Path, "media"),
            Posts = new[] { new { PostId = "old", Ordinal = int.MaxValue, Fields = PostFieldPresence.Html, Html = "legacy-json" } }
        });
        CapturedThreadSnapshot legacy = JsonSerializer.Deserialize<CapturedThreadSnapshot>(legacyJson)!;
        Assert.Equal((long)int.MaxValue, Assert.Single(legacy.Posts).Ordinal);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            await catalog.CaptureAsync(legacy);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        CapturedPost persisted = Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts());
        Assert.Equal((long)int.MaxValue, persisted.Ordinal); Assert.Equal("legacy-json", persisted.Html);
    }

    [Theory]
    [InlineData("4chan")][InlineData("8kun")][InlineData("lainchan")][InlineData("kohlchan")]
    public async Task ExplicitDecimalIdentityOrderStillTakesPrecedenceOverWideOrdinals(string source)
    {
        using TempDirectory profile = new();
        ResourceId id = Id with { Source = source };
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            await catalog.CaptureAsync(Snapshot(profile.Path,
                new("10", FirstOrdinal, PostFieldPresence.Html, Html: "ten"),
                new("9", long.MaxValue, PostFieldPresence.Html, Html: "nine")) with { Id = id, DecimalPostOrder = true });
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        Assert.Equal(new[] { "9", "10" }, (await reader.ReadThreadAsync(id))!.GetPosts().Select(post => post.PostId));
    }

    [Fact]
    public async Task NegativeInt64OrdinalIsRejectedWithoutReplacingLastCapture()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Snapshot(profile.Path, new CapturedPost("valid", long.MaxValue, PostFieldPresence.Html, Html: "retained")));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.CaptureAsync(Snapshot(profile.Path,
            new CapturedPost("bad", long.MinValue, PostFieldPresence.Html, Html: "invalid"))));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        Assert.Equal("valid", Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).PostId);
    }

    private static CapturedThreadSnapshot Snapshot(string media, params CapturedPost[] posts) =>
        new(Id, Url, "wide ordinal fixture", ThreadSourceState.Active, ObservedAt, media, posts) { DecimalPostOrder = false };
    private static void AssertOrder(IReadOnlyList<CapturedPost> posts)
    {
        Assert.Equal(new[] { "legacy", "first", "middle", "last" }, posts.Select(post => post.PostId));
        Assert.Equal(new long[] { 0, FirstOrdinal, MiddleOrdinal, long.MaxValue }, posts.Select(post => post.Ordinal));
    }
}
