using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// MP5 on the real page (smoke id media-page, run after the Booru/tag list checks in the filter surface fixture; the chip "surface" is applied when
// this starts and when it ends). The Layout menu composes the page: the tag list left or right with its drag handle and width, the rows above
// the grid, the caption parts and the size of the tiles, three presets and Reset. Applying a layout never presents the grid again (PresentCount),
// starts no tag count while the list is off (TagCountRuns), keeps the realized tiles' previews and survives the focus mode. Compile-checked only
// when written (no GUI smokes at that time); the central run decides.
internal static partial class MediaPageChecks
{
    private static async Task CheckNarrowMediaToolbarAsync(Window window, MediaPage page)
    {
        MediaPageLayout saved = page.PageLayout;
        double width = window.Width;
        Exception? failureBeforeCleanup = null;
        page.ApplyLayout(saved with { TagList = true, TagListSide = MediaTagListSide.Left }, save: false);
        try
        {
            window.Width = 832;
            await UntilAsync(() => Math.Abs(page.Bounds.Width - 832) < 0.5);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Control more = C<Control>(page, "MediaMore");
            Control row = page.ToolbarRow;
            double moreLeft = more.TranslatePoint(default, page)!.Value.X;
            double moreRight = moreLeft + more.Bounds.Width;
            double moreInRow = more.TranslatePoint(new Point(more.Bounds.Width, 0), row)!.Value.X;
            Require(more.IsEffectivelyVisible && more.Bounds.Width > 0 && moreLeft >= 0 && moreRight <= page.Bounds.Width + 0.5
                && moreInRow <= row.Bounds.Width + 0.5 && row.Bounds.Width >= MediaToolbarLayout.NarrowestRowWidth - 0.5,
                $"At 832 DIP More must fit inside the toolbar and the page (row {row.Bounds.Width}, More right {moreRight}).");
            Require(!page.TagSidebar.IsEffectivelyVisible && !page.IsPanelShown && page.PageLayout.TagListWidth == saved.TagListWidth,
                "Narrowing must fold the sidebars and preserve the list preference.");
            Console.WriteLine($"SMOKE media More at 832 DIP: toolbar {row.Bounds.Width:0.##}, More right {moreRight:0.##}.");
            Program.CaptureWindow(window, "smoke-media-more-832.png");
        }
        catch (Exception failure)
        {
            failureBeforeCleanup = failure;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                window.Width = width;
                page.ApplyLayout(saved, save: false);
                await UntilAsync(() => Math.Abs(page.Bounds.Width - width) < 0.5);
            }, failure => Console.Error.WriteLine("SMOKE narrow Media toolbar cleanup: " + failure));
        }
    }

    private static async Task LayoutAsync(Window window, MediaPage page, ThemeVariant theme, string code)
    {
        string state = $"layout {theme}/{code}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = 1400; window.Height = 900;
        await UntilAsync(() => AutomationProperties.GetName(page.LayoutButton) == Texts.T("media.layout.name")
            && AutomationProperties.GetName(page.BooruModeButton)?.StartsWith(Texts.T("media.tagList") + ": ", StringComparison.Ordinal) == true
            && AutomationProperties.GetName(page.PanelToggleButton)?.StartsWith(Texts.T("command.media.panel.toggle") + ": ", StringComparison.Ordinal) == true);
        MediaTagSidebar sidebar = page.TagSidebar;
        Control surface = C<Control>(page, "MediaGridSurface");
        Control chipRow = C<Control>(page, "MediaFilterChipRow"), statusRow = (Control)C<TextBlock>(page, "MediaStatus").Parent!;
        TextBlock subtitle = page.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == Texts.T("media.subtitle"));
        List<MediaPageLayout> saved = new();
        page.SaveLayoutAsync = layout => { saved.Add(layout); return Task.CompletedTask; };
        Require(page.PageLayout == MediaPageLayout.Standard && page.LayoutButton.IsEffectivelyVisible && page.PanelToggleButton.IsEffectivelyVisible
            && page.PanelToggleButton.IsChecked == true && !page.IsBooruMode && page.TileAppearance == MediaTileMetrics.Standard,
            state + ": the page must start with the standard layout and its Layout button on the toolbar.");
        // The standard layout has the media panel on (right of the grid); on this wide page it is on screen, and every step below keeps it.
        await UntilAsync(() => page.IsPanelShown && page.Panel is { IsEffectivelyVisible: true });
        Control panel = page.Panel!;
        double X(Control control) => control.TranslatePoint(default, page)!.Value.X;
        Require(X(panel) > X(surface) && Math.Abs(panel.Bounds.Width - page.PageLayout.PanelWidth) < 0.5,
            state + ": the standard page must show the media panel right of the grid at its width: " + panel.Bounds + " / " + surface.Bounds);
        Require(X(page.LayoutButton) < X(page.PanelToggleButton) && ToolTip.GetTip(page.PanelToggleButton) is string panelTip
            && panelTip.StartsWith(Texts.T("command.media.panel.toggle"), StringComparison.Ordinal),
            state + ": the media-sidebar switch must sit directly to the right of Layout and explain itself: " + page.LayoutButton.Bounds + " / " + page.PanelToggleButton.Bounds);
        Require(ToolTip.GetTip(page.LayoutButton) as string == Texts.T("media.layout.tip") && page.LayoutButton.Flyout == page.LayoutFlyout, state + ": the button must explain itself and open a menu.");
        int presents = page.PresentCount, counts = page.TagCountRuns, tiles = page.Tiles.Count;
        MediaLibraryTileView[] Realized() => page.Grid.GetVisualDescendants().OfType<MediaLibraryTileView>().ToArray();
        Require(Realized().Length > 0 && Realized().All(tile => tile.Appearance == MediaTileMetrics.Standard && tile.Height == MediaLibraryTileView.TileHeight), state + ": the tiles start standard.");

        // The menu opens from the button (C15: GL3's LayoutMenuPanel): the rows as the model says, the matching preset checked, no tag list.
        page.LayoutButton.Flyout!.ShowAt(page.LayoutButton);
        await UntilAsync(() => page.LayoutFlyout.IsOpen && page.LayoutMenuRows.Buttons.Count == MediaLayoutMenu.Rows(page.PageLayout).Interactive.Count());
        Require(page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.TagListId) is ToggleButton { IsChecked: false } && page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.PresetStandardId) is ToggleButton { IsChecked: true }
            && page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.ResetId) is { } reset && AutomationProperties.GetName(reset) == Texts.T("media.layout.reset"),
            state + ": the menu must show the standard preset, an off tag list and Reset layout.");
        // A switch keeps the menu open and shows its new state at once; Escape closes it and the focus returns to the button.
        page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.SubtitleId)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !page.PageLayout.Subtitle && page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.SubtitleId) is ToggleButton { IsChecked: false });
        Require(page.LayoutFlyout.IsOpen, state + ": a switch must keep the menu open.");
        page.ApplyLayout(MediaPageLayout.Standard);
        page.LayoutMenuRows.Refresh();
        page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.StatusId)!.Focus(Avalonia.Input.NavigationMethod.Tab);
        page.LayoutMenuRows.ButtonOf(MediaLayoutMenu.StatusId)!.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        await UntilAsync(() => !page.LayoutFlyout.IsOpen);
        await UntilAsync(() => page.LayoutButton.IsKeyboardFocusWithin);
        saved.Clear();

        // Booru view: tag list on the left (256 dip, with its handle), subtitle off, small tiles with the tag count, status line and chips on.
        page.ApplyLayout(MediaPageLayout.For(MediaLayoutPreset.BooruView, page.PageLayout));
        await UntilAsync(() => sidebar.IsEffectivelyVisible && page.TagCounts != null && page.TagCountTask.IsCompleted);
        double listLeft = sidebar.TranslatePoint(default, page)!.Value.X, gridLeft = surface.TranslatePoint(default, page)!.Value.X;
        Require(page.IsBooruMode && Math.Abs(sidebar.Bounds.Width - 256) < 0.5 && listLeft < gridLeft && page.TagListHandle.IsEffectivelyVisible && !subtitle.IsVisible
            && chipRow.IsVisible && statusRow.IsVisible, state + ": the Booru view must show the list left of the grid, the handle, no subtitle, the chips and the status line.");
        Require(page.TileAppearance == MediaTileMetrics.For(MediaTileSize.Small, MediaTileCaption.TagCount | MediaTileCaption.QueueMark)
            && Realized().All(tile => tile.Appearance == page.TileAppearance && Math.Abs(tile.Height - page.TileAppearance.CellHeight) < 0.5),
            state + ": the realized tiles must be small with the tag count only.");
        Require(page.PresentCount == presents && page.Tiles.Count == tiles, state + ": a layout must not present the grid again.");
        Require(saved.Count == 1 && saved[0].TagList && saved[0].TileSize == MediaTileSize.Small, state + ": the layout must be saved once.");
        Require(page.IsPanelShown && X(panel) > X(surface), state + ": the Booru view keeps the media panel right of the grid (the list on the left).");
        TilePanelHeightMatches(page, state);

        // With both sidebars on the right, the tag list stands outermost. At its maximum width the media panel must give space to the toolbar.
        page.ApplyLayout(page.PageLayout with { TagListSide = MediaTagListSide.Right, TagListWidth = 400 });
        await UntilAsync(() => sidebar.TranslatePoint(default, page)!.Value.X > surface.TranslatePoint(default, page)!.Value.X);
        Require(Math.Abs(sidebar.Bounds.Width - 400) < 0.5, state + ": the list must be 400 dip wide on the right: " + sidebar.Bounds);
        Require(page.IsPanelShown && X(surface) < X(panel) && X(panel) < X(sidebar),
            state + ": with both on the right the order must be grid, panel, tag list: " + X(surface) + " / " + X(panel) + " / " + X(sidebar));
        int preferredPanelWidth = page.PageLayout.PanelWidth;
        page.ApplyLayout(page.PageLayout with { TagListWidth = int.MaxValue });
        await UntilAsync(() => sidebar.Bounds.Width > 400);
        Require(Math.Abs(sidebar.Bounds.Width - Config.MediaTagListWidthMaximum) < 0.5 && page.PageLayout.TagListWidth == Config.MediaTagListWidthMaximum,
            state + ": the width must stop at its maximum: " + sidebar.Bounds.Width);
        MediaPanelColumns columns = MediaPanelColumns.Of(page.PageLayout, page.Bounds.Width, page.PanelMinimumPageWidth);
        Require(page.IsPanelShown && Math.Abs(panel.Bounds.Width - columns.PanelWidth) < 0.5 && panel.Bounds.Width < preferredPanelWidth
            && page.ToolbarRow.Bounds.Width >= MediaToolbarLayout.NarrowestRowWidth - 0.5 && page.PageLayout.PanelWidth == preferredPanelWidth,
            state + ": the widest list must leave room for the toolbar and preserve the panel's preferred width: " + panel.Bounds.Width);
        window.Width = 1520;
        await UntilAsync(() => Math.Abs(panel.Bounds.Width - preferredPanelWidth) < 0.5);
        Require(page.IsPanelShown && page.PageLayout.PanelWidth == preferredPanelWidth,
            state + ": widening must restore the preferred media-panel width.");

        window.Width = 1400;
        page.ApplyLayout(page.PageLayout with { TagListWidth = 256, TagListSide = MediaTagListSide.Left });
        await UntilAsync(() => Math.Abs(window.ClientSize.Width - 1400) < 1 && sidebar.IsEffectivelyVisible && page.IsPanelShown);

        // The focus mode keeps working: the rows go, the list and the tiles stay as chosen, and the rows come back as chosen.
        page.ApplyLayout(page.PageLayout with { Subtitle = true, StatusLine = false });
        await UntilAsync(() => subtitle.IsVisible && !statusRow.IsVisible);
        page.SetFocusMode(true);
        await UntilAsync(() => page.IsFocusMode);
        Require(!subtitle.IsVisible && !chipRow.IsEffectivelyVisible && sidebar.IsEffectivelyVisible && page.TileAppearance.Caption == (MediaTileCaption.TagCount | MediaTileCaption.QueueMark),
            state + ": the focus mode must hide the rows and keep the list and the tiles.");
        page.SetFocusMode(false);
        await UntilAsync(() => !page.IsFocusMode && subtitle.IsVisible);
        Require(!statusRow.IsVisible && chipRow.IsVisible, state + ": the rows must come back as the layout chose.");

        // Grid + media sidebar: nothing but the toolbar and the tiles in the main column; the selected medium stays in the sidebar.
        page.ApplyLayout(MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, page.PageLayout with { Panel = false }));
        await UntilAsync(() => !sidebar.IsVisible);
        int stopped = page.TagCountRuns;
        Require(!page.IsBooruMode && !chipRow.IsVisible && !statusRow.IsVisible && !subtitle.IsVisible && !page.TagListHandle.IsVisible && page.TileAppearance.Caption == MediaTileCaption.None
            && Realized().All(tile => tile.Badges.State.TagCount is null or 0 && !tile.Badges.State.Queued), state + ": grid + sidebar must show the toolbar and the tiles.");
        Require(page.PageLayout.Panel && page.IsPanelShown, state + ": the Grid + media sidebar preset must enable the media panel.");
        Require(page.Grid.Bounds.Height > 100 && page.PresentCount == presents, state + ": the grid keeps its room and is not presented again.");
        page.ApplyLayout(page.PageLayout with { TileSize = MediaTileSize.Large });
        await UntilAsync(() => page.TileAppearance.CellWidth == MediaTileMetrics.LargeCellWidth);
        Require(page.TagCountRuns == stopped && page.PresentCount == presents, state + ": no count may run while the list is off, and a size is no presentation.");
        TilePanelHeightMatches(page, state);

        // Reset: today's page again, the default width included, and the tag list switch of the toolbar follows.
        page.ApplyLayout(MediaPageLayout.Reset);
        await UntilAsync(() => page.TileAppearance == MediaTileMetrics.Standard && chipRow.IsVisible && statusRow.IsVisible && subtitle.IsVisible);
        Require(page.PageLayout == MediaPageLayout.Standard && !page.BooruModeButton.IsChecked.GetValueOrDefault() && page.TagListHandle.IsVisible == false
            && page.PresentCount == presents && page.TagCountRuns == counts + 1, state + ": Reset must give the standard page without presenting (one count only for the Booru view's list).");
    }

    // The panel's cell height follows the tile size: the realized tiles keep inside their cell.
    private static void TilePanelHeightMatches(MediaPage page, string state)
    {
        MediaTilePanel panel = page.Grid.GetVisualDescendants().OfType<MediaTilePanel>().Single();
        Require(Math.Abs(panel.ItemHeight - page.TileAppearance.CellHeight) < 0.5 && Math.Abs(panel.ColumnWidth - page.TileAppearance.CellWidth) < 0.5,
            state + ": the panel's cell must follow the tile size: " + panel.ItemHeight + "x" + panel.ColumnWidth);
    }
}
