using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// M4: the focus mode of the media page (media.focus-mode). The toolbar, the chips with "+ Filter", the hint row, the status line and the
// filter section are hidden; one 32 dip row shows the live counter and the filter count on the left and "Exit focus ×" alone at the right
// end; the grid and the selection bar stay. Escape ends the mode first, the selection next. Starting and ending are announced through a
// polite live region. A narrow page moves the toolbar button under More. Keyboard input is routed, not OS input.
// Full screen: the page title and subtitle go with the toolbar (the counts stay in the focus row, whole as its tooltip) and the page tells the
// shell (FocusModeChanged) so it can hide the footer and the app header (the shell half is pinned by MediaFocusFullscreenTests and looked at in the central capture of the real window).
// The order box carries a visible muted "Sort" prefix inside it (UX review 17.11.2026), with the same accessible name.
internal static partial class MediaPageChecks
{
    private static async Task FocusModeAsync(Window window, MediaPage page, ThemeVariant theme, string code)
    {
        string state = $"focus {theme}/{code}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = 1400; window.Height = 900;
        window.UpdateLayout();
        // The page localizes after the language change on the UI thread (Texts.Changed is posted), so wait for the new name too.
        await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Wide && page.FocusButton.IsEffectivelyVisible && page.ActiveFilterCount == 1 && page.Tiles.Count == SurfaceTagged + 1
            && Avalonia.Automation.AutomationProperties.GetName(page.FocusButton) == Texts.T("command.media.focus-mode"));
        Button focus = page.FocusButton, exit = page.FocusExitButton, filtersButton = page.FocusFiltersButton;
        Control toolbar = C<Control>(page, "MediaToolbar"), chipRow = C<Control>(page, "MediaFilterChipRow"), status = C<Control>(page, "MediaStatus"),
            hint = C<Control>(page, "MediaQueryHint"), surface = C<Control>(page, "MediaFilterSurface"), selectionRow = C<Control>(page, "MediaSelectionRow");
        Require(!page.IsFocusMode && !page.FocusRow.IsVisible && toolbar.IsEffectivelyVisible && chipRow.IsEffectivelyVisible && status.IsEffectivelyVisible
            && Avalonia.Automation.AutomationProperties.GetName(focus) == Texts.T("command.media.focus-mode") && focus.Classes.Contains("icon-square")
            // Nothing is announced yet, or only the end of the previous round (the live region keeps its last text).
            && ToolTip.GetTip(focus)?.ToString() == CommandBindings.Tooltip("media.focus-mode") && page.FocusAnnouncement != Texts.T("media.focus.on"),
            state + ": the page must start with its header and the ⤢ tool named like the command: focus mode " + page.IsFocusMode
                + ", focus row " + page.FocusRow.IsVisible + ", toolbar " + toolbar.IsEffectivelyVisible + ", chips " + chipRow.IsEffectivelyVisible
                + ", status " + status.IsEffectivelyVisible + ", name '" + Avalonia.Automation.AutomationProperties.GetName(focus) + "' vs '"
                + Texts.T("command.media.focus-mode") + "', icon-square " + focus.Classes.Contains("icon-square") + ", tip '" + ToolTip.GetTip(focus)
                + "' vs '" + CommandBindings.Tooltip("media.focus-mode") + "', announcement '" + page.FocusAnnouncement + "'");
        Require(Avalonia.Automation.AutomationProperties.GetLiveSetting(C<TextBlock>(page, "MediaFocusAnnouncement")) == Avalonia.Automation.AutomationLiveSetting.Polite,
            state + ": the announcement must be a polite live region.");
        // The visible label of the order box is the muted "Sort" prefix inside its closed box, before the value on the same line; the box's
        // accessible name is the same text and the prefix is not in the accessibility tree (not read twice). UX review 17.11.2026.
        PrefixedComboBox sortBox = C<PrefixedComboBox>(page, "MediaSort");
        TextBlock sortPrefix = C<TextBlock>(sortBox, "PrefixedComboPrefix"), sortValue = C<TextBlock>(sortBox, "PrefixedComboValue");
        double LabelX(Control control) => control.TranslatePoint(default, page.ToolbarRow)!.Value.X;
        double LabelMid(Control control) => control.TranslatePoint(default, page.ToolbarRow)!.Value.Y + control.Bounds.Height / 2;
        Require(sortBox.ShowsPrefix && sortBox.PrefixDrawn && sortPrefix.Text == Texts.T("media.sort") && !string.IsNullOrWhiteSpace(sortPrefix.Text)
            && sortPrefix.Bounds.Width > 0 && Avalonia.Automation.AutomationProperties.GetName(sortBox) == sortPrefix.Text
            && Avalonia.Automation.AutomationProperties.GetAccessibilityView(sortPrefix) == Avalonia.Automation.AccessibilityView.Raw
            && LabelX(sortBox) <= LabelX(sortPrefix) && LabelX(sortPrefix) + sortPrefix.Bounds.Width <= LabelX(sortValue) + 0.5
            && Math.Abs(LabelMid(sortPrefix) - LabelMid(sortValue)) < 3 && Math.Abs(sortBox.Bounds.Height - 32) < 0.6,
            state + ": the order box must show its muted Sort prefix inside, before the value: " + sortPrefix.Text + " / " + sortPrefix.Bounds + " / " + sortValue.Bounds + " / " + sortBox.Bounds);
        Control pageTitle = page.FocusHiddenRows[2], pageSubtitle = page.FocusHiddenRows[3];
        List<bool> modeChanges = new();
        Action<bool> hook = modeChanges.Add;
        page.FocusModeChanged += hook;
        Require(pageTitle.IsEffectivelyVisible && pageSubtitle.IsEffectivelyVisible, state + ": the page title and subtitle must show before the mode.");

