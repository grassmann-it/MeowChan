using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// MP1-MP4 around the media page: the settings and view preferences of the Booru mode, the work-view rows, the tile's queue mark, and the
// source contracts of the page's wiring (the tag list writes the page's term set through the same checks as every other way of adding a
// tag, the mode is saved per profile and restored without saving, the queue is read passively and only realized tiles are touched).
// The page itself needs the asset loader; its behaviour is driven in the media-page smoke (MediaPageChecks.BooruMode.cs).
[Collection("Localization")]
public sealed class MediaBooruModeTests : IDisposable
{
    public MediaBooruModeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", relative)));

    [Fact]
    public void TheConfigKeepsTheModeTheOrderAndTheTopNWithItsRange()
    {
        Config config = new();
        Assert.False(config.MediaBooruMode);
        Assert.Equal("Count", config.MediaTagListSort);
        Assert.Equal((30, 5, 1_000), (config.MediaTagListTop, Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum));
        config.MediaTagListSort = "name";
        Assert.Equal("Name", config.MediaTagListSort);
        config.MediaTagListSort = "bogus";
        Assert.Equal("Count", config.MediaTagListSort);
        config.MediaBooruMode = true; config.MediaTagListSort = "Name"; config.MediaTagListTop = 12;
        Config clone = config.Clone();
        Assert.Equal((true, "Name", 12), (clone.MediaBooruMode, clone.MediaTagListSort, clone.MediaTagListTop));
        Config copy = new();
        copy.CopyFrom(config);
        Assert.Equal((true, "Name", 12), (copy.MediaBooruMode, copy.MediaTagListSort, copy.MediaTagListTop));
        foreach (int bad in new[] { Config.MediaTagListTopMinimum - 1, Config.MediaTagListTopMaximum + 1 })
        {
            Config invalid = new() { MediaTagListTop = bad };
            Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
        }
        // The settings transfer and the settings editor carry the limit; the mode and the order are view preferences like the media order.
        string transfer = Read("Desktop/Settings/SettingsTransfer.cs");
        Assert.Contains("MediaTagListTop = source.MediaTagListTop,", transfer);
        Assert.Contains("MediaBooruMode = source.MediaBooruMode,", transfer);
        Assert.Contains("MediaTagListSort = source.MediaTagListSort,", transfer);
        string editor = Read("Desktop/Views/Settings/SettingsEditor.TagQueue.cs");
        Assert.Contains("candidate.MediaTagListTop = GetInteger(MediaTagListTop, MediaTagListTopLabel, Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum);", editor);
        Assert.Contains("x:Name=\"MediaTagListTopInput\"", Read("Desktop/Views/Settings/SettingsEditor.axaml"));
    }

    [Fact]
    public void TheModeAndTheOrderAreSavedPerProfileAndRestoredWithoutSaving()
    {
        string host = Read("Desktop/Shell/MainWindow.Media.cs"), mode = Read("Desktop/Views/Media/MediaPage.BooruMode.cs");
        Assert.Contains("_mediaPage.RestoreBooruMode(_session.Config.MediaBooruMode,", host);
        Assert.Contains("_session.SaveViewPreferencesAsync(config => { config.MediaBooruMode = on; config.MediaTagListSort = sort.ToString(); })", host);
        Assert.Contains("SetBooruMode(on, save: false);", mode);
        Assert.Contains("if (changed && save) SaveBooruMode();", mode);
        Assert.Contains("_tagSidebar.SortChanged = _ => SaveBooruMode();", mode);
        // The toolbar switch drives the mode; the mode drives the switch back without a second save.
        Assert.Contains("if (!_booruModeSyncing) SetBooruMode(_booruMode.IsChecked == true);", mode);
    }

