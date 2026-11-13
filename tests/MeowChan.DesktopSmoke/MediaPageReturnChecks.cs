using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, third part (MEDIA103-NAV P0): Medien -> "show in thread" -> archive -> back to the same Medien view,
// in a real MainWindow: T and the context menu, the return button and Alt+Left, a duplicate's occurrence, a tile that is gone,
// a file without a post, the gallery's own way back, text input keeping Alt+Left, and a jump overtaken by leaving the archive.
internal static partial class MediaPageChecks
{
    private const int ReturnThreads = 30;

    private static async Task ReturnPathAsync(MainWindow owner, string profile)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        await SeedReturnAsync(profile);
        DesktopSessionHost session = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(session) { Width = 1400, Height = 900 };
        Exception? failureBeforeCleanup = null;
        try
        {
            shell.Show(owner);
            await UntilAsync(() => session.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Button mediaNavigation = shell.FindControl<Button>("MediaNavigationButton")!;
            await UntilAsync(() => mediaNavigation.IsVisible && mediaNavigation.IsEffectivelyEnabled);
            mediaNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContentControl mediaHost = shell.FindControl<ContentControl>("MediaPageHost")!;
            ContentControl archiveHost = shell.FindControl<ContentControl>("ArchivePageHost")!;
            await UntilAsync(() => mediaHost.IsVisible && mediaHost.Content is MediaPage);
            MediaPage page = (MediaPage)mediaHost.Content!;
            await page.BuildTask.WaitAsync(Deadline);
            Require(page.Tiles.Count == ReturnThreads + 2, "The return fixture must give " + (ReturnThreads + 2) + " tiles: " + page.Tiles.Count);
            ArchivePage archive = shell.ArchivePage;
            Button mediaReturn = archive.FindControl<Button>("ArchiveMediaReturn")!, galleryReturn = archive.FindControl<Button>("ArchiveGalleryReturn")!;
            ListBox posts = archive.FindControl<ListBox>("ArchivePosts")!;
            MediaTilePanel panel = page.Grid.GetVisualDescendants().OfType<MediaTilePanel>().Single();
            ComboBox sort = C<ComboBox>(page, "MediaSort");
            TextBox name = C<TextBox>(page, "MediaNameFilter");
            CheckBox favoritesOnly = C<CheckBox>(page, "MediaFavoritesOnly");
            Button reset = C<Button>(page, "MediaFilterReset");
            TextBlock note = C<TextBlock>(page, "MediaSelectionNote");
            shell.Activate();
            await UntilAsync(() => shell.IsActive);

            int IndexOf(string file) => page.Tiles.Select((tile, index) => (tile, index)).Single(pair => pair.tile.Occurrences.Any(entry => entry.Name == file)).index;
            string? Selected() => page.Grid.SelectedItem is MediaLibraryTileRow row ? row.Entry.Name : null;
            bool FocusOn(int index) => shell.FocusManager?.GetFocusedElement() is Control focused
                && page.Grid.ContainerFromIndex(index) is Control container && (focused == container || focused.GetVisualAncestors().Contains(container));
            string? ReaderPost() => (posts.SelectedItem as ArchivePostRow)?.Id;
            void Key(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
                target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            Control Tile(int index) => page.Grid.ContainerFromIndex(index) as Control ?? throw new InvalidOperationException("Tile " + index + " is not realized.");
            async Task InArchiveAsync(string thread, string post)
            {
                await UntilAsync(() => archiveHost.IsVisible && archive.CurrentThread?.Summary.Id.Thread == thread && ReaderPost() == post);
                Require(!mediaHost.IsVisible, "The Medien page must be hidden behind the archive.");
            }
            async Task BackOnMediaAsync()
            {
                await UntilAsync(() => mediaHost.IsVisible && !archiveHost.IsVisible);
                await shell.MediaReturnTask.WaitAsync(Deadline);
                Require(!shell.MediaReturnAvailableForTests && !mediaReturn.IsVisible, "The return point must end with the return.");
            }

            // 1. T from a scrolled, sorted and filtered view; Alt+Left from a post brings back the same view.
            sort.SelectedIndex = 1; // newest first
            name.Text = "r-";
            await UntilAsync(() => page.Tiles.Count == ReturnThreads);
            panel.RestoreAnchor(panel.Columns * 2, -40);
            await UntilAsync(() => panel.CaptureAnchor() is { Index: > 0 });
            (int Index, double Offset) anchor = panel.CaptureAnchor()!.Value;
            int chosen = anchor.Index + 1;
            string chosenName = page.Tiles[chosen].Entry.Name;
            await ClickTileAsync(page, shell, chosen);
            Require(Selected() == chosenName, "The click did not select " + chosenName);
            anchor = panel.CaptureAnchor()!.Value; // a click can nudge a partly visible row into view
            MediaLibraryEntry target = page.Tiles[chosen].Entry;
            Key(Tile(chosen), Avalonia.Input.Key.T);
            await InArchiveAsync(target.Thread.Thread!, target.PostId!);
            await UntilAsync(() => mediaReturn.IsEffectivelyVisible);
            Require(!galleryReturn.IsVisible && shell.MediaReturnAvailableForTests, "Only the Medien return belongs to this jump.");
            Require(mediaReturn.Content?.ToString()?.Contains(Texts.T("command.media.return")) == true
                || Avalonia.Automation.AutomationProperties.GetName(mediaReturn) == Texts.T("command.media.return")
                || ToolTip.GetTip(mediaReturn)?.ToString()?.Contains(Texts.T("command.media.return")) == true, "The return button does not say where it goes.");
            // Text input keeps Alt+Left (word/caret movement), the archive stays.
            TextBox search = archive.FindControl<TextBox>("ArchiveSearchText")!;
            search.Focus();
            Key(search, Avalonia.Input.Key.Left, KeyModifiers.Alt);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(archiveHost.IsVisible && shell.MediaReturnAvailableForTests, "Alt+Left in a text field must not leave the archive.");
            // The hidden page loses its position (as a re-presented grid can); only the return point can bring it back.
            ScrollViewer gridScroll = page.Grid.GetVisualDescendants().OfType<ScrollViewer>().First();
            gridScroll.Offset = default;
            Control post = posts.ContainerFromItem(posts.SelectedItem!) as Control ?? throw new InvalidOperationException("The focused post is not realized.");
            post.Focus();
            Key(post, Avalonia.Input.Key.Left, KeyModifiers.Alt);
            await BackOnMediaAsync();
            Require(sort.SelectedIndex == 1 && name.Text == "r-" && page.Tiles.Count == ReturnThreads, "Order and filter must be unchanged after the return.");
            await UntilAsync(() => FocusOn(chosen));
            Require(Selected() == chosenName && page.SelectedTiles.Count == 1, "The opened tile must be selected again: " + Selected());
            (int Index, double Offset) back = panel.CaptureAnchor()!.Value;
            Require(back.Index == anchor.Index && Math.Abs(back.Offset - anchor.Offset) < 1.5, "The view must stand where it stood: " + anchor + " -> " + back);
            Require(!note.IsVisible, "No note when the tile is still there.");

            // 1b. A library above the background threshold presents off the UI thread. A presentation still running when the page
            // comes back replaces the rows just restored, so the return must wait for it, then restore selection, focus and view.
            page.BackgroundPresentEntries = 0;
            TaskCompletionSource releasePresent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int heldRuns = 0;
            page.PresentGate = (_, _) => { Interlocked.Increment(ref heldRuns); return releasePresent.Task; };
            try
            {
                anchor = panel.CaptureAnchor()!.Value;
                Key(Tile(chosen), Avalonia.Input.Key.T);
                await InArchiveAsync(target.Thread.Thread!, target.PostId!);
                await UntilAsync(() => mediaReturn.IsEffectivelyVisible);
                gridScroll.Offset = default;
                Control heldPost = posts.ContainerFromItem(posts.SelectedItem!) as Control ?? throw new InvalidOperationException("The focused post is not realized.");
                heldPost.Focus();
                Key(heldPost, Avalonia.Input.Key.Left, KeyModifiers.Alt);
                await UntilAsync(() => mediaHost.IsVisible && !archiveHost.IsVisible && Volatile.Read(ref heldRuns) > 0);
                Require(!page.PresentTask.IsCompleted, "The gated presentation must still be running.");
                await Task.Delay(300);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(!shell.MediaReturnTask.IsCompleted, "The return must wait for the presentation that is still running.");
                releasePresent.SetResult();
                await BackOnMediaAsync();
                await page.PresentTask.WaitAsync(Deadline);
                await UntilAsync(() => FocusOn(chosen));
                Require(Selected() == chosenName && page.SelectedTiles.Count == 1, "The opened tile must be selected after a background presentation: " + Selected());
                back = panel.CaptureAnchor()!.Value;
                Require(back.Index == anchor.Index && Math.Abs(back.Offset - anchor.Offset) < 1.5,
                    "The view must stand where it stood after a background presentation: " + anchor + " -> " + back);
            }
            finally
            {
                releasePresent.TrySetResult();
                page.PresentGate = null;
                page.BackgroundPresentEntries = 2000;
                await page.PresentTask.WaitAsync(Deadline);
            }

            // 2. A duplicate: the occurrences list opens the second thread; the button brings back the one tile of those bytes.
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            sort.SelectedIndex = 0;
            await UntilAsync(() => page.Tiles.Count == ReturnThreads + 2);
            int group = IndexOf("dup-701.png");
            Require(page.Tiles[group].IsGroup && page.Tiles[group].Occurrences.Count == 2, "The duplicate fixture must be one tile with two occurrences.");
            await ShowTileAsync(page, group);
            await ClickTileAsync(page, shell, group);
            page.ShowOccurrences(page.Tiles[group]);
            await UntilAsync(() => page.OccurrencesWindow?.IsVisible == true);
            Window occurrences = page.OccurrencesWindow!;
            await UntilAsync(() => occurrences.GetVisualDescendants().OfType<StackPanel>().Count(row => row.Name == "MediaOccurrence") == 2);
            StackPanel secondRow = occurrences.GetVisualDescendants().OfType<StackPanel>().Single(row => row.Name == "MediaOccurrence"
                && row.Children.OfType<TextBlock>().Any(text => text.Name == "MediaOccurrenceFile" && text.Text?.StartsWith("dup-701.png", StringComparison.Ordinal) == true));
            Button second = secondRow.Children.OfType<Button>().Single(button => button.Name == "MediaOccurrenceShow");
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await InArchiveAsync("701", "701");
            await UntilAsync(() => page.OccurrencesWindow == null);
            await UntilAsync(() => mediaReturn.IsEffectivelyVisible);
            mediaReturn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BackOnMediaAsync();
            await UntilAsync(() => FocusOn(IndexOf("dup-701.png")));
            Require(page.SelectedTiles.Single().IsGroup && page.SelectedTiles.Single().Occurrences.Count == 2,
                "The return must select the one tile of those bytes, with both occurrences kept.");

            // 3. The tile is gone on return (its favorite was removed in the archive): the note says so, the filter stays, nothing else is selected.
            MediaLibraryEntry liked = page.Tiles[IndexOf("r-605.png")].Entry;
            FavoriteEntry favorite = new(MediaLibraryFilterEngine.AssetKey(liked)!, liked.Length ?? 0, liked.Name);
            Require(await session.SetFavoriteAsync(favorite, true).WaitAsync(Deadline), "The favorite was not saved.");
            page.RefreshUserData();
            favoritesOnly.IsChecked = true;
            await UntilAsync(() => page.Tiles.Count == 1);
            await ShowTileAsync(page, 0);
            await ClickTileAsync(page, shell, 0);
            page.Grid.ContextMenu = null;
            page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            MenuItem showInThread = page.Grid.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == Texts.T("media.showInThread"));
            Require(showInThread.IsEnabled && showInThread.InputGesture?.Key == Avalonia.Input.Key.T, "The menu must offer the jump with its T shortcut.");
            page.Grid.ContextMenu.Close();
            Key(Tile(0), Avalonia.Input.Key.T);
            await InArchiveAsync(liked.Thread.Thread!, liked.PostId!);
            Require(await session.SetFavoriteAsync(favorite, false).WaitAsync(Deadline), "The favorite was not removed.");
            await UntilAsync(() => mediaReturn.IsEffectivelyVisible);
            Control focusedPost = posts.ContainerFromItem(posts.SelectedItem!) as Control ?? throw new InvalidOperationException("The focused post is not realized.");
            focusedPost.Focus();
            Key(focusedPost, Avalonia.Input.Key.Left, KeyModifiers.Alt);
            await BackOnMediaAsync();
            await UntilAsync(() => note.IsVisible);
            Require(note.Text == Texts.T("media.return.missing", liked.Name), "The note must name the gone tile: " + note.Text);
            Require(favoritesOnly.IsChecked == true && page.Tiles.Count == 0 && page.SelectedTiles.Count == 0,
                "The filter must stay and no other tile may stand in for the gone one.");

            // 4. A file without a post: T and the menu do nothing.
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => page.Tiles.Count == ReturnThreads + 2);
            int loose = IndexOf("loose-r.png");
            await ShowTileAsync(page, loose);
            await ClickTileAsync(page, shell, loose);
            Require(page.Tiles[loose].Entry.PostId == null, "The loose fixture must have no post.");
            Key(Tile(loose), Avalonia.Input.Key.T);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(mediaHost.IsVisible && !shell.MediaReturnAvailableForTests, "A file without a post must not jump anywhere.");
            page.Grid.ContextMenu = null;
            page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            Require(!page.Grid.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == Texts.T("media.showInThread")).IsEnabled,
                "The menu must not offer a jump for a file without a post.");
            page.Grid.ContextMenu.Close();