        // Start with something selected: the selection bar must stay usable.
        page.Grid.Selection.Clear();
        page.Grid.Selection.Select(0); page.Grid.Selection.Select(1);
        Require(page.SelectedTiles.Count == 2 && selectionRow.IsEffectivelyVisible, state + ": the setup selection.");
        focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        await UntilAsync(() => page.IsFocusMode && page.FocusRow.IsEffectivelyVisible);
        Require(page.FocusHiddenRows.All(row => !row.IsVisible) && !toolbar.IsEffectivelyVisible && !chipRow.IsEffectivelyVisible && !status.IsEffectivelyVisible
            && !hint.IsEffectivelyVisible && !surface.IsEffectivelyVisible && !page.FilterAdd.IsEffectivelyVisible && !page.NameFilter.IsEffectivelyVisible,
            state + ": the toolbar, chips, hint, status and filter section must be hidden.");
        Require(!pageTitle.IsEffectivelyVisible && !pageSubtitle.IsEffectivelyVisible && modeChanges.SequenceEqual([true]),
            state + ": the page title and subtitle must be hidden and the shell told once: " + string.Join(",", modeChanges));
        // The counts line is gone from the page; it lives on in the focus row, whole as its tooltip.
        Require(!status.IsEffectivelyVisible && ToolTip.GetTip(C<TextBlock>(page, "MediaFocusText"))?.ToString() == C<TextBlock>(page, "MediaStatus").Text,
            state + ": the counts must stay in the focus row's tooltip.");
        Require(page.Grid.IsEffectivelyVisible && page.Grid.Bounds.Height > 100 && selectionRow.IsEffectivelyVisible && page.SelectedTiles.Count == 2
            && C<Button>(page, "MediaSelectionClear").IsEffectivelyVisible && C<Button>(page, "MediaSelectionGallery").IsEnabled,
            state + ": the grid and the selection bar must stay: grid " + page.Grid.IsEffectivelyVisible + " " + page.Grid.Bounds + ", selection row "
                + selectionRow.IsEffectivelyVisible + ", selected " + page.SelectedTiles.Count + ", clear " + C<Button>(page, "MediaSelectionClear").IsEffectivelyVisible
                + ", gallery enabled " + C<Button>(page, "MediaSelectionGallery").IsEnabled);
        Require(page.FocusAnnouncement == Texts.T("media.focus.on"), state + ": the start must be announced: " + page.FocusAnnouncement);
        // The row: 32 dip, the counter on the left (the page's status behind the label), the exit alone at the far left.
        TextBlock text = C<TextBlock>(page, "MediaFocusText");
        Require(Math.Abs(page.FocusRow.Bounds.Height - 32) < 0.6 && text.Text == MediaFocusMode.Line(C<TextBlock>(page, "MediaStatus").Text)
            && text.Text!.StartsWith(Texts.T("media.focus.label"), StringComparison.Ordinal),
            state + ": the focus row must be 32 dip and show the live counter: " + page.FocusRow.Bounds.Height + " / " + text.Text);
        double FocusRowX(Control control) => control.TranslatePoint(default, page.FocusRow)!.Value.X;
        Require(exit.IsEffectivelyVisible && page.FocusExitLabel.Text == Texts.T("media.focus.exit") && FocusRowX(exit) < 1.5
            && FocusRowX(text) > FocusRowX(exit) + exit.Bounds.Width && Avalonia.Automation.AutomationProperties.GetName(exit) == Texts.T("media.focus.exit"),
            state + ": 'Exit focus' (icon plus text) must sit alone at the far left, before the counter: " + exit.Bounds + " in " + page.FocusRow.Bounds);
        Require(filtersButton.IsEffectivelyVisible && filtersButton.Content as string == Texts.T("media.filters.count", 1)
            && ToolTip.GetTip(filtersButton)?.ToString() == Texts.T("tags.filter.includeChip", "surface"), state + ": the active filter must be counted with its chips as the tooltip.");
        Program.CaptureWindow(window, $"smoke-media-focus-{code}-{theme.ToString().ToLowerInvariant()}.png");

