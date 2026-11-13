using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Plan AP5 §1.7 (WV5): what the media page shows directly in each work view, and that it moves, never removes.</summary>
public sealed class MediaWorkViewTests
{
    private static Presence P(string id, WorkView view, bool state = false) => WorkViewPolicy.Resolve(id, view, state);

    [Fact]
    public void HeaderToolsMoveUnderMoreByViewAndTheCoreStaysDirect()
    {
        foreach (WorkView view in WorkViewSetting.All)
            foreach (string id in new[] { "media.search", "media.filter.toggle", "media.sort", "media.toolbar.more" })
                Assert.Equal(Presence.Required, P(id, view));
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct], WorkViewSetting.All.Select(view => P("media.toolbar.refresh", view)));
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct], WorkViewSetting.All.Select(view => P("media.toolbar.shuffle", view)));
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], WorkViewSetting.All.Select(view => P("media.toolbar.missing", view)));
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], WorkViewSetting.All.Select(view => P("media.toolbar.export-all", view)));
        // After AP6 the ratings manager is under More in every view; its command (and so its shortcut) is the catalog's.
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Overflow, P("media.toolbar.manage-ratings", view)));
        Assert.Equal("media.rating.manage", WorkViewPolicy.Get("media.toolbar.manage-ratings").CommandId);
    }

    [Fact]
    public void FilterFieldsGrowFromBarebonesToAdvanced()
    {
        string[] always = ["media.filter.kind", "media.filter.favorites"];
        string[] fromSimple = ["media.filter.source", "media.filter.board", "media.filter.rating", "media.filter.tags", "media.filter.tag-include", "media.filter.tag-exclude"];
        string[] fromAdvanced = ["media.filter.sound", "media.filter.without-media-tags", "media.filter.tag-mode"];
        Assert.All(always, id => Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Direct, P(id, view))));
        Assert.All(fromSimple, id => Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct], WorkViewSetting.All.Select(view => P(id, view))));
        Assert.All(fromAdvanced, id => Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], WorkViewSetting.All.Select(view => P(id, view))));
        // The held-back fields are under "More filters", never lost: nothing in the filter row is Hidden.
        Assert.DoesNotContain(WorkViewPolicy.Entries.Where(entry => entry.Id.StartsWith("media.filter.", StringComparison.Ordinal)),
            entry => WorkViewSetting.All.Any(view => entry.For(view) == Presence.Hidden));
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, P("media.filter.more", view)));
    }

    [Fact]
    public void AnActiveFilterOrSwitchedOnStateIsShownInEveryView()
    {
        string[] states = WorkViewPolicy.Entries.Where(entry => entry.Id.StartsWith("media.filter.", StringComparison.Ordinal)
            || entry.Id == "media.toolbar.missing").Select(entry => entry.Id).ToArray();
        Assert.NotEmpty(states);
        Assert.All(states, id => Assert.All(WorkViewSetting.All, view =>
            Assert.True(WorkViewPolicy.OnSurface(P(id, view, state: true)), id + " in " + view)));
    }

    [Fact]
    public void SelectionBarKeepsTheSelectionItselfAndMovesTheRest()
    {
        foreach (WorkView view in WorkViewSetting.All)
            foreach (string id in new[] { "media.selection.status", "media.selection.gallery", "media.selection.clear", "media.selection.more" })
                Assert.Equal(Presence.Required, P(id, view));
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct], WorkViewSetting.All.Select(view => P("media.selection.tag", view)));
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], WorkViewSetting.All.Select(view => P("media.selection.export", view)));
    }

    [Fact]
    public void TheUntaggedCountIsLeftOutOfBarebonesButItsFilterIsReachable()
    {
        Assert.Equal([Presence.Hidden, Presence.Direct, Presence.Direct], WorkViewSetting.All.Select(view => P("media.status.untagged", view)));
        Assert.Equal("media.filter.without-media-tags", WorkViewPolicy.Get("media.status.untagged").Alternative);
        Assert.True(WorkViewPolicy.OnSurface(P("media.status.untagged", WorkView.Barebones, state: true)));
    }

    [Fact]
    public void OverflowOrderIsUniqueWithinHeaderAndSelectionBar()
    {
        foreach (string group in new[] { "media.toolbar.", "media.selection." })
        {
            int[] orders = WorkViewPolicy.Entries.Where(entry => entry.Id.StartsWith(group, StringComparison.Ordinal)
                && WorkViewSetting.All.Any(view => entry.For(view) == Presence.Overflow)).Select(entry => entry.OverflowOrder).ToArray();
            Assert.Equal(orders.Length, orders.Distinct().Count());
            Assert.All(orders, order => Assert.True(order > 0));
        }
    }

    // The page needs the Avalonia platform to be built, so it is exercised by the work-views-media smoke; here every media row
    // must at least be registered by the page (no orphaned row), and every id the page registers must have a row (lint test).
    [Fact]
    public void EveryMediaRowIsRegisteredByThePage()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../..", "Desktop/Views/Media/MediaPage.WorkView.cs")));
        string[] unregistered = WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Media)
            .Where(entry => !source.Contains("\"" + entry.Id + "\"", StringComparison.Ordinal)).Select(entry => entry.Id).ToArray();
        Assert.Empty(unregistered);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
