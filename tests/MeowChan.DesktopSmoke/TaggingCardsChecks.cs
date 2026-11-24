using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_TAGGING_CARDS_ONLY. PT4: the real main window opens the gallery in the tagging mode over a queue of three
// synthetic media (one bound, two loose) in the real host and stores, then K switches the card mode on. Five cards at least 48 high and
// 120 wide with their text and key, named for the screen reader; the strip's own buttons give way. Key 4 (Stamp) gives the stamp to the
// bound medium and finishes it, key 1 (Back) takes that decision back and returns, key 3 (Favorite) makes it a favorite and finishes it
// in one step, key 2 skips, key 5 finishes the last medium; the choice of K is kept in the settings, the cards follow Light and Dark,
// both widths and a language switch. Managed input only (routed key events), no OS keyboard, no Windows/macOS acceptance.
internal static class TaggingCardsChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "tagging-cards-fixture");
        string language = Texts.Language;
        ThemeVariant theme = Application.Current!.RequestedThemeVariant ?? ThemeVariant.Default;
        Size originalSize = window.ClientSize;
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
            await host.DefinePrivateTagAsync("stamped");
            Require(PrivateTagName.TryCreate("stamped", out PrivateTagName? stampName, out _) && stampName != null, "The stamp name is invalid.");
            window.QuickTagStampSet.Add(stampName!);
            await UntilAsync(() => host.Config.TagStamp.SequenceEqual(new[] { "stamped" }), "The stamp was not kept in the settings.");

            await window.OpenTaggingWalkAsync(queue, queue.Items, entries, bound.EntryKey);
            await GalleryViewerChecksAccess.WaitForLayoutAsync(window);
            ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            await UntilAsync(() => viewer.IsTaggingMode && viewer.IsTagPanelOpen && viewer.TaggingStrip.IsEffectivelyVisible, "The strip or the tag panel is missing.");
            Require(!viewer.IsCardMode && !viewer.TaggingCardsRow.IsVisible && viewer.TaggingDoneButton.IsVisible, "The walk did not start without cards.");

            // Shift+K (revision 13; it was K) switches the card mode on: five cards, the strip's buttons give way, the choice is kept for the next walk.
            Raise(viewer, Key.K, KeyModifiers.Shift);
            await UntilAsync(() => viewer.IsCardMode && viewer.TaggingCardsRow.IsEffectivelyVisible, "Shift+K did not show the cards.");
            Require(!viewer.TaggingDoneButton.IsVisible && !viewer.TaggingSkipButton.IsVisible && !viewer.TaggingBackButton.IsVisible, "The strip's buttons stayed.");
            Require(viewer.TaggingStatusText.Text == Texts.T("cards.status.on", "1–5", CommandCatalog.ShortcutDisplay("tags.queue.cards") ?? ""), "The card mode was not announced: " + viewer.TaggingStatusText.Text);
            await UntilAsync(() => host.Config.TaggingCardsDefault, "The card mode choice was not kept in the settings.");
            await LayoutAsync(window);
            RequireCards(viewer, window, "start");
            Program.CaptureWindow(window, "smoke-tagging-cards.png");

            // The tag panel keeps the keyboard in its field while it is open and a key typed there is text (the router leaves it alone), so the
            // smoke gives the keyboard back to the picture before each card key, like a user who clicks the medium first.
            // Key 4 (Stamp & next): the stamp lands on the bound medium and finishes it.
            viewer.Focus();
            Raise(viewer, Key.D4, KeyModifiers.None);
            await UntilAsync(() => State(host, 0) == TagQueueState.Done && Shown(viewer) == looseA.EntryKey, "The stamp card did not finish the first medium.");
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _) ?? throw new InvalidOperationException("The bound fixture has no tag target.");
            Require(host.GetPrivateTagSnapshot().Snapshot!.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "stamped" }), "The stamp card did not tag the medium.");

            // Key 1 (Back): the newest decision is taken back through the walk's own log and the gallery returns to its medium.
            await IdleAsync(window);
            viewer.Focus();
            Raise(viewer, Key.D1, KeyModifiers.None);
            await UntilAsync(() => State(host, 0) == TagQueueState.Open && Shown(viewer) == bound.EntryKey, "The back card did not return to the medium.");

            // Key 3 (Favorite & next): one step writes the favorite, then finishes the medium; the sentence names both.
            viewer.Focus();
            Raise(viewer, Key.D3, KeyModifiers.None);
            await UntilAsync(() => State(host, 0) == TagQueueState.Done && Shown(viewer) == looseA.EntryKey, "The favorite card did not finish the medium.");
            Require(host.IsFavorite(bound), "The favorite card did not make the medium a favorite.");
            Require(viewer.TaggingStatusText.Text!.StartsWith(Texts.T("gallery.decision.status.favoriteSaved"), StringComparison.Ordinal),
                "The favorite and the decision were not said together: " + viewer.TaggingStatusText.Text);

            // Backspace takes the newest step first (the done), then the favorite in place: one log for cards and keys.
            await IdleAsync(window);
            viewer.Focus();
            Raise(viewer, Key.Back, KeyModifiers.None);
            await UntilAsync(() => State(host, 0) == TagQueueState.Open && Shown(viewer) == bound.EntryKey, "Backspace did not take the done back.");
            Require(host.IsFavorite(bound), "The favorite went with the done.");
            await IdleAsync(window);
            viewer.Focus();
            Raise(viewer, Key.Back, KeyModifiers.None);
            await UntilAsync(() => !host.IsFavorite(bound), "The second Backspace did not take the favorite back.");
            viewer.Focus();
            Raise(viewer, Key.D5, KeyModifiers.None); // Done & next on the bound medium again
            await UntilAsync(() => State(host, 0) == TagQueueState.Done && Shown(viewer) == looseA.EntryKey, "The done card did not finish the medium.");

            // Key 2 (Skip) and key 5 (Done) finish the rest; a card key never decides while the previous medium is still being shown.
            viewer.Focus();
            Raise(viewer, Key.D2, KeyModifiers.None);
            await UntilAsync(() => State(host, 1) == TagQueueState.Skipped && Shown(viewer) == looseB.EntryKey, "The skip card did not skip.");
            viewer.Focus();
            Raise(viewer, Key.D5, KeyModifiers.None);
            await UntilAsync(() => State(host, 2) == TagQueueState.Done, "The done card did not finish the last medium.");
            Require(viewer.TaggingStatusText.Text!.EndsWith("Every medium of this walk is decided.", StringComparison.Ordinal),
                "The end of the walk was not said: " + viewer.TaggingStatusText.Text);

            // Light and Dark, both widths, a language switch: names, sizes and the visible key hold.
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach ((int width, int height, string lang) in new[] { (1440, 920, "en"), (760, 500, "de") })
            {
                Application.Current.RequestedThemeVariant = variant;
                Texts.SetLanguage(lang);
                window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
                await UntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height), "The window did not reach " + width + "x" + height + ".");
                await LayoutAsync(window);
                string tag = $"{width}x{height}-{variant}-{lang}".ToLowerInvariant();
                RequireCards(viewer, window, tag);
                Program.CaptureWindow(window, $"smoke-tagging-cards-{tag}.png");
            }
            Application.Current.RequestedThemeVariant = theme;
            Texts.SetLanguage("en");

            // Shift+K again: the strip is back and the choice is kept off; the Back card stays enabled while the others need a decidable medium.
            viewer.Focus();
            Raise(viewer, Key.K, KeyModifiers.Shift);
            await UntilAsync(() => !viewer.IsCardMode && viewer.TaggingDoneButton.IsEffectivelyVisible && !viewer.TaggingCardsRow.IsVisible, "Shift+K did not hide the cards.");
            await UntilAsync(() => !host.Config.TaggingCardsDefault, "The card mode choice was not kept off.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await UntilAsync(() => galleryHost.Content is not GalleryViewer && window.GalleryTagging == null, "The gallery did not close.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = theme;
            window.Width = originalSize.Width; window.Height = originalSize.Height;
            try { await window.SessionHost.ClearTagQueueAsync(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        }
    }

    // An undo is offered once the walk's writes are over (TaggingUndo.CanUndo): the queue file shows a decision a moment before its step is
    // recorded, so the smoke waits for the walk to be idle like a user who sees the strip settle.
    private static Task IdleAsync(MainWindow window) => UntilAsync(() => window.GalleryTagging is not { Busy: true }, "The walk's writes did not end.");

    // Five buttons, each at least 48 x 120 dip with text and key, named "<label>, key <n>" with the key as accelerator, inside the window.
    private static void RequireCards(GalleryViewer viewer, MainWindow window, string tag)
    {
        Require(viewer.TaggingCardButtons.Count == 5 && viewer.TaggingCards.Count == 5, $"{tag}: not five cards.");
        for (int i = 0; i < 5; i++)
        {
            Button card = viewer.TaggingCardButtons[i];
            TaggingCardAction action = viewer.TaggingCards[i];
            string expected = action.Kind == TaggingCardKind.Back
                ? TaggingCardsText.Name(AutomationProperties.GetName(viewer.TaggingBackButton)!, i + 1)
                : TaggingCardsText.Name(action, i + 1);
            Require(card.IsEffectivelyVisible && card.Bounds.Height >= 48 && card.Bounds.Width >= 120, $"{tag}: card {i + 1} is too small: {card.Bounds.Size}.");
            Require(AutomationProperties.GetName(card) == expected, $"{tag}: card {i + 1} has the name '{AutomationProperties.GetName(card)}', expected '{expected}'.");
            Require(AutomationProperties.GetAcceleratorKey(card) == (i + 1).ToString(), $"{tag}: card {i + 1} does not name its key.");
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(card)), $"{tag}: card {i + 1} has no help text.");
            string text = string.Join(" ", card.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
            Require(text.Contains(viewer.TaggingCards[i].Label(), StringComparison.Ordinal) && text.Contains((i + 1).ToString(), StringComparison.Ordinal),
                $"{tag}: card {i + 1} does not show its text and key: {text}");
            Point origin = card.TranslatePoint(default, window) ?? default;
            Require(origin.X >= -0.5 && origin.X + card.Bounds.Width <= window.Bounds.Width + 0.5, $"{tag}: card {i + 1} leaves the window.");
        }
        Require(AutomationProperties.GetName(viewer.TaggingCardsRow) == Texts.T("cards.strip.name"), $"{tag}: the card row has no name.");
    }

    private static async Task LayoutAsync(MainWindow window)
    {
        await GalleryViewerChecksAccess.WaitForLayoutAsync(window);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
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