        // The counter is live: a filter changed meanwhile (here through the section's own combo, which is hidden) changes the row.
        ComboBox kind = C<ComboBox>(page, "MediaKindFilter");
        kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
        await UntilAsync(() => filtersButton.Content as string == Texts.T("media.filters.count", 2) && page.IsFocusMode);
        kind.SelectedIndex = 0;
        await UntilAsync(() => filtersButton.Content as string == Texts.T("media.filters.count", 1) && page.Tiles.Count == SurfaceTagged + 1);
        Require(page.IsFocusMode, state + ": the mode must stay while the filters change.");

        // Escape on the grid ends the mode first; the selection survives until the next Escape.
        page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        window.UpdateLayout();
        // The two selected tiles keep the one context line: the selection actions are on it, the summary line is back with the second Escape.
        Require(!page.IsFocusMode && !page.FocusRow.IsVisible && toolbar.IsEffectivelyVisible && chipRow.IsEffectivelyVisible && !status.IsEffectivelyVisible && selectionRow.IsEffectivelyVisible
            && page.SelectedTiles.Count == 2 && page.FocusAnnouncement == Texts.T("media.focus.off")
            && pageTitle.IsEffectivelyVisible && pageSubtitle.IsEffectivelyVisible && modeChanges.SequenceEqual([true, false]),
            state + ": the first Escape must end the focus mode, give the title back, tell the shell and keep the selection: " + page.SelectedTiles.Count);
        page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Require(page.SelectedTiles.Count == 0 && !page.IsFocusMode && status.IsEffectivelyVisible && !selectionRow.IsEffectivelyVisible, state + ": the second Escape must clear the selection and give the summary line back.");

        // The exit button and Escape on the button end the mode as well; the search field's request (Primary+F) shows the header.
        page.ToggleFocusModeByShortcut();
        Require(page.IsFocusMode && page.FocusAnnouncement == Texts.T("media.focus.on"), state + ": the shortcut must start the mode.");
        exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!page.IsFocusMode && page.FocusAnnouncement == Texts.T("media.focus.off") && toolbar.IsEffectivelyVisible, state + ": the exit button must end the mode.");
        page.SetFocusMode(true);
        exit.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Require(!page.IsFocusMode, state + ": Escape on the exit button must end the mode.");
        page.SetFocusMode(true);
        page.FocusSearch();
        Require(!page.IsFocusMode && page.NameFilter.IsEffectivelyVisible, state + ": asking for the search field must show the header again.");

        // A narrow page moves the tool under More; the entry there is the command's.
        if (code == "en")
        {
            window.Width = 660; window.Height = 560;
            window.UpdateLayout();
            await UntilAsync(() => page.ToolbarLevel != MediaToolbarLayout.Level.Wide);
            window.UpdateLayout();
            Require(!focus.IsVisible && page.HeaderMoreIds.Contains("media.toolbar.focus")
                && page.HeaderMoreIds.First(id => id is not "media.toolbar.tag-list" and not "media.toolbar.layout" and not "media.toolbar.panel") == "media.toolbar.focus", // N6/MP5: the tag list, Layout and media-sidebar switches join at the Compact stage
                state + ": a narrow page must move the tool under More, first (after the Booru mode switch): " + string.Join(", ", page.HeaderMoreIds));
            page.SetFocusMode(true);
            window.UpdateLayout();
            Require(page.FocusRow.IsEffectivelyVisible && exit.IsEffectivelyVisible && page.FocusRow.Bounds.Height < 33
                && exit.Bounds.X < 1.5, state + ": the focus row must fit the narrow page with the exit at the left.");
            Program.CaptureWindow(window, $"smoke-media-focus-narrow-{theme.ToString().ToLowerInvariant()}.png");
            page.SetFocusMode(false);
            window.Width = 1400; window.Height = 900;
            window.UpdateLayout();
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Wide && focus.IsEffectivelyVisible);
        }
        page.FocusModeChanged -= hook;
        Require(!page.IsFocusMode && pageTitle.IsEffectivelyVisible && pageSubtitle.IsEffectivelyVisible, state + ": the mode must be off at the end.");
    }
}
