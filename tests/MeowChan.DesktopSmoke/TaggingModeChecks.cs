using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_TAGGING_MODE_ONLY. PT3: the real main window opens the gallery in the tagging mode over a queue of three
// synthetic media (one bound, two loose) in the real host and stores. Stamp & next (Ctrl+Shift+Enter) gives the shared stamp to the shown
// medium and finishes it, Enter finishes, Shift+Space never decides, Space skips, Backspace takes the newest decision back (and returns to
// its medium); the header names the queue position, the strip follows a language switch, paging decides nothing, and closing keeps the
// resume point on the open medium shown last. Managed input only (routed key events), no OS keyboard, no Windows/macOS acceptance.
internal static class TaggingModeChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "tagging-mode-fixture");
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            DesktopSessionHost host = window.SessionHost;
            GalleryEntry bound = await GalleryMediaTagChecks.BoundAsync(Path.Combine(root, "bound"));
            GalleryEntry looseA = await GalleryMediaTagChecks.UnboundAsync(Path.Combine(root, "loose-a"));
            GalleryEntry looseB = await GalleryMediaTagChecks.UnboundAsync(Path.Combine(root, "loose-b"));
            GalleryEntry[] entries = { bound, looseA, looseB };
            (IReadOnlyList<TagQueueItem> items, int skipped) = TagQueueActions.ItemsOf(entries, DateTimeOffset.UtcNow);
            Require(items.Count == 3 && skipped == 0, "The fixture media cannot be queued.");
            await host.EnqueueTagQueueAsync(items);
            TagQueueSnapshot queue = await host.LoadTagQueueAsync();

            // The shared stamp (PT5 host glue): one own tag, saved in the settings.
            await host.DefinePrivateTagAsync("stamped");
            Require(PrivateTagName.TryCreate("stamped", out PrivateTagName? stampName, out _) && stampName != null, "The stamp name is invalid.");
            window.QuickTagStampSet.Add(stampName!);
            await UntilAsync(() => host.Config.TagStamp.SequenceEqual(new[] { "stamped" }), "The stamp was not kept in the settings.");

            Require(window.ShowTaggingPage(), "The Tagging page could not open.");
            await window.OpenTaggingWalkAsync(queue, queue.Items, entries, bound.EntryKey);
            await GalleryViewerChecksAccess.WaitForLayoutAsync(window);
            ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            TaggingWalk walk = window.GalleryTagging ?? throw new InvalidOperationException("The walk-through did not start the tagging mode.");
            await UntilAsync(() => viewer.IsTaggingMode && viewer.IsTagPanelOpen && viewer.TaggingStrip.IsEffectivelyVisible, "The strip or the tag panel is missing.");
            Require(viewer.PositionText == "Queue 1 of 3", "The header does not name the queue position: " + viewer.PositionText);
            Require(viewer.TaggingProgressText.Text == "Open: 3 · Done: 0 · Skipped: 0", "Wrong progress: " + viewer.TaggingProgressText.Text);
            string doneShortcut = MeowChan.Desktop.Commands.CommandCatalog.ShortcutDisplay("tags.queue.done-next") ?? string.Empty;
            Require(AutomationProperties.GetName(viewer.TaggingDoneButton) == Texts.T("tagging.done")
                && AutomationProperties.GetAcceleratorKey(viewer.TaggingDoneButton) == doneShortcut,
                $"The Done button has no name or current key (expected '{doneShortcut}', got '{AutomationProperties.GetAcceleratorKey(viewer.TaggingDoneButton)}').");
            Require(viewer.TagSurface!.Session.Stamp.Count == 1, "The panel does not share the window's stamp.");
            Require(viewer.TagSurface.CounterText == null && !viewer.Filmstrip.IsPositionShown && !viewer.TaggingStatusText.IsVisible,
                "The Tags editor repeated the counters or the status.");
            MediaPositionPill pill = viewer.SidePanel.Position;
            void CheckPill(int number)
            {
                Require(ReferenceEquals(pill, viewer.SidePanel.Position) && pill.IsEffectivelyVisible
                    && ((TextBlock)pill.Child!).Text == Texts.T("tags.quick.gallery.positionShort", number, 3)
                    && AutomationProperties.GetName(pill) == Texts.T("tagging.position", number, 3),
                    "The shared sidebar pill did not follow the queue position.");
            }
            CheckPill(1);
            Program.CaptureWindow(window, "112-p2-tagging-gallery-tags.png");
            viewer.SidePanel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !viewer.IsTagPanelOpen && viewer.SidePanel.Segment == GallerySidePanelSegment.Post,
                "The queue gallery did not switch to Post.");
            Require(viewer.IsTaggingMode, "Switching the panel tab ended the queue walk.");
            CheckPill(1);
            Program.CaptureWindow(window, "112-p2-tagging-gallery-post.png");
            viewer.SidePanel.TagsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => viewer.IsTagPanelOpen && viewer.SidePanel.Segment == GallerySidePanelSegment.Tags,
                "The queue gallery did not switch back to Tags.");
            CheckPill(1);

            // Stamp & next: the stamp tag lands on the bound medium, the medium is done and the gallery shows the next one.
            KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            Raise(viewer, Key.Enter, primary | KeyModifiers.Shift);
            await UntilAsync(() => State(host, 0) == TagQueueState.Done && Shown(viewer) == looseA.EntryKey, "Stamp & next did not finish the first medium.");
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _) ?? throw new InvalidOperationException("The bound fixture has no tag target.");
            Require(host.GetPrivateTagSnapshot().Snapshot!.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "stamped" }),
                "The stamp was not given to the bound medium.");
            Require(viewer.PositionText == "Queue 2 of 3", "The header did not follow: " + viewer.PositionText);
            CheckPill(2);

            // Primary+Enter on the picture finishes; Shift+Space plays or pauses and never decides; S skips the last open medium and the walk is through.
            viewer.Focus();
            Raise(viewer, Key.Enter, primary);
            await UntilAsync(() => State(host, 1) == TagQueueState.Done && Shown(viewer) == looseB.EntryKey, "Primary+Enter did not finish the medium.");
            viewer.Focus();
            Raise(viewer, Key.Space, KeyModifiers.Shift);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(State(host, 2) == TagQueueState.Open, "Shift+Space decided the medium.");
            viewer.Focus();
            Raise(viewer, Key.S, KeyModifiers.None);
            await UntilAsync(() => State(host, 2) == TagQueueState.Skipped, "S did not skip.");
            Require(Shown(viewer) == looseB.EntryKey && viewer.TaggingStatusText.Text!.EndsWith("Every medium of this walk is decided.", StringComparison.Ordinal),
                "The end of the walk was not said: " + viewer.TaggingStatusText.Text);

            // Backspace takes the newest decision back first (the skip, in place), then the one before it and returns to its medium.
            // The undo is offered once the walk's write is over (TaggingUndo.CanUndo; the step is recorded when the write ends, a moment
            // after the queue file shows the state), so the smoke waits for that like a user who sees the strip settle.
            await UntilAsync(() => !walk.Busy && walk.Steps == 3, "The walk's writes did not end.");
            viewer.Focus();
            Raise(viewer, Key.Back, KeyModifiers.None);
            await UntilAsync(() => State(host, 2) == TagQueueState.Open, "Backspace did not take the skip back.");
            await UntilAsync(() => !walk.Busy && walk.Steps == 2, "The undo of the skip did not end.");
            viewer.Focus();
            Raise(viewer, Key.Back, KeyModifiers.None);
            await UntilAsync(() => State(host, 1) == TagQueueState.Open && Shown(viewer) == looseA.EntryKey, "Backspace did not return to the finished medium.");
            Require(viewer.TaggingStatusText.Text == Texts.T("tagging.status.undone.open", walk.Items[1].Name), "The undo was not said: " + viewer.TaggingStatusText.Text);
            Require(host.GetTagQueue()!.Cursor == 1, "The undo did not move the resume point back.");

            // The strip follows a language switch.
            Texts.SetLanguage("de");
            await UntilAsync(() => AutomationProperties.GetName(viewer.TaggingDoneButton) == "Erledigt & weiter", "The strip did not follow the language.");
            Require(viewer.TaggingProgressText.Text == "Offen: 2 · Erledigt: 1 · Übersprungen: 0", "Wrong German progress: " + viewer.TaggingProgressText.Text);
            CheckPill(2);
            Program.CaptureWindow(window, "smoke-tagging-mode-de.png");
            Texts.SetLanguage("en");

            // Paging moves without deciding; closing ends the mode and keeps the resume point on the open medium shown last.
            GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
            await UntilAsync(() => Shown(viewer) == looseB.EntryKey, "Paging did not move on.");
            Require(State(host, 1) == TagQueueState.Open && State(host, 2) == TagQueueState.Open, "Paging decided a medium.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await UntilAsync(() => galleryHost.Content is not GalleryViewer && window.GalleryTagging == null, "The gallery did not close.");
            await UntilAsync(() => host.GetTagQueue()?.Cursor == 2, "The resume point did not move to the medium shown last.");
            Require((await host.LoadTagQueueAsync()).ResumeIndex() == 2, "The next walk would not continue at the medium shown last.");
        }
        finally
        {
            Texts.SetLanguage(language);
            try { await window.SessionHost.ClearTagQueueAsync(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        }
    }

    private static TagQueueState? State(DesktopSessionHost host, int index) =>
        host.GetTagQueue() is { } queue && index < queue.Count ? queue.Items[index].State : null;

    private static GalleryEntryKey? Shown(GalleryViewer viewer) => viewer.TagPanelController?.Entry.EntryKey;

    private static void Raise(GalleryViewer viewer, Key key, KeyModifiers modifiers) =>
        viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
