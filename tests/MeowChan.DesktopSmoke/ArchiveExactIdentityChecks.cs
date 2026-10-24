using System.Text.Json;
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
            void Add(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            async Task Accept(string name, string value, string? source)
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
                KeyEventArgs down = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
                input.RaiseEvent(down); KeyboardState("immediate-after-down", down); await Settle();
                KeyboardState("settled-before-enter", down);
                KeyEventArgs enter = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
                input.RaiseEvent(enter); KeyboardState("immediate-after-enter", enter); await Settle();
                KeyboardState("settled-after-enter", enter);
                Require(enter.Handled && !field.IsDropDownOpen && page.TryReadSelectedIdentity(name) is { } accepted
                    && accepted.Value == value && accepted.Source == source && field.Text == value, "Shown exact suggestion was not explicitly accepted.");
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
                await Type("ArchiveSourceFilter", "A"); Add("ArchiveAddSource");
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
                Scope("A"); await Type("ArchiveBoardFilter", "x"); Add("ArchiveAddBoard");
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
                await Accept("ArchiveSourceFilter", " A ", null);
                Observe("accepted-source-and-board-stay-exact-across-language", "after-source-accepted-before-add");
                Add("ArchiveAddSource");
                Observe("accepted-source-and-board-stay-exact-across-language", "after-source-add-before-scope");
                Scope(" A ");
                await Accept("ArchiveBoardFilter", " x ", " A ");
                Texts.SetLanguage(Texts.Language == "en" ? "de" : "en"); await Settle();
                Observe("accepted-source-and-board-stay-exact-across-language", "after-language");
                Require(page.TryReadSelectedIdentity("ArchiveBoardFilter") is { Value: " x ", Source: " A " }, "Language-only relabel revoked an accepted exact board.");
                Add("ArchiveAddBoard"); await page.ApplySearchAsync();
                Membership((" A ", " x "));
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A "]) && !page.HasUnappliedSearchDraft, "Accepted source or board was trimmed by Add.");
            });
            await Case("explicit-scope-change-revokes-previous-board-qualification", async () =>
            {
                await Apply(Definition(["A", " A "], []));
                Observe("explicit-scope-change-revokes-previous-board-qualification", "after-definition-before-scope");
                Scope(" A ");
                await Accept("ArchiveBoardFilter", " x ", " A "); Scope("A");
                Observe("explicit-scope-change-revokes-previous-board-qualification", "after-scope-change");
                Require(page.ReadIdentityEditor("ArchiveBoardFilter") == " x " && page.TryReadSelectedIdentity("ArchiveBoardFilter") == null,
                    "Explicit source change kept stale accepted qualification or rewrote raw text.");
                Add("ArchiveAddBoard"); await page.ApplySearchAsync();
                Membership(("A", "x"));
                Require(page.AppliedSearchSpec!.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]), "Free board did not use its new explicit scope.");
            });
            await Case("new-free-input-trims-without-normalizing-existing-identities", async () =>
            {
                await Apply(Definition([" A "], []));
                await Type("ArchiveSourceFilter", " A ");
                Require(page.TryReadSelectedIdentity("ArchiveSourceFilter") == null, "Merely typing accepted a suggestion.");
                Add("ArchiveAddSource"); Scope("A");
                await Type("ArchiveBoardFilter", " x "); Add("ArchiveAddBoard");
                await page.ApplySearchAsync(); Observe("new-free-input-trims-without-normalizing-existing-identities", "after-apply");
                Require(page.AppliedSearchSpec!.Sources.SequenceEqual([" A ", "A"]) && page.AppliedSearchSpec.Boards.SequenceEqual([new ArchiveBoardConstraint("A", "x")]),
                    "Adding free text normalized an existing identity or stopped trimming new free text.");
                Membership(("A", "x"));
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
