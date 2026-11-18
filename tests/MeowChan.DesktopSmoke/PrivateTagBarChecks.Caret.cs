using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// TG-01/TG-03/TG-04 (UX review 17.11.2026): the token under the caret is completed, Enter takes the one real match. Keys and text
// go through the window's platform input (RawKeyboardInput), so the keyboard device routes them to the focused editor like typing.
internal static partial class PrivateTagBarChecks
{
    private static string[] Values(IdentitySuggestionBox box) =>
        box.ItemsSource?.OfType<IdentitySuggestion>().Select(item => item.Value).ToArray() ?? [];

    private static TextBox Editor(IdentitySuggestionBox box) => box.GetVisualDescendants().OfType<TextBox>().First();

    private static string Observed(IdentitySuggestionBox box) => "text '" + box.Text + "', caret " + Editor(box).CaretIndex
        + ", list " + (box.IsDropDownOpen ? "open" : "closed") + " [" + string.Join("|", Values(box)) + "], highlighted '"
        + box.HighlightedCandidate?.Value + "'";

    private static MediaLibraryEntry QuickEntry() =>
        new(new ResourceId("s", "b", "caret"), "Tcaret", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "1", 1, null, 0,
            "att-v1:" + new string('c', 64), "/dir/caret", "asset-caret", new string('c', 64), 1, "m1", "original",
            "caret.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    // O1-01/TG-01 in the product's own quick tag surface (Rate and tag, gallery tag panel): its Enter handler sits on the field, added
    // after the field's own, and tunnel handlers of one element run newest first. Keys through the platform input exercise that order.
    private static async Task QuickTagEnterAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tags-quick-enter-");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        PrivateTagStore store = new(profile, lease);
        foreach (string name in new[] { "innocent", "inner", "hot", "blonde" }) store.Define(name);
        MediaLibraryEntry entry = QuickEntry();
        MediaTagSubject.Bound subject = new(MediaLibraryFilterEngine.TagTarget(entry)!);
        MediaTagSession media = new(subject, () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, target, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [target], null, token)),
            (id, target, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [target], assigned, null, token)));
        string[] On() => store.ReadSnapshot().TagsOf(MediaLibraryFilterEngine.TagTarget(entry)!).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();
        string[] Vocabulary() => store.ReadSnapshot().Tags.Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();
        using QuickTagSession session = new(QuickTagTarget.For(media));
        MeowChan.Desktop.Controls.QuickTagSurface surface = new(session, name: "CaretQuickTag");
        Window window = new() { Width = 640, Height = 520, Title = "Synthetic quick tag surface", ShowInTaskbar = false, Content = surface };
        window.Show(owner);
        try
        {
            IdentitySuggestionBox box = surface.Input;
            box.Focus();
            await Program.WaitUntilAsync(() => Editor(box).IsKeyboardFocusWithin && session.Ready, () => "The quick tag field is not ready: " + Observed(box));
            // Two matches, Down highlights the first, Enter writes that tag, never a new tag "in" (O1-01).
            RawKeyboardInput.Type(window, "in");
            await Program.WaitUntilAsync(() => Values(box).Contains("innocent") && Values(box).Contains("inner"), () => "in: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Down);
            await Program.WaitUntilAsync(() => box.HighlightedCandidate is { Value: "innocent" or "inner" }, () => "Down: " + Observed(box));
            string chosen = box.HighlightedCandidate!.Value, other = chosen == "inner" ? "innocent" : "inner";
            RawKeyboardInput.Press(window, Key.Enter);
            await Program.WaitUntilAsync(() => On().Length > 0 && session.Ready, () => "Enter wrote nothing: " + Observed(box));
            Require(On().SequenceEqual([chosen]) && !Vocabulary().Contains("in"),
                "O1-01: in + Down (" + chosen + ") + Enter in the quick tag surface gave " + string.Join("|", On()) + ", vocabulary " + string.Join("|", Vocabulary()));
            // TG-01: the one real match left is taken by Enter alone.
            await Program.WaitUntilAsync(() => string.IsNullOrEmpty(box.Text), () => Observed(box));
            RawKeyboardInput.Type(window, "inn");
            await Program.WaitUntilAsync(() => Values(box).Contains(other), () => "inn: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Enter);
            await Program.WaitUntilAsync(() => On().Length > 1 && session.Ready, () => "Enter wrote nothing: " + Observed(box));
            Require(On().SequenceEqual(["inner", "innocent"]) && !Vocabulary().Contains("inn"),
                "TG-01: inn + Enter in the quick tag surface gave " + string.Join("|", On()) + ", vocabulary " + string.Join("|", Vocabulary()));
            // TG-03: a middle token is completed in place; the next Enter adds the draft.
            await Program.WaitUntilAsync(() => string.IsNullOrEmpty(box.Text), () => Observed(box));
            RawKeyboardInput.Type(window, "blo hot");
            await Program.WaitUntilAsync(() => box.Text == "blo hot", () => Observed(box));
            RawKeyboardInput.Press(window, Key.Left, times: 4);
            await Program.WaitUntilAsync(() => box.HighlightedCandidate?.Value == "blonde hot", () => "The list did not follow the caret: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Enter);
            await Program.WaitUntilAsync(() => box.Text == "blonde hot" && Editor(box).CaretIndex == 6, () => Observed(box));
            Require(On().Length == 2, "Completing a middle token already wrote: " + string.Join("|", On()));
            RawKeyboardInput.Press(window, Key.Enter);
            await Program.WaitUntilAsync(() => On().Length == 4 && session.Ready, () => "The second Enter did not add the draft: " + Observed(box));
            Require(On().SequenceEqual(["blonde", "hot", "inner", "innocent"]) && Vocabulary().Length == 4,
                "The completed draft gave " + string.Join("|", On()) + ", vocabulary " + string.Join("|", Vocabulary()));
            Console.WriteLine("SMOKE private-tags quick tag enter [en]: platform keys into the real quick tag surface; in+Down+Enter and inn+Enter take the suggestion, a middle token is completed in place, nothing typed is created.");
        }
        finally
        {
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    // One real tag field: an IdentitySuggestionBox wired by TagSuggestionInput.Attach in its own window, with the owner's Enter
    // like every tag field has it (an Enter the field leaves unhandled adds the typed text).
    private static async Task CaretEntryAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tags-caret-");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        PrivateTagStore store = new(profile, lease);
        foreach (string name in new[] { "innocent", "hot", "blonde", "big tits", "cats", "catgirl" }) store.Define(name);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        List<string> committed = new();
        IdentitySuggestionBox box = new() { Name = "CaretEntryInput", Width = 420 };
        void Commit() { committed.Add(box.Text ?? string.Empty); box.Text = string.Empty; }
        TagSuggestionInput.Attach(box, () => snapshot, () => new HashSet<TagId>(), Commit);
        // Added after Attach like the product owners, so it runs before the field's own tunnel handler; like them it leaves an Enter
        // on a highlighted suggestion to the field (TagSuggestionInput.FieldTakesEnter, here without the IME part).
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            if (TagSuggestionInput.Route(box, e) == TagFieldKey.AcceptSuggestion) return;
            e.Handled = true;
            box.Dismiss();
            Commit();
        }, RoutingStrategies.Tunnel);
        Window window = new()
        {
            Width = 640, Height = 360, Title = "Synthetic tag field", ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Avalonia.Thickness(24), Children = { box } }
        };
        window.Show(owner);
        try
        {
            box.Focus();
            await Program.WaitUntilAsync(() => Editor(box).IsKeyboardFocusWithin, () => "The tag field did not take the focus.");
            async Task FreshAsync()
            {
                box.Text = string.Empty;
                await Program.WaitUntilAsync(() => !box.IsDropDownOpen, () => Observed(box));
                committed.Clear();
            }
            Task ListAsync(Func<bool> condition, string what) => Program.WaitUntilAsync(condition, () => what + ": " + Observed(box));

            // TG-01: one real match, Enter without Up/Down takes it, not the typed prefix.
            RawKeyboardInput.Type(window, "inn");
            await ListAsync(() => Values(box).Contains("innocent"), "inn lists innocent");
            IdentitySuggestion? highlighted = box.HighlightedCandidate;
            // The highlight is the popup list's selected item: what a screen reader names.
            object? announced = box.GetVisualDescendants().OfType<Popup>().Select(popup => popup.Child).OfType<Control>()
                .SelectMany(child => child.GetSelfAndVisualDescendants()).OfType<SelectingItemsControl>().FirstOrDefault()?.SelectedItem;
            string before = Observed(box) + ", selected '" + (box.SelectedItem as IdentitySuggestion)?.Value + "', list selected '" + (announced as IdentitySuggestion)?.Value + "'";
            RawKeyboardInput.Press(window, Key.Enter);
            await Program.WaitUntilAsync(() => committed.Count > 0, () => "Enter added nothing: " + Observed(box));
            Require(committed.SequenceEqual(["innocent"]), "TG-01: inn + Enter added '" + string.Join("|", committed) + "' instead of innocent (before Enter: " + before + ").");
            Require(highlighted?.Value == "innocent" && ReferenceEquals(announced, highlighted),
                "The one match was not visibly highlighted as the list's selected item: " + highlighted?.Value + " / " + (announced as IdentitySuggestion)?.Value);

            // A prefix of an existing tag stays creatable: a space after it, the "New tag" row, or a comma.
            await FreshAsync();
            RawKeyboardInput.Type(window, "in");
            await ListAsync(() => box.HighlightedCandidate?.Value == "innocent", "in preselects innocent");
            RawKeyboardInput.Type(window, " ");
            await ListAsync(() => !box.IsDropDownOpen, "a space ends the token");
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => committed.Count > 0, "Enter after the space");
            Require(TagEntryParser.Parse(committed[0], snapshot).Items.Select(item => item.Name.Display).SequenceEqual(["in"]) && committed[0].Trim() == "in",
                "in + space + Enter did not add in: " + committed[0]);
            await FreshAsync();
            RawKeyboardInput.Type(window, "in");
            await ListAsync(() => box.HighlightedCandidate?.Value == "innocent", "in preselects innocent");
            RawKeyboardInput.Press(window, Key.Down);
            await ListAsync(() => box.HighlightedCandidate?.Source == TagSuggestionIndex.CreateGroup, "Down reaches the New tag row");
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => committed.Count > 0, "Enter on the New tag row");
            Require(committed.SequenceEqual(["in"]), "Down + Enter on the New tag row did not add in: " + string.Join("|", committed));
            await FreshAsync();
            RawKeyboardInput.Type(window, "in");
            await ListAsync(() => box.HighlightedCandidate?.Value == "innocent", "in preselects innocent");
            RawKeyboardInput.Type(window, ",");
            await ListAsync(() => committed.Count > 0, "a comma after in");
            Require(committed.SequenceEqual(["in"]), "in + comma did not add in: " + string.Join("|", committed));

            // Several matches: nothing is chosen for the user, Enter adds the typed text.
            await FreshAsync();
            RawKeyboardInput.Type(window, "cat");
            await ListAsync(() => Values(box).Contains("cats") && Values(box).Contains("catgirl"), "cat lists both");
            Require(box.HighlightedCandidate is null || box.HighlightedCandidate.Source == TagSuggestionIndex.CreateGroup,
                "With two matches one was highlighted: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => committed.Count > 0, "Enter with two matches");
            Require(committed.SequenceEqual(["cat"]), "cat + Enter did not add the typed text: " + string.Join("|", committed));

            // TG-03: the caret moved into a middle token; the list follows it and accepting replaces only that token.
            await FreshAsync();
            RawKeyboardInput.Type(window, "hot inn blonde");
            await ListAsync(() => box.Text == "hot inn blonde" && Editor(box).CaretIndex == 14, "typed the draft");
            RawKeyboardInput.Press(window, Key.Left, times: 7);
            await ListAsync(() => Values(box).SequenceEqual(["hot innocent blonde"]) && box.HighlightedCandidate?.Value == "hot innocent blonde",
                "the list follows the caret into inn");
            Require(Editor(box).CaretIndex == 7, "The list moved the caret: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => box.Text == "hot innocent blonde" && !box.IsDropDownOpen, "Enter completes inn in place");
            Require(committed.Count == 0 && Editor(box).CaretIndex == 12, "Completing a middle token added or moved wrongly: " + Observed(box)
                + ", added " + string.Join("|", committed));
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => committed.Count > 0, "the second Enter adds the draft");
            Require(committed.SequenceEqual(["hot innocent blonde"]), "The completed draft was not added as typed: " + string.Join("|", committed));

            // Up/Down choose without moving the caret out of the token.
            await FreshAsync();
            RawKeyboardInput.Type(window, "hot cat blonde");
            RawKeyboardInput.Press(window, Key.Left, times: 7);
            await ListAsync(() => Values(box).Order().SequenceEqual(["hot catgirl blonde", "hot cats blonde"]), "the list follows the caret into cat");
            RawKeyboardInput.Press(window, Key.Down);
            await ListAsync(() => box.HighlightedCandidate?.Value == Values(box)[0], "Down highlights the first row");
            string chosen = box.HighlightedCandidate!.Value;
            Require(Editor(box).CaretIndex == 7, "Down moved the caret: " + Observed(box));
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => box.Text == chosen && Editor(box).CaretIndex == chosen.Length - " blonde".Length, "the chosen row replaces only cat");
            Require(committed.Count == 0, "Completing a middle token added the draft: " + string.Join("|", committed));

            // Home moves the list to the first token, End back to the last one; a selection over two tokens has no list.
            await FreshAsync();
            RawKeyboardInput.Type(window, "ho inn");
            await ListAsync(() => Values(box).SequenceEqual(["ho innocent"]), "ho inn lists innocent");
            RawKeyboardInput.Press(window, Key.Home);
            await ListAsync(() => Values(box).SequenceEqual(["hot inn"]) && box.HighlightedCandidate?.Value == "hot inn", "Home lists hot for the first token");
            RawKeyboardInput.Press(window, Key.End);
            await ListAsync(() => Values(box).SequenceEqual(["ho innocent"]) && box.IsDropDownOpen, "End lists innocent again");
            RawKeyboardInput.Press(window, Key.Home, RawInputModifiers.Shift);
            await ListAsync(() => !box.IsDropDownOpen, "a selection over both tokens closes the list");

            // TG-04: a name with spaces replaces the words already typed and is offered once.
            await FreshAsync();
            RawKeyboardInput.Type(window, "hot big ti");
            await ListAsync(() => Values(box).SequenceEqual(["hot \"big tits\""]), "hot big ti offers one big tits");
            RawKeyboardInput.Press(window, Key.Enter);
            await ListAsync(() => committed.Count > 0, "Enter on big tits");
            Require(committed.SequenceEqual(["hot \"big tits\""]), "hot big ti + Enter added " + string.Join("|", committed));
            Console.WriteLine("SMOKE private-tags caret entry [en]: platform keys/text into a real tag field; inn+Enter takes innocent (highlighted, list selection), "
                + "in + space/New tag row/comma add in, two matches keep the typed text, the list follows the caret into a middle token and completes only it, "
                + "Down keeps the caret, Home/End/selection, hot big ti → hot \"big tits\".");
        }
        finally
        {
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    // The same in the reader tag bar of the archive page: inn + Enter takes the own tag innocent, and a middle token is completed in
    // place before the second Enter adds the whole draft. The vocabulary is left as the journey had it.
    private static async Task CaretAutocompleteAsync(Fixture f, IdentitySuggestionBox input)
    {
        int tags = f.Stored().Tags.Count;
        async Task ClearChipsAsync()
        {
            for (int round = 0; round < 8 && f.C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().FirstOrDefault() is { } chip; round++)
            {
                f.Click(chip);
                await f.SettleAsync();
            }
        }
        input.Focus();
        input.Text = "innocent hot blonde";
        f.Click("ReaderTagAdd");
        await f.SettleAsync();
        Require(f.Chips().Order().SequenceEqual(["blonde", "hot", "innocent"]), "The caret fixture tags were not added: " + string.Join("|", f.Chips()));
        await ClearChipsAsync();
        input.Focus();
        await Program.WaitUntilAsync(() => Editor(input).IsKeyboardFocusWithin, () => "The reader tag field did not take the focus.");

        RawKeyboardInput.Type(f.Window, "inn");
        await Program.WaitUntilAsync(() => input.HighlightedCandidate?.Value == "innocent", () => "inn did not preselect innocent: " + Observed(input));
        RawKeyboardInput.Press(f.Window, Key.Enter);
        await Program.WaitUntilAsync(() => f.Chips().Length > 0, () => "Enter added nothing: " + Observed(input));
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(["innocent"]) && f.Stored().Tags.Count == tags + 3 && string.IsNullOrEmpty(input.Text),
            "TG-01 in the reader bar: inn + Enter gave " + string.Join("|", f.Chips()) + " with " + f.Stored().Tags.Count + " tags.");
        await ClearChipsAsync();

        input.Focus();
        RawKeyboardInput.Type(f.Window, "hot inn blonde");
        await Program.WaitUntilAsync(() => input.Text == "hot inn blonde", () => Observed(input));
        RawKeyboardInput.Press(f.Window, Key.Left, times: 7);
        await Program.WaitUntilAsync(() => input.HighlightedCandidate?.Value == "hot innocent blonde",
            () => "The reader list did not follow the caret into inn: " + Observed(input));
        RawKeyboardInput.Press(f.Window, Key.Enter);
        await Program.WaitUntilAsync(() => input.Text == "hot innocent blonde" && Editor(input).CaretIndex == 12, () => Observed(input));
        Require(f.Chips().Length == 0, "Completing a middle token already added tags: " + string.Join("|", f.Chips()));
        RawKeyboardInput.Press(f.Window, Key.Enter);
        await Program.WaitUntilAsync(() => f.Chips().Length == 3, () => "The second Enter did not add the draft: " + Observed(input));
        await f.SettleAsync();
        Require(f.Chips().Order().SequenceEqual(["blonde", "hot", "innocent"]) && f.Stored().Tags.Count == tags + 3,
            "The completed draft gave " + string.Join("|", f.Chips()));

        await ClearChipsAsync();
        foreach (string name in new[] { "innocent", "hot", "blonde" })
        {
            PrivateTagStoreSnapshot current = f.Stored();
            await f.Host.DeletePrivateTagAsync(current.Tags.Single(tag => tag.Name.Display == name).Id, current.Revision).WaitAsync(Deadline);
            await f.SettleAsync();
        }
        Require(f.Stored().Tags.Count == tags && f.Chips().Length == 0, "The caret fixture left tags behind.");
        Console.WriteLine("SMOKE private-tags caret bar [en]: platform keys into the reader tag bar; inn+Enter adds innocent, a middle token is completed in place and the second Enter adds all three.");
    }
}
