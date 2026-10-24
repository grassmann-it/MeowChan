using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

/// <summary>Independent counterexamples at the real host boundary; all data is disposable.</summary>
public sealed class ArchiveCleanupAcceptanceHostTests
{
    private static ResourceId Id(string thread, string board = "g") => new("4chan", board, thread);

    [Fact]
    public async Task OnlyExplicitZeroOneTwoSelectionIsDeletedAndGlobalStateSurvivesReopen()
    {
        using Fixture fixture = new();
        await fixture.SeedAsync((Id("100"), 0), (Id("101"), 1), (Id("102"), 2), (Id("103"), 3));
        await using (DesktopSessionHost host = await fixture.StartAsync())
        {
            var before = host.GetView();
            var durableBefore = fixture.ProtectedFiles();
            var preview = await host.PrepareArchiveCleanupAsync([]);
            Assert.True(preview.Complete); Assert.False(preview.IsStale);
            foreach (int count in new[] { 0, 1, 2, 3 })
            {
                var row = Assert.Single(preview.Rows, row => row.Id == Id((100 + count).ToString()));
                Assert.Equal(count, row.Media.SavedAssetCount);
                Assert.All(row.Media.Directories, directory => Assert.Null(directory.StorageAnchor));
                Assert.Equal(count < 3 ? ArchiveCleanupEligibility.Candidate : ArchiveCleanupEligibility.Kept, row.Eligibility);
            }
            List<ResourceId> selected = [Id("100"), Id("102")];
            var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, selected);
            Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
            Assert.Equal(2, plan.PostCount);
            selected.Clear(); selected.Add(Id("101")); // Caller mutation cannot change the confirmed selection.
            var result = await host.ExecuteArchiveCleanupAsync(plan);
            Assert.Equal(new[] { Id("100"), Id("102") }, result.DeletedThreads);
            Assert.Empty(result.RemainingThreads); Assert.Empty(result.Errors); Assert.False(result.Canceled);
            Assert.True(result.RequiresNewPreview); Assert.True(result.DeletedFileCount >= 3);
            Assert.False(host.IsPaused); Assert.Equal(before.Threads, host.GetView().Threads); Assert.Equal(before.Boards, host.GetView().Boards);
            fixture.AssertProtectedFiles(durableBefore);
            Assert.Empty((await host.ExecuteArchiveCleanupAsync(plan)).DeletedThreads);
            Assert.True(File.Exists(Path.Combine(fixture.Media(Id("101")), "asset0.bin")));
            Assert.True(File.Exists(Path.Combine(fixture.Media(Id("103")), "asset2.bin")));
        }
        await using ArchiveReader reopened = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.Null(await reopened.ReadThreadAsync(Id("100"))); Assert.Null(await reopened.ReadThreadAsync(Id("102")));
        Assert.NotNull(await reopened.ReadThreadAsync(Id("101"))); Assert.NotNull(await reopened.ReadThreadAsync(Id("103")));
    }

    [Theory]
    [InlineData("thread", "ThreadFavorite")]
    [InlineData("media", "MediaFavorite")]
    [InlineData("rating", "MediaRating")]
    [InlineData("bookmark", "PostBookmark")]
    [InlineData("group", "ReaderGroup")]
    [InlineData("position", "ReadingPosition")]
    [InlineData("open", "OpenView")]
    public async Task EachProtectionIndependentlyBlocksSelectionAndPreservesBytes(string protection, string expected)
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1), (Id("101"), 1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        await ProtectAsync(host, Id("100"), protection);
        var protectedBefore = fixture.ProtectedFiles();
        byte[] media = await File.ReadAllBytesAsync(Path.Combine(fixture.Media(Id("100")), "asset0.bin"));
        ResourceId[] open = protection == "open" ? [Id("100")] : [];
        var preview = await host.PrepareArchiveCleanupAsync(open);
        var row = Assert.Single(preview.Rows, row => row.Id == Id("100"));
        Assert.Equal(ArchiveCleanupEligibility.Protected, row.Eligibility); Assert.Contains(Enum.Parse<ArchiveCleanupReason>(expected), row.Reasons);
        Assert.False((await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")], currentOpenThreadIds: open)).CanExecute);
        var allowed = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("101")], currentOpenThreadIds: open);
        Assert.True(allowed.CanExecute, string.Join(";", allowed.Reasons));
        Assert.Empty((await host.ExecuteArchiveCleanupAsync(allowed, currentOpenThreadIds: open)).Errors);
        fixture.AssertProtectedFiles(protectedBefore);
        Assert.Equal(media, await File.ReadAllBytesAsync(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.NotNull(await reader.ReadThreadAsync(Id("100"))); Assert.Null(await reader.ReadThreadAsync(Id("101")));
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("rating")]
    [InlineData("bookmark")]
    [InlineData("group")]
    [InlineData("position")]
    public async Task NewlyProtectedThreadInvalidatesAlreadyPreparedPlan(string protection)
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]);
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        await ProtectAsync(host, Id("100"), protection);
        var before = fixture.ProtectedFiles();
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount); Assert.NotEmpty(result.Errors);
        Assert.True(result.RequiresNewPreview); fixture.AssertProtectedFiles(before);
        Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
    }

    [Fact]
    public async Task CanceledAndForeignProfilePlansNeverDeleteAndRequireNewPreview()
    {
        using Fixture a = new(); using Fixture b = new();
        await a.SeedAsync((Id("100"), 1)); await b.SeedAsync((Id("100"), 1));
        await using DesktopSessionHost first = await a.StartAsync(); await using DesktopSessionHost second = await b.StartAsync();
        var preview = await first.PrepareArchiveCleanupAsync([]);
        Assert.False((await second.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")])).CanExecute);
        var foreign = await first.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]);
        Assert.True(foreign.CanExecute);
        var foreignResult = await second.ExecuteArchiveCleanupAsync(foreign);
        Assert.Empty(foreignResult.DeletedThreads); Assert.Equal(0, foreignResult.DeletedFileCount); Assert.NotEmpty(foreignResult.Errors);
        var canceledPlan = await first.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        var result = await first.ExecuteArchiveCleanupAsync(canceledPlan, canceled.Token);
        Assert.True(result.Canceled); Assert.True(result.RequiresNewPreview); Assert.Empty(result.DeletedThreads);
        Assert.Empty((await first.ExecuteArchiveCleanupAsync(canceledPlan)).DeletedThreads);
        foreach (Fixture fixture in new[] { a, b })
            Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
    }

    [Fact]
    public async Task ForeignFileAddedAfterConfirmationFailsBeforeDeletingAnyOwnedFile()
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 2));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]); Assert.True(plan.CanExecute);
        string foreign = Path.Combine(fixture.Media(Id("100")), "personal-note.txt"); await File.WriteAllTextAsync(foreign, "retain");
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount); Assert.NotEmpty(result.Errors);
        Assert.Equal("retain", await File.ReadAllTextAsync(foreign));
        Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        Assert.Equal(ArchiveCleanupEligibility.Unknown, Assert.Single((await host.PrepareArchiveCleanupAsync([])).Rows).Eligibility);
    }

    [Fact]
    public async Task MissingBoardImportNeedsSameBoardAnchorAndLosesEligibilityWhenAnchorDisappears()
    {
        using Fixture fixture = new();
        await fixture.SeedAsync((Id("100"), -1), (Id("101"), 3), (Id("200", "x"), -1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var witnessed = Assert.Single(preview.Rows, row => row.Id == Id("100"));
        Assert.Equal(ArchiveCleanupEligibility.Candidate, witnessed.Eligibility); Assert.Equal(0, witnessed.Media.SavedAssetCount);
        Assert.NotNull(Assert.Single(witnessed.Media.Directories).StorageAnchor);
        Assert.Equal(ArchiveCleanupEligibility.Unknown, Assert.Single(preview.Rows, row => row.Id == Id("200", "x")).Eligibility);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]); Assert.True(plan.CanExecute);
        File.Delete(Path.Combine(fixture.Media(Id("101")), "asset0.bin"));
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Empty(result.DeletedThreads); Assert.NotEmpty(result.Errors);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.NotNull(await reader.ReadThreadAsync(Id("100")));
    }

    [Fact]
    public async Task CatalogFailureAfterFilesReportsPartialTruthAndNeverReplaysConsumedPlan()
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1), (Id("101"), 1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        using (SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Profile, "archive.db"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER cleanup_fixture_fail BEFORE DELETE ON archive_threads WHEN old.thread='101' BEGIN SELECT RAISE(ABORT,'fixture catalog failure'); END;";
            command.ExecuteNonQuery();
        }
        var before = fixture.ProtectedFiles();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100"), Id("101")]);
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Equal(new[] { Id("100") }, result.DeletedThreads); Assert.Equal(new[] { Id("101") }, result.RemainingThreads);
        Assert.True(result.DeletedFileCount >= 2); Assert.True(result.DeletedBytes >= 6);
        Assert.Contains(result.Errors, error => error.Contains("fixture catalog failure")); Assert.True(result.RequiresNewPreview);
        Assert.False(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        Assert.False(File.Exists(Path.Combine(fixture.Media(Id("101")), "asset0.bin")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.Null(await reader.ReadThreadAsync(Id("100"))); Assert.NotNull(await reader.ReadThreadAsync(Id("101")));
        var replay = await host.ExecuteArchiveCleanupAsync(plan); Assert.Equal(0, replay.DeletedFileCount); Assert.Empty(replay.DeletedThreads);
        fixture.AssertProtectedFiles(before); Assert.False(host.IsPaused);
    }

    [Theory]
    [InlineData("Watching")]
    [InlineData("Repairing")]
    [InlineData("capture")]
    [InlineData("Ready")]
    [InlineData("Waiting")]
    [InlineData("NeedsIntervention")]
    public async Task PausedOrProblematicUnfinishedWorkIsExcludedEvenWithVerifiedSmallInventory(string kind)
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1));
        Config config = Config.LoadConfig(fixture.Profile); config.DownloadsPaused = true; config.SaveConfig(fixture.Profile);
        using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile))
        {
            WorkStateStore work = new(fixture.Profile, lease);
            string url = "https://boards.4chan.org/g/thread/100";
            work.CommitIntent(new WorkResource { Url = url, OutputRoot = fixture.Output,
                Generation = 1, Intent = kind is "Watching" or "Repairing" ? Enum.Parse<WorkIntent>(kind) : WorkIntent.Completed,
                IsThread = true, CapturePending = kind == "capture" });
            if (Enum.TryParse(kind, out MediaWorkStatus status))
                Assert.True(work.CommitMedia(new MediaWork { ResourceUrl = url, Generation = 1, MediaIdentifier = "asset0",
                    DestinationPath = Path.Combine(fixture.Media(Id("100")), "asset0.bin"), DownloadUrl = "https://offline.invalid/asset0.bin", Status = status }));
        }
        await using DesktopSessionHost host = await fixture.StartAsync();
        var before = fixture.ProtectedFiles();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ArchiveCleanupEligibility.Active, row.Eligibility); Assert.Contains(ArchiveCleanupReason.PendingWork, row.Reasons);
        Assert.False((await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")])).CanExecute);
        Assert.True(host.IsPaused); fixture.AssertProtectedFiles(before);
        Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
    }

    [Theory]
    [InlineData("Completed", false)]
    [InlineData("Suppressed", false)]
    [InlineData("Completed", true)]
    [InlineData("Suppressed", true)]
    public async Task ExpiredTerminalWorkAllowsCleanupOnlyWithoutPendingCapture(string intent, bool capturePending)
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1));
        using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile))
        {
            WorkStateStore work = new(fixture.Profile, lease);
            string url = "https://boards.4chan.org/g/thread/100";
            // Commit media before suppression, which correctly refuses new media work.
            WorkResource resource = work.CommitIntent(new WorkResource { Url = url, OutputRoot = fixture.Output,
                Generation = 1, Intent = WorkIntent.Completed, IsThread = true, CapturePending = capturePending,
                Source = ThreadSourceStateValue.Archived });
            Assert.True(work.CommitMedia(new MediaWork { ResourceUrl = url, Generation = 1, MediaIdentifier = "asset0",
                DestinationPath = Path.Combine(fixture.Media(Id("100")), "asset0.bin"), DownloadUrl = "https://offline.invalid/asset0.bin",
                Status = MediaWorkStatus.Expired, ManualAttempt = false }));
            Assert.NotNull(work.CommitState(resource with { Intent = Enum.Parse<WorkIntent>(intent) }));
        }
        await using DesktopSessionHost host = await fixture.StartAsync();
        Assert.Empty(host.GetView().Threads); Assert.Empty(host.GetView().Boards);
        var before = fixture.ProtectedFiles();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        Assert.True(preview.Complete); Assert.False(preview.IsStale);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(1, row.Media.SavedAssetCount); Assert.Null(row.Media.Error);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]);
        if (capturePending)
        {
            Assert.Equal(ArchiveCleanupEligibility.Active, row.Eligibility);
            Assert.Contains(ArchiveCleanupReason.PendingWork, row.Reasons); Assert.False(plan.CanExecute);
            Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        }
        else
        {
            Assert.Equal(ArchiveCleanupEligibility.Candidate, row.Eligibility); Assert.Empty(row.Reasons);
            Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
            var result = await host.ExecuteArchiveCleanupAsync(plan);
            Assert.Equal(new[] { Id("100") }, result.DeletedThreads); Assert.Empty(result.RemainingThreads);
            Assert.Empty(result.Errors); Assert.False(result.Canceled);
            Assert.False(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.Equal(capturePending, await reader.ReadThreadAsync(Id("100")) != null);
        fixture.AssertProtectedFiles(before); Assert.False(host.IsPaused);
    }

    [Theory]
    [InlineData("thread-favorites.json")]
    [InlineData("media-favorites.json")]
    [InlineData("media-ratings.json")]
    [InlineData("post-bookmarks.json")]
    [InlineData("reader-groups.json")]
    [InlineData("reading-position.json")]
    public async Task CorruptProtectionAfterConfirmationStopsBeforeAnyDeletion(string file)
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var plan = await host.PrepareArchiveCleanupExecutionAsync(await host.PrepareArchiveCleanupAsync([]), [Id("100")]);
        Assert.True(plan.CanExecute);
        string path = Path.Combine(fixture.Profile, file); await File.WriteAllTextAsync(path, "{unreadable protected data");
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount); Assert.NotEmpty(result.Errors);
        Assert.Equal("{unreadable protected data", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SelectedWitnessFirstStillDeletesAllDependentMissingZeroThreadsBeforeWitness(int witnessAssets)
    {
        using Fixture fixture = new();
        // The witness sorts first AND is explicitly selected first: success must not depend on caller order.
        await fixture.SeedAsync((Id("100"), witnessAssets), (Id("101"), -1), (Id("102"), -1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var before = fixture.ProtectedFiles();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        foreach (string missing in new[] { "101", "102" })
        {
            var row = Assert.Single(preview.Rows, row => row.Id == Id(missing));
            Assert.Equal(ArchiveCleanupEligibility.Candidate, row.Eligibility);
            Assert.Equal(0, row.Media.SavedAssetCount);
            Assert.Equal(Id("100"), Assert.Single(row.Media.Directories).StorageAnchor!.Thread);
        }
        ResourceId[] selected = [Id("100"), Id("101"), Id("102")];
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, selected);
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        Assert.True(plan.SelectedIds.ToHashSet().SetEquals(selected));
        var result = await host.ExecuteArchiveCleanupAsync(plan);
        Assert.Empty(result.Errors); Assert.Empty(result.RemainingThreads); Assert.False(result.Canceled);
        Assert.Equal(3, result.DeletedThreads.Count); Assert.True(result.DeletedThreads.ToHashSet().SetEquals(selected));
        Assert.Equal(Id("100"), result.DeletedThreads[^1]);
        foreach (string file in new[] { ".meowchan-manifest.json", "asset0.bin", "asset1.bin" })
            Assert.False(File.Exists(Path.Combine(fixture.Media(Id("100")), file)));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        foreach (ResourceId id in selected) Assert.Null(await reader.ReadThreadAsync(id));
        fixture.AssertProtectedFiles(before); Assert.False(host.IsPaused);
    }

    [Fact]
    public async Task NewlyOpenedTargetRejectsPrepareAndPreviouslyConfirmedExecutionWithoutFileLoss()
    {
        using Fixture fixture = new(); await fixture.SeedAsync((Id("100"), 1));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync([]);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]); Assert.True(plan.CanExecute);
        var changed = await host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")], currentOpenThreadIds: [Id("100")]);
        Assert.False(changed.CanExecute); Assert.NotEmpty(changed.Reasons);
        var result = await host.ExecuteArchiveCleanupAsync(plan, currentOpenThreadIds: [Id("100")]);
        Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount); Assert.NotEmpty(result.Errors);
        Assert.True(result.RequiresNewPreview); Assert.Equal(new[] { Id("100") }, result.RemainingThreads);
        Assert.True(File.Exists(Path.Combine(fixture.Media(Id("100")), "asset0.bin")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.NotNull(await reader.ReadThreadAsync(Id("100")));
    }

    private static async Task ProtectAsync(DesktopSessionHost host, ResourceId id, string kind)
    {
        FavoriteAssetKey key = new(id, "asset0", "original", new string('a', 64));
        switch (kind)
        {
            case "thread": await host.SetThreadFavoriteAsync(id, true); break;
            case "media": await host.SetFavoriteAsync(new FavoriteEntry(key, 3, "asset0.bin"), true); break;
            case "rating": await host.SetMediaRatingAsync(key, 5); break;
            case "bookmark": await host.SavePostBookmarkAsync(new(id, "100"), "private note\n🙂", 0, false); break;
            case "group": await host.SaveReaderGroupsAsync(new(1, 0, [new(Guid.NewGuid(), "Keep", [id])]), 0); break;
            case "position": await host.RememberReadingPositionAsync(new(id.Source, id.Board, id.Thread!, "100", -4, null), host.ReadingPositionGeneration); break;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _root = new();
        public string Profile => Path.Combine(_root.Path, "profile");
        public string Output => Path.Combine(_root.Path, "media");
        public string Media(ResourceId id) => StorageLayout.GetThreadDirectory(Output, id.Source, id.Board, id.Thread!);
        public async Task SeedAsync(params (ResourceId Id, int Count)[] rows)
        {
            Directory.CreateDirectory(Profile); Directory.CreateDirectory(Output);
            new Config { DownloadPath = Output, DownloadsPaused = false, SaveUrlsOnExit = true, RememberReadingPosition = true }.SaveConfig(Profile);
            using ProfileLease lease = ProfileLease.Acquire(Profile);
            await new SessionStore(Profile, lease).SaveAsync(new SessionSnapshot { SuppressedThreadUrls = ["https://offline.invalid/retained-suppression"] });
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(Profile, lease);
            foreach (var row in rows)
            {
                string directory = Media(row.Id); Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                if (row.Count >= 0)
                {
                    Directory.CreateDirectory(directory); Dictionary<string, object> entries = new();
                    for (int i = 0; i < row.Count; i++)
                    {
                        byte[] bytes = [(byte)i, 17, 29]; string name = $"asset{i}.bin";
                        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                        entries[name] = new { MediaIdentifier = $"asset{i}", SourceUrl = "https://offline.invalid/" + name,
                            FileName = name, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
                    }
                    if (entries.Count > 0) await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
                }
                await catalog.RegisterThreadAsync(new(row.Id, $"https://boards.4chan.org/{row.Id.Board}/thread/{row.Id.Thread}", row.Id.Thread!, directory, true));
                await catalog.CaptureAsync(new(row.Id, $"https://boards.4chan.org/{row.Id.Board}/thread/{row.Id.Thread}", row.Id.Thread!,
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory,
                    [new CapturedPost(row.Id.Thread!, 0, PostFieldPresence.All, Html: "<p>Disposable archived text</p>", Attachments: [])]));
            }
        }
        public async Task<DesktopSessionHost> StartAsync()
        { DesktopSessionHost host = new(Profile, Array.Empty<IImageBoard>()); await host.StartAsync(); return host; }
        public Dictionary<string, byte[]> ProtectedFiles()
        {
            var files = Directory.EnumerateFiles(Profile).Where(path =>
            Path.GetFileName(path).Replace(".bak", "", StringComparison.Ordinal) is "settings.json" or "saved_urls.json" or "work-state.json" or "media-favorites.json" or "media-ratings.json"
                or "thread-favorites.json" or "post-bookmarks.json" or "reader-groups.json" or "reading-position.json")
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
            files["work-state.logical"] = WorkJournal(Profile);
            return files;
        }
        private static byte[] WorkJournal(string profile)
        {
            string path = Path.Combine(profile, "work-state.sqlite");
            if (!File.Exists(path)) return [];
            using SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); List<string> rows = [];
            foreach (string table in new[] { "work_resources", "work_media" })
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM " + table + " ORDER BY payload";
                using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(table + ":" + reader.GetString(0));
            }
            return JsonSerializer.SerializeToUtf8Bytes(rows);
        }
        public void AssertProtectedFiles(Dictionary<string, byte[]> before)
        {
            var after = ProtectedFiles(); Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        }
        public void Dispose() => _root.Dispose();
    }
}
