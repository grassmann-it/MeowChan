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
}
