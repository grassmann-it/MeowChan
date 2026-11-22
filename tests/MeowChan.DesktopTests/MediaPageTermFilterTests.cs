using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// MP1/MP4: the page-side terms of the media filter: membership in the tagging queue ("queue:yes/no") and the provider's source tags
// ("booru:name"). Their place in the term model (exclusion, chips' order, the core filter skips them), the query syntax and the filter
// the page applies after the core filter, with its refusals. Pure: no controls.
[Collection("Localization")]
public sealed class MediaPageTermFilterTests : IDisposable
{
    public MediaPageTermFilterTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaLibraryEntry Entry(int index, string? booruPost = null) => MediaTagCountsTests.Entry(index, booruPost);

    [Fact]
    public void QueueAndSourceTagTermsExcludeAndTheCoreFilterLeavesThemToThePage()
    {
        MediaFilterTerm queued = MediaFilterTerm.InQueue(), notQueued = MediaFilterTerm.InQueue(inQueue: false);
        Assert.Equal((MediaFilterField.Queue, false), (queued.Field, queued.Exclude));
        Assert.Equal(notQueued, queued.Toggled());
        Assert.Equal("blue_eyes", MediaFilterTerm.SourceTag("  Blue_Eyes ").Value);
        Assert.Throws<ArgumentException>(() => MediaFilterTerm.SourceTag("two words"));
        Assert.Throws<ArgumentException>(() => MediaFilterTerm.SourceTag(""));
        Assert.True(MediaFilterTerms.ToFilter([queued, MediaFilterTerm.SourceTag("solo", exclude: true)]).IsEmpty);
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.SourceTag, "a b")]));
        Assert.True(MediaFilterTerms.IsPageTerm(queued) && MediaFilterTerms.IsPageTerm(MediaFilterTerm.SourceTag("solo")));
        Assert.False(MediaFilterTerms.IsPageTerm(MediaFilterTerm.Flag(MediaFilterField.Favorite)));
        // One queue term at a time: the other polarity replaces it; source tags add up like own tags.
        IReadOnlyList<MediaFilterTerm> set = MediaFilterTermSet.With([queued], notQueued);
        Assert.Equal([notQueued], set);
        set = MediaFilterTermSet.With(MediaFilterTermSet.With(set, MediaFilterTerm.SourceTag("solo")), MediaFilterTerm.SourceTag("comic", exclude: true));
        Assert.Equal(3, set.Count);
        // Chips: source tags right after the own tags, the queue with the favourites.
        Assert.Equal([MediaFilterField.Tag, MediaFilterField.SourceTag, MediaFilterField.SourceTag, MediaFilterField.Favorite, MediaFilterField.Queue],
            MediaFilterTermSet.Ordered([notQueued, MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.SourceTag("solo"), MediaFilterTerm.Tag(Guid.NewGuid()),
                MediaFilterTerm.SourceTag("comic", exclude: true)]).Select(term => term.Field));
    }

    [Fact]
    public void TheQueryReadsAndWritesTheQueueAndSourceTags()
    {
        MediaFilterQueryResult parsed = MediaFilterQuery.Parse("queue booru:Cat_Ears -booru:comic fox");
        Assert.Equal([MediaFilterTerm.InQueue(), MediaFilterTerm.SourceTag("cat_ears"), MediaFilterTerm.SourceTag("comic", exclude: true)], parsed.Terms);
        Assert.Equal("fox", parsed.FreeText);
        Assert.Equal([MediaFilterTerm.InQueue(false)], MediaFilterQuery.Parse("-queue").Terms);
        Assert.Equal([MediaFilterTerm.InQueue(false)], MediaFilterQuery.Parse("queue:no").Terms);
        Assert.Equal([MediaFilterTerm.InQueue(true)], MediaFilterQuery.Parse("-queue:no").Terms);
        Assert.Equal([MediaFilterTerm.InQueue(true)], MediaFilterQuery.Parse("QUEUE:yes").Terms);
        Assert.Equal(MediaFilterQueryProblem.UnknownValue, MediaFilterQuery.Parse("queue:maybe").Problems.Single().Problem);
        Assert.Equal(MediaFilterQueryProblem.NeedsValue, MediaFilterQuery.Parse("booru:").Problems.Single().Problem);
        MediaFilterTerm[] terms = [MediaFilterTerm.SourceTag("cat_ears"), MediaFilterTerm.SourceTag("comic", exclude: true), MediaFilterTerm.InQueue(false)];
        string text = MediaFilterQuery.Serialize(terms);
        Assert.Equal("booru:cat_ears -booru:comic queue:no", text);
        Assert.Equal(terms, MediaFilterQuery.Parse(text).Terms);
    }

    [Fact]
    public void TheQueueTermKeepsQueuedOrUnqueuedMediaByTheKeyTheQueueWrites()
    {
        MediaLibraryEntry a = Entry(1), b = Entry(2), c = Entry(3);
        TagQueueItem item = TagQueueItem.From(a, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(item.Key, TagQueueKeys.Of(a));
        IReadOnlySet<TagQueueKey> queue = new HashSet<TagQueueKey> { item.Key };
        Assert.Equal([a], MediaPageTermFilter.From([MediaFilterTerm.InQueue()])!.Apply([a, b, c], queue, null));
        Assert.Equal([b, c], MediaPageTermFilter.From([MediaFilterTerm.InQueue(false)])!.Apply([a, b, c], queue, null));
        // An unreadable queue refuses the filter instead of guessing.
        MediaPageTermException refused = Assert.Throws<MediaPageTermException>(() => MediaPageTermFilter.From([MediaFilterTerm.InQueue()])!.Apply([a], null, null));
        Assert.Equal("media.filter.error.queue", refused.TextKey);
        Assert.Null(MediaPageTermFilter.From([MediaFilterTerm.Flag(MediaFilterField.Favorite)]));
    }

    [Fact]
    public void SourceTagsNarrowLikeABooruSearchAndAnUnreadableCatalogRefuses()
    {
        MediaLibraryEntry chan = Entry(1), cat = Entry(2, "10"), catSolo = Entry(3, "11"), dog = Entry(4, "12");
        MediaSourceTagIndex tags = MediaSourceTagIndex.Of(new Dictionary<string, string[]>
        {
            ["10"] = ["cat_ears"], ["11"] = ["Cat_Ears", "solo"], ["12"] = ["dog"]
        });
        MediaLibraryEntry[] all = [chan, cat, catSolo, dog];
        Assert.Equal([cat, catSolo], MediaPageTermFilter.From([MediaFilterTerm.SourceTag("cat_ears")])!.Apply(all, null, tags));
        Assert.Equal([catSolo], MediaPageTermFilter.From([MediaFilterTerm.SourceTag("cat_ears"), MediaFilterTerm.SourceTag("solo")])!.Apply(all, null, tags));
        // Excluding keeps media without source tags (the chan files) and drops every medium that carries the tag.
        Assert.Equal([chan, cat, dog], MediaPageTermFilter.From([MediaFilterTerm.SourceTag("solo", exclude: true)])!.Apply(all, null, tags));
        Assert.Equal("media.filter.error.sourceTags", Assert.Throws<MediaPageTermException>(() =>
            MediaPageTermFilter.From([MediaFilterTerm.SourceTag("solo")])!.Apply(all, null, null)).TextKey);
    }

    [Fact]
    public void TheSourceTagIndexReadsTheProvidersTagsOfOneSnapshotOnce()
    {
        static BooruPostRecord Post(string source, string id, params string[] names) => new(source, id,
            new BooruPostClaim(source, id, BooruPostClaimPresence.None, Tags: names.Select(name => new BooruPostClaimTag(name, BooruPostClaimTagCategory.General)).ToArray()),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        BooruCatalogSnapshot catalog = new(BooruCatalogSnapshot.CurrentSchemaVersion,
            [Post(BooruMediaIdentity.Source, "10", "Cat_Ears", "solo", "cat_ears", "with space"), Post("other", "11", "dog")], []);
        MediaSourceTagIndexCache cache = new();
        MediaSourceTagIndex index = cache.For(catalog);
        Assert.Same(index, cache.For(catalog));
        cache.Clear();
        Assert.NotSame(index, cache.For(catalog));
        Assert.Same(MediaSourceTagIndex.Empty, cache.For(null));
        Assert.Equal(["cat_ears", "solo"], index.TagsOf(Entry(1, "10")));
        Assert.Empty(index.TagsOf(Entry(2, "11"))); // another provider's post is not a Danbooru medium of this index
        Assert.Empty(index.TagsOf(Entry(3)));
        Assert.Equal(1, index.PostCount);
    }

    [Fact]
    public void SourceTagFiltersUseOnlyTheDefaultDanbooruInstanceInEitherOrder()
    {
        static BooruPostRecord Post(string? instance, string name) => new(BooruMediaIdentity.Source, "42",
            new BooruPostClaim(BooruMediaIdentity.Source, "42", BooruPostClaimPresence.Tags,
                Tags: [new(name, BooruPostClaimTagCategory.General)], InstanceId: instance),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, InstanceId: instance);

        BooruPostRecord legacy = Post(null, "default_tag");
        BooruPostRecord other = Post("test-instance", "foreign_tag");
        MediaLibraryEntry medium = Entry(1, "42");
        MediaPageTermFilter legacyFilter = MediaPageTermFilter.From([MediaFilterTerm.SourceTag("default_tag")])!;
        MediaPageTermFilter otherFilter = MediaPageTermFilter.From([MediaFilterTerm.SourceTag("foreign_tag")])!;
        foreach (BooruPostRecord[] posts in new[] { new[] { legacy, other }, new[] { other, legacy } })
        {
            BooruCatalogSnapshot catalog = new(BooruCatalogSnapshot.ScopedIdentitySchemaVersion, posts, []);
            MediaSourceTagIndex index = MediaSourceTagIndex.From(catalog);
            Assert.Equal([medium], legacyFilter.Apply([medium], null, index));
            Assert.Empty(otherFilter.Apply([medium], null, index));
        }
    }

    [Fact]
    public void EveryLanguageHasTheChipsTheRefusalsAndQueryNamesOfTheNewFields()
    {
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            foreach (string key in new[] { "media.filter.queueChip", "media.filter.queueExcludeChip", "media.filter.sourceTagChip", "media.filter.sourceTagExcludeChip",
                         "media.filter.error.queue", "media.filter.error.sourceTags", "media.filter.retry", "media.query.alias.queue", "media.query.alias.booru" })
                Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, code + " lacks " + key);
            Assert.Contains("solo", Texts.T("media.filter.sourceTagChip", "solo"));
            IReadOnlyDictionary<string, MediaFilterField> aliases = MediaFilterQueryVocabulary.AliasesFrom(key => Texts.T(key));
            string queueWord = Texts.T("media.query.alias.queue").Split(' ')[0];
            Assert.Equal(MediaFilterField.Queue, aliases.TryGetValue(queueWord, out MediaFilterField field) ? field : MediaFilterTerm.FieldOf(queueWord));
        }
    }
}
