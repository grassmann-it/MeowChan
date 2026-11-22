using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

// PT1: tag-queue.json is strict, atomic, bounded by settings and never repaired silently; the decision log undoes one step at a time.
public sealed class TagQueueStoreTests
{
    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private static TagQueueItem Item(int n, string? post = null, string? directory = null, string? name = null) => new(Thread,
        directory ?? "/media/42", "asset-" + n, new string((char)('a' + n % 6), 64), 100 + n, "media-" + n, "original", post ?? "p" + n, null, null, null,
        false, null, name ?? "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open, T0.AddSeconds(n));

    private static IReadOnlyList<TagQueueItem> Items(int from, int count) => Enumerable.Range(from, count).Select(n => Item(n)).ToArray();

    private static KeyValuePair<TagQueueKey, TagQueueState> Set(int n, TagQueueState state) => new(Item(n).Key, state);

    private static string FileOf(TempDirectory temp) => Path.Combine(temp.Path, TagQueueStore.FileName);

    [Fact]
    public void AMissingFileIsAnEmptyQueueAndReadingNeverCreatesIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueSnapshot snapshot = new TagQueueStore(temp.Path, lease).Read();
        Assert.Equal((0, 0, TagQueueLoadState.Ready), (snapshot.Count, snapshot.Cursor, snapshot.State));
        Assert.False(File.Exists(FileOf(temp)));
    }

