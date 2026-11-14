using System.Runtime.CompilerServices;
using Avalonia.Automation;
using MeowChan.Desktop;
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
        Assert.Contains("if (input.PageTerms is { } pageTerms) passing = pageTerms.Apply(passing, input.Queue, input.SourceTags?.Invoke());", page);
    }

    [Fact]
    public void TheWorkViewsPutTheSwitchUnderMoreInBarebonesAndTheWaysToTaggingAlwaysThere()
    {
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct],
            new[] { WorkView.Barebones, WorkView.Simple, WorkView.Advanced }.Select(view => WorkViewPolicy.Resolve("media.toolbar.tag-list", view)));
        foreach (WorkView view in Enum.GetValues<WorkView>())
        {
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("media.toolbar.queue-walk", view));
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("media.toolbar.tagging-page", view));
        }
        // The queue walk and the Tagging page stand side by side under More.
        Assert.Equal(2, WorkViewPolicy.Get("media.toolbar.tagging-page").OverflowOrder - WorkViewPolicy.Get("media.toolbar.queue-walk").OverflowOrder);
        // MP2: the queue before tagging together, in the bar from Simple on and under the bar's More.
        Assert.True(WorkViewPolicy.Get("media.selection.queue").OverflowOrder < WorkViewPolicy.Get("media.selection.tag").OverflowOrder);
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("media.selection.queue", WorkView.Simple));
        Assert.Contains("{ _selectionText, _selQueue, _selTag, _selGallery, _selExport, _selMore, _selClear }", Read("Desktop/Views/Media/MediaPage.Selection.cs"));
        // A switch under More keeps its state as a checked entry.
        Assert.Contains("if (control is ToggleButton check)", Read("Desktop/Views/Media/MediaPage.WorkView.cs"));
    }

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
        Assert.Contains("if (HasField(MediaFilterField.Queue) && _snapshot != null) Present();", queue);
        Assert.Contains("_session.StateChanged += OnMediaTagQueueChanged;", Read("Desktop/Shell/MainWindow.Media.cs"));
    }

    [Fact]
    public void AddingToTheQueueFromTheMediaPageOffersToGoThroughItNow()
    {
        string host = Read("Desktop/Shell/MainWindow.Media.cs");
        Assert.Contains("AddToTagQueue = AddMediaToTagQueueAsync,", host);
        Assert.Contains("if (added > 0 && !_isClosing)", host);
        Assert.Contains("new FeedbackToastAction(Texts.T(\"media.queue.walkNow\"), OpenTagQueueGalleryAsync)", host);
        Assert.Contains("OpenTaggingPage = ShowTaggingPage,", host);
        Assert.Contains("OpenTagQueueWalk = OpenTagQueueGalleryAsync,", host);
    }
}
