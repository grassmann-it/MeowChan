using System.Text;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.Tests;

public sealed class MediaLabelCoreTests
{
    private static FavoriteAssetKey Key => new(new("source", "board", "42"), "media", "original", new string('a', 64));

    [Fact]
    public void LabelSetChangeRemoveAndPersistenceLeaveTheRatingAlone()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "media-ratings.json");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            MediaRatingStore store = new(temp.Path, lease);
            Assert.False(store.SetLabel(Key, null).Changed); Assert.False(File.Exists(file));
            store.Set(Key, 4);
            Assert.True(store.SetLabel(Key, "red").Changed);
            Assert.False(store.SetLabel(Key, "red").Changed);
            Assert.True(store.SetLabel(Key, "blue").Changed);
            Assert.Equal("blue", Assert.Single(store.ListLabels()).Color);
            Assert.Equal(4, Assert.Single(store.List()).Value);
            Assert.True(store.Set(Key, null).Changed); // removing the rating keeps the label
            Assert.Equal("blue", Assert.Single(store.ListLabels()).Color);
        }
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            MediaRatingStore store = new(temp.Path, lease);
            Assert.Equal("blue", Assert.Single(store.ListLabels()).Color);
            Assert.True(store.SetLabel(Key, null).Changed);
            Assert.Empty(store.ListLabels());
            Assert.DoesNotContain("Labels", File.ReadAllText(file));
        }
    }

    [Fact]
    public void AnOldFileWithoutLabelsReadsAsNoLabelsAndIsWrittenBackUnchanged()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "media-ratings.json");
        string key = "{\"Thread\":{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"},\"MediaIdentifier\":\"media\",\"Variant\":\"original\",\"Sha256\":\"" + new string('a', 64) + "\"}";
        File.WriteAllText(file, "{\"Version\":1,\"Entries\":[{\"Key\":" + key + ",\"Value\":3}]}", new UTF8Encoding(false));
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        Assert.Equal(3, Assert.Single(store.List()).Value);
        Assert.Empty(store.ListLabels());
        Assert.True(store.Set(Key, 5).Changed);
        Assert.DoesNotContain("Labels", File.ReadAllText(file));
    }

    [Fact]
    public void InvalidColorsAreRefusedAndThreadCleanupRemovesLabels()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStore store = new(temp.Path, lease);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetLabel(Key, "purple"));
        store.SetLabel(Key, MediaLabelColors.Green);
        store.SetLabel(Key with { Thread = new("source", "board", "43") }, MediaLabelColors.Yellow);
        store.RemoveThreads([Key.Thread]);
        Assert.Equal("yellow", Assert.Single(store.ListLabels()).Color);
    }
}
