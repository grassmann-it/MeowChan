using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

public sealed class BooruContentFilterTests
{
    [Theory]
    [InlineData("danbooru", "g", "General", "General", "Allowed")]
    [InlineData("danbooru", "s", "Sensitive", "General", "AboveRatingCeiling")]
    [InlineData("danbooru", "s", "Sensitive", "Sensitive", "Allowed")]
    [InlineData("danbooru", "q", "Questionable", "Sensitive", "AboveRatingCeiling")]
    [InlineData("danbooru", "q", "Questionable", "Questionable", "Allowed")]
    [InlineData("danbooru", "e", "Explicit", "Questionable", "AboveRatingCeiling")]
    [InlineData("danbooru", "e", "Explicit", "Explicit", "Allowed")]
    [InlineData("danbooru", null, "General", "General", "Allowed")]
    [InlineData("e621", "s", "General", "General", "Allowed")]
    [InlineData("e621", "safe", "General", "General", "Allowed")]
    [InlineData("e621", null, "General", "General", "Allowed")]
    [InlineData("e621", "q", "Questionable", "General", "AboveRatingCeiling")]
    [InlineData("e621", "e", "Explicit", "Questionable", "AboveRatingCeiling")]
    [InlineData("e926", "s", "General", "General", "Allowed")]
    [InlineData("e926", "safe", "General", "General", "Allowed")]
    [InlineData("e926", "q", "Questionable", "General", "AboveRatingCeiling")]
    [InlineData("e926", "e", "Explicit", "Questionable", "AboveRatingCeiling")]
    [InlineData("e621", "s", "Sensitive", "Explicit", "UnknownRating")]
    [InlineData("danbooru", "safe", "General", "Explicit", "UnknownRating")]
    [InlineData("danbooru", "other", "General", "Explicit", "UnknownRating")]
    [InlineData("danbooru", "g", "Unknown", "Explicit", "UnknownRating")]
    public void SourceRatingTable(string source, string? raw, string rating, string maximum, string reason)
    {
        BooruPostClaim post = Post(source, Enum.Parse<BooruPostClaimRating>(rating), raw);
        BooruContentFilter filter = new(17, Settings(Enum.Parse<BooruPostClaimRating>(maximum)));

        BooruContentDecision decision = filter.Evaluate(post);

        Assert.Equal(Enum.Parse<BooruContentReason>(reason), decision.Reason);
        Assert.Equal(reason == "Allowed", decision.IsAllowed);
        Assert.Equal(17L, decision.Revision);
    }

    [Fact]
    public void AnUnflaggedRatingAndAnUnknownNormalizedValueStayBlocked()
    {
        BooruContentFilter filter = new(2, Settings(BooruPostClaimRating.Explicit));
        BooruPostClaim missing = Post("danbooru", BooruPostClaimRating.General, "g") with
        {
            Fields = BooruPostClaimPresence.None,
            PostUrl = "https://danbooru.donmai.us/posts/100",
            SourceClaim = "rating:g"
        };
        BooruPostClaim unknown = Post("danbooru", (BooruPostClaimRating)99);
        BooruPostClaim observedNull = Post("danbooru", BooruPostClaimRating.Unknown);

        Assert.Equal(BooruContentReason.UnknownRating, filter.EvaluateForPage(missing, 2).Reason);
        Assert.Equal(BooruContentReason.UnknownRating, filter.Evaluate(unknown).Reason);
        Assert.Equal(BooruContentReason.UnknownRating, filter.Evaluate(observedNull).Reason);
    }

