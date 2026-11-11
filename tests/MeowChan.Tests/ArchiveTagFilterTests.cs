using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests;

public sealed class ArchiveTagFilterTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = new("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void FilterIsCanonicalAndRejectsEmptyOverlappingAndOversizedInput()
    {
        ArchiveTagFilter filter = new([B, A, B], [C], ArchiveTagMatch.Any);
        Assert.Equal(new[] { A, B }, filter.Include);
        Assert.Equal(new[] { C }, filter.Exclude);
        Assert.Equal(ArchiveTagMatch.Any, filter.Match);
        Assert.Equal(filter, new ArchiveTagFilter([A, B], [C], ArchiveTagMatch.Any));
        Assert.NotEqual(filter, new ArchiveTagFilter([A, B], [C]));
        // One included tag means the same in both modes: one canonical spelling.
        Assert.Equal(new ArchiveTagFilter([A], null), new ArchiveTagFilter([A], null, ArchiveTagMatch.Any));
        Assert.Equal(ArchiveTagMatch.All, new ArchiveTagFilter(null, [A], ArchiveTagMatch.Any).Match);

        Assert.Null(ArchiveTagFilter.OrNull(null, []));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(null, null));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter([A], [A]));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter([Guid.Empty], null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveTagFilter([A], null, (ArchiveTagMatch)7));
        Guid[] many = Enumerable.Range(1, ArchiveTagFilter.MaximumTagsPerList + 1).Select(index => new Guid(index, 0, 0, new byte[8])).ToArray();
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(many, null));
        Assert.Equal(ArchiveTagFilter.MaximumTagsPerList, new ArchiveTagFilter(many[..^1], null).Include.Count);
    }

    [Fact]
    public void DefinitionsWithoutTagsKeepSchemaOneByteForByteAndTagsMakeSchemaTwo()
    {
        ArchiveSearchSpec plain = new("needle", ["s"], [new("s", "b")], [ThreadSourceState.Archived], true, false,
            ArchiveThreadSort.Title, ArchiveWorkFilter.Completed, new(new(2026, 1, 2), null, "UTC"));
        string before = ArchiveSearchSpecJson.Serialize(plain);
        Assert.Equal(1, plain.SchemaVersion);
        Assert.Null(plain.Tags);
        Assert.Same(plain, plain.WithTags(null));
        Assert.DoesNotContain("tags", before, StringComparison.Ordinal);

        ArchiveSearchSpec tagged = plain.WithTags(new ArchiveTagFilter([B, A], [C], ArchiveTagMatch.Any));
        Assert.Equal(2, tagged.SchemaVersion);
        Assert.NotEqual(plain, tagged);
        Assert.Equal(tagged, plain.WithTags(new ArchiveTagFilter([A, B], [C], ArchiveTagMatch.Any)));
        Assert.Equal(tagged.GetHashCode(), plain.WithTags(new ArchiveTagFilter([A, B], [C], ArchiveTagMatch.Any)).GetHashCode());
        // Removing the filter restores exactly the schema 1 definition and bytes.
        Assert.Equal(plain, tagged.WithTags(null));
        Assert.Equal(before, ArchiveSearchSpecJson.Serialize(tagged.WithTags(null)));

        string json = ArchiveSearchSpecJson.Serialize(tagged);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement tags = document.RootElement.GetProperty("tags");
        Assert.Equal("any", tags.GetProperty("match").GetString());
        Assert.Equal(new[] { A.ToString("D"), B.ToString("D") }, tags.GetProperty("include").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { C.ToString("D") }, tags.GetProperty("exclude").EnumerateArray().Select(item => item.GetString()));
        ArchiveSearchSpec roundTrip = ArchiveSearchSpecJson.Deserialize(json);
        Assert.Equal(tagged, roundTrip);
        Assert.Equal(json, ArchiveSearchSpecJson.Serialize(roundTrip));
    }

    [Theory]
    // Schema 1 must not carry tags; schema 2 must carry a complete, non-empty, canonical filter.
    [InlineData("{\"schemaVersion\":1,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[\"00000000-0000-0000-0000-00000000000a\"],\"exclude\":[]}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":null}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[],\"exclude\":[]}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"some\",\"include\":[\"00000000-0000-0000-0000-00000000000a\"],\"exclude\":[]}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[\"0000000000000000000000000000000A\"],\"exclude\":[]}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[\"00000000-0000-0000-0000-00000000000a\"],\"exclude\":[\"00000000-0000-0000-0000-00000000000a\"]}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[\"00000000-0000-0000-0000-00000000000a\"],\"exclude\":[],\"extra\":1}}")]
    [InlineData("{\"schemaVersion\":2,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null,\"tags\":{\"match\":\"all\",\"include\":[\"00000000-0000-0000-0000-000000000000\"],\"exclude\":[]}}")]
    [InlineData("{\"schemaVersion\":3,\"text\":\"\",\"sources\":[],\"boards\":[],\"sourceStates\":[],\"captureEnabled\":null,\"hasPosts\":null,\"sort\":\"latest-capture\",\"workFilter\":\"all\",\"dateRange\":null}")]
    public void MalformedOrMismatchedTagDefinitionsAreRejected(string json) =>
        Assert.ThrowsAny<Exception>(() => ArchiveSearchSpecJson.Deserialize(json));

    private static ResourceId Thread(string id) => new("source", "board", id);

    private static PrivateTagStoreSnapshot Tags(TempDirectory temp, out TagId a, out TagId b, out TagId c)
    {
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        a = store.Define("a").Tag; b = store.Define("b").Tag; c = store.Define("c").Tag;
        store.SetThread(a, Thread("1"), true); store.SetThread(b, Thread("1"), true);
        store.SetThread(a, Thread("2"), true);
        store.SetThread(b, Thread("3"), true); store.SetThread(c, Thread("3"), true);
        // Thread 4 carries tag a only on one of its media versions; that must never make the thread match.
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread("4"), "p", 0, "att-v2:" + new string('a', 64)),
            "m", "original", TransferMediaRole.Original, new string('c', 64), out PrivateMediaTagTarget? media, out _));
        return store.SetMedia(a, media, true).Snapshot;
    }

    [Fact]
    public void MembershipFollowsAllAnyAndExcludeAndIgnoresMediaTags()
    {
        using TempDirectory temp = new();
        PrivateTagStoreSnapshot tags = Tags(temp, out TagId a, out TagId b, out TagId c);
        string[] Matching(ArchiveTagFilter filter)
        {
            Assert.True(ThreadTagMembership.TryCreate(filter, tags, out ThreadTagMembership? membership));
            Assert.Equal(tags.Revision, membership!.Revision);
            return new[] { "1", "2", "3", "4", "5" }.Where(id => membership.Matches(Thread(id))).ToArray();
        }

        Assert.Equal(new[] { "1", "2" }, Matching(new([a.Value], null)));
        Assert.Equal(new[] { "1" }, Matching(new([a.Value, b.Value], null)));
        Assert.Equal(new[] { "1", "2", "3" }, Matching(new([a.Value, b.Value], null, ArchiveTagMatch.Any)));
        Assert.Equal(new[] { "1", "2", "4", "5" }, Matching(new(null, [c.Value])));
        Assert.Equal(new[] { "2" }, Matching(new([a.Value], [b.Value])));
        Assert.Equal(new[] { "1", "2" }, Matching(new([a.Value, b.Value], [c.Value], ArchiveTagMatch.Any)));
        // Another source with the same thread number is a different thread.
        Assert.True(ThreadTagMembership.TryCreate(new([a.Value], null), tags, out ThreadTagMembership? exact));
        Assert.False(exact!.Matches(new ResourceId("other", "board", "1")));

        Assert.False(ThreadTagMembership.TryCreate(new([a.Value, Guid.NewGuid()], null), tags, out ThreadTagMembership? unknown));
        Assert.Null(unknown);
        Assert.False(ThreadTagMembership.TryCreate(new(null, [Guid.NewGuid()]), tags, out _));
    }

    [Fact]
    public void RequestsBindTagsOrFailClosedAndRefreshOnlyWhenTagsChanged()
    {
        using TempDirectory temp = new();
        PrivateTagStoreSnapshot tags = Tags(temp, out TagId a, out _, out _);
        ArchiveSearchSpec plain = new();
        ArchiveSearchSpec tagged = plain.WithTags(new ArchiveTagFilter([a.Value], null));

        Assert.Null(ArchiveSearchRequest.Create(plain).TagMembership);
        Assert.Null(ArchiveSearchRequest.Create(plain, tags).TagMembership);
        // The public factory has no tag index: a tag filter is never dropped, the request is refused.
        Assert.Equal(ArchiveTagFilterError.TagsUnavailable, Assert.Throws<ArchiveTagFilterException>(() => ArchiveSearchRequest.Create(tagged)).Error);
        Assert.Equal(ArchiveTagFilterError.UnknownTag, Assert.Throws<ArchiveTagFilterException>(
            () => ArchiveSearchRequest.Create(plain.WithTags(new ArchiveTagFilter([Guid.NewGuid()], null)), tags)).Error);

        ArchiveSearchRequest request = ArchiveSearchRequest.Create(tagged, tags);
        Assert.True(request.TagMembership!.Matches(Thread("1")));
        Assert.Same(request, request.WithTagSnapshot(tags));
        ArchiveSearchRequest untagged = ArchiveSearchRequest.Create(plain);
        Assert.Same(untagged, untagged.WithTagSnapshot(null));
        Assert.Throws<ArchiveTagFilterException>(() => request.WithTagSnapshot(null));

        PrivateTagStoreSnapshot newer;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
            newer = new PrivateTagStore(temp.Path, lease).SetThread(a, Thread("5"), true).Snapshot;
        ArchiveSearchRequest refreshed = request.WithTagSnapshot(newer);
        Assert.NotSame(request, refreshed);
        Assert.Same(request.Spec, refreshed.Spec);
        Assert.Same(request.DateWindow, refreshed.DateWindow);
        Assert.True(refreshed.TagMembership!.Matches(Thread("5")));
        Assert.False(request.TagMembership.Matches(Thread("5")));
    }

    [Fact]
    public async Task ReaderAppliesTheFrozenMembershipToCountPagesIdsAndSummariesAlike()
    {
        using TempDirectory temp = new();
        PrivateTagStoreSnapshot tags = Tags(temp, out TagId a, out TagId b, out _);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            PrivateTagStore store = new(temp.Path, lease);
            for (int index = 1; index <= 9; index++)
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(Thread(index.ToString()), "https://fixture.invalid/" + index,
                    index % 2 == 0 ? "even needle" : "odd", ThreadSourceState.Archived,
                    new DateTimeOffset(2026, 10, index, 0, 0, 0, TimeSpan.Zero), temp.Path,
                    [new CapturedPost("p" + index, 0, PostFieldPresence.All, Html: "post")]));
                if (index >= 6) store.SetThread(a, Thread(index.ToString()), true);
            }
            tags = store.ReadSnapshot();
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        ArchiveSearchSpec[] specs =
        [
            new ArchiveSearchSpec(sort: ArchiveThreadSort.OldestFirst).WithTags(new ArchiveTagFilter([a.Value], null)),
            new ArchiveSearchSpec(sort: ArchiveThreadSort.Title).WithTags(new ArchiveTagFilter([a.Value, b.Value], null, ArchiveTagMatch.Any)),
            new ArchiveSearchSpec("needle").WithTags(new ArchiveTagFilter([a.Value], [b.Value])),
            new ArchiveSearchSpec().WithTags(new ArchiveTagFilter(null, [a.Value]))
        ];
        string[][] expected = [["1", "2", "6", "7", "8", "9"], ["1", "2", "3", "6", "7", "8", "9"], ["2", "6", "8"], ["3", "4", "5"]];
        for (int index = 0; index < specs.Length; index++)
        {
            ArchiveSearchRequest request = ArchiveSearchRequest.Create(specs[index], tags);
            IReadOnlyList<ResourceId> ids = await reader.SelectThreadIdsBySpecAsync(request);
            IReadOnlyList<ArchiveThreadSummary> summaries = await reader.SelectThreadSummariesBySpecAsync(request);
            Assert.Equal(expected[index], ids.Select(id => id.Thread!).Order());
            Assert.Equal(ids, summaries.Select(summary => summary.Id));
            List<ResourceId> paged = [];
            for (int offset = 0; ; offset += 2)
            {
                ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset, 2);
                Assert.Equal(ids.Count, page.TotalCount);
                Assert.Equal(offset, page.Offset);
                if (page.Items.Count == 0) break;
                Assert.True(page.Items.Count <= 2);
                paged.AddRange(page.Items.Select(item => item.Id));
            }
            // Pages are exactly the ordered full result: same count, same order, no gaps or repeats.
            Assert.Equal(ids, paged);
        }
        // A definition without tags is untouched by the presence of a tag index.
        Assert.Equal(9, (await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new ArchiveSearchSpec(), tags))).TotalCount);
    }
}
