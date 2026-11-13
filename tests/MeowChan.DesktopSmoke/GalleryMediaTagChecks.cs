using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY. Real main window gallery, action wiring,
// host and tag store with a synthetic bound and an unbound media file. Managed input only.
internal static class GalleryMediaTagChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Resource = new("media-tag-fixture", "g", "42");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    internal static async Task RunAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "gallery-media-tag-fixture");
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            GalleryEntry bound = await BoundAsync(Path.Combine(root, "bound"));
            GalleryEntry unbound = await UnboundAsync(Path.Combine(root, "unbound"));
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _)
                ?? throw new InvalidOperationException("Fixture entry did not resolve to a tag target.");
            await window.SessionHost.DefinePrivateTagAsync("existing");

            await window.OpenMediaGalleryAsync([bound, unbound], bound.EntryKey);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => host.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            Button tags = viewer.TagsButton;
            await UntilAsync(() => tags.IsEffectivelyEnabled);
            Require(Equals(tags.Content, Texts.T("tags.media.button")), "Initial tags button label is wrong: " + tags.Content);

            tags.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => window.GalleryMediaTagWindow?.IsVisible == true);
            MediaTagDialog dialog = window.GalleryMediaTagWindow!;
            Require(dialog.Session.Target == target, "Dialog is not bound to the shown media version.");
            Require(Rows(dialog).SequenceEqual(new[] { "#Other", "existing-" }), "Existing tags are not offered: " + string.Join("|", Rows(dialog)));
            Require(dialog.Surface.Summary.Text == Texts.T("tags.dialog.summary", 0, 1), "Empty summary missing: " + dialog.Surface.Summary.Text);
            Require(dialog.Input.IsKeyboardFocusWithin, "The tag field must have the focus when the dialog opens.");

            // New tag by Enter, then an existing tag by Space on its list row.
            dialog.Input.Focus();
            dialog.Input.Text = "my_media";
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            dialog.Input.RaiseEvent(enter);
            Require(enter.Handled, "Enter was not handled by the tag field.");
            await IdleAsync(dialog);
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "my_media+", "#Other", "existing-" }), "New tag not on top: " + string.Join("|", Rows(dialog)));
            object shownRows = dialog.TagList.ItemsSource!;
            TagToggleRow existingRow = await PressSpaceAsync(dialog, "existing");
            Require(existingRow.State == TagToggleState.All && ReferenceEquals(dialog.TagList.ItemsSource, shownRows),
                "Space must toggle the row in place, without laying the list out again.");
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "my_media+", "#Other", "existing+" }), "Assigned tags are wrong: " + string.Join("|", Rows(dialog)));
            Require(string.IsNullOrEmpty(dialog.Input.Text) && dialog.Surface.Notice.Text == Texts.T("tags.dialog.added", "existing")
                && dialog.UndoButton.IsVisible, "Field, notice or undo did not update.");
            Program.CaptureWindow(dialog, "smoke-gallery-media-tags.png");

            // An invalid name keeps the draft and changes nothing.
            long revision = window.SessionHost.GetPrivateTagSnapshot().Snapshot!.Revision;
            dialog.Input.Text = "bad\tname";
            dialog.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(dialog);
            Require(dialog.Status.Text == Texts.T("tags.error.invalidName") && dialog.Input.Text == "bad\tname"
                && window.SessionHost.GetPrivateTagSnapshot().Snapshot!.Revision == revision, "Invalid name was not refused cleanly.");
            dialog.Input.Text = string.Empty;
            // A new filter (here: back to none) lays the sections out again, so "existing" is now on top.
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "existing+", "my_media+" }), "Clearing the draft did not lay the list out again: " + string.Join("|", Rows(dialog)));

            // Removing is the same toggle; one undo puts it back, and the row keeps its place.
            await PressSpaceAsync(dialog, "existing");
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "existing-", "my_media+" })
                && dialog.Surface.Notice.Text == Texts.T("tags.dialog.removed", "existing"), "Removing did not untick the row: " + string.Join("|", Rows(dialog)));
            dialog.UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(dialog);
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "existing+", "my_media+" }) && !dialog.UndoButton.IsVisible
                && dialog.Surface.Notice.Text == Texts.T("tags.dialog.undone"), "Undo did not restore the removed tag once.");
            Require(window.SessionHost.GetPrivateTagSnapshot().Snapshot!.TagsOf(target).Count == 2, "Undo was not written.");
            Require(dialog.TagList.IsKeyboardFocusWithin && dialog.TagList.Current?.Label == "existing", "Focus must return to the undone row.");
            await PressSpaceAsync(dialog, "existing");
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "existing-", "my_media+" }), "Removing again failed.");

            // T4: typing offers the existing tag; the first Escape closes only that list, the next one the dialog.
            dialog.Input.Focus();
            dialog.Input.Text = "XIST";
            await UntilAsync(() => dialog.Input.IsDropDownOpen);
            Require(dialog.Input.ItemsSource!.OfType<IdentitySuggestion>().Select(item => item.Value).SequenceEqual(new[] { "existing", "XIST" }),
                "The existing tag was not suggested, or an assigned tag was suggested again.");
            Require(Rows(dialog).SequenceEqual(new[] { "#Matching", "existing-" }), "Typing must filter the list: " + string.Join("|", Rows(dialog)));
            var listEscape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.Input.RaiseEvent(listEscape);
            Require(listEscape.Handled && !dialog.Input.IsDropDownOpen && window.GalleryMediaTagWindow == dialog, "Escape must close the suggestion list first.");
            dialog.Input.Text = string.Empty;
            Require(Rows(dialog).SequenceEqual(new[] { "#Assigned", "my_media+", "#Other", "existing-" }), "Clearing the field must show every tag again.");

            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.Input.RaiseEvent(escape);
            await UntilAsync(() => window.GalleryMediaTagWindow == null);
            Require(escape.Handled && Equals(tags.Content, Texts.T("tags.media.buttonCount", 1)), "Closing did not update the gallery button: " + tags.Content);

            PrivateTagStoreSnapshot stored = window.SessionHost.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "my_media" }) && stored.TagsOf(Resource).Count == 0,
                "Store does not hold exactly one media tag and no thread tag.");

            // The next entry has no bound post version and was not offered as a loose file by the gallery selection
            // (a synthetic entry): no tagging, with an explanation. Real loose files are covered by local-media-tags.
            GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
            await UntilAsync(() => !tags.IsEffectivelyEnabled && ToolTip.GetTip(tags) as string == Texts.T("tags.media.boundElsewhere"));
            Require(Equals(tags.Content, Texts.T("tags.media.button")), "Unbound entry shows another medium's tags.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryPrevious");
            await UntilAsync(() => tags.IsEffectivelyEnabled && Equals(tags.Content, Texts.T("tags.media.buttonCount", 1)));

            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await window.GalleryCleanupTask.WaitAsync(Deadline);
            foreach (int count in new[] { 1_024, 20_000 }) await LargeVocabularyAsync(window, target, count);
            await ArchiveJourneyAsync(window, Path.Combine(root, "archive-profile"));
            Console.WriteLine("SMOKE gallery media tags [en]: bound target, new and existing tag, invalid name, remove, Escape, button count, unbound entry checked.");
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Where(child => child.IsVisible).ToArray()) owned.Close();
            if (window.FindControl<ContentControl>("GalleryHost")?.Content is GalleryViewer open)
                GalleryViewerChecksAccess.Click(open, "GalleryClose");
            await window.GalleryCleanupTask;
            Texts.SetLanguage(language);
        }
    }

    // Same dialog through the real archive menu and MainWindow wiring, without
    // opening a gallery. Check inline images and both saved-media layouts.
    private static async Task ArchiveJourneyAsync(MainWindow owner, string profile)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false,
            MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        GalleryEntry bound = await BoundAsync(Path.Combine(profile, "bound"));
        GalleryEntry loose = await UnboundAsync(Path.Combine(profile, "loose"));
        ResourceId looseThread = new(Resource.Source, Resource.Board, "99");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
        {
            await catalog.CaptureAsync(new CapturedThreadSnapshot(Resource, "https://offline.invalid/42", "Bound media",
                ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, bound.Manifest.Directory, [bound.Post!]));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(looseThread, "https://offline.invalid/99", "Loose media",
                ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, loose.Manifest.Directory,
                [new CapturedPost("99", 0, PostFieldPresence.All, Html: "No bound attachment.")]));
        }
        DesktopSessionHost session = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(session) { Width = 1300, Height = 900 };
        Exception? failureBeforeCleanup = null;
        try
        {
            shell.Show(owner);
            await UntilAsync(() => session.State == DesktopSessionState.Ready
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            MenuItem archive = shell.FindControl<MenuItem>("ArchiveMenuItem")!;
            archive.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ArchivePage page = shell.ArchivePage;
            await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Archive && !page.IsBusy);
            await page.LoadThreadAsync(Resource).WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline);
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _)!;
            PrivateTagCommit threadTag = await session.DefinePrivateTagAsync("thread_only");
            await session.SetThreadTagAsync(threadTag.Tag, Resource, true);

            async Task<MediaTagDialog> OpenAsync(Control origin)
            {
                origin.Focus();
                origin.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                await UntilAsync(() => page.ArchiveMediaMenu?.IsOpen == true);
                MenuItem edit = page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
                    .Single(item => item.Tag as string == "media.tags.edit");
                Require(edit.IsEnabled && edit.Command!.CanExecute(null), "Bound archive image has no enabled media-tag action.");
                Require(Equals(edit.Header, Texts.T("media.tags.edit")), "Media-tag action is not localized.");
                // Execute the actual menu command, then retire its popup as a native click does.
                edit.Command!.Execute(null);
                page.ArchiveMediaMenu.Close();
                await UntilAsync(() => shell.GalleryMediaTagWindow?.IsVisible == true);
                MediaTagDialog dialog = shell.GalleryMediaTagWindow!;
                Require(dialog.Session.Target == target && shell.OwnedWindows.Contains(dialog),
                    "Archive tag dialog is not owned by the shell or targets the wrong media version.");
                Require(!shell.FindControl<ContentControl>("GalleryHost")!.IsVisible,
                    "Tagging an archive image unexpectedly opened the gallery.");
                return dialog;
            }
            async Task CloseAsync(MediaTagDialog dialog)
            {
                dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await UntilAsync(() => shell.GalleryMediaTagWindow == null);
            }

            ArchiveAttachmentView? inline = null;
            await UntilAsync(() => (inline = page.FindControl<ListBox>("ArchivePosts")!.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .FirstOrDefault(view => view.IsEffectivelyVisible)) != null);
            await ArchiveMediaCommandChecks.RunAsync(page, inline!);
            MediaTagDialog first = await OpenAsync(inline!);
            first.Input.Text = "archive_image";
            first.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(first);
            Require(first.Session.Assigned.Select(tag => tag.Name.Display).SequenceEqual(new[] { "archive_image" }),
                "Inline image tag was not assigned independently of the thread's tag.");
            Program.CaptureWindow(first, "smoke-archive-media-tags.png");
            await CloseAsync(first);
            PrivateTagStoreSnapshot stored = session.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "archive_image" })
                && stored.TagsOf(Resource).Select(tag => tag.Name.Display).SequenceEqual(new[] { "thread_only" }),
                "Archive image tagging changed thread tags or tagged the wrong file.");

            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            foreach (bool tiles in new[] { false, true })
            {
                page.MediaTilesPreference = tiles;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Control list = tiles ? page.FindControl<ListBox>("ArchiveMediaTiles")! : page.FindControl<DataGrid>("ArchiveMediaGrid")!;
                if (list is ListBox tileList) tileList.SelectedIndex = 0;
                else ((DataGrid)list).SelectedIndex = 0;
                MediaTagDialog reopened = await OpenAsync(list);
                Require(reopened.Session.Assigned.Select(tag => tag.Name.Display).SequenceEqual(new[] { "archive_image" }),
                    "Saved-media layout did not reopen the exact image's tags.");
                await CloseAsync(reopened);
            }
            await page.LoadThreadAsync(looseThread).WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline);
            ListBox looseTiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            looseTiles.SelectedIndex = 0;
            looseTiles.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await UntilAsync(() => page.ArchiveMediaMenu?.IsOpen == true);
            // AP-01b replaces "unbound file disabled": the loose file is tagged as its own local occurrence.
            MenuItem looseEdit = page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
                .Single(item => item.Tag as string == "media.tags.edit");
            Require(looseEdit.IsEnabled && looseEdit.Command!.CanExecute(null), "A loose archive file must offer media tags as a local file.");
            looseEdit.Command!.Execute(null);
            page.ArchiveMediaMenu.Close();
            await UntilAsync(() => shell.GalleryMediaTagWindow?.IsVisible == true);
            MediaTagDialog looseDialog = shell.GalleryMediaTagWindow!;
            Require(looseDialog.Session.IsLocal && looseDialog.Session.Target == null, "The loose file must get a local, not a bound or guessed target.");
            looseDialog.Input.Text = "loose_archive";
            looseDialog.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(looseDialog);
            await CloseAsync(looseDialog);
            PrivateTagStoreSnapshot afterLoose = session.GetPrivateTagSnapshot().Snapshot!;
            Require(afterLoose.LocalAssignments.Count == 1 && afterLoose.TagsOf(looseThread).Count == 0
                && afterLoose.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "archive_image" }),
                "Tagging the loose file changed thread or bound tags, or did not store one local assignment.");
            Console.WriteLine("SMOKE archive media tags [en]: inline image -> real tag dialog -> host/store; thread tags unchanged; table and tile menus reopen the same media tags; loose file tagged as a local occurrence; no gallery opened.");
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
                shell.Close();
                await UntilAsync(() => !shell.IsVisible);
                await shell.GalleryCleanupTask.WaitAsync(Deadline);
                owner.Activate();
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in GalleryMediaTagChecks: " + exception));
        }
    }

    /// <summary>
    /// C5 / performance hot spot #1: the real dialog over a synthetic vocabulary of <paramref name="count"/> tags (1,024 is the
    /// store's cap today; 20,000 is the planned configurable maximum, so it is fed from memory). Opening, one toggle and one
    /// filter must stay far below the former seconds-long freeze, and only a screenful of rows may be realized.
    /// </summary>
    private static async Task LargeVocabularyAsync(MainWindow owner, PrivateMediaTagTarget target, int count)
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, count).Select(index =>
        {
            Require(PrivateTagName.TryCreate("tag_" + index.ToString("D5"), out PrivateTagName? name, out _) && name != null, "Bad synthetic name.");
            return new PrivateTagDefinition(new TagId(Guid.NewGuid()), name!);
        }).ToArray();
        // Three tags on the file, twelve used by threads (they fill "recently and often used").
        PrivateThreadTagAssignment[] threads = tags.Skip(100).Take(12).Select((tag, at) => new PrivateThreadTagAssignment(tag.Id, new ResourceId("synthetic", "g", (at + 1).ToString()))).ToArray();
        PrivateMediaTagAssignment[] media = tags.Skip(count / 2).Take(3).Select(tag => new PrivateMediaTagAssignment(tag.Id, target)).ToArray();
        PrivateTagStoreSnapshot current = new(1, "synthetic", tags, threads, media);
        int writes = 0;
        Task<PrivateTagCommit> Set(TagId tag, MediaTagSubject subject, bool assigned, CancellationToken token)
        {
            writes++;
            IEnumerable<PrivateMediaTagAssignment> next = current.MediaAssignments.Where(item => (TagId)item.TagId != tag);
            if (assigned) next = next.Append(new PrivateMediaTagAssignment(tag, target));
            current = new PrivateTagStoreSnapshot(current.Revision + 1, "synthetic", tags, threads, next);
            return Task.FromResult(new PrivateTagCommit(true, tag, current));
        }
        MediaTagSession session = new(new MediaTagSubject.Bound(target),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, current, null),
            (_, _, _) => throw new InvalidOperationException("The synthetic vocabulary takes no new names."), Set);

        System.Diagnostics.Stopwatch open = System.Diagnostics.Stopwatch.StartNew();
        MediaTagDialog dialog = new(session, "synthetic-" + count + ".png");
        try
        {
            dialog.Show(owner);
            await UntilAsync(() => dialog.IsVisible && dialog.TagList.GetRealizedContainers().Any());
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            open.Stop();
            int realized = dialog.TagList.GetRealizedContainers().Count();
            Require(dialog.TagList.Rows.Count == count + 3, "Every tag must be listed once under three labels: " + dialog.TagList.Rows.Count);
            Require(realized < 200, "The list must virtualize, but realized " + realized + " rows.");
            Require(dialog.TagList.ItemsPanelRoot is VirtualizingStackPanel, "The list panel is not virtualizing.");
            Require(Rows(dialog).Take(5).SequenceEqual(new[] { "#Assigned", Name(count / 2) + "+", Name(count / 2 + 1) + "+", Name(count / 2 + 2) + "+", "#Frequent" }),
                "The assigned tags are not on top: " + string.Join("|", Rows(dialog).Take(5)));

            object shown = dialog.TagList.ItemsSource!;
            System.Diagnostics.Stopwatch toggle = System.Diagnostics.Stopwatch.StartNew();
            TagToggleRow last = await PressSpaceAsync(dialog, Name(count - 1));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            toggle.Stop();
            Require(last.State == TagToggleState.All && writes == 1 && ReferenceEquals(dialog.TagList.ItemsSource, shown), "The toggle was not one in-place write.");

            System.Diagnostics.Stopwatch filter = System.Diagnostics.Stopwatch.StartNew();
            dialog.Input.Text = "_0001";
            dialog.Surface.FlushFilter();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            filter.Stop();
            int matches = tags.Count(tag => tag.Name.Display.Contains("_0001", StringComparison.Ordinal));
            Require(dialog.TagList.Rows.Count(row => !row.IsHeader) == matches, "The filter did not narrow the list to " + matches + " tags.");
            dialog.Input.Text = string.Empty;
            dialog.Surface.FlushFilter();
            System.Diagnostics.Stopwatch sort = System.Diagnostics.Stopwatch.StartNew();
            dialog.Surface.Sort.SelectedIndex = 1;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            sort.Stop();
            Require(dialog.TagList.Rows.Count == count + 2 && dialog.TagList.Rows.SkipWhile(row => row.Section != TagListSection.Other || !row.IsHeader).Skip(1).First().Uses == 1,
                "Most used first must list the used tags first in one section.");
            dialog.Surface.Sort.SelectedIndex = 0;
            if (count == 20_000) Program.CaptureWindow(dialog, "smoke-tag-dialog-20000.png");

            Console.WriteLine($"SMOKE tag dialog timing [{count} tags, scale {dialog.RenderScaling}]: open {open.ElapsedMilliseconds} ms, toggle {toggle.ElapsedMilliseconds} ms, filter {filter.ElapsedMilliseconds} ms, sort {sort.ElapsedMilliseconds} ms, realized rows {realized}");
            // Generous bounds for a loaded CI box; the former dialog took seconds per refresh at this size.
            Require(open.ElapsedMilliseconds < 4_000 && toggle.ElapsedMilliseconds < 1_500 && filter.ElapsedMilliseconds < 1_500 && sort.ElapsedMilliseconds < 1_500,
                "The tag dialog is too slow with " + count + " tags.");
            dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => !dialog.IsVisible);
        }
        finally { dialog.CloseForLifetime(); }

        static string Name(int index) => "tag_" + index.ToString("D5");
    }

    /// <summary>The list as "#Section" labels and "name+" (on) / "name-" (off) rows.</summary>
    internal static string[] Rows(MediaTagDialog dialog) => dialog.TagList.Rows
        .Select(row => row.IsHeader ? "#" + row.Section : row.Label + (row.State == TagToggleState.All ? "+" : "-")).ToArray();

    /// <summary>Moves the list cursor to the tag and presses Space on its row, as the keyboard does.</summary>
    internal static async Task<TagToggleRow> PressSpaceAsync(MediaTagDialog dialog, string name)
    {
        TagToggleRow row = dialog.TagList.Rows.Single(item => item.Tag?.Name.Display == name);
        dialog.TagList.Focus(row);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(dialog.TagList.Current == row, "The list cursor did not reach " + name);
        Control target = dialog.TagList.ContainerFromItem(row) ?? dialog.TagList;
        var space = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space };
        target.RaiseEvent(space);
        Require(space.Handled, "Space was not handled by the tag list.");
        await IdleAsync(dialog);
        return row;
    }

    private static async Task IdleAsync(MediaTagDialog dialog)
    {
        await UntilAsync(() => !dialog.Session.Busy);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static async Task<GalleryEntry> BoundAsync(string directory)
    {
        (MediaAssetSnapshot snapshot, MediaAssetReference asset, object entry, string path) = await WriteAsync(directory, "bound.png", "901");
        MediaProviderMetadata provider = new("901", "original", "https://offline.invalid/bound.png", "bound.png",
            Png.Length, asset.Md5, asset.Sha256);
        string revision = MediaRevisionKeys.CreateAttachmentKey(Resource, "43", 0, provider);
        MediaAttachmentBinding binding = new(new(Resource, "43", 0, revision), MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256),
            new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch, "fixture-profile", "fixture-owner", Resource.Source, 1));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3,
            Entries = new Dictionary<string, object> { ["bound.png"] = entry },
            Assets = new Dictionary<string, object> { [asset.AssetId] = new { RequestedFileName = "bound.png", Entry = entry } },
            AttachmentBindings = new Dictionary<string, MediaAttachmentBinding> { [revision] = binding } }));
        snapshot = await MediaAssetReader.OpenAsync(directory); asset = snapshot.GetPage().Single();
        CapturedAttachment attachment = new(0, "901", Url: provider.Url, OriginalFileName: provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = revision };
        ResolvedMediaAttachment resolution = MediaAttachmentResolver.Resolve(snapshot, Resource, "43", attachment)
            ?? throw new InvalidOperationException("The bound fixture must resolve its exact binding.");
        return new(new(Resource, directory, asset.AssetId), snapshot, asset,
            new CapturedPost("43", 0, PostFieldPresence.All, Html: "<p>Bound image.</p>", Attachments: [attachment]), attachment, false, false)
            { AttachmentResolution = resolution };
    }

    internal static async Task<GalleryEntry> UnboundAsync(string directory)
    {
        (MediaAssetSnapshot snapshot, MediaAssetReference asset, _, _) = await WriteAsync(directory, "loose.png", "902");
        return new(new(Resource, directory, asset.AssetId), snapshot, asset, null, null, false, true);
    }

    internal static async Task<(MediaAssetSnapshot, MediaAssetReference, object, string)> WriteAsync(string directory, string file, string mediaId)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, file), Png);
        object entry = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file,
            FileName = file, OriginalFileName = file, Length = Png.Length,
            Md5 = Convert.ToBase64String(MD5.HashData(Png)), Sha256 = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant() };
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { [file] = entry } }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        return (snapshot, snapshot.GetPage().Single(), entry, path);
    }

    /// <summary>A loose saved file of a kind the gallery has no viewer for (bytes are irrelevant, only the type counts).</summary>
    internal static async Task<GalleryEntry> OtherFileAsync(string directory, string file, string mediaId)
    {
        (MediaAssetSnapshot snapshot, MediaAssetReference asset, _, _) = await WriteAsync(directory, file, mediaId);
        return new(new(Resource, directory, asset.AssetId), snapshot, asset, null, null, false, true);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Gallery media tag UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal static class GalleryViewerChecksAccess
{
    internal static void Click(GalleryViewer viewer, string name)
    {
        Button button = viewer.GetGalleryVisualDescendants().OfType<Button>().FirstOrDefault(item => item.Name == name)
            ?? throw new InvalidOperationException("Missing gallery control " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
