using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Native pointer regression coverage for the shared filter controls, registered as filter-pointer-selection.
// A routed event or SelectedItem assignment is not equivalent to the click path under test.
internal static class FilterPointerSelectionChecks
{
    internal static async Task RunAsync(Window owner)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("filter-pointer-selection-");
        ArchivePage? page = null;
        Window? host = null;
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            foreach (string boardId in new[] { "g", "pol" })
                await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(
                    new ResourceId("2ch", boardId, boardId), "https://filter-pointer.invalid/" + boardId, boardId, directory, true));

            page = new(token => ArchiveReader.OpenAsync(directory, token), (_, _) => Task.CompletedTask, _ => { })
            { IsAvailable = true };
            host = new Window { Title = "Filter pointer selection — native harness", Width = 1100, Height = 760, Content = page, ShowInTaskbar = false };
            host.Show(owner);
            host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive && page.IsEffectivelyVisible && page.Bounds.Width > 0);
            Require(await page.ApplySearchSpecAsync(new ArchiveSearchSpec()), "Filter pointer fixture did not establish an applied search.");
            ArchiveSearchSpec applied = page.AppliedSearchSpec ?? throw new InvalidOperationException("Applied fixture search is missing.");
            Expander filters = page.FindControl<Expander>("ArchiveFilters")!;
            filters.IsExpanded = true;
            await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveFilterSurface")!.IsEffectivelyVisible);

            using MediaNativeInput native = new(host);
            IdentitySuggestionBox source = page.FindControl<IdentitySuggestionBox>("ArchiveSourceFilter")!;
            IdentitySuggestionBox board = page.FindControl<IdentitySuggestionBox>("ArchiveBoardFilter")!;
            // Expanding the surface schedules layout/template creation. Surface visibility alone
            // does not mean that either editor's inner TextBox has been materialized yet.
            await Program.WaitUntilAsync(() => source.GetVisualDescendants().OfType<TextBox>().Any(input => input.Bounds.Width > 0)
                && board.GetVisualDescendants().OfType<TextBox>().Any(input => input.Bounds.Width > 0));
            TextBox sourceInput = source.GetVisualDescendants().OfType<TextBox>().First();
            TextBox boardInput = board.GetVisualDescendants().OfType<TextBox>().First();

            async Task TypeWithNativeFocusAsync(IdentitySuggestionBox field, TextBox input, string text)
            {
                field.BringIntoView();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                await native.ClickAsync(input);
                input.Text = text;
                await Program.WaitUntilAsync(() => field.Text == text && field.IsKeyboardFocusWithin);
            }

            string SuggestionState(IdentitySuggestionBox field) =>
                $"field={field.Name}; text={field.Text}; open={field.IsDropDownOpen}; focus={field.IsKeyboardFocusWithin}; "
                + $"items=[{string.Join(",", field.ItemsSource?.OfType<IdentitySuggestion>().Select(item => item.Source + ":" + item.Value) ?? [])}]; "
                + $"draftSources=[{string.Join(",", page.DraftSources)}]; retry={page.FindControl<Button>("ArchiveRetrySuggestions")!.IsVisible}";
            source.PropertyChanged += (_, change) =>
            {
                if (change.Property.Name == nameof(source.ItemsSource) && source.Text == "2"
                    && source.ItemsSource?.OfType<IdentitySuggestion>().Any() != true)
                    Console.WriteLine($"SMOKE source items cleared: {SuggestionState(source)}; stack={Environment.StackTrace}");
            };
            source.LoadCompleted += error => Console.WriteLine($"SMOKE source load: error={error}; {SuggestionState(source)}");
            board.LoadCompleted += error => Console.WriteLine($"SMOKE board load: error={error}; {SuggestionState(board)}");

            async Task<Control> SuggestionRowAsync(IdentitySuggestionBox field, string value, string? sourceValue)
            {
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>()
                    .Any(item => item.Value == value && item.Source == sourceValue) == true,
                    () => SuggestionState(field));
                Popup popup = field.GetVisualDescendants().OfType<Popup>().FirstOrDefault()
                    ?? throw new InvalidOperationException(field.Name + " suggestion popup is missing.");
                SelectingItemsControl list = popup.Child as SelectingItemsControl
                    ?? popup.Child?.GetVisualDescendants().OfType<SelectingItemsControl>()
                        .FirstOrDefault(item => item.Name == "PART_SelectingItemsControl")
                    ?? throw new InvalidOperationException(field.Name + " suggestion list is missing.");
                await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.DataContext is IdentitySuggestion candidate
                    && candidate.Value == value && candidate.Source == sourceValue && item.Bounds.Width > 0 && item.Bounds.Height > 0),
                    () => SuggestionState(field) + $"; popupOpen={popup.IsOpen}; listItems={list.Items.Count}; listVisible={list.IsEffectivelyVisible}; listBounds={list.Bounds}; rows=["
                        + string.Join(",", list.GetVisualDescendants().OfType<ListBoxItem>().Select(item => $"{item.DataContext}:{item.Bounds}")) + "]");
                Control row = list.GetVisualDescendants().OfType<ListBoxItem>().First(item => item.DataContext is IdentitySuggestion candidate
                    && candidate.Value == value && candidate.Source == sourceValue);
                Require(row.IsEffectivelyVisible && row.Bounds.Width > 0 && row.Bounds.Height > 0,
                    field.Name + " suggestion row is not laid out.");
                return row;
            }

            int sourceAccepts = 0;
            source.CandidateAccepted += _ => ++sourceAccepts;
            async Task<IPointer> PressSourceAsync()
            {
                bool unchanged = sourceInput.Text == "2";
                await TypeWithNativeFocusAsync(source, sourceInput, "2");
                // New text already has an asynchronous population queued. Do not race it
                // with an extra open command; only unchanged text needs explicit reopening.
                if (unchanged && !source.IsDropDownOpen) await native.KeyAsync(MediaNativeInput.Down);
                Control row = await SuggestionRowAsync(source, "2ch", null);
                var list = row.GetVisualAncestors().OfType<SelectingItemsControl>().First();
                IPointer? pointer = null;
                void Pressed(object? _, PointerPressedEventArgs e) => pointer = e.Pointer;
                list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
                try
                {
                    await native.MoveAsync(row);
                    await native.ButtonAsync(true);
                    Require(pointer != null && ReferenceEquals(pointer.Captured, list), "Suggestion press did not own pointer capture.");
                    Require(source.IsDropDownOpen && source.Text == "2" && sourceAccepts == 0
                        && page.DraftSources.Count == 0 && sourceInput.IsKeyboardFocusWithin,
                        "Suggestion press changed the draft, accepted prematurely or dismissed the popup.");
                    return pointer!;
                }
                finally { list.RemoveHandler(InputElement.PointerPressedEvent, Pressed); }
            }
            void RequireSourceNotAccepted(string action)
            {
                Require(sourceAccepts == 0 && page.DraftSources.Count == 0 && source.Text == "2"
                    && source.AcceptedCandidate == null && ReferenceEquals(page.AppliedSearchSpec, applied),
                    action + " accepted a cancelled/stale suggestion or applied Search.");
            }

            // A release must not revive a dismissed gesture, even if its row existed on press.
            IPointer held = await PressSourceAsync();
            await native.KeyAsync(MediaNativeInput.Escape);
            await native.ButtonAsync(false);
            RequireSourceNotAccepted("Escape between press and release");
            Require(held.Captured == null && !source.IsDropDownOpen, "Escape retained capture or reopened suggestions.");

            held = await PressSourceAsync();
            source.InvalidateIdentityContext();
            await native.ButtonAsync(false);
            RequireSourceNotAccepted("Context invalidation between press and release");
            Require(held.Captured == null && !source.IsDropDownOpen, "Context invalidation retained capture.");

            held = await PressSourceAsync();
            await native.MoveAsync(boardInput);
            await native.ButtonAsync(false);
            RequireSourceNotAccepted("Drag outside the pressed row");
            Require(held.Captured == null, "Outside release retained capture.");
            await native.KeyAsync(MediaNativeInput.Escape);

            held = await PressSourceAsync();
            held.Capture(boardInput); // Real capture-loss transition during an OS mouse gesture.
            await native.ButtonAsync(false);
            if (ReferenceEquals(held.Captured, boardInput)) held.Capture(null);
            RequireSourceNotAccepted("Capture stolen between press and release");
            await native.KeyAsync(MediaNativeInput.Escape);

            // A genuine same-row release adds exactly one draft identity, not an implicit Search.
            held = await PressSourceAsync();
            await native.ButtonAsync(false);
            await Program.WaitUntilAsync(() => page.DraftSources.Contains("2ch", StringComparer.Ordinal)
                && !source.IsDropDownOpen && source.Text == "" && sourceInput.IsKeyboardFocusWithin);
            Require(sourceAccepts == 1 && held.Captured == null, "Same-row release did not accept exactly once and clear capture.");
            Require(ReferenceEquals(page.AppliedSearchSpec, applied), "Native source selection executed Search instead of adding a draft filter.");

            // The board row carries its qualifying source. This is the same native pointer path,
            // followed by a keyboard Enter comparison for the second board.
            // Dragging from one suggestion to another must not commit either identity.
            int boardAccepts = 0;
            board.CandidateAccepted += _ => ++boardAccepts;
            await TypeWithNativeFocusAsync(board, boardInput, "");
            Control fromRow = await SuggestionRowAsync(board, "g", "2ch");
            Control otherRow = await SuggestionRowAsync(board, "pol", "2ch");
            await native.MoveAsync(fromRow);
            await native.ButtonAsync(true);
            Require(boardAccepts == 0 && board.IsDropDownOpen, "Board press accepted before release.");
            await native.MoveAsync(otherRow);
            await native.ButtonAsync(false);
            Require(boardAccepts == 0 && page.DraftBoards.Count == 0 && board.Text == "",
                "Dragging across board rows committed the wrong identity.");
            await native.KeyAsync(MediaNativeInput.Escape);

            await TypeWithNativeFocusAsync(board, boardInput, "g");
            Control boardRow = await SuggestionRowAsync(board, "g", "2ch");
            await native.ClickAsync(boardRow);
            await Program.WaitUntilAsync(() => page.DraftBoards.Contains(new ArchiveBoardConstraint("2ch", "g"))
                && !board.IsDropDownOpen && board.Text == "");
            Require(ReferenceEquals(page.AppliedSearchSpec, applied), "Native board selection executed Search instead of adding a draft filter.");

            await TypeWithNativeFocusAsync(board, boardInput, "pol");
            await Program.WaitUntilAsync(() => board.IsDropDownOpen && board.ItemsSource?.Cast<IdentitySuggestion>()
                .Any(item => item.Value == "pol" && item.Source == "2ch") == true);
            await native.KeyAsync(MediaNativeInput.Down);
            await native.KeyAsync(MediaNativeInput.Enter);
            await Program.WaitUntilAsync(() => page.DraftBoards.Contains(new ArchiveBoardConstraint("2ch", "pol"))
                && !board.IsDropDownOpen && board.Text == "");
            Require(boardAccepts == 2, "Board mouse/Enter acceptance was duplicated or lost.");
            Require(ReferenceEquals(page.AppliedSearchSpec, applied), "Board Enter unexpectedly executed Search.");

            // A free value remains draft text when the list is dismissed; accepting a suggestion is
            // never an implicit normalization of unrelated free-form input.
            await TypeWithNativeFocusAsync(source, sourceInput, "free-form");
            await native.KeyAsync(MediaNativeInput.Escape);
            Require(source.Text == "free-form" && page.DraftSources.SequenceEqual(["2ch"])
                && page.DraftBoards.SequenceEqual([new ArchiveBoardConstraint("2ch", "g"), new ArchiveBoardConstraint("2ch", "pol")]),
                "Dismissing a suggestion changed or committed free-form text.");

            // A normal Archive filter ComboBox also has to adopt a native pointer pick. State is
            // a draft-only choice and resets to All after its chip is created.
            ComboBox state = page.FindControl<ComboBox>("ArchiveStateFilter")!;
            state.BringIntoView();
            await native.ClickAsync(state);
            await Program.WaitUntilAsync(() => state.IsDropDownOpen);
            Popup statePopup = state.GetVisualDescendants().OfType<Popup>().FirstOrDefault()
                ?? throw new InvalidOperationException("Archive state popup is missing.");
            ComboBoxItem active = statePopup.Child?.GetVisualDescendants().OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Content?.ToString() == "Active")
                ?? throw new InvalidOperationException("Archive active-state row is not realized.");
            await native.ClickAsync(active);
            await Program.WaitUntilAsync(() => !state.IsDropDownOpen && state.SelectedIndex == 0
                && page.FindControl<WrapPanel>("ArchiveDraftStates")!.Children.Count == 1);
            Require(ReferenceEquals(page.AppliedSearchSpec, applied), "Native state selection executed Search.");

            Console.WriteLine("SMOKE filter pointer selection: native press/release adopted Archive source/board and state choices exactly once; Escape, context invalidation, outside/cross-row drag and capture loss did not accept; Enter, free text and draft/applied separation passed.");
        }
        finally
        {
            Texts.SetLanguage(language);
            page?.EndLifetime();
            host?.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
