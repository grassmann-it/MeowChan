using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// R8: the media sidebar of the large reader. Off by default; the View-menu switch shows it beside the posts in the same frame,
// the selected post's medium follows ↑/↓ (throttled), the inline enlargement steps back while it is shown, a narrow reader hides
// it, Escape inside it closes only it, the right side mirrors it, and closing the reader puts the pane back into the archive split.
internal static class ReaderMediaSidebarChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        window.Width = 1600; window.Height = 960;
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader UX";
        await page.ApplySearchAsync();
        page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread != null);
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        int embeddedColumn = Grid.GetColumn(pane);
        Require(!page.ReaderMedia.Sidebar, "The media sidebar is not off by default.");
        Require(page.ReaderViewOptionIds.Contains("reader.sidebar.toggle"), "The View menu has no media sidebar switch.");

        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => host.IsVisible && !page.IsBusy);
        await page.MediaLoadTask;
        Require(host.Content is Grid { Name: "ReaderLargeFrame" } && pane.Parent == host.Content, "The large reader is not framed for the sidebar.");
        Require(!page.IsMediaSidebarShown && KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Cycle,
            "Without the sidebar the pane lost its own Tab cycle.");

        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
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

            // Right side.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarRight = true });
            await Flush();
            Require(Grid.GetColumn(sidebar) == 2 && Grid.GetColumn(pane) == 0, "The sidebar did not move to the right.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarRight = false });

            // A reader narrower than the setting hides the sidebar; it stays chosen and returns.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarMinimumReaderWidth = 3840 });
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            Require(page.ReaderMedia.Sidebar && !page.FocusedMediaPresentedElsewhere && sidebar.Shown == null,
                "A collapsed sidebar kept its picture or the focus enlargement stayed off.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthDefault });
            await Program.WaitUntilAsync(() => page.IsMediaSidebarShown);

            // Escape inside the sidebar closes only the sidebar, the reader stays and the focus is back in the posts.
            sidebar.Open.Focus();
            await Flush();
            KeyEventArgs escape = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = sidebar.Open };
            sidebar.Open.RaiseEvent(escape);
            await Program.WaitUntilAsync(() => !page.IsMediaSidebarShown);
            Require(host.IsVisible && !page.ReaderMedia.Sidebar, "Escape in the sidebar closed the reader or kept the sidebar on.");
            Require(KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Cycle, "The pane did not get its Tab cycle back.");
        }
        finally
        {
            if (page.ReaderMedia.Sidebar) page.ToggleMediaSidebar();
        }
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !host.IsVisible);
        Require(pane.Parent == page.FindControl<Grid>("ArchiveSplit") && Grid.GetColumn(pane) == embeddedColumn,
            "The pane did not return to its archive split column.");
        Console.WriteLine("SMOKE reader media sidebar: off by default, framed large reader, View-menu switch, follow with throttle, live region, right side, width collapse, Escape and return passed.");
    }

    private static async Task Flush() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
