using System.Text;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.Tests;

/// <summary>
/// The three sibling lists (media favorites, thread favorites, ratings) share one default entry limit and one default
/// file limit of 8 MiB (8388608 bytes), all from <see cref="ProfileStoreLimits"/>. Two of the stores used to carry their
/// own copies with a wrong "8 MiB" of 8000000 bytes; they are obsolete aliases now. Files written under the old value stay readable.
/// </summary>
public sealed class ProfileStoreLimitsSharedConstantsTests
{
    [Fact]
    public void AllThreeStoresUseTheSharedDefaultsAndEightMiBIsEightMiB()
    {
#pragma warning disable CS0618 // The former public constants stay, as obsolete aliases of the shared defaults.
        Assert.Equal(8 * 1024 * 1024, ProfileStoreLimits.DefaultFavoriteFileBytes);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteFileBytes, FavoriteStore.MaximumBytes);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteFileBytes, ThreadFavoriteStore.MaximumBytes);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteFileBytes, MediaRatingStore.MaximumBytes);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteEntries, FavoriteStore.MaximumEntries);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteEntries, ThreadFavoriteStore.MaximumEntries);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteEntries, MediaRatingStore.MaximumEntries);
        Assert.Equal(ProfileStoreLimits.DefaultFavoriteFileBytes, new ProfileStoreLimits().FavoriteFileBytes);
#pragma warning restore CS0618
    }

    [Fact]
    public void TheListDefaultsAreNamedAsDefaultsAndComeFromTheSharedLimits()
    {
        // These were called Maximum*, but they are only the defaults of settings whose ceilings are far higher.
        Assert.Equal(ProfileStoreLimits.DefaultBookmarks, PostBookmarkStore.DefaultEntries);
        Assert.Equal(ProfileStoreLimits.DefaultSavedSearches, SavedArchiveSearchStore.DefaultSearches);
        Assert.Equal(ProfileStoreLimits.DefaultReaderGroups, ReaderGroupsValidation.DefaultGroups);
        Assert.True(PostBookmarkStore.DefaultEntries < ProfileStoreLimits.BookmarkCeiling);
        Assert.True(SavedArchiveSearchStore.DefaultSearches < ProfileStoreLimits.SavedSearchCeiling);
        Assert.True(ReaderGroupsValidation.DefaultGroups < ProfileStoreLimits.ReaderGroupCeiling);
    }

    [Theory]
    [InlineData(8_000_000)]
    [InlineData(8_000_001)]
    [InlineData(8 * 1024 * 1024)]
    public void FilesUpToTheDefaultLimitStayReadableByEveryStore(int length)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Write(Path.Combine(temp.Path, "thread-favorites.json"), "{\"Version\":1,\"Revision\":3,\"Threads\":[{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"}]}", length);
        Write(Path.Combine(temp.Path, "media-ratings.json"), "{\"Version\":1,\"Entries\":[]}", length);

        ThreadFavoriteSnapshot threads = new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot();
        Assert.Equal(new ResourceId("source", "board", "42"), Assert.Single(threads.Threads));
        Assert.Empty(new MediaRatingStore(temp.Path, lease).List());
    }

    private static void Write(string path, string json, int length)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using FileStream stream = File.Create(path);
        stream.Write(bytes);
        // Trailing whitespace is valid JSON and pads the file to exactly the requested length.
        byte[] padding = new byte[64 * 1024];
        Array.Fill(padding, (byte)' ');
        for (long remaining = length - bytes.Length; remaining > 0; remaining -= padding.Length)
            stream.Write(padding, 0, (int)Math.Min(remaining, padding.Length));
        Assert.Equal(length, stream.Length);
    }
}