    [Fact]
    public void AddAppendsInOrderAndCountsDuplicatesAcrossAndWithinTheCall()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        TagQueueAddResult first = store.Add(new[] { Item(1), Item(2), Item(1) });
        Assert.Equal((2, 1, 0), (first.Added, first.AlreadyPresent, first.OverLimit));
        TagQueueAddResult second = store.Add(new[] { Item(2), Item(3) });
        Assert.Equal((1, 1, 0), (second.Added, second.AlreadyPresent, second.OverLimit));
        TagQueueSnapshot read = store.Read();
        Assert.Equal(new[] { "asset-1", "asset-2", "asset-3" }, read.Items.Select(item => item.AssetId));
        Assert.All(read.Items, item => Assert.Equal(TagQueueState.Open, item.State));
        Assert.Equal(2, read.Revision);
    }

    [Fact]
    public void AnAddWithOnlyDuplicatesWritesNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(new[] { Item(1) });
        byte[] before = File.ReadAllBytes(FileOf(temp));
        TagQueueAddResult result = store.Add(new[] { Item(1) });
        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(FileOf(temp)));
        Assert.False(File.Exists(FileOf(temp) + ".bak"));
    }

    [Fact]
    public void ItemsAreAlwaysAddedOpenWhateverStateTheCallerSent()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(new[] { Item(1) with { State = TagQueueState.Done, DoneAtUtc = T0 } });
        Assert.Equal((TagQueueState.Open, (DateTimeOffset?)null), (store.Read().Items[0].State, store.Read().Items[0].DoneAtUtc));
    }

    [Fact]
    public void AnInvalidEntryIsACallerBugAndNothingIsWritten()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        Assert.Throws<ArgumentException>(() => store.Add(new[] { Item(1), Item(2) with { Sha256 = "short" } }));
        Assert.False(File.Exists(FileOf(temp)));
    }

    [Fact]
    public void EntriesOverTheLimitAreCountedNotRaisedAndALoweredLimitKeepsEverything()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.StoreLimits.TagQueueEntries = ProfileStoreLimits.MinimumTagQueueEntries;
        TagQueueStore store = new(temp.Path, lease);
        TagQueueAddResult result = store.Add(Items(0, 60));
        Assert.Equal((50, 0, 10), (result.Added, result.AlreadyPresent, result.OverLimit));
        Assert.Equal(50, store.Read().Count);
        // The file holds 50 entries; with the limit at its minimum nothing more fits, but nothing is dropped either.
        Assert.Equal((0, 5), (store.Add(Items(100, 5)).Added, store.Add(Items(100, 5)).OverLimit));
        Assert.Equal(50, store.Read().Count);
        lease.StoreLimits.TagQueueEntries = 200;
        Assert.Equal(5, store.Add(Items(100, 5)).Added);
        Assert.Equal(55, store.Read().Count);
    }

    [Fact]
    public void TheFileLimitRefusesGrowthWithTheSettingAndLeavesEveryFileUntouched()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.StoreLimits.TagQueueFileBytes = ProfileStoreLimits.MinimumTagQueueFileBytes;
        TagQueueStore store = new(temp.Path, lease);
        string longDirectory = "/" + new string('d', 1_000);
        store.Add(new[] { Item(0, directory: longDirectory, name: new string('n', 200)) });
        byte[] before = File.ReadAllBytes(FileOf(temp));
        TagQueueItem[] many = Enumerable.Range(1, 900).Select(n => Item(n, directory: longDirectory, name: new string('n', 200))).ToArray();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Add(many));
        Assert.True(PrivateStoreLimit.TryDescribe(error, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal((PrivateStoreLimitKind.TagQueueFile, (long)ProfileStoreLimits.MinimumTagQueueFileBytes), (kind, limit));
        Assert.Equal(before, File.ReadAllBytes(FileOf(temp)));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        // Removing entries is a write that does not grow the file, so it still works at the limit.
        Assert.Equal(0, store.Remove(new[] { Item(0, directory: longDirectory).Key }).Count);
    }

    [Fact]
    public void LimitsOutsideTheirRangeAreRefused()
    {
        ProfileStoreLimits limits = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueEntries = ProfileStoreLimits.MinimumTagQueueEntries - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueEntries = ProfileStoreLimits.TagQueueEntryCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueFileBytes = ProfileStoreLimits.MinimumTagQueueFileBytes - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueFileBytes = ProfileStoreLimits.TagQueueFileCeilingBytes + 1L);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueHistory = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagQueueHistory = ProfileStoreLimits.TagQueueHistoryCeiling + 1);
        Assert.Equal((2_000, 8L * 1024 * 1024, 500), (limits.TagQueueEntries, limits.TagQueueFileBytes, limits.TagQueueHistory));
    }

    [Fact]
    public void TheStateSurvivesARestartExactlyIncludingTheCursorAndTheStamp()
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            TagQueueStore store = new(temp.Path, lease);
            store.Add(Items(1, 4));
            store.SetStates(new[] { Set(1, TagQueueState.Done), Set(2, TagQueueState.Skipped) }, T0.AddHours(1), TagQueueCursorTarget.Relative(Item(2).Key, 1));
            store.SetStamp(new[] { "cat", "Dog" });
        }
        using ProfileLease again = ProfileLease.Acquire(temp.Path);
        TagQueueSnapshot read = new TagQueueStore(temp.Path, again).Read();
        Assert.Equal(new[] { TagQueueState.Done, TagQueueState.Skipped, TagQueueState.Open, TagQueueState.Open }, read.Items.Select(item => item.State));
        Assert.Equal(T0.AddHours(1), read.Items[0].DoneAtUtc);
        Assert.Null(read.Items[1].DoneAtUtc);
        Assert.Equal(2, read.Cursor);
        Assert.Equal(new[] { "cat", "Dog" }, read.Stamp);
        Assert.Equal(2, read.ResumeIndex());
    }

    [Fact]
    public void ResumeSkipsFinishedAndSkippedEntriesAndWrapsToTheFirstOpenOne()
    {
        TagQueueItem Of(int n, TagQueueState state) => Item(n) with { State = state, DoneAtUtc = state == TagQueueState.Done ? T0 : null };
        TagQueueItem[] items = { Of(1, TagQueueState.Open), Of(2, TagQueueState.Done), Of(3, TagQueueState.Skipped), Of(4, TagQueueState.Done) };
        Assert.Equal(0, new TagQueueSnapshot(1, 0, Array.Empty<string>(), items, TagQueueLoadState.Ready).ResumeIndex());
        Assert.Equal(0, new TagQueueSnapshot(1, 2, Array.Empty<string>(), items, TagQueueLoadState.Ready).ResumeIndex());
        Assert.Equal(0, new TagQueueSnapshot(1, 4, Array.Empty<string>(), items, TagQueueLoadState.Ready).ResumeIndex());
        TagQueueItem[] none = { Of(1, TagQueueState.Done), Of(2, TagQueueState.Skipped) };
        Assert.Null(new TagQueueSnapshot(1, 0, Array.Empty<string>(), none, TagQueueLoadState.Ready).ResumeIndex());
        Assert.Null(TagQueueSnapshot.Empty.ResumeIndex());
    }

    [Fact]
    public void ADecisionChangesStateAndCursorInOneWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 3));
        long revision = store.Read().Revision;
        TagQueueSnapshot after = store.SetStates(new[] { Set(1, TagQueueState.Done) }, T0, TagQueueCursorTarget.Relative(Item(1).Key, 1));
        Assert.Equal((revision + 1, 1, TagQueueState.Done), (after.Revision, after.Cursor, after.Items[0].State));
        // The reverse restores the state and the exact earlier cursor, again in one write.
        TagQueueSnapshot undone = store.SetStates(new[] { Set(1, TagQueueState.Open) }, T0, TagQueueCursorTarget.At(0));
        Assert.Equal((revision + 2, 0, TagQueueState.Open, (DateTimeOffset?)null), (undone.Revision, undone.Cursor, undone.Items[0].State, undone.Items[0].DoneAtUtc));
    }

    [Fact]
    public void AnUnchangedStateWritesNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 2));
        byte[] before = File.ReadAllBytes(FileOf(temp));
        store.SetStates(new[] { Set(1, TagQueueState.Open), new KeyValuePair<TagQueueKey, TagQueueState>(Item(99).Key, TagQueueState.Done) }, T0);
        Assert.Equal(before, File.ReadAllBytes(FileOf(temp)));
    }

    [Fact]
    public void FinishedEntriesBeyondTheHistoryLimitDropOutOldestFirstAndTheCursorFollows()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.StoreLimits.TagQueueHistory = 2;
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 5));
        store.SetStates(new[] { Set(1, TagQueueState.Done) }, T0.AddMinutes(1), TagQueueCursorTarget.Relative(Item(1).Key, 1));
        store.SetStates(new[] { Set(2, TagQueueState.Done) }, T0.AddMinutes(2), TagQueueCursorTarget.Relative(Item(2).Key, 1));
        TagQueueSnapshot third = store.SetStates(new[] { Set(3, TagQueueState.Done) }, T0.AddMinutes(3), TagQueueCursorTarget.Relative(Item(3).Key, 1));
        // The entry finished first is gone, open and the two newest finished entries stay, the cursor still stands on entry 4.
        Assert.Equal(new[] { "asset-2", "asset-3", "asset-4", "asset-5" }, third.Items.Select(item => item.AssetId));
        Assert.Equal("asset-4", third.Items[third.Cursor].AssetId);
        Assert.Equal(2, third.Done);
    }

    [Fact]
    public void HistoryZeroKeepsNoFinishedEntryAndNeverDropsOpenOrSkippedOnes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.StoreLimits.TagQueueHistory = 0;
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 3));
        store.SetStates(new[] { Set(1, TagQueueState.Skipped), Set(2, TagQueueState.Done) }, T0);
        TagQueueSnapshot read = store.Read();
        Assert.Equal(new[] { "asset-1", "asset-3" }, read.Items.Select(item => item.AssetId));
        Assert.Equal(new[] { TagQueueState.Skipped, TagQueueState.Open }, read.Items.Select(item => item.State));
    }

    [Fact]
    public void RemoveKeepsTheCursorOnTheSameEntryAndIgnoresUnknownKeys()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 5));
        store.SetCursor(3);
        TagQueueSnapshot after = store.Remove(new[] { Item(1).Key, Item(99).Key });
        Assert.Equal(new[] { "asset-2", "asset-3", "asset-4", "asset-5" }, after.Items.Select(item => item.AssetId));
        Assert.Equal("asset-4", after.Items[after.Cursor].AssetId);
        Assert.Equal(after.Revision, store.Remove(new[] { Item(99).Key }).Revision);
    }

    [Fact]
    public void MoveKeepsTheCursorOnTheSameEntryAndRefusesBadPositions()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 4));
        store.SetCursor(2);
        TagQueueSnapshot moved = store.Move(Item(4).Key, 0);
        Assert.Equal(new[] { "asset-4", "asset-1", "asset-2", "asset-3" }, moved.Items.Select(item => item.AssetId));
        Assert.Equal("asset-3", moved.Items[moved.Cursor].AssetId);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Move(Item(1).Key, 4));
        Assert.Throws<InvalidOperationException>(() => store.Move(Item(99).Key, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetCursor(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetCursor(-1));
    }

    [Fact]
    public void ClearRemovesEverythingOrOnlyTheFinishedEntries()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 4));
        store.SetStates(new[] { Set(1, TagQueueState.Done), Set(3, TagQueueState.Skipped) }, T0);
        store.SetCursor(2);
        TagQueueSnapshot finishedGone = store.Clear(finishedOnly: true);
        Assert.Equal(new[] { "asset-2", "asset-3", "asset-4" }, finishedGone.Items.Select(item => item.AssetId));
        Assert.Equal("asset-3", finishedGone.Items[finishedGone.Cursor].AssetId);
        Assert.Equal((0, 0), (store.Clear(finishedOnly: false).Count, store.Read().Cursor));
    }

    [Fact]
    public void TheStampIsNormalizedLimitedAndKeptWithTheQueue()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        Assert.Equal(new[] { "Cat", "dog" }, store.SetStamp(new[] { "Cat", "dog" }).Stamp);
        Assert.Throws<ArgumentException>(() => store.SetStamp(new[] { "cat", "CAT" }));
        Assert.Throws<ArgumentException>(() => store.SetStamp(new[] { "bad\nname" }));
        Assert.Throws<ArgumentException>(() => store.SetStamp(Enumerable.Range(0, TagQueueSnapshot.MaximumStamp + 1).Select(n => "tag" + n).ToArray()));
        store.Add(Items(1, 1));
        Assert.Equal(new[] { "Cat", "dog" }, store.Read().Stamp);
    }

    [Fact]
    public void EveryWriteRotatesTheLastGoodFileIntoTheBackup()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        Assert.False(File.Exists(FileOf(temp) + ".bak"));
        store.Add(Items(2, 1));
        byte[] firstRevision = File.ReadAllBytes(FileOf(temp) + ".bak");
        Assert.Contains("\"Revision\":1", Encoding.UTF8.GetString(firstRevision), StringComparison.Ordinal);
        Assert.Contains("\"Revision\":2", File.ReadAllText(FileOf(temp)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Cursor\":0,\"Stamp\":[],\"Items\":[],\"Extra\":1}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Cursor\":0,\"Stamp\":[],\"Items\":[],\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Cursor\":3,\"Stamp\":[],\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Cursor\":0,\"Stamp\":[\"cat\",\"CAT\"],\"Items\":[]}")]
    [InlineData("{\"Version\":0,\"Revision\":1,\"Cursor\":0,\"Stamp\":[],\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Cursor\":0,\"Stamp\":[],\"Items\":[{\"Thread\":{\"Source\":\"s\",\"Board\":\"b\",\"Thread\":\"1\"}}]}")]
    public void AMalformedFileIsRefusedAndNeverRewrittenByARead(string json)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllText(FileOf(temp), json);
        TagQueueStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.Add(Items(1, 1)));
        Assert.Equal(json, File.ReadAllText(FileOf(temp)));
        Assert.False(File.Exists(FileOf(temp) + ".bak"));
    }

    [Fact]
    public void AnEntryWithAnUnknownPropertyOrAMalformedIdentityIsRefused()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        string good = File.ReadAllText(FileOf(temp));
        foreach (string broken in new[]
        {
            good.Replace("\"Kind\":\"Image\"", "\"Kind\":\"Image\",\"Extra\":true"),
            good.Replace("\"Kind\":\"Image\"", "\"Kind\":\"Nothing\""),
            good.Replace("\"State\":\"Open\"", "\"State\":\"Done\""),
            good.Replace(new string('b', 64), new string('B', 64)),
            good.Replace("\"AddedAtUtc\":\"2026-11-14T08:00:01.0000000+00:00\"", "\"AddedAtUtc\":\"yesterday\""),
        })
        {
            Assert.NotEqual(good, broken);
            File.WriteAllText(FileOf(temp), broken);
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
    }

    [Fact]
    public void AnEntryHeldTwiceIsRefused()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        string good = File.ReadAllText(FileOf(temp));
        int open = good.IndexOf("\"Items\":[", StringComparison.Ordinal) + "\"Items\":[".Length;
        string item = good[open..good.LastIndexOf(']')];
        File.WriteAllText(FileOf(temp), good.Replace(item, item + "," + item));
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void InvalidUtf8IsRefusedNotReplaced()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        byte[] bytes = File.ReadAllBytes(FileOf(temp));
        int at = Array.IndexOf(bytes, (byte)'f');
        bytes[at] = 0xFF;
        File.WriteAllBytes(FileOf(temp), bytes);
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void GroupsAreNamedIndependentScopesWithPrivateStampsAndDeleteUnassigns()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 3));

        TagQueueSnapshot grouped = store.CreateGroup("Review", new[] { Item(1).Key, Item(2).Key });
        TagQueueGroup group = Assert.Single(grouped.Groups);
        Assert.Equal(2, grouped.CountFor(group.Id));
        Assert.Equal(group.Id, grouped.Items[0].GroupId);
        Assert.Equal(group.Id, grouped.Items[1].GroupId);
        Assert.Equal(0, grouped.ResumeIndex(group.Id));

        grouped = store.SetGroupStamp(group.Id, new[] { "needs-review" });
        Assert.Equal(new[] { "needs-review" }, grouped.Group(group.Id)!.Stamp);
        grouped = store.RenameGroup(group.Id, "Reviewed");
        Assert.Equal("Reviewed", grouped.Group(group.Id)!.Name);
        grouped = store.DeleteGroup(group.Id);
        Assert.Empty(grouped.Groups);
        Assert.All(grouped.Items, item => Assert.Null(item.GroupId));
        Assert.Contains("\"Version\":" + TagQueueStore.Version, File.ReadAllText(FileOf(temp)));
    }

    [Fact]
    public void VersionOneQueueRemainsReadableAndIsUpgradedOnTheNextWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(new[] { Item(1) });
        JsonObject root = JsonNode.Parse(File.ReadAllText(FileOf(temp)))!.AsObject();
        root["Version"] = TagQueueStore.LegacyVersion;
        root.Remove("Groups");
        foreach (JsonObject item in root["Items"]!.AsArray().OfType<JsonObject>()) item.Remove("GroupId");
        string original = root.ToJsonString();
        File.WriteAllText(FileOf(temp), original);

        TagQueueSnapshot legacy = store.Read();
        Assert.Equal(original, File.ReadAllText(FileOf(temp)));
        Assert.Empty(legacy.Groups);
        Assert.Null(legacy.Items[0].GroupId);
        store.SetStamp(new[] { "legacy" });
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(FileOf(temp)))!["Version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheFirstEditKeepsTheWalkAndItsPreviousRevisionInVersionTwo(int sourceVersion)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        string path = FileOf(temp);
        TagQueueItem first = Item(1) with { Position = 0 };
        store.Add([first, Item(2)]);
        string group = store.CreateGroup("Review", [first.Key]).Groups.Single().Id;
        store.SetGroupStamp(group, ["blue", "cat"]);
        store.SetStamp(["general"]);
        store.SetCursor(1);

        JsonObject expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(2, expected["Version"]!.GetValue<int>());
        Assert.All(expected["Items"]!.AsArray(), item => Assert.False(item!.AsObject().ContainsKey("AttachmentRevisionKey")));
        JsonObject input = expected.DeepClone().AsObject();
        input["Version"] = sourceVersion;
        if (sourceVersion == 3)
        {
            input["Items"]![0]!["AttachmentRevisionKey"] = "rev-1";
            input["Items"]![1]!["AttachmentRevisionKey"] = null;
        }
        string original = input.ToJsonString();
        File.WriteAllText(path, original);
        input["Revision"] = 0;
        string previousBackup = input.ToJsonString();
        File.WriteAllText(path + ".bak", previousBackup);

        TagQueueStore reopened = new(temp.Path, lease);
        TagQueueSnapshot loaded = reopened.Read();
        Assert.Equal(sourceVersion == 3 ? "rev-1" : null, loaded.Items[0].AttachmentRevisionKey);
        Assert.Equal(1, loaded.Cursor);
        Assert.Equal("general", Assert.Single(loaded.Stamp));
        TagQueueGroup review = Assert.Single(loaded.Groups);
        Assert.Equal((group, "Review", 0), (review.Id, review.Name, review.Order));
        Assert.Equal(["blue", "cat"], review.Stamp);
        Assert.Equal(group, loaded.Items[0].GroupId);
        Assert.Null(loaded.Items[1].GroupId);
        Assert.Same(loaded, reopened.SetCursor(1));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(previousBackup, File.ReadAllText(path + ".bak"));

        const string futureBackup = """{"Version":4}""";
        File.WriteAllText(path + ".bak", futureBackup);
        Assert.Throws<ProfileBackupBlockedException>(() => reopened.SetStates(new[] { Set(2, TagQueueState.Skipped) }, T0));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(futureBackup, File.ReadAllText(path + ".bak"));
        File.WriteAllText(path + ".bak", previousBackup);

        reopened.SetStates(new[] { Set(2, TagQueueState.Skipped) }, T0);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path + ".bak"))));
        expected["Items"]![1]!["State"] = "Skipped";
        expected["Revision"] = loaded.Revision + 1;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path))));
        Assert.Equal(2, new TagQueueStore(temp.Path, lease).SetCursor(2).Cursor);
    }

    [Fact]
    public void ARevisionKeyStaysInMemoryAndTheVersionTwoRoundTripKeepsTheIdentity()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        TagQueueItem keyed = Item(1) with { Position = 0, AttachmentRevisionKey = "rev-1" };
        store.Add(new[] { keyed });
        Assert.Equal("rev-1", store.Read().Items.Single().ToLibraryEntry().AttachmentRevisionKey);
        Assert.DoesNotContain("AttachmentRevisionKey", File.ReadAllText(FileOf(temp)));
        TagQueueItem back = new TagQueueStore(temp.Path, lease).Read().Items.Single();
        Assert.Equal(keyed with { AttachmentRevisionKey = null }, back);
        Assert.Equal(keyed.Key, back.Key);
        // A key without a post attachment is an invalid entry, as is an over-long one.
        Assert.NotNull((Item(2) with { AttachmentRevisionKey = "rev" }).Validate());
        Assert.NotNull((keyed with { AttachmentRevisionKey = new string('k', TagQueueItem.MaximumTokenCharacters + 1) }).Validate());
        Assert.Null(keyed.Validate());
    }

    [Fact]
    public void AnIndexEntryKeepsItsRevisionKeyWhenQueued()
    {
        MediaLibraryEntry entry = (Item(3) with { Position = 0 }).ToLibraryEntry() with { AttachmentRevisionKey = "rev-3" };
        Assert.Equal("rev-3", TagQueueItem.From(entry, T0)!.AttachmentRevisionKey);
    }

    [Fact]
    public void TheIndexResolvesTheKeyOfAnOldEntryOnlyForTheExactOccurrence()
    {
        TagQueueItem old = Item(4) with { Position = 0 };
        MediaLibraryEntry indexed = old.ToLibraryEntry() with { AttachmentRevisionKey = "rev-4" };
        Assert.Equal("rev-4", new TagQueueKeyResolver(new[] { indexed }).Resolve(old).AttachmentRevisionKey);
        // The same asset at another post, position or with other bytes, or a missing file, does not resolve it.
        foreach (MediaLibraryEntry other in new[]
        {
            indexed with { PostId = "other" }, indexed with { Position = 5 }, indexed with { Sha256 = new string('0', 64) },
            indexed with { Presence = MediaLibraryPresence.Missing }, indexed with { AttachmentRevisionKey = null }
        })
            Assert.Null(new TagQueueKeyResolver(new[] { other }).Resolve(old).AttachmentRevisionKey);
        // Two keys for one occurrence are ambiguous: nothing is chosen. A stored key always wins.
        Assert.Null(new TagQueueKeyResolver(new[] { indexed, indexed with { AttachmentRevisionKey = "rev-x" } }).Resolve(old).AttachmentRevisionKey);
        Assert.Equal("kept", new TagQueueKeyResolver(new[] { indexed }).Resolve(old with { AttachmentRevisionKey = "kept" }).AttachmentRevisionKey);
        // An entry without a post attachment (a loose or imported file) is never looked up.
        TagQueueItem loose = Item(5) with { PostId = null };
        Assert.Same(loose, new TagQueueKeyResolver(new[] { indexed }).Resolve(loose));
    }

    [Fact]
    public void GroupLimitsAreValidatedAndLoweringDoesNotHideExistingGroups()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.StoreLimits.TagQueueGroups = 1;
        TagQueueStore store = new(temp.Path, lease);
        store.Add(new[] { Item(1) });
        TagQueueSnapshot first = store.CreateGroup("First group");
        Assert.Throws<InvalidDataException>(() => store.CreateGroup("Second group"));
        lease.StoreLimits.TagQueueGroups = ProfileStoreLimits.MinimumTagQueueGroups;
        Assert.Single(store.Read().Groups);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.StoreLimits.TagQueueGroups = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.StoreLimits.TagQueueGroupNameCharacters = ProfileStoreLimits.MinimumTagQueueGroupNameCharacters - 1);
        Assert.Equal(first.Groups[0].Id, store.Read().Groups[0].Id);
    }

    [Fact]
    public void AFileOfANewerVersionReadsAsEmptyStaysUntouchedAndRefusesEveryWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string newer = "{\"Version\":4,\"Anything\":[1,2,3]}";
        File.WriteAllText(FileOf(temp), newer);
        TagQueueStore store = new(temp.Path, lease);
        TagQueueSnapshot read = store.Read();
        Assert.Equal((0, TagQueueLoadState.NewerVersion, false), (read.Count, read.State, read.CanWrite));
        Assert.Throws<TagQueueNewerVersionException>(() => store.Add(Items(1, 1)));
        Assert.Throws<TagQueueNewerVersionException>(() => store.SetStamp(new[] { "cat" }));
        Assert.Throws<TagQueueNewerVersionException>(() => store.Clear(finishedOnly: false));
        Assert.Equal(newer, File.ReadAllText(FileOf(temp)));
        Assert.False(File.Exists(FileOf(temp) + ".bak"));
    }

    [Fact]
    public void ABackupOfANewerVersionBlocksTheWriteAndStaysUntouched()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        File.WriteAllText(FileOf(temp) + ".bak", "{\"Version\":9}");
        byte[] primary = File.ReadAllBytes(FileOf(temp));
        Assert.Throws<ProfileBackupBlockedException>(() => store.Add(Items(2, 1)));
        Assert.Equal(primary, File.ReadAllBytes(FileOf(temp)));
        Assert.Equal("{\"Version\":9}", File.ReadAllText(FileOf(temp) + ".bak"));
    }

    [Fact]
    public void ADamagedBackupIsSetAsideSoTheGoodFileIsNeverLost()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        File.WriteAllText(FileOf(temp) + ".bak", "damaged");
        store.Add(Items(2, 1));
        Assert.Equal(2, store.Read().Count);
        Assert.Contains(Directory.GetFiles(temp.Path), path => Path.GetFileName(path).StartsWith(TagQueueStore.FileName + ".bak.", StringComparison.Ordinal)
            || Path.GetFileName(path).Contains(".bad", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFileWithABackupIsNotRestoredSilentlyButCanBeCleared()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        store.Add(Items(2, 1));
        File.Delete(FileOf(temp));
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.Add(Items(3, 1)));
        // Clearing starts over and still keeps the unreadable evidence next to the new file.
        Assert.Equal(0, store.Clear(finishedOnly: false).Count);
    }

    [Fact]
    public void ClearingADamagedFileKeepsTheDamagedBytesAndWritesAnEmptyQueue()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllText(FileOf(temp), "damaged bytes");
        TagQueueStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.Read());
        TagQueueSnapshot cleared = store.Clear(finishedOnly: false);
        Assert.Equal(0, cleared.Count);
        TagQueueSnapshot reopened = store.Read();
        Assert.Equal((cleared.Revision, 0), (reopened.Revision, reopened.Count));
        Assert.Contains(Directory.GetFiles(temp.Path, "tag-queue*"), path => File.ReadAllText(path) == "damaged bytes");
        // A damaged file cannot be partially cleared.
        File.WriteAllText(FileOf(temp), "damaged again");
        Assert.Throws<InvalidDataException>(() => store.Clear(finishedOnly: true));
    }

    [Fact]
    public void ALinkInPlaceOfTheFileIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string target = Path.Combine(temp.Path, "elsewhere.json");
        File.WriteAllText(target, "{\"Version\":1,\"Revision\":0,\"Cursor\":0,\"Stamp\":[],\"Items\":[]}");
        File.CreateSymbolicLink(FileOf(temp), target);
        TagQueueStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.Add(Items(1, 1)));
    }

    [Fact]
    public void ACancelledWriteLeavesTheFileAsItWas()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        byte[] before = File.ReadAllBytes(FileOf(temp));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Add(Items(2, 1), cancelled.Token));
        Assert.Equal(before, File.ReadAllBytes(FileOf(temp)));
    }

    [Fact]
    public void AStoreForAnotherProfileDirectoryIsRefused()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Assert.ThrowsAny<Exception>(() => new TagQueueStore(other.Path, lease));
    }

    [Fact]
    public void AnEntryIsDescribedByTheSameFieldsTheGalleryResolverUses()
    {
        TagQueueItem item = Item(7, post: "700") with { Position = 1, Historical = true, LocalSlot = "slot-1" };
        MediaLibraryEntry entry = item.ToLibraryEntry();
        Assert.Equal((item.Thread, item.Directory, item.AssetId, item.PostId, item.Position), (entry.Thread, entry.Directory, entry.AssetId, entry.PostId, entry.Position));
        Assert.Equal((MediaLibraryPresence.Present, item.Sha256, item.Length, item.MediaId, item.Variant, true, "slot-1"),
            (entry.Presence, entry.Sha256, entry.Length, entry.MediaId, entry.Variant, entry.Historical, entry.LocalSlot));
        TagQueueItem back = TagQueueItem.From(entry, T0.AddSeconds(7))!;
        Assert.Equal(item.Key, back.Key);
        Assert.Equal(item, back);
    }

    [Fact]
    public void AnIndexEntryWithoutASavedFileIsNotQueueable()
    {
        MediaLibraryEntry present = Item(1).ToLibraryEntry();
        Assert.NotNull(TagQueueItem.From(present, T0));
        Assert.Null(TagQueueItem.From(present with { Presence = MediaLibraryPresence.Missing }, T0));
        Assert.Null(TagQueueItem.From(present with { Directory = null }, T0));
        Assert.Null(TagQueueItem.From(present with { AssetId = null }, T0));
        Assert.Null(TagQueueItem.From(present with { Sha256 = null }, T0));
    }

    [Fact]
    public void ANameIsCleanedAndCutWithoutSplittingASurrogatePair()
    {
        Assert.Equal("ab", TagQueueItem.CleanName("a\nb\u0000"));
        string cut = TagQueueItem.CleanName(new string('x', TagQueueItem.MaximumNameCharacters - 1) + "\U0001F600");
        Assert.Equal(TagQueueItem.MaximumNameCharacters - 1, cut.Length);
        Assert.Equal(string.Empty, TagQueueItem.CleanName(null));
    }

    [Fact]
    public void TheDecisionLogKeepsTheNewestStepsAndForgetsRemovedEntries()
    {
        TagQueueDecision Decision(int n) => new(Item(n).Key, TagQueueState.Open, TagQueueState.Done, n, n + 1);
        TagQueueDecisionLog log = new(3);
        for (int n = 1; n <= 5; n++) log.Push(Decision(n));
        Assert.Equal(3, log.Count);
        Assert.Equal(Decision(5), log.Last);
        log.Forget(new[] { Item(4).Key });
        Assert.Equal(Decision(5), log.Pop());
        Assert.Equal(Decision(3), log.Pop());
        Assert.Null(log.Pop());
        log.Push(Decision(1));
        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TagQueueDecisionLog(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TagQueueDecisionLog(TagQueueDecisionLog.MaximumDepth + 1));
        Assert.Equal(TagQueueDecisionLog.DefaultDepth, new TagQueueDecisionLog().Depth);
    }

    // W3-01 / W3-18: an undo names its decision and removes it only after the write; the depth is a setting that trims at once.
    [Fact]
    public void TheDecisionLogFindsExactlyTheNamedDecisionRemovesItOnlyOnRequestAndFollowsALoweredDepth()
    {
        TagQueueDecision Decision(int n) => new(Item(n).Key, TagQueueState.Open, TagQueueState.Done, n, n + 1, Item(n).Key);
        TagQueueDecisionLog log = new(5);
        TagQueueDecision[] pushed = Enumerable.Range(1, 5).Select(Decision).ToArray();
        foreach (TagQueueDecision decision in pushed) log.Push(decision);
        Assert.Same(pushed[4], log.Find());
        Assert.Same(pushed[2], log.Find(pushed[2]));
        Assert.Equal(5, log.Count); // finding never removes
        Assert.True(log.Remove(pushed[4]));
        Assert.False(log.Remove(pushed[4]));
        Assert.Null(log.Find(pushed[4]));
        log.Depth = 2;
        Assert.Equal(new[] { pushed[2], pushed[3] }, new[] { log.Find(pushed[2]), log.Find(pushed[3]) });
        Assert.Null(log.Find(pushed[1])); // fell out with the lowered depth
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Depth = TagQueueDecisionLog.MaximumDepth + 1);

        TagQueueSnapshot onlyFour = new(1, 0, Array.Empty<string>(), new[] { Item(4) }, TagQueueLoadState.Ready);
        log.ForgetMissing(onlyFour);
        Assert.Equal(1, log.Count);
        Assert.Same(pushed[3], log.Last);
    }

    // W3-04: the undo's resume point is the entry the cursor stood on, wherever it stands now.
    [Fact]
    public void AnUndoFindsTheOldResumePointAgainAfterEntriesBeforeItLeftTheQueue()
    {
        TagQueueItem[] all = Items(1, 7).ToArray();
        TagQueueDecision decision = new(all[5].Key, TagQueueState.Open, TagQueueState.Done, 5, 6, all[5].Key);
        TagQueueSnapshot withoutThird = new(2, 5, Array.Empty<string>(), all.Where((_, i) => i != 2).ToArray(), TagQueueLoadState.Ready);
        Assert.Equal(TagQueueCursorTarget.Relative(all[5].Key, 0), decision.CursorTargetIn(withoutThird));

        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(all);
        store.SetStates(new[] { Set(6, TagQueueState.Done) }, T0, TagQueueCursorTarget.Relative(Item(6).Key, 1));
        store.Remove(new[] { Item(3).Key });
        TagQueueSnapshot undone = store.SetStates(new[] { Set(6, TagQueueState.Open) }, T0, decision.CursorTargetIn(store.Read()));
        Assert.Equal(4, undone.Cursor);
        Assert.Equal(Item(6).Key, undone.Items[undone.Cursor].Key);

        // Without the anchor (it left the queue, or the cursor stood behind the last entry) the old position counts, clamped.
        TagQueueDecision behind = new(all[6].Key, TagQueueState.Open, TagQueueState.Done, 7, 7);
        Assert.Equal(TagQueueCursorTarget.At(6), behind.CursorTargetIn(withoutThird));
    }

    // W3-23: only the names the writer uses are states and kinds; numbers and flag lists are refused, never read.
    [Fact]
    public void ANumericOrCombinedStateOrKindIsRefused()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        string good = File.ReadAllText(FileOf(temp));
        foreach (string broken in new[]
        {
            good.Replace("\"State\":\"Open\"", "\"State\":\"1\""),
            good.Replace("\"State\":\"Open\"", "\"State\":\"0\""),
            good.Replace("\"Kind\":\"Image\"", "\"Kind\":\"0\""),
            good.Replace("\"State\":\"Open\"", "\"State\":\"open\""),
            good.Replace("\"State\":\"Open\"", "\"State\":\" Open\""),
            good.Replace("\"State\":\"Open\"", "\"State\":\"Open, Skipped\""),
        })
        {
            Assert.NotEqual(good, broken);
            File.WriteAllText(FileOf(temp), broken);
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
        File.WriteAllText(FileOf(temp), good);
        Assert.Equal(TagQueueState.Open, store.Read().Items[0].State);
    }

    // W3-24: a decision on an unchanged file parses neither the file nor its backup again; a file or backup changed by anything else is
    // compared in full, parsed and refused like before.
    [Fact]
    public void AnUnchangedFileIsNotParsedAgainForEveryDecisionButAChangedOneIs()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 50));
        store.SetStates(new[] { Set(1, TagQueueState.Done) }, T0, TagQueueCursorTarget.Relative(Item(1).Key, 1));
        Assert.True(File.Exists(FileOf(temp) + ".bak"));
        int parses = store.ParsesForTests;
        for (int n = 2; n <= 21; n++)
            store.SetStates(new[] { Set(n, TagQueueState.Done) }, T0.AddSeconds(n), TagQueueCursorTarget.Relative(Item(n).Key, 1));
        Assert.Equal(21, store.Read().Done);
        Assert.Equal(parses, store.ParsesForTests);

        string good = File.ReadAllText(FileOf(temp));
        File.WriteAllText(FileOf(temp), good.Replace("\"State\":\"Skipped\"", "x").Replace("\"State\":\"Open\"", "\"State\":\"Nope\""));
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.WriteAllText(FileOf(temp), good);
        Assert.Equal(21, store.Read().Done);
        Assert.True(store.ParsesForTests > parses);

        File.WriteAllText(FileOf(temp) + ".bak", "{\"Version\":99}");
        Assert.Throws<ProfileBackupBlockedException>(() => store.SetCursor(0));
        Assert.Equal(good, File.ReadAllText(FileOf(temp)));
    }

    // W3-22: clearing a damaged file keeps exactly one copy of the damaged bytes.
    [Fact]
    public void ClearingADamagedFileKeepsExactlyOneCopyOfTheDamagedBytes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllText(FileOf(temp), "damaged bytes");
        TagQueueStore store = new(temp.Path, lease);
        Assert.Equal(0, store.Clear(finishedOnly: false).Count);
        string copy = Assert.Single(Directory.GetFiles(temp.Path, TagQueueStore.FileName + ".corrupt-*"));
        Assert.Equal("damaged bytes", File.ReadAllText(copy));
    }

    // W3-21: an unreadable file is told apart by its type, not by its message.
    [Fact]
    public void AFileThatCannotBeOpenedIsAReadErrorOfItsOwnType()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagQueueStore store = new(temp.Path, lease);
        store.Add(Items(1, 1));
        File.SetUnixFileMode(FileOf(temp), UnixFileMode.None);
        try
        {
            try { using (File.OpenRead(FileOf(temp))) { } return; } // running with rights that ignore the mode: nothing to check
            catch (UnauthorizedAccessException) { }
            TagQueueReadException error = Assert.Throws<TagQueueReadException>(() => store.Read());
            Assert.IsAssignableFrom<IOException>(error);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        }
        finally { File.SetUnixFileMode(FileOf(temp), UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }
}
