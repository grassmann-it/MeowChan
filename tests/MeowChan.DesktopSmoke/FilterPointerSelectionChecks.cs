using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Native pointer regression coverage for the shared filter controls. This file is intentionally
// not registered here: the parent smoke owner must add it to a disposable X11/native-input hook.
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
            foreach (string boardName in new[] { "g", "pol" })
                await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(
                    new ResourceId("2ch", boardName, boardName), "https://filter-pointer.invalid/" + boardName, boardName, directory, true));

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
            TextBox sourceInput = source.GetVisualDescendants().OfType<TextBox>().First();
            TextBox boardInput = board.GetVisualDescendants().OfType<TextBox>().First();

            async Task TypeWithNativeFocusAsync(IdentitySuggestionBox field, TextBox input, string text)
            {
                await native.ClickAsync(input);
                input.Text = text;
                await Program.WaitUntilAsync(() => field.Text == text && field.IsKeyboardFocusWithin);
            }

            async Task<Control> SuggestionRowAsync(IdentitySuggestionBox field, string value, string? sourceValue)
            {
                await Program.WaitUntilAsync(() => field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>()
                    .Any(item => item.Value == value && item.Source == sourceValue) == true);
                Popup popup = field.GetVisualDescendants().OfType<Popup>().FirstOrDefault()
                    ?? throw new InvalidOperationException(field.Name + " suggestion popup is missing.");
                SelectingItemsControl list = popup.Child as SelectingItemsControl
                    ?? popup.Child?.GetVisualDescendants().OfType<SelectingItemsControl>()
                        .FirstOrDefault(item => item.Name == "PART_SelectingItemsControl")
                    ?? throw new InvalidOperationException(field.Name + " suggestion list is missing.");
                Control row = list.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is IdentitySuggestion candidate
                    && candidate.Value == value && candidate.Source == sourceValue)
                    ?? throw new InvalidOperationException(field.Name + " suggestion row is not realized: " + value);
                Require(row.IsEffectivelyVisible && row.Bounds.Width > 0 && row.Bounds.Height > 0,
                    field.Name + " suggestion row is not laid out.");
                return row;
            }

            // Native source click: it must add the draft identity, keep the applied search untouched,
            // and return focus to the editor. It must not be confused with clicking Add or Search.
            await TypeWithNativeFocusAsync(source, sourceInput, "2");
            Control sourceRow = await SuggestionRowAsync(source, "2ch", null);
            await native.ClickAsync(sourceRow);
            await Program.WaitUntilAsync(() => page.DraftSources.Contains("2ch", StringComparer.Ordinal)
                && !source.IsDropDownOpen && source.Text == "" && sourceInput.IsKeyboardFocusWithin);
            Require(ReferenceEquals(page.AppliedSearchSpec, applied), "Native source selection executed Search instead of adding a draft filter.");

            // The board row carries its qualifying source. This is the same native pointer path,
            // followed by a keyboard Enter comparison for the second board.
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

            Console.WriteLine("SMOKE filter pointer selection: native MediaNativeInput clicks adopted Archive source/board suggestions and state ComboBox choices; Enter matched board adoption; free text, draft/applied separation and no implicit Search passed.");
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
