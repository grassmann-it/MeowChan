using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.Tests;

public sealed class MediaRatingCoreTests
{
    private static FavoriteAssetKey Key => new(new("source", "board", "42"), "media", "original", new string('a', 64));
    [Fact]
    public void NewProfileDoesNotCreateFileAndAllValuesRoundtripIndependentlyOfFavorites()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "media-ratings.json");
        byte[] favoriteBytes;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore favorites = new(temp.Path, lease);
            favorites.SetFavorite(new(Key, 7, "image.png"), true);
            favoriteBytes = File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json"));
            MediaRatingStore store = new(temp.Path, lease);
            Assert.Empty(store.List()); Assert.False(File.Exists(file));
            Assert.False(store.Set(Key, null).Changed); Assert.False(File.Exists(file));
            for (int value = 1; value <= 5; value++)
            {
                Assert.True(store.Set(Key, value).Changed);
                byte[] saved = File.ReadAllBytes(file);
                Assert.False(store.Set(Key, value).Changed);
                Assert.Equal(saved, File.ReadAllBytes(file));
                Assert.Equal(value, Assert.Single(new MediaRatingStore(temp.Path, lease).List()).Value);
            }
            Assert.Equal(favoriteBytes, File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json")));
        }
        using ProfileLease restored = ProfileLease.Acquire(temp.Path);
        MediaRatingStore restarted = new(temp.Path, restored);
        Assert.Equal(5, Assert.Single(restarted.List()).Value);
        Assert.True(restarted.Set(Key, null).Changed);
        Assert.Empty(restarted.List());
        Assert.Equal(favoriteBytes, File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json")));
    }
    [Fact]
    public void IdentitiesSeparateHashVariantMediaAndThreadWithoutResolvingAnyFiles()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        FavoriteAssetKey[] keys = [Key, Key with { Sha256 = new string('b', 64) }, Key with { Variant = "thumbnail" },
            Key with { MediaIdentifier = "other" }, Key with { Thread = new("source", "board", "43") }];
        for (int i = 0; i < keys.Length; i++) store.Set(keys[i], i + 1);
        Assert.Equal(5, store.List().Count);
        Assert.Equal(4, store.RemoveThreads([Key.Thread]));
        Assert.Equal(keys[4], Assert.Single(store.List()).Key);
        Assert.Equal(0, store.RemoveThreads([Key.Thread]));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(6)]
    public void InvalidValuesCannotMutateStore(int value)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        store.Set(Key, 3); byte[] original = File.ReadAllBytes(Path.Combine(temp.Path, "media-ratings.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Set(Key, value));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(temp.Path, "media-ratings.json")));
    }
    [Fact]
    public void CorruptFutureDuplicateAndInvalidKeysBlockWritesWithoutTouchingPrimaryOrBackup()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        string file = Path.Combine(temp.Path, "media-ratings.json");
        string[] documents = ["{broken", JsonSerializer.Serialize(new { Version = 2, Entries = new[] { new MediaRatingEntry(Key, 3) } }),
            JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new MediaRatingEntry(Key, 3), new MediaRatingEntry(Key, 4) } }),
            JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new MediaRatingEntry(Key, 0) } }),
            JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new MediaRatingEntry(Key with { Sha256 = new string('A', 64) }, 3) } })];
        File.WriteAllText(file + ".bak", "preserve backup");
        foreach (string json in documents)
        {
            File.WriteAllText(file, json);
            Assert.Throws<InvalidDataException>(() => store.List());
            Assert.Throws<InvalidDataException>(() => store.Set(Key, 5));
            Assert.Equal(json, File.ReadAllText(file)); Assert.Equal("preserve backup", File.ReadAllText(file + ".bak"));
        }
    }
    [Fact]
    public void MalformedUtf8IdentityBlocksReadsAndAllMutationsWithoutReplacingPrimaryOrBackup()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        store.Set(Key, 3); store.Set(Key, 4);
        string file = Path.Combine(temp.Path, "media-ratings.json");
        byte[] corrupt = File.ReadAllBytes(file);
        byte[] backup = File.ReadAllBytes(file + ".bak");
        string valid = Encoding.UTF8.GetString(corrupt);
        const string property = "\"MediaIdentifier\":\"";
        int start = valid.IndexOf(property, StringComparison.Ordinal);
        Assert.True(start >= 0);
        int offset = Encoding.UTF8.GetByteCount(valid[..(start + property.Length)]);
        Assert.Equal((byte)'m', corrupt[offset]);
        corrupt[offset] = 0xFF;
        File.WriteAllBytes(file, corrupt);

        InvalidDataException readError = Assert.Throws<InvalidDataException>(() => store.List());
        Assert.IsType<DecoderFallbackException>(readError.InnerException);
        Assert.Throws<InvalidDataException>(() => store.Set(Key with { MediaIdentifier = "another" }, 5));
        Assert.Throws<InvalidDataException>(() => store.Set(Key, null));
        Assert.Throws<InvalidDataException>(() => store.RemoveThreads([Key.Thread]));
        Assert.Equal(corrupt, File.ReadAllBytes(file));
        Assert.Equal(backup, File.ReadAllBytes(file + ".bak"));
    }
    [Fact]
    public void StrictUtf8PreservesUnicodeIdentityAndAcceptsOptionalUtf8Bom()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        FavoriteAssetKey unicode = Key with { MediaIdentifier = "Bild-猫-😀-\uFFFD" };
        store.Set(unicode, 5);
        string file = Path.Combine(temp.Path, "media-ratings.json");
        string rawUnicodeJson = JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new MediaRatingEntry(unicode, 5) } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("猫", rawUnicodeJson);
        byte[] json = Encoding.UTF8.GetBytes(rawUnicodeJson);
        File.WriteAllBytes(file, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray());
        Assert.Equal(new MediaRatingEntry(unicode, 5), Assert.Single(new MediaRatingStore(temp.Path, lease).List()));
    }
    [Fact]
    public void ClearThenMissingPrimaryNeverResurrectsBackup()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease); string file = Path.Combine(temp.Path, "media-ratings.json");
        store.Set(Key, 4); store.Set(Key, null);
        Assert.Empty(new MediaRatingStore(temp.Path, lease).List());
        File.Delete(file);
        Assert.Throws<InvalidDataException>(() => new MediaRatingStore(temp.Path, lease).List());
        Assert.Throws<InvalidDataException>(() => store.Set(Key, 1));
        Assert.False(File.Exists(file));
    }
    [Fact]
    public void CancellationAndBackupFailureLeaveOldSnapshotAndAllowRetry()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease); store.Set(Key, 2);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Set(Key, 5, cancel.Token));
        string backup = Path.Combine(temp.Path, "media-ratings.json.bak"); Directory.CreateDirectory(backup);
        Assert.Throws<InvalidDataException>(() => store.Set(Key, 5));
        Assert.Equal(2, Assert.Single(store.List()).Value);
        Directory.Delete(backup); Assert.True(store.Set(Key, 5).Changed);
    }
    [Fact]
    public void OversizedReadAndWriteCannotReplaceOldSnapshot()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string file = Path.Combine(temp.Path, "media-ratings.json");
        MediaRatingStore store = new(temp.Path, lease); store.Set(Key, 3);
        byte[] original = File.ReadAllBytes(file);
        Assert.Throws<InvalidDataException>(() => store.Set(Key with { MediaIdentifier = new string('x', MediaRatingStore.MaximumBytes) }, 1));
        Assert.Equal(original, File.ReadAllBytes(file));
        File.WriteAllText(file, new string(' ', MediaRatingStore.MaximumBytes + 1));
        Assert.Throws<InvalidDataException>(() => store.List());
    }
    [Fact]
    public void EntryLimitAndLeaseOwnershipAreEnforced()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        Assert.Throws<ArgumentException>(() => new MediaRatingStore(Path.Combine(temp.Path, "other"), lease));
        string file = Path.Combine(temp.Path, "media-ratings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { Version = 1, Entries = new MediaRatingEntry?[MediaRatingStore.MaximumEntries + 1] }));
        Assert.Contains("count", Assert.Throws<InvalidDataException>(() => store.List()).Message);
        Assert.Throws<InvalidDataException>(() => store.Set(Key, null));
        File.Delete(file); lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Set(Key, 1));
    }
    [Fact]
    public void SymlinkPrimaryAndBackupAreRejected()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string file = Path.Combine(temp.Path, "media-ratings.json");
        string outside = Path.Combine(temp.Path, "outside.json"); File.WriteAllText(outside, "preserve");
        File.CreateSymbolicLink(file, outside);
        MediaRatingStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.List()); File.Delete(file);
        store.Set(Key, 3); File.CreateSymbolicLink(file + ".bak", outside);
        Assert.Throws<InvalidDataException>(() => store.Set(Key, 5));
        Assert.Equal("preserve", File.ReadAllText(outside));
    }
    [Theory]
    [InlineData(null, 0, true)] [InlineData(null, 1, true)] [InlineData(null, 2, false)]
    [InlineData(1, 1, false)] [InlineData(1, 2, true)] [InlineData(3, 4, true)] [InlineData(3, 5, false)] [InlineData(5, 6, true)]
    public void FiltersHaveExplicitUnratedAndMinimumSemantics(int? value, int filter, bool matches)
        => Assert.Equal(matches, MediaRatingProjection.Matches(value, filter));
}
