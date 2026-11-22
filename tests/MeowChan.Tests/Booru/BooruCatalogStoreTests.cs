using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

public sealed partial class BooruCatalogStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public void ClaimsFindingsWatchersAndAssetLinksSurviveAProfileRestart()
    {
        using TempDirectory directory = new();
        DateTimeOffset observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            BooruPostClaim post = CompletePost("123");
            store.UpsertPost(post, observed);
            store.AddFinding("danbooru", "123", BooruFindingKind.Search, null, "cat", observed);
            store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "cat", observed);
            store.LinkLocalAsset("danbooru", "123", "asset-1", "/offline/asset.png");
            // A partial traversal must carry its full restart contract (see BooruWatcherRestartContractTests).
            store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", observed,
                HighWaterMark: "123", NextPage: 2, BaselineEstablished: true, KnownPostIds: ["123"],
                CursorBeforePostId: "123", CheckStartHighWaterMark: "100", CheckStartHighWaterMarkCaptured: true,
                ResumeState: "Active", PageSize: 20, PageBudget: 5));
        }

        using ProfileLease reopenedLease = ProfileLease.Acquire(directory.Path);
        BooruCatalogSnapshot snapshot = new BooruCatalogStore(directory.Path, reopenedLease).Read();
        BooruPostRecord saved = Assert.Single(snapshot.Posts);
        Assert.Equal("asset-1", saved.LocalAssetId);
        Assert.Equal("/offline/asset.png", saved.LocalPath);
        Assert.Equal(2, saved.Findings.Count);
        Assert.Equal(BooruPostClaimRating.Sensitive, saved.Post.Rating);
        Assert.Equal(7, saved.Post.Score);
        Assert.Equal(3, saved.Post.FavoriteCount);
        Assert.Equal("https://source.example/post/123", saved.Post.SourceClaim);
        Assert.Equal(2, saved.Post.Tags.Count);
        Assert.Equal(3, saved.Post.Media.Count);
        Assert.Equal(BooruPostClaimMediaRole.Original, saved.Post.Media[0].Role);
        Assert.Equal(BooruPostClaimMediaRole.Sample, saved.Post.Media[1].Role);
        Assert.Equal(BooruPostClaimMediaRole.Preview, saved.Post.Media[2].Role);
        BooruWatcherRecord savedWatcher = Assert.Single(snapshot.Watchers);
        Assert.Equal("123", savedWatcher.KnownPostIds.Single());
        Assert.Equal(2, savedWatcher.NextPage);
        Assert.Equal("123", savedWatcher.CursorBeforePostId);
        Assert.Equal("100", savedWatcher.CheckStartHighWaterMark);
        Assert.Equal(BooruCatalogSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void RepeatedFindingsAndPostsAreIdempotentAndProviderTagsStayInThePostClaim()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        BooruPostClaim post = Post("7") with
        {
            Tags = [new BooruPostClaimTag("artist_name", BooruPostClaimTagCategory.Artist)]
        };
        store.UpsertPost(post, DateTimeOffset.UtcNow);
        store.AddFinding("danbooru", "7", BooruFindingKind.OneShot, null, null, DateTimeOffset.UtcNow);
        store.AddFinding("danbooru", "7", BooruFindingKind.OneShot, null, null, DateTimeOffset.UtcNow);
        BooruPostRecord saved = Assert.Single(store.Read().Posts);
        Assert.Single(saved.Findings);
        Assert.Equal(BooruPostClaimTagCategory.Artist, Assert.Single(saved.Post.Tags).Category);
        Assert.DoesNotContain(saved.Post.Tags, tag => tag.Name == "artist_name" && tag.Category == BooruPostClaimTagCategory.Unknown);
    }

    [Fact]
    public void LaterPartialClaimsRetainPreviouslyObservedProviderFields()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset first = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        DateTimeOffset later = first.AddMinutes(1);

        store.UpsertPost(CompletePost("16"), first);
        // A search result may only carry a tag field. It must update that
        // explicitly observed field without erasing richer single-post data.
        store.UpsertPost(Post("16") with
        {
            Tags = [new BooruPostClaimTag("updated_tag", BooruPostClaimTagCategory.General)]
        }, later);

        BooruPostClaim saved = Assert.Single(store.Read().Posts).Post;
        Assert.Equal(BooruPostClaimPresence.All, saved.Fields);
        Assert.Equal(BooruPostClaimRating.Sensitive, saved.Rating);
        Assert.Equal(7, saved.Score);
        Assert.Equal("updated_tag", Assert.Single(saved.Tags).Name);
        Assert.Equal(3, saved.Media.Count);
        Assert.Equal("https://source.example/post/123", saved.SourceClaim);
    }

    [Fact]
    public void RepeatedPartialClaimsForANonDefaultInstanceSurviveRestart()
    {
        using TempDirectory directory = new();
        DateTimeOffset first = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        BooruPostClaim tags = Post("42") with
        {
            InstanceId = "test-instance",
            Tags = [new("scoped_tag", BooruPostClaimTagCategory.General)]
        };
        BooruPostClaim score = new(tags.SourceId, tags.PostId, BooruPostClaimPresence.Score,
            Score: 19, InstanceId: tags.InstanceId);

        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            store.UpsertPost(tags, first);
            store.UpsertPost(score, first.AddMinutes(1));
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruPostRecord saved = Assert.Single(new BooruCatalogStore(directory.Path, reopened).Read().Posts);
        Assert.Equal(tags.Identity, saved.Identity);
        Assert.Equal(tags.Identity, saved.Post.Identity);
        Assert.Equal(tags.Fields | score.Fields, saved.Post.Fields);
        Assert.Equal(Assert.Single(tags.Tags), Assert.Single(saved.Post.Tags));
        Assert.Equal(19, saved.Post.Score);
    }

    [Fact]
    public void OutOfOrderObservationTimesKeepAStableObservationRange()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset first = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        DateTimeOffset earlier = first.AddMinutes(-5);
        DateTimeOffset earliest = first.AddMinutes(-10);
        DateTimeOffset later = first.AddMinutes(5);

        store.UpsertPost(Post("17"), first);
        store.AddFinding("danbooru", "17", BooruFindingKind.Search, null, "cat", earlier);
        store.AddFinding("danbooru", "17", BooruFindingKind.Search, null, "cat", earliest);
        store.AddFinding("danbooru", "17", BooruFindingKind.OneShot, null, null, later);

        BooruPostRecord saved = Assert.Single(store.Read().Posts);
        Assert.Equal(earliest, saved.FirstSeenAtUtc);
        Assert.Equal(later, saved.LastSeenAtUtc);
    }

    [Fact]
    public void RemovingWatcherKeepsWatcherClaimsAndLocalPost()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("9"), DateTimeOffset.UtcNow);
        store.AddFinding("danbooru", "9", BooruFindingKind.Watcher, "w1", "cat", DateTimeOffset.UtcNow);
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", DateTimeOffset.UtcNow));
        Assert.True(store.MarkWatcherRemoved("w1"));
        BooruCatalogSnapshot snapshot = store.Read();
        Assert.Equal("Removed", Assert.Single(snapshot.Watchers).State);
        Assert.Single(Assert.Single(snapshot.Posts).Findings);
    }

    [Fact]
    public void ProviderFieldsArePresenceSensitiveAndInvalidClaimsAreRejected()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);

        Assert.Throws<ArgumentException>(() => store.UpsertPost(Post("01"), DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            Post("10") with { Score = 1 }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            Post("11") with { Rating = (BooruPostClaimRating)99 }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            Post("12") with { Tags = [new BooruPostClaimTag("bad tag", BooruPostClaimTagCategory.General)] }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            CompletePost("13") with
            {
                Media = [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original,
                    "http://cdn.donmai.us/original/a.jpg")]
            }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            CompletePost("14") with
            {
                Media = [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original,
                    "https://cdn.donmai.us/original/a.jpg", ".jpg", ExpectedMd5: "not-md5")]
            }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => store.UpsertPost(
            CompletePost("15") with
            {
                Media = [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original,
                    "https://cdn.donmai.us/original/a.jpg"),
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Original,
                        "https://cdn.donmai.us/original/b.jpg")]
            }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WatchersRequireCanonicalQueriesAndPersistAllRestartState()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w-invalid", "danbooru", "cat  dog", "Active", now)));
        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w-invalid-state", "danbooru", "cat", "Unknown", now)));
        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w-invalid-cooldown", "danbooru", "cat", "Cooldown", now)));
        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w-invalid-id", "danbooru", "cat", "Active", now,
            KnownPostIds: ["01"])));

        BooruWatcherRecord watcher = new("w1", "danbooru", "-cat rating:g", "Cooldown", now,
            LastCheckedAtUtc: now.AddMinutes(-1), CooldownUntilUtc: now.AddMinutes(1), ErrorCode: "rate_limited",
            HighWaterMark: "100", NextPage: 6, BaselineEstablished: true, KnownPostIds: ["7", "100"],
            CursorBeforePostId: "50", CheckStartHighWaterMark: "40", CheckStartHighWaterMarkCaptured: true,
            ResumeState: "Active", PageSize: 20, PageBudget: 5);
        store.UpsertWatcher(watcher);
        BooruWatcherRecord saved = Assert.Single(store.Read().Watchers);
        Assert.Equal("-cat rating:g", saved.Query);
        Assert.Equal("Cooldown", saved.State);
        Assert.Equal(6, saved.NextPage);
        Assert.Equal(["7", "100"], saved.KnownPostIds);
        Assert.Equal("100", saved.HighWaterMark);
        Assert.Equal("50", saved.CursorBeforePostId);
        Assert.Equal("40", saved.CheckStartHighWaterMark);
        Assert.Equal("Active", saved.ResumeState);
        Assert.Equal(20, saved.PageSize);
        Assert.Equal(5, saved.PageBudget);
        Assert.Equal(0, saved.PagesCheckedInRun);
        Assert.False(saved.BudgetLimited);
    }

    [Fact]
    public void MutationsUseTheProfileAdmissionLifecycleAndReadsNeedTheLiveLease()
    {
        using TempDirectory directory = new();
        ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        int before = 0;
        int after = 0;
        store.BeforeAdmittedMutationForTests = _ => before++;
        store.AfterAdmittedMutationForTests = _ => after++;

        store.UpsertPost(Post("20"), DateTimeOffset.UtcNow);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        lease.Admission.CompleteStartup();
        store.UpsertPost(Post("20") with { Score = null }, DateTimeOffset.UtcNow);
        Assert.Equal(2, before);
        Assert.Equal(2, after);

        lease.Admission.BeginShutdown();
        Assert.Throws<InvalidOperationException>(() => store.UpsertPost(Post("21"), DateTimeOffset.UtcNow));
        lease.Admission.CompleteShutdown();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Read());
    }

    [Fact]
    public void StoreRejectsAForeignProfileLease()
    {
        using TempDirectory left = new();
        using TempDirectory right = new();
        using ProfileLease lease = ProfileLease.Acquire(left.Path);
        Assert.Throws<ArgumentException>(() => new BooruCatalogStore(right.Path, lease));
    }

    [Fact]
    public void MalformedAndFutureCatalogsFailClosed()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        File.WriteAllText(path, "{");
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());

        File.WriteAllText(path, Json(new BooruCatalogSnapshot(99, [], [])));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
    }

    [Theory]
    [InlineData("schemaVersion", "99", "1")]
    [InlineData("posts", "[null]", "[]")]
    [InlineData("state", "\"Removed\"", "\"Active\"")]
    [InlineData("originalIntent", "\"Cancelled\"", "\"Requested\"")]
    [InlineData("score", "-1", "7")]
    public void DuplicateJsonPropertiesFailClosedWithoutOverwritingCatalog(
        string property, string firstValue, string lastValue)
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        DateTimeOffset observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        BooruPostRecord post = new("danbooru", "30", CompletePost("30"), observed, observed,
            OriginalIntent: BooruOriginalIntent.Requested);
        BooruWatcherRecord watcher = new("w1", "danbooru", "cat", "Active", observed);
        string json = Json(new BooruCatalogSnapshot(1, [post], [watcher]));
        string member = $"\"{property}\":{lastValue}";
        if (property == "posts")
            json = json.Replace("\"posts\":[", $"\"posts\":{firstValue},\"posts\":[", StringComparison.Ordinal);
        else
        {
            Assert.Contains(member, json);
            json = json.Replace(member, $"\"{property}\":{firstValue},{member}", StringComparison.Ordinal);
        }
        File.WriteAllText(path, json);
        byte[] original = File.ReadAllBytes(path);

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.UpsertPost(Post("31"), observed));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path),
            file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedCatalogFailsClosedBeforeDeserialization()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        // Sparse: the length check runs before any byte is read or parsed.
        using (FileStream file = File.Create(path)) file.SetLength(BooruCatalogStore.MaximumDocumentBytes + 1L);
        BooruCatalogStore store = new(directory.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Equal(0, store.Counters.FullReads);
    }

    [Fact]
    public void DuplicatePostFindingAndWatcherIdentitiesFailClosed()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        BooruPostRecord post = new("danbooru", "30", Post("30"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        File.WriteAllText(path, Json(new BooruCatalogSnapshot(1, [post, post], [])));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());

        BooruFinding finding = new("danbooru|30|Search||cat", BooruFindingKind.Search, null, "cat", DateTimeOffset.UtcNow);
        BooruPostRecord withDuplicateFinding = post with { Findings = [finding, finding] };
        File.WriteAllText(path, Json(new BooruCatalogSnapshot(1, [withDuplicateFinding], [])));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());

        BooruWatcherRecord watcher = new("w1", "danbooru", "cat", "Active", DateTimeOffset.UtcNow);
        File.WriteAllText(path, Json(new BooruCatalogSnapshot(1, [post], [watcher, watcher])));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
    }

    [Fact]
    public void NewBooruFileIsAnAdditiveOfflineMigrationForAnExistingProfile()
    {
        using TempDirectory directory = new();
        string legacyPath = Path.Combine(directory.Path, "saved_urls.json");
        File.WriteAllText(legacyPath, "legacy-session-content");
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        Assert.Empty(store.Read().Posts);
        store.UpsertPost(Post("40"), DateTimeOffset.UtcNow);
        Assert.Equal("legacy-session-content", File.ReadAllText(legacyPath));
        Assert.True(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":99,\"posts\":[],\"watchers\":[]}")]
    public async Task PreparedRestoreFailsClosedForMalformedOrFutureBooruCatalog(string contents)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, BooruCatalogStore.FileName), contents);
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024),
            directory.Path, TimeSpan.FromHours(1), 3600, persistSession: false);

        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RestoreAdmittedAsync(startup));

        lease.Admission.FailStartup();
        await registry.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public void LinkedIntentWithoutACommittedAssetIsRejectedAsACallerErrorAndWritesNothing()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("60"), DateTimeOffset.UtcNow);
        int writes = 0;
        store.BeforeAdmittedMutationForTests = _ => writes++;

        ArgumentException rejected = Assert.Throws<ArgumentException>(() =>
            store.SetOriginalIntent("danbooru", "60", BooruOriginalIntent.Linked));
        Assert.Equal("intent", rejected.ParamName);
        Assert.Equal(0, writes);
        Assert.Equal(BooruOriginalIntent.None, Assert.Single(store.Read().Posts).OriginalIntent);

        // After a real link the same intent is an accepted no-op.
        store.LinkLocalAsset("danbooru", "60", "asset-60");
        writes = 0;
        BooruPostRecord linked = store.SetOriginalIntent("danbooru", "60", BooruOriginalIntent.Linked);
        Assert.Equal(("asset-60", BooruOriginalIntent.Linked), (linked.LocalAssetId, linked.OriginalIntent));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ReadsReuseTheValidatedSnapshotUntilTheFileChangesUnderneath()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("70"), DateTimeOffset.UtcNow);
        BooruCatalogSnapshot first = store.Read();
        BooruCatalogStoreCounters warm = store.Counters;
        Assert.Same(first, store.Read());
        Assert.NotNull(store.TryGetPost("danbooru", "70"));
        Assert.Null(store.TryGetPost("danbooru", "71"));
        Assert.Equal(warm.FullReads, store.Counters.FullReads);
        Assert.Equal(warm.CachedReads + 3, store.Counters.CachedReads);
        // The cached snapshot is immutable for every reader.
        Assert.Throws<NotSupportedException>(() => ((IList<BooruPostRecord>)first.Posts)[0] = first.Posts[0]);

        // Another writer (here: a restored copy with one more post) replaces the file: the next read parses it.
        string text = File.ReadAllText(store.FilePath);
        BooruCatalogStore other = new(directory.Path, lease);
        other.UpsertPost(Post("71"), DateTimeOffset.UtcNow);
        Assert.Equal(2, store.Read().Posts.Count);
        Assert.Equal(warm.FullReads + 1, store.Counters.FullReads);

        // Damage underneath is never hidden by the cache: the read fails closed, and keeps failing.
        File.WriteAllText(store.FilePath, text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.TryGetPost("danbooru", "70"));
        Assert.Throws<InvalidDataException>(() => store.UpsertPost(Post("72"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AFailedWriteDropsTheCacheAndKeepsTheLastDurableDocument()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("80"), DateTimeOffset.UtcNow);
        store.BeforeAdmittedMutationForTests = _ => throw new IOException("synthetic catalog failure");
        Assert.Throws<IOException>(() => store.UpsertPost(Post("81"), DateTimeOffset.UtcNow));
        store.BeforeAdmittedMutationForTests = null;
        long reads = store.Counters.FullReads;
        Assert.Equal("80", Assert.Single(store.Read().Posts).PostId);
        Assert.Equal(reads + 1, store.Counters.FullReads);
        Assert.Null(store.TryGetPost("danbooru", "81"));
        // The rebuilt indexes keep appending at the right place.
        store.UpsertPost(Post("81"), DateTimeOffset.UtcNow);
        store.AddFinding("danbooru", "81", BooruFindingKind.Search, null, "cat", DateTimeOffset.UtcNow);
        Assert.Equal(["80", "81"], store.Read().Posts.Select(post => post.PostId).ToArray());
        Assert.Single(store.TryGetPost("danbooru", "81")!.Findings);
        Assert.Equal(["80", "81"], new BooruCatalogStore(directory.Path, lease).Read().Posts.Select(post => post.PostId).ToArray());
    }

    [Fact]
    public void RecordObservationWritesClaimFindingAndIntentOnceAndRejectsLinkedWithoutAsset()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        int writes = 0;
        store.AfterAdmittedMutationForTests = _ => writes++;
        BooruPostRecord seen = store.RecordObservation(CompletePost("90"), DateTimeOffset.UtcNow, BooruFindingKind.Watcher, "w1", "cat",
            merged => merged.OriginalIntent == BooruOriginalIntent.None ? BooruOriginalIntent.Requested : null);
        Assert.Equal(1, writes);
        Assert.Equal(BooruOriginalIntent.Requested, seen.OriginalIntent);
        Assert.Equal("w1", Assert.Single(seen.Findings).WatcherId);
        Assert.Equal(seen, Assert.Single(store.Read().Posts));

        Assert.Throws<ArgumentException>(() => store.RecordObservation(CompletePost("90"), DateTimeOffset.UtcNow,
            BooruFindingKind.Search, null, "cat", _ => BooruOriginalIntent.Linked));
        Assert.Equal(1, writes);
        Assert.Single(Assert.Single(store.Read().Posts).Findings);
    }

    [Fact]
    public void TheWriteLimitIsConfigurableAndALowerLimitKeepsTheCatalogReadable()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        Assert.Equal(BooruCatalogStore.DefaultDocumentLimitBytes, store.DocumentLimitBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.DocumentLimitBytes = BooruCatalogStore.MinimumDocumentLimitBytes - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.DocumentLimitBytes = BooruCatalogStore.MaximumDocumentBytes + 1L);

        // About 9 MiB in one post: over the minimum limit, under the default.
        BooruPostClaim large = Post("95") with
        {
            Tags = Enumerable.Range(0, 600).Select(index => new BooruPostClaimTag(index.ToString("D4") + new string('t', 15_000),
                BooruPostClaimTagCategory.General)).ToArray()
        };
        store.UpsertPost(large, DateTimeOffset.UtcNow);
        BooruCatalogUsage usage = store.GetUsage();
        Assert.Equal(new FileInfo(store.FilePath).Length, usage.DocumentBytes);
        Assert.Equal((1, 0, BooruCatalogStore.DefaultDocumentLimitBytes), (usage.PostCount, usage.WatcherCount, usage.LimitBytes));
        Assert.False(usage.NearLimit);

        store.DocumentLimitBytes = BooruCatalogStore.MinimumDocumentLimitBytes;
        Assert.True(store.GetUsage().NearLimit);
        Assert.Equal("95", Assert.Single(store.Read().Posts).PostId);
        int writes = 0;
        store.BeforeAdmittedMutationForTests = _ => writes++;
        InvalidDataException full = Assert.Throws<InvalidDataException>(() => store.UpsertPost(Post("96"), DateTimeOffset.UtcNow));
        Assert.Equal(BooruCatalogStore.FullMessage, full.Message);
        Assert.Equal((long)BooruCatalogStore.MinimumDocumentLimitBytes, full.Data["limitBytes"]);
        Assert.Equal(0, writes);
        // A fresh store (next start) with the lower limit still reads the larger file.
        Assert.Single(new BooruCatalogStore(directory.Path, lease).Read().Posts);
    }

    [Fact]
    public void SuccessfulWritesLeaveNoTemporaryCatalogFiles()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("50"), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstLoadSweepsOrphanedTemporaryCatalogFilesAndOnlyThose()
    {
        using TempDirectory directory = new();
        string orphan = Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.tmp");
        string second = Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.tmp");
        // Look alike but are not this store's temporaries.
        string[] others =
        [
            Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.bak"),
            Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.notaguid.tmp"),
            Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}x.tmp"),
            Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.TMP"),
            Path.Combine(directory.Path, $"{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.tmp"),
            Path.Combine(directory.Path, $".other.json.{Guid.NewGuid():N}.tmp"),
            Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.tmp.keep")
        ];
        foreach (string path in new[] { orphan, second }.Concat(others)) File.WriteAllText(path, "partial");
        // A directory with a matching name is never touched.
        string nested = Path.Combine(directory.Path, $".{BooruCatalogStore.FileName}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(nested);

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        Assert.True(File.Exists(orphan));
        Assert.Empty(store.Read().Posts);

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(second));
        Assert.All(others, path => Assert.True(File.Exists(path), path));
        Assert.True(Directory.Exists(nested));
        // A write after the sweep still works and leaves no temporary behind.
        store.UpsertPost(Post("60"), DateTimeOffset.UtcNow);
        Assert.Equal("60", Assert.Single(store.Read().Posts).PostId);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), path => IsTemporary(Path.GetFileName(path)));
    }

    // ---- Incomplete, truncated and regressing catalog data ------------------------------------------

    [Fact]
    public void EveryTruncationOfAWrittenCatalogFailsClosed()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteRepresentativeCatalog(store);
        byte[] complete = File.ReadAllBytes(store.FilePath);
        Assert.NotEmpty(store.Read().Watchers);

        // A torn or partially flushed file must never be accepted as a (smaller) valid catalog.
        for (int length = 0; length < complete.Length; length++)
        {
            File.WriteAllBytes(store.FilePath, complete.AsSpan(0, length).ToArray());
            Assert.Throws<InvalidDataException>(() => store.Read());
        }

        File.WriteAllBytes(store.FilePath, complete);
        Assert.Equal("123", Assert.Single(store.Read().Watchers).HighWaterMark);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public void ZeroFilledCatalogAfterPowerLossFailsClosed(int length)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        File.WriteAllBytes(store.FilePath, new byte[length]);
        Assert.Throws<InvalidDataException>(() => store.Read());
        // A failed read never becomes an empty catalog that a later write could persist over the data.
        Assert.Throws<InvalidDataException>(() => store.UpsertPost(Post("1"), DateTimeOffset.UtcNow));
        Assert.Equal(length, new FileInfo(store.FilePath).Length);
    }

    [Theory]
    [InlineData("watchers", "highWaterMark")]
    [InlineData("watchers", "nextPage")]
    [InlineData("watchers", "baselineEstablished")]
    [InlineData("watchers", "knownPostIds")]
    [InlineData("watchers", "watcherId")]
    [InlineData("watchers", "query")]
    [InlineData("watchers", "state")]
    [InlineData("watchers", "createdAtUtc")]
    [InlineData("posts", "firstSeenAtUtc")]
    [InlineData("posts", "lastSeenAtUtc")]
    [InlineData("posts", "localAssetId")]
    [InlineData("posts", "localPath")]
    [InlineData("posts", "findings")]
    [InlineData("posts", "post")]
    public void RecordsMissingASchemaOneKeyFailClosed(string collection, string key)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteRepresentativeCatalog(store);

        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        JsonObject record = root[collection]![0]!.AsObject();
        Assert.True(record.Remove(key), $"fixture lacks {key}");
        File.WriteAllText(store.FilePath, root.ToJsonString());

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void RecordsMissingANestedRequiredKeyFailClosed()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteRepresentativeCatalog(store);
        string original = File.ReadAllText(store.FilePath);

        foreach (Action<JsonObject> damage in new Action<JsonObject>[]
                 {
                     root => root.Remove("schemaVersion"),
                     root => root.Remove("watchers"),
                     root => root["posts"]![0]!["findings"]![0]!.AsObject().Remove("foundAtUtc"),
                     root => root["posts"]![0]!["findings"]![0]!.AsObject().Remove("findingId"),
                     root => root["posts"]![0]!["post"]!.AsObject().Remove("fields"),
                     root => root["posts"]![0]!["post"]!["media"]![0]!.AsObject().Remove("url"),
                     root => root["posts"]![0]!["post"]!["tags"]![0]!.AsObject().Remove("category")
                 })
        {
            JsonObject root = JsonNode.Parse(original)!.AsObject();
            damage(root);
            File.WriteAllText(store.FilePath, root.ToJsonString());
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
    }

    [Fact]
    public void DuplicateJsonKeysCannotReplaceADurableHighWaterMark()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteRepresentativeCatalog(store);
        string text = File.ReadAllText(store.FilePath);
        const string original = "\"highWaterMark\": \"123\"";
        Assert.Contains(original, text);
        // "Last wins" would silently erase the fence and re-announce every older post.
        File.WriteAllText(store.FilePath, text.Replace(original, original + ", \"highWaterMark\": null", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Theory]
    [InlineData("remove-highWaterMark")]
    [InlineData("remove-baselineEstablished")]
    [InlineData("duplicate-highWaterMark")]
    public void IdleEstablishedWatcherCannotSilentlyLoseItsHighWaterMark(string damage)
    {
        // An idle established watcher keeps no known ids (they are compacted below the fence), so the
        // high-water mark is the only thing separating "already seen" from "new" after a restart.
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertWatcher(new("w-idle", "danbooru", "cat", "Active", DateTimeOffset.Parse("2026-11-12T12:00:00Z"),
            HighWaterMark: "500", BaselineEstablished: true, ResumeState: "Active", PageSize: 20, PageBudget: 5));
        string text = File.ReadAllText(store.FilePath);
        const string fence = "\"highWaterMark\": \"500\"";
        Assert.Contains(fence, text);

        if (damage == "duplicate-highWaterMark")
        {
            text = text.Replace(fence, fence + ", \"highWaterMark\": null", StringComparison.Ordinal);
        }
        else
        {
            JsonObject root = JsonNode.Parse(text)!.AsObject();
            Assert.True(root["watchers"]![0]!.AsObject().Remove(damage["remove-".Length..]));
            text = root.ToJsonString();
        }
        File.WriteAllText(store.FilePath, text);

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void IdleRecordsWithoutLaterAdditiveKeysStayReadable()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        store.UpsertPost(Post("5"), now);
        store.UpsertWatcher(new("w-idle", "danbooru", "cat", "Paused", now, HighWaterMark: "5",
            BaselineEstablished: true, ResumeState: "Paused"));

        // A schema-1 file written before the restart fields and the original intent existed.
        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        root["posts"]![0]!.AsObject().Remove("originalIntent");
        JsonObject watcher = root["watchers"]![0]!.AsObject();
        foreach (string additive in new[] { "cursorBeforePostId", "checkStartHighWaterMark", "checkStartHighWaterMarkCaptured",
                     "resumeState", "pageSize", "pageBudget", "pagesCheckedInRun", "budgetLimited" })
            Assert.True(watcher.Remove(additive), additive);
        File.WriteAllText(store.FilePath, root.ToJsonString());

        BooruCatalogSnapshot migrated = store.Read();
        Assert.Equal(BooruOriginalIntent.None, Assert.Single(migrated.Posts).OriginalIntent);
        BooruWatcherRecord saved = Assert.Single(migrated.Watchers);
        Assert.Equal("5", saved.HighWaterMark);
        Assert.Null(saved.ResumeState);
        Assert.Equal(0, saved.NextPage);
    }

    [Fact]
    public void CatalogsWrittenByTheCurrentWriterKeepLoadingUnderTheRequiredKeys()
    {
        // Every record shape the current writer produces, including the production watcher projection driven
        // through real domain transitions, is read back by a fresh store under the required-key and fence rules.
        using TempDirectory directory = new();
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        byte[] written;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            WriteRepresentativeCatalog(store);
            store.AddFinding("danbooru", "123", BooruFindingKind.Search, null, "cat", now);
            store.AddFinding("danbooru", "123", BooruFindingKind.OneShot, null, null, now);
            BooruOriginalIntent[] intents =
            [
                BooruOriginalIntent.None, BooruOriginalIntent.Requested, BooruOriginalIntent.Cancelled,
                BooruOriginalIntent.Unavailable, BooruOriginalIntent.NotAdmitted
            ];
            for (int item = 0; item < intents.Length; item++)
            {
                string id = (200 + item).ToString();
                store.UpsertPost(Post(id), now);
                store.SetOriginalIntent("danbooru", id, intents[item]);
            }
            store.UpsertPost(Post("300"), now);
            store.LinkLocalAsset("danbooru", "300", "asset-without-path");

            store.UpsertWatcher(new("w-fresh", "danbooru", "cat", "Active", now, ResumeState: "Active", PageSize: 20, PageBudget: 5));
            store.UpsertWatcher(new("w-empty", "danbooru", "cat", "Active", now, BaselineEstablished: true,
                ResumeState: "Active", PageSize: 20, PageBudget: 5));
            store.UpsertWatcher(new("w-cool", "danbooru", "cat", "Cooldown", now, CooldownUntilUtc: now.AddMinutes(5),
                ErrorCode: "rate_limited", HighWaterMark: "9", BaselineEstablished: true, ResumeState: "Active"));
            store.UpsertWatcher(new("w-error", "danbooru", "cat", "Error", now, ErrorCode: "provider_error",
                HighWaterMark: "9", BaselineEstablished: true, KnownPostIds: ["9"]));
            store.UpsertWatcher(new("w-gone", "danbooru", "cat", "Active", now, HighWaterMark: "9", BaselineEstablished: true));
            Assert.True(store.MarkWatcherRemoved("w-gone"));

            // The production projection, persisted after every transition of a baseline and a budget-limited catch-up.
            Assert.True(DanbooruWatcher.TryCreate("w-domain", "cat", now, out DanbooruWatcher? created, out _, 2, 2));
            DanbooruWatcher watcher = created!;
            void Persist(DanbooruWatcher next)
            {
                watcher = next;
                store.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(watcher));
            }
            DanbooruSearchPageObservation Page(int page, bool end, params string[] ids) =>
                new("danbooru", watcher.CanonicalQuery, page, ids, end);
            Persist(watcher);
            Persist(DanbooruWatcher.BeginCheck(watcher, now).Watcher);
            Persist(DanbooruWatcher.ApplyPage(watcher, Page(1, false, "100", "99"), now).Watcher);
            Persist(DanbooruWatcher.BeginCheck(watcher, now.AddMinutes(1)).Watcher);
            Persist(DanbooruWatcher.ApplyPage(watcher, Page(1, false, "140", "130"), now).Watcher);
            Persist(DanbooruWatcher.ApplyPage(watcher, Page(2, false, "120", "110"), now).Watcher);
            Assert.True(watcher.Progress.BudgetLimited);
            Persist(DanbooruWatcher.BeginCheck(watcher, now.AddMinutes(2)).Watcher);
            Persist(DanbooruWatcher.ApplyPage(watcher, Page(3, false, "105", "100"), now).Watcher);
            Assert.Equal(0, watcher.Progress.NextPage);
            Persist(DanbooruWatcher.Pause(watcher).Watcher);
            written = File.ReadAllBytes(store.FilePath);
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore restarted = new(directory.Path, reopened);
        BooruCatalogSnapshot snapshot = restarted.Read();
        Assert.Equal(7, snapshot.Posts.Count);
        Assert.Equal(7, snapshot.Watchers.Count);
        BooruWatcherRecord domain = Assert.Single(snapshot.Watchers, item => item.WatcherId == "w-domain");
        Assert.Equal("140", domain.HighWaterMark);
        Assert.Empty(domain.KnownPostIds);
        Assert.Equal("Removed", Assert.Single(snapshot.Watchers, item => item.WatcherId == "w-gone").State);
        Assert.Equal(BooruOriginalIntent.Linked, restarted.TryGetPost("danbooru", "300")!.OriginalIntent);
        Assert.Equal("w-domain", DanbooruWatcherRecordMapper.FromRecord(domain).WatcherId);
        // Re-persisting every record unchanged is accepted and reproduces the same bytes.
        foreach (BooruWatcherRecord record in snapshot.Watchers) restarted.UpsertWatcher(record);
        Assert.Equal(written, File.ReadAllBytes(restarted.FilePath));
    }

    [Fact]
    public void InterruptedReplacementKeepsThePreviousCatalogAndItsCursor()
    {
        using TempDirectory directory = new();
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            WriteRepresentativeCatalog(store);
            byte[] before = File.ReadAllBytes(store.FilePath);

            // Fault after the durable temporary write, before the rename: the crash window of the writer.
            store.BeforeAdmittedMutationForTests = _ => throw new IOException("simulated crash before replace");
            Assert.Throws<IOException>(() => store.UpsertWatcher(InFlightWatcher(now) with
            {
                HighWaterMark = "200", KnownPostIds = ["150", "200"], CursorBeforePostId = "150"
            }));
            Assert.Throws<IOException>(() => store.UpsertPost(Post("999"), now));

            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
            Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), path => IsTemporary(Path.GetFileName(path)));
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruCatalogSnapshot restarted = new BooruCatalogStore(directory.Path, reopened).Read();
        BooruWatcherRecord watcher = Assert.Single(restarted.Watchers);
        Assert.Equal("123", watcher.HighWaterMark);
        Assert.Equal("123", watcher.CursorBeforePostId);
        Assert.Equal("100", watcher.CheckStartHighWaterMark);
        Assert.Equal(2, watcher.NextPage);
        Assert.DoesNotContain(restarted.Posts, post => post.PostId == "999");
    }

    [Fact]
    public void ObservationInvariantsRejectKnownIdsBeyondOrWithoutTheHighWaterMark()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", now,
            BaselineEstablished: true, KnownPostIds: ["5"])));
        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", now,
            HighWaterMark: "5", BaselineEstablished: true, KnownPostIds: ["6"])));
        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", now,
            HighWaterMark: "5", BaselineEstablished: false)));
        Assert.False(File.Exists(store.FilePath));
        // An established watcher whose baseline was empty legitimately has no high-water mark.
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", now, BaselineEstablished: true));

        // The same invariants hold when the file is read, not only when it is written.
        File.WriteAllText(store.FilePath, Json(new BooruCatalogSnapshot(1, [], [new BooruWatcherRecord("w2", "danbooru", "cat",
            "Active", now, HighWaterMark: "5", BaselineEstablished: true, KnownPostIds: ["9"])])));
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.WriteAllText(store.FilePath, Json(new BooruCatalogSnapshot(1, [], [new BooruWatcherRecord("w2", "danbooru", "cat",
            "Active", now, HighWaterMark: "5", BaselineEstablished: false)])));
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void WatcherReplacementCannotLowerTheHighWaterMarkOrReviveARemovedWatcher()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        BooruWatcherRecord stored = store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", now,
            HighWaterMark: "100", BaselineEstablished: true, ResumeState: "Active"));
        byte[] before = File.ReadAllBytes(store.FilePath);

        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(stored with { HighWaterMark = "99" }));
        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(stored with { HighWaterMark = null }));
        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(stored with
        {
            HighWaterMark = null, BaselineEstablished = false
        }));
        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(stored with { Query = "dog" }));
        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(stored with { CreatedAtUtc = now.AddDays(-1) }));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Equal("100", Assert.Single(store.Read().Watchers).HighWaterMark);

        // Forward progress and unchanged re-writes remain allowed.
        store.UpsertWatcher(stored);
        store.UpsertWatcher(stored with { HighWaterMark = "150", LastCheckedAtUtc = now.AddMinutes(1) });
        Assert.Equal("150", Assert.Single(store.Read().Watchers).HighWaterMark);

        Assert.True(store.MarkWatcherRemoved("w1"));
        BooruWatcherRecord removed = Assert.Single(store.Read().Watchers);
        Assert.Equal("Removed", removed.State);
        Assert.Equal("150", removed.HighWaterMark);
        Assert.Throws<InvalidOperationException>(() => store.UpsertWatcher(removed with { State = "Active" }));
        store.UpsertWatcher(removed);
    }

    [Fact]
    public void CursorAndFenceSurviveRepeatedProfileRestartsByteForByte()
    {
        using TempDirectory directory = new();
        byte[] first;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            WriteRepresentativeCatalog(store);
            first = File.ReadAllBytes(store.FilePath);
        }

        for (int restart = 0; restart < 3; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            BooruCatalogStore store = new(directory.Path, lease);
            BooruCatalogSnapshot snapshot = store.Read();
            // Re-persisting what was read is lossless: the serialized form is a fixed point.
            store.UpsertWatcher(Assert.Single(snapshot.Watchers));
            BooruPostRecord post = Assert.Single(snapshot.Posts);
            store.UpsertPost(post.Post, post.LastSeenAtUtc);
            Assert.Equal(first, File.ReadAllBytes(store.FilePath));
        }
    }

    [Fact]
    public async Task PreparedRestoreFailsClosedForACatalogMissingItsHighWaterMark()
    {
        using TempDirectory directory = new();
        using (ProfileLease seed = ProfileLease.Acquire(directory.Path))
            WriteRepresentativeCatalog(new BooruCatalogStore(directory.Path, seed));
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["watchers"]![0]!.AsObject().Remove("highWaterMark");
        File.WriteAllText(path, root.ToJsonString());
        string damaged = File.ReadAllText(path);

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024),
            directory.Path, TimeSpan.FromHours(1), 3600, persistSession: false);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RestoreAdmittedAsync(startup));
        lease.Admission.FailStartup();
        await registry.StopAsync().WaitAsync(Deadline);

        // Failing closed is not a repair: the damaged file is left untouched for the user.
        Assert.Equal(damaged, File.ReadAllText(path));
    }

    private static BooruWatcherRecord InFlightWatcher(DateTimeOffset now) => new("w1", "danbooru", "cat", "Active", now,
        LastCheckedAtUtc: now, HighWaterMark: "123", NextPage: 2, BaselineEstablished: true, KnownPostIds: ["123"],
        CursorBeforePostId: "123", CheckStartHighWaterMark: "100", CheckStartHighWaterMarkCaptured: true,
        ResumeState: "Active", PageSize: 20, PageBudget: 5);

    private static void WriteRepresentativeCatalog(BooruCatalogStore store)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        store.UpsertPost(CompletePost("123"), now);
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "cat", now);
        store.LinkLocalAsset("danbooru", "123", "asset-1", "/offline/asset.png");
        store.UpsertWatcher(InFlightWatcher(now));
    }

    private static bool IsTemporary(string name) =>
        name.StartsWith("." + BooruCatalogStore.FileName + ".", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)
        && name.Length == BooruCatalogStore.FileName.Length + 2 + 32 + 4;

    private static BooruPostClaim Post(string id) => new("danbooru", id, BooruPostClaimPresence.Tags,
        $"https://danbooru.donmai.us/posts/{id}", Tags: Array.Empty<BooruPostClaimTag>());

    private static BooruPostClaim CompletePost(string id) => new("danbooru", id, BooruPostClaimPresence.All,
        $"https://danbooru.donmai.us/posts/{id}",
        CreatedAtUtc: DateTimeOffset.Parse("2026-11-12T11:00:00Z"),
        Rating: BooruPostClaimRating.Sensitive,
        Score: 7,
        FavoriteCount: 3,
        Tags:
        [
            new BooruPostClaimTag("cat", BooruPostClaimTagCategory.General),
            new BooruPostClaimTag("artist_name", BooruPostClaimTagCategory.Artist)
        ],
        Media:
        [
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Original,
                "https://cdn.donmai.us/original/a.jpg", ".jpg", 100, 640, 480,
                "ABEiM0RVZneImaq7zN3u/w=="),
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample,
                "https://cdn.donmai.us/sample/a.jpg"),
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview,
                "https://cdn.donmai.us/preview/a.jpg")
        ],
        SourceClaim: "https://source.example/post/123");

    private static string Json(BooruCatalogSnapshot snapshot)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return JsonSerializer.Serialize(snapshot, options);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-booru-" + Guid.NewGuid().ToString("N"));
        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
