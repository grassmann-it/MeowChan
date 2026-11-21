using System.Diagnostics;
using System.Security.Cryptography;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.FolderSources;

/// <summary>
/// Read-only folder sources (PLAN-POWER-TAGGEN PR2): the shadow catalog (schema, damage, limits), the scanner (no hashing,
/// no links, exclusions, unreadable folders), the rescan semantics (missing, changed bytes, revival), lazy hashing with
/// priorities, the virtual manifests and the media index projection. Temp folders only; every test that touches a user
/// folder checks it is byte- and timestamp-identical afterwards.
/// </summary>
public sealed class FolderSourceTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6, 7 };
    private static readonly byte[] Webm = { 0x1A, 0x45, 0xDF, 0xA3, 9, 9 };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T22:00:00Z");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public readonly TempDirectory Temp = new();
        public string Profile => Path.Combine(Temp.Path, "profile");
        public string Downloads => Path.Combine(Temp.Path, "downloads");
        public string Media => Path.Combine(Temp.Path, "media");
        public readonly ProfileLease Lease;
        public readonly FolderSourceCatalog Catalog;

        public Fixture()
        {
            Directory.CreateDirectory(Media);
            Lease = ProfileLease.Acquire(Profile);
            Catalog = new FolderSourceCatalog(Profile, Lease);
        }

        public string Write(string relative, byte[] bytes, DateTime? modified = null)
        {
            string path = Path.Combine(Media, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, modified ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            return path;
        }

        public FolderSourceRoot AddRoot(bool subfolders = true) => Catalog.AddRoot(Media, "Holiday", subfolders, Now);

        public FolderSourceScanResult Scan(FolderSourceRoot root, int maximum = 1_000) =>
            Catalog.ApplyScan(root.Id, FolderSourceScanner.Scan(root.Path, root.IncludeSubfolders, maximum, new[] { Profile, Downloads }), Now);

        public void Dispose() { Lease.Dispose(); Temp.Dispose(); }
    }

    /// <summary>Names, lengths, write times and content digests of everything below a folder (links listed, not followed).</summary>
    private static IReadOnlyList<string> Fingerprint(string folder)
    {
        List<string> lines = new();
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                     .Order(StringComparer.Ordinal))
        {
            FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
            string relative = Path.GetRelativePath(folder, entry);
            if (info.LinkTarget != null) { lines.Add(relative + " -> " + info.LinkTarget); continue; }
            if (info is FileInfo file)
                lines.Add(relative + " " + file.Length + " " + file.LastWriteTimeUtc.Ticks + " " + Sha(File.ReadAllBytes(entry)));
            else lines.Add(relative + "/ " + info.LastWriteTimeUtc.Ticks);
        }
        return lines;
    }

    // ---- Catalog ----

    [Fact]
    public void CatalogIsEmptyWithoutAFileAndCreatesItOnlyForTheFirstFolder()
    {
        using Fixture fixture = new();
        FolderSourceCatalogSnapshot empty = fixture.Catalog.Read();
        Assert.Empty(empty.Roots);
        Assert.Empty(empty.Items);
        Assert.False(File.Exists(fixture.Catalog.FilePath));
        FolderSourceRoot root = fixture.AddRoot();
        Assert.True(File.Exists(fixture.Catalog.FilePath));
        Assert.Equal(Path.Combine(fixture.Profile, "folder-sources.db"), fixture.Catalog.FilePath);
        Assert.Equal("Holiday", Assert.Single(fixture.Catalog.Read().Roots).Name);
        Assert.Equal(FolderSourceIdentity.Thread(root.Id), root.Thread);
        Assert.True(FolderSourceIdentity.IsFolderThread(root.Thread));
        Assert.Equal("Trip", fixture.Catalog.RenameRoot(root.Id, "  Trip ").Name);
        Assert.Throws<FolderSourceException>(() => fixture.Catalog.RenameRoot(root.Id, " "));
        fixture.Catalog.RemoveRoot(root.Id);
        Assert.Empty(fixture.Catalog.Read().Roots);
        Assert.Equal(FolderSourceError.UnknownRoot, Assert.Throws<FolderSourceException>(() => fixture.Catalog.RemoveRoot(root.Id)).Error);
    }

    [Fact]
    public void ADamagedForeignOrNewerCatalogIsNeverReadAsNoSources()
    {
        using Fixture fixture = new();
        fixture.AddRoot();
        File.WriteAllText(fixture.Catalog.FilePath, "not a database at all, just text that is long enough to be a header ............");
        FolderSourceCatalog reopened = new(fixture.Profile, fixture.Lease);
        Assert.Equal(FolderSourceError.CatalogDamaged, Assert.Throws<FolderSourceException>(() => reopened.Read()).Error);

        File.Delete(fixture.Catalog.FilePath);
        using (SqliteConnection foreign = new("Data Source=" + fixture.Catalog.FilePath + ";Pooling=False"))
        {
            foreign.Open();
            using SqliteCommand command = foreign.CreateCommand();
            command.CommandText = "PRAGMA application_id=1; CREATE TABLE other(x);";
            command.ExecuteNonQuery();
        }
        Assert.Equal(FolderSourceError.CatalogDamaged, Assert.Throws<FolderSourceException>(() => new FolderSourceCatalog(fixture.Profile, fixture.Lease).Read()).Error);

        File.Delete(fixture.Catalog.FilePath);
        FolderSourceCatalog fresh = new(fixture.Profile, fixture.Lease);
        fresh.AddRoot(fixture.Media, "x", true, Now);
        using (SqliteConnection newer = new("Data Source=" + fixture.Catalog.FilePath + ";Pooling=False"))
        {
            newer.Open();
            using SqliteCommand command = newer.CreateCommand();
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        Assert.Equal(FolderSourceError.CatalogNewer, Assert.Throws<FolderSourceException>(() => new FolderSourceCatalog(fixture.Profile, fixture.Lease).Read()).Error);
        Assert.Throws<FolderSourceException>(() => new FolderSourceCatalog(fixture.Profile, fixture.Lease).AddRoot(fixture.Downloads, "y", true, Now));
    }

    [Fact]
    public void AnInvalidRowMakesTheCatalogDamagedInsteadOfAGuess()
    {
        using Fixture fixture = new();
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Write("a.png", Png);
        fixture.Scan(root);
        using (SqliteConnection connection = new("Data Source=" + fixture.Catalog.FilePath + ";Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE items SET rel_path = '../escape.png'; UPDATE meta SET value = '99' WHERE key = 'revision';";
            command.ExecuteNonQuery();
        }
        Assert.Equal(FolderSourceError.CatalogDamaged,
            Assert.Throws<FolderSourceException>(() => new FolderSourceCatalog(fixture.Profile, fixture.Lease).Read()).Error);
    }

    [Fact]
    public void RootsAreLimitedAndNeverOverlapAndTheValidationRefusesProfileAndDownloads()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Downloads);
        Directory.CreateDirectory(Path.Combine(fixture.Media, "inner"));
        Directory.CreateDirectory(Path.Combine(fixture.Downloads, "inside"));
        fixture.Catalog.MaxRoots = 1;
        Assert.Equal(1, fixture.Catalog.MaxRoots);
        FolderSourceRoot root = fixture.AddRoot();
        Assert.Equal(FolderSourceError.TooManyRoots, Assert.Throws<FolderSourceException>(() =>
            fixture.Catalog.AddRoot(fixture.Downloads, "other", true, Now)).Error);
        fixture.Catalog.MaxRoots = 8;
        Assert.Equal(FolderSourceError.FolderOverlaps, Assert.Throws<FolderSourceException>(() =>
            fixture.Catalog.AddRoot(Path.Combine(fixture.Media, "inner"), "inner", true, Now)).Error);
        IReadOnlyList<FolderSourceRoot> roots = fixture.Catalog.ReadRoots();
        Assert.Equal(FolderSourceError.FolderOverlaps, FolderSourceValidation.Check(Path.Combine(fixture.Media, "inner"), fixture.Profile, fixture.Downloads, roots));
        Assert.Equal(FolderSourceError.FolderOverlaps, FolderSourceValidation.Check(fixture.Temp.Path, Path.Combine(fixture.Temp.Path, "elsewhere"), Path.Combine(fixture.Temp.Path, "dl2"), roots));
        Assert.Equal(FolderSourceError.FolderIsProfile, FolderSourceValidation.Check(fixture.Profile, fixture.Profile, fixture.Downloads, Array.Empty<FolderSourceRoot>()));
        Assert.Equal(FolderSourceError.FolderIsDownloads, FolderSourceValidation.Check(Path.Combine(fixture.Downloads, "inside"), fixture.Profile, fixture.Downloads, roots));
        Assert.Equal(FolderSourceError.FolderUnavailable, FolderSourceValidation.Check(Path.Combine(fixture.Temp.Path, "missing"), fixture.Profile, fixture.Downloads, roots));
        Assert.Equal(FolderSourceError.FolderUnavailable, FolderSourceValidation.Check(" ", fixture.Profile, fixture.Downloads, roots));
        // A folder that holds the profile and the download folder may be a source: the scan skips both.
        Assert.Equal(FolderSourceError.None, FolderSourceValidation.Check(fixture.Temp.Path, fixture.Profile, fixture.Downloads, Array.Empty<FolderSourceRoot>()));
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(fixture.Temp.Path, "link");
            Directory.CreateSymbolicLink(link, fixture.Downloads);
            Assert.Equal(FolderSourceError.FolderUnavailable, FolderSourceValidation.Check(link, fixture.Profile, Path.Combine(fixture.Temp.Path, "dl3"), roots));
        }
        Assert.Equal(root.Id, Assert.Single(fixture.Catalog.ReadRoots()).Id);
    }

    // ---- Scanner and rescan ----

    [Fact]
    public void AScanListsImagesAndVideosWithoutHashingFollowsNoLinkAndSkipsProfileAndDownloads()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("sub/b.JPG", Jpg);
        fixture.Write("sub/deeper/c.webm", Webm);
        fixture.Write("notes.txt", new byte[] { 1 });
        fixture.Write("song.mp3", new byte[] { 2 });
        if (!OperatingSystem.IsWindows())
        {
            string outside = Path.Combine(fixture.Temp.Path, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "secret.png"), Png);
            Directory.CreateSymbolicLink(Path.Combine(fixture.Media, "linked-folder"), outside);
            File.CreateSymbolicLink(Path.Combine(fixture.Media, "linked.png"), Path.Combine(outside, "secret.png"));
        }
        IReadOnlyList<string> before = Fingerprint(fixture.Media);
        FolderSourceScan scan = FolderSourceScanner.Scan(fixture.Media, true, 1_000, new[] { fixture.Profile });
        Assert.True(scan.Complete);
        Assert.Equal(new[] { "a.png", "sub/b.JPG", "sub/deeper/c.webm" }, scan.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(SavedMediaKind.Video, scan.Files.Single(file => file.RelativePath.EndsWith(".webm")).Kind);
        if (!OperatingSystem.IsWindows()) Assert.Equal(2, scan.SkippedLinks);
        Assert.Equal(new[] { "a.png" }, FolderSourceScanner.Scan(fixture.Media, false, 1_000, Array.Empty<string>()).Files.Select(file => file.RelativePath));
        // The excluded folder (here: "sub" playing the download folder) is skipped with everything below it.
        Assert.Equal(new[] { "a.png" }, FolderSourceScanner.Scan(fixture.Media, true, 1_000, new[] { Path.Combine(fixture.Media, "sub") }).Files.Select(file => file.RelativePath));
        FolderSourceScan limited = FolderSourceScanner.Scan(fixture.Media, true, 2, Array.Empty<string>());
        Assert.True(limited.LimitReached);
        Assert.Equal(2, limited.Files.Count);
        Assert.False(FolderSourceScanner.Scan(Path.Combine(fixture.Temp.Path, "gone"), true, 10, Array.Empty<string>()).RootAvailable);
        Assert.Equal(before, Fingerprint(fixture.Media));
    }

    [Fact]
    public void TruncatedScanDoesNotMarkExistingKnownFilesMissingButFullScanStillDoes()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("b.png", Jpg);
        fixture.Write("c.png", Png);
        FolderSourceRoot root = fixture.AddRoot();
        Assert.Equal(3, fixture.Scan(root).Added);
        // The limit drops b and c from the listing, but both still exist: nothing is missing.
        FolderSourceScanResult limited = fixture.Scan(root, 1);
        Assert.Equal(FolderSourceScanStatus.LimitReached, limited.Status);
        Assert.Equal(0, limited.Missing);
        Assert.All(fixture.Catalog.Read().Items, item => Assert.NotEqual(FolderSourceItemState.Missing, item.State));
        // A file really deleted is still not missing when the scan is cut short before it, but is detected by a full scan.
        File.Delete(Path.Combine(fixture.Media, "c.png"));
        fixture.Scan(root, 1);
        FolderSourceScanResult full = fixture.Scan(root);
        Assert.Equal(FolderSourceScanStatus.Complete, full.Status);
        Assert.Equal(FolderSourceItemState.Missing, fixture.Catalog.Read().Items.Single(item => item.RelativePath == "c.png").State);
        Assert.Equal(2, fixture.Catalog.Read().Items.Count(item => item.State != FolderSourceItemState.Missing));
    }

    [Fact]
    public void RescanMarksMissingKeepsUnreadableSubfoldersAndRevivesReturningFiles()
    {
        if (OperatingSystem.IsWindows()) return; // Unreadable folders are made with POSIX permissions.
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        string locked = Path.GetDirectoryName(fixture.Write("locked/b.png", Jpg))!;
        FolderSourceRoot root = fixture.AddRoot();
        FolderSourceScanResult first = fixture.Scan(root);
        Assert.Equal((FolderSourceScanStatus.Complete, 2, 2), (first.Status, first.Files, first.Added));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        bool enforced;
        try { _ = Directory.EnumerateFileSystemEntries(locked).Any(); enforced = false; } // Running as root: permissions do not apply.
        catch (UnauthorizedAccessException) { enforced = true; }
        try
        {
            if (enforced)
            {
                FolderSourceScanResult second = fixture.Scan(root);
                Assert.Equal(FolderSourceScanStatus.Incomplete, second.Status);
                Assert.Equal(0, second.Missing);
                Assert.All(fixture.Catalog.Read().Items, item => Assert.NotEqual(FolderSourceItemState.Missing, item.State));
            }
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        string a = Path.Combine(fixture.Media, "a.png");
        string parked = Path.Combine(fixture.Temp.Path, "parked.png");
        File.Move(a, parked);
        FolderSourceScanResult gone = fixture.Scan(root);
        Assert.Equal(1, gone.Missing);
        FolderSourceItem missing = fixture.Catalog.Read().Items.Single(item => item.RelativePath == "a.png");
        Assert.Equal(FolderSourceItemState.Missing, missing.State);
        File.Move(parked, a);
        FolderSourceScanResult back = fixture.Scan(root);
        Assert.Equal(1, back.Revived);
        FolderSourceItem revived = fixture.Catalog.Read().Items.Single(item => item.RelativePath == "a.png");
        Assert.Equal(missing.Id, revived.Id);
        Assert.Equal(FolderSourceItemState.Seen, revived.State);
    }

    [Fact]
    public async Task ChangedBytesOfAHashedFileMakeANewItemWhileAnUnhashedOneIsUpdatedInPlace()
    {
        using Fixture fixture = new();
        string a = fixture.Write("a.png", Png);
        string b = fixture.Write("b.jpg", Jpg);
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        FolderSourceItem hashedBefore = fixture.Catalog.Read().Items.Single(item => item.RelativePath == "a.png");
        FolderSourceItem unhashedBefore = fixture.Catalog.Read().Items.Single(item => item.RelativePath == "b.jpg");
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await hasher.HashAsync(hashedBefore.Id, FolderSourceHashPriority.View)).Outcome);

        File.WriteAllBytes(a, Webm.Concat(Png).ToArray());
        File.SetLastWriteTimeUtc(a, new DateTime(2026, 5, 5, 5, 5, 5, DateTimeKind.Utc));
        File.WriteAllBytes(b, Png.Concat(Jpg).ToArray());
        File.SetLastWriteTimeUtc(b, new DateTime(2026, 5, 5, 5, 5, 5, DateTimeKind.Utc));
        FolderSourceScanResult result = fixture.Scan(root);
        Assert.Equal((1, 1), (result.Replaced, result.Changed));
        FolderSourceCatalogSnapshot after = fixture.Catalog.Read();
        FolderSourceItem old = after.Item(hashedBefore.Id)!;
        Assert.Equal(FolderSourceItemState.Missing, old.State);
        Assert.Equal(Sha(Png), old.Sha256);
        FolderSourceItem replacement = after.Items.Single(item => item.RelativePath == "a.png" && item.State != FolderSourceItemState.Missing);
        Assert.NotEqual(old.Id, replacement.Id);
        Assert.Null(replacement.Sha256);
        FolderSourceItem updated = after.Item(unhashedBefore.Id)!;
        Assert.Equal((FolderSourceItemState.Seen, Png.Length + Jpg.Length), (updated.State, (int)updated.Length));
    }

    [Fact]
    public void TheFileLimitCountsActiveFilesOverAllRootsAndNeverDropsKnownOnes()
    {
        using Fixture fixture = new();
        for (int index = 0; index < 5; index++) fixture.Write("f" + index + ".png", Png.Append((byte)index).ToArray());
        string second = Path.Combine(fixture.Temp.Path, "second");
        Directory.CreateDirectory(second);
        for (int index = 0; index < 5; index++) File.WriteAllBytes(Path.Combine(second, "g" + index + ".png"), Png);
        fixture.Catalog.MaxFiles = FolderSourceCatalog.MinimumMaxFiles;
        Assert.Equal(FolderSourceCatalog.MinimumMaxFiles, fixture.Catalog.MaxFiles);
        FolderSourceRoot first = fixture.AddRoot();
        Assert.Equal(5, fixture.Scan(first).Added);
        FolderSourceRoot other = fixture.Catalog.AddRoot(second, "second", true, Now);
        // A scan that found more files than the budget leaves the rest out and says so.
        FolderSourceScan many = new(Enumerable.Range(0, FolderSourceCatalog.MinimumMaxFiles).Select(index =>
            new FolderSourceScannedFile("x" + index + ".png", 1, 1, SavedMediaKind.Image)).ToArray(), true, true, false, Array.Empty<string>(), 0);
        FolderSourceScanResult capped = fixture.Catalog.ApplyScan(other.Id, many, Now);
        Assert.Equal(FolderSourceScanStatus.LimitReached, capped.Status);
        Assert.Equal(FolderSourceCatalog.MinimumMaxFiles - 5, capped.Added);
        Assert.Equal(5, fixture.Scan(first).Unchanged);
    }

    // ---- Hashing ----

    [Fact]
    public async Task HashingRecordsOnlyUnchangedBytesHonorsTheSizeLimitAndRefusesLinkedFolders()
    {
        using Fixture fixture = new();
        string a = fixture.Write("a.png", Png);
        fixture.Write("big.webm", new byte[4096]);
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        FolderSourceCatalogSnapshot snapshot = fixture.Catalog.Read();
        FolderSourceItem item = snapshot.Items.Single(candidate => candidate.RelativePath == "a.png");
        FolderSourceItem big = snapshot.Items.Single(candidate => candidate.RelativePath == "big.webm");
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        IReadOnlyList<string> before = Fingerprint(fixture.Media);

        FolderSourceHashResult hashed = await hasher.HashAsync(item.Id, FolderSourceHashPriority.View);
        Assert.Equal(FolderSourceHashOutcome.Hashed, hashed.Outcome);
        Assert.Equal(Sha(Png), hashed.Item!.Sha256);
        Assert.Equal(FolderSourceItemState.Hashed, fixture.Catalog.Read().Item(item.Id)!.State);
        Assert.Equal(FolderSourceHashOutcome.AlreadyHashed, (await hasher.HashAsync(item.Id, FolderSourceHashPriority.Background)).Outcome);

        // Above the size limit only a user action reads the file.
        hasher.MaxFileBytes = 1; // Clamped to the 1 GiB minimum; the test uses the seam below for "too large".
        Assert.Equal(1L << 30, hasher.MaxFileBytes);
        Assert.True(big.Length < hasher.MaxFileBytes);
        Assert.Equal(before, Fingerprint(fixture.Media));

        // A file that changed after the scan is never given a digest.
        string c = fixture.Write("c.png", Jpg);
        fixture.Scan(root);
        FolderSourceItem changed = fixture.Catalog.Read().Items.Single(candidate => candidate.RelativePath == "c.png");
        File.SetLastWriteTimeUtc(c, new DateTime(2026, 9, 9, 9, 9, 9, DateTimeKind.Utc));
        Assert.Equal(FolderSourceHashOutcome.Changed, (await hasher.HashAsync(changed.Id, FolderSourceHashPriority.UserAction)).Outcome);
        Assert.Null(fixture.Catalog.Read().Item(changed.Id)!.Sha256);
        File.Delete(c);
        Assert.Equal(FolderSourceHashOutcome.Missing, (await hasher.HashAsync(changed.Id, FolderSourceHashPriority.UserAction)).Outcome);

        if (!OperatingSystem.IsWindows())
        {
            // A subfolder replaced by a link after the scan is not followed.
            string sub = Path.GetDirectoryName(fixture.Write("sub/d.png", Png))!;
            fixture.Scan(root);
            FolderSourceItem inside = fixture.Catalog.Read().Items.Single(candidate => candidate.RelativePath == "sub/d.png");
            string elsewhere = Path.Combine(fixture.Temp.Path, "elsewhere");
            Directory.Move(sub, elsewhere);
            Directory.CreateSymbolicLink(sub, elsewhere);
            Assert.Equal(FolderSourceHashOutcome.Unreadable, (await hasher.HashAsync(inside.Id, FolderSourceHashPriority.UserAction)).Outcome);
        }
        (FolderSourceHashOutcome tooLarge, _) = await FolderSourceHasher.HashFileAsync(root.Path, big with { Length = big.Length + 1 }, CancellationToken.None);
        Assert.Equal(FolderSourceHashOutcome.Changed, tooLarge);
    }

    [Fact]
    public async Task TheSizeLimitAppliesToViewAndBackgroundButNotToAUserAction()
    {
        using Fixture fixture = new();
        FolderSourceRoot root = fixture.AddRoot();
        FolderSourceScan scan = new(new[] { new FolderSourceScannedFile("huge.webm", 5L << 30, 7, SavedMediaKind.Video) }, true, true, false, Array.Empty<string>(), 0);
        fixture.Catalog.ApplyScan(root.Id, scan, Now);
        FolderSourceItem huge = Assert.Single(fixture.Catalog.Read().Items);
        await using FolderSourceHasher hasher = new(fixture.Catalog) { MaxFileBytes = 1L << 30 };
        hasher.HashFileForTests = (_, _, _) => Task.FromResult<(FolderSourceHashOutcome, string?)>((FolderSourceHashOutcome.Hashed, new string('a', 64)));
        Assert.Equal(FolderSourceHashOutcome.TooLarge, (await hasher.HashAsync(huge.Id, FolderSourceHashPriority.View)).Outcome);
        Assert.Equal(FolderSourceHashOutcome.TooLarge, (await hasher.HashAsync(huge.Id, FolderSourceHashPriority.Background)).Outcome);
        Assert.Empty(fixture.Catalog.NextUnhashed(10, hasher.MaxFileBytes, 0).Items);
        Assert.Equal(FolderSourceHashOutcome.Hashed, (await hasher.HashAsync(huge.Id, FolderSourceHashPriority.UserAction)).Outcome);
    }

    [Fact]
    public async Task AWaitingUserActionIsServedBeforeWaitingViewsAndTheBackgroundAndRequestsAreShared()
    {
        using Fixture fixture = new();
        FolderSourceRoot root = fixture.AddRoot();
        FolderSourceScan scan = new(Enumerable.Range(0, 4).Select(index => new FolderSourceScannedFile(index + ".png", 10, index, SavedMediaKind.Image)).ToArray(),
            true, true, false, Array.Empty<string>(), 0);
        fixture.Catalog.ApplyScan(root.Id, scan, Now);
        FolderSourceItem[] items = fixture.Catalog.Read().Items.OrderBy(item => item.RelativePath).ToArray();
        await using FolderSourceHasher hasher = new(fixture.Catalog) { Parallelism = 1 };
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> order = new();
        int calls = 0;
        hasher.HashFileForTests = async (path, item, token) =>
        {
            Interlocked.Increment(ref calls);
            lock (order) order.Add(item.RelativePath);
            if (item.RelativePath == "0.png") await release.Task;
            return (FolderSourceHashOutcome.Hashed, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.RelativePath))).ToLowerInvariant());
        };
        Task<FolderSourceHashResult> first = hasher.HashAsync(items[0].Id, FolderSourceHashPriority.Background);
        await TestWait.UntilAsync(() => Volatile.Read(ref calls) == 1);
        Task<FolderSourceHashResult> background = hasher.HashAsync(items[1].Id, FolderSourceHashPriority.Background);
        Task<FolderSourceHashResult> view = hasher.HashAsync(items[2].Id, FolderSourceHashPriority.View);
        Task<FolderSourceHashResult> user = hasher.HashAsync(items[3].Id, FolderSourceHashPriority.UserAction);
        Task<FolderSourceHashResult> shared = hasher.HashAsync(items[3].Id, FolderSourceHashPriority.View);
        // HashAsync starts jobs on the thread pool. Wait until every queued request has reached the
        // priority queue before releasing the first job; a fixed delay races with slower Windows runners.
        await TestWait.UntilAsync(() => hasher.WaitingForSlot == 3);
        release.SetResult();
        await Task.WhenAll(first, background, view, user, shared);
        Assert.Equal(new[] { "0.png", "3.png", "2.png", "1.png" }, order);
        Assert.Equal(4, calls);
        await hasher.FlushAsync(); // Background digests are committed in batches (W3-06).
        Assert.All(fixture.Catalog.Read().Items, item => Assert.Equal(FolderSourceItemState.Hashed, item.State));
    }

    [Fact]
    public async Task TheBackgroundPassHashesEverythingOnlyWhileIdleAndNotPaused()
    {
        using Fixture fixture = new();
        for (int index = 0; index < 6; index++) fixture.Write(index + ".png", Png.Append((byte)index).ToArray());
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        await using FolderSourceHasher hasher = new(fixture.Catalog) { Parallelism = 2 };
        hasher.Paused = true;
        hasher.StartBackground();
        await Task.Delay(200);
        Assert.All(fixture.Catalog.Read().Items, item => Assert.Equal(FolderSourceItemState.Seen, item.State));
        hasher.Paused = false;
        await TestWait.UntilAsync(() => fixture.Catalog.Read().Items.All(item => item.State == FolderSourceItemState.Hashed), "Background pass did not finish.");
        Assert.Equal(6, hasher.Status.HashedThisSession);
        await hasher.StopBackgroundAsync();
    }

    // ---- Virtual manifests, projection and index ----

    [Fact]
    public async Task AVirtualManifestHashesOnOpenAndServesTheVerifiedFileWithoutWritingAnything()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        IReadOnlyList<string> before = Fingerprint(fixture.Media);
        FolderSourceItem item = Assert.Single(fixture.Catalog.Read().Items);
        string directory = FolderSourceIdentity.VirtualDirectory(fixture.Profile, root.Id, item.Id);
        // Not registered: the folder is read as an ordinary media folder, which does not exist.
        Assert.False(await AnsweredAsync(directory));
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        using (new FolderSourceManifests(fixture.Catalog, hasher).Register())
        {
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            Assert.Equal(MediaManifestStatus.Available, manifest.Status);
            MediaAssetReference asset = Assert.Single(manifest.GetPage(0, 10));
            Assert.Equal((item.AssetId, Sha(Png), (long)Png.Length, item.RequestedFileName, "a.png"), (asset.AssetId, asset.Sha256, asset.Length, asset.RequestedFileName, asset.OriginalFileName));
            await using (Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId))
                Assert.Equal(Png, ReadAll(verified));
            Assert.Equal(MediaManifestStatus.Missing, (await MediaAssetReader.OpenAsync(FolderSourceIdentity.VirtualDirectory(fixture.Profile, root.Id, Guid.NewGuid()))).Status);
            Assert.Equal(MediaManifestStatus.Missing, (await MediaAssetReader.OpenAsync(Path.Combine(FolderSourceIdentity.VirtualRoot(fixture.Profile), "junk"))).Status);
        }
        Assert.False(await AnsweredAsync(directory));
        Assert.False(Directory.Exists(FolderSourceIdentity.VirtualRoot(fixture.Profile)));
        Assert.Equal(before, Fingerprint(fixture.Media));
    }

    [Fact]
    public async Task TheIndexShowsUnhashedFilesAsPresentLooseEntriesAndNeverSavesThemInTheCache()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("b.webm", Webm);
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        FolderSourceItem a = fixture.Catalog.Read().Items.Single(item => item.RelativePath == "a.png");
        await hasher.HashAsync(a.Id, FolderSourceHashPriority.View);
        MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(_ => throw new FileNotFoundException(),
            (_, _) => throw new InvalidOperationException("No manifest is opened for folder files."), readFolderSources: fixture.Catalog.Read);
        Assert.True(index.Complete);
        Assert.Equal(2, index.Entries.Count);
        MediaLibraryEntry hashed = index.Entries.Single(entry => entry.Name == "a.png");
        MediaLibraryEntry unhashed = index.Entries.Single(entry => entry.Name == "b.webm");
        Assert.All(index.Entries, entry =>
        {
            Assert.True(entry.IsCatalogMedium);
            Assert.Equal(MediaLibraryPresence.Present, entry.Presence);
            Assert.Equal(root.Thread, entry.Thread);
            Assert.Equal("Holiday", entry.ThreadTitle);
            Assert.NotNull(entry.LocalSlot);
        });
        Assert.Equal(Sha(Png), hashed.Sha256);
        Assert.Null(unhashed.Sha256);
        Assert.Equal(SavedMediaKind.Video, unhashed.Kind);
        Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(hashed));
        Assert.Null(MediaTagSubjects.Of(unhashed)); // Never written to before its bytes are known.
        Assert.NotNull(MediaLibraryFilterEngine.AssetKey(hashed));
        Assert.Null(MediaLibraryFilterEngine.AssetKey(unhashed));

        string cache = Path.Combine(fixture.Temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibraryIndexCacheFile file = new(cache);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await file.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        MediaLibrarySnapshot? loaded = await new MediaLibraryIndexCacheFile(cache).LoadAsync();
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Entries);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Unchanged, (await file.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes)).Status);

        // An unreadable catalog keeps what the previous index showed and marks the index incomplete.
        MediaLibrarySnapshot kept = await MediaLibraryIndexBuilder.BuildAsync(_ => throw new FileNotFoundException(),
            (_, _) => throw new InvalidOperationException(), previous: index, readFolderSources: () => null);
        Assert.False(kept.Complete);
        Assert.Equal(2, kept.Entries.Count);
    }

    [Fact]
    public async Task TheGalleryResolvesFolderFilesHashingOnlyTheNearestUnhashedOnes()
    {
        using Fixture fixture = new();
        for (int index = 0; index < 5; index++) fixture.Write(index + ".png", Png.Append((byte)index).ToArray());
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        using IDisposable registration = new FolderSourceManifests(fixture.Catalog, hasher).Register();
        IReadOnlyList<MediaLibraryEntry> entries = FolderSourceProjection.EntriesOf(fixture.Catalog.Read()).OrderBy(entry => entry.Name).ToArray();
        IReadOnlyList<GalleryEntry> gallery = await MediaLibraryGallery.ResolveAsync(entries, _ => throw new FileNotFoundException(),
            MediaAssetReader.OpenAsync, folderStart: 2, folderHashWindow: 1);
        Assert.Equal(new[] { "1.png", "2.png", "3.png" }, gallery.Select(entry => entry.Asset.OriginalFileName));
        Assert.All(gallery, entry => Assert.True(entry.LocalTagEligible));
        Assert.Equal(3, fixture.Catalog.Read().Items.Count(item => item.State == FolderSourceItemState.Hashed));
        GalleryEntry resolved = (await FolderSourceMediaGallery.ResolveAsync(entries[2], MediaAssetReader.OpenAsync))!;
        Assert.Equal(Sha(Png.Append((byte)2).ToArray()), FolderSourceMediaGallery.WithDigest(entries[2], resolved).Sha256);
        Assert.NotNull(MediaTagSubjects.Of(resolved));
    }

    [Fact]
    public async Task TagRevalidationChecksFolderFilesThroughTheirVirtualManifest()
    {
        using Fixture fixture = new();
        string a = fixture.Write("a.png", Png);
        FolderSourceRoot root = fixture.AddRoot();
        fixture.Scan(root);
        await using FolderSourceHasher hasher = new(fixture.Catalog);
        using IDisposable registration = new FolderSourceManifests(fixture.Catalog, hasher).Register();
        FolderSourceItem item = Assert.Single(fixture.Catalog.Read().Items);
        await hasher.HashAsync(item.Id, FolderSourceHashPriority.UserAction);
        MediaLibraryEntry entry = Assert.Single(FolderSourceProjection.EntriesOf(fixture.Catalog.Read()));
        LocalMediaObservation observation = ((MediaTagSubject.Local)MediaTagSubjects.Of(entry)!).Observation;
        Func<CancellationToken, Task<MeowChan.Archive.ArchiveReader>> noArchive = _ => throw new InvalidOperationException("No archive read for folder files.");
        Assert.Equal(LocalMediaCheck.Verified, Assert.Single(await LocalMediaTagRevalidation.CheckAsync(new[] { observation }, noArchive, MediaAssetReader.OpenAsync, true, CancellationToken.None)));
        File.WriteAllBytes(a, Jpg);
        File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.Equal(LocalMediaCheck.Changed, Assert.Single(await LocalMediaTagRevalidation.CheckAsync(new[] { observation }, noArchive, MediaAssetReader.OpenAsync, true, CancellationToken.None)));
        fixture.Catalog.RemoveRoot(root.Id);
        Assert.Equal(LocalMediaCheck.Missing, Assert.Single(await LocalMediaTagRevalidation.CheckAsync(new[] { observation }, noArchive, MediaAssetReader.OpenAsync, false, CancellationToken.None)));
    }

    [Fact]
    public void IdentitiesAreStrictAndNeverNamePaths()
    {
        Guid root = Guid.NewGuid(), item = Guid.NewGuid();
        string virtualRoot = FolderSourceIdentity.VirtualRoot(Path.GetTempPath());
        Assert.True(FolderSourceIdentity.TryParseVirtualDirectory(virtualRoot, FolderSourceIdentity.VirtualDirectory(Path.GetTempPath(), root, item), out Guid r, out Guid i));
        Assert.Equal((root, item), (r, i));
        Assert.False(FolderSourceIdentity.TryParseVirtualDirectory(virtualRoot, Path.Combine(virtualRoot, "x", "y"), out _, out _));
        Assert.False(FolderSourceIdentity.TryParseVirtualDirectory(virtualRoot, Path.Combine(virtualRoot, FolderSourceIdentity.Format(root)), out _, out _));
        Assert.Equal(64, FolderSourceIdentity.AssetId(item).Length);
        Assert.True(LocalMediaObservation.CanonicalHex(FolderSourceIdentity.AssetId(item)));
        Assert.False(FolderSourceIdentity.IsFolderThread(new ResourceId(FolderSourceIdentity.Source, "a", "b")));
        Assert.False(FolderSourceIdentity.TryNormalizeRelative("a/../b.png", out _));
        Assert.False(FolderSourceIdentity.TryNormalizeRelative("a/\u0001.png", out _));
        Assert.True(FolderSourceIdentity.TryNormalizeRelative("\\a\\b.png", out string normalized));
        Assert.Equal("a/b.png", normalized);
        Assert.False(FolderSourceIdentity.TryNormalizeRelative(new string('a', FolderSourceCatalog.MaximumPathLength + 1), out _));
    }

    private static async Task<bool> AnsweredAsync(string directory)
    {
        try { return (await MediaAssetReader.OpenAsync(directory)).Status == MediaManifestStatus.Available; }
        catch (IOException) { return false; }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>PR2 acceptance budget: 100,000 synthetic (empty) files are listed and catalogued without hashing; Windows gets a two-minute filesystem allowance.</summary>
public sealed class FolderSourceScanBench
{
    internal static TimeSpan AcceptanceBudget(bool isWindows) =>
        isWindows ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(60);

    [Fact]
    [Trait("Category", "Bench")]
    public void OneHundredThousandFilesAreScannedAndCataloguedWithoutHashing()
    {
        using TempDirectory temp = new();
        string media = Path.Combine(temp.Path, "media");
        for (int folder = 0; folder < 100; folder++)
        {
            string directory = Path.Combine(media, "d" + folder.ToString("D3"));
            Directory.CreateDirectory(directory);
            for (int file = 0; file < 1_000; file++) File.Create(Path.Combine(directory, file.ToString("D4") + ".jpg")).Dispose();
        }
        using ProfileLease lease = ProfileLease.Acquire(Path.Combine(temp.Path, "profile"));
        FolderSourceCatalog catalog = new(Path.Combine(temp.Path, "profile"), lease) { MaxFiles = 200_000 };
        FolderSourceRoot root = catalog.AddRoot(media, "bench", true, DateTimeOffset.UtcNow);
        Stopwatch watch = Stopwatch.StartNew();
        FolderSourceScan scan = FolderSourceScanner.Scan(media, true, 200_000, Array.Empty<string>());
        TimeSpan listed = watch.Elapsed;
        FolderSourceScanResult result = catalog.ApplyScan(root.Id, scan, DateTimeOffset.UtcNow);
        TimeSpan applied = watch.Elapsed;
        FolderSourceCatalogSnapshot snapshot = catalog.Read();
        IReadOnlyList<MediaLibraryEntry> entries = FolderSourceProjection.EntriesOf(snapshot);
        TimeSpan projected = watch.Elapsed;
        FolderSourceScanResult again = catalog.ApplyScan(root.Id, FolderSourceScanner.Scan(media, true, 200_000, Array.Empty<string>()), DateTimeOffset.UtcNow);
        TimeSpan rescanned = watch.Elapsed;
        Assert.Equal(100_000, result.Added);
        Assert.Equal(100_000, entries.Count);
        Assert.Equal(100_000, again.Unchanged);
        Assert.All(snapshot.Items.Take(100), item => Assert.Null(item.Sha256));
        Assert.True(rescanned < AcceptanceBudget(OperatingSystem.IsWindows()),
            $"listed {listed}, applied {applied}, projected {projected}, rescanned {rescanned}");
        Console.WriteLine($"FolderSourceScanBench: listed {listed.TotalMilliseconds:F0} ms, applied {applied.TotalMilliseconds:F0} ms, projected {projected.TotalMilliseconds:F0} ms, rescanned {rescanned.TotalMilliseconds:F0} ms, db {new FileInfo(catalog.FilePath).Length / 1024} KiB");
    }
}

public sealed class FolderSourceScanBenchClassificationTests
{
    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 120)]
    public void AcceptanceBudgetAccountsForWindowsFilesystemCatalogCost(bool isWindows, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), FolderSourceScanBench.AcceptanceBudget(isWindows));
    }
}
