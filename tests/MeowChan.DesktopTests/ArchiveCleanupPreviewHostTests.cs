using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCleanupPreviewHostTests
{
    [Fact]
    public async Task ReadsEveryCatalogPageAndPreservesOptionalFilesAndMedia()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root);
        ResourceId[] ids = Enumerable.Range(0, 501).Select(index => Id(index.ToString())).ToArray();
        foreach (ResourceId id in ids) Directory.CreateDirectory(DirectoryFor(root, id));
        await RegisterAsync(profile, root, ids);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        string pending = Path.Combine(profile, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(pending, "retained pending fixture");
        var before = Snapshot(profile, root);

        var preview = await host.PrepareArchiveCleanupAsync();
        var again = await host.PrepareArchiveCleanupAsync();

        Assert.True(preview.Complete); Assert.False(preview.IsStale); Assert.Empty(preview.Errors);
        Assert.Equal(ids.Length, preview.Rows.Count);
        Assert.True(ids.ToHashSet().SetEquals(preview.Rows.Select(row => row.Id)));
        Assert.All(preview.Rows, row => { Assert.Equal(ArchiveCleanupEligibility.Candidate, row.Eligibility); Assert.Equal(0, row.Media.SavedAssetCount); });
        Assert.Equal(preview.EvidenceToken, again.EvidenceToken);
        AssertSnapshot(before, Snapshot(profile, root));
    }

    [Fact]
    public async Task EveryProtectionSourceProtectsItsExactThreadIncludingMissingHistoricalMedia()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root);
        ResourceId[] ids = Enumerable.Range(0, 8).Select(index => Id(index.ToString())).ToArray();
        foreach (ResourceId id in ids) Directory.CreateDirectory(DirectoryFor(root, id));
        await RegisterAsync(profile, root, ids);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            new ThreadFavoriteStore(profile, lease).Set(ids[0], true);
            new FavoriteStore(profile, lease).SetFavorite(new(new(ids[1], "old-missing", "original", new string('a', 64)), 3, "old.bin"), true);
            new MediaRatingStore(profile, lease).Set(new(ids[2], "old-missing", "thumbnail", new string('b', 64)), 4);
            new PostBookmarkStore(profile, lease).Upsert(new(ids[3], "post"), "keep this note", 0, false);
            await new ReaderGroupStore(profile, lease).SaveAsync(new(1, 0, [new(Guid.NewGuid(), "Keep", [ids[4]])]), 0);
        }
        // A persisted reading anchor protects even while remembering new positions is disabled.
        await File.WriteAllTextAsync(Path.Combine(profile, "reading-position.json"),
            JsonSerializer.Serialize(new LocalReadingPosition(ids[5].Source, ids[5].Board, ids[5].Thread!, "post", 0, null)));
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(profile, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N")), "do not recover during preview");
        var before = Snapshot(profile, root);

        var preview = await host.PrepareArchiveCleanupAsync([ids[6]]);

        Assert.True(preview.Complete); Assert.False(preview.IsStale);
        ArchiveCleanupReason[] reasons = [ArchiveCleanupReason.ThreadFavorite, ArchiveCleanupReason.MediaFavorite,
            ArchiveCleanupReason.MediaRating, ArchiveCleanupReason.PostBookmark, ArchiveCleanupReason.ReaderGroup,
            ArchiveCleanupReason.ReadingPosition, ArchiveCleanupReason.OpenView];
        for (int index = 0; index < reasons.Length; index++)
        {
            var row = Assert.Single(preview.Rows, row => row.Id == ids[index]);
            Assert.Equal(0, row.Media.SavedAssetCount);
            Assert.Equal(ArchiveCleanupEligibility.Protected, row.Eligibility); Assert.Contains(reasons[index], row.Reasons);
        }
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(preview.Rows, row => row.Id == ids[7]).Eligibility);
        AssertSnapshot(before, Snapshot(profile, root));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    public async Task InvalidOrFutureProtectionVersionBlocksWithoutRecoveryOrWrites(string version)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root); ResourceId id = Id("bad-protection");
        Directory.CreateDirectory(DirectoryFor(root, id)); await RegisterAsync(profile, root, id);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(profile, "thread-favorites.json"), "{\"Version\":" + version + ",\"Revision\":0,\"Threads\":[]}");
        var before = Snapshot(profile, root);

        var preview = await host.PrepareArchiveCleanupAsync();

        Assert.False(preview.Complete); Assert.NotEmpty(preview.Errors);
        var row = Assert.Single(preview.Rows); Assert.Equal(ArchiveCleanupEligibility.Unknown, row.Eligibility);
        Assert.Contains(ArchiveCleanupReason.ProtectionUnknown, row.Reasons);
        AssertSnapshot(before, Snapshot(profile, root));
    }

    [Theory]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Expired, false, false, "Candidate")]
    [InlineData(WorkIntent.Suppressed, MediaWorkStatus.Expired, false, false, "Candidate")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Excluded, false, false, "Candidate")]
    [InlineData(WorkIntent.Watching, MediaWorkStatus.Expired, false, false, "Active")]
    [InlineData(WorkIntent.Repairing, MediaWorkStatus.Expired, false, false, "Active")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Expired, true, false, "Active")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Ready, false, false, "Active")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Waiting, false, false, "Active")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.NeedsIntervention, false, false, "Active")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Succeeded, false, false, "Unknown")]
    [InlineData(WorkIntent.Completed, MediaWorkStatus.Expired, false, true, "Active")]
    public async Task AuthoritativeWorkJournalDistinguishesTerminalAndUnfinishedWork(
        WorkIntent intent, MediaWorkStatus status, bool capturePending, bool manualAttempt, string expected)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root); ResourceId id = Id("work");
        string directory = DirectoryFor(root, id), url = "https://offline.invalid/work";
        Directory.CreateDirectory(directory); await RegisterAsync(profile, root, id);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            WorkStateStore store = new(profile, lease);
            WorkResource resource = new() { Url = url, OutputRoot = root, Generation = 1, IsThread = true,
                Intent = intent == WorkIntent.Suppressed ? WorkIntent.Completed : intent,
                CapturePending = capturePending, Source = ThreadSourceStateValue.Archived };
            store.CommitIntent(resource);
            Assert.True(store.CommitMedia(new() { ResourceUrl = url, Generation = 1, MediaIdentifier = "m",
                DestinationPath = Path.Combine(directory, "m.bin"), DownloadUrl = "https://offline.invalid/m.bin", Status = status, ManualAttempt = manualAttempt }));
            if (intent == WorkIntent.Suppressed) Assert.NotNull(store.CommitState(resource with { Intent = intent }));
        }
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        var before = Snapshot(profile, root);

        var preview = await host.PrepareArchiveCleanupAsync();

        Assert.True(preview.Complete); Assert.False(preview.IsStale);
        Assert.Equal(Enum.Parse<ArchiveCleanupEligibility>(expected), Assert.Single(preview.Rows).Eligibility);
        AssertSnapshot(before, Snapshot(profile, root));
    }

    [Fact]
    public async Task CatalogBoundSiblingProvesMissingZeroButUnconfirmedHistoryAndSharedOwnershipBlock()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root);
        ResourceId anchor = Id("anchor"), missing = Id("missing"), history = Id("history"), sharedA = Id("shared-a"), sharedB = Id("shared-b");
        await AssetsAsync(DirectoryFor(root, anchor), new Asset("m", "original", "m.bin", "m.bin", [1, 2]));
        Directory.CreateDirectory(DirectoryFor(root, sharedA));
        await RegisterAsync(profile, root, anchor, missing, history, sharedA, sharedB);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            await catalog.RegisterThreadAsync(new(history, "https://offline.invalid/history", "History", Path.Combine(root, "legacy", "unconfirmed"), false));
            await catalog.RegisterThreadAsync(new(history, "https://offline.invalid/history", "History", DirectoryFor(root, history), false));
            await catalog.RegisterThreadAsync(new(sharedB, "https://offline.invalid/shared-b", "Shared", DirectoryFor(root, sharedA), false));
        }
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();

        var preview = await host.PrepareArchiveCleanupAsync();

        Assert.True(preview.Complete); Assert.False(preview.IsStale);
        var zero = Assert.Single(preview.Rows, row => row.Id == missing);
        Assert.Equal(ArchiveCleanupEligibility.Candidate, zero.Eligibility); Assert.Equal(0, zero.Media.SavedAssetCount);
        Assert.NotNull(Assert.Single(zero.Media.Directories).StorageAnchor);
        var historical = Assert.Single(preview.Rows, row => row.Id == history);
        Assert.Equal(ArchiveCleanupEligibility.Unknown, historical.Eligibility); Assert.Null(historical.Media.SavedAssetCount);
        foreach (ResourceId id in new[] { sharedA, sharedB })
        {
            var row = Assert.Single(preview.Rows, row => row.Id == id);
            Assert.Equal(ArchiveCleanupEligibility.Protected, row.Eligibility); Assert.Contains(ArchiveCleanupReason.SharedDirectory, row.Reasons);
        }
    }

    [Fact]
    public async Task MediaProtectionAndHostLifetimeChangeEvidenceAndCancellationDoesNotWrite()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root); ResourceId id = Id("tokens"); string directory = DirectoryFor(root, id);
        Directory.CreateDirectory(directory); await RegisterAsync(profile, root, id);
        string binding;
        await using (DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            var empty = await host.PrepareArchiveCleanupAsync(); binding = empty.ProfileBinding;
            await AssetsAsync(directory, new Asset("m", "original", "m.bin", "m.bin", [1, 2]));
            var media = await host.PrepareArchiveCleanupAsync();
            Assert.NotEqual(empty.EvidenceToken, media.EvidenceToken); Assert.Equal(1, Assert.Single(media.Rows).Media.SavedAssetCount);
            var open = await host.PrepareArchiveCleanupAsync([id]);
            Assert.NotEqual(media.ProtectionToken, open.ProtectionToken); Assert.NotEqual(media.EvidenceToken, open.EvidenceToken);
            var before = Snapshot(profile, root);
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.PrepareArchiveCleanupAsync(token: cancelled.Token));
            AssertSnapshot(before, Snapshot(profile, root));
        }
        await using DesktopSessionHost restarted = new(profile, Array.Empty<IImageBoard>());
        await restarted.StartAsync();
        Assert.NotEqual(binding, (await restarted.PrepareArchiveCleanupAsync()).ProfileBinding);
    }

    private static Dictionary<string, string> Snapshot(string profile, string root) =>
        Directory.EnumerateFiles(profile, "*.json*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void AssertSnapshot(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach ((string path, string digest) in expected) Assert.Equal(digest, actual[path]);
    }
}
