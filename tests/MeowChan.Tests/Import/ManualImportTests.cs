using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests.Import;

/// <summary>
/// Manual import, phase 1 (contract manual-import-v1): the catalog store, the planner (selection, kinds, limits,
/// hashing, dedupe), the executor (copy only, collision-safe names, manifest, failure, cancellation, interruption),
/// the media index projection and the loose-file tag path. Temp profiles only; sources are never changed.
/// </summary>
public sealed class ManualImportTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6, 7 };
    private static readonly byte[] Webm = { 0x1A, 0x45, 0xDF, 0xA3, 9, 9 };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public readonly TempDirectory Temp = new();
        public string Profile => System.IO.Path.Combine(Temp.Path, "profile");
        public string Downloads => System.IO.Path.Combine(Temp.Path, "downloads");
        public string Source => System.IO.Path.Combine(Temp.Path, "source");
        public readonly ProfileLease Lease;
        public readonly ManualImportCatalogStore Store;

        public Fixture()
        {
            Directory.CreateDirectory(Source);
            Lease = ProfileLease.Acquire(Profile);
            Store = new ManualImportCatalogStore(Profile, Lease);
        }

        public string Write(string relative, byte[] bytes)
        {
            string path = System.IO.Path.Combine(Source, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public Task<ManualImportPlan> PlanAsync(ManualImportRequest? request = null, ManualImportLimits? limits = null,
            ManualImportKnownMedia? known = null, IProgress<ManualImportCheckProgress>? progress = null, CancellationToken token = default) =>
            ManualImportPlanner.PlanAsync(request ?? new ManualImportRequest(new[] { Source }), limits ?? ManualImportLimits.Default, Downloads,
                _ => Task.FromResult(known ?? ManualImportKnownMedia.Empty), progress, token);

        public ManualImportExecutor Executor(Func<string, long?>? freeSpace = null) => new(Store, new MediaStore(), freeSpace: freeSpace ?? (_ => null));

        public void Dispose() { Lease.Dispose(); Temp.Dispose(); }
    }

    private static ManualImportBatch Batch(Guid? id = null, ManualImportBatchState state = ManualImportBatchState.Completed, params ManualImportItem[] items) =>
        new(id ?? Guid.NewGuid(), "Import 2026-11-13 10:00", DateTimeOffset.Parse("2026-11-13T10:00:00Z"),
            state == ManualImportBatchState.Completed ? DateTimeOffset.Parse("2026-11-13T10:01:00Z") : null, state,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dl", "meowchan-import", "x"), "Urlaub", false, false, new[] { "cat" }, false, items);

    private static ManualImportItem Item(ManualImportItemState state = ManualImportItemState.Committed) =>
        new(Guid.NewGuid(), state, Sha(Png), Png.Length, "IMG 1.png", SavedMediaKind.Image, ".png",
            state == ManualImportItemState.Committed ? new string('a', 64) : null, state == ManualImportItemState.Failed ? ManualImportFailure.Changed : null);

    // ---- Catalog ----

    [Fact]
    public void CatalogStartsEmptyWithoutWritingAndSurvivesRestartWithBackup()
    {
        using Fixture fixture = new();
        Assert.Empty(fixture.Store.Read().Batches);
        Assert.False(File.Exists(fixture.Store.FilePath));
        ManualImportBatch batch = Batch(items: Item());
        using (ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter())
        {
            ManualImportCatalogSnapshot written = fixture.Store.PutBatchAdmitted(batch, 0, admission);
            Assert.Equal(1, written.Revision);
            fixture.Store.PutBatchAdmitted(batch with { TagsApplied = true }, 1, admission);
            Assert.Throws<ManualImportException>(() => fixture.Store.PutBatchAdmitted(batch, 1, admission));
        }
        Assert.True(File.Exists(fixture.Store.FilePath + ".bak"));
        ManualImportCatalogSnapshot read = new ManualImportCatalogStore(fixture.Profile, fixture.Lease).Read();
        ManualImportBatch again = Assert.Single(read.Batches);
        Assert.Equal(2, read.Revision);
        Assert.True(again.TagsApplied);
        Assert.Equal(batch.Items.Single(), again.Items.Single());
        Assert.Equal(batch.CreatedAtUtc, again.CreatedAtUtc);
        // Nothing in the file names a source path; only the file name and the folder label are kept.
        Assert.DoesNotContain(fixture.Source, File.ReadAllText(fixture.Store.FilePath));
    }

    [Fact]
    public void ReadsAreServedFromTheCacheUntilTheFileChangesAndTheBackupIsParsedOnlyOnce()
    {
        using Fixture fixture = new();
        ManualImportBatch batch = Batch(items: Item());
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportCatalogSnapshot written = fixture.Store.PutBatchAdmitted(batch, 0, admission);
        // The write primed the cache: neither the viewer reads nor the next writes parse the primary again.
        for (int index = 0; index < 5; index++) Assert.Same(written, fixture.Store.Read());
        Assert.Equal(0, fixture.Store.ReadCounts.Full);
        long revision = written.Revision;
        for (int index = 0; index < 6; index++)
            revision = fixture.Store.PutBatchAdmitted(batch with { TagsApplied = index % 2 == 0 }, revision, admission).Revision;
        (long full, long cached, long backupParses) = fixture.Store.ReadCounts;
        Assert.Equal(0, full);
        Assert.True(cached >= 11);
        Assert.True(backupParses <= 1, "backup parsed " + backupParses + " times");
        Assert.Equal(7, fixture.Store.Read().Revision);

        // A different process or the user replaced the file: the next read parses it, whatever the cache holds.
        string text = File.ReadAllText(fixture.Store.FilePath);
        File.WriteAllText(fixture.Store.FilePath, text.Replace("\"Revision\": 7,", "\"Revision\": 7,\n  \"Extra\": 1,"));
        Assert.Equal(ManualImportError.CatalogDamaged, Assert.Throws<ManualImportException>(() => fixture.Store.Read()).Error);
        Assert.Equal(1, fixture.Store.ReadCounts.Full);

        // A fresh store (restart) reads the file once and then answers from memory.
        File.WriteAllText(fixture.Store.FilePath, text);
        ManualImportCatalogStore restarted = new(fixture.Profile, fixture.Lease);
        Assert.Equal(7, restarted.Read().Revision);
        Assert.Equal(7, restarted.Read().Revision);
        Assert.Equal((1L, 1L), (restarted.ReadCounts.Full, restarted.ReadCounts.Cached));
    }

    [Fact]
    public void ABackupChangedBehindTheStoresBackIsCheckedAgain()
    {
        using Fixture fixture = new();
        ManualImportBatch batch = Batch(items: Item());
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        fixture.Store.PutBatchAdmitted(batch, 0, admission);
        fixture.Store.PutBatchAdmitted(batch with { TagsApplied = true }, 1, admission);
        fixture.Store.PutBatchAdmitted(batch, 2, admission);
        string newer = "{\"Version\": 2, \"Anything\": true}";
        File.WriteAllText(fixture.Store.FilePath + ".bak", newer);
        Assert.Equal(ManualImportError.CatalogNewer, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(batch with { TagsApplied = true }, 3, admission)).Error);
        Assert.Equal(newer, File.ReadAllText(fixture.Store.FilePath + ".bak"));
        File.WriteAllText(fixture.Store.FilePath + ".bak", "damaged");
        fixture.Store.PutBatchAdmitted(batch with { TagsApplied = true }, 3, admission);
        Assert.Equal(4, fixture.Store.Read().Revision);
    }

    [Theory]
    [InlineData("\"Version\": 1,", "\"Version\": 1, \"Extra\": 1,")]
    [InlineData("\"Revision\": 1,", "\"Revision\": 1, \"Revision\": 1,")]
    [InlineData("\"State\": \"Committed\"", "\"State\": \"committed\"")]
    [InlineData("\"Kind\": \"Image\"", "\"Kind\": \"Video\"")]
    public void StrictParserRejectsUnknownDuplicateOrInconsistentFieldsAndNeverOverwritesTheDamagedFile(string find, string replace)
    {
        using Fixture fixture = new();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        fixture.Store.PutBatchAdmitted(Batch(items: Item()), 0, admission);
        string text = File.ReadAllText(fixture.Store.FilePath);
        Assert.Contains(find, text);
        string damaged = text.Replace(find, replace);
        File.WriteAllText(fixture.Store.FilePath, damaged);
        Assert.Equal(ManualImportError.CatalogDamaged, Assert.Throws<ManualImportException>(() => fixture.Store.Read()).Error);
        Assert.Equal(ManualImportError.CatalogDamaged, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(Batch(items: Item()), 1, admission)).Error);
        Assert.Equal(damaged, File.ReadAllText(fixture.Store.FilePath));
    }

    [Fact]
    public void UppercaseHexIsDamagedAndANewerCatalogOrBackupIsNeverReplaced()
    {
        using Fixture fixture = new();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportItem item = Item();
        fixture.Store.PutBatchAdmitted(Batch(items: item), 0, admission);
        string text = File.ReadAllText(fixture.Store.FilePath);
        File.WriteAllText(fixture.Store.FilePath, text.Replace(item.Sha256, item.Sha256.ToUpperInvariant()));
        Assert.Equal(ManualImportError.CatalogDamaged, Assert.Throws<ManualImportException>(() => fixture.Store.Read()).Error);

        string newer = "{\"Version\": 2, \"Anything\": true}";
        File.WriteAllText(fixture.Store.FilePath, newer);
        Assert.Equal(ManualImportError.CatalogNewer, Assert.Throws<ManualImportException>(() => fixture.Store.Read()).Error);
        Assert.Throws<ManualImportException>(() => fixture.Store.PutBatchAdmitted(Batch(items: Item()), 0, admission));
        Assert.Equal(newer, File.ReadAllText(fixture.Store.FilePath));

        // A newer backup blocks writes too, so rotating the current file into .bak never destroys it.
        File.WriteAllText(fixture.Store.FilePath, text);
        File.WriteAllText(fixture.Store.FilePath + ".bak", newer);
        Assert.Equal(ManualImportError.CatalogNewer, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(Batch(items: Item()), 1, admission)).Error);
        Assert.Equal(newer, File.ReadAllText(fixture.Store.FilePath + ".bak"));
    }

    [Fact]
    public void ConfiguredTotalAndSizeLimitsStopGrowthButNotStateChanges()
    {
        using Fixture fixture = new();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportBatch first = Batch(items: new[] { Item(), Item() });
        fixture.Store.PutBatchAdmitted(first, 0, admission);
        fixture.Store.MaximumTotalItems = 2;
        Assert.Equal(ManualImportError.TooManyImportedFiles, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(Batch(items: Item()), 1, admission)).Error);
        // Changing existing items is no growth.
        fixture.Store.PutBatchAdmitted(first with { TagsApplied = true }, 1, admission);
        fixture.Store.DocumentLimitBytes = ManualImportCatalogStore.MinimumDocumentLimitBytes;
        ManualImportItem[] many = Enumerable.Range(0, 4000).Select(_ => Item()).ToArray();
        fixture.Store.MaximumTotalItems = 10_000;
        Assert.Equal(ManualImportError.CatalogTooLarge, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(Batch(items: many), 2, admission)).Error);
        Assert.Equal(2, fixture.Store.Read().Revision);
    }

    [Fact]
    public void TheBatchCountAndTagLimitsAreSettingsThatStopOnlyGrowthAndNeverMakeACatalogUnreadable()
    {
        Assert.Equal((100_000, 1_000, 1_000_000, 32, 256), (ManualImportCatalogStore.DefaultMaximumBatches, ManualImportCatalogStore.MinimumMaximumBatches,
            ManualImportCatalogStore.MaximumBatchesCeiling, ManualImportCatalogStore.DefaultMaximumTags, ManualImportCatalogStore.MaximumTagsCeiling));
        using Fixture fixture = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.MaximumBatchCount = ManualImportCatalogStore.MinimumMaximumBatches - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.MaximumBatchCount = ManualImportCatalogStore.MaximumBatchesCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.MaximumTagsPerBatch = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.MaximumTagsPerBatch = ManualImportCatalogStore.MaximumTagsCeiling + 1);
        // Reading accepts a batch with more tags than the default write limit (a lowered setting never hides saved data).
        string[] tags = Enumerable.Range(0, 100).Select(index => "tag" + index).ToArray();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportBatch first = Batch(items: Item()) with { Tags = tags };
        fixture.Store.PutBatchAdmitted(first, 0, admission);
        Assert.Equal(100, Assert.Single(fixture.Store.Read().Batches).Tags.Count);
        // The batch count is checked against the configured limit (the minimum is 1,000, so fill up to it with small batches).
        fixture.Store.MaximumTotalItems = 5_000;
        fixture.Store.MaximumBatchCount = ManualImportCatalogStore.MinimumMaximumBatches;
        long revision = 1;
        for (int index = 1; index < ManualImportCatalogStore.MinimumMaximumBatches; index++)
            fixture.Store.PutBatchAdmitted(Batch(items: Item()), revision++, admission);
        Assert.Equal(ManualImportError.TooManyBatches, Assert.Throws<ManualImportException>(
            () => fixture.Store.PutBatchAdmitted(Batch(items: Item()), revision, admission)).Error);
        // Changing an existing batch is no growth, and raising the setting lifts the refusal.
        fixture.Store.PutBatchAdmitted(first with { TagsApplied = true }, revision++, admission);
        fixture.Store.MaximumBatchCount = 2_000;
        fixture.Store.PutBatchAdmitted(Batch(items: Item()), revision, admission);
        Assert.Equal(ManualImportCatalogStore.MinimumMaximumBatches + 1, fixture.Store.Read().Batches.Count);
    }

    // ---- Planner ----

    [Fact]
    public async Task PlannerSortsKindsSkipsHiddenAndLinksHashesOnceAndFindsDuplicates()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("sub/b.JPG", Jpg);
        fixture.Write("sub/again.png", Png);
        fixture.Write("clip.webm", Webm);
        fixture.Write("notes.txt", Encoding.UTF8.GetBytes("hello"));
        fixture.Write("paper.pdf", Encoding.UTF8.GetBytes("%PDF-1.4"));
        fixture.Write(".hidden.png", Png);
        fixture.Write(".secret/c.png", Jpg);
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(fixture.Source, "link.png"), Path.Combine(fixture.Source, "a.png"));
        List<ManualImportCheckProgress> reports = new();

        ManualImportPlan plan = await fixture.PlanAsync(known: new ManualImportKnownMedia(new Dictionary<string, int> { [Sha(Webm)] = 3 }, true),
            progress: new SynchronousProgress<ManualImportCheckProgress>(reports.Add));

        Assert.Equal(ManualImportPlanStatus.Ready, plan.Status);
        Assert.True(plan.DuplicateCheckComplete);
        Assert.Equal("source", plan.SourceLabel);
        Dictionary<string, ManualImportCandidate> byName = plan.Items.ToDictionary(item => item.Name);
        Assert.Equal(ManualImportDisposition.New, byName["a.png"].Disposition);
        Assert.Equal(Sha(Png), byName["a.png"].Sha256);
        Assert.Equal(ManualImportDisposition.New, byName["b.JPG"].Disposition);
        Assert.Equal(".jpg", byName["b.JPG"].Extension);
        Assert.Equal(ManualImportDisposition.DuplicateInSelection, byName["again.png"].Disposition);
        Assert.Equal((ManualImportDisposition.AlreadyInLibrary, 3), (byName["clip.webm"].Disposition, byName["clip.webm"].ExistingOccurrences));
        Assert.Equal(ManualImportDisposition.Unsupported, byName["notes.txt"].Disposition);
        Assert.Equal(ManualImportDisposition.OtherFilesOff, byName["paper.pdf"].Disposition);
        Assert.DoesNotContain(".hidden.png", byName.Keys);
        Assert.DoesNotContain("c.png", byName.Keys);
        Assert.DoesNotContain("link.png", byName.Keys);
        Assert.Equal(4, plan.ImportableFiles);
        Assert.Equal(reports.Last().FilesTotal, reports.Last().FilesDone);

        ManualImportPlan flat = await fixture.PlanAsync(new ManualImportRequest(new[] { fixture.Source }, IncludeSubfolders: false, IncludeOtherFiles: true));
        Assert.DoesNotContain(flat.Items, item => item.Name is "b.JPG" or "again.png");
        Assert.Equal(ManualImportDisposition.New, flat.Items.Single(item => item.Name == "paper.pdf").Disposition);
    }

    [Fact]
    public async Task LimitsAreReportedBeforeHashingAndOneFileOverTheLimitIsLeftOut()
    {
        using Fixture fixture = new();
        for (int index = 0; index < 5; index++) fixture.Write($"f{index}.png", Png.Append((byte)index).ToArray());
        bool askedLibrary = false;
        ManualImportPlan tooMany = await ManualImportPlanner.PlanAsync(new ManualImportRequest(new[] { fixture.Source }),
            new ManualImportLimits(3, long.MaxValue, long.MaxValue), fixture.Downloads,
            _ => { askedLibrary = true; return Task.FromResult(ManualImportKnownMedia.Empty); }, null, default);
        Assert.Equal(ManualImportPlanStatus.TooManyFiles, tooMany.Status);
        Assert.All(tooMany.Items, item => Assert.Null(item.Sha256));
        Assert.False(askedLibrary);

        ManualImportPlan tooLarge = await fixture.PlanAsync(limits: new ManualImportLimits(100, Png.Length * 2, long.MaxValue));
        Assert.Equal(ManualImportPlanStatus.BatchTooLarge, tooLarge.Status);

        ManualImportPlan oneTooLarge = await fixture.PlanAsync(new ManualImportRequest(new[] { fixture.Write("big.png", new byte[64]), fixture.Write("ok.png", Png) }),
            new ManualImportLimits(100, long.MaxValue, 32));
        Assert.Equal(ManualImportDisposition.TooLarge, oneTooLarge.Items.Single(item => item.Name == "big.png").Disposition);
        Assert.Equal(ManualImportDisposition.New, oneTooLarge.Items.Single(item => item.Name == "ok.png").Disposition);
    }

    [Fact]
    public async Task TheDownloadFolderIsNeverImportedAndAMissingRootIsUnreadable()
    {
        using Fixture fixture = new();
        string inside = Path.Combine(fixture.Downloads, "4chan", "g", "1", "x.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, Png);
        fixture.Write("y.png", Jpg);
        // A selected parent of the download folder is walked, but the download folder itself is pruned.
        ManualImportPlan plan = await fixture.PlanAsync(new ManualImportRequest(new[] { fixture.Temp.Path, inside, Path.Combine(fixture.Source, "gone.png") }));
        Assert.DoesNotContain(plan.Items, item => item.SourcePath.StartsWith(fixture.Downloads, StringComparison.Ordinal) && item.Disposition != ManualImportDisposition.Skipped);
        Assert.Equal(ManualImportDisposition.Skipped, plan.Items.Single(item => item.SourcePath == inside).Disposition);
        Assert.Equal(ManualImportDisposition.New, plan.Items.Single(item => item.Name == "y.png").Disposition);
        Assert.Equal(ManualImportDisposition.Unreadable, plan.Items.Single(item => item.Name == "gone.png").Disposition);
    }

    [Fact]
    public async Task ACancelledCheckThrowsAndAnIncompleteIndexIsReported()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PlanAsync(token: cancel.Token));
        ManualImportPlan plan = await fixture.PlanAsync(known: new ManualImportKnownMedia(new Dictionary<string, int>(), complete: false));
        Assert.False(plan.DuplicateCheckComplete);
        Assert.Equal(ManualImportDisposition.New, Assert.Single(plan.Items).Disposition);
    }

    // ---- Executor ----

    [Fact]
    public async Task ImportCopiesOnlyNewFilesUnderItemNamesAndLeavesSourcesUntouched()
    {
        using Fixture fixture = new();
        string a = fixture.Write("a.png", Png), b = fixture.Write("b.jpg", Jpg), twin = fixture.Write("twin.png", Png);
        DateTime stamp = File.GetLastWriteTimeUtc(a);
        ManualImportPlan plan = await fixture.PlanAsync();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();

        ManualImportResult result = await fixture.Executor().ExecuteAsync(plan, new ManualImportOptions("Holiday", new[] { "cat", "Cat", "dog" }),
            fixture.Downloads, admission, null, default);

        ManualImportBatch batch = result.Batch;
        Assert.Equal((2, 1, 0), (result.Copied, result.Linked, result.Failed));
        Assert.Equal(ManualImportBatchState.Completed, batch.State);
        Assert.Equal(new[] { "cat", "dog" }, batch.Tags);
        Assert.False(batch.TagsApplied);
        Assert.Equal(ManualImportIdentity.BatchDirectory(fixture.Downloads, batch.Id), batch.Directory);
        Assert.StartsWith(Path.Combine(fixture.Downloads, "meowchan-import") + Path.DirectorySeparatorChar, batch.Directory);
        // Sources are only read.
        Assert.Equal(Png, File.ReadAllBytes(a)); Assert.Equal(Jpg, File.ReadAllBytes(b)); Assert.Equal(Png, File.ReadAllBytes(twin));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(a));

        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(batch.Directory);
        Assert.Equal(MediaManifestStatus.Available, manifest.Status);
        foreach (ManualImportItem item in batch.Items.Where(item => item.State == ManualImportItemState.Committed))
        {
            MediaAssetReference asset = manifest.FindById(item.AssetId!)!;
            Assert.Equal(item.RequestedFileName, asset.RequestedFileName);
            Assert.Equal(item.Id.ToString("N") + item.Extension, Path.GetFileName(asset.Path));
            Assert.Equal(MediaStore.LocalImportSourceUrl, asset.SourceUrl);
            Assert.Equal(item.Name, asset.OriginalFileName);
            Assert.Equal(item.Sha256, asset.Sha256);
        }
        Assert.DoesNotContain(fixture.Source, File.ReadAllText(Path.Combine(batch.Directory, ".meowchan-manifest.json")));
        Assert.Equal(2, Directory.GetFiles(batch.Directory).Count(path => !Path.GetFileName(path).StartsWith('.')));
        Assert.Equal(ManualImportItemState.Duplicate, batch.Items.Single(item => item.Name == "twin.png").State);
    }

    [Fact]
    public async Task AChangedSourceFailsAloneAndAForeignFileAtTheDestinationIsNeverOverwritten()
    {
        using Fixture fixture = new();
        string a = fixture.Write("a.png", Png);
        fixture.Write("b.jpg", Jpg);
        ManualImportPlan plan = await fixture.PlanAsync();
        File.WriteAllBytes(a, Png.Concat(new byte[] { 42 }).ToArray()); // Changed after the check.
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportExecutor executor = fixture.Executor();
        ManualImportResult result = await executor.ExecuteAsync(plan, new ManualImportOptions("X", Array.Empty<string>()), fixture.Downloads, admission, null, default);
        Assert.Equal(ManualImportFailure.Changed, result.Batch.Items.Single(item => item.Name == "a.png").Failure);
        Assert.Equal(ManualImportItemState.Committed, result.Batch.Items.Single(item => item.Name == "b.jpg").State);
        Assert.True(result.Batch.TagsApplied); // No tags requested.
        Assert.Empty(Directory.GetFiles(result.Batch.Directory, "*.part"));

        // A foreign file already at an item's destination stays; the copy gets a conflict name.
        MediaStore store = new();
        string directory = Path.Combine(fixture.Temp.Path, "conflict");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "item.png"), Jpg);
        await using MemoryStream source = new(Png);
        MediaCommitResult committed = await store.CommitLocalFileAsync(directory, "item.png", "item", "x.png", source, Sha(Png), Png.Length, default);
        Assert.Equal(Jpg, File.ReadAllBytes(Path.Combine(directory, "item.png")));
        Assert.NotEqual(Path.Combine(directory, "item.png"), committed.Path);
        Assert.Equal(Png, File.ReadAllBytes(committed.Path));
        // A retry of the same item with the same bytes reuses the published file.
        await using MemoryStream again = new(Png);
        MediaCommitResult retried = await store.CommitLocalFileAsync(directory, "item.png", "item", "x.png", again, Sha(Png), Png.Length, default);
        Assert.True(retried.Reused);
        Assert.Equal(committed.AssetId, retried.AssetId);
    }

    [Fact]
    public async Task CancellationStopsAfterTheCurrentFileAndKeepsWhatIsFinished()
    {
        using Fixture fixture = new();
        for (int index = 0; index < 4; index++) fixture.Write($"f{index}.png", Png.Append((byte)index).ToArray());
        ManualImportPlan plan = await fixture.PlanAsync();
        using CancellationTokenSource cancel = new();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportResult result = await fixture.Executor().ExecuteAsync(plan, new ManualImportOptions("X", new[] { "t" }), fixture.Downloads, admission,
            new SynchronousProgress<ManualImportCopyProgress>(progress => { if (progress.FilesDone == 1) cancel.Cancel(); }), cancel.Token);
        Assert.True(result.Batch.Cancelled);
        Assert.Equal(ManualImportBatchState.Completed, result.Batch.State);
        Assert.Equal(ManualImportItemState.Committed, Assert.Single(result.Batch.Items).State);
        Assert.False(result.Batch.TagsApplied);
        Assert.Single(Directory.GetFiles(result.Batch.Directory).Where(path => !Path.GetFileName(path).StartsWith('.')));
    }

    [Fact]
    public async Task AnInterruptedBatchIsCompletedFromItsManifestBeforeTheNextImport()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("b.jpg", Jpg);
        ManualImportPlan plan = await fixture.PlanAsync();
        ManualImportExecutor crashing = fixture.Executor();
        // The stop lands after both files were published but before the catalog recorded them.
        crashing.BeforeCatalogUpdateForTests = _ => throw new IOException("simulated stop");
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        await Assert.ThrowsAsync<IOException>(() => crashing.ExecuteAsync(plan, new ManualImportOptions("X", Array.Empty<string>()),
            fixture.Downloads, admission, null, default));
        ManualImportCatalogSnapshot catalog = fixture.Store.Read();
        ManualImportBatch interrupted = Assert.Single(catalog.Batches);
        Assert.Equal(ManualImportBatchState.Copying, interrupted.State);
        Assert.All(interrupted.Items, item => Assert.Equal(ManualImportItemState.Planned, item.State));
        // A third planned item never reached the folder.
        ManualImportItem unpublished = new(Guid.NewGuid(), ManualImportItemState.Planned, Sha(Webm), Webm.Length, "c.webm", SavedMediaKind.Video, ".webm");
        fixture.Store.PutBatchAdmitted(interrupted with { Items = interrupted.Items.Append(unpublished).ToArray() }, catalog.Revision, admission);

        Assert.Equal(1, await fixture.Executor().FinishInterruptedAsync(admission, default));
        ManualImportBatch finished = Assert.Single(fixture.Store.Read().Batches);
        Assert.Equal(ManualImportBatchState.Completed, finished.State);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(finished.Directory);
        Assert.Equal(2, finished.Count(ManualImportItemState.Committed));
        Assert.All(finished.Items.Where(item => item.State == ManualImportItemState.Committed), item => Assert.NotNull(manifest.FindById(item.AssetId!)));
        Assert.Equal(ManualImportFailure.Interrupted, finished.Find(unpublished.Id)!.Failure);
        Assert.Equal(0, await fixture.Executor().FinishInterruptedAsync(admission, default));
    }

    [Fact]
    public async Task NotEnoughSpaceOrTotalLimitRefusesBeforeAnythingIsWritten()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        ManualImportPlan plan = await fixture.PlanAsync();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportException space = await Assert.ThrowsAsync<ManualImportException>(() => fixture.Executor(_ => Png.Length + 10L)
            .ExecuteAsync(plan, new ManualImportOptions("X", Array.Empty<string>(), ReserveBytes: 100), fixture.Downloads, admission, null, default));
        Assert.Equal(ManualImportError.NotEnoughSpace, space.Error);
        fixture.Store.MaximumTotalItems = 1;
        await fixture.Executor().ExecuteAsync(plan, new ManualImportOptions("X", Array.Empty<string>()), fixture.Downloads, admission, null, default);
        ManualImportException total = await Assert.ThrowsAsync<ManualImportException>(() => fixture.Executor()
            .ExecuteAsync(plan, new ManualImportOptions("Y", Array.Empty<string>()), fixture.Downloads, admission, null, default));
        Assert.Equal(ManualImportError.TooManyImportedFiles, total.Error);
        Assert.Single(fixture.Store.Read().Batches);
        Assert.Single(Directory.GetDirectories(ManualImportIdentity.ImportRoot(fixture.Downloads)));
        await Assert.ThrowsAsync<ManualImportException>(() => fixture.Executor()
            .ExecuteAsync(plan, new ManualImportOptions("  ", Array.Empty<string>()), fixture.Downloads, admission, null, default));
    }

    // ---- Media library and tags ----

    [Fact]
    public async Task ImportedFilesAreLooseTaggableMediaInTheIndexGalleryCacheAndTagStore()
    {
        using Fixture fixture = new();
        fixture.Write("a.png", Png);
        fixture.Write("b.jpg", Jpg);
        ManualImportPlan plan = await fixture.PlanAsync();
        using ProfileAdmissionToken admission = fixture.Lease.Admission.EnterStoreWriter();
        ManualImportBatch batch = (await fixture.Executor().ExecuteAsync(plan, new ManualImportOptions("Holiday", new[] { "cat" }),
            fixture.Downloads, admission, null, default)).Batch;

        // No archive catalog at all: the index still shows the imports.
        MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(token => ArchiveReader.OpenAsync(fixture.Profile, token),
            MediaAssetReader.OpenAsync, readImportCatalog: () => fixture.Store.Read());
        Assert.True(index.Complete);
        Assert.Equal(2, index.Entries.Count);
        Assert.All(index.Entries, entry =>
        {
            Assert.Equal(MediaLibraryPresence.Present, entry.Presence);
            Assert.True(ManualImportIdentity.IsImportThread(entry.Thread));
            Assert.Equal("Holiday", entry.ThreadTitle);
            Assert.NotNull(entry.LocalSlot);
            Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(entry));
        });
        IReadOnlyList<GalleryEntry> gallery = await MediaLibraryGallery.ResolveAsync(index.Entries, _ => throw new InvalidOperationException("no archive"),
            MediaAssetReader.OpenAsync, default, null, () => fixture.Store.Read());
        Assert.Equal(2, gallery.Count);
        Assert.All(gallery, entry => Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(entry)));

        string cache = Path.Combine(fixture.Temp.Path, "media-index.json");
        MediaLibraryIndexCacheSync.Save(cache, index);
        MediaLibrarySnapshot cached = MediaLibraryIndexCacheSync.Load(cache)!;
        Assert.Equal(index.Entries.Select(entry => entry.ImportItemId), cached.Entries.Select(entry => entry.ImportItemId));
        // Imported files are a version 4 feature: a version 3 document never carries them and is no cache.
        string document = File.ReadAllText(cache);
        Assert.Contains("\"Im\":", document);
        File.WriteAllText(cache, document.Replace("\"Version\":4", "\"Version\":3"));
        Assert.Null(MediaLibraryIndexCacheSync.Load(cache));
        File.WriteAllText(cache, document);

        // Revalidation reads the import catalog and the batch manifest, never the archive.
        IReadOnlyList<MediaTagSubject> subjects = ManualImportExecutor.TagSubjects(batch);
        LocalMediaObservation[] observations = subjects.Cast<MediaTagSubject.Local>().Select(subject => subject.Observation).ToArray();
        Func<CancellationToken, Task<ArchiveReader>> noArchive = _ => throw new InvalidOperationException("archive opened");
        Assert.All(await LocalMediaTagRevalidation.CheckAsync(observations, noArchive, MediaAssetReader.OpenAsync, true, default, () => fixture.Store.Read()),
            check => Assert.Equal(LocalMediaCheck.Verified, check));
        Assert.All(await LocalMediaTagRevalidation.CheckAsync(observations, noArchive, MediaAssetReader.OpenAsync, true, default, null),
            check => Assert.Equal(LocalMediaCheck.Unreadable, check));

        PrivateTagStore tags = new(fixture.Profile, fixture.Lease);
        PrivateTagCommit commit = tags.DefineAndSetMediaSubjects("cat", subjects);
        Assert.Equal(2, commit.Snapshot.LocalAssignments.Count);
        Assert.All(observations, observation => Assert.True(commit.Snapshot.ResolveLocal(observation).IsRegistered));
        Assert.Equal(0, (await LocalMediaTagRevalidation.InspectAsync(commit.Snapshot, noArchive, MediaAssetReader.OpenAsync, default,
            () => fixture.Store.Read())).OrphanedFiles);

        // A changed copy is refused for a new tag, and a vanished batch is missing.
        ManualImportItem first = batch.Items.First(item => item.State == ManualImportItemState.Committed);
        File.WriteAllBytes(Path.Combine(batch.Directory, first.RequestedFileName), Webm);
        IReadOnlyList<LocalMediaCheck> changed = await LocalMediaTagRevalidation.CheckAsync(observations, noArchive, MediaAssetReader.OpenAsync, true, default,
            () => fixture.Store.Read());
        Assert.Contains(LocalMediaCheck.Changed, changed);
        Assert.Contains(LocalMediaCheck.Verified, changed);
        Assert.All(await LocalMediaTagRevalidation.CheckAsync(observations, noArchive, MediaAssetReader.OpenAsync, true, default,
            () => ManualImportCatalogSnapshot.Empty), check => Assert.Equal(LocalMediaCheck.Missing, check));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
