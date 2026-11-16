using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// M4: the focus mode of the media page (media.focus-mode, Shift+F). It hides the rows above the grid that are for finding (the page title and
// subtitle, the toolbar, the chips with "+ Filter", the hint row, the status line and the filter section) and shows one 32 dip row instead:
// the live counter and the number of active filters on the left, the way out ("Exit focus", icon plus text) alone at the far left, set apart by a gap and a thin separator. The grid and the selection bar stay and
// keep working. The shell hides its own chrome around the page on the same signal (FocusModeChanged, MainWindow.MediaFocusChrome.cs): the
// footer navigation always, the application header when the setting asks for it.
// The rows are only hidden, never rebuilt: the filter state, the chips and their keyboard focus are those of the page (MediaPage.Filters.cs
// and MediaPage.FilterChips.cs are unchanged), and a refused filter keeps explaining itself above the grid. Escape ends the mode before any other
// level (MediaFocusMode.Escape). The mode is a working state: it is not saved. Starting and ending it is announced through a polite live region.
public sealed partial class MediaPage
{
    private readonly Button _focus = new() { Name = "MediaFocus" };
    private readonly Grid _focusRow = new() { Name = "MediaFocusRow", Height = MediaFocusMode.FocusRowHeight, IsVisible = false, ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
    private readonly TextBlock _focusText = new() { Name = "MediaFocusText", VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly TextBlock _focusSeparator = new() { Name = "MediaFocusSeparator", Text = "·", VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Button _focusFilters = new()
    {
        Name = "MediaFocusFilters", Classes = { "link" }, IsVisible = false, Padding = new Thickness(8, 2),
        BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Button _focusExit = new() { Name = "MediaFocusExit", Classes = { "subtle" }, MinHeight = MediaFocusMode.FocusRowHeight, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _focusExitLabel = new() { Name = "MediaFocusExitLabel", VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The gap between the way out at the far left and the counter, and its thin separator (the same 24 dip and Workspace.Border token as the HeaderBar back slot).</summary>
    internal const double FocusExitGap = 24, FocusExitSeparatorThickness = 1;
    /// <summary>Below this row width (dip) the exit keeps only its icon; tooltip and accessible name still name it (the HeaderBar compact rule).</summary>
    internal const double FocusExitCompactWidth = 360;
    // The live region: it is in the tree at no size (the row it belongs to may be hidden when the mode ends), only its text is for a screen reader.
    private readonly TextBlock _focusAnnouncement = new()
    {
        Name = "MediaFocusAnnouncement", Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false, Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private StackPanel? _topRows, _bottomRows;
    private bool _focusMode;

    /// <summary>Whether the page is in focus mode (a working state, never saved).</summary>
    internal bool IsFocusMode => _focusMode;
    /// <summary>The row that replaces the header rows while the mode is on (smokes).</summary>
    internal Grid FocusRow => _focusRow;
    /// <summary>The tool button of the toolbar (smokes).</summary>
    internal Button FocusButton => _focus;
    /// <summary>"Exit focus", alone at the far left of the focus row (smokes).</summary>
    internal Button FocusExitButton => _focusExit;
    internal TextBlock FocusExitLabel => _focusExitLabel;
    /// <summary>The count of active filters in the focus row; shown only while one is active (smokes).</summary>
    internal Button FocusFiltersButton => _focusFilters;
    /// <summary>The text the screen reader got last for the mode ("Focus mode on" / "Focus mode off"; null: never changed), for tests and smokes.</summary>
    internal string? FocusAnnouncement => _focusAnnouncement.Text;
    /// <summary>The rows above the grid that the mode hides: the toolbar with the chips, and the hint, status and filter section.</summary>
    internal Control[] FocusHiddenRows => new Control[] { _topRows!, _bottomRows!, _title, _subtitle };
    /// <summary>Raised after the mode started or ended; the shell hides or restores the chrome around the page (the footer, the header).</summary>
    internal event Action<bool>? FocusModeChanged;
    /// <summary>The mode can start while the header is shown (an empty library has no header to hide).</summary>
    internal bool CanToggleFocusMode => !_lifetime.IsCancellationRequested && (_focusMode || _headerBody.IsVisible);

    // Called once by the constructor, after the header is composed: wraps the rows that the mode hides into two groups around the filter
    // error (which stays: a refused filter explains an empty grid) and puts the focus row in their place. Spacing and order are those the
    // rows had as direct children of the header body, so the header looks as before while the mode is off.
    private void InstallFocus(Panel body)
    {
        Control[] top = { _toolbar!, _searchCarryOffer, (Control)_filterChips.Parent! };
        Control[] bottom = { _queryHint, (Control)_status.Parent!, _filterSurface! };
        int at = _headerBody.Children.IndexOf(top[0]);
        foreach (Control row in top) _headerBody.Children.Remove(row);
        foreach (Control row in bottom) _headerBody.Children.Remove(row);
        _topRows = new StackPanel { Name = "MediaHeaderTools", Spacing = _headerBody.Spacing };
        _bottomRows = new StackPanel { Name = "MediaHeaderStatus", Spacing = _headerBody.Spacing };
        foreach (Control row in top) _topRows.Children.Add(row);
        foreach (Control row in bottom) _bottomRows.Children.Add(row);
        _headerBody.Children.Insert(at, BuildFocusRow());
        _headerBody.Children.Insert(at + 1, _topRows);
        _headerBody.Children.Insert(_headerBody.Children.IndexOf(_filterError) + 1, _bottomRows);
        AutomationProperties.SetLiveSetting(_focusAnnouncement, AutomationLiveSetting.Polite);
        body.Children.Add(_focusAnnouncement);
        _focus.Click += (_, _) => SetFocusMode(!_focusMode);
        _focusExit.Click += (_, _) => SetFocusMode(false);
        _focusFilters.Click += (_, _) => ToolTip.SetIsOpen(_focusFilters, !ToolTip.GetIsOpen(_focusFilters));
        _focusFilters.LostFocus += (_, _) => ToolTip.SetIsOpen(_focusFilters, false);
        // Escape anywhere on the page ends the mode (the grid's own handler does it first when the grid has the focus, MediaPage.Selection.cs).
        AddHandler(KeyDownEvent, (_, e) => { if (!e.Handled) OnFocusEscape(e); }, RoutingStrategies.Bubble);
    }

    private Control BuildFocusRow()
    {
        // Narrow-window sweep (16.11.2026): the counter, its dot and the filter count share the room right of the exit. A horizontal StackPanel
        // measured the counter at its full length, so its ellipsis never came and the clip cut the filter count off the row's end; in a
        // left-aligned grid the counter's star column gets what the dot and the count leave, trims there (the full status is its tooltip) and the
        // count stays visible. The 4 dip gaps are the margins of the two that can be hidden, so a hidden one leaves no gap behind.
        Grid left = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left };
        _focusSeparator.Margin = new Thickness(4, 0, 0, 0);
        _focusFilters.Margin = new Thickness(4, 0, 0, 0);
        left.Children.Add(_focusText);
        left.Children.Add(_focusSeparator); Avalonia.Controls.Grid.SetColumn(_focusSeparator, 1);
        left.Children.Add(_focusFilters); Avalonia.Controls.Grid.SetColumn(_focusFilters, 2);
        // Tom's rule 14.11. evening: the way out stands at the far left, first in the row (and in its tab order), then a gap with a thin
        // separator, then the counter. Icon plus a short text; a narrow row keeps only the icon.
        _focusExit.HorizontalAlignment = HorizontalAlignment.Left;
        _focusExit.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { WorkspaceIcons.Create(WorkspaceIcons.ChevronLeft), _focusExitLabel } };
        Border separator = new() { Name = "MediaFocusExitSeparator", Width = FocusExitSeparatorThickness, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 8), IsHitTestVisible = false };
        separator.Bind(Border.BackgroundProperty, separator.GetResourceObservable("Workspace.Border"));
        Border gap = new() { Name = "MediaFocusExitGap", Width = FocusExitGap, Child = separator };
        _focusRow.Children.Add(_focusExit); Avalonia.Controls.Grid.SetColumn(_focusExit, 0);
        _focusRow.Children.Add(gap); Avalonia.Controls.Grid.SetColumn(gap, 1);
        _focusRow.Children.Add(left); Avalonia.Controls.Grid.SetColumn(left, 2);
        _focusRow.SizeChanged += (_, e) => _focusExitLabel.IsVisible = e.NewSize.Width >= FocusExitCompactWidth;
        return _focusRow;
    }

    /// <summary>Starts or ends the mode. Ending is always possible; starting needs a header to hide (an empty library has none).</summary>
    internal void SetFocusMode(bool on, bool announce = true)
    {
        if (_lifetime.IsCancellationRequested || on == _focusMode || on && !CanToggleFocusMode) return;
        // The control that has the keyboard focus may be hidden by the change: the focus goes on where the user's attention is.
        bool inHeader = FocusWithin(_topRows!) || FocusWithin(_bottomRows!), inRow = FocusWithin(_focusRow);
        _focusMode = on;
        _focusRow.IsVisible = on;
        _topRows!.IsVisible = _bottomRows!.IsVisible = !on;
        // The page title and the subtitle go too: the grid gets the whole page. The counter lives on in the focus row. MP5: the rows the layout left
        // out stay out when the mode ends (the layout plan owns the subtitle, the chips and the status line).
        _title.IsVisible = !on;
        ApplyLayoutPlan();
        if (!on) ToolTip.SetIsOpen(_focusFilters, false);
        RefreshFocusRow();
        if (announce) _focusAnnouncement.Text = MediaFocusMode.Announcement(on);
        FocusModeChanged?.Invoke(on);
        if (on && inHeader) FocusGrid();
        else if (!on && inRow)
        {
            // Ended with the button: back on the tool that started it (it may sit under More on a narrow page).
            if (_focus.IsEffectivelyVisible) _focus.Focus(NavigationMethod.Tab); else FocusGrid();
        }
    }

    /// <summary>The shell's shortcut (Shift+F) and More entry: toggles the mode.</summary>
    internal void ToggleFocusModeByShortcut() => SetFocusMode(!_focusMode);

    // Escape: the mode is the first level (before "export all" and the selection).
    private bool OnFocusEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !_focusMode) return false;
        SetFocusMode(false);
        e.Handled = true;
        return true;
    }

    // The counter is the status line of the page behind a label, so it is as live as that line (every presentation and progress tick).
    private void RefreshFocusRow()
    {
        if (!_focusMode) return;
        _focusText.Text = MediaFocusMode.Line(_status.Text);
        // The whole counts line (threads, media, untagged, indexing) is the tooltip, since the row itself is one short line.
        ToolTip.SetTip(_focusText, string.IsNullOrWhiteSpace(_status.Text) ? null : _status.Text);
        int count = ActiveFilterCount;
        _focusSeparator.IsVisible = _focusFilters.IsVisible = count > 0;
        if (count == 0) return;
        _focusFilters.Content = Texts.T("media.filters.count", count);
        string name = Texts.Count("media.filters.countName", count);
        AutomationProperties.SetName(_focusFilters, name);
        ToolTip.SetTip(_focusFilters, MediaFocusMode.FilterList(_renderedChips));
    }

    // An empty library hides the header body altogether; the mode ends with it instead of leaving the user without the exit.
    private void EndFocusWithoutHeader()
    {
        if (_focusMode && !_headerBody.IsVisible) SetFocusMode(false);
    }

    private void LocalizeFocus()
    {
        WorkspaceIcons.MakeSquare(_focus, WorkspaceIcons.Maximize, Texts.T("command.media.focus-mode"), CommandBindings.Tooltip(MediaFocusMode.CommandId));
        _focusExitLabel.Text = Texts.T("media.focus.exit");
        string exit = Texts.T("media.focus.exit.tip");
        ToolTip.SetTip(_focusExit, exit);
        AutomationProperties.SetName(_focusExit, Texts.T("media.focus.exit"));
        AutomationProperties.SetHelpText(_focusExit, exit);
        RefreshFocusRow();
    }
}
