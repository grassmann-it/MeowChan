using System;
using System.Linq;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

/// <summary>BP3's local-only identity and catalog contracts. These tests do not start a provider or a UI host.</summary>
public sealed class BooruCrossLinksTests
{
    private static MediaLibraryEntry Entry(ResourceId thread, string postId, string name, MediaLibraryPresence presence = MediaLibraryPresence.Present) =>
        new(thread, "Danbooru #" + postId, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, null, null, null, null, "/media", "asset-" + postId, new string('a', 64), 1,
            "booru-danbooru-" + postId, "original", name, SavedMediaKind.Image, presence, true, false)
        { BooruPostId = postId };

    [Fact]
    public void SavedEntryRequiresTheExactProviderThreadAndPostIdentity()
    {
        MediaLibraryEntry exact = Entry(BooruMediaIdentity.Thread("123"), "123", "123.png");
        MediaLibraryEntry idSubstring = Entry(BooruMediaIdentity.Thread("1234"), "1234", "1234.png");
        MediaLibraryEntry wrongSource = Entry(new ResourceId("other", BooruMediaIdentity.Board, "123"), "123", "123.png");
        MediaLibraryEntry missing = Entry(BooruMediaIdentity.Thread("123"), "123", "123.png", MediaLibraryPresence.Missing);

        Assert.True(MediaPage.IsSavedBooruEntry(exact, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(idSubstring, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(wrongSource, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(missing, "123"));
    }

    [Fact]
    public void SourceAndPostIdFiltersStillHaveAnExactThreadGuard()
    {
        MediaLibraryEntry exact = Entry(BooruMediaIdentity.Thread("123"), "123", "123.png");
        MediaLibraryEntry idSubstring = Entry(BooruMediaIdentity.Thread("1234"), "1234", "1234.png");
        MediaLibraryFilter filter = new(source: BooruMediaIdentity.Source, nameContains: "123",
            thread: BooruMediaIdentity.Thread("123"));

        Assert.Equal(exact, Assert.Single(MediaLibraryFilterEngine.Apply(new[] { exact, idSubstring }, filter,
            MediaLibraryUserData.Unavailable)));
    }

    [Fact]
    public void CrossLinkCatalogHasCompleteEnglishAndGermanOwnedKeys()
    {
        Assert.Equal(BooruCrossLinksCatalog.Entries.Length,
            System.Linq.Enumerable.Distinct(BooruCrossLinksCatalog.Entries, new KeyComparer()).Count());
        Assert.Contains(BooruCrossLinksCatalog.Entries, entry => entry.Key == "booru.ui.result.showInMedia");
        Assert.Contains(BooruCrossLinksCatalog.Entries, entry => entry.Key == "booru.ui.watcher.showInMediaFor");
        Assert.Contains(BooruCrossLinksCatalog.Entries, entry => entry.Key == "media.toolbar.booru");
        Assert.All(BooruCrossLinksCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
        });
    }

    [Fact]
    public void BooruLauncherIsOverflowInEveryWorkView()
    {
        WorkViewEntry entry = WorkViewPolicy.Get("media.toolbar.booru");
        Assert.Equal(WorkViewPage.Media, entry.Page);
        Assert.Equal(WorkViewKind.Action, entry.Kind);
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Overflow],
            WorkViewSetting.All.Select(view => entry.For(view)));
    }

    private sealed class KeyComparer : System.Collections.Generic.IEqualityComparer<(string Key, string English, string German)>
    {
        public bool Equals((string Key, string English, string German) x, (string Key, string English, string German) y) => x.Key == y.Key;
        public int GetHashCode((string Key, string English, string German) value) => StringComparer.Ordinal.GetHashCode(value.Key);
    }
}
