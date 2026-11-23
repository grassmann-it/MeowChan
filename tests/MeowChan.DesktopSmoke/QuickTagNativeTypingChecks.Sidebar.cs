using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;
using MeowChan.Tags;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static partial class QuickTagNativeTypingChecks
{
    private static async Task GallerySidebarSegmentsAsync(MainWindow window, GalleryViewer viewer, MediaNativeInput input)
    {
        await input.ChordAsync('p', MediaNativeInput.ShiftLeft);
        GallerySidePanel panel = viewer.SidePanel;
        await Program.WaitUntilAsync(() => panel.IsEffectivelyVisible && panel.Segment == GallerySidePanelSegment.Post);
        panel.PostButton.Focus();
        await input.KeyAsync(MediaNativeInput.Right);
        Check(panel.Segment == GallerySidePanelSegment.Tags && viewer.IsTagPanelOpen && panel.TagsButton.IsFocused,
            "Right on the Post segment did not focus and select Tags.");
        GalleryEntry entry = viewer.CurrentEntry!;
        MediaTagSubject subject = MediaTagSubjects.Of(entry)!;
        viewer.Marks!.Mark(subject, entry);
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage(language == "de" ? "en" : "de");
            await Settle(window);
            string marked = (string)ToolTip.GetTip(panel.Position)!;
            Check(panel.Position.Classes.Contains("marked"), "The Tags pill lost the mark after a language change.");
            await input.KeyAsync(MediaNativeInput.Left);
            Check(panel.Segment == GallerySidePanelSegment.Post && !viewer.IsTagPanelOpen && panel.PostButton.IsFocused,
                "Left on the Tags segment did not focus and select Post.");
            Check(Equals(marked, ToolTip.GetTip(panel.Position)) && Avalonia.Automation.AutomationProperties.GetName(panel.Position) == marked,
                "Switching to Post changed the marked position or its accessible name.");
            await input.KeyAsync(MediaNativeInput.Escape);
            await Program.WaitUntilAsync(() => !panel.IsEffectivelyVisible);
            Check(viewer.IsKeyboardFocusWithin && !panel.IsKeyboardFocusWithin, "Escape did not return focus to the gallery.");
            Console.WriteLine("SMOKE gallery sidebar: real Left/Right/Escape, marked position across segments and language refresh.");
        }
        finally
        {
            viewer.Marks.Unmark(subject);
            Texts.SetLanguage(language);
        }
    }

    private static Button EditTags(ReaderMediaSidebar sidebar) => sidebar.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Name == "ReaderMediaSidebarEditTags");

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static async Task WriteSidebarTagAsync(MainWindow window, MediaNativeInput input, Button origin,
        MediaTagSubject prior, MediaTagSubject visible, string tag)
    {
        await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface.Input.IsKeyboardFocusWithin: true });
        QuickTagPopover popover = QuickTagPopover.Current!;
        QuickTagSurface editor = popover.Surface!;
        Check(ReferenceEquals(popover.Anchor, origin), "The sidebar shortcut did not use the Edit tags anchor.");
        editor.Input.Text = tag;
        Check(await editor.CommitTypedAsync(), "The tag editor refused the test tag.");
        var snapshot = window.SessionHost.GetPublishedPrivateTagSnapshot().Snapshot!;
        Check(snapshot.TagsOf(visible).Any(definition => definition.Name.Display == tag), "The visible medium did not receive the tag.");
        Check(snapshot.TagsOf(prior).All(definition => definition.Name.Display != tag), "The previously visible medium received the tag.");
        editor.Input.Dismiss();
        await input.KeyAsync(MediaNativeInput.Escape);
        await Program.WaitUntilAsync(() => QuickTagPopover.Current == null && origin.IsFocused);
    }

    private static async Task ReaderSidebarTagRaceAsync(MainWindow window, ArchivePage page, MediaNativeInput input)
    {
        Button edit = EditTags(page.MediaSidebar!);
        ListBox posts = Named<ListBox>(window, "ArchivePosts");
        ArchivePostRow a = posts.Items.OfType<ArchivePostRow>().Single(row => row.Id == "482");
        ArchivePostRow b = posts.Items.OfType<ArchivePostRow>().Single(row => row.Id == PicturePost);
        GalleryEntry savedA = (await a.Attachments[1].ResolveEntry!(CancellationToken.None))!;
        MediaTagSubject subjectA = MediaTagSubjects.Of(savedA)!;
        MediaTagSubject subjectB = MediaTagSubjects.Of(await b.Attachments[0].ResolveEntry!(CancellationToken.None))!;
        var open = page.TagPopoverAsync!;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GalleryEntry? captured = null;
        page.TagPopoverAsync = async (entry, anchor, context, current, request) =>
        {
            captured = entry;
            await release.Task;
            try { await open(entry, anchor, context, current, request); }
            finally { returned.TrySetResult(); }
        };
        try
        {
            // Re-showing the sidebar resets its follow throttle. A is admitted synchronously, B follows at the next tick.
            posts.SelectedItem = b;
            page.ToggleMediaSidebar();
            page.ToggleMediaSidebar();
            await Settle(window);
            posts.SelectedItem = a;
            page.SelectMediaSidebarIndex(1);
            posts.SelectedItem = b;
            Check(page.MediaSidebar!.HeaderLine.Contains("picture-482-second.jpg", StringComparison.Ordinal),
                "The test missed the Reader follow interval.");
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(captured != null && Equals(MediaTagSubjects.Of(captured), subjectA),
                $"Edit tags captured {captured?.EntryKey.ToString() ?? "nothing"} at {page.MediaSidebar.HeaderLine}; expected {savedA.EntryKey} (edit visible={edit.IsEffectivelyVisible}).");
            await Program.WaitUntilAsync(() => page.MediaSidebar.HeaderLine.Contains("picture-481.jpg", StringComparison.Ordinal));
            page.TagPopoverAsync = open;
            edit.Focus();
            await input.KeyAsync(MediaNativeInput.Enter);
            await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: not null });
            QuickTagPopover shown = QuickTagPopover.Current!;
            release.SetResult();
            await returned.Task;
            Check(ReferenceEquals(shown, QuickTagPopover.Current), "Reader resolution replaced the new editor with the retired attachment.");
            await WriteSidebarTagAsync(window, input, edit, subjectA, subjectB, "reader sidebar race");
            Console.WriteLine("SMOKE Reader sidebar tags: delayed 482 attachment 2, visible 481 attachment 1, Enter, exact write, Escape focus.");
        }
        finally
        {
            page.TagPopoverAsync = open;
            release.TrySetResult();
            QuickTagPopover.Current?.Close();
        }
    }

    private static async Task MediaSidebarTagRaceAsync(MainWindow window, MediaNativeInput input)
    {
        MediaPage page = window.MediaPageForTests!;
        MediaLibraryTileRow[] tiles = (MediaLibraryTileRow[])page.Grid.ItemsSource!;
        MediaLibraryTileRow a = tiles.Single(row => row.Entry.Name == "picture-481.jpg");
        MediaLibraryTileRow b = tiles.Single(row => row.Entry.Name == "picture-482-second.jpg");
        MediaTagSubject subjectA = MediaTagSubjects.Of(a.Entry)!;
        MediaTagSubject subjectB = MediaTagSubjects.Of(b.Entry)!;
        TaskCompletionSource resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.AfterMediaTagResolutionForTests = entry =>
        {
            if (entry?.Asset.OriginalFileName != a.Entry.Name) return Task.CompletedTask;
            resolved.TrySetResult();
            return release.Task;
        };
        try
        {
            page.Grid.SelectedItem = a;
            await Program.WaitUntilAsync(() => page.PanelRow?.Entry == a.Entry);
            Button edit = EditTags(page.Panel!);
            edit.Focus();
            await input.KeyAsync(MediaNativeInput.Enter);
            await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            page.Grid.SelectedItem = b;
            await Program.WaitUntilAsync(() => page.PanelRow?.Entry == b.Entry);
            edit.Focus();
            await input.KeyAsync('t', modified: true);
            await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: not null });
            QuickTagPopover shown = QuickTagPopover.Current!;
            release.SetResult();
            await Settle(window);
            Check(ReferenceEquals(shown, QuickTagPopover.Current), "Media resolution replaced the new editor with the retired medium.");
            await WriteSidebarTagAsync(window, input, edit, subjectA, subjectB, "media sidebar race");

            resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Grid.SelectedItem = page.Grid.Items.OfType<MediaLibraryTileRow>().Single(row => row.Entry.Name == a.Entry.Name);
            await Program.WaitUntilAsync(() => page.PanelRow?.Entry.Name == a.Entry.Name);
            edit.Focus();
            await input.KeyAsync(MediaNativeInput.Enter);
            await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await input.KeyAsync(MediaNativeInput.Escape);
            release.SetResult();
            await Settle(window);
            Check(QuickTagPopover.Current == null && edit.IsFocused, "Escape let the outstanding Media request take focus later.");
            Console.WriteLine("SMOKE Media sidebar tags: delayed 481, visible 482 attachment 2, exact write, Enter, shortcut, pending and open Escape.");
        }
        finally
        {
            window.AfterMediaTagResolutionForTests = null;
            release.TrySetResult();
            QuickTagPopover.Current?.Close();
        }
    }
}
