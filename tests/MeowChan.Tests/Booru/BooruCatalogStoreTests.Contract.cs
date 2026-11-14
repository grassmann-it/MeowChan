using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

public sealed partial class BooruCatalogStoreTests
{
    private static readonly DateTimeOffset ContractTime = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("future-rating")]
    public void ObservedProviderTextCanBeNullOrEmptyAndClearsEarlierClaimsLosslessly(string? text)
    {
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            store.UpsertPost(CompletePost("123") with { RawRating = "s" }, ContractTime);
            BooruPostClaim incoming = new("danbooru", "123",
                BooruPostClaimPresence.Rating | BooruPostClaimPresence.SourceClaim,
                RawRating: text, SourceClaim: text);
            store.UpsertPost(incoming, ContractTime.AddMinutes(1));
            // Absence is not an observation: a still thinner response must retain the explicit empty/null value.
            store.UpsertPost(new("danbooru", "123", BooruPostClaimPresence.None), ContractTime.AddMinutes(2));
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruPostClaim actual = Assert.Single(new BooruCatalogStore(directory.Path, reopened).Read().Posts).Post;
        BooruPostClaim expected = CompletePost("123") with
        {
            Rating = BooruPostClaimRating.Unknown, RawRating = text, SourceClaim = text
        };
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(BooruPostClaimPresence.All, actual.Fields);
    }

    [Theory]
    [InlineData("rawRating")]
    [InlineData("sourceClaim")]
    public void ProviderTextStillRejectsControlsAndOversizeWithoutChangingTheCatalog(string field)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(CompletePost("123"), ContractTime);
        byte[] before = File.ReadAllBytes(store.FilePath);
        foreach (string invalid in new[] { "text\ncontrol", new string('x', BooruCatalogStore.MaximumClaimTextLength + 1) })
        {
            BooruPostClaim claim = CompletePost("123");
            claim = field == "rawRating" ? claim with { RawRating = invalid } : claim with { SourceClaim = invalid };
            Assert.Throws<ArgumentException>(() => store.UpsertPost(claim, ContractTime));
            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        }
    }

    [Theory]
    [InlineData("state", "5")]
    [InlineData("state", "Active, Removed")]
    [InlineData("state", " Removed ")]
    [InlineData("resumeState", "1")]
    [InlineData("resumeState", "Active, Paused")]
    [InlineData("resumeState", " Paused ")]
    public void NonCanonicalWatcherStatesFailClosedOnWriteAndOfflineRead(string property, string alias)
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            BooruWatcherRecord valid = new("w-terminal", "danbooru", "cat", "Removed", ContractTime,
                ResumeState: "Paused", PageSize: 2, PageBudget: 2);
            store.UpsertWatcher(valid);
            byte[] before = File.ReadAllBytes(path);
            BooruWatcherRecord invalid = property == "state" ? valid with { State = alias } : valid with { ResumeState = alias };
            // Use a new id so the existing terminal-state guard cannot mask the validation gap.
            Assert.Throws<ArgumentException>(() => store.UpsertWatcher(invalid with { WatcherId = "w-alias" }));
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["watchers"]![0]![property] = alias;
        File.WriteAllText(path, root.ToJsonString());
        byte[] damaged = File.ReadAllBytes(path);
        using ProfileLease restarted = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore offline = new(directory.Path, restarted);
        Assert.Throws<InvalidDataException>(() => offline.Read());
        Assert.Throws<InvalidDataException>(() => offline.UpsertPost(Post("124"), ContractTime));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
    }

    [Theory]
    [InlineData("findings", "[null]")]
    [InlineData("tags", "[null]")]
    [InlineData("media", "[null]")]
    [InlineData("knownPostIds", "[null]")]
    [InlineData("sourceClaim", "\"\\uD800\"")]
    [InlineData("sourceClaim", "\"\\uDC00\"")]
    [InlineData("sourceClaim", "{}")]
    [InlineData("originalIntent", "null")]
    [InlineData("score", "1e999")]
    public void MalformedNestedValuesCannotBeReadOrOverwritten(string property, string rawValue)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteRepresentativeCatalog(store);
        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        JsonNode owner = property switch
        {
            "knownPostIds" => root["watchers"]![0]!,
            "findings" or "originalIntent" => root["posts"]![0]!,
            _ => root["posts"]![0]!["post"]!
        };
        // Insert raw JSON separately so unpaired surrogate escapes are not normalized by JsonNode's writer.
        owner[property] = "__malformed_fixture__";
        string damagedJson = root.ToJsonString().Replace("\"__malformed_fixture__\"", rawValue, StringComparison.Ordinal);
        File.WriteAllText(store.FilePath, damagedJson);
        byte[] damaged = File.ReadAllBytes(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.UpsertPost(Post("124"), ContractTime));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
        Assert.Equal(damaged, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void EveryPostOriginalAndFindingFieldRoundTripsWithoutRewritingOtherProfileFiles()
    {
        using TempDirectory directory = new();
        // These bytes are deliberately opaque to B3: migration must not parse or rewrite other stores.
        string[] legacyNames = ["saved_urls.json", "archive.db", "private-tags.json", "settings.json"];
        byte[] legacyBytes = [0, 255, 123, 10, 128, 1];
        foreach (string name in legacyNames) File.WriteAllBytes(Path.Combine(directory.Path, name), legacyBytes);
        BooruPostClaim claim = CompletePost("123") with
        {
            RawRating = "s", IsDeleted = true, IsBanned = true,
            Tags = Enum.GetValues<BooruPostClaimTagCategory>().Select(category =>
                new BooruPostClaimTag("provider_" + category, category)).ToArray(),
            Media = Enum.GetValues<BooruPostClaimMediaRole>().Select(role =>
                new BooruPostClaimMedia(role, $"https://cdn.donmai.us/{role}/synthetic.jpg", ".jpg",
                    1234567890123, 640, 480, "ABEiM0RVZneImaq7zN3u/w==")).ToArray()
        };
        BooruPostRecord[] expected;
        byte[] catalogBytes;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            Assert.Empty(store.Read().Posts);
            Assert.False(File.Exists(store.FilePath));
            foreach (BooruOriginalIntent intent in Enum.GetValues<BooruOriginalIntent>())
            {
                string id = (123 + (int)intent).ToString(System.Globalization.CultureInfo.InvariantCulture);
                BooruPostClaim next = claim with { PostId = id, PostUrl = $"https://danbooru.donmai.us/posts/{id}" };
                store.RecordObservation(next, ContractTime, BooruFindingKind.Search, null, "cat");
                store.AddFinding("danbooru", id, BooruFindingKind.OneShot, null, null, ContractTime.AddSeconds(1));
                store.AddFinding("danbooru", id, BooruFindingKind.Watcher, "w1", "cat", ContractTime.AddSeconds(2));
                if (intent == BooruOriginalIntent.Linked)
                    store.LinkLocalAsset("danbooru", id, "synthetic-asset", "/offline/synthetic.jpg");
                else
                    store.SetOriginalIntent("danbooru", id, intent);
            }
            expected = store.Read().Posts.ToArray();
            Assert.All(expected, record =>
            {
                Assert.Equal(JsonSerializer.Serialize(claim with { PostId = record.PostId, PostUrl = record.Post.PostUrl }),
                    JsonSerializer.Serialize(record.Post));
                Assert.Equal(3, record.Findings.Count);
                Assert.Equal(ContractTime, record.FirstSeenAtUtc);
                Assert.Equal(ContractTime.AddSeconds(2), record.LastSeenAtUtc);
            });
            catalogBytes = File.ReadAllBytes(store.FilePath);
        }

        using ProfileLease restarted = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore offline = new(directory.Path, restarted);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(offline.Read().Posts));
        Assert.Equal(catalogBytes, File.ReadAllBytes(offline.FilePath));
        foreach (string name in legacyNames) Assert.Equal(legacyBytes, File.ReadAllBytes(Path.Combine(directory.Path, name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionMapperKeepsEveryWatcherFieldAcrossPausedCooldownAndBudgetRestarts(bool emptyBaseline)
    {
        using TempDirectory directory = new();
        Assert.True(DanbooruWatcher.TryCreate("w-production", "cat", ContractTime, out DanbooruWatcher? created, out _, 2, 2));
        DanbooruWatcher watcher = created!;
        void PersistAndRestart()
        {
            BooruWatcherRecord expected = DanbooruWatcherRecordMapper.ToRecord(watcher);
            byte[] written;
            using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
            {
                BooruCatalogStore store = new(directory.Path, lease);
                store.UpsertWatcher(expected);
                written = File.ReadAllBytes(store.FilePath);
            }
            using ProfileLease restarted = ProfileLease.Acquire(directory.Path);
            BooruCatalogStore offline = new(directory.Path, restarted);
            BooruWatcherRecord saved = Assert.Single(offline.Read().Watchers);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(saved));
            DanbooruWatcher restored = DanbooruWatcherRecordMapper.FromRecord(saved);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(DanbooruWatcherRecordMapper.ToRecord(restored)));
            Assert.Equal(written, File.ReadAllBytes(offline.FilePath));
            watcher = restored;
        }
        void Apply(int ordinal, bool end, params string[] ids)
        {
            DanbooruWatcherTransition result = DanbooruWatcher.ApplyPage(watcher, new("danbooru", "cat", ordinal, ids, end), ContractTime);
            Assert.True(result.Accepted);
            watcher = result.Watcher;
            PersistAndRestart();
        }
        PersistAndRestart();
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime).Watcher;
        Apply(1, true, emptyBaseline ? [] : ["100"]);
        watcher = DanbooruWatcher.Pause(watcher).Watcher;
        PersistAndRestart();
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(1), manual: true).Watcher;
        PersistAndRestart();
        Apply(1, false, "120", "119");
        Assert.Equal(1, watcher.Progress.PagesCheckedInRun);
        watcher = DanbooruWatcher.ApplyDeferred(watcher, ContractTime.AddMinutes(5), "rate_limited", ContractTime.AddMinutes(1)).Watcher;
        PersistAndRestart();
        Assert.Equal(DanbooruWatcherState.Paused, watcher.ResumeState);
        Assert.False(DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(2), manual: true).Accepted);
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(5), manual: true).Watcher;
        Apply(2, false, "118", "117");
        Apply(3, false, "116", "115");
        Assert.True(watcher.Progress.BudgetLimited);
        Assert.Equal(DanbooruWatcherState.Paused, watcher.State);
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(6), manual: true).Watcher;
        watcher = DanbooruWatcher.ApplyError(watcher, "synthetic_error", ContractTime.AddMinutes(6)).Watcher;
        PersistAndRestart();
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(7), manual: true).Watcher;
        PersistAndRestart();
        watcher = DanbooruWatcher.RecoverAfterRestart(watcher);
        Assert.Equal(DanbooruWatcherState.Paused, watcher.State);
        Assert.Equal("115", watcher.CursorPostId);
        Assert.Equal(emptyBaseline ? null : "100", watcher.CheckStartHighWaterMark);
        watcher = DanbooruWatcher.BeginCheck(watcher, ContractTime.AddMinutes(8), manual: true).Watcher;
        Apply(4, true, "114", "100");
        Assert.Equal(0, watcher.Progress.NextPage);
        watcher = DanbooruWatcher.Remove(watcher).Watcher;
        PersistAndRestart();
        Assert.Equal(DanbooruWatcherState.Removed, watcher.State);
    }
}
