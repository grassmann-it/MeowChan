using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, fourth part (MEDIA103-NAV P1): the gallery way back in a real MainWindow.
// (a) Medien -> gallery ("To media" on its close action) -> show in thread (only "Back to gallery") -> back to the gallery ->
//     Escape -> the same Medien tile focused, no media return left behind.
// (b) Medien -> show in thread ("Back to media") -> gallery from the reader ("Close gallery") -> show in thread -> back to the gallery
//     -> close -> the archive reader again with "Back to media" visible and working.
// Each origin is restored once and in order: the gallery's own first, then the Medien return it had only hidden.
internal static partial class MediaPageChecks
{
    private static async Task GalleryReturnPathAsync(MainWindow owner, string profile)
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
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => mediaHost.IsVisible && mediaHost.Content is MediaPage);
            MediaPage page = (MediaPage)mediaHost.Content!;
            await page.BuildTask.WaitAsync(Deadline);
            Require(page.Tiles.Count == ReturnThreads + 2, "The return fixture must give " + (ReturnThreads + 2) + " tiles: " + page.Tiles.Count);
            ArchivePage archive = shell.ArchivePage;
            Button mediaReturn = archive.FindControl<Button>("ArchiveMediaReturn")!, galleryReturn = archive.FindControl<Button>("ArchiveGalleryReturn")!;
            ListBox posts = archive.FindControl<ListBox>("ArchivePosts")!;
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
            Button Close(GalleryViewer viewer) => viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose");
            string Shown(Button button) => string.Join(" ", button.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            async Task<GalleryViewer> GalleryAsync(string post)
            {
                await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
                GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
                await UntilAsync(() => viewer.CaptureState().PostId == post);
                return viewer;
            }
            void RequireCloseLabel(GalleryViewer viewer, string label, string context)
            {
                Button close = Close(viewer);
                string tip = ToolTip.GetTip(close)?.ToString() ?? string.Empty;
                Require(viewer.CloseLabel == label && AutomationProperties.GetName(close) == label
                    && tip == CommandBindings.Tooltip("gallery.close", visibleLabel: label) && AutomationProperties.GetHelpText(close) == tip,
                    context + ": the close action must be named '" + label + "': name '" + AutomationProperties.GetName(close) + "', tip '" + tip + "'.");
                // The same command and key: the tooltip keeps the gallery.close shortcut.
                Require(CommandCatalog.ShortcutDisplay("gallery.close") is not { } key || tip.Contains(key, StringComparison.Ordinal),
                    context + ": the close tooltip lost its shortcut: " + tip);
            }
            async Task InArchiveAsync(string thread, string post)
            {
                await UntilAsync(() => archiveHost.IsVisible && !galleryHost.IsVisible && archive.CurrentThread?.Summary.Id.Thread == thread && ReaderPost() == post);
                Require(!mediaHost.IsVisible, "The Medien page must be hidden behind the archive.");
            }
            async Task CloseToAsync(GalleryViewer viewer)
            {
                // The same gallery.close binding as for every other gallery; an open context panel takes the first Escape.
                for (int press = 0; press < 3 && galleryHost.IsVisible; press++)
                {
                    viewer.Focus();
                    Key(viewer, Avalonia.Input.Key.Escape);
                    for (int turn = 0; turn < 3; turn++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                await UntilAsync(() => !galleryHost.IsVisible);
                await shell.GalleryCleanupTask.WaitAsync(Deadline);
            }

            // (a) Medien -> gallery -> show in thread -> back to the gallery -> Escape -> the Medien tile.
            int viaGallery = IndexOf("r-612.png");
            await ShowTileAsync(page, viaGallery);
            await ClickTileAsync(page, shell, viaGallery);
            Require(Selected() == "r-612.png", "The click did not select the gallery tile.");
            page.Grid.Focus();
            Key(page.Grid, Avalonia.Input.Key.Enter);
            GalleryViewer fromMedia = await GalleryAsync("612");
            RequireCloseLabel(fromMedia, "To media", "Gallery from Medien");
            Require(Shown(Close(fromMedia)).Contains("To media", StringComparison.Ordinal), "The visible close label must say 'To media': " + Shown(Close(fromMedia)));
            // The help lists the close action under its media name, and Escape's last step leads back to media.
            GalleryViewerChecksAccess.Click(fromMedia, "GalleryHelp");
            await UntilAsync(() => fromMedia.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryHelpMore"));
            GalleryViewerChecksAccess.Click(fromMedia, "GalleryHelpMore");
            string[] help = Array.Empty<string>();
            await UntilAsync(() => (help = fromMedia.GetGalleryVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray())
                .Contains(Texts.T("viewer.help.escape.media")));
            Require(help.Contains(CommandBindings.Tooltip("gallery.close", visibleLabel: "To media")) && !help.Contains(CommandBindings.Tooltip("gallery.close"))
                && !help.Contains(Texts.T("viewer.help.escape")), "The help must name the close action 'To media': " + string.Join(" | ", help));
            GalleryViewerChecksAccess.Click(fromMedia, "GalleryHelpClose");
            // A language switch keeps the media name (in that language).
            Texts.SetLanguage("de");
            await UntilAsync(() => AutomationProperties.GetName(Close(fromMedia)) == "Zu Medien");
            RequireCloseLabel(fromMedia, "Zu Medien", "Gallery from Medien (de)");
            Texts.SetLanguage("en");
            await UntilAsync(() => AutomationProperties.GetName(Close(fromMedia)) == "To media");
            fromMedia.Focus();
            Key(fromMedia, Avalonia.Input.Key.T);
            await InArchiveAsync("612", "612");
            await UntilAsync(() => galleryReturn.IsEffectivelyVisible);
            Require(!mediaReturn.IsVisible && !shell.MediaReturnAvailableForTests, "(a) While the gallery waits, only its own return is offered.");
            // Alt+Left belongs to a Medien jump only; with the gallery waiting it must not leave for the Medien page.
            Control post = posts.ContainerFromItem(posts.SelectedItem!) as Control ?? throw new InvalidOperationException("The focused post is not realized.");
            post.Focus();
            Key(post, Avalonia.Input.Key.Left, KeyModifiers.Alt);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(archiveHost.IsVisible && !mediaHost.IsVisible && galleryReturn.IsVisible, "(a) Alt+Left must not bypass the waiting gallery.");
            galleryReturn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => galleryHost.IsVisible && !galleryReturn.IsVisible);
            Require(ReferenceEquals(galleryHost.Content, fromMedia) && fromMedia.CaptureState().PostId == "612", "(a) The same gallery must come back at the same position.");
            RequireCloseLabel(fromMedia, "To media", "Gallery from Medien after the reader");
            await CloseToAsync(fromMedia);
            await UntilAsync(() => mediaHost.IsVisible && !archiveHost.IsVisible && FocusOn(viaGallery));
            Require(Selected() == "r-612.png" && page.SelectedTiles.Count == 1, "(a) Closing the gallery must return to the same Medien tile: " + Selected());
            Require(!shell.MediaReturnAvailableForTests && !mediaReturn.IsVisible && !galleryReturn.IsVisible && !note.IsVisible,
                "(a) No stray return state may be left after closing the gallery.");
            // The tile keeps the focus: no late restore or second focus pass moves it.
            for (int turn = 0; turn < 5; turn++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(FocusOn(viaGallery) && Selected() == "r-612.png", "(a) The focus must stay on the Medien tile after closing.");

            // (b) Medien -> show in thread -> gallery from the reader -> show in thread -> back -> close -> archive with "Back to media".
            int chosen = IndexOf("r-618.png");
            await ShowTileAsync(page, chosen);
            await ClickTileAsync(page, shell, chosen);
            Require(Selected() == "r-618.png", "The click did not select the jump tile.");
            Key(Tile(chosen), Avalonia.Input.Key.T);
            await InArchiveAsync("618", "618");
            await UntilAsync(() => mediaReturn.IsEffectivelyVisible);
            Require(shell.MediaReturnAvailableForTests && !galleryReturn.IsVisible, "(b) The jump must offer only 'Back to media'.");
            archive.FindControl<Button>("ArchiveOpenGallery")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            GalleryViewer fromReader = await GalleryAsync("618");
            RequireCloseLabel(fromReader, CommandCatalog.Label("gallery.close"), "Gallery from the archive reader");
            Require(!fromReader.ClosesToMedia && !mediaReturn.IsVisible && shell.MediaReturnAvailableForTests,
                "(b) The open gallery hides the Medien return but must keep it.");
            fromReader.Focus();
            Key(fromReader, Avalonia.Input.Key.T);
            await InArchiveAsync("618", "618");
            await UntilAsync(() => galleryReturn.IsEffectivelyVisible);
            Require(!mediaReturn.IsVisible && shell.MediaReturnAvailableForTests, "(b) The reader must show only 'Back to gallery' while the gallery waits.");
            galleryReturn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => galleryHost.IsVisible && !galleryReturn.IsVisible);
            Require(ReferenceEquals(galleryHost.Content, fromReader), "(b) The same gallery must come back.");
            await CloseToAsync(fromReader);
            await UntilAsync(() => archiveHost.IsVisible && !mediaHost.IsVisible && mediaReturn.IsEffectivelyVisible);
            Require(!galleryReturn.IsVisible && shell.MediaReturnAvailableForTests && archive.CurrentThread?.Summary.Id.Thread == "618",
                "(b) Closing the gallery must leave the archive reader with 'Back to media' again.");
            mediaReturn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => mediaHost.IsVisible && !archiveHost.IsVisible);
            await shell.MediaReturnTask.WaitAsync(Deadline);
            Require(!shell.MediaReturnAvailableForTests && !mediaReturn.IsVisible, "(b) The return point must end with the return.");
            await UntilAsync(() => FocusOn(chosen));
            Require(Selected() == "r-618.png" && page.SelectedTiles.Count == 1 && !note.IsVisible, "(b) 'Back to media' must lead to the same Medien tile: " + Selected());
            await DirectGalleryOpenGuardAsync(shell, page);
            Console.WriteLine("SMOKE media gallery return [en]: real main window -> (a) Medien tile -> gallery whose close action is 'To media' (name, tooltip with Esc, visible label, help entry and Escape line; 'Zu Medien' after a switch to German) -> T -> thread with only 'Back to gallery', Alt+Left kept in the archive -> back to the same gallery -> Escape -> the same Medien tile focused and selected, no return state left, focus stable; (b) Medien tile -> T -> 'Back to media' -> gallery from the reader ('Close gallery') hides it -> T -> only 'Back to gallery' -> back -> close -> archive reader with 'Back to media' again -> the same Medien tile.");
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
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in MediaGalleryReturnChecks: " + exception));
        }
    }
}
