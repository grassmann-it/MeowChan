using System.Security.Cryptography;
using MeowChan.Downloader;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Tests;

// OR-05 export of the Medien page against real committed originals: the plan of the page plus the export dialog's exporter.
public sealed class MediaLibraryExportTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Saved(MediaLibraryEntry Entry, string Path, byte[] Bytes, string Sha);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // A real committed original with its manifest, and the index entry the media page would hold for it.
    private static async Task<Saved> Commit(string root, StubImageBoard board, byte[] bytes, string mediaId, string folder, ResourceId thread, string post)
    {
        DownloadItem item = new(new Uri($"https://stub.test/media/{mediaId}.bin"),
            Path.Combine(root, "media", "stub", "g", folder, mediaId + ".bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), mediaId,
            bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), "photo-" + mediaId + ".bin");
        using MemoryStream stream = new(bytes, writable: false);
        MediaCommitResult committed = await new MediaStore().CommitAsync(item, stream, CancellationToken.None);
        string directory = Path.GetDirectoryName(committed.Path)!;
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.FindById(committed.AssetId!)!;
        MediaLibraryEntry entry = new(thread, "T", 1, Day, Day, post, long.Parse(post), null, 0, "att-v1:" + mediaId,
            directory, asset.AssetId, asset.Sha256.ToLowerInvariant(), asset.Length, asset.MediaIdentifier, asset.Variant,
            asset.OriginalFileName, SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        return new(entry, committed.Path, bytes, Sha(bytes));
    }

    private static IReadOnlyList<MediaLibraryTile> TilesOf(params MediaLibraryEntry[] ordered) =>
        MediaLibraryGrouping.Build(ordered, ordered, MediaLibrarySort.ByThread, includeMissing: true);

    // The page's export: plan from the frozen tiles (one original per tile), then the existing exporter of the export dialog.
    private static async Task<(MediaLibraryExportPlan Plan, FavoriteExportResult Result)> ExportAsync(IEnumerable<MediaLibraryTile> frozen,
        string destination, CancellationToken token = default)
    {
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(frozen, MediaAssetReader.OpenAsync, token);
        return (plan, await FavoriteOriginalExporter.ExportAsync(plan.Assets, destination, token));
    }

    private static string[] Files(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Sha(File.ReadAllBytes(path))).Order().ToArray();

    [Fact]
    public async Task SelectedMediaAreExportedByteIdenticalWithMatchingHashesAndTheArchiveStaysUntouched()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved[] saved =
        [
            await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1"),
            await Commit(directory.Path, board, [4, 5, 6, 7], "b", "200", new("stub", "g", "200"), "2"),
            await Commit(directory.Path, board, [8, 9, 10, 11, 12], "c", "300", new("stub", "g", "300"), "3")
        ];
        Saved unselected = await Commit(directory.Path, board, [13, 14], "d", "400", new("stub", "g", "400"), "4");
        string[] before = Snapshot(Path.Combine(directory.Path, "media"));
        string destination = Path.Combine(directory.Path, "out");

        (MediaLibraryExportPlan plan, FavoriteExportResult result) = await ExportAsync(TilesOf(saved.Select(value => value.Entry).ToArray()), destination);

        Assert.Equal(0, plan.Unavailable);
        Assert.Equal((3, 0, 0, 0, 0), (result.Copied, result.AlreadyExists, result.Missing, result.Failed, result.Cancelled));
        string[] copied = Files(destination);
        Assert.Equal(3, copied.Length);
        foreach (Saved value in saved)
        {
            // Each original is there byte for byte, and the recorded hash is the hash of the copied bytes.
            string file = Assert.Single(copied, path => File.ReadAllBytes(path).SequenceEqual(value.Bytes));
            Assert.Equal(value.Sha, Sha(File.ReadAllBytes(file)));
            Assert.Equal(value.Entry.Sha256, Sha(File.ReadAllBytes(file)));
        }
        Assert.DoesNotContain(copied, path => File.ReadAllBytes(path).SequenceEqual(unselected.Bytes));
        Assert.Equal(before, Snapshot(Path.Combine(directory.Path, "media"))); // read-only on the archive
        Assert.Empty(Files(destination).Where(path => Path.GetFileName(path).StartsWith(".favorite-pending-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IdenticalBytesInSeveralThreadsAreOneTileAndOneExportedFile()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved first = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        Saved twin = await Commit(directory.Path, board, [1, 2, 3], "a", "200", new("stub", "g", "200"), "2");
        IReadOnlyList<MediaLibraryTile> tiles = TilesOf(first.Entry, twin.Entry);
        Assert.Single(tiles);

        (_, FavoriteExportResult result) = await ExportAsync(tiles, Path.Combine(directory.Path, "out"));

        Assert.Equal(1, result.Copied);
        Assert.Single(Files(Path.Combine(directory.Path, "out")));
    }

    [Fact]
    public async Task SelectionFrozenAtStartIsExportedEvenIfThePageFiltersAfterwards()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved a = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        Saved b = await Commit(directory.Path, board, [4, 5, 6], "b", "200", new("stub", "g", "200"), "2");
        IReadOnlyList<MediaLibraryTile> shown = TilesOf(a.Entry, b.Entry);
        IReadOnlyList<MediaLibraryTile> frozen = MediaLibrarySelection.Pick(shown, [0, 1]);
        // The page now filters b away: a selection restored from the same keys would reach only a, the started export must not.
        IReadOnlyList<MediaLibraryTile> filtered = MediaLibraryGrouping.Build([a.Entry, b.Entry], [a.Entry], MediaLibrarySort.ByThread, true);
        Assert.Single(MediaLibrarySelection.Restore(frozen.Select(MediaLibraryTileKey.Of).ToArray(), filtered));

        (MediaLibraryExportPlan plan, FavoriteExportResult result) = await ExportAsync(frozen, Path.Combine(directory.Path, "out"));

        Assert.Equal(2, plan.Assets.Count);
        Assert.Equal(2, result.Copied);
        Assert.Contains(Files(Path.Combine(directory.Path, "out")), path => File.ReadAllBytes(path).SequenceEqual(b.Bytes));
    }

    [Fact]
    public async Task MissingChangedAndNonOriginalMediaAreGapsAndNeverSuccessOrSubstitutes()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved kept = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        Saved deleted = await Commit(directory.Path, board, [4, 5, 6], "b", "200", new("stub", "g", "200"), "2");
        Saved overwritten = await Commit(directory.Path, board, [7, 8, 9, 10], "c", "300", new("stub", "g", "300"), "3");
        Saved folderGone = await Commit(directory.Path, board, [11, 12], "d", "400", new("stub", "g", "400"), "4");
        Saved preview = await Commit(directory.Path, board, [13, 14, 15], "e", "500", new("stub", "g", "500"), "5");
        MediaLibraryEntry notSaved = kept.Entry with { Thread = new("stub", "g", "600"), PostId = "6", Presence = MediaLibraryPresence.Missing,
            Directory = null, AssetId = null, Sha256 = null, Length = null, MediaId = "x" };
        MediaLibraryEntry previewEntry = preview.Entry with { Variant = "thumbnail" };
        MediaLibraryEntry staleIndex = kept.Entry with { Thread = new("stub", "g", "700"), Sha256 = new string('f', 64) };
        File.Delete(deleted.Path);
        await File.WriteAllBytesAsync(overwritten.Path, [9, 9, 9, 9]); // same length, other bytes
        Directory.Delete(Path.GetDirectoryName(folderGone.Path)!, recursive: true);
        string destination = Path.Combine(directory.Path, "out");

        (MediaLibraryExportPlan plan, FavoriteExportResult result) = await ExportAsync(
            TilesOf(kept.Entry, deleted.Entry, overwritten.Entry, folderGone.Entry, previewEntry, notSaved, staleIndex), destination);

        // Unsaved, preview-only, stale index and vanished folder never reach the exporter; the others fail or miss there.
        Assert.Equal(4, plan.Unavailable);
        Assert.Equal(3, plan.Assets.Count);
        Assert.Equal((1, 1, 1), (result.Copied, result.Missing, result.Failed));
        Assert.Equal(FavoriteExportStatus.Missing, result.Items.Single(item => item.Key.Sha256 == deleted.Sha.ToUpperInvariant() || item.Key.Sha256 == deleted.Sha).Status);
        Assert.Equal(FavoriteExportStatus.Failed, result.Items.Single(item => string.Equals(item.Key.Sha256, overwritten.Sha, StringComparison.OrdinalIgnoreCase)).Status);
        // Only the intact original was written; no preview, poster or other bytes took the place of a gap.
        string only = Assert.Single(Files(destination));
        Assert.Equal(kept.Bytes, File.ReadAllBytes(only));
    }

    [Fact]
    public async Task EntriesWithBrokenOrForeignOriginalFactsAreGapsOnRealManifestsAndMeasureAgrees()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved kept = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        Saved other = await Commit(directory.Path, board, [4, 5, 6, 7], "b", "200", new("stub", "g", "200"), "2");
        // No directory / asset id / media id and a blank asset id: nothing to read, not a null key for the manifest reader.
        MediaLibraryEntry noDirectory = Fresh(kept.Entry, "301", "3") with { Directory = null };
        MediaLibraryEntry noAsset = Fresh(kept.Entry, "302", "4") with { AssetId = null };
        MediaLibraryEntry blankAsset = Fresh(kept.Entry, "303", "5") with { AssetId = "  " };
        MediaLibraryEntry noMedia = Fresh(kept.Entry, "304", "6") with { MediaId = null };
        // Valid-looking facts that point into another original's folder: the folder holds a different asset, never a substitute.
        MediaLibraryEntry foreignFolder = Fresh(kept.Entry, "305", "7") with { Directory = other.Entry.Directory };
        MediaLibraryEntry foreignAsset = Fresh(kept.Entry, "306", "8") with { Directory = other.Entry.Directory, AssetId = other.Entry.AssetId };
        MediaLibraryEntry[] gaps = [noDirectory, noAsset, blankAsset, noMedia, foreignFolder, foreignAsset];
        IReadOnlyList<MediaLibraryTile> tiles = TilesOf([kept.Entry, .. gaps]);
        Assert.Equal(gaps.Length + 1, tiles.Count);
        List<string?> opened = new();
        string destination = Path.Combine(directory.Path, "out");
        string[] before = Snapshot(Path.Combine(directory.Path, "media"));

        MediaLibraryExportSize size = MediaLibraryExport.Measure(tiles);
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (folder, token) => { opened.Add(folder); return MediaAssetReader.OpenAsync(folder, token); });
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(plan.Assets, destination);

        // Measure is the index-only upper bound: it may offer the two folder-pointing entries that the manifest then refuses,
        // but every entry whose facts are invalid counts as not offered in both, and the exported bytes are only kept's.
        Assert.Equal(4, size.NotOffered);
        Assert.Equal(size.Files, plan.Assets.Count + 2);
        Assert.Equal(size.NotOffered + 2, plan.Unavailable);
        Assert.DoesNotContain(opened, folder => string.IsNullOrWhiteSpace(folder));
        Assert.Equal(1, result.Copied);
        Assert.Equal(kept.Bytes, File.ReadAllBytes(Assert.Single(Files(destination))));
        Assert.Equal(before, Snapshot(Path.Combine(directory.Path, "media")));
    }

    // The same index entry as a tile of its own (another thread and post, hence its own group key via a distinct hash).
    private static MediaLibraryEntry Fresh(MediaLibraryEntry source, string thread, string post) =>
        source with { Thread = new("stub", "g", thread), PostId = post, PostOrdinal = long.Parse(post),
            Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fresh" + thread))).ToLowerInvariant() };

    [Fact]
    public async Task CancelBeforeTheCopyWritesNothingAndClaimsNothing()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved a = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        Saved b = await Commit(directory.Path, board, [4, 5, 6], "b", "200", new("stub", "g", "200"), "2");
        string destination = Path.Combine(directory.Path, "out");
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(TilesOf(a.Entry, b.Entry), MediaAssetReader.OpenAsync);

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(plan.Assets, destination, new CancellationToken(true));

        Assert.Equal((0, 2), (result.Copied, result.Cancelled));
        Assert.Empty(Files(destination));
        // Cancelling while the plan is being made stops the page without a plan at all.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryExport.BuildAsync(TilesOf(a.Entry), MediaAssetReader.OpenAsync, new CancellationToken(true)));
    }

    [Fact]
    public async Task AnUnwritableDestinationIsAFailureAndLeavesNoPartialFile()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved a = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        string asFile = Path.Combine(directory.Path, "not-a-folder");
        await File.WriteAllTextAsync(asFile, "x");

        (_, FavoriteExportResult result) = await ExportAsync(TilesOf(a.Entry), asFile);

        Assert.Equal((0, 1), (result.Copied, result.Failed));
        Assert.Equal("x", await File.ReadAllTextAsync(asFile));

        // A destination that cannot take files (the stand-in for a full device: the write throws an IOException/UnauthorizedAccess)
        // is a failure too, and the half-written temporary file is gone.
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            string readOnly = Path.Combine(directory.Path, "readonly");
            Directory.CreateDirectory(readOnly);
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                (_, FavoriteExportResult denied) = await ExportAsync(TilesOf(a.Entry), readOnly);
                Assert.Equal((0, 1), (denied.Copied, denied.Failed));
                Assert.Empty(Files(readOnly));
            }
            finally { File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
    }

    [Fact]
    public async Task ExportingTwiceIsRepeatableWithoutDuplicates()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Saved a = await Commit(directory.Path, board, [1, 2, 3], "a", "100", new("stub", "g", "100"), "1");
        string destination = Path.Combine(directory.Path, "out");
        await ExportAsync(TilesOf(a.Entry), destination);

        (_, FavoriteExportResult again) = await ExportAsync(TilesOf(a.Entry), destination);

        Assert.Equal((0, 1), (again.Copied, again.AlreadyExists));
        Assert.Single(Files(destination));
    }

    // An output stream that takes the first half of a write and then runs a fault, like a device that fills up or a cancel that arrives mid-copy.
    private sealed class HalfThenFaultStream(Stream inner, Action fault) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer[..(buffer.Length / 2)], cancellationToken);
            await inner.FlushAsync(cancellationToken);
            fault();
        }
    }

    private sealed class DiskFullException : IOException
    {
        // ENOSPC on Unix, ERROR_DISK_FULL as HRESULT on Windows.
        public DiskFullException() : base("No space left on device") => HResult = OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28;
    }

    private static async Task<(Saved[] Saved, FavoriteExportAsset[] Assets, string Destination, string Archive)> ThreeAsync(TempDirectory directory, StubImageBoard board)
    {
        Saved[] saved =
        [
            await Commit(directory.Path, board, [1, 2, 3, 4], "a", "100", new("stub", "g", "100"), "1"),
            await Commit(directory.Path, board, [5, 6, 7, 8], "b", "200", new("stub", "g", "200"), "2"),
            await Commit(directory.Path, board, [9, 10, 11, 12], "c", "300", new("stub", "g", "300"), "3")
        ];
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(TilesOf(saved.Select(item => item.Entry).ToArray()), MediaAssetReader.OpenAsync);
        Assert.Equal(3, plan.Assets.Count);
        return (saved, plan.Assets.ToArray(), Path.Combine(directory.Path, "out"), Path.Combine(directory.Path, "media"));
    }

    [Fact]
    public async Task CancelAfterTheFirstOfThreeFilesKeepsOnlyTheFirstAndReportsTheRestCancelled()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        (Saved[] saved, FavoriteExportAsset[] assets, string destination, string archive) = await ThreeAsync(directory, board);
        string[] archiveBefore = Snapshot(archive);
        using CancellationTokenSource cancel = new();

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportWithHooksAsync(assets, destination,
            new() { AfterItem = item => { if (item.Status == FavoriteExportStatus.Copied) cancel.Cancel(); } }, cancel.Token);

        Assert.Equal([FavoriteExportStatus.Copied, FavoriteExportStatus.Cancelled, FavoriteExportStatus.Cancelled], result.Items.Select(item => item.Status));
        Assert.Equal((1, 2), (result.Copied, result.Cancelled));
        Assert.NotEqual(result.Items.Count, result.Copied + result.AlreadyExists); // never reported as complete
        string only = Assert.Single(Files(destination)); // no pending temporary file either
        Assert.Equal(saved[0].Bytes, File.ReadAllBytes(only));
        Assert.Equal(result.Items[0].Path, only);
        Assert.Equal(archiveBefore, Snapshot(archive));
    }

    [Fact]
    public async Task CancelInTheMiddleOfTheSecondFileLeavesNoPartialFile()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        (Saved[] saved, FavoriteExportAsset[] assets, string destination, string archive) = await ThreeAsync(directory, board);
        string[] archiveBefore = Snapshot(archive);
        using CancellationTokenSource cancel = new();
        int second = 0, partialAtCancel = 0;

        // Half of the second file is on disk when the cancel arrives.
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportWithHooksAsync(assets, destination, new()
        {
            WrapOutput = (item, file) => ++second == 2
                ? new HalfThenFaultStream(file, () => { partialAtCancel = Files(destination).Count(path => Path.GetFileName(path).StartsWith(".favorite-pending-")); cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); })
                : file
        }, cancel.Token);

        Assert.Equal(1, partialAtCancel); // the half-written temporary file really existed when the cancel came
        Assert.Equal([FavoriteExportStatus.Copied, FavoriteExportStatus.Cancelled, FavoriteExportStatus.Cancelled], result.Items.Select(item => item.Status));
        string only = Assert.Single(Files(destination));
        Assert.Equal(saved[0].Bytes, File.ReadAllBytes(only));
        Assert.Equal(archiveBefore, Snapshot(archive));
    }

    [Fact]
    public async Task ADiskThatFillsUpOnTheSecondFileFailsItAndTheRestWithoutPartialFilesOrArchiveChanges()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        (Saved[] saved, FavoriteExportAsset[] assets, string destination, string archive) = await ThreeAsync(directory, board);
        string[] archiveBefore = Snapshot(archive);
        int count = 0, partialAtFault = 0;

        // From the second file on the device is full: half of the bytes are written, then every write fails with the disk-full error.
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportWithHooksAsync(assets, destination, new()
        {
            WrapOutput = (item, file) => ++count >= 2 ? new HalfThenFaultStream(file, () => { partialAtFault += Files(destination).Count(path => Path.GetFileName(path).StartsWith(".favorite-pending-")); throw new DiskFullException(); }) : file
        });

        Assert.Equal([FavoriteExportStatus.Copied, FavoriteExportStatus.Failed, FavoriteExportStatus.Failed], result.Items.Select(item => item.Status));
        Assert.Equal(2, partialAtFault); // a half-written temporary file existed at each failure
        Assert.All(result.Items.Skip(1), item => { Assert.Contains("No space left", item.Error); Assert.Null(item.Path); });
        Assert.Equal((1, 2), (result.Copied, result.Failed));
        Assert.NotEqual(result.Items.Count, result.Copied + result.AlreadyExists); // incomplete, not success
        string only = Assert.Single(Files(destination)); // no partial second file, no pending temporary
        Assert.Equal(saved[0].Bytes, File.ReadAllBytes(only));
        Assert.Equal(archiveBefore, Snapshot(archive));

        // Once there is room again the same export completes the missing files and keeps the first.
        FavoriteExportResult retry = await FavoriteOriginalExporter.ExportAsync(assets, destination);
        Assert.Equal((2, 1), (retry.Copied, retry.AlreadyExists));
        Assert.Equal(3, Files(destination).Length);
    }

    // Real ENOSPC on the fixture filesystem (MEOWCHAN_FS_FIXTURE_ROOT, TMPDIR inside it): archive and destination
    // share the full device. Whatever fits is byte-identical, the rest fails, nothing partial stays, and once
    // space returns the same export completes.
    [MeowChan.Tests.Transfer.DiskFullFixtureTheory]
    [InlineData(0)]
    [InlineData(64 * 1024)]
    [InlineData(400 * 1024)]
    public async Task ARealFullDiskFailsWhatDoesNotFitWithoutPartialFilesAndTheRetryCompletes(long freeBytes)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        byte[][] payloads = Enumerable.Range(0, 3).Select(index => RandomNumberGenerator.GetBytes(256 * 1024)).ToArray();
        Saved[] saved =
        [
            await Commit(directory.Path, board, payloads[0], "a", "100", new("stub", "g", "100"), "1"),
            await Commit(directory.Path, board, payloads[1], "b", "200", new("stub", "g", "200"), "2"),
            await Commit(directory.Path, board, payloads[2], "c", "300", new("stub", "g", "300"), "3")
        ];
        string destination = Path.Combine(directory.Path, "out"), archive = Path.Combine(directory.Path, "media");
        Directory.CreateDirectory(destination);
        string[] archiveBefore = Snapshot(archive);
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(TilesOf(saved.Select(item => item.Entry).ToArray()), MediaAssetReader.OpenAsync);
        string filler = Path.Combine(directory.Path, "filler.bin");
        FavoriteExportResult result;
        try
        {
            FillLeaving(filler, freeBytes);
            result = await FavoriteOriginalExporter.ExportAsync(plan.Assets, destination);
        }
        finally { File.Delete(filler); }

        Assert.All(result.Items, item => Assert.Contains(item.Status, new[] { FavoriteExportStatus.Copied, FavoriteExportStatus.Failed }));
        Assert.True(result.Failed > 0, "The fixture must actually run out of space.");
        Assert.NotEqual(result.Items.Count, result.Copied + result.AlreadyExists); // incomplete, never success
        Assert.All(result.Items.Where(item => item.Status == FavoriteExportStatus.Failed), item => { Assert.NotNull(item.Error); Assert.Null(item.Path); });
        // Only whole, byte-identical originals: no partial file and no pending temporary.
        Assert.Equal(result.Copied, Files(destination).Length);
        Assert.All(Files(destination), path => Assert.Contains(Sha(File.ReadAllBytes(path)), saved.Select(item => item.Sha)));
        Assert.Equal(archiveBefore, Snapshot(archive));

        FavoriteExportResult retry = await FavoriteOriginalExporter.ExportAsync(plan.Assets, destination);
        Assert.Equal((3 - result.Copied, result.Copied), (retry.Copied, retry.AlreadyExists));
        Assert.Equal(saved.Select(item => item.Sha).Order(), Files(destination).Select(path => Sha(File.ReadAllBytes(path))).Order());
    }

    private static void FillLeaving(string path, long freeBytes)
    {
        DriveInfo drive = new(Path.GetDirectoryName(path)!);
        byte[] chunk = new byte[1024 * 1024];
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            for (int size = chunk.Length; size >= 4096; size /= 2)
                while (drive.AvailableFreeSpace - size >= freeBytes) stream.Write(chunk, 0, size);
            stream.Flush(true);
        }
        catch (IOException) { /* Already full: the strictest level. */ }
    }
}
