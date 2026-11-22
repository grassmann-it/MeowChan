using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// R8: the media sidebar of the large reader. Off by default; the View-menu switch shows it beside the posts in the same frame,
// the selected post's medium follows ↑/↓ (throttled), the inline enlargement steps back while it is shown, a narrow reader hides
// it, Escape inside it closes only it, the right side mirrors it, and closing the reader puts the pane back into the archive split.
internal static class ReaderMediaSidebarChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        window.Width = 1600; window.Height = 960;
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Archive);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader UX";
        await page.ApplySearchAsync();
        page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread != null);
        // The large reader snapshots embedded media; do not detach before its load completes.
        await page.MediaLoadTask;
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        int embeddedColumn = Grid.GetColumn(pane);
        Require(page.ReaderMedia.Sidebar, "The media sidebar is not on by default.");
        Require(page.ReaderViewOptionIds.Contains("reader.sidebar.toggle"), "The reader has no media sidebar switch.");

        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => host.IsVisible && !page.IsBusy);
        await page.MediaLoadTask;
        Require(host.Content is Grid { Name: "ReaderLargeFrame" } && pane.Parent == host.Content, "The large reader is not framed for the sidebar.");
        // On by default: the large reader shows the sidebar at once. It is switched off once for the plain-pane checks and back on below.
        await Program.WaitUntilAsync(() => page.IsMediaSidebarShown && page.MediaSidebar!.IsEffectivelyVisible);
        page.ToggleMediaSidebar();
        await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
        Require(!page.IsMediaSidebarShown && KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Cycle,
            "Without the sidebar the pane lost its own Tab cycle.");

        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        // R11/RL1: while the sidebar is off and the reader wide enough, its row in the settings popover names the shortcut (and nothing turned it on).
        await ReaderSettingsProbe.CheckGearRoutesAsync(page, window);
        QuickSettingsSurface settings = await ReaderSettingsProbe.OpenAsync(page);
        string? shortcut = MeowChan.Desktop.Commands.CommandCatalog.ShortcutDisplay("reader.sidebar.toggle");
        Require(shortcut == null || settings.ShortcutTextOf("reader.sidebar.toggle").Contains(shortcut, StringComparison.Ordinal),
            "The settings row of the sidebar does not show its shortcut while the sidebar is off: '" + settings.ShortcutTextOf("reader.sidebar.toggle") + "'.");
        await ReaderSettingsProbe.CloseAsync(page);
        Require(!page.ReaderMedia.Sidebar && !page.IsMediaSidebarShown, "The discovery hint turned the sidebar on.");
        try
        {
            page.ToggleMediaSidebar();
            await Program.WaitUntilAsync(() => page.IsMediaSidebarShown && page.MediaSidebar!.IsEffectivelyVisible);
            ReaderMediaSidebar sidebar = page.MediaSidebar!;
            Grid frame = (Grid)host.Content!;
            Require(page.FocusedMediaPresentedElsewhere, "The inline enlargement did not step back for the sidebar.");
            Require(KeyboardNavigation.GetTabNavigation(frame) == KeyboardNavigationMode.Cycle
                && KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Continue, "Tab does not reach the sidebar.");
            Require(Grid.GetColumn(sidebar) == 0 && Grid.GetColumn(pane) == 2, "The default side is not left.");
            await CheckFullHeightAsync(sidebar, frame);
            using (MediaNativeInput native = new(window))
            {
                await native.MoveAsync(sidebar.Frame);
                await Program.WaitUntilAsync(() => sidebar.Close.Opacity == 1 && sidebar.Close.IsHitTestVisible);
                await native.MoveAsync(posts);
                await Program.WaitUntilAsync(() => sidebar.Close.Opacity == 0 && !sidebar.Close.IsHitTestVisible);
            }
            // The column takes its width in the next layout pass after the sidebar is shown.
            await Program.WaitUntilAsync(() => Math.Abs(frame.ColumnDefinitions[0].ActualWidth - page.ReaderMedia.SidebarWidth) <= 1,
                () => "The sidebar does not have its configured width: column " + frame.ColumnDefinitions[0].ActualWidth + " (" + frame.ColumnDefinitions[0].Width
                    + ", min " + frame.ColumnDefinitions[0].MinWidth + ", max " + frame.ColumnDefinitions[0].MaxWidth + "), configured " + page.ReaderMedia.SidebarWidth
                    + ", frame " + frame.Bounds.Size + ", sidebar " + sidebar.Bounds.Size + ", window " + window.Bounds.Size);

            // A post with media: its picture (or an explaining placeholder) shows, decoded through the gallery cache.
            ArchivePostRow[] rows = posts.Items.OfType<ArchivePostRow>().ToArray();
            ArchivePostRow? withMedia = rows.FirstOrDefault(row => row.ShowsMedia);
            Require(withMedia != null, "The reader fixture has no post with media.");
            posts.SelectedItem = withMedia;
            await Program.WaitUntilAsync(() => page.MediaSidebarTask.IsCompleted);
            await Task.Delay(ReaderMediaSidebarRules.FollowIntervalMs * 2);
            await page.MediaSidebarTask;
            Require(sidebar.Shown != null || sidebar.StatusText.Length > 0, "The sidebar shows neither the medium nor a reason.");
            // The header is one compact row (post, file, size; the star, "Open in gallery" and the cross beside it); it replaces the old title and info box.
            Require(sidebar.HeaderLine.Contains(withMedia!.Id, StringComparison.Ordinal) && !sidebar.HeaderLine.Contains('\n', StringComparison.Ordinal),
                "The sidebar header is not one line naming the post.");
            Require(sidebar.Open.IsEffectivelyVisible && sidebar.Close.IsEffectivelyVisible, "The header lost its gallery button or its cross.");
            await Program.WaitUntilAsync(() => sidebar.LiveText.Contains(withMedia!.Id, StringComparison.Ordinal));

            // Held ↓: many selection changes, at most one decode start per interval; the sidebar ends on the last selected post.
            int start = Array.IndexOf(rows, withMedia);
            for (int index = start; index < Math.Min(rows.Length, start + 20); index++) posts.SelectedIndex = index;
            await Task.Delay(ReaderMediaSidebarRules.FollowIntervalMs * 3);
            await page.MediaSidebarTask;
            ArchivePostRow last = (ArchivePostRow)posts.SelectedItem!;
            await Program.WaitUntilAsync(() => sidebar.LiveText.Contains(last.Id, StringComparison.Ordinal)
                || sidebar.LiveText.Contains(Desktop.Localization.Texts.T("reader.sidebar.noMedia", last.Id), StringComparison.Ordinal));
            if (!last.ShowsMedia) Require(sidebar.StatusText.Length > 0, "A post without media shows no reason.");

            // R11: several files in one post: the strip shows all of them (at most the configured number), the current one is marked and
            // counted, a click or Enter on a thumbnail shows that file, a single-file post has no strip.
            ArchivePostRow? several = rows.FirstOrDefault(row => row.ShowsMedia && row.Attachments.Count > 1);
            if (several != null)
            {
                posts.SelectedItem = several;
                await Program.WaitUntilAsync(() => page.MediaSidebarTask.IsCompleted);
                await Task.Delay(ReaderMediaSidebarRules.FollowIntervalMs * 2);
                await page.MediaSidebarTask;
                await page.MediaSidebarStripTask;
                int count = several.Attachments.Count;
                int shownFiles = Math.Min(count, page.ReaderMedia.Clamped().StripLimit);
                Require(sidebar.StripVisible && sidebar.StripIndexes.Count == shownFiles, "The strip does not show the files of a post with several media.");
                Require(sidebar.StripCurrent == page.MediaSidebarIndex && sidebar.Strip.SelectedIndex >= 0, "The strip does not mark the current file.");
                // No overlay arrows and no "2 / 3" counter over the medium, also for a post with several media (the strip and Alt+Shift+Left/Right step).
                Require(!HasOverlayNavigation(sidebar), "A post with several media shows overlay arrows or a counter over the medium.");
                Require(sidebar.StripCounterText == ReaderMediaMultiRules.CounterText(page.MediaSidebarIndex, count), "The strip counter is wrong.");
                int next = Math.Min(count - 1, page.MediaSidebarIndex + 1);
                sidebar.Strip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = sidebar.Strip });
                await Program.WaitUntilAsync(() => page.MediaSidebarIndex == next);
                await page.MediaSidebarTask;
                Require(sidebar.StripCurrent == next && sidebar.StripCounterText == ReaderMediaMultiRules.CounterText(next, count),
                    "Choosing the next thumbnail did not move the mark and the counter.");
                page.SelectMediaSidebarIndex(0);
                await Program.WaitUntilAsync(() => page.MediaSidebarIndex == 0 && sidebar.StripCurrent == 0);
                // M4/N7: the grid flag of the row is what the panel reads; a row is a grid only with little text AND two tiles that fit the column.
                if (several.MediaGrid)
                    Require(ReaderMediaMultiRules.UseGrid(page.ReaderMedia, count, several.Body, several.MediaWidth) && several.MediaWidth > 0
                        && several.Attachments.All(row => row.ThumbnailWidth == ReaderMediaMultiRules.TileWidth(several.MediaWidth)),
                        "A post with several images and little text is not laid out as a grid.");
                else
                    Require(!ReaderMediaMultiRules.UseGrid(page.ReaderMedia, count, several.Body) || several.MediaWidth == 0 || !ReaderMediaMultiRules.TilesFit(several.MediaWidth),
                        "A post that fits the grid rules must be a grid.");
            }
            ArchivePostRow? single = rows.FirstOrDefault(row => row.ShowsMedia && row.Attachments.Count == 1);
            if (single != null)
            {
                posts.SelectedItem = single;
                await Program.WaitUntilAsync(() => page.MediaSidebarTask.IsCompleted);
                await Task.Delay(ReaderMediaSidebarRules.FollowIntervalMs * 2);
                await page.MediaSidebarTask;
                Require(!sidebar.StripVisible && sidebar.StripCounterText.Length == 0, "A post with one file shows a strip.");
                Require(!HasOverlayNavigation(sidebar), "A post with one file shows overlay arrows or a counter.");
            }

            // Right side.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarRight = true });
            await Flush();
            Require(Grid.GetColumn(sidebar) == 2 && Grid.GetColumn(pane) == 0, "The sidebar did not move to the right.");
            await CheckFullHeightAsync(sidebar, frame);
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarRight = false });

            ReaderMediaPreferences widthPreferences = page.ReaderMedia;
            page.ApplyReaderMediaPreferences(page.ReaderMedia with
            {
                SidebarWidth = ReaderDisplayPreferences.MediaSidebarWidthMaximum,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum
            });
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            ToggleButton toggle = page.FindControl<ToggleButton>("ReaderMediaSidebarToggle")!;
            Require(!toggle.IsEnabled && Ursa.Controls.ToolBar.GetOverflowMode(toggle) == Ursa.Controls.OverflowMode.Always,
                "The unavailable narrow-width sidebar toggle stayed active in the direct toolbar.");
            string widthHint = Texts.T("reader.sidebar.collapsed", Math.Ceiling(ReaderMediaSidebarRules.MinimumReaderWidth(page.ReaderMedia)));
            Require(ToolTip.GetShowOnDisabled(toggle) && ToolTip.GetTip(toggle) is string tip && tip.Contains(widthHint, StringComparison.Ordinal),
                "The disabled sidebar toggle does not expose its minimum width on hover.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with
            {
                SidebarWidth = widthPreferences.SidebarWidth,
                SidebarMinimumReaderWidth = widthPreferences.SidebarMinimumReaderWidth
            });
            await Program.WaitUntilAsync(() => page.IsMediaSidebarShown);
            Require(toggle.IsEnabled && Ursa.Controls.ToolBar.GetOverflowMode(toggle) == Ursa.Controls.OverflowMode.AsNeeded,
                "The sidebar toggle did not return to the direct toolbar when enough width became available.");

            // Escape inside the sidebar only takes the focus back to the posts: the sidebar stays on (its choice is stored, only the × and the
            // switch change it) and the reader stays open.
            sidebar.Open.Focus();
            await Flush();
            KeyEventArgs escape = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = sidebar.Open };
            sidebar.Open.RaiseEvent(escape);
            await Flush();
            Require(escape.Handled && host.IsVisible && page.ReaderMedia.Sidebar && page.IsMediaSidebarShown,
                "Escape in the sidebar closed the reader or switched the sidebar off.");
            Require(!sidebar.IsKeyboardFocusWithin && pane.IsKeyboardFocusWithin, "Escape in the sidebar did not take the focus back to the posts.");
            // RD-01: the sidebar stays shown, so pane and sidebar keep their one shared Tab cycle (the frame cycles, the pane continues into it).
            Require(KeyboardNavigation.GetTabNavigation(frame) == KeyboardNavigationMode.Cycle
                && KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Continue, "After Escape in the sidebar Tab no longer reaches the sidebar.");
            // Only switching the sidebar off gives the pane its own Tab cycle back.
            page.ToggleMediaSidebar();
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            Require(KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Cycle, "The pane did not get its Tab cycle back when the sidebar was switched off.");
        }
        finally
        {
            if (page.ReaderMedia.Sidebar) page.ToggleMediaSidebar();
        }
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !host.IsVisible);
        Require(pane.Parent == page.FindControl<Grid>("ArchiveSplit") && Grid.GetColumn(pane) == embeddedColumn,
            "The pane did not return to its archive split column.");
        await CheckArchiveViewAsync(page, pane, posts);
        CheckDisabledGalleryTagsTooltip(window);
        await ReaderSettingsProbe.CheckDisposalAsync(window);
        Console.WriteLine("SMOKE reader media sidebar: on by default, compact header with the gallery button, no overlay arrows or counter (strip for several media), framed large reader, View-menu switch, follow with throttle, live region, several-file strip with counter, grid layout, discovery hint, right side, narrow-width overflow, Escape and return, archive view (Always) passed.");
    }

    private static void CheckDisabledGalleryTagsTooltip(Window owner)
    {
        GallerySidePanel panel = new(new Button());
        Window fixture = new() { Content = panel, Width = 400, Height = 240, ShowInTaskbar = false };
        string reason = Texts.T("archive.notSaved");
        try
        {
            fixture.Show(owner);
            panel.SetTagsAvailable(false, reason);
            ToggleButton tags = panel.TagsButton;
            Require(tags.IsEffectivelyVisible && !tags.IsEffectivelyEnabled && ToolTip.GetShowOnDisabled(tags)
                && ToolTip.GetTip(tags) as string == reason, "The disabled Gallery Tags segment does not expose its reason on hover.");
        }
        finally { fixture.Close(); }
    }

    // "Always": the archive view shows the full-height sidebar beside the posts and their header, with the same following and width rules;
    // its switch there chooses Always or Large reader only, and a narrow reader moves the disabled toggle into overflow.
    private static async Task CheckArchiveViewAsync(ArchivePage page, Grid pane, ListBox posts)
    {
        Border content = page.FindControl<Border>("ArchiveReaderContent")!;
        ReaderMediaPreferences before = page.ReaderMedia;
        try
        {
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarWidth = ReaderDisplayPreferences.MediaSidebarWidthMinimum });
            Require(!page.IsMediaSidebarShown, "The archive view showed the sidebar while it is set to the large reader only.");
            page.ToggleMediaSidebar();
            await Program.WaitUntilAsync(() => page.IsMediaSidebarShown && page.IsMediaSidebarInArchiveView && page.MediaSidebar!.IsEffectivelyVisible);
            ReaderMediaSidebar sidebar = page.MediaSidebar!;
            Require(page.ReaderMedia.Sidebar && page.ReaderMedia.SidebarAlways, "The archive view's switch did not choose Always.");
            Require(sidebar.Parent == pane && Grid.GetRow(sidebar) == 0 && Grid.GetRowSpan(sidebar) == 2
                && Grid.GetColumn(sidebar) == (page.ReaderMedia.SidebarRight ? 2 : 0)
                && Grid.GetColumn(content) == (page.ReaderMedia.SidebarRight ? 0 : 2), "The sidebar does not span the archive reader beside its posts column.");
            foreach (bool right in new[] { false, true })
            {
                page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarRight = right });
                await CheckFullHeightAsync(sidebar, pane);
                Require(Grid.GetColumn(sidebar) == (right ? 2 : 0) && Grid.GetColumn(content) == (right ? 0 : 2),
                    "The archive sidebar did not preserve its side columns.");
            }
            Require(page.FocusedMediaPresentedElsewhere, "The inline enlargement did not step back for the archive view's sidebar.");
            if (posts.Items.OfType<ArchivePostRow>().FirstOrDefault(row => row.ShowsMedia) is { } withMedia)
            {
                posts.SelectedItem = withMedia;
                await Task.Delay(ReaderMediaSidebarRules.FollowIntervalMs * 2);
                await page.MediaSidebarTask;
                await Program.WaitUntilAsync(() => sidebar.LiveText.Contains(withMedia.Id, StringComparison.Ordinal));
            }
            page.ApplyReaderMediaPreferences(page.ReaderMedia with
            {
                SidebarWidth = ReaderDisplayPreferences.MediaSidebarWidthMaximum,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum
            });
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            ToggleButton toggle = page.FindControl<ToggleButton>("ReaderMediaSidebarToggle")!;
            Require(!toggle.IsEnabled && Ursa.Controls.ToolBar.GetOverflowMode(toggle) == Ursa.Controls.OverflowMode.Always,
                "The narrow archive-view sidebar toggle stayed active in the direct toolbar.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarWidth = ReaderDisplayPreferences.MediaSidebarWidthMinimum });
            await Program.WaitUntilAsync(() => page.IsMediaSidebarShown);
            page.ToggleMediaSidebar();
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            Require(page.ReaderMedia.Sidebar && !page.ReaderMedia.SidebarAlways, "Hiding the archive view's sidebar took it from the large reader.");
        }
        finally { page.ApplyReaderMediaPreferences(page.ReaderMedia with
        {
            SidebarWidth = before.SidebarWidth,
            SidebarMinimumReaderWidth = before.SidebarMinimumReaderWidth,
            SidebarRight = before.SidebarRight
        }); }
    }

    private static async Task CheckFullHeightAsync(ReaderMediaSidebar sidebar, Grid host)
    {
        await Program.WaitUntilAsync(() => host.Bounds.Height > 0 && sidebar.Frame.Bounds.Height > 0
            && Math.Abs(sidebar.Bounds.Height - host.Bounds.Height) <= 1);
        await Flush();
        Require(sidebar.TranslatePoint(default, host) is { Y: >= -1 and <= 1 }, "The sidebar does not start at the host's top.");
        Require(sidebar.HeaderRow.TranslatePoint(default, sidebar) is { Y: >= 7 and <= 9 }, "The sidebar header is missing its top inset.");
        Require(sidebar.HeaderRow.Bounds.Height == ReaderMediaSidebar.HeaderLineHeight, "The media title lost its stable height.");
        Require(sidebar.Padding.Top == 0 && sidebar.Padding.Bottom == 0, "Neighbour alignment reclaimed media height as padding.");
        Require(Math.Abs(sidebar.Card.TranslatePoint(default, sidebar)!.Value.Y + sidebar.Card.Bounds.Height - sidebar.Bounds.Height) <= 1,
            "There is unused height below the media card.");
        Console.WriteLine($"SMOKE sidebar full-height: host={host.Name}, side={Grid.GetColumn(sidebar)}, hostHeight={host.Bounds.Height}, paneHeight={sidebar.Bounds.Height}, mediaHeight={sidebar.Frame.Bounds.Height}");
        if (TopLevel.GetTopLevel(sidebar) is Window window)
            Program.CaptureWindow(window, $"smoke-reader-full-height-{host.Name}-{Grid.GetColumn(sidebar)}.png");
    }

    // The sidebar has no arrows and no counter over the medium (named controls of the old overlay).
    private static bool HasOverlayNavigation(ReaderMediaSidebar sidebar) => sidebar.GetVisualDescendants().OfType<Control>()
        .Any(control => control.Name is "ReaderMediaSidebarPrevious" or "ReaderMediaSidebarNext" or "ReaderMediaSidebarCounter" or "ReaderMediaSidebarCounterText");

    private static async Task Flush() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