    [Fact]
    public void IdentityUsesTheClaimSourceAndInstanceEvenWithTheSameHostAndPostId()
    {
        BooruContentFilter filter = new(5, Settings(BooruPostClaimRating.General, "",
            new("danbooru", "default", "cat"), new("e621", "mirror", "cat")));
        BooruPostClaim original = Post("danbooru", BooruPostClaimRating.General, "g", tags: ["cat"]) with
        {
            PostUrl = "https://e621.net/posts/100"
        };
        BooruPostClaim e621 = original with { SourceId = "e621", RawRating = "s" };

        Assert.Equal(new BooruContentRuleLocation(1, "danbooru", "default"), filter.Evaluate(original).Rule);
        Assert.True(filter.Evaluate(original with { InstanceId = "mirror" }).IsAllowed);
        Assert.True(filter.Evaluate(e621).IsAllowed);
        Assert.Equal(new BooruContentRuleLocation(1, "e621", "mirror"),
            filter.Evaluate(e621 with { InstanceId = "mirror" }).Rule);
        Assert.True(filter.Evaluate(e621 with { SourceId = "e926", InstanceId = "mirror" }).IsAllowed);
        Assert.Equal(BooruContentReason.InvalidPostIdentity, filter.Evaluate(original with { InstanceId = "" }).Reason);
    }

    [Theory]
    [InlineData("cat dog", "Allowed")]
    [InlineData("cat mouse\r\ncat -dog", "Blacklisted")]
    [InlineData("cat -CAT", "Allowed")]
    [InlineData("  CAT\t-bird ", "Blacklisted")]
    [InlineData("rating:s", "Blacklisted")]
    [InlineData("rating:sensitive", "Blacklisted")]
    [InlineData("rating:safe", "Allowed")]
    [InlineData("-rating:s", "Allowed")]
    [InlineData("-rating:q", "Blacklisted")]
    [InlineData("cat score:<0", "Blacklisted")]
    [InlineData("cat score:<-2", "Allowed")]
    [InlineData("cat -score:<-2", "Blacklisted")]
    [InlineData("cat -score:<0", "Allowed")]
    public void AndOrNegationRatingAndScoreUseProviderFields(string blacklist, string reason)
    {
        BooruPostClaim post = Post("danbooru", BooruPostClaimRating.Sensitive, "s", -2, ["cat"]);
        BooruContentFilter filter = new(2, Settings(BooruPostClaimRating.Explicit, blacklist));

        Assert.Equal(Enum.Parse<BooruContentReason>(reason), filter.Evaluate(post).Reason);
    }

    [Theory]
    [InlineData("e621", "s", "rating:s")]
    [InlineData("e621", "safe", "rating:safe")]
    [InlineData("e926", "s", "rating:s")]
    [InlineData("e926", "safe", "rating:general")]
    public void BlacklistRecognizesTheSafeRatingOfTheE621Family(string source, string raw, string blacklist)
    {
        BooruContentFilter filter = new(1, Settings(BooruPostClaimRating.General, blacklist));

        Assert.Equal(BooruContentReason.Blacklisted,
            filter.Evaluate(Post(source, BooruPostClaimRating.General, raw)).Reason);
    }

    [Fact]
    public void MultipleMatchingLinesAndScopesProduceOneHiddenDecision()
    {
        BooruContentFilter filter = new(3, Settings(BooruPostClaimRating.Explicit, "\ncat\r\ncat -bird",
            new BooruContentSourceBlacklist("danbooru", "default", "cat\nscore:<0")));
        BooruPostClaim cat = Post("danbooru", BooruPostClaimRating.General, "g", -2, ["cat"]);
        BooruPostClaim dog = Post("danbooru", BooruPostClaimRating.General, "g", 5, ["dog"]) with { PostId = "101" };

        BooruContentDecision hidden = Assert.Single(new[] { cat, dog }.Select(filter.Evaluate), decision => !decision.IsAllowed);
        Assert.Equal(BooruContentReason.Blacklisted, hidden.Reason);
        Assert.Equal(new BooruContentRuleLocation(2, null, null), hidden.Rule);
    }

