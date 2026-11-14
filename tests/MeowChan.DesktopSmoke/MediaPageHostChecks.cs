using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, second part: the Medien page inside a real MainWindow with its own host and profile.
// The page's host paths (edit media tags, gallery close) run through the shell's real wiring, not through stubs.
internal static partial class MediaPageChecks
{
    private static async Task HostPathsAsync(MainWindow owner, string profile)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        await SeedAsync(profile);
        DesktopSessionHost session = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(session) { Width = 1400, Height = 900 };
        Exception? failureBeforeCleanup = null;
        try
        {
            shell.Show(owner);
            await UntilAsync(() => session.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Button navigation = shell.FindControl<Button>("MediaNavigationButton")!;
            await UntilAsync(() => navigation.IsVisible && navigation.IsEffectivelyEnabled);
            navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContentControl host = shell.FindControl<ContentControl>("MediaPageHost")!;
            await UntilAsync(() => host.IsVisible && host.Content is MediaPage);
            MediaPage page = (MediaPage)host.Content!;
            Require(ReferenceEquals(page, shell.MediaPageForTests), "The shell's media page is not the page on screen.");
            await page.BuildTask.WaitAsync(Deadline);
            // By thread: B's file, A's file with a post, A's loose file.
            Require(page.ShownEntries.Count == 3, "The shell's media page must show the three saved files.");
            MediaLibraryTileRow Row(string? post, bool loose = false) => ((MediaLibraryTileRow[])page.Grid.ItemsSource!)
                .Single(row => loose ? row.Entry.Unassigned : row.Entry.PostId == post);
            // The notices of this command as the shell recorded them (the same entries the banner and the diagnostics show).
            var feedback = (MeowChan.Desktop.Feedback.SessionFeedbackStore)typeof(MainWindow).GetField("_feedback",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shell)!;
            (string Summary, int Count)[] Refusals() => feedback.Entries.Where(entry => entry.Operation == "media.tags").Select(entry => (entry.Summary, entry.Count)).ToArray();
            int RefusalCount() => Refusals().Sum(entry => entry.Count);
            string Tags(string post) => string.Join(",", Row(post).State!.MediaTags.Order(StringComparer.Ordinal));
            async Task<MediaLibraryTileView> TileViewAsync(string post)
            {
                MediaLibraryTileView? view = null;
                await UntilAsync(() => (view = page.GetVisualDescendants().OfType<MediaLibraryTileView>().FirstOrDefault(candidate =>
                    candidate.DataContext is MediaLibraryTileRow row && row.Entry.PostId == post)) != null);
                return view!;
            }
            // The focused element belongs to the grid: the grid itself or one of its tiles.
            bool FocusInGrid() => shell.FocusManager?.GetFocusedElement() is Control focused && (focused == page.Grid || focused.GetVisualAncestors().Contains(page.Grid));
            async Task OpenMenuAndEditTagsAsync()
            {
                page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                MenuItem edit = page.Grid.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == Texts.T("media.tags.edit"));
                Require(edit.IsEnabled, "Edit media tags must be offered for a bound tile.");
                // A real pointer click on the entry: it runs the command and then closes the menu like a user's click does.
                await UntilAsync(() => edit.IsEffectivelyVisible && TopLevel.GetTopLevel(edit) != null);
                TopLevel root = TopLevel.GetTopLevel(edit)!;
                Point point = new(edit.Bounds.Width / 2, edit.Bounds.Height / 2);
                using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                edit.RaiseEvent(new PointerPressedEventArgs(edit, pointer, root, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                edit.RaiseEvent(new PointerReleasedEventArgs(edit, pointer, root, point, 1,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                pointer.Capture(null);
            }

            PrivateTagCommit existing = await session.DefinePrivateTagAsync("existing").WaitAsync(Deadline);
            string tagRevision = session.GetPrivateTagSnapshot().Snapshot!.Revision.ToString();

            // 1. Edit media tags through the real dialog, host commands and refresh.
            await ClickTileAsync(page, shell, 1);
            Require(page.Grid.SelectedItem is MediaLibraryTileRow { Entry.PostId: "100" }, "The click did not select A's bound tile.");
            Require(Tags("100") == string.Empty, "The bound tile must start without media tags.");
            await OpenMenuAndEditTagsAsync();
            await UntilAsync(() => shell.MediaPageTagWindow?.IsVisible == true);
            MediaTagDialog dialog = shell.MediaPageTagWindow!;
            PrivateMediaTagTarget target = MediaLibraryFilterEngine.TagTarget(Row("100").Entry)!;
            Require(dialog.Session.Target == target, "The dialog is not bound to the tile's exact media version.");
            Require(shell.OwnedWindows.Contains(dialog), "The tag dialog is not owned by the main window.");
            dialog.Input.Focus();
            dialog.Input.Text = "from_page";
            dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await IdleAsync(dialog);
            TagToggleRow offered = await GalleryMediaTagChecks.PressSpaceAsync(dialog, "existing");
            Require(offered.State == TagToggleState.All, "Space did not add the existing tag.");
            await IdleAsync(dialog);
            Require(Row("100").State!.MediaTags.Count == 0, "The tile must not change while the dialog is still open.");
            dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => shell.MediaPageTagWindow == null);
            await UntilAsync(() => Tags("100") == "existing,from_page");
            MediaLibraryTileView tileView = await TileViewAsync("100");
            await UntilAsync(() => ToolTip.GetTip(tileView) is string tip && tip.Contains(Texts.T("media.state.mediaTags", "existing, from_page"))
                || ToolTip.GetTip(tileView) is string loose && loose.Contains("from_page"));
            PrivateTagStoreSnapshot stored = session.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.TagsOf(target).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).SequenceEqual(new[] { "existing", "from_page" })
                && stored.TagsOf(A).Count == 0, "The store must hold exactly the two media tags and no thread tag.");
            try { await UntilAsync(FocusInGrid); }
            catch (TimeoutException) { throw new InvalidOperationException("Focus after the tag dialog: " + (shell.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none") + " name=" + (shell.FocusManager?.GetFocusedElement() as Control)?.Name); }
            Require(page.Grid.SelectedItem is MediaLibraryTileRow { Entry.PostId: "100" }, "The tile selection was lost by the refresh.");

            // 2. Refusals write nothing. The page keeps its index; the files change underneath it.
            long revision = session.GetPrivateTagSnapshot().Snapshot!.Revision;
            string[] storeBefore = TagFiles(profile);
            // 2a. A's file was replaced after indexing: its old asset is gone from the manifest.
            await WriteAsync(Path.Combine(profile, "media", "a"), A, ("one-replaced.png", "m1", "100"), ("loose.png", "m9", null));
            Require(RefusalCount() == 0, "No refusal expected yet.");
            await ClickTileAsync(page, shell, 1);
            await OpenMenuAndEditTagsAsync();
            await UntilAsync(() => RefusalCount() == 1);
            Require(Refusals().Single().Summary == Texts.T("media.galleryFailed"), "A changed file is refused with the failure notice: " + Refusals().Single().Summary);
            Require(shell.FindControl<TextBlock>("ShellActionError")!.Text == Texts.T("media.galleryFailed")
                && shell.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("ShellActionErrorBanner")!.IsVisible, "The refusal is not shown in the shell banner.");
            Require(shell.MediaPageTagWindow == null && !shell.OwnedWindows.OfType<MediaTagDialog>().Any(), "A changed file must not open the tag dialog.");
            await UntilAsync(FocusInGrid);
            // 2b. B's file lost its binding to the post: still there, but unbound.
            await WriteAsync(Path.Combine(profile, "media", "b"), B, ("two.png", "x1", null));
            await ClickTileAsync(page, shell, 0);
            Require(page.Grid.SelectedItem is MediaLibraryTileRow { Entry.PostId: "200" }, "The click did not select B's tile.");
            await OpenMenuAndEditTagsAsync();
            await UntilAsync(() => RefusalCount() == 2);
            string unboundNotice = string.Join(" | ", Refusals().Select(entry => entry.Summary + " x" + entry.Count));
            Require(Refusals().All(entry => entry.Summary == Texts.T("media.galleryFailed") || entry.Summary == Texts.T("tags.media.boundElsewhere")), "Unexpected refusal text: " + unboundNotice);
            Require(shell.MediaPageTagWindow == null && !shell.OwnedWindows.OfType<MediaTagDialog>().Any(), "An unbound file must not open the tag dialog.");
            await UntilAsync(FocusInGrid);
            Require(session.GetPrivateTagSnapshot().Snapshot!.Revision == revision && Tags("200") == string.Empty && RefusalCount() == 2
                && TagFiles(profile).SequenceEqual(storeBefore), "A refusal must write nothing to the tag store.");
            Console.WriteLine("SMOKE media page host [en]: refusal notices recorded: " + unboundNotice + ".");

            // 3. Gallery from the page: change favorite and rating there, close, the page re-reads its own state, focus is on the grid.
            // Restore A's file so that its tile still opens in the gallery.
            await WriteAsync(Path.Combine(profile, "media", "a"), A, ("one.png", "m1", "100"), ("loose.png", "m9", null));
            await WriteAsync(Path.Combine(profile, "media", "b"), B, ("two.png", "x1", "200"));
            await ClickTileAsync(page, shell, 1);
            page.Grid.Focus();
            Require(Row("100").State!.Favorite == MediaLibraryFlag.No && Row("100").State!.UserRating == null, "A's tile must start without favorite and rating.");
            page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            await UntilAsync(() => viewer.CaptureState().PostId == "100");
            Button favorite = viewer.GetGalleryVisualDescendants().OfType<Button>().First(button => button.Name == "GalleryFavorite");
            await UntilAsync(() => favorite.IsEffectivelyEnabled);
            favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => session.GetFavorites().Count == 1);
            Button rating = viewer.GetGalleryVisualDescendants().OfType<Button>().First(button => button.Name == "GalleryRating");
            await UntilAsync(() => rating.IsEffectivelyEnabled);
            rating.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => shell.OwnedWindows.OfType<MediaRatingDialog>().Any(window => window.IsVisible));
            MediaRatingDialog ratingDialog = shell.OwnedWindows.OfType<MediaRatingDialog>().Single();
            Ursa.Controls.Rating editor = C<Ursa.Controls.Rating>(ratingDialog, "MediaRatingEditor");
            await UntilAsync(() => editor.IsEffectivelyEnabled);
            for (int guard = 0; editor.Value < 3 && guard < 8; guard++)
            {
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                await Task.Yield();
            }
            Require(editor.Value == 3, "The rating draft did not reach 3.");
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => session.GetMediaRatings().Count == 1 && session.GetMediaRatings()[0].Value == 3);
            await UntilAsync(() => editor.IsEnabled);
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => !ratingDialog.IsVisible);
            // The gallery is an overlay: until it closes, the page still shows what it read before.
            Require(Row("100").State!.Favorite == MediaLibraryFlag.No && Row("100").State!.UserRating == null,
                "The page must not change under the open gallery (the refresh belongs to the close).");

            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await UntilAsync(() => !galleryHost.IsVisible);
            await shell.GalleryCleanupTask.WaitAsync(Deadline);
            Require(Row("100").State!.Favorite == MediaLibraryFlag.Yes && Row("100").State!.UserRating == 3,
                "Closing the gallery did not refresh the page's own state: " + Row("100").State!.Favorite + "/" + Row("100").State!.UserRating);
            Require(Row("100").DetailWithState.Contains('★') && Row("100").DetailWithState.Contains("3/5"), "The tile line does not show the new favorite and rating.");
            MediaLibraryTileView after = await TileViewAsync("100");
            await UntilAsync(() => ToolTip.GetTip(after) is string tip && tip.Contains(Texts.T("media.state.rating", Texts.T("media.state.ratingValue", 3))));
            await UntilAsync(FocusInGrid);
            Require(page.Grid.SelectedItem is MediaLibraryTileRow { Entry.PostId: "100" }, "The tile selection was lost by closing the gallery.");
            Console.WriteLine("SMOKE media page host [en]: real main window -> Medien page -> tile -> Edit media tags (real dialog, two tags, tile shows them after refresh, focus on grid); changed and unbound file refused with a notice and nothing written; gallery from the page -> favorite and rating changed there -> close -> tile shows both, focus on grid.");
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
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in MediaPageHostChecks: " + exception));
        }
    }

    private static string[] TagFiles(string profile) => Directory.GetFiles(profile, "*tag*", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static async Task IdleAsync(MediaTagDialog dialog)
    {
        await UntilAsync(() => !dialog.Session.Busy);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
