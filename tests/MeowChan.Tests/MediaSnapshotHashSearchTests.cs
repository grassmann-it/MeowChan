using System.Globalization;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaSnapshotHashSearchTests
{
    private static readonly string Hash = new('b', 64);
    private static readonly string OtherHash = new('c', 64);

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    public void QueryAcceptsExactFullSha256OnlyAndNormalizesWithoutTrimmingOrCulture(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            string uppercase = Hash.ToUpperInvariant();
            MediaAssetSnapshot snapshot = Snapshot([Asset()]);

            MediaSnapshotHashQuery query = MediaSnapshotHashSearch.CreateQuery(snapshot, uppercase);

            Assert.Equal(Hash, query.Sha256);
            Assert.Equal(uppercase, Hash.ToUpperInvariant());
            Assert.Single(query.Search().GetPage());

            string md5 = new('b', 32);
            string nonHex = Hash[..63] + "g";
            string fullWidthNonHex = Hash[..63] + "Ａ";
            string[] invalid =
            [
                "",
                " ",
                " " + Hash,
                Hash + " ",
                Hash[..63],
                Hash + "b",
                "sha256:" + Hash,
                md5,
                nonHex,
                fullWidthNonHex
            ];
            Assert.Throws<ArgumentNullException>(() => MediaSnapshotHashSearch.CreateQuery(snapshot, null!));
            Assert.All(invalid, value => Assert.Throws<ArgumentException>(
                () => MediaSnapshotHashSearch.CreateQuery(snapshot, value)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void LeadingZeroIsSignificantAndExactMatchUsesOnlyCommittedSha256Metadata()
    {
        string leadingZero = "0" + new string('a', 63);
        MediaAssetReference expected = Asset(hash: leadingZero);
        MediaAssetReference other = Asset(assetId: "other", hash: OtherHash);
        MediaAssetSnapshot snapshot = Snapshot([expected, other]);

        MediaSnapshotHashQuery query = MediaSnapshotHashSearch.CreateQuery(
            snapshot, ("0" + new string('A', 63)));
        MediaSnapshotHashSearchResult result = query.Search();

        Assert.Equal(MediaSnapshotHashSearchStatus.Available, result.Status);
        Assert.Equal(leadingZero, result.Sha256);
        Assert.Equal("generation", result.Generation);
        Assert.Equal(1, result.Count);
        Assert.Equal(expected, Assert.Single(result.GetPage()));
    }

    [Fact]
    public void SameBytesCanHaveMultipleAssetIdsIncludingHistoricalAndPreviewVariants()
    {
        MediaAssetReference current = Asset("current", mediaId: "1", requestedFileName: "current.bin");
        MediaAssetReference historical = Asset("historical", mediaId: "2", requestedFileName: "historical.bin",
            isCurrent: false);
        MediaAssetReference preview = Asset("preview", mediaId: "3", requestedFileName: "preview.bin",
            variant: "preview", isCurrent: false);
        MediaAssetReference other = Asset("other", mediaId: "4", requestedFileName: "other.bin", hash: OtherHash);
        MediaAssetSnapshot snapshot = Snapshot([current, historical, preview, other]);

        MediaSnapshotHashSearchResult result = MediaSnapshotHashSearch.CreateQuery(snapshot, Hash).Search();

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { current, historical, preview }, result.GetPage());
        Assert.Equal(new[] { current, historical }, result.GetPage(0, 2));
        Assert.Equal(preview, Assert.Single(result.GetPage(2, 500)));
        Assert.False(historical.IsCurrent);
        Assert.Equal("preview", preview.Variant);
        Assert.Equal(preview, result.GetPage(2).Single());
        Assert.DoesNotContain(result.GetPage(), asset => asset.AssetId == other.AssetId);
    }

    [Fact]
    public void ResultAndRepeatedSearchRemainBoundToTheSnapshotCapturedByTheQuery()
    {
        MediaAssetReference first = Asset("first", mediaId: "1");
        MediaAssetReference second = Asset("second", mediaId: "2");
        MediaAssetSnapshot oldSnapshot = Snapshot([first], "old");
        MediaAssetSnapshot newSnapshot = Snapshot([first, second], "new");
        MediaSnapshotHashQuery query = MediaSnapshotHashSearch.CreateQuery(oldSnapshot, Hash);
        MediaSnapshotHashSearchResult firstResult = query.Search();

        MediaSnapshotHashSearchResult secondResult = query.Search();
        MediaSnapshotHashSearchResult newerResult = MediaSnapshotHashSearch.CreateQuery(newSnapshot, Hash).Search();

        Assert.Equal("old", firstResult.Generation);
        Assert.Equal("old", secondResult.Generation);
        Assert.Equal(first, Assert.Single(firstResult.GetPage()));
        Assert.Equal(first, Assert.Single(secondResult.GetPage()));
        Assert.Equal(2, newerResult.Count);
        Assert.Equal(1, firstResult.Count);
        Assert.Equal(1, oldSnapshot.Count);
        Assert.Equal(new[] { first }, oldSnapshot.GetPage());
    }

    [Fact]
    public void SearchCrossesTheFiveHundredAssetBoundaryAndResultPagesAreBoundedAndReadOnly()
    {
        MediaAssetReference[] assets = Enumerable.Range(0, 501)
            .Select(index => index == 500
                ? Asset("target", mediaId: "0500", hash: Hash)
                : Asset("asset-" + index, mediaId: index.ToString("D4", CultureInfo.InvariantCulture), hash: OtherHash))
            .ToArray();
        MediaAssetReference target = assets[^1];
        MediaAssetSnapshot snapshot = Snapshot([.. assets]);

        MediaSnapshotHashSearchResult result = MediaSnapshotHashSearch.CreateQuery(snapshot, Hash).Search();

        Assert.Equal(501, snapshot.Count);
        Assert.Equal(1, result.Count);
        Assert.Equal(target, Assert.Single(snapshot.GetPage(500, 500)));
        Assert.Equal(target, Assert.Single(result.GetPage(0, 500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPage(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPage(0, 501));
        Assert.Empty(result.GetPage(int.MaxValue));
        IReadOnlyList<MediaAssetReference> page = result.GetPage();
        Assert.Throws<NotSupportedException>(() => ((IList<MediaAssetReference>)page)[0] = target);
    }

    [Fact]
    public void MissingAndCorruptManifestsAreExplicitAndExposeNoEmptySuccessPage()
    {
        (MediaManifestStatus, MediaSnapshotHashSearchStatus)[] cases =
        [
            (MediaManifestStatus.Missing, MediaSnapshotHashSearchStatus.MissingManifest),
            (MediaManifestStatus.Corrupt, MediaSnapshotHashSearchStatus.CorruptManifest)
        ];
        foreach ((MediaManifestStatus manifestStatus, MediaSnapshotHashSearchStatus expected) in cases)
        {
            MediaAssetSnapshot snapshot = new("/synthetic/never-opened", manifestStatus, null, null, []);
            MediaSnapshotHashQuery query = MediaSnapshotHashSearch.CreateQuery(snapshot, Hash.ToUpperInvariant());

            MediaSnapshotHashSearchResult result = query.Search();

            Assert.Equal(expected, result.Status);
            Assert.Equal(Hash, result.Sha256);
            Assert.Null(result.Generation);
            Assert.Throws<InvalidOperationException>(() => result.Count);
            Assert.Throws<InvalidOperationException>(() => result.GetPage());
        }
    }

    [Fact]
    // A recorded hash hit is metadata-only: missing current bytes must still be searchable without verification.
    public void MissingAssetPathsAndPrivateProfileStateAreNotTouchedByMetadataSearch()
    {
        using TempDirectory directory = new();
        string missingPath = Path.Combine(directory.Path, "not-created.bin");
        MediaAssetSnapshot snapshot = new(directory.Path, MediaManifestStatus.Available, 4,
            "generation", [Asset(path: missingPath)]);
        string[] before = Directory.GetFileSystemEntries(directory.Path);

        MediaSnapshotHashSearchResult result = MediaSnapshotHashSearch.CreateQuery(snapshot, Hash).Search();

        Assert.Equal(1, result.Count);
        Assert.Equal(missingPath, Assert.Single(result.GetPage()).Path);
        Assert.Equal(before, Directory.GetFileSystemEntries(directory.Path));
        Assert.False(File.Exists(missingPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".meowchan-manifest.json")));
    }

    [Fact]
    public void CancelledSearchDoesNotTouchMissingPathsOrReturnAPartialSuccess()
    {
        using TempDirectory directory = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        string missingPath = Path.Combine(directory.Path, "cancelled.bin");
        MediaAssetSnapshot snapshot = new(directory.Path, MediaManifestStatus.Available, 4,
            "generation", [Asset(path: missingPath)]);
        MediaSnapshotHashQuery query = MediaSnapshotHashSearch.CreateQuery(snapshot, Hash);

        Assert.Throws<OperationCanceledException>(() => query.Search(cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        Assert.False(File.Exists(missingPath));
    }

    private static MediaAssetReference Asset(string assetId = "asset", string? hash = null,
        string variant = "original", string mediaId = "media", string requestedFileName = "file.bin",
        string path = "/synthetic/never-opened/file.bin", bool isCurrent = true)
        => new(assetId, mediaId, variant, requestedFileName, path, 3, hash ?? Hash,
            "https://stub.test/media", "original.bin", isCurrent);

    private static MediaAssetSnapshot Snapshot(MediaAssetReference[] assets,
        string generation = "generation")
        => new("/synthetic/never-opened", MediaManifestStatus.Available, 4, generation, assets);
}