    [Theory]
    [InlineData("cat", "InsufficientMetadata")]
    [InlineData("-cat", "InsufficientMetadata")]
    [InlineData("score:<0", "InsufficientMetadata")]
    [InlineData("-score:<0", "InsufficientMetadata")]
    [InlineData("rating:q -cat", "Allowed")]
    [InlineData("score:<0 rating:q", "Allowed")]
    [InlineData("-cat\nrating:g", "Blacklisted")]
    public void MissingMetadataUsesThreeValuedMatching(string blacklist, string reason)
    {
        BooruContentFilter filter = new(1, Settings(BooruPostClaimRating.General, blacklist));
        BooruPostClaim post = Post("danbooru", BooruPostClaimRating.General, "g", -2, ["cat"]) with
        {
            Fields = BooruPostClaimPresence.Rating
        };
        BooruContentDecision decision = filter.Evaluate(post);

        Assert.Equal(Enum.Parse<BooruContentReason>(reason), decision.Reason);
    }

    [Fact]
    public void ObservedEmptyTagsAndObservedNullScoreAreDifferent()
    {
        BooruPostClaim post = Post("danbooru", BooruPostClaimRating.General, "g", tags: []) with
        {
            Fields = BooruPostClaimPresence.Rating | BooruPostClaimPresence.Tags | BooruPostClaimPresence.Score
        };
        BooruContentFilter tagRule = new(1, Settings(BooruPostClaimRating.General, "-cat"));
        BooruContentFilter scoreRule = new(1, Settings(BooruPostClaimRating.General, "-score:<0"));

        Assert.Equal(BooruContentReason.Blacklisted, tagRule.Evaluate(post).Reason);
        Assert.Equal(BooruContentReason.InsufficientMetadata, scoreRule.Evaluate(post).Reason);
    }

    [Theory]
    [InlineData("score:<=5")]
    [InlineData("score:<2147483648")]
    [InlineData("-rating:future")]
    [InlineData("rating:g,s")]
    [InlineData("tag:cat")]
    [InlineData("cat*")]
    [InlineData("cat | dog")]
    [InlineData("--cat")]
    public void AnUnrecognizedActiveRuleBlocksEvenWithAPageException(string badRule)
    {
        BooruContentFilter filter = new(7, Settings(BooruPostClaimRating.General, "",
            new BooruContentSourceBlacklist("e621", "mirror", "\n" + badRule)));
        BooruPostClaim post = Post("e621", BooruPostClaimRating.General, "s", tags: ["cat"]);

        BooruContentDecision blocked = filter.EvaluateForPage(post with { InstanceId = "mirror" }, 7);

        Assert.Equal(BooruContentReason.InvalidRules, blocked.Reason);
        Assert.Equal(new BooruContentRuleLocation(2, "e621", "mirror"), blocked.Rule);
        Assert.True(filter.Evaluate(post).IsAllowed);
    }

    [Fact]
    public void PageExceptionAppliesToKnownBlacklistMatchesAtItsOwnRevision()
    {
        BooruContentFilter filter = new(8, Settings(BooruPostClaimRating.General, "cat"));
        BooruPostClaim post = Post("danbooru", BooruPostClaimRating.General, "g", tags: ["cat"]);

        Assert.Equal(BooruContentReason.Blacklisted, filter.Evaluate(post).Reason);
        Assert.Equal(BooruContentReason.Blacklisted, filter.EvaluateForPage(post, 7).Reason);
        BooruContentDecision allowed = filter.EvaluateForPage(post, 8);
        Assert.True(allowed.IsAllowed);
        Assert.Equal(BooruContentReason.PageBlacklistException, allowed.Reason);
        Assert.Equal(new BooruContentRuleLocation(1, null, null), allowed.Rule);

        Assert.Equal(BooruContentReason.AboveRatingCeiling,
            filter.EvaluateForPage(post with { Rating = BooruPostClaimRating.Sensitive, RawRating = "s" }, 8).Reason);
        Assert.Equal(BooruContentReason.InsufficientMetadata,
            filter.EvaluateForPage(post with { Fields = BooruPostClaimPresence.Rating }, 8).Reason);

        BooruContentFilter uncertain = new(8, Settings(BooruPostClaimRating.General, "cat\nscore:<0"));
        Assert.Equal(BooruContentReason.InsufficientMetadata, uncertain.EvaluateForPage(post, 8).Reason);
    }

