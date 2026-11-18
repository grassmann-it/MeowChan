using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>Relevance order: title hits, then opening-post hits, then threads that match only in other posts; plus the text scope.</summary>
public sealed class ArchiveSearchRelevanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CapturedPost Post(string id, string html, string? subject = null) =>
        new(id, long.Parse(id), PostFieldPresence.All, Subject: subject, Html: html);

    private static CapturedThreadSnapshot Thread(string directory, string number, string title, int minutes, params CapturedPost[] posts) =>
        new(new ResourceId("4chan", "g", number), $"https://boards.4chan.org/g/thread/{number}", title,
            ThreadSourceState.Archived, Start.AddMinutes(minutes), directory, posts);

    /// <summary>
    /// The term occurs in the title of 100, in the opening post of 200 (body) and 210 (subject), only in a reply of 300 and
    /// nowhere in 400. Later captures are newer, so the plain latest-capture order would be the reverse of the tiers.
    /// </summary>
    private static async Task SeedAsync(ArchiveCatalog catalog, string directory, string term)
    {
        await catalog.CaptureAsync(Thread(directory, "100", "about " + term + " here", 0, Post("100", "plain opening"), Post("101", "plain reply")));
        await catalog.CaptureAsync(Thread(directory, "200", "second", 10, Post("200", "the " + term + " opening"), Post("201", "plain reply")));
        await catalog.CaptureAsync(Thread(directory, "210", "third", 20, Post("210", "plain opening", subject: term), Post("211", "plain reply")));
        await catalog.CaptureAsync(Thread(directory, "300", "fourth", 30, Post("300", "plain opening"), Post("301", "a " + term + " reply")));
        await catalog.CaptureAsync(Thread(directory, "400", "fifth", 40, Post("400", "plain opening"), Post("401", "plain reply")));
    }

    private static ArchiveSearchRequest Request(string text, ArchiveThreadSort sort = ArchiveThreadSort.Relevance,
        ArchiveSearchScope scope = ArchiveSearchScope.AllPosts) =>
        ArchiveSearchRequest.Create(new ArchiveSearchSpec(text, sort: sort).WithScope(scope));

    private static string[] Numbers(ArchiveThreadPage page) => page.Items.Select(item => item.Id.Thread).ToArray();

    [Theory]
    [InlineData("needle")] // FTS trigram path
    [InlineData("zq")]     // 1–2 character literal fallback
    public async Task RelevanceOrdersByTitleThenOpeningPostThenOtherPosts(string term)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) await SeedAsync(catalog, directory.Path, term);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);

        ArchiveThreadPage ranked = await reader.SearchThreadsBySpecAsync(Request(term));
        // Inside a tier the newest capture comes first (210 after 200 would be wrong: 210 is newer).
        Assert.Equal(["100", "210", "200", "300"], Numbers(ranked));
        Assert.Equal([ArchiveSearchMatchTier.Title, ArchiveSearchMatchTier.OpeningPost, ArchiveSearchMatchTier.OpeningPost,
            ArchiveSearchMatchTier.OtherPosts], ranked.MatchTiers);
        Assert.Equal([1, 2, 1], ranked.MatchTierCounts);
        Assert.Equal(4, ranked.TotalCount);

        // Every other order is the plain order of today, without tiers.
        ArchiveThreadPage latest = await reader.SearchThreadsBySpecAsync(Request(term, ArchiveThreadSort.LatestCapture));
        Assert.Equal(["300", "210", "200", "100"], Numbers(latest));
        Assert.Null(latest.MatchTiers);
        Assert.Null(latest.MatchTierCounts);

        // Relevance without a text has no tiers and is the latest-capture order.
        ArchiveThreadPage all = await reader.SearchThreadsBySpecAsync(Request(""));
        Assert.Equal(["400", "300", "210", "200", "100"], Numbers(all));
        Assert.Null(all.MatchTiers);
    }

    [Theory]
    [InlineData("needle")]
    [InlineData("zq")]
    public async Task ScopeCutsOffWhereTheTextMayOccur(string term)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) await SeedAsync(catalog, directory.Path, term);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);

        Assert.Equal(["100"], Numbers(await reader.SearchThreadsBySpecAsync(Request(term, scope: ArchiveSearchScope.Title))));
        ArchiveThreadPage opening = await reader.SearchThreadsBySpecAsync(Request(term, scope: ArchiveSearchScope.TitleAndOpeningPost));
        Assert.Equal(["100", "210", "200"], Numbers(opening));
        Assert.Equal([1, 2, 0], opening.MatchTierCounts);
        // The scope applies under every order, and IDs, summaries and pages agree.
        ArchiveSearchRequest latest = Request(term, ArchiveThreadSort.LatestCapture, ArchiveSearchScope.TitleAndOpeningPost);
        Assert.Equal(["210", "200", "100"], Numbers(await reader.SearchThreadsBySpecAsync(latest)));
        Assert.Equal(["210", "200", "100"], (await reader.SelectThreadIdsBySpecAsync(latest)).Select(id => id.Thread));
        Assert.Equal(4, (await reader.SearchThreadsBySpecAsync(Request(term))).TotalCount);
        // Without a text the scope narrows nothing.
        Assert.Equal(5, (await reader.SearchThreadsBySpecAsync(Request("", scope: ArchiveSearchScope.Title))).TotalCount);
    }

    [Fact]
    public async Task ATitleHitWinsOverAnOpeningPostHitAndTheOpeningPostIsTheFirstRetainedPost()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease))
        {
            await catalog.CaptureAsync(Thread(directory.Path, "500", "needle title", 0, Post("500", "needle opening"), Post("501", "needle reply")));
            // The real opening post 600 was never captured: the first retained post 601 is the opening post here.
            await catalog.CaptureAsync(Thread(directory.Path, "600", "other", 5, Post("601", "needle first kept"), Post("602", "plain")));
            await catalog.CaptureAsync(Thread(directory.Path, "700", "other", 10, Post("700", "plain"), Post("701", "plain"), Post("702", "needle late")));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(Request("needle"));
        Assert.Equal(["500", "600", "700"], Numbers(page));
        Assert.Equal([ArchiveSearchMatchTier.Title, ArchiveSearchMatchTier.OpeningPost, ArchiveSearchMatchTier.OtherPosts], page.MatchTiers);
    }

    [Fact]
    public async Task FrozenPagesStayConsistentAcrossTierBoundaries()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease))
        {
            // Equal capture times inside the tiers: the identity tie-break keeps the order total.
            for (int i = 0; i < 7; i++)
            {
                string number = (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string reply = (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await catalog.CaptureAsync((i % 3) switch
                {
                    0 => Thread(directory.Path, number, "needle", i / 2, Post(number, "plain"), Post(reply, "plain")),
                    1 => Thread(directory.Path, number, "other", i / 2, Post(number, "needle"), Post(reply, "plain")),
                    _ => Thread(directory.Path, number, "other", i / 2, Post(number, "plain"), Post(reply, "needle"))
                });
            }
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveSearchRequest request = Request("needle");
        ArchiveThreadPage whole = await reader.SearchThreadsBySpecAsync(request);
        List<string> paged = new();
        List<ArchiveSearchMatchTier> tiers = new();
        for (int offset = 0; offset < whole.TotalCount; offset += 2)
        {
            ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset, 2);
            paged.AddRange(Numbers(page));
            tiers.AddRange(page.MatchTiers!);
            Assert.Equal(whole.MatchTierCounts, page.MatchTierCounts);
        }
        Assert.Equal(Numbers(whole), paged);
        Assert.Equal(whole.MatchTiers, tiers);
        Assert.Equal(tiers.Order(), tiers); // tiers never interleave
        Assert.Equal(Numbers(whole), (await reader.SelectThreadIdsBySpecAsync(request)).Select(id => id.Thread));
        Assert.Equal(Numbers(whole), (await reader.SelectThreadSummariesBySpecAsync(request)).Select(item => item.Id.Thread));
        IReadOnlyList<ArchiveRankedThread> ranked = await reader.SelectRankedThreadSummariesBySpecAsync(request);
        Assert.Equal(Numbers(whole), ranked.Select(item => item.Summary.Id.Thread));
        Assert.Equal(whole.MatchTiers, ranked.Select(item => item.Tier!.Value));
        ArchiveThreadPage rebuilt = ArchiveThreadPage.FromRanked(ranked, 2, 3, tiered: true);
        Assert.Equal(Numbers(whole).Skip(2).Take(3), Numbers(rebuilt));
        Assert.Equal(whole.MatchTierCounts, rebuilt.MatchTierCounts);
        // The summaries carry no tier, so they stay equal to fresh single-thread reads (deletion and cleanup compare them).
        Assert.Equal(whole.Items[0], (await reader.SearchThreadsBySpecAsync(Request("needle", ArchiveThreadSort.Title))).Items
            .Single(item => item.Id == whole.Items[0].Id));
    }

    [Fact]
    public void ScopeAndRelevanceUseSchemaThreeAndOlderDefinitionsLoadAsAllPosts()
    {
        ArchiveSearchSpec plain = new("needle", sort: ArchiveThreadSort.Title);
        string plainJson = ArchiveSearchSpecJson.Serialize(plain);
        Assert.Equal(1, plain.SchemaVersion);
        Assert.DoesNotContain("scope", plainJson);
        // A schema 1 and a schema 2 definition of an earlier version load with every post searched.
        ArchiveSearchSpec old = ArchiveSearchSpecJson.Deserialize(plainJson);
        Assert.Equal(ArchiveSearchScope.AllPosts, old.Scope);
        Assert.Equal(plain, old);
        ArchiveTagFilter tags = ArchiveTagFilter.OrNull([Guid.Parse("8f5c7c3e-49c7-4bd0-9e3c-3b8d2f6c1a01")], [], ArchiveTagMatch.All)!;
        string tagged = ArchiveSearchSpecJson.Serialize(plain.WithTags(tags));
        Assert.Contains("\"schemaVersion\":2", tagged);
        Assert.Equal(ArchiveSearchScope.AllPosts, ArchiveSearchSpecJson.Deserialize(tagged).Scope);

        foreach (ArchiveSearchSpec spec in new[]
        {
            new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Relevance),
            plain.WithScope(ArchiveSearchScope.Title),
            new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Relevance).WithScope(ArchiveSearchScope.TitleAndOpeningPost).WithTags(tags)
        })
        {
            string json = ArchiveSearchSpecJson.Serialize(spec);
            Assert.Equal(3, spec.SchemaVersion);
            Assert.Contains("\"schemaVersion\":3", json);
            ArchiveSearchSpec read = ArchiveSearchSpecJson.Deserialize(json);
            Assert.Equal(spec, read);
            Assert.Equal(json, ArchiveSearchSpecJson.Serialize(read));
        }
        string three = ArchiveSearchSpecJson.Serialize(plain.WithScope(ArchiveSearchScope.TitleAndOpeningPost));
        Assert.Contains("\"scope\":\"title-and-opening-post\",\"tags\":null", three);
        // Removing the new content returns to the old schema and bytes.
        Assert.Equal(plainJson, ArchiveSearchSpecJson.Serialize(plain.WithScope(ArchiveSearchScope.Title).WithScope(ArchiveSearchScope.AllPosts)));
        // Strict contract: relevance needs schema 3, a scope is mandatory there, unknown values are refused.
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(plainJson.Replace("\"sort\":\"title\"", "\"sort\":\"relevance\"")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(three.Replace(",\"scope\":\"title-and-opening-post\"", "")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(three.Replace("title-and-opening-post", "everywhere")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(three.Replace(",\"tags\":null", "")));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.WithScope((ArchiveSearchScope)9));
        Assert.NotEqual(plain, plain.WithScope(ArchiveSearchScope.Title));
    }
}
