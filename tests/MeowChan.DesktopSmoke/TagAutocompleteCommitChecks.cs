using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// XTest mouse clicks are deliberately separated by the dispatcher in MediaNativeInput: raising press/release
// back-to-back misses the posted QuickTagSurface focus reclaim that used to cancel the captured candidate.
// Real Ursa template and QuickTagSession/store; Enter/Tab are routed keys, not OS keyboard/IME certification.
internal static class TagAutocompleteCommitChecks
{
    private sealed record Case(string Draft, string Value, string[] Names, int? Caret = null, bool InPlace = false);

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "autocomplete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            PrivateTagStore store = new(profile, lease);
            foreach (string name in new[] { "buttplug", "buttplugs", "innocent", "big tits", "Smith, John" }) store.Define(name);
            TagId target = store.Define("piss").Tag, alias = store.Define("pee").Tag;
            store.Merge(target, [alias]);
            MediaLibraryEntry entry = new(new ResourceId("s", "b", "1"), "Test", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "1", 1, null, 0,
                "att-v1:" + new string('a', 64), "/synthetic", "asset", new string('a', 64), 1, "m1", "original", "f.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
            var subject = MediaLibraryFilterEngine.TagTarget(entry)!;
            MediaTagSession media = new(new MediaTagSubject.Bound(subject),
                () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
                (name, file, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [file], null, token)),
                (id, file, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [file], assigned, null, token)));
            using QuickTagSession session = new(QuickTagTarget.For(media), undoDepth: 100);
            using QuickTagSurface surface = new(session, new QuickTagOptions { UndoDepth = 100 });
            IdentitySuggestionBox field = surface.Input;
            // Supply a deterministic offline source index through the same token-aware provider as Attach.
            TagSuggestionIndex sources = TagSuggestionIndex.FromSourceTags([("source_tag", 1)]);
            field.Candidates = (text, _) =>
            {
                (int start, int end) = field.EditorSelection;
                return Task.FromResult(TagSuggestionIndex.CombineEntry(store.ReadSnapshot(), sources, text, session.OnAll(),
                    selectionStart: start, selectionEnd: end));
            };
            Window host = new() { Width = 640, Height = 600, Content = surface };
            host.Show(owner);
            using MediaNativeInput input = new(host);
            int passed = 0;
            try
            {
                Case[] cases =
                [
                    new("buttplu", "buttplug", ["buttplug"]), // ambiguous; exact create row initially highlighted
                    new("innoc", "innocent", ["innocent"]), // automatically preselected real row
                    new("pee", "piss", ["piss"]),
                    new("sour", "source_tag", ["source_tag"]),
                    new("novel", "novel", ["novel"]),
                    new("hot big ti", "hot \"big tits\"", ["hot", "big tits"]),
                    new("hot in cool", "hot innocent cool", [], Caret: 6, InPlace: true),
                    new("\"big ti\" cool", "\"big tits\" cool", [], Caret: 7, InPlace: true)
                ];
                foreach (Case test in cases)
                foreach (string gesture in new[] { "click", "highlight-click", "Enter" })
                {
                    TextBox editor = await Draft(test.Draft, test.Caret);
                    SelectingItemsControl list = List(field);
                    IdentitySuggestion row = field.ItemsSource!.Cast<IdentitySuggestion>().Single(item => item.Value == test.Value);
                    Require(row.InPlace == test.InPlace, "Unexpected token completion mode.");
                    if (gesture != "click") list.SelectedItem = row;
                    int accepted = 0, undo = session.UndoSteps.Count;
                    void Accepted(IdentitySuggestion item) { Require(ReferenceEquals(item, row), "Accepted the draft/create row instead of the chosen suggestion."); accepted++; }
                    field.CandidateAccepted += Accepted;
                    try
                    {
                        if (gesture == "Enter") Press(editor, Key.Enter);
                        else await input.ClickAsync(list.ContainerFromItem(row)!);
                        await Settle();
                        if (!test.InPlace) await Program.WaitUntilAsync(() => session.Ready && field.Text == "");
                        Require(accepted == 1 && !field.IsDropDownOpen, "Suggestion must be accepted once and close the list.");
                        Require(store.ReadSnapshot().TagsOf(subject).Select(tag => tag.Name.Display).Order().SequenceEqual(test.Names.Order()), "Wrong persisted names.");
                        Require(session.UndoSteps.Count == undo + (test.InPlace ? 0 : 1), "Acceptance wrote zero or multiple tag operations.");
                        if (test.InPlace) Require(field.Text == test.Value && editor.CaretIndex == row.Caret, "Middle-token text/caret changed.");
                        Console.WriteLine($"SMOKE tag-autocomplete-commit PASS {gesture}: {test.Draft} -> {test.Value}, inPlace={test.InPlace}");
                        passed++;
                    }
                    finally { field.CandidateAccepted -= Accepted; }
                    await Clear();
                }
                // Tab edits only, then Enter commits the completed draft.
                TextBox tabEditor = await Draft("buttplu");
                List(field).SelectedItem = field.ItemsSource!.Cast<IdentitySuggestion>().First(item => item.Value == "buttplug");
                Press(tabEditor, Key.Tab);
                await Settle();
                Require(field.Text == "buttplug" && !store.ReadSnapshot().TagsOf(subject).Any(), "Tab must complete without writing.");
                Press(tabEditor, Key.Enter);
                await Program.WaitUntilAsync(() => session.Ready && field.Text == "");
                Require(store.ReadSnapshot().TagsOf(subject).Single().Name.Display == "buttplug", "Enter after Tab wrote the prefix.");
                await Clear();
                // Comma must still ignore an automatic preselection, but take an explicitly highlighted row.
                foreach (bool highlight in new[] { false, true })
                {
                    TextBox editor = await Draft("innoc");
                    if (highlight) List(field).SelectedItem = field.ItemsSource!.Cast<IdentitySuggestion>().First(item => item.Value == "innocent");
                    if (highlight) Press(editor, Key.Down); // explicit navigation ends the automatic preselection
                    if (highlight) List(field).SelectedItem = field.ItemsSource!.Cast<IdentitySuggestion>().First(item => item.Value == "innocent");
                    editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "," });
                    await Program.WaitUntilAsync(() => session.Ready && field.Text == "");
                    Require(store.ReadSnapshot().TagsOf(subject).Single().Name.Display == (highlight ? "innocent" : "innoc"), "Comma acceptance changed.");
                    await Clear();
                }
                TextBox commaEditor = await Draft("Smith");
                TextInputEventArgs comma = new() { RoutedEvent = InputElement.TextInputEvent, Text = "," };
                commaEditor.RaiseEvent(comma);
                // TextBox handles the event when it inserts the comma; assert the editor outcome, not an unhandled event.
                await Settle();
                Require(field.Text == "Smith," && !store.ReadSnapshot().TagsOf(subject).Any(), "Comma inside an existing tag name must remain typeable.");
                await Clear();
                // The comma test intentionally created "innoc"; use a fresh prefix with one real match.
                TextBox imeEditor = await Draft("innoce");
                Require(field.HighlightedCandidate?.Value == "innocent", "The IME fixture did not highlight its suggestion.");
                var presenter = imeEditor.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
                int beforeComposition = session.UndoSteps.Count;
                presenter.PreeditText = "未";
                try
                {
                    Require(field.IsComposing, "The composition guard did not observe the preedit.");
                    Press(imeEditor, Key.Enter);
                    await Settle();
                    Require(field.Text == "innoce" && field.HighlightedCandidate?.Value == "innocent"
                        && !store.ReadSnapshot().TagsOf(subject).Any() && session.UndoSteps.Count == beforeComposition,
                        "Enter during composition accepted a suggestion or committed the draft.");
                }
                finally { presenter.PreeditText = null; }
                Press(imeEditor, Key.Enter);
                await Program.WaitUntilAsync(() => session.Ready && field.Text == "");
                Require(store.ReadSnapshot().TagsOf(subject).Single().Name.Display == "innocent", "Enter after composition failed to accept the suggestion.");
                Console.WriteLine($"SMOKE tag-autocomplete-commit PASS: {passed} click/Enter cases, Tab then Enter, preselected/highlighted comma, comma continuation and composing Enter guard; no native IME certification.");

                async Task<TextBox> Draft(string text, int? caret = null)
                {
                    field.Focus();
                    await Settle();
                    TextBox editor = field.GetVisualDescendants().OfType<TextBox>().First();
                    editor.Text = "";
                    await Settle();
                    editor.Text = text;
                    await Program.WaitUntilAsync(() => field.Text == text && field.SearchText == text);
                    editor.ClearSelection(); editor.CaretIndex = caret ?? text.Length;
                    field.RestartSuggestions();
                    await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().Any() == true);
                    await Settle();
                    return editor;
                }
                async Task Clear()
                {
                    foreach (TagId id in store.ReadSnapshot().TagsOf(subject).Select(tag => tag.Id).ToArray()) await session.ToggleAsync(id);
                    field.Text = ""; field.Dismiss();
                    await Settle();
                }
            }
            finally { host.Close(); }
        }
        finally { Directory.Delete(profile, true); }
    }

    private static SelectingItemsControl List(IdentitySuggestionBox field)
    {
        Popup popup = field.GetLogicalDescendants().OfType<Popup>().Concat(field.GetVisualDescendants().OfType<Popup>()).Distinct().Single(p => p.IsOpen);
        return popup.Child!.GetVisualDescendants().OfType<SelectingItemsControl>().Prepend(popup.Child as SelectingItemsControl).OfType<SelectingItemsControl>().First();
    }
    private static void Press(Control editor, Key key) => editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(100); }
}