    [Fact]
    public void ChoiceAndRevisionAreProfileLocalAndSurviveReopening()
    {
        using TempDirectory directory = new();
        using TempDirectory otherDirectory = new();
        BooruPostClaim post = Post("danbooru", BooruPostClaimRating.General, "g", tags: ["cat"]);
        string path = Path.Combine(directory.Path, BooruContentProfileStore.FileName);

        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruContentProfileStore store = new(lease);
            BooruContentProfileSnapshot missing = store.Load();
            Assert.Equal(BooruContentProfileState.Missing, missing.State);
            Assert.Equal(0L, missing.Revision);
            Assert.Equal(BooruContentReason.ChoiceRequired, missing.Filter.EvaluateForPage(post, 0).Reason);
            Assert.False(File.Exists(path));

            using ProfileAdmissionToken writer = EnterWriter(lease);
            BooruContentProfileSnapshot chosen = store.SaveAdmitted(Settings(BooruPostClaimRating.General), 0, writer);
            Assert.Equal(1L, chosen.Revision);
            Assert.True(chosen.Filter.Evaluate(post).IsAllowed);

            BooruContentSettings revised = Settings(BooruPostClaimRating.General, "",
                new BooruContentSourceBlacklist("danbooru", "default", "cat"));
            BooruContentProfileSnapshot updated = store.SaveAdmitted(revised, chosen.Revision, writer);
            Assert.Equal(2L, updated.Revision);
            Assert.Equal(BooruContentReason.Blacklisted, updated.Filter.EvaluateForPage(post, 1).Reason);
            Assert.True(chosen.Filter.Evaluate(post).IsAllowed);
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        using ProfileLease other = ProfileLease.Acquire(otherDirectory.Path);
        BooruContentProfileSnapshot restored = new BooruContentProfileStore(reopened).Load();
        Assert.Equal(BooruContentProfileState.Ready, restored.State);
        Assert.Equal(2L, restored.Revision);
        Assert.Equal(BooruPostClaimRating.General, restored.Settings.MaximumRating);
        Assert.Equal(BooruContentReason.Blacklisted, restored.Filter.Evaluate(post).Reason);
        Assert.Equal(BooruContentReason.ChoiceRequired, new BooruContentProfileStore(other).Load().Filter.Evaluate(post).Reason);
    }