    [Fact]
    public void TheTagListWritesTheTermSetThroughTheSameChecksAsEveryOtherWayOfAddingATag()
    {
        string mode = Read("Desktop/Views/Media/MediaPage.BooruMode.cs");
        Assert.Contains("_tagSidebar.Changed = (term, change) => SetTagListTerm(term, change);", mode);
        Assert.Contains("refusal = held != null ? ToggleTagFilter(Guid.Parse(identity.Value)) : TryIncludeOrExclude(Guid.Parse(identity.Value), include: !exclude);", mode);
        Assert.Contains("else refusal = SourceTagRefusal(identity with { Exclude = exclude }) ?? AddFilterTerm(identity with { Exclude = exclude });", mode);
        Assert.Contains("return same >= ArchiveTagFilterTagsPerListPreference ? Texts.T(\"coreLimits.tags.tooMany\", ArchiveTagFilterTagsPerListPreference) : null;", mode);
        Assert.Contains("if (SourceTagRefusal(term, terms) != null) return MediaFilterQueryProblem.TooManyTags;", Read("Desktop/Views/Media/MediaPage.Query.cs"));
        // After every presentation the list shows the terms and counts of what is shown; the count runs off the UI thread and is cancelled by a newer one.
        string page = Read("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("RefreshTagSidebar(); // MP4: the tag list counts the tiles now shown", page);
        Assert.Contains("_tagCounting?.Cancel();", mode);
        Assert.Contains("await Task.Run(() =>", mode);
        // The page-side terms narrow the passing media and, since the media search breakdown, the per-field tally too.
        Assert.Contains("if (input.PageTerms is { } pageTerms)", page);
        Assert.Contains("passing = pageTerms.Apply(passing, input.Queue, sourceTags);", page);
    }

    [Fact]
    public void TheWorkViewsKeepTheSidebarSwitchesInTheBarAndTheWaysToTaggingUnderMore() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("This test does not read the media library."))
        {
            OpenTagQueueWalk = () => Task.CompletedTask,
            OpenTaggingPage = () => true
        };
        try
        {
            const string tags = "media.toolbar.tag-list", panel = "media.toolbar.panel";
            const string walk = "media.toolbar.queue-walk", tagging = "media.toolbar.tagging-page";
            var group = Assert.IsType<SidebarToggleGroup>(page.BooruModeButton.Parent);
            Assert.Same(group, page.PanelToggleButton.Parent);
            foreach (WorkView view in WorkViewSetting.All)
            {
                page.ApplyWorkView(view, viewChanged: true);
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(tags, view));
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(panel, view));
                Assert.True(page.BooruModeButton.IsVisible);
                Assert.True(page.PanelToggleButton.IsVisible);
                string[] overflow = page.HeaderMoreIds.ToArray();
                Assert.DoesNotContain(tags, overflow);
                Assert.DoesNotContain(panel, overflow);
                Assert.Contains(walk, overflow);
                Assert.Equal(Array.IndexOf(overflow, walk) + 1, Array.IndexOf(overflow, tagging));
            }

            Assert.True(WorkViewPolicy.Get("media.selection.queue").OverflowOrder < WorkViewPolicy.Get("media.selection.tag").OverflowOrder);
            Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("media.selection.queue", WorkView.Simple));
            StackPanel selection = Assert.Single(page.GetLogicalDescendants().OfType<StackPanel>(), row => row.Name == "MediaSelectionActions");
            Assert.Equal(["media.selection.queue", "media.selection.tag"],
                selection.Children.Select(WorkViewElement.GetId).Where(id => id is "media.selection.queue" or "media.selection.tag"));
        }
        finally { page.EndLifetime(); }
    });

    [Fact]
    public void TheTileShowsTheQueueMarkAndFollowsAQueueChangeWithoutARebuild()
    {
        MediaLibraryEntry queued = MediaTagCountsTests.Entry(1);
        bool inQueue = true;
        MediaLibraryTileView tile = new() { InTagQueue = _ => inQueue };
        tile.DataContext = new MediaLibraryTileRow(new MediaLibraryTile(queued, [queued]), 0);
        Assert.True(tile.Badges.State.Queued && tile.Badges.Options.ShowBooru);
        Assert.Contains(Texts.T("media.badge.queued"), AutomationProperties.GetName(tile));
        inQueue = false;
        tile.RefreshQueueBadge();
        Assert.False(tile.Badges.State.Queued);
        Assert.DoesNotContain(Texts.T("media.badge.queued"), AutomationProperties.GetName(tile));
        Assert.Null(tile.Badges.State.TagCount); // no own state read: no count
        Assert.Equal("3 own tags", Texts.Count("media.tile.ownTags", 3, "3"));
        Assert.Equal("1 own tag", Texts.Count("media.tile.ownTags", 1, "1"));
        // The page touches only the realized tiles when the queue changes, and re-presents only for the queue term.
        string queue = Read("Desktop/Views/Media/MediaPage.Queue.cs");
        Assert.Contains("foreach (Control container in _grid.GetRealizedContainers())", queue);
        Assert.Contains("if (HasField(MediaFilterField.Queue) && _snapshot != null) Present(keepView: true);", queue);
        Assert.Contains("_session.StateChanged += OnMediaTagQueueChanged;", Read("Desktop/Shell/MainWindow.Media.cs"));
    }

    [Fact]
    public void AddingToTheQueueFromTheMediaPageOffersToGoThroughItNow()
    {
        string host = Read("Desktop/Shell/MainWindow.Media.cs");
        Assert.Contains("AddToTagQueue = AddMediaToTagQueueAsync,", host);
        Assert.Contains("if (added > 0 && !_isClosing)", host);
        Assert.Contains("new[] { new FeedbackToastAction(Texts.T(\"media.queue.walkNow\"), () => OpenTagQueueGalleryAsync(groupId)) }", host);
        Assert.Contains("OpenTaggingPage = ShowTaggingPage,", host);
        Assert.Contains("OpenTagQueueWalk = () => OpenTagQueueGalleryAsync(),", host);
    }
}
