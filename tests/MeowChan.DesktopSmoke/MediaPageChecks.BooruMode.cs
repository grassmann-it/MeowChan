using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// MP1-MP4 on the real page (smoke id media-page, run after the focus mode in the filter surface fixture: 20 threads 600..619 carry the
// own tag "surface", the chip "surface" is applied when this starts and is applied again when it ends).
// Booru mode: the toolbar switch shows the tag list left of the grid (256 dip, the grid keeps its row); its counts are made off the UI
// thread and equal the shown tiles; a click takes an included tag out, Alt+click excludes, Enter includes and Shift+Enter excludes from
// the keyboard; the search box narrows the rows; the order switch sorts by name; the list stays in the focus mode; every row is named
// with its count. Queue: an entry added through the host gets the tile's queue mark and the status count, and "queue:yes" as a term
// shows exactly it. Keyboard input is routed, not OS input.
internal static partial class MediaPageChecks
{
    private static async Task BooruModeAsync(Window window, MediaPage page, DesktopSessionHost host, ThemeVariant theme, string code)
    {
        string state = $"booru mode {theme}/{code}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = 1400; window.Height = 900;
        await UntilAsync(() => AutomationProperties.GetName(page.BooruModeButton) == Texts.T("media.tagList"));
        int tagged = page.Tiles.Count, all = 0;
        MediaTagSidebar sidebar = page.TagSidebar;
        Require(!page.IsBooruMode && !sidebar.IsVisible && page.BooruModeButton.IsEffectivelyVisible, state + ": the mode starts off with its switch on the toolbar.");

        // On: the list stands left of the grid surface and counts what is shown.
        page.BooruModeButton.IsChecked = true;
        await UntilAsync(() => sidebar.IsEffectivelyVisible && page.TagCounts != null && page.TagCountTask.IsCompleted);
        Control surface = C<Control>(page, "MediaGridSurface");
        double sidebarRight = sidebar.TranslatePoint(new Point(sidebar.Bounds.Width, 0), page)!.Value.X;
        Require(Math.Abs(sidebar.Bounds.Width - MediaTagSidebar.ColumnWidth) < 0.5 && sidebarRight <= surface.TranslatePoint(default, page)!.Value.X + 0.5
            && page.Grid.Bounds.Height > 100, state + ": the list must stand left of the grid: " + sidebar.Bounds + " / " + surface.Bounds);
        Require(C<TextBlock>(sidebar, "MediaTagSidebarTitle").Text == Texts.T("media.tags.title") && AutomationProperties.GetName(sidebar.List) == Texts.T("media.tags.title")
            && AutomationProperties.GetHelpText(sidebar.List) == Texts.T("media.tags.hint", PlatformKeyNames.Alt()), state + ": the list must be named and explained.");
        MediaTagListItem Surface() => sidebar.Items.Single(row => row.Kind == MediaTagListKind.Tag && row.Name == "surface");
        Require(Surface() is { Polarity: 1 } held && held.Count == tagged && page.TagCounts!.Media == tagged
            && held.AccessibleName.Contains(tagged.ToString("N0", Texts.Culture), StringComparison.Ordinal),
            state + ": the included tag must head its group with the count of the shown tiles: " + Surface());

        // A click on the included tag takes it out; Alt+click excludes it; the counts follow every change.
        int runs = page.TagCountRuns;
        Require(sidebar.Activate(Surface(), MediaTagListGesture.Include), state + ": a click on a tag must act.");
        await UntilAsync(() => page.TagCountRuns > runs && page.TagCountTask.IsCompleted && Surface().Polarity == 0);
        all = page.Tiles.Count;
        Require(all > tagged && Surface().Count == tagged && !page.FilterTerms.Any(term => term.Field == MediaFilterField.Tag), state + ": the click must take the tag out.");
        Require(sidebar.Activate(Surface(), MediaTagListGesture.Exclude), state + ": Alt+click must act.");
        await UntilAsync(() => page.TagCountTask.IsCompleted && Surface().Polarity == -1);
        Require(page.Tiles.Count == all - tagged && Surface().Count == 0 && page.FilterTerms.Single(term => term.Field == MediaFilterField.Tag).Exclude,
            state + ": Alt+click must exclude: " + page.Tiles.Count);

        // From the keyboard: Enter includes the selected tag, Shift+Enter excludes it, Delete takes it out.
        sidebar.List.SelectedItem = Surface();
        sidebar.List.Focus(NavigationMethod.Tab);
        void Key(Avalonia.Input.Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            (sidebar.List.ContainerFromIndex(sidebar.List.SelectedIndex) ?? (Control)sidebar.List).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Key(Avalonia.Input.Key.Enter);
        await UntilAsync(() => page.TagCountTask.IsCompleted && Surface().Polarity == 1 && page.Tiles.Count == tagged);
        Require((sidebar.List.SelectedItem as MediaTagListItem)?.Name == "surface", state + ": the selection must stay on the tag after the list was rebuilt.");
        Key(Avalonia.Input.Key.Enter, KeyModifiers.Shift);
        await UntilAsync(() => page.TagCountTask.IsCompleted && Surface().Polarity == -1);
        Key(Avalonia.Input.Key.Delete);
        await UntilAsync(() => page.TagCountTask.IsCompleted && Surface().Polarity == 0 && page.Tiles.Count == all);

        // The search box narrows the rows; nothing found says so.
        sidebar.Search.Text = "SURF";
        sidebar.Search.FlushQuery();
        await UntilAsync(() => sidebar.Items.Where(row => row.Kind == MediaTagListKind.Tag).Select(row => row.Name).SequenceEqual(["surface"]));
        sidebar.Search.Text = "zzz-none";
        sidebar.Search.FlushQuery();
        await UntilAsync(() => sidebar.Items.Any(row => row.Kind == MediaTagListKind.Note));
        sidebar.Search.Text = string.Empty;
        sidebar.Search.FlushQuery();
        sidebar.SortSwitch.SelectedIndex = 1;
        Require(sidebar.Sort == MediaTagListSort.Name, state + ": the order switch must sort by name.");
        sidebar.SortSwitch.SelectedIndex = 0;

        // The list stays in the focus mode (the mode hides the rows for finding, not the tag list).
        page.SetFocusMode(true);
        await UntilAsync(() => page.IsFocusMode);
        Require(sidebar.IsEffectivelyVisible && sidebar.Bounds.Height > 100, state + ": the tag list must stay in the focus mode.");
        page.SetFocusMode(false);

        // MP1: a queued tile carries the mark, the status line counts it, and "queue:yes" shows exactly it.
        page.ReadTagQueue = host.GetTagQueue;
        MediaLibraryEntry first = page.ShownEntries[0];
        if (!page.IsInTagQueue(first))
            await host.EnqueueTagQueueAsync(TagQueueActions.ItemsOf([first], DateTimeOffset.UtcNow).Items).WaitAsync(Deadline);
        page.RefreshTagQueue();
        Require(page.IsInTagQueue(first) && page.QueueOpenCount >= 1 && C<TextBlock>(page, "MediaStatus").Text!.Contains(Texts.T("media.status.queue", page.QueueOpenCount.ToString("N0", Texts.Culture)), StringComparison.Ordinal),
            state + ": the queue must reach the page's marks and status: " + C<TextBlock>(page, "MediaStatus").Text);
        MediaLibraryTileView? tile = page.Grid.GetRealizedContainers().OfType<ListBoxItem>().Select(item => item.GetVisualDescendants().OfType<MediaLibraryTileView>().FirstOrDefault())
            .FirstOrDefault(view => (view?.DataContext as MediaLibraryTileRow)?.Entry == first);
        Require(tile is { Badges.State.Queued: true } && AutomationProperties.GetName(tile).Contains(Texts.T("media.badge.queued"), StringComparison.Ordinal),
            state + ": the queued tile must show the mark and say it.");
        Require(page.AddFilterTerm(MediaFilterTerm.InQueue()) == null, state + ": the queue term must be accepted.");
        await UntilAsync(() => page.ShownEntries.Count >= 1 && page.ShownEntries.All(page.IsInTagQueue));
        Require(page.FilterTerms.Contains(MediaFilterTerm.InQueue()) && page.ShownEntries.Contains(first), state + ": queue:yes must show the queued tiles only.");
        page.ResetFilters();
        await UntilAsync(() => page.Tiles.Count == all);

        // Off: the list goes, the grid takes the width again; the chip "surface" is back for the checks after this one.
        page.BooruModeButton.IsChecked = false;
        await UntilAsync(() => !sidebar.IsVisible);
        Require(page.AddTagFilterByName("surface", include: true) == null, state + ": the tag filter could not be restored.");
        await UntilAsync(() => page.Tiles.Count == tagged);
    }
}
