using System.Security.Cryptography;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.FolderSources;

/// <summary>
/// Regression tests for the folder source findings of the wave 3 review (W3-02, W3-03, W3-05, W3-06, W3-08): cancellable and
/// bounded view hashing, previews that never hash, a full catalog that can always be shrunk, reads outside the profile-wide
/// lease monitor, batched digest commits, and links told apart from cloud placeholders. Temp folders only.
/// </summary>
public sealed class FolderSourceReviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T23:00:00Z");
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7 };

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public readonly TempDirectory Temp = new();
        public string Profile => Path.Combine(Temp.Path, "profile");
        public string Media => Path.Combine(Temp.Path, "media");
        public readonly ProfileLease Lease;
        public readonly FolderSourceCatalog Catalog;

        public Fixture()
        {
            Directory.CreateDirectory(Media);
            Lease = ProfileLease.Acquire(Profile);
            Catalog = new FolderSourceCatalog(Profile, Lease);
        }

        /// <summary>A root with <paramref name="count"/> catalogued (not existing) files, for hasher and catalog tests with fakes.</summary>
        public (FolderSourceRoot Root, FolderSourceItem[] Items) Synthetic(int count, string folder = "media", string prefix = "")
        {
            string path = Path.Combine(Temp.Path, folder);
            Directory.CreateDirectory(path);
            FolderSourceRoot root = Catalog.AddRoot(path, folder, true, Now);
            FolderSourceScan scan = new(Enumerable.Range(0, count)
                .Select(index => new FolderSourceScannedFile(prefix + index.ToString("D6") + ".png", 10 + index, index, SavedMediaKind.Image)).ToArray(),
                true, true, false, Array.Empty<string>(), 0);
            Catalog.ApplyScan(root.Id, scan, Now);
            return (root, Catalog.Read().Items.Where(item => item.RootId == root.Id).OrderBy(item => item.RelativePath).ToArray());
        }

        public void Dispose() { Lease.Dispose(); Temp.Dispose(); }
    }

    // ---- W3-02: view hashing is cancellable, bounded and never started by a preview ----

    [Fact]
    public async Task CancelledViewRequestsStopTheirReadsLeaveNothingPendingAndGiveTheirSlotsBack()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(50);
        await using FolderSourceHasher hasher = new(fixture.Catalog) { Parallelism = 2 };
        int started = 0, stoppedMidRead = 0;
        bool block = true;
        HashSet<Guid> reading = new();
        hasher.HashFileForTests = async (_, item, token) =>
        {
            Interlocked.Increment(ref started);
            lock (reading) reading.Add(item.Id);
            if (Volatile.Read(ref block))
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Interlocked.Increment(ref stoppedMidRead); throw; }
            }
            return (FolderSourceHashOutcome.Hashed, Digest(item.RelativePath));
        };
        CancellationTokenSource[] scrolled = items.Select(_ => new CancellationTokenSource()).ToArray();
        Task<FolderSourceHashResult>[] views = items.Select((item, index) => hasher.HashAsync(item.Id, FolderSourceHashPriority.View, scrolled[index].Token)).ToArray();
        await TestWait.UntilAsync(() => Volatile.Read(ref started) == 2, "The two slots did not start reading.");
        // The tiles scroll away: the waiting ones first, so a slot given back cannot go to a request that is about to leave anyway.
        HashSet<Guid> running;
        lock (reading) running = reading.ToHashSet();
        for (int index = 0; index < items.Length; index++) if (!running.Contains(items[index].Id)) scrolled[index].Cancel();
        for (int index = 0; index < items.Length; index++) if (running.Contains(items[index].Id)) scrolled[index].Cancel();
        foreach (Task<FolderSourceHashResult> view in views) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view);
        await TestWait.UntilAsync(() => hasher.Status.Pending == 0, "Cancelled requests stayed pending.");
        Assert.Equal(2, Volatile.Read(ref started)); // Nothing waiting was read after its request left.
        Assert.Equal(2, Volatile.Read(ref stoppedMidRead)); // Both running reads were stopped in the middle.
        Assert.All(fixture.Catalog.Read().Items, item => Assert.Equal(FolderSourceItemState.Seen, item.State));

        // No slot leaked: two new requests are read at the same time and finish.
        Volatile.Write(ref block, false);
        FolderSourceHashResult[] after = await Task.WhenAll(hasher.HashAsync(items[0].Id, FolderSourceHashPriority.UserAction),
            hasher.HashAsync(items[1].Id, FolderSourceHashPriority.View)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(after, result => Assert.Equal(FolderSourceHashOutcome.Hashed, result.Outcome));
    }

    [Fact]
    public async Task ARequestThatStillWaitsKeepsTheSharedReadGoing()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(1);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        hasher.HashFileForTests = async (_, item, token) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(token);
            return (FolderSourceHashOutcome.Hashed, Digest(item.RelativePath));
        };
        using CancellationTokenSource leaving = new();
        Task<FolderSourceHashResult> left = hasher.HashAsync(items[0].Id, FolderSourceHashPriority.View, leaving.Token);
        Task<FolderSourceHashResult> staying = hasher.HashAsync(items[0].Id, FolderSourceHashPriority.View, CancellationToken.None);
        await TestWait.UntilAsync(() => Volatile.Read(ref started) == 1);
        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        release.SetResult();
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await staying).Outcome);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Equal(FolderSourceItemState.Hashed, fixture.Catalog.Read().Item(items[0].Id)!.State); // A view commits at once.
    }

    [Fact]
    public async Task WaitingViewsAreBoundedAndTheOldestGiveWayToTheNewest()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(6);
        await using FolderSourceHasher hasher = new(fixture.Catalog) { Parallelism = 1, ViewQueueLimit = 3 };
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> read = new();
        hasher.HashFileForTests = async (_, item, token) =>
        {
            lock (read) read.Add(item.RelativePath);
            if (item.Id == items[0].Id) await release.Task.WaitAsync(token);
            return (FolderSourceHashOutcome.Hashed, Digest(item.RelativePath));
        };
        Task<FolderSourceHashResult> busy = hasher.HashAsync(items[0].Id, FolderSourceHashPriority.UserAction);
        await TestWait.UntilAsync(() => { lock (read) return read.Count == 1; });
        List<Task<FolderSourceHashResult>> views = new();
        for (int index = 1; index < 6; index++)
        {
            views.Add(hasher.HashAsync(items[index].Id, FolderSourceHashPriority.View));
            int queued = index;
            // Up to three wait; from the fourth on, each newer view makes the oldest waiting one give way.
            await TestWait.UntilAsync(() => queued <= 3 ? hasher.WaitingForSlot == queued : views[queued - 4].IsCompleted, "A view did not queue.");
        }
        // Five views waited for one slot with room for three: the two oldest gave way.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => views[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => views[1]);
        release.SetResult();
        await busy;
        FolderSourceHashResult[] kept = await Task.WhenAll(views.Skip(2));
        Assert.All(kept, result => Assert.Equal(FolderSourceHashOutcome.Hashed, result.Outcome));
        lock (read) Assert.Equal(new[] { items[0], items[3], items[4], items[5] }.Select(item => item.RelativePath), read);
    }

    [Fact]
    public async Task APreviewNeverReadsAnUnhashedFolderFileWhileTheGalleryStillDoes()
    {
        using Fixture fixture = new();
        File.WriteAllBytes(Path.Combine(fixture.Media, "a.png"), Png);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Media, "a.png"), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        FolderSourceRoot root = fixture.Catalog.AddRoot(fixture.Media, "Holiday", true, Now);
        fixture.Catalog.ApplyScan(root.Id, FolderSourceScanner.Scan(root.Path, true, 100, Array.Empty<string>()), Now);
        FolderSourceItem item = Assert.Single(fixture.Catalog.Read().Items);
        string directory = FolderSourceIdentity.VirtualDirectory(fixture.Profile, root.Id, item.Id);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        int reads = 0;
        hasher.HashFileForTests = (path, current, token) =>
        {
            Interlocked.Increment(ref reads);
            return FolderSourceHasher.HashFileAsync(root.Path, current, token);
        };
        using (new FolderSourceManifests(fixture.Catalog, hasher).Register())
        {
            // A tile (the page's manifest cache uses the preview reader) and the sound check: missing, nothing read.
            Assert.Equal(MediaManifestStatus.Missing, (await MediaAssetReader.OpenForPreviewAsync(directory)).Status);
            Assert.Equal(MediaManifestStatus.Missing, (await new MediaManifestCache().OpenAsync(directory)).Status);
            Assert.Equal(0, Volatile.Read(ref reads));
            Assert.Equal(FolderSourceItemState.Seen, fixture.Catalog.Read().Item(item.Id)!.State);

            // The opened gallery hashes it; afterwards the preview shows the same verified file.
            Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(directory)).Status);
            Assert.Equal(1, Volatile.Read(ref reads));
            MediaAssetSnapshot preview = await MediaAssetReader.OpenForPreviewAsync(directory);
            Assert.Equal(MediaManifestStatus.Available, preview.Status);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(), Assert.Single(preview.GetPage(0, 10)).Sha256);
            Assert.Equal(1, Volatile.Read(ref reads));
        }
    }

    [Fact]
    public async Task AReadStopsWhenItsTokenIsCancelled()
    {
        using Fixture fixture = new();
        string path = Path.Combine(fixture.Media, "big.webm");
        File.WriteAllBytes(path, new byte[4 * 1024 * 1024]);
        FileInfo info = new(path);
        FolderSourceItem item = new(Guid.NewGuid(), Guid.NewGuid(), "big.webm", info.Length, info.LastWriteTimeUtc.Ticks, null, SavedMediaKind.Video,
            FolderSourceItemState.Seen, Now);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderSourceHasher.HashFileAsync(fixture.Media, item, cancelled.Token));
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await FolderSourceHasher.HashFileAsync(fixture.Media, item, CancellationToken.None)).Outcome);
    }

    // ---- W3-05: the hash never starts on the caller's (UI) context ----

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); ThreadPool.QueueUserWorkItem(_ => d(state)); }
    }

    [Fact]
    public async Task AHashRequestedFromTheWindowReadsOnTheThreadPool()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(1);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        RecordingContext window = new();
        SynchronizationContext? seen = window;
        hasher.HashFileForTests = (_, item, _) =>
        {
            seen = SynchronizationContext.Current;
            return Task.FromResult<(FolderSourceHashOutcome, string?)>((FolderSourceHashOutcome.Hashed, Digest(item.RelativePath)));
        };
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<FolderSourceHashResult> hashing;
        SynchronizationContext.SetSynchronizationContext(window);
        try { hashing = hasher.HashAsync(items[0].Id, FolderSourceHashPriority.UserAction); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await hashing).Outcome);
        Assert.NotSame(window, seen);
    }

    // ---- W3-03: a full catalog can always be shrunk ----

    [Fact]
    public void AFullCatalogStillRemovesRenamesAndRecordsAndRemovingASourceMakesRoomAgain()
    {
        using Fixture fixture = new();
        (FolderSourceRoot big, FolderSourceItem[] items) = fixture.Synthetic(3_000, "big", prefix: new string('x', 120) + "/");
        long size = new FileInfo(fixture.Catalog.FilePath).Length;
        fixture.Catalog.SetLimitBytesForTests(size / 2);
        string other = Path.Combine(fixture.Temp.Path, "other");
        Directory.CreateDirectory(other);

        // Growing is refused…
        Assert.Equal(FolderSourceError.CatalogFull, Assert.Throws<FolderSourceException>(() => fixture.Catalog.AddRoot(other, "Other", true, Now)).Error);
        // …but renaming, recording a digest and a scan that only marks files missing still run.
        Assert.Equal("Big", fixture.Catalog.RenameRoot(big.Id, "Big").Name);
        Assert.True(fixture.Catalog.RecordHash(items[0].Id, items[0].Length, items[0].ModifiedTicks, Digest("0"), Now));
        FolderSourceScan shrunk = new(items.Skip(1).Select(item => new FolderSourceScannedFile(item.RelativePath, item.Length, item.ModifiedTicks, item.Kind))
            .Append(new FolderSourceScannedFile("new.png", 5, 5, SavedMediaKind.Image)).ToArray(), true, true, false, Array.Empty<string>(), 0);
        Assert.Equal(FolderSourceError.CatalogFull, Assert.Throws<FolderSourceException>(() => fixture.Catalog.ApplyScan(big.Id, shrunk, Now)).Error);
        FolderSourceCatalogSnapshot afterScan = fixture.Catalog.Read();
        Assert.Equal(FolderSourceItemState.Missing, afterScan.Item(items[0].Id)!.State); // What is gone was recorded…
        Assert.DoesNotContain(afterScan.Items, item => item.RelativePath == "new.png"); // …nothing new was added.
        Assert.Equal(FolderSourceScanStatus.LimitReached, afterScan.Root(big.Id)!.LastScanStatus);

        // Removing the source is the way out: the file gives the pages back and new folders can be added again.
        fixture.Catalog.RemoveRoot(big.Id);
        Assert.True(new FileInfo(fixture.Catalog.FilePath).Length < size / 2, "The catalog did not shrink after removing a source.");
        Assert.Equal("Other", fixture.Catalog.AddRoot(other, "Other", true, Now).Name);
    }

    [Fact]
    public void ACatalogFromBeforeIncrementalVacuumIsRewrittenOnceWhenASourceIsRemoved()
    {
        using Fixture fixture = new();
        (FolderSourceRoot big, _) = fixture.Synthetic(3_000, "big", prefix: new string('y', 120) + "/");
        using (SqliteConnection legacy = new("Data Source=" + fixture.Catalog.FilePath + ";Pooling=False"))
        {
            legacy.Open();
            using SqliteCommand command = legacy.CreateCommand();
            command.CommandText = "PRAGMA auto_vacuum=NONE; VACUUM;";
            command.ExecuteNonQuery();
        }
        long size = new FileInfo(fixture.Catalog.FilePath).Length;
        fixture.Catalog.RemoveRoot(big.Id);
        Assert.True(new FileInfo(fixture.Catalog.FilePath).Length < size / 2);
        using SqliteConnection check = new("Data Source=" + fixture.Catalog.FilePath + ";Mode=ReadOnly;Pooling=False");
        check.Open();
        using SqliteCommand mode = check.CreateCommand();
        mode.CommandText = "PRAGMA auto_vacuum";
        Assert.Equal(2L, Convert.ToInt64(mode.ExecuteScalar()));
        Assert.Empty(fixture.Catalog.Read().Roots);
    }

    // ---- W3-06: reads outside the profile-wide lock, batched digests, a view kept current ----

    [Fact]
    public async Task CatalogReadsDoNotWaitForTheProfileWideLeaseMonitor()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(10);
        // A second catalog on the same lease has no kept view, so every read below goes to the file. Its constructor checks the
        // lease (briefly taking the monitor), so it is made before the monitor is held elsewhere.
        FolderSourceCatalog fresh = new(fixture.Profile, fixture.Lease);
        using ManualResetEventSlim held = new(), release = new();
        Task holder = Task.Factory.StartNew(() => fixture.Lease.RunExclusive(() => { held.Set(); release.Wait(TimeSpan.FromSeconds(30)); return true; }),
            TaskCreationOptions.LongRunning);
        held.Wait(TimeSpan.FromSeconds(10));
        try
        {
            Task reads = Task.Run(() =>
            {
                Assert.Equal(10, fresh.Read().Items.Count);
                Assert.Single(fresh.ReadRoots());
                Assert.NotNull(fresh.Find(items[3].Id));
                Assert.Equal(10, fresh.Counts().Single().Files);
                Assert.Equal(10, fresh.NextUnhashed(100, long.MaxValue, 0).Items.Count);
            });
            Assert.Same(reads, await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(10))));
            await reads;
        }
        finally { release.Set(); await holder; }
    }

    [Fact]
    public void RecordedDigestsKeepTheViewCurrentWithoutReadingTheCatalogAgain()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(1_000);
        FolderSourceCatalogSnapshot first = fixture.Catalog.Read();
        int reads = fixture.Catalog.FullReads;
        for (int offset = 0; offset < items.Length; offset += 100)
        {
            IReadOnlyList<bool> recorded = fixture.Catalog.RecordHashes(items.Skip(offset).Take(100)
                .Select(item => new FolderSourceDigest(item.Id, item.Length, item.ModifiedTicks, Digest(item.RelativePath))).ToArray(), Now);
            Assert.All(recorded, Assert.True);
            FolderSourceCatalogSnapshot now = fixture.Catalog.Read();
            Assert.Equal(offset + 100, now.Items.Count(item => item.State == FolderSourceItemState.Hashed));
            Assert.Equal(Digest(items[offset].RelativePath), fixture.Catalog.Find(items[offset].Id)!.Value.Item.Sha256);
        }
        Assert.Equal(reads, fixture.Catalog.FullReads);
        Assert.All(first.Items, item => Assert.Equal(FolderSourceItemState.Seen, item.State)); // Earlier views never change.
        // The kept view is exactly what the file says.
        FolderSourceCatalogSnapshot kept = fixture.Catalog.Read();
        FolderSourceCatalogSnapshot file = new FolderSourceCatalog(fixture.Profile, fixture.Lease).Read();
        Assert.Equal(file.Revision, kept.Revision);
        Assert.Equal(file.Items, kept.Items);
        // A row that changed meanwhile is not recorded and the view stays true to the file.
        Assert.False(Assert.Single(fixture.Catalog.RecordHashes(new[] { new FolderSourceDigest(items[0].Id, items[0].Length, items[0].ModifiedTicks, Digest("x")) }, Now)));
        Assert.Equal(Digest(items[0].RelativePath), fixture.Catalog.Read().Item(items[0].Id)!.Sha256);
    }

    [Fact]
    public async Task TheBackgroundPassCommitsItsDigestsInBatches()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(250);
        long before = fixture.Catalog.Read().Revision;
        await using FolderSourceHasher hasher = new(fixture.Catalog) { Parallelism = 4 };
        int events = 0;
        hasher.Hashed += () => Interlocked.Increment(ref events);
        hasher.HashFileForTests = (_, item, _) => Task.FromResult<(FolderSourceHashOutcome, string?)>((FolderSourceHashOutcome.Hashed, Digest(item.RelativePath)));
        hasher.StartBackground();
        await TestWait.UntilAsync(() => !hasher.Status.BackgroundRunning && fixture.Catalog.Read().Items.All(item => item.State == FolderSourceItemState.Hashed),
            "The background pass did not finish.");
        long commits = fixture.Catalog.Read().Revision - before;
        Assert.InRange(commits, 1, 25); // 250 digests, not 250 transactions.
        Assert.InRange(Volatile.Read(ref events), 1, (int)commits);
        Assert.Equal(250, hasher.Status.HashedThisSession);
        Assert.Equal(FolderSourceHashOutcome.AlreadyHashed, (await hasher.HashAsync(items[7].Id, FolderSourceHashPriority.View)).Outcome);
    }

    [Fact]
    public async Task ABackgroundDigestNotCommittedYetIsNeverReadAgain()
    {
        using Fixture fixture = new();
        (_, FolderSourceItem[] items) = fixture.Synthetic(1);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        int reads = 0;
        hasher.HashFileForTests = (_, item, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<(FolderSourceHashOutcome, string?)>((FolderSourceHashOutcome.Hashed, Digest(item.RelativePath)));
        };
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await hasher.HashAsync(items[0].Id, FolderSourceHashPriority.Background)).Outcome);
        // Whether or not its batch was committed meanwhile, a view finds the digest and does not read the file again.
        FolderSourceHashResult view = await hasher.HashAsync(items[0].Id, FolderSourceHashPriority.View);
        Assert.Equal(FolderSourceHashOutcome.AlreadyHashed, view.Outcome);
        Assert.Equal(Digest(items[0].RelativePath), view.Item!.Sha256);
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(FolderSourceItemState.Hashed, fixture.Catalog.Read().Item(items[0].Id)!.State);
    }

    // ---- W3-08: links versus cloud placeholders ----

    [Theory]
    [InlineData(FileAttributes.Normal, null, true, "Plain")]
    [InlineData(FileAttributes.Directory, null, false, "Plain")]
    [InlineData(FileAttributes.ReparsePoint, null, false, "Link")] // Unix: a symbolic link
    [InlineData(FileAttributes.ReparsePoint, 0x9000001Au, false, "Link")] // No tags off Windows
    [InlineData(FileAttributes.ReparsePoint, null, true, "Link")] // Tag unreadable: fail closed
    [InlineData(FileAttributes.ReparsePoint, 0xA000000Cu, true, "Link")] // IO_REPARSE_TAG_SYMLINK
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, 0xA0000003u, true, "Link")] // junction
    [InlineData(FileAttributes.ReparsePoint, 0xA000001Du, true, "Link")] // WSL symbolic link
    [InlineData(FileAttributes.ReparsePoint, 0x9000001Au, true, "CloudPlaceholder")] // IO_REPARSE_TAG_CLOUD
    [InlineData(FileAttributes.ReparsePoint, 0x9000601Au, true, "CloudPlaceholder")] // IO_REPARSE_TAG_CLOUD_6 (OneDrive)
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, 0x9000F01Au, true, "CloudPlaceholder")] // CLOUD_F folder
    [InlineData(FileAttributes.ReparsePoint, 0x80000013u, true, "Plain")] // deduplicated file
    [InlineData(FileAttributes.ReparsePoint, 0x80000017u, true, "Plain")] // WOF compressed file
    [InlineData(FileAttributes.ReparsePoint, 0x8000001Bu, true, "Link")] // app execution alias: unknown, not followed
    [InlineData(FileAttributes.ReparsePoint, 0xC0000004u, true, "Link")] // HSM: unknown, not followed
    public void ReparsePointsAreToldApartByTheirTag(FileAttributes attributes, uint? tag, bool windows, string expected) =>
        Assert.Equal(Enum.Parse<FolderSourceEntryKind>(expected), FolderSourceEntries.Classify(attributes, tag, windows));

    [Fact]
    public void OnlyFilesWhoseContentIsElsewhereNeedARecall()
    {
        Assert.False(FolderSourceEntries.NeedsRecall(FileAttributes.Normal));
        Assert.False(FolderSourceEntries.NeedsRecall(FileAttributes.ReparsePoint | FileAttributes.Archive)); // a hydrated placeholder
        Assert.True(FolderSourceEntries.NeedsRecall(FileAttributes.ReparsePoint | FolderSourceEntries.RecallOnDataAccess)); // online-only
        Assert.True(FolderSourceEntries.NeedsRecall(FolderSourceEntries.RecallOnOpen));
        Assert.True(FolderSourceEntries.NeedsRecall(FileAttributes.Offline));
    }

    [Fact]
    public void OnThisSystemALinkIsALinkAndAFileIsPlain()
    {
        using Fixture fixture = new();
        string file = Path.Combine(fixture.Media, "a.png");
        File.WriteAllBytes(file, Png);
        FileInfo plain = new(file);
        Assert.Equal(FolderSourceEntryKind.Plain, FolderSourceEntries.Of(plain, plain.Attributes));
        if (OperatingSystem.IsWindows()) return; // Creating links needs a privilege there; the classification above covers the tags.
        string link = Path.Combine(fixture.Media, "link.png");
        File.CreateSymbolicLink(link, file);
        FileInfo linked = new(link);
        Assert.True(FolderSourceEntries.IsLink(linked, linked.Attributes));
    }
}
