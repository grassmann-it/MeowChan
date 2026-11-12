using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

public sealed class BooruCatalogStoreTests
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

    [Fact]
    public void OversizedCatalogFailsClosedBeforeDeserialization()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruCatalogStore.FileName);
        File.WriteAllText(path, new string('x', BooruCatalogStore.MaximumDocumentBytes + 1));
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
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
    public void SuccessfulWritesLeaveNoTemporaryCatalogFiles()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("50"), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

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
