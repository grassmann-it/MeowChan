using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
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

            await window.OpenMediaGalleryLaidOutAsync([bound, unbound], bound.EntryKey);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => host.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            Button tags = viewer.TagsButton;
            await UntilAsync(() => tags.IsEffectivelyEnabled);
            // On the toolbar the live count is the button's accessible name and tooltip; its visible text stays "Tags" (GalleryViewer.UpdateTagsButton).
            Require(Equals(Avalonia.Automation.AutomationProperties.GetName(tags), Texts.T("tags.media.button")), "Initial tags button label is wrong: " + Avalonia.Automation.AutomationProperties.GetName(tags));

            // AP6: Ctrl+T belongs to the thread's tags (Archive, large reader); the gallery's media tags are Ctrl+Shift+T.
            Require(CommandCatalog.ShortcutDisplay("media.tags.edit") == (OperatingSystem.IsMacOS() ? "⇧⌘T" : "Ctrl+Shift+T"),
                "Media tags did not move to Ctrl+Shift+T: " + CommandCatalog.ShortcutDisplay("media.tags.edit"));
            KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            viewer.Focus();
            KeyEventArgs oldKey = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = primary };
            viewer.RaiseEvent(oldKey);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(window.GalleryMediaTagWindow == null && !oldKey.Handled, "Ctrl+T opened the media tags in the gallery; it is the thread-tags key.");
            // The row opens the shell's dialog, so it is only checked for its presence and label here (the media-ratings smoke opens the dialog).
            Require(viewer.ManageRatingsButton.Tag != null && viewer.ManageRatingsButton.IsEnabled
                && ToolTip.GetTip(viewer.ManageRatingsButton)?.ToString()?.StartsWith(CommandCatalog.Label("media.rating.manage"), StringComparison.Ordinal) == true,
                "The gallery menu does not offer the saved-ratings manager next to the rating.");
            KeyEventArgs newKey = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = primary | KeyModifiers.Shift };
            viewer.RaiseEvent(newKey);
            Require(newKey.Handled, "Ctrl+Shift+T did not reach the gallery's media tags.");
            // TP2: the gallery opens the tag panel next to the picture; the full dialog is behind "Advanced...".
            Require(viewer.IsTagPanelOpen && viewer.TagSurface != null && window.GalleryMediaTagWindow == null,
                "Ctrl+Shift+T did not open the tag panel instead of the dialog.");
            // V2: the tag panel is the Tags segment of the side panel host (one × in its head); the Post segment takes the host over
            // and the Tags segment brings the panel back for the same medium.
            Require(viewer.SidePanel.Segment == MeowChan.Desktop.Controls.GallerySidePanelSegment.Tags && viewer.SidePanel.TagsButton.IsChecked == true
                && !viewer.TagSurface!.CloseButton.IsVisible, "The tag panel is not the Tags segment of the side panel.");
            // UX-LEDGER-14: the existing live counter belongs beside Post/Tags, not beside the title in the surface below.
            double panelWidth = viewer.PanelWidth;
            foreach (string counterLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(counterLanguage);
                foreach (int width in new[] { 280, 320, 480 })
                {
                    viewer.PanelWidth = width;
                    window.UpdateLayout(); // the existing gallery close button does not prove the new header slot has been templated
                    Border counter = viewer.SidePanel.HeaderCounter as Border ?? throw new InvalidOperationException("Missing header counter.");
                    Require(counter.IsEffectivelyVisible && !viewer.TagSurface!.Header.Children.Contains(counter), "Counter was duplicated or hidden.");
                    Require(Avalonia.Automation.AutomationProperties.GetName(counter) == Texts.T("tags.quick.gallery.position", 1, 2)
                        && Avalonia.Automation.AutomationProperties.GetLiveSetting(counter) == Avalonia.Automation.AutomationLiveSetting.Polite
                        && counter.Child is TextBlock liveLabel
                        && Avalonia.Automation.AutomationProperties.GetLiveSetting(liveLabel) == Avalonia.Automation.AutomationLiveSetting.Polite
                        && liveLabel.Text == Texts.T("tags.quick.gallery.position", 1, 2),
                        "The moved counter lost localization or polite announcements.");
                    var origin = counter.TranslatePoint(default, viewer.SidePanel)!.Value;
                    var segment = (Control)viewer.SidePanel.PostButton.Parent!;
                    var closeOrigin = viewer.SidePanel.CloseButton.TranslatePoint(default, viewer.SidePanel)!.Value;
                    Require(origin.X >= segment.Bounds.Right && origin.X - segment.Bounds.Right <= 9
                        && Math.Abs(origin.Y + counter.Bounds.Height / 2 - (segment.Bounds.Y + segment.Bounds.Height / 2)) <= 1
                        && origin.X + counter.Bounds.Width <= closeOrigin.X - 23,
                        "The counter is not adjacent to Post/Tags in the same row, or crowds close.");
                    Program.CaptureWindow(window, $"smoke-gallery-counter-header-{counterLanguage}-{width}.png");
                }
            }
            viewer.PanelWidth = panelWidth;
            Texts.SetLanguage("en");
            viewer.SidePanel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !viewer.IsTagPanelOpen && viewer.SidePanel.Segment == MeowChan.Desktop.Controls.GallerySidePanelSegment.Post);
            Require(viewer.CaptureState().ContextOpen && viewer.SidePanel.PostButton.IsChecked == true, "The Post segment did not take the side panel over.");
            Require(viewer.SidePanel.HeaderCounter == null, "The released tag surface left a stale counter in the header.");
            viewer.SidePanel.TagsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => viewer.IsTagPanelOpen && viewer.SidePanel.Segment == MeowChan.Desktop.Controls.GallerySidePanelSegment.Tags);
            await UntilAsync(() => viewer.TagSurface!.IsVisible && viewer.TagSurface.AdvancedButton.IsEffectivelyEnabled);
            // TP4: Shift+Page Down marks the shown medium and moves on, Shift+Page Up goes back and unmarks it. The counter chip shows
            // "1 marked" with its clear button, the panel's target is the selection while a mark exists, and the panel is on the first medium again.
            MeowChan.Desktop.Controls.QuickTagSurface marking = viewer.TagSurface!;
            Require(viewer.Marks is { Count: 0 } && !marking.MarkBar.IsVisible && !marking.Session.IsSelection, "The tag panel starts with marks.");
            Require(marking.Execute(new MeowChan.Desktop.Controls.QuickTagKeyRoute(MeowChan.Desktop.Controls.QuickTagKeyAction.MarkNext)),
                "Shift+Page Down was not handled by the tag panel.");
            Require(viewer.Marks.Count == 1 && marking.MarkBar.IsVisible && marking.Session.IsSelection && marking.MarkedCount == 1
                && ((TextBlock)marking.MarkedBadge.Child!).Text == Texts.T("tags.quick.marked", "1"), "Marking did not show the counter chip and the selection.");
            await UntilAsync(() => viewer.TagPanelController!.Entry.EntryKey == unbound.EntryKey);
            Require(marking.Session.IsSelection && marking.MarkedCount == 1, "The selection did not follow the gallery to the next medium.");
            Program.CaptureWindow(window, "smoke-gallery-marked.png");
            Require(marking.Execute(new MeowChan.Desktop.Controls.QuickTagKeyRoute(MeowChan.Desktop.Controls.QuickTagKeyAction.UnmarkPrevious)),
                "Shift+Page Up was not handled by the tag panel.");
            await UntilAsync(() => viewer.TagPanelController!.Entry.EntryKey == bound.EntryKey);
            Require(viewer.Marks.Count == 0 && !marking.MarkBar.IsVisible && !marking.Session.IsSelection, "Unmarking did not return the panel to the single medium.");
            // The clear button ends a mark as well.
            marking.Execute(new MeowChan.Desktop.Controls.QuickTagKeyRoute(MeowChan.Desktop.Controls.QuickTagKeyAction.MarkNext));
            await UntilAsync(() => viewer.TagPanelController!.Entry.EntryKey == unbound.EntryKey && marking.MarkBar.IsVisible);
            marking.MarkedClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(viewer.Marks.Count == 0 && !marking.MarkBar.IsVisible && !marking.Session.IsSelection, "The clear button left marks.");
            Border movedCounter = viewer.SidePanel.HeaderCounter as Border ?? throw new InvalidOperationException("Navigation lost the header counter.");
            Require(movedCounter.Child is TextBlock movedLabel && movedLabel.Text == Texts.T("tags.quick.gallery.position", 2, 2)
                && Avalonia.Automation.AutomationProperties.GetName(movedCounter) == movedLabel.Text
                && Avalonia.Automation.AutomationProperties.GetLiveSetting(movedCounter) == Avalonia.Automation.AutomationLiveSetting.Polite
                && Avalonia.Automation.AutomationProperties.GetLiveSetting(movedLabel) == Avalonia.Automation.AutomationLiveSetting.Polite,
                "Navigation did not update the reparented polite live region's text and accessible name.");
            // PT6: the favorite and rating row sits above the field; Shift+4 in the empty field rates the shown medium, the row and the status line
            // say so, and the panel's undo key takes the rating back (the newest step of the tag and decision stacks goes first).
            // The clear button leaves the gallery on the unbound next medium, whose tag field is disabled (it has no tag target), so the
            // empty-field rating key is tried on the bound medium again.
            viewer.TagSurface.Execute(new MeowChan.Desktop.Controls.QuickTagKeyRoute(MeowChan.Desktop.Controls.QuickTagKeyAction.Previous));
            await UntilAsync(() => viewer.TagPanelController!.Entry.EntryKey == bound.EntryKey && marking.Input.IsEffectivelyEnabled);
            MeowChan.Desktop.Controls.GalleryDecisionRow decisions = viewer.DecisionRow ?? throw new InvalidOperationException("The tag panel has no favorite and rating row.");
            Require(decisions.StarButtons.Count == 5 && decisions.FavoriteButton.IsEffectivelyVisible && decisions.View is { Available: true, Rating: null },
                "The row does not show an unrated medium: " + decisions.Summary.Text);
            marking.FocusField();
            // Focusing the field offers the saved tags in a list for a moment, and an open list keeps the digit keys for itself (typing or
            // choosing); the smoke closes it first, like a user who dismisses the list before rating.
            marking.Input.Dismiss();
            await UntilAsync(() => !marking.Input.IsDropDownOpen);
            KeyEventArgs rate = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D4, KeyModifiers = KeyModifiers.Shift };
            marking.Input.RaiseEvent(rate);
            // The row shows the rating a moment before the decision's sentence reaches the status line, so wait for both.
            await UntilAsync(() => decisions.View.Rating == 4 && marking.Status.Text == Texts.T("gallery.decision.status.ratingSaved", 4));
            Require(rate.Handled && decisions.StarButtons[3].IsChecked == true && marking.Status.Text == Texts.T("gallery.decision.status.ratingSaved", 4)
                && string.IsNullOrEmpty(marking.Input.Text), "Shift+4 in the empty field did not rate the shown medium: " + marking.Status.Text);
            Program.CaptureWindow(window, "smoke-gallery-decision-row.png");
            Require(marking.Execute(new MeowChan.Desktop.Controls.QuickTagKeyRoute(MeowChan.Desktop.Controls.QuickTagKeyAction.Undo)), "The undo key did not reach the decision stack.");
            await UntilAsync(() => decisions.View.Rating == null);
            Require(decisions.StarButtons.All(star => star.IsChecked != true), "The undo left a star checked.");
            viewer.TagSurface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Require(escape.Handled && Equals(Avalonia.Automation.AutomationProperties.GetName(tags), Texts.T("tags.media.buttonCount", 1)), "Closing did not update the gallery button: " + Avalonia.Automation.AutomationProperties.GetName(tags));

            PrivateTagStoreSnapshot stored = window.SessionHost.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "my_media" }) && stored.TagsOf(Resource).Count == 0,
                "Store does not hold exactly one media tag and no thread tag.");

            // The next entry has no bound post version and was not offered as a loose file by the gallery selection
            // (a synthetic entry): no tagging, with an explanation. Real loose files are covered by local-media-tags.
            GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
            await UntilAsync(() => !tags.IsEffectivelyEnabled && ToolTip.GetTip(tags) as string == Texts.T("tags.media.boundElsewhere"));
            Require(Equals(Avalonia.Automation.AutomationProperties.GetName(tags), Texts.T("tags.media.button")), "Unbound entry shows another medium's tags.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryPrevious");
            await UntilAsync(() => tags.IsEffectivelyEnabled && Equals(Avalonia.Automation.AutomationProperties.GetName(tags), Texts.T("tags.media.buttonCount", 1)));

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
            SmokeNavigation.GoTo(shell, MainWindow.WorkspacePage.Archive);
            ArchivePage page = shell.ArchivePage;
            await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Archive && !page.IsBusy);
            await page.LoadThreadAsync(Resource).WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline);
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _)!;
            PrivateTagCommit threadTag = await session.DefinePrivateTagAsync("thread_only");
            await session.SetThreadTagAsync(threadTag.Tag, Resource, true);

            // TP3 (E5): the context menu's "Tag…" opens the quick tag popover on the tile; the full dialog is "Advanced…" inside it.
            async Task<QuickTagPopover> OpenPopoverAsync(Control origin)
            {
                origin.Focus();
                origin.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                await UntilAsync(() => page.ArchiveMediaMenu?.IsOpen == true);
                MenuItem edit = page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
                    .Single(item => item.Tag as string == "media.rating.edit");
                Require(edit.IsEnabled && edit.Command!.CanExecute(null), "Bound archive image has no enabled media-tag action.");
                Require(Equals(edit.Header, CommandCatalog.Label("media.rating.edit")), "The Rate and tag action is not localized.");
                Require(!page.ArchiveMediaMenu.Items.OfType<MenuItem>().Any(item => item.Tag as string == "media.tags.dialog"),
                    "The menu must offer one Rate and tag entry, not a second one for the dialog or a rating submenu.");
                // Execute the actual menu command, then retire its popup as a native click does.
                edit.Command!.Execute(null);
                page.ArchiveMediaMenu.Close();
                await UntilAsync(() => shell.ArchiveTagPopover is { IsOpen: true });
                QuickTagPopover popover = shell.ArchiveTagPopover!;
                Require(popover.Surface != null && ReferenceEquals(popover.Anchor, origin) && shell.GalleryMediaTagWindow == null,
                    "The archive tag popover is not anchored at the tile or a dialog opened instead.");
                return popover;
            }
            async Task<MediaTagDialog> OpenAsync(Control origin)
            {
                QuickTagPopover popover = await OpenPopoverAsync(origin);
                popover.Surface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            // The popover itself: add a tag, see the store change and the strip repaint, undo it, close with Escape; focus returns to the tile.
            QuickTagPopover quick = await OpenPopoverAsync(inline!);
            await quick.Surface!.Session.AddAsync("popover_tag").WaitAsync(Deadline);
            Require(session.GetPrivateTagSnapshot().Snapshot!.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "popover_tag" }),
                "The tag popover did not write the tag to the exact media version.");
            await quick.Surface.Session.UndoAsync().WaitAsync(Deadline);
            Require(session.GetPrivateTagSnapshot().Snapshot!.TagsOf(target).Count == 0, "Undo in the tag popover did not take the tag off again.");
            Program.CaptureWindow(shell, "smoke-archive-media-tag-popover.png");
            quick.Surface.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => shell.ArchiveTagPopover == null);
            Require(await quick.Result == QuickTagPopoverCloseReason.Closed, "Escape did not close the tag popover.");
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
                .Single(item => item.Tag as string == "media.rating.edit");
            Require(looseEdit.IsEnabled && looseEdit.Command!.CanExecute(null), "A loose archive file must offer media tags as a local file.");
            looseEdit.Command!.Execute(null);
            page.ArchiveMediaMenu.Close();
            await UntilAsync(() => shell.ArchiveTagPopover is { IsOpen: true });
            shell.ArchiveTagPopover!.Surface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Console.WriteLine("SMOKE archive media tags [en]: inline image -> right-click Tag… -> tag popover (add, undo, Esc) -> Advanced… -> real tag dialog -> host/store; thread tags unchanged; table and tile menus reopen the same media tags; loose file tagged as a local occurrence; no gallery opened.");
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
    /// <summary>
    /// Opens the gallery like the product does and then waits for the new viewer's first layout. An image that is already kept in
    /// the decoded-image cache is shown in the same dispatcher turn the gallery opens, before the viewer has been templated and laid
    /// out, so a check that acts at once would find no controls. A user cannot act before that layout, so only the checks wait.
    /// </summary>
    internal static async Task OpenMediaGalleryLaidOutAsync(this MainWindow window, IReadOnlyList<GalleryEntry> entries,
        GalleryEntryKey start, Control? origin = null)
    {
        await window.OpenMediaGalleryAsync(entries, start, origin);
        await WaitForLayoutAsync(window);
    }

    internal static Task WaitForLayoutAsync(MainWindow window)
    {
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        return Program.WaitUntilAsync(() => host.Content is not GalleryViewer viewer
            || viewer.GetGalleryVisualDescendants().OfType<Button>().Any(item => item.Name == "GalleryClose"),
            () => "The gallery viewer was never templated after it opened.");
    }
    internal static void Click(GalleryViewer viewer, string name)
    {
        Button button = viewer.GetGalleryVisualDescendants().OfType<Button>().FirstOrDefault(item => item.Name == name)
            ?? throw new InvalidOperationException("Missing gallery control " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