    [Fact]
    public void UnknownVersionKeepsTheOriginalBytesAndRefusesAWrite()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruContentProfileStore.FileName);
        byte[] bytes = """{ "schemaVersion": 99, "revision": 8, "futureRules": [true] }"""u8.ToArray();
        File.WriteAllBytes(path, bytes);
        BooruContentProfileStore store = new(lease);

        BooruContentProfileSnapshot snapshot = store.Load();
        Assert.Equal(BooruContentProfileState.Unsupported, snapshot.State);
        Assert.Equal(BooruContentReason.ProfileUnavailable,
            snapshot.Filter.Evaluate(Post("danbooru", BooruPostClaimRating.General)).Reason);
        using ProfileAdmissionToken writer = EnterWriter(lease);
        Assert.Throws<InvalidOperationException>(() => store.SaveAdmitted(Settings(BooruPostClaimRating.General), 0, writer));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"revision":1,"settings":{"globalBlacklist":"","sourceBlacklists":[]}}""")]
    [InlineData("""{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"general","globalBlacklist":"cat","globalBlacklist":"","sourceBlacklists":[]}}""")]
    [InlineData("""{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"general","globalBlacklist":"","sourceBlacklists":[],"future":42}}""")]
    [InlineData("""{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"unknown","globalBlacklist":"","sourceBlacklists":[]}}""")]
    public void UnreadableDocumentsBlockMediaAndKeepTheirBytes(string json)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruContentProfileStore.FileName);
        File.WriteAllText(path, json);
        BooruContentProfileStore store = new(lease);

        BooruContentProfileSnapshot snapshot = store.Load();
        Assert.Equal(BooruContentProfileState.Unreadable, snapshot.State);
        Assert.Equal(BooruContentReason.ProfileUnavailable,
            snapshot.Filter.Evaluate(Post("danbooru", BooruPostClaimRating.General)).Reason);
        using ProfileAdmissionToken writer = EnterWriter(lease);
        Assert.Throws<InvalidOperationException>(() => store.SaveAdmitted(Settings(BooruPostClaimRating.General), 0, writer));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void StaleCanceledAndMalformedSavesKeepTheLastConfirmedRules()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileAdmissionToken writer = EnterWriter(lease);
        BooruContentProfileStore store = new(lease);
        BooruContentProfileStore secondStore = new(lease);
        BooruContentSettings confirmed = Settings(BooruPostClaimRating.General, "cat");
        string path = Path.Combine(directory.Path, BooruContentProfileStore.FileName);

        Assert.Throws<ArgumentException>(() => store.SaveAdmitted(Settings(null), 0, writer));
        Assert.False(File.Exists(path));
        store.SaveAdmitted(confirmed, 0, writer);
        byte[] original = File.ReadAllBytes(path);

        BooruContentRevisionException stale = Assert.Throws<BooruContentRevisionException>(
            () => secondStore.SaveAdmitted(confirmed, 0, writer));
        Assert.Equal(0L, stale.ExpectedRevision);
        Assert.Equal(1L, stale.CurrentRevision);
        Assert.Throws<OperationCanceledException>(
            () => store.SaveAdmitted(confirmed, 1, writer, new CancellationToken(canceled: true)));
        BooruContentSettings malformed = Settings(BooruPostClaimRating.General, "",
            new BooruContentSourceBlacklist("e926", "default", "cat\nscore:>=0"));
        BooruContentRuleException invalid = Assert.Throws<BooruContentRuleException>(() => store.SaveAdmitted(malformed, 1, writer));
        Assert.Equal(new BooruContentRuleLocation(2, "e926", "default"), invalid.Rule);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1L, store.Load().Revision);
    }

    [Fact]
    public void WriterRequiresAdmissionFromTheSameProfile()
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(foreignDirectory.Path);
        using ProfileAdmissionToken foreign = EnterWriter(foreignLease);
        BooruContentProfileStore store = new(lease);

        Assert.Throws<InvalidOperationException>(
            () => store.SaveAdmitted(Settings(BooruPostClaimRating.General), 0, foreign));
        Assert.Equal(BooruContentProfileState.Missing, store.Load().State);
    }

    private static ProfileAdmissionToken EnterWriter(ProfileLease lease)
    {
        lease.Admission.CompleteStartup();
        return lease.Admission.EnterOrdinaryWriter();
    }

    private static BooruContentSettings Settings(BooruPostClaimRating? rating, string blacklist = "",
        params BooruContentSourceBlacklist[] sourceBlacklists) => new(rating, blacklist, sourceBlacklists);

    private static BooruPostClaim Post(string source, BooruPostClaimRating rating, string? raw = null,
        int? score = null, string[]? tags = null)
    {
        BooruPostClaimPresence fields = BooruPostClaimPresence.Rating;
        if (score.HasValue) fields |= BooruPostClaimPresence.Score;
        if (tags is not null) fields |= BooruPostClaimPresence.Tags;
        return new(source, "100", fields, Rating: rating, RawRating: raw, Score: score,
            Tags: tags?.Select(tag => new BooruPostClaimTag(tag, BooruPostClaimTagCategory.General)).ToArray());
    }
}