            // 5. The gallery owns its way back: T in the gallery, back to the gallery, close it, the Medien tile has the focus.
            int viaGallery = IndexOf("r-610.png");
            await ShowTileAsync(page, viaGallery);
            await ClickTileAsync(page, shell, viaGallery);
            Require(Selected() == "r-610.png", "The click did not select the gallery tile.");
            page.Grid.Focus();
            Key(page.Grid, Avalonia.Input.Key.Enter);
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            await UntilAsync(() => viewer.CaptureState().PostId == "610");
            viewer.Focus();
            Key(viewer, Avalonia.Input.Key.T);
            await InArchiveAsync("610", "610");
            await UntilAsync(() => galleryReturn.IsEffectivelyVisible);
            Require(!mediaReturn.IsVisible && !shell.MediaReturnAvailableForTests, "While the gallery waits, only its own return is offered.");
            galleryReturn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => galleryHost.IsVisible);
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await UntilAsync(() => !galleryHost.IsVisible);
            await shell.GalleryCleanupTask.WaitAsync(Deadline);
            await UntilAsync(() => mediaHost.IsVisible && FocusOn(viaGallery));
            Require(Selected() == "r-610.png", "Closing the gallery must return to the Medien tile.");

            // 6. Leaving the archive while the jump still loads ends it: no late focus, no return point.
            int late = IndexOf("r-615.png");
            await ShowTileAsync(page, late);
            await ClickTileAsync(page, shell, late);
            Task jump = page.ShowSelectedInThreadAsync();
            Require(shell.MediaReturnAvailableForTests, "The jump must record its return point at once.");
            shell.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await jump.WaitAsync(Deadline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(!archiveHost.IsVisible && !mediaHost.IsVisible && !shell.MediaReturnAvailableForTests,
                "A jump overtaken by leaving the archive must not come back or keep a return point.");
            Console.WriteLine("SMOKE media return [en]: real main window -> Medien sorted newest, name filter, scrolled -> T on a tile -> exact thread and post -> 'Back to media' shown, gallery return hidden -> Alt+Left kept by the archive search field -> Alt+Left on the post -> same order, filter, selection, focus and view (anchor " + anchor + " -> " + back + "); duplicate: occurrences list opens the second thread -> button -> the one byte tile selected; gone tile (favorite removed in the archive) -> note, filter kept, nothing selected; file without post: T and menu do nothing; gallery: T -> thread -> back to gallery -> close -> Medien tile focused; leaving the archive during a jump ends it.");
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                foreach (Window child in shell.OwnedWindows.Where(window => window.IsVisible).ToArray()) child.Close();
                shell.Close();
                await UntilAsync(() => !shell.IsVisible);
                await shell.GalleryCleanupTask;
                owner.Activate();
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in MediaPageReturnChecks: " + exception));
        }
    }

    // Scrolls the tile's row to the top through the panel's own offset and waits until a pointer would reach the tile.
    // RestoreAnchor puts the row at the top; ListBox.ScrollIntoView only scrolls as far as needed (after a refill it works too, see MediaTileRefillChecks).
    private static async Task ShowTileAsync(MediaPage page, int index)
    {
        MediaTilePanel panel = page.Grid.GetVisualDescendants().OfType<MediaTilePanel>().Single();
        Window window = (Window)TopLevel.GetTopLevel(page)!;
        await UntilAsync(() =>
        {
            panel.RestoreAnchor(index - index % Math.Max(1, panel.Columns), 0);
            page.Grid.UpdateLayout();
            ListBoxItem? item = page.GetVisualDescendants().OfType<ListBoxItem>()
                .FirstOrDefault(candidate => candidate.DataContext is MediaLibraryTileRow row && row.Index == index && candidate.IsArrangeValid);
            return item?.TranslatePoint(new Point(item.Bounds.Width / 2, 55), window) is { } at
                && window.InputHitTest(at) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(item);
        });
    }

    // Thirty threads with one bound file each (r-600 ... r-629), a loose file in 600, and the same bytes in 700 and 701.
    private static async Task SeedReturnAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        async Task ThreadAsync(string id, int hour, params (string Name, string MediaId, string? Post)[] files)
        {
            string directory = Path.Combine(profile, "media", id);
            ResourceId resource = new("media-fixture", "g", id);
            CapturedAttachment bound = await WriteAsync(directory, resource, files);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(resource, "https://offline.invalid/" + id, "Return " + id,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hour), directory,
                [new CapturedPost(id, 0, PostFieldPresence.All, Html: "op " + id, Attachments: [bound]),
                 new CapturedPost(id + "1", 1, PostFieldPresence.All, Html: "reply " + id)]));
        }
        for (int index = 0; index < ReturnThreads; index++)
        {
            string id = (600 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (index == 0) await ThreadAsync(id, index, ("r-" + id + ".png", "r" + id, id), ("loose-r.png", "rloose", null));
            else await ThreadAsync(id, index, ("r-" + id + ".png", "r" + id, id));
        }
        await ThreadAsync("700", ReturnThreads, ("dup-700.png", "d700", "700"));
        await ThreadAsync("701", ReturnThreads + 1, ("dup-701.png", "d701", "701"));
    }
}
