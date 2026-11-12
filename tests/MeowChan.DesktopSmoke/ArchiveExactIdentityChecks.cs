using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Four distinct authoritative legacy identities. No production reflection, SQL or new API is needed.
internal static class ArchiveExactIdentityChecks
{
    internal static async Task RunAsync(Window owner)
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-exact-identities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ArchivePage? page = null;
        Window? host = null;
        string originalLanguage = Texts.Language;
        List<string> failures = [];
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            foreach (string source in new[] { "A", " A " })
                foreach (string board in new[] { "x", " x " })
                {
                    int number = (source == "A" ? 0 : 2) + (board == "x" ? 0 : 1);
                    await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId(source, board, "thread"),
                        "https://exact-identity.fixture/" + number, "exact-" + number, directory, true));
                }
            page = new ArchivePage(token => ArchiveReader.OpenAsync(directory, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            host = new Window { Title = "Archive exact identity regression — harness", Width = 900, Height = 700, Content = page };
            host.Show(owner);
            C<Expander>("ArchiveFilters").IsExpanded = true;
            await Settle();

            T C<T>(string name) where T : Control => page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
            ArchiveThreadRow[] Rows() => (C<ListBox>("ArchiveThreads").ItemsSource?.Cast<ArchiveThreadRow>() ?? []).ToArray();
            void Observe(string scenario, string step, string? error = null) => Console.WriteLine("SMOKE archive exact-identity: " + JsonSerializer.Serialize(new
            {
                scenario, step, error, input = "in-process materialized controls, popup keyboard events and commands; not OS input",
                language = Texts.Language, applied = page.AppliedSearchSpec == null ? null : ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec),
                page.HasUnappliedSearchDraft, rows = Rows().Select(row => new { row.Summary.Id.Source, row.Summary.Id.Board, row.Summary.Id.Thread }),
                searchError = C<TextBlock>("ArchiveSearchError").Text, busy = page.IsBusy,
                sourceRaw = C<IdentitySuggestionBox>("ArchiveSourceFilter").Text,
                boardRaw = C<IdentitySuggestionBox>("ArchiveBoardFilter").Text,
                sourceAccepted = page.TryReadSelectedIdentity("ArchiveSourceFilter"),
                boardAccepted = page.TryReadSelectedIdentity("ArchiveBoardFilter"),
                boardScope = C<ComboBox>("ArchiveBoardScope").SelectedItem?.ToString(),
                boardScopeOptions = C<ComboBox>("ArchiveBoardScope").Items.Cast<object>().Select(item => item.ToString()),
                draftSources = C<WrapPanel>("ArchiveDraftSources").Children.OfType<ClosableTag>().Select(tag => (tag.Content as TextBlock)?.Text),
                draftBoards = C<WrapPanel>("ArchiveDraftBoards").Children.OfType<ClosableTag>().Select(tag => (tag.Content as TextBlock)?.Text)
            }));
            async Task Case(string name, Func<Task> body)
            {
                try { await body(); Observe(name, "pass"); }
                catch (Exception error) { Observe(name, "fail", error.ToString()); failures.Add(name + ": " + error.Message); }
            }
            async Task Apply(ArchiveSearchSpec spec)
            {
                Require(await page.ApplySearchSpecAsync(spec), "Definition apply failed.");
                await Settle();
            }
            void Membership(params (string Source, string Board)[] expected)
            {
                var actual = Rows().Select(row => (row.Summary.Id.Source, row.Summary.Id.Board)).OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Board, StringComparer.Ordinal);
                Require(actual.SequenceEqual(expected.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Board, StringComparer.Ordinal)), "Exact result membership changed.");
            }
            async Task Add(string name) { C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); }
            bool Draft(string name, string value, string? source) => name == "ArchiveSourceFilter"
                ? page.DraftSources.Contains(value, StringComparer.Ordinal) : page.DraftBoards.Contains(new ArchiveBoardConstraint(source, value));
            void Scope(string source)
            {
                ComboBox scope = C<ComboBox>("ArchiveBoardScope");
                object[] options = scope.Items.Cast<object>().ToArray();
                object[] exact = options.Where(item => StringComparer.Ordinal.Equals(item.ToString(), source)).ToArray();
                Require(exact.Length == 1, "Expected exact board scope " + JsonSerializer.Serialize(source)
                    + " once; actual options=" + JsonSerializer.Serialize(options.Select(item => item.ToString())));
                scope.SelectedItem = exact[0];
            }
            async Task Type(string name, string value)
            {
                IdentitySuggestionBox field = C<IdentitySuggestionBox>(name);
                field.BringIntoView(); await Settle();
                TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                input.Focus(NavigationMethod.Tab); await Settle();
                input.Text = ""; await Settle();
                // Text replacement alone can leave the TextBox and its presenter at
                // different caret positions. Use the actual input handler, which
                // synchronizes text, insertion caret and selection before Down.
                TextInputEventArgs typing = new() { RoutedEvent = InputElement.TextInputEvent, Text = value };
                input.RaiseEvent(typing); await Settle();
                await Program.WaitUntilAsync(() => input.Text == value && field.Text == value && field.SearchText == value);
                var presenter = input.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
                Require(typing.Handled && input.CaretIndex == value.Length && presenter.CaretIndex == value.Length
                    && input.SelectionStart == value.Length && input.SelectionEnd == value.Length,
                    "Routed text input did not produce a synchronized insertion caret and collapsed selection.");
                Require(field.Text == value, "Materialized input did not reach its identity field.");
            }
            async Task Accept(string name, string value, string? source, bool pointer = false)
            {
                await Type(name, " ");
                IdentitySuggestionBox field = C<IdentitySuggestionBox>(name);
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().SingleOrDefault() is { } candidate
                    && candidate.Value == value && candidate.Source == source);
                TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                void KeyboardState(string step, KeyEventArgs? key = null)
                {
                    Popup? popup = field.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                    SelectingItemsControl? selector = popup?.Child is SelectingItemsControl direct ? direct
                        : popup?.Child?.GetVisualDescendants().OfType<SelectingItemsControl>().FirstOrDefault(item => item.Name == "PART_SelectingItemsControl");
                    Console.WriteLine("SMOKE archive exact-identity-keyboard: " + JsonSerializer.Serialize(new
                    {
                        name, expectedValue = value, expectedSource = source, step,
                        key = key?.Key.ToString(), handled = key?.Handled,
                        rawEditor = input.Text, canonicalText = field.Text, searchText = field.SearchText,
                        fieldSelected = field.SelectedItem as IdentitySuggestion, selectorName = selector?.Name,
                        selectorIndex = selector?.SelectedIndex, selectorSelected = selector?.SelectedItem as IdentitySuggestion,
                        selectorCount = selector?.ItemCount, selectorType = selector?.GetType().FullName,
                        selectorEnabled = selector?.IsEffectivelyEnabled, optionEnabled = selector?.ContainerFromIndex(0)?.IsEffectivelyEnabled,
                        field.IsDropDownOpen, popupOpen = popup?.IsOpen,
                        caretIndex = input.CaretIndex, selectionStart = input.SelectionStart, selectionEnd = input.SelectionEnd,
                        presenterCaretIndex = input.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault()?.CaretIndex,
                        input.IsFocused, input.IsEffectivelyEnabled, field.IsKeyboardFocusWithin,
                        focusedName = (host.FocusManager?.GetFocusedElement() as Control)?.Name,
                        focusedType = host.FocusManager?.GetFocusedElement()?.GetType().Name,
                        acceptedDirect = field.AcceptedCandidate, acceptedCurrent = page.TryReadSelectedIdentity(name),
                        candidates = field.ItemsSource?.Cast<IdentitySuggestion>().ToArray()
                    }));
                }
                KeyboardState("ready-before-down");
                if (pointer)
                {
                    Popup? popup = field.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                    SelectingItemsControl selector = popup?.Child as SelectingItemsControl
                        ?? popup?.Child?.GetVisualDescendants().OfType<SelectingItemsControl>().First(item => item.Name == "PART_SelectingItemsControl")
                        ?? throw new InvalidOperationException("Suggestion list not found.");
                    Control option = selector.ContainerFromIndex(0) ?? throw new InvalidOperationException("Suggestion option not realized.");
                    Visual root = TopLevel.GetTopLevel(option) ?? host;
                    Point at = option.TranslatePoint(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2), root) ?? default;
                    var mouse = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                    option.RaiseEvent(new PointerPressedEventArgs(option, mouse, root, at, 1,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent });
                    await Settle();
                    option.RaiseEvent(new PointerReleasedEventArgs(option, mouse, root, at, 2,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left) { RoutedEvent = InputElement.PointerReleasedEvent });
                    await Settle();
                    // Accepting adds the exact identity (a board with its own source) as a chip, empties the field and keeps focus there.
                    Require(!field.IsDropDownOpen && field.Text == "" && Draft(name, value, source) && field.IsKeyboardFocusWithin,
                        "A clicked exact suggestion was not added as a chip with focus kept in its field.");
                    return;
                }
                KeyEventArgs down = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
                input.RaiseEvent(down); KeyboardState("immediate-after-down", down); await Settle();
                KeyboardState("settled-before-enter", down);
                KeyEventArgs enter = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
                input.RaiseEvent(enter); KeyboardState("immediate-after-enter", enter); await Settle();
                KeyboardState("settled-after-enter", enter);
                Require(enter.Handled && !field.IsDropDownOpen && field.Text == "" && Draft(name, value, source) && field.IsKeyboardFocusWithin,
                    "Shown exact suggestion was not added as a chip on Enter.");
            }

            await Case("definition-draft-reapply", async () =>
            {
                await Apply(Definition([" A "], [new(" A ", " x ")], true));
                Observe("definition-draft-reapply", "after-definition");
                Membership((" A ", " x "));
                Require(!page.HasUnappliedSearchDraft, "Exact saved definition and draft differ.");
                await page.ApplySearchAsync();
                Observe("definition-draft-reapply", "after-draft-apply");
                Membership((" A ", " x "));
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A "]) && page.AppliedSearchSpec.Boards.SequenceEqual([new ArchiveBoardConstraint(" A ", " x ")])
                    && !page.HasUnappliedSearchDraft, "Reading the draft normalized authoritative IDs.");
            });
            await Case("chip-copy-preserves-other-identities", async () =>
            {
                await Apply(Definition([" A "], [new(" A ", " x ")], true));
                Require(await page.RemoveAppliedFilter("capture"), "Capture removal failed.");
                Observe("chip-copy-preserves-other-identities", "after-capture");
                Membership((" A ", " x "));
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A "]) && !page.HasUnappliedSearchDraft, "CopySearch normalized unrelated identities.");
                string boardKey = ArchivePage.AppliedFilterLabels(page.AppliedSearchSpec).Single(item => item.Field == "board").Value!;
                Require(await page.RemoveAppliedFilter("board", boardKey), "Board removal failed.");
                Observe("chip-copy-preserves-other-identities", "after-board");
                Membership((" A ", "x"), (" A ", " x "));
                Require(page.AppliedSearchSpec.Sources.SequenceEqual([" A "]) && !page.HasUnappliedSearchDraft, "Board removal broadened the source constraint.");
            });
            await Case("source-category-distinguishes-padded-draft", async () =>
            {
                await Apply(Definition([" A "], []));
                C<WrapPanel>("ArchiveDraftSources").Children.OfType<ClosableTag>().Single().Command!.Execute(null);
                await Type("ArchiveSourceFilter", "A"); await Add("ArchiveAddSource");
                Require(await page.RemoveAppliedFilter("source", " A "), "Source chip removal failed.");
                Observe("source-category-distinguishes-padded-draft", "after-chip");
                Membership(("A", "x"), ("A", " x "), (" A ", "x"), (" A ", " x "));
                Require(page.HasUnappliedSearchDraft, "Distinct unsaved source draft was cleared as a matching padded category.");
                await page.ApplySearchAsync(); Membership(("A", "x"), ("A", " x "));
            });
            await Case("board-category-distinguishes-padded-draft", async () =>
            {
                await Apply(Definition(["A"], [new("A", " x ")]));
                C<WrapPanel>("ArchiveDraftBoards").Children.OfType<ClosableTag>().Single().Command!.Execute(null);
                Scope("A"); await Type("ArchiveBoardFilter", "x"); await Add("ArchiveAddBoard");
                string key = ArchivePage.AppliedFilterLabels(page.AppliedSearchSpec!).Single(item => item.Field == "board").Value!;
                Require(await page.RemoveAppliedFilter("board", key), "Padded board removal failed.");
                Observe("board-category-distinguishes-padded-draft", "after-chip");
                Membership(("A", "x"), ("A", " x "));
                Require(page.HasUnappliedSearchDraft, "Distinct unsaved board draft was cleared as a matching padded category.");
                await page.ApplySearchAsync(); Membership(("A", "x"));
            });
            await Case("accepted-source-and-board-stay-exact-across-language", async () =>
            {
                await Apply(Definition([], []));
                await Accept("ArchiveSourceFilter", " A ", null, pointer: true);
                Observe("accepted-source-and-board-stay-exact-across-language", "after-source-accepted");
                Scope(" A ");
                await Accept("ArchiveBoardFilter", " x ", " A ");
                string languageBefore = Texts.Language;
                Texts.SetLanguage(languageBefore == "en" ? "de" : "en"); await Settle();
                Observe("accepted-source-and-board-stay-exact-across-language", "after-language");
                Require(page.DraftBoards.SequenceEqual([new ArchiveBoardConstraint(" A ", " x ")]), "Language-only relabel changed an accepted exact board.");
                await page.ApplySearchAsync();
                Membership((" A ", " x "));
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A "]) && !page.HasUnappliedSearchDraft, "Accepted source or board was trimmed.");
                Texts.SetLanguage(languageBefore); await Settle();
            });
            await Case("explicit-scope-change-revokes-previous-board-qualification", async () =>
            {
                await Apply(Definition(["A", " A "], []));
                Observe("explicit-scope-change-revokes-previous-board-qualification", "after-definition-before-scope");
                Scope(" A ");
                await Accept("ArchiveBoardFilter", " x ", " A "); Scope("A");
                Observe("explicit-scope-change-revokes-previous-board-qualification", "after-scope-change");
                // The accepted chip keeps its own source; free text typed afterwards takes the new scope.
                await Type("ArchiveBoardFilter", "x "); await Add("ArchiveAddBoard"); await page.ApplySearchAsync();
                Membership((" A ", " x "), ("A", "x"));
                Require(page.AppliedSearchSpec!.Boards.SequenceEqual([new ArchiveBoardConstraint(" A ", " x "), new ArchiveBoardConstraint("A", "x")]),
                    "Accepted board lost its source or free board did not use its new explicit scope.");
            });
            await Case("new-free-input-trims-without-normalizing-existing-identities", async () =>
            {
                await Apply(Definition([" A "], []));
                // "A " and "x " match no stored identity, so they stay free text and are trimmed.
                await Type("ArchiveSourceFilter", "A ");
                Require(page.TryReadSelectedIdentity("ArchiveSourceFilter") == null, "Merely typing accepted a suggestion.");
                await Add("ArchiveAddSource"); Scope("A");
                await Type("ArchiveBoardFilter", "x "); await Add("ArchiveAddBoard");
                await Program.WaitUntilAsync(() => !page.IsBusy);
                await page.ApplySearchAsync(); Observe("new-free-input-trims-without-normalizing-existing-identities", "after-apply");
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A ", "A"]) && page.AppliedSearchSpec.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]),
                    "Adding free text normalized an existing identity or stopped trimming new free text.");
                Membership(("A", "x"));
            });
            // ---- Suggestion keyboard navigation regressions (selection never searches; free text stays authoritative) ----
            SelectingItemsControl? Selector(IdentitySuggestionBox field)
            {
                Popup? popup = field.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                return popup?.Child is SelectingItemsControl direct ? direct
                    : popup?.Child?.GetVisualDescendants().OfType<SelectingItemsControl>().FirstOrDefault(item => item.Name == "PART_SelectingItemsControl");
            }
            async Task<(IdentitySuggestionBox Field, TextBox Input)> OpenList(string name, int expected)
            {
                IdentitySuggestionBox field = C<IdentitySuggestionBox>(name);
                field.BringIntoView(); await Settle();
                TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                input.Focus(NavigationMethod.Tab); await Settle();
                input.Text = ""; await Settle();
                field.RestartSuggestions();
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().Count() == expected);
                return (field, input);
            }
            async Task<KeyEventArgs> PressKey(TextBox input, Key key)
            {
                KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key };
                input.RaiseEvent(args); await Settle(); return args;
            }
            void NoSearchHappened(ArchiveSearchSpec? appliedBefore, string[] rowsBefore, string step)
            {
                Require(ReferenceEquals(page.AppliedSearchSpec, appliedBefore)
                    && Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).SequenceEqual(rowsBefore), step + ": selection started a search.");
            }
            await Case("keyboard-multi-navigation-then-enter", async () =>
            {
                await Apply(Definition([], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec; string[] rows = Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).ToArray();
                var (field, input) = await OpenList("ArchiveSourceFilter", 2);
                var list = field.ItemsSource!.Cast<IdentitySuggestion>().Select(x => x.Value).ToArray();
                Require(list.SequenceEqual([" A ", "A"]), "Unexpected source candidates: " + JsonSerializer.Serialize(list));
                foreach (var (key, index) in new[] { (Avalonia.Input.Key.Down, 0), (Avalonia.Input.Key.Down, 1), (Avalonia.Input.Key.Up, 0), (Avalonia.Input.Key.Down, 1) })
                {
                    KeyEventArgs pressed = await PressKey(input, key);
                    Require(pressed.Handled && field.IsDropDownOpen && Selector(field)?.SelectedIndex == index,
                        key + " did not move the visible highlight to " + index + "; actual=" + Selector(field)?.SelectedIndex);
                    Require(field.Text == "" && field.AcceptedCandidate == null, "Navigation rewrote the draft or accepted a candidate.");
                }
                NoSearchHappened(applied, rows, "navigation");
                KeyEventArgs enter = await PressKey(input, Avalonia.Input.Key.Enter);
                Require(enter.Handled && !field.IsDropDownOpen && field.Text == "" && page.DraftSources.SequenceEqual(["A"]) && page.HasUnappliedSearchDraft,
                    "Enter did not add the highlighted second candidate to the unapplied draft.");
                NoSearchHappened(applied, rows, "enter");
                await page.ApplySearchAsync();
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual(["A"]), "Search did not use the accepted source.");
            });
            await Case("escape-then-navigation-reopens-list", async () =>
            {
                await Apply(Definition([], []));
                var (field, input) = await OpenList("ArchiveSourceFilter", 2);
                await PressKey(input, Avalonia.Input.Key.Down);
                KeyEventArgs escape = await PressKey(input, Avalonia.Input.Key.Escape);
                Require(escape.Handled && !field.IsDropDownOpen && field.Text == "" && field.AcceptedCandidate == null, "Escape accepted or kept the list.");
                // Enter on the empty field with a closed list searches and adds nothing.
                ArchiveSearchSpec? beforeEnter = page.AppliedSearchSpec;
                KeyEventArgs enterClosed = await PressKey(input, Avalonia.Input.Key.Enter);
                await Program.WaitUntilAsync(() => !page.IsBusy);
                Require(field.AcceptedCandidate == null && field.Text == "" && page.DraftSources.Count == 0 && !ReferenceEquals(beforeEnter, page.AppliedSearchSpec)
                    && string.IsNullOrEmpty(C<TextBlock>("ArchiveSearchError").Text), "Enter on an empty closed field did not search cleanly or added a candidate.");
                KeyEventArgs down = await PressKey(input, Avalonia.Input.Key.Down);
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().Count() == 2);
                Require(down.Handled && field.Text == "" && field.AcceptedCandidate == null, "Reopening changed the draft.");
                await PressKey(input, Avalonia.Input.Key.Down);
                Require(Selector(field)?.SelectedIndex == 0, "Navigation after reopen did not highlight the first candidate.");
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Enter);
                Require(field.Text == "" && page.DraftSources.SequenceEqual(["A"]), "Candidate after Escape reopen was not added.");
                // Scenario 8: arrows through the list, then Escape: text and chips stay as they were.
                await PressKey(input, Avalonia.Input.Key.Down);
                await Program.WaitUntilAsync(() => field.IsDropDownOpen);
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Down);
                await PressKey(input, Avalonia.Input.Key.Escape);
                Require(field.Text == "" && !field.IsDropDownOpen && page.DraftSources.SequenceEqual(["A"]), "Navigation and Escape changed the text or the chips.");
            });
            await Case("board-candidate-add-then-explicit-search", async () =>
            {
                await Apply(Definition(["A", " A "], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec; string[] rows = Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).ToArray();
                Scope("A");
                var (field, input) = await OpenList("ArchiveBoardFilter", 2);
                var list = field.ItemsSource!.Cast<IdentitySuggestion>().ToArray();
                Require(list.All(x => x.Source == "A") && list.Select(x => x.Value).SequenceEqual([" x ", "x"]), "Board candidates are not scoped to source A: " + JsonSerializer.Serialize(list));
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Down);
                await PressKey(input, Avalonia.Input.Key.Enter);
                Require(page.ReadIdentityEditor("ArchiveBoardFilter") == "" && page.DraftBoards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]) && page.HasUnappliedSearchDraft,
                    "Board candidate was not added with its source to the unapplied draft.");
                NoSearchHappened(applied, rows, "board enter");
                await page.ApplySearchAsync();
                Membership(("A", "x"));
                Require(page.AppliedSearchSpec!.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]), "Search did not receive the qualified board.");
            });
            await Case("multiple-accepted-candidates-collect-in-draft-then-one-search", async () =>
            {
                await Apply(Definition([], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec; string[] rows = Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).ToArray();
                var (field, input) = await OpenList("ArchiveSourceFilter", 2);
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Enter);
                Require(page.DraftSources.SequenceEqual([" A "]), "First highlighted source was not added.");
                (field, input) = await OpenList("ArchiveSourceFilter", 2);
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Enter);
                Require(page.DraftSources.SequenceEqual([" A ", "A"]), "Second highlighted source was not added.");
                NoSearchHappened(applied, rows, "two source accepts");
                Scope("A");
                (field, input) = await OpenList("ArchiveBoardFilter", 2);
                await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Down); await PressKey(input, Avalonia.Input.Key.Enter);
                NoSearchHappened(applied, rows, "board accept after sources");
                Require(page.HasUnappliedSearchDraft, "Accepted candidates did not collect in the unapplied draft.");
                await page.ApplySearchAsync();
                Require(page.AppliedSearchSpec!.Sources.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual([" A ", "A"])
                    && page.AppliedSearchSpec.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]),
                    "One explicit search did not receive every accepted candidate: " + ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec));
                Membership(("A", "x"));
            });
            await Case("stale-async-suggestions-never-resurface", async () =>
            {
                var (field, input) = await OpenList("ArchiveSourceFilter", 2);
                var original = field.Candidates;
                var pending = new List<TaskCompletionSource<IdentitySuggestion[]>>();
                bool immediate = false;
                try
                {
                    field.Candidates = (text, token) =>
                    {
                        if (immediate) return Task.FromResult(new[] { new IdentitySuggestion("fresh-" + text, null, "fresh-" + text) });
                        var tcs = new TaskCompletionSource<IdentitySuggestion[]>(); pending.Add(tcs); return tcs.Task;
                    };
                    field.RestartSuggestions(); await Settle();
                    Require(pending.Count >= 1 && !field.IsDropDownOpen, "Pending request opened the list early.");
                    immediate = true; input.Text = "b"; await Settle();
                    await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().Single().Value == "fresh-b");
                    foreach (var tcs in pending) tcs.TrySetResult([new IdentitySuggestion("stale", null, "stale")]);
                    await Settle();
                    Require(field.ItemsSource?.Cast<IdentitySuggestion>().Select(x => x.Value).SequenceEqual(["fresh-b"]) == true, "Stale response replaced newer suggestions.");
                    immediate = false; pending.Clear();
                    await PressKey(input, Avalonia.Input.Key.Escape);
                    await PressKey(input, Avalonia.Input.Key.Down);
                    Require(pending.Count == 1 && !field.IsDropDownOpen, "Reopen did not issue exactly one pending request.");
                    await PressKey(input, Avalonia.Input.Key.Escape);
                    pending[0].TrySetResult([new IdentitySuggestion("late", null, "late")]); await Settle();
                    Require(!field.IsDropDownOpen && field.Text == "b" && field.AcceptedCandidate == null, "A response after Escape reopened the list or changed the draft.");
                }
                finally { field.Candidates = original; field.Dismiss(); input.Text = ""; }
            });
            await Case("draft-tag-removal-continues-focus-locally", async () =>
            {
                string? appliedBefore = page.AppliedSearchSpec == null ? null : ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec);
                ComboBox state = C<ComboBox>("ArchiveStateFilter");
                for (int choice = 1; choice <= 3; choice++) { state.SelectedIndex = choice; await Settle(); }
                WrapPanel tags = C<WrapPanel>("ArchiveDraftStates");
                Require(tags.Children.Count == 3, "Three draft state tags expected.");
                IInputElement? Focused() => TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement();
                async Task Remove(int index, Key key)
                {
                    Control tag = tags.Children[index];
                    tag.Focus(NavigationMethod.Tab); await Settle();
                    Require(tag.IsKeyboardFocusWithin, "Draft tag did not take keyboard focus.");
                    tag.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = tag });
                    await Settle();
                }
                await Remove(1, Key.Space); // middle: the tag that followed it takes over
                Require(tags.Children.Count == 2 && ReferenceEquals(Focused(), tags.Children[1]), "Focus did not move to the following draft tag.");
                await Remove(1, Key.Enter); // last: the remaining tag of the same category
                Require(tags.Children.Count == 1 && ReferenceEquals(Focused(), tags.Children[0]), "Focus did not move to the remaining draft tag.");
                await Remove(0, Key.Enter); // only tag: its own editor
                Require(tags.Children.Count == 0 && ReferenceEquals(Focused(), state), "Focus did not return to the state editor.");
                string? appliedAfter = page.AppliedSearchSpec == null ? null : ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec);
                Require(appliedBefore == appliedAfter, "Removing a draft tag applied a search.");
                // A pointer-style removal from a tag that is not focused must not take focus from another control.
                state.SelectedIndex = 1; await Settle();
                Control other = C<IdentitySuggestionBox>("ArchiveSourceFilter").GetVisualDescendants().OfType<TextBox>().First();
                other.Focus(NavigationMethod.Tab); await Settle();
                ((ClosableTag)tags.Children[0]).Command!.Execute(null); await Settle();
                Require(tags.Children.Count == 0 && other.IsKeyboardFocusWithin, "An unfocused tag removal stole focus.");
            });
            // ---- UX filter input (spec UX-ARCHIVFILTER-EINGABE): accepting adds, search never fails on resolvable input ----
            void Shot(string name)
            {
                if (Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_OUTPUT") is not { } output || !Directory.Exists(output)) return;
                Control surface = C<Border>("ArchiveFilterSurface");
                double scale = host.RenderScaling;
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(host.Bounds.Width * scale)),
                    Math.Max(1, (int)Math.Ceiling(host.Bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
                bitmap.Render(host); bitmap.Save(Path.Combine(output, "filter-input-" + name + ".png"));
                Console.WriteLine("SMOKE archive filter-input capture: " + JsonSerializer.Serialize(new { name, surface = surface.Bounds.ToString() }));
            }
            bool TopErrorShown() => C<TextBlock>("ArchiveSearchError") is { IsVisible: true, Text.Length: > 0 };
            await Case("scenario-1-click-adds-chip-without-search", async () =>
            {
                await Apply(Definition([], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec; string[] rows = Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).ToArray();
                await Accept("ArchiveSourceFilter", " A ", null, pointer: true);
                NoSearchHappened(applied, rows, "click");
                Require(page.DraftSources.SequenceEqual([" A "]) && C<WrapPanel>("ArchiveDraftSources").Children.Count == 1, "Clicked suggestion is not a chip.");
                Shot("scenario-1-click");
            });
            await Case("scenario-2-enter-adds-single-candidate-then-enter-searches", async () =>
            {
                await Apply(Definition([], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec; string[] rows = Rows().Select(row => row.Summary.Id.Source + "|" + row.Summary.Id.Board).ToArray();
                // " A" is a prefix of exactly one stored source, the padded legacy " A " (like "4ch" of "4chan").
                // Whitespace alone counts as an empty field, so it is not used as the prefix here.
                await Type("ArchiveSourceFilter", " A");
                IdentitySuggestionBox field = C<IdentitySuggestionBox>("ArchiveSourceFilter");
                TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>().Count() == 1);
                KeyEventArgs enter = await PressKey(input, Key.Enter);
                Require(enter.Handled && field.Text == "" && !field.IsDropDownOpen && page.DraftSources.SequenceEqual([" A "]) && field.IsKeyboardFocusWithin,
                    "Enter without a highlight did not add the only suggestion.");
                NoSearchHappened(applied, rows, "first enter");
                await PressKey(input, Key.Enter); await Program.WaitUntilAsync(() => !page.IsBusy);
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A "]) && !TopErrorShown() && !DataValidationErrors.GetHasErrors(field),
                    "Second Enter on the empty field did not search without error.");
            });
            await Case("scenario-3-search-adds-leftover-text-first", async () =>
            {
                await Apply(Definition([], []));
                async Task Search()
                {
                    C<Button>("SearchArchiveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => !page.IsBusy); await Settle();
                }
                await Type("ArchiveSourceFilter", "A"); await Search();
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual(["A"]) && page.ReadIdentityEditor("ArchiveSourceFilter") == ""
                    && page.DraftSources.SequenceEqual(["A"]) && !TopErrorShown() && !page.HasUnappliedSearchDraft, "Search with leftover source text did not add it first.");
                Membership(("A", "x"), ("A", " x "));
                Scope("A"); await Type("ArchiveBoardFilter", "x"); await Search();
                Require(page.AppliedSearchSpec!.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]) && page.ReadIdentityEditor("ArchiveBoardFilter") == ""
                    && !TopErrorShown() && !page.HasUnappliedSearchDraft, "Search with leftover board text did not add it first.");
                Membership(("A", "x"));
            });
            await Case("scenario-4-unknown-free-value-becomes-chip", async () =>
            {
                await Apply(Definition([], []));
                await Type("ArchiveSourceFilter", " free β' value ");
                TextBox input = C<IdentitySuggestionBox>("ArchiveSourceFilter").GetVisualDescendants().OfType<TextBox>().First();
                await PressKey(input, Key.Enter);
                Require(page.DraftSources.SequenceEqual(["free β' value"]) && page.ReadIdentityEditor("ArchiveSourceFilter") == "", "Unknown free text was not added trimmed.");
                Require(page.HasUnappliedSearchDraft, "A new chip is not an unapplied draft.");
            });
            await Case("scenario-5-invalid-value-one-message-under-field", async () =>
            {
                await Apply(Definition([], []));
                ArchiveSearchSpec? applied = page.AppliedSearchSpec;
                IdentitySuggestionBox field = C<IdentitySuggestionBox>("ArchiveSourceFilter");
                TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                input.Focus(NavigationMethod.Tab); await Settle();
                input.Text = "bad\0value"; await Settle();
                await PressKey(input, Key.Enter);
                Require(page.DraftSources.Count == 0 && field.Text == "bad\0value" && DataValidationErrors.GetHasErrors(field) && !TopErrorShown()
                    && ReferenceEquals(applied, page.AppliedSearchSpec), "Invalid Enter added a chip, lost the text or showed the message twice.");
                string message = DataValidationErrors.GetErrors(field)?.OfType<object>().Single().ToString() ?? "";
                int ShownUnderField() => field.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == message && text.IsEffectivelyVisible);
                Require(message.Length > 0 && ShownUnderField() == 1, "The validation message is not shown once under the field.");
                Shot("scenario-5-invalid-enter");
                // Search with the invalid leftover: nothing searched, still only the field message.
                C<Button>("SearchArchiveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                Require(ReferenceEquals(applied, page.AppliedSearchSpec) && DataValidationErrors.GetHasErrors(field) && ShownUnderField() == 1 && !TopErrorShown() && page.DraftSources.Count == 0,
                    "Search with an invalid leftover searched or repeated the message above the toolbar.");
                // The marker counts the leftover text as unapplied input.
                Require(page.HasUnappliedSearchDraft, "Leftover text is not an unapplied draft.");
                input.Text = ""; await Settle();
                await page.ApplySearchAsync();
                Require(!DataValidationErrors.GetHasErrors(field) && !page.HasUnappliedSearchDraft, "Clearing the invalid text did not clear its message on search.");
            });
            await Case("scenario-6-choosing-state-adds-chip", async () =>
            {
                await Apply(Definition([], []));
                ComboBox state = C<ComboBox>("ArchiveStateFilter");
                Require(page.FindControl<Button>("ArchiveAddState") == null, "The state Add button is still there.");
                state.SelectedIndex = 2; await Settle();
                WrapPanel chips = C<WrapPanel>("ArchiveDraftStates");
                Require(state.SelectedIndex == 0 && chips.Children.OfType<ClosableTag>().Select(tag => (tag.Content as TextBlock)?.Text).SequenceEqual([Texts.T("archive.sourceArchived")]),
                    "Choosing a state did not add its chip and reset the list.");
                Require(Avalonia.Automation.AutomationProperties.GetLiveSetting(chips) == Avalonia.Automation.AutomationLiveSetting.Polite
                    && Avalonia.Automation.AutomationProperties.GetName(chips) == Texts.T("archive.sourceArchived"), "The new state chip is not announced politely.");
                Shot("scenario-6-state");
                // Arrows on the closed list open it instead of adding the next state.
                state.Focus(NavigationMethod.Tab); await Settle();
                KeyEventArgs down = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
                state.RaiseEvent(down); await Settle();
                Require(down.Handled && state.IsDropDownOpen && chips.Children.Count == 1, "Down on the closed state list added a state.");
                state.IsDropDownOpen = false; await Settle();
                Require(chips.Children.Count == 1 && state.SelectedIndex == 0, "Closing the state list unchanged added a state.");
                await page.ApplySearchAsync();
                Require(page.AppliedSearchSpec!.SourceStates.SequenceEqual([ThreadSourceState.Archived]), "Search did not use the state chip.");
            });
            Require(failures.Count == 0, "Exact identity regressions: " + string.Join(" | ", failures));
        }
        finally
        {
            page?.EndLifetime(); host?.Close(); Texts.SetLanguage(originalLanguage);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private static ArchiveSearchSpec Definition(string[] sources, ArchiveBoardConstraint[] boards, bool? captureEnabled = null)
    {
        string raw = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, text = "", sources, boards = boards.Select(board => new { source = board.Source, board = board.Board }),
            sourceStates = Array.Empty<string>(), captureEnabled, hasPosts = (bool?)null, sort = "title", workFilter = "all", dateRange = (object?)null
        });
        ArchiveSearchSpec parsed = ArchiveSearchSpecJson.Deserialize(raw);
        Console.WriteLine("SMOKE archive exact-identity-definition: " + JsonSerializer.Serialize(new
        { raw, parsed = ArchiveSearchSpecJson.Serialize(parsed) }));
        return parsed;
    }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(80); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
