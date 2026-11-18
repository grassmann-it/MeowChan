using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class PostFilterStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meowchan-post-filter-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "post-filters.json");
    private ProfileLease Lease() => ProfileLease.Acquire(_directory);
    private static readonly ResourceId Thread1 = new("4chan", "g", "100");
    private static PostFilterRuleDraft Draft(string pattern = "spam", PostFilterField field = PostFilterField.Text,
        PostFilterAction action = PostFilterAction.Hide, string? source = null, string? board = null,
        PostFilterMatch match = PostFilterMatch.Contains, bool replies = false) =>
        new(field, match, pattern, action, replies, source, board);

    [Fact]
    public void MissingFileIsPassiveAndRoundtripSurvivesRestart()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset added = new(2026, 11, 13, 10, 30, 15, TimeSpan.FromHours(1));
        using (var lease = Lease())
        {
            var store = new PostFilterStore(_directory, lease);
            PostFilterSnapshot empty = store.Load();
            Assert.Equal(PostFilterStoreState.Ready, empty.State);
            Assert.Empty(empty.Rules); Assert.Empty(empty.HiddenPosts);
            Assert.Equal(HiddenPostsAction.Stub, empty.HiddenPostsAction);
            Assert.False(File.Exists(FilePath));
            var one = store.AddRule(id, Draft("  Spam Link ", source: "4chan", board: "g", action: PostFilterAction.Stub, replies: true), 0);
            Assert.Equal(1, one.Revision);
            var two = store.HidePost(Thread1, "101", true, added, 1);
            var three = store.SetHiddenPostsAction(HiddenPostsAction.Hide, 2);
            Assert.Equal(3, three.Revision);
            Assert.Equal(two.Rules, three.Rules);
        }
        using (var lease = Lease())
        {
            PostFilterSnapshot loaded = new PostFilterStore(_directory, lease).Load();
            Assert.Equal(3, loaded.Revision); Assert.Equal(PostFilterStoreState.Ready, loaded.State);
            Assert.Equal(HiddenPostsAction.Hide, loaded.HiddenPostsAction);
            PostFilterRule rule = Assert.Single(loaded.Rules);
            Assert.Equal(new PostFilterRule(id, true, PostFilterField.Text, PostFilterMatch.Contains, "  Spam Link ",
                PostFilterAction.Stub, true, "4chan", "g"), rule);
            HiddenPost hidden = Assert.Single(loaded.HiddenPosts);
            Assert.Equal(new HiddenPost(Thread1, "101", true, added.ToUniversalTime()), hidden);
            JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!;
            Assert.Equal(1, (int)root["schemaVersion"]!);
            Assert.Equal("hide", (string)root["hiddenPostsAction"]!);
            Assert.Equal("contains", (string)root["rules"]![0]!["match"]!);
            Assert.Equal("g", (string)root["rules"]![0]!["board"]!);
            Assert.True(File.Exists(FilePath + ".bak"));
        }
    }

    [Fact]
    public void GlobalRuleWritesNoScopeMembersAndEmptyScopeIsGlobal()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        store.AddRule(Guid.NewGuid(), Draft("x", source: "", board: ""), 0);
        JsonNode rule = JsonNode.Parse(File.ReadAllText(FilePath))!["rules"]![0]!;
        Assert.Null(rule["source"]); Assert.Null(rule["board"]);
        Assert.Null(Assert.Single(store.Load().Rules).Source);
    }

    [Fact]
    public void EditEnableAndRemoveKeepIdentityAndRequireCurrentRevision()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        store.AddRule(first, Draft("one"), 0); store.AddRule(second, Draft("two"), 1);
        var replaced = store.ReplaceRule(first, Draft("uno", action: PostFilterAction.Highlight, source: "4chan"), 2);
        Assert.Equal(new[] { first, second }, replaced.Rules.Select(x => x.Id));
        Assert.Equal("uno", replaced.Rules[0].Pattern); Assert.Equal(PostFilterAction.Highlight, replaced.Rules[0].Action);
        Assert.Throws<PostFilterRevisionException>(() => store.RemoveRule(first, 2));
        var disabled = store.SetRuleEnabled(second, false, 3);
        Assert.False(disabled.Rules[1].Enabled); Assert.True(disabled.Rules[0].Enabled);
        Assert.Equal(second, Assert.Single(store.RemoveRule(first, 4).Rules).Id);
        Assert.Throws<InvalidOperationException>(() => store.RemoveRule(first, 5));
        Assert.Equal(5, store.Load().Revision);
    }

    [Fact]
    public void ReplacingARuleWithItselfIsNotADuplicateButCollidingWithAnotherIs()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        store.AddRule(first, Draft("one"), 0); store.AddRule(second, Draft("two"), 1);
        store.ReplaceRule(first, Draft("ONE", action: PostFilterAction.Stub), 2);
        var error = Assert.Throws<PostFilterDuplicateException>(() => store.ReplaceRule(second, Draft("one"), 3));
        Assert.True(error.IsRule); Assert.Equal(first, error.ExistingRuleId);
        Assert.Equal(3, store.Load().Revision);
    }

    [Fact]
    public void DuplicateRuleIsRejectedByFieldMatchPatternAndScopeIgnoringCase()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        store.AddRule(Guid.NewGuid(), Draft("Spam"), 0);
        byte[] before = File.ReadAllBytes(FilePath);
        var error = Assert.Throws<PostFilterDuplicateException>(() => store.AddRule(Guid.NewGuid(), Draft("sPAM", action: PostFilterAction.Highlight, replies: true), 1));
        Assert.True(error.IsRule);
        Assert.Equal(before, File.ReadAllBytes(FilePath)); Assert.Equal(1, store.Load().Revision);
        // Another field, match or scope is a different rule.
        long revision = 1;
        foreach (PostFilterRuleDraft other in new[]
        {
            Draft("Spam", field: PostFilterField.Subject), Draft("Spam", match: PostFilterMatch.Exact),
            Draft("Spam", source: "4chan"), Draft("Spam", source: "4chan", board: "g"), Draft("Spam", source: "4chan", board: "a"),
        })
            revision = store.AddRule(Guid.NewGuid(), other, revision).Revision;
        Assert.Equal(6, store.Load().Rules.Count);
        Assert.Throws<PostFilterDuplicateException>(() => store.AddRule(Guid.NewGuid(), Draft("SPAM", source: "4chan", board: "g"), revision));
    }

    [Fact]
    public void Md5IsCanonicalizedAndHexAndBase64AreTheSameRule()
    {
        const string hex = "0123456789ABCDEF0123456789abcdef";
        string base64 = Convert.ToBase64String(Convert.FromHexString(hex));
        Assert.Equal(24, base64.Length);
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        var saved = store.AddRule(Guid.NewGuid(), Draft(" " + hex + " ", field: PostFilterField.Md5, match: PostFilterMatch.Contains), 0);
        Assert.Equal(hex.ToLowerInvariant(), saved.Rules[0].Pattern); Assert.Equal(PostFilterMatch.Exact, saved.Rules[0].Match);
        Assert.Throws<PostFilterDuplicateException>(() => store.AddRule(Guid.NewGuid(), Draft(base64, field: PostFilterField.Md5), 1));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("abc", field: PostFilterField.Md5), 1));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft(new string('z', 32), field: PostFilterField.Md5), 1));
        Assert.True(PostFilterRules.TryParseMd5(base64, out byte[] bytes)); Assert.Equal(Convert.FromHexString(hex), bytes);
        Assert.False(PostFilterRules.TryParseMd5(null, out _));
    }

    [Fact]
    public void InvalidRuleInputIsRefusedWithoutWriting()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.Empty, Draft(), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft(""), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("   "), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("a\nb"), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("x", board: "g"), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("x", source: new string('s', 257)), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("x", field: (PostFilterField)9), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("x", action: (PostFilterAction)9), 0));
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft("x", match: (PostFilterMatch)9), 0));
        Assert.Throws<ArgumentException>(() => store.HidePost(Thread1, "", false, DateTimeOffset.UtcNow, 0));
        Assert.Throws<ArgumentException>(() => store.HidePost(new ResourceId("4chan", "g"), "1", false, DateTimeOffset.UtcNow, 0));
        Assert.Throws<ArgumentException>(() => store.HidePost(Thread1, "1\n2", false, DateTimeOffset.UtcNow, 0));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void PatternLengthIsCountedInCodepointsAtFiveHundredTwelve()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        string emoji = string.Concat(Enumerable.Repeat("😀", PostFilterRules.MaximumPatternCodepoints));
        Assert.Equal(emoji, store.AddRule(Guid.NewGuid(), Draft(emoji), 0).Rules[0].Pattern);
        Assert.Throws<ArgumentException>(() => store.AddRule(Guid.NewGuid(), Draft(emoji + "x"), 1));
        Assert.Equal(emoji, Assert.Single(store.Load().Rules).Pattern);
    }

    [Fact]
    public void HiddenPostsRejectDuplicatesAndCanBeShownAgain()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.HidePost(Thread1, "101", false, now, 0);
        var error = Assert.Throws<PostFilterDuplicateException>(() => store.HidePost(Thread1, "101", true, now, 1));
        Assert.False(error.IsRule);
        store.HidePost(Thread1, "102", true, now, 1);
        store.HidePost(new ResourceId("4chan", "g", "200"), "101", false, now, 2);
        Assert.Equal(3, store.Load().HiddenPosts.Count);
        var shown = store.UnhidePost(Thread1, "101", 3);
        Assert.Equal(new[] { "102", "101" }, shown.HiddenPosts.Select(x => x.Post));
        Assert.Throws<InvalidOperationException>(() => store.UnhidePost(Thread1, "101", 4));
    }

    [Fact]
    public void RuleLimitRefusesGrowthNamingTheLimitButStillAllowsEditsAndRemovals()
    {
        using var lease = Lease();
        int count = ProfileStoreLimits.MinimumPostFilterRules + 5;
        JsonArray rules = new();
        for (int index = 0; index < count; index++)
            rules.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["enabled"] = true, ["field"] = "text", ["match"] = "contains",
                ["pattern"] = "p" + index, ["action"] = "hide", ["replies"] = false });
        string bytes = new JsonObject { ["schemaVersion"] = 1, ["revision"] = 42, ["hiddenPostsAction"] = "stub", ["rules"] = rules, ["hiddenPosts"] = new JsonArray() }.ToJsonString();
        File.WriteAllText(FilePath, bytes);
        lease.StoreLimits.PostFilterRules = ProfileStoreLimits.MinimumPostFilterRules;
        var store = new PostFilterStore(_directory, lease);
        Assert.Equal(count, store.Load().Rules.Count);
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.AddRule(Guid.NewGuid(), Draft("overflow"), 42));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.PostFilterRules, kind); Assert.Equal(ProfileStoreLimits.MinimumPostFilterRules, limit);
        Assert.Equal(bytes, File.ReadAllText(FilePath)); Assert.Equal(42, store.Load().Revision);
        var snapshot = store.Load();
        store.ReplaceRule(snapshot.Rules[0].Id, Draft("edited"), 42);
        Assert.Equal(count - 1, store.RemoveRule(snapshot.Rules[1].Id, 43).Rules.Count);
        lease.StoreLimits.PostFilterRules = 500;
        Assert.Equal(count, store.AddRule(Guid.NewGuid(), Draft("again"), 44).Rules.Count);
    }

    [Fact]
    public void HiddenPostLimitRefusesGrowthNamingTheLimit()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        lease.StoreLimits.HiddenPosts = ProfileStoreLimits.MinimumHiddenPosts;
        // Seed at the limit through the store itself, in one file write per hundred: the limit is only checked per write.
        JsonArray hidden = new();
        for (int index = 0; index < ProfileStoreLimits.MinimumHiddenPosts; index++)
            hidden.Add(new JsonObject { ["source"] = "4chan", ["board"] = "g", ["thread"] = "100", ["post"] = index.ToString(),
                ["replies"] = false, ["added"] = "2026-11-13T00:00:00.0000000Z" });
        string bytes = new JsonObject { ["schemaVersion"] = 1, ["revision"] = 7, ["hiddenPostsAction"] = "stub", ["rules"] = new JsonArray(), ["hiddenPosts"] = hidden }.ToJsonString();
        File.WriteAllText(FilePath, bytes);
        Assert.Equal(ProfileStoreLimits.MinimumHiddenPosts, store.Load().HiddenPosts.Count);
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.HidePost(Thread1, "new", false, DateTimeOffset.UtcNow, 7));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out _)); Assert.Equal(PrivateStoreLimitKind.HiddenPosts, kind);
        Assert.Equal(bytes, File.ReadAllText(FilePath));
        Assert.Equal(ProfileStoreLimits.MinimumHiddenPosts - 1, store.UnhidePost(Thread1, "5", 7).HiddenPosts.Count);
        Assert.Equal(ProfileStoreLimits.MinimumHiddenPosts, store.HidePost(Thread1, "new", false, DateTimeOffset.UtcNow, 8).HiddenPosts.Count);
    }

    [Fact]
    public void LimitsHaveTheirDocumentedDefaultsAndRanges()
    {
        ProfileStoreLimits limits = new();
        Assert.Equal((200, 10_000), (limits.PostFilterRules, limits.HiddenPosts));
        Assert.Equal(32 * 1024 * 1024, PostFilterStore.MaximumFileBytes);
        Assert.Equal(512, PostFilterRules.MaximumPatternCodepoints);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.PostFilterRules = ProfileStoreLimits.MinimumPostFilterRules - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.PostFilterRules = ProfileStoreLimits.PostFilterRuleCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.HiddenPosts = ProfileStoreLimits.MinimumHiddenPosts - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.HiddenPosts = ProfileStoreLimits.HiddenPostCeiling + 1);
        limits.PostFilterRules = 2_000; limits.HiddenPosts = 100_000;
        Assert.Equal((2_000, 100_000), (limits.PostFilterRules, limits.HiddenPosts));
    }

    [Fact]
    public void ExternalSameRevisionChangeConflictsAndKeepsDiskBytes()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("first"), 0);
        string changed = File.ReadAllText(FilePath).Replace("first", "other"); File.WriteAllText(FilePath, changed);
        var error = Assert.Throws<PostFilterRevisionException>(() => store.SetRuleEnabled(id, false, 1));
        Assert.Equal("other", error.Current.Rules[0].Pattern); Assert.Equal(changed, File.ReadAllText(FilePath));
    }

    [Fact]
    public void DamagedPrimaryRequiresExplicitRecoveryAndPreservesOriginal()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("one"), 0); store.SetRuleEnabled(id, false, 1);
        File.WriteAllText(FilePath, "{broken"); var recovery = store.Load();
        Assert.Equal(PostFilterStoreState.RecoveryAvailable, recovery.State); Assert.False(recovery.CanWrite);
        Assert.Equal("{broken", File.ReadAllText(FilePath));
        Assert.Throws<PostFilterRevisionException>(() => store.RemoveRule(id, recovery.Revision));
        var restored = store.Recover(recovery.Revision);
        Assert.True(restored.CanWrite); Assert.True(restored.Rules[0].Enabled); Assert.Equal(2, restored.Revision); // the backup (revision 1) plus one
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "post-filters.json.corrupt-*"))));
        Assert.Equal(PostFilterStoreState.Ready, store.Load().State);
    }

    [Fact]
    public void ChangedCorruptPrimaryRequiresAnotherRecoveryReview()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("one"), 0); store.SetRuleEnabled(id, false, 1);
        File.WriteAllText(FilePath, "{broken-first"); var reviewed = store.Load();
        File.WriteAllText(FilePath, "{broken-second");
        Assert.Throws<PostFilterRevisionException>(() => store.Recover(reviewed.Revision));
        Assert.Equal("{broken-second", File.ReadAllText(FilePath)); Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }

    [Fact]
    public void BrokenFileWithoutBackupIsNotAnEmptyWritableStore()
    {
        using var lease = Lease(); File.WriteAllText(FilePath, "{"); var store = new PostFilterStore(_directory, lease);
        Assert.Equal(PostFilterStoreState.Failed, store.Load().State);
        Assert.Throws<PostFilterRevisionException>(() => store.AddRule(Guid.NewGuid(), Draft(), 0));
        Assert.Equal("{", File.ReadAllText(FilePath));
    }

    [Fact]
    public void DamagedBackupBlocksTheNextWriteAndKeepsBothFiles()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("one"), 0); store.SetRuleEnabled(id, false, 1);
        File.WriteAllText(FilePath + ".bak", "{damaged");
        byte[] primary = File.ReadAllBytes(FilePath);
        Assert.Equal(PostFilterStoreState.Ready, store.Load().State);
        Assert.Throws<InvalidOperationException>(() => store.RemoveRule(id, 2));
        Assert.Equal(primary, File.ReadAllBytes(FilePath)); Assert.Equal("{damaged", File.ReadAllText(FilePath + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, ".post-filters.json.*.tmp"));
    }

    [Theory]
    [InlineData("""{"schemaVersion":9,"future":true}""")]
    [InlineData("""{"schemaVersion":1,"revision":4,"hiddenPostsAction":"stub","rules":[],"hiddenPosts":[],"exclusions":[]}""")]
    [InlineData("""{"schemaVersion":1,"revision":4,"hiddenPostsAction":"collapse","rules":[],"hiddenPosts":[]}""")]
    [InlineData("""{"schemaVersion":1,"revision":4,"hiddenPostsAction":"stub","rules":[],"hiddenPosts":[{"source":"4chan","board":"g","thread":"1","post":"2","replies":false,"added":"2026-11-13T00:00:00Z","future":1}]}""")]
    public void UnknownEnvelopeOrEntryMembersMakeTheWholeStoreReadOnlyAndNeverFallBackToABackup(string document)
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("one"), 0); store.SetRuleEnabled(id, false, 1);
        File.WriteAllText(FilePath, document);
        var loaded = store.Load();
        Assert.Equal(PostFilterStoreState.Unsupported, loaded.State); Assert.False(loaded.CanWrite);
        Assert.Throws<InvalidOperationException>(() => store.Recover(loaded.Revision));
        Assert.Throws<PostFilterRevisionException>(() => store.AddRule(Guid.NewGuid(), Draft("two"), loaded.Revision));
        Assert.Equal(document, File.ReadAllText(FilePath));
    }

    [Fact]
    public void RegexAndOtherUnknownRulesAreKeptUnchangedAndNeverApplied()
    {
        Guid regexId = Guid.NewGuid(), futureId = Guid.NewGuid(), knownId = Guid.NewGuid();
        const string regexRule = """{"id":"@R","enabled":true,"field":"text","match":"regex","pattern":"^a+$","action":"hide","replies":false,"flags":"i"}""";
        const string futureRule = """{"id":"@F","enabled":true,"field":"poster-id","match":"exact","pattern":"x","action":"hide","replies":false}""";
        string document = "{\"schemaVersion\":1,\"revision\":5,\"hiddenPostsAction\":\"stub\",\"rules\":[" +
            regexRule.Replace("@R", regexId.ToString()) + "," + futureRule.Replace("@F", futureId.ToString()) + "," +
            "{\"id\":\"" + knownId + "\",\"enabled\":true,\"field\":\"text\",\"match\":\"contains\",\"pattern\":\"abc\",\"action\":\"hide\",\"replies\":false}],\"hiddenPosts\":[]}";
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease);
        File.WriteAllText(FilePath, document);
        var loaded = store.Load();
        Assert.Equal(PostFilterStoreState.Ready, loaded.State); Assert.True(loaded.CanWrite);
        Assert.Equal(new[] { false, false, true }, loaded.Rules.Select(x => x.CanApply));
        Assert.NotNull(loaded.Rules[0].UnsupportedReason);
        Assert.Throws<InvalidOperationException>(() => store.SetRuleEnabled(regexId, false, 5));
        Assert.Throws<InvalidOperationException>(() => store.ReplaceRule(regexId, Draft("x"), 5));
        // An unrelated write keeps both unsupported rules byte for byte (compared as JSON).
        var written = store.SetRuleEnabled(knownId, false, 5);
        Assert.Equal(new[] { regexId, futureId, knownId }, written.Rules.Select(x => x.Id));
        JsonNode rules = JsonNode.Parse(File.ReadAllText(FilePath))!["rules"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(regexRule.Replace("@R", regexId.ToString())), rules[0]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(futureRule.Replace("@F", futureId.ToString())), rules[1]));
        // An unsupported rule is neither a duplicate nor a blocker for a known rule with the same pattern, and it can be removed.
        store.AddRule(Guid.NewGuid(), Draft("^a+$"), 6);
        Assert.Equal(3, store.RemoveRule(regexId, 7).Rules.Count);
    }

    [Theory]
    [InlineData("duplicate-rule-id")]
    [InlineData("empty-rule-id")]
    [InlineData("duplicate-hidden-post")]
    [InlineData("duplicate-property")]
    [InlineData("missing-replies")]
    [InlineData("empty-pattern")]
    [InlineData("long-pattern")]
    [InlineData("board-without-source")]
    [InlineData("bad-added")]
    [InlineData("pattern-number")]
    public void InvalidContentIsDamageAndCannotPartiallyImport(string kind)
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("one"), 0); store.HidePost(Thread1, "1", false, DateTimeOffset.UtcNow, 1);
        JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!;
        JsonNode rule = root["rules"]![0]!, hidden = root["hiddenPosts"]![0]!;
        string text;
        switch (kind)
        {
            case "duplicate-rule-id": root["rules"]!.AsArray().Add(rule.DeepClone()); text = root.ToJsonString(); break;
            case "empty-rule-id": rule["id"] = Guid.Empty.ToString(); text = root.ToJsonString(); break;
            case "duplicate-hidden-post": root["hiddenPosts"]!.AsArray().Add(hidden.DeepClone()); text = root.ToJsonString(); break;
            case "duplicate-property": text = root.ToJsonString().Replace("\"enabled\":true,", "\"enabled\":true,\"enabled\":false,"); break;
            case "missing-replies": rule.AsObject().Remove("replies"); text = root.ToJsonString(); break;
            case "empty-pattern": rule["pattern"] = ""; text = root.ToJsonString(); break;
            case "long-pattern": rule["pattern"] = new string('x', 513); text = root.ToJsonString(); break;
            case "board-without-source": rule["board"] = "g"; text = root.ToJsonString(); break;
            case "bad-added": hidden["added"] = "yesterday"; text = root.ToJsonString(); break;
            default: rule["pattern"] = 5; text = root.ToJsonString(); break;
        }
        if (kind == "duplicate-property") Assert.Contains("\"enabled\":false", text);
        File.WriteAllText(FilePath, text);
        var loaded = store.Load();
        Assert.Equal(PostFilterStoreState.RecoveryAvailable, loaded.State);
        Assert.Throws<PostFilterRevisionException>(() => store.RemoveRule(id, loaded.Revision));
        Assert.Equal(text, File.ReadAllText(FilePath));
    }

    [Fact]
    public void OversizedFileAndBadUtf8CannotBeOverwrittenAsEmpty()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease);
        File.WriteAllBytes(FilePath, new byte[PostFilterStore.MaximumFileBytes + 1]);
        Assert.Equal(PostFilterStoreState.Failed, store.Load().State);
        File.WriteAllBytes(FilePath, new byte[] { 0xff, 0xfe });
        Assert.Equal(PostFilterStoreState.Failed, store.Load().State);
        Assert.Throws<PostFilterRevisionException>(() => store.AddRule(Guid.NewGuid(), Draft(), 0));
        Assert.Equal(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void CancellationBeforeCommitLeavesLastSnapshotAndBytes()
    {
        using var lease = Lease(); var store = new PostFilterStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.AddRule(id, Draft("before"), 0); string bytes = File.ReadAllText(FilePath);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.SetRuleEnabled(id, false, 1, canceled.Token));
        Assert.Equal(bytes, File.ReadAllText(FilePath)); Assert.True(store.Load().Rules[0].Enabled);
    }

    [Fact]
    public void StoreRequiresMatchingLiveLeaseAndProfilesRemainSeparate()
    {
        using var lease = Lease(); Assert.Throws<ArgumentException>(() => new PostFilterStore(_directory + "-other", lease));
        var store = new PostFilterStore(_directory, lease); lease.Dispose(); Assert.Throws<ObjectDisposedException>(() => store.Load());
        string otherDirectory = Path.Combine(_directory, "second-profile");
        using var first = Lease(); using var second = ProfileLease.Acquire(otherDirectory);
        var a = new PostFilterStore(_directory, first); var b = new PostFilterStore(otherDirectory, second); a.Load(); b.Load();
        a.AddRule(Guid.NewGuid(), Draft("one"), 0);
        Assert.Empty(b.Load().Rules);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
