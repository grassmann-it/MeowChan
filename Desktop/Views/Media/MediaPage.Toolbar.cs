using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using MeowChan.Gallery;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// S3: the media toolbar is one row in three groups. Left: the file name search, which fills the free width of the row up to a fixed cap
// (MediaToolbarLayout.SearchMaxWidth; at least 240 dip), directly followed by the filter toggle (MediaToolbarQuery). Tom 16.11.2026: the order
// (MediaToolbarSort: "Shuffle again" while the order is random, the criterion with its muted "Sort" prefix inside the box, and the direction right
// beside it) sits on the right above the grid and ends at the grid's right edge. Right: the view tools (MediaToolbarEnd): "Refresh", the tag list
// switch, "Layout", the focus mode and "More" (⋯), above the media panel. The free space between the filter toggle and the order is the spacer,
// at least the group gap (24 dip). UX review 17.11.2026: nothing of the row depends on the selection line under it. M1: the shuffle and
// the refresh are drawn icons in square buttons (name as tooltip and accessible name), so the only text in the query group is the criterion;
// the focus mode (M4, MediaPage.Focus.cs) is one more square between "Layout" and "More". Export all and the
// saved-ratings manager always sit under More; "show missing files" is a filter (a field of the filter section and a chip of the active
// filters). When the row runs short the less important tools move under More first and the order stays visible (MediaToolbarLayout).
public sealed partial class MediaPage
{
    private MediaToolbarLayout.Level _toolbarLevel = MediaToolbarLayout.Level.Wide;
    private Grid? _toolbar;
    private Func<bool>? _canOpenBooru;
    private Action? _openBooru;
    private readonly Button _booruOpen = new() { Name = "MediaBooruOpen", IsVisible = false };
    // QW-A #1: "Media folders…" under More (the settings group of the read-only folder sources); an entry like the queue walk, never a header button.
    private readonly Button _mediaFolders = new() { Name = "MediaFolders", IsVisible = false };

    /// <summary>The width class the toolbar row is laid out for (tests and smokes).</summary>
    internal MediaToolbarLayout.Level ToolbarLevel => _toolbarLevel;

    /// <summary>The toolbar row (smokes).</summary>
    internal Grid ToolbarRow => _toolbar!;

    /// <summary>Attaches the optional action to the ordinary registered work-view overflow surface.</summary>
    internal void AttachBooruOverflow(Func<bool> canOpen, Action open)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(open);
        _canOpenBooru = canOpen;
        _openBooru = open;
        LocalizeBooruOverflow();
        RefreshWorkView();
    }

    private void LocalizeBooruOverflow()
    {
        _booruOpen.Content = Texts.T("media.toolbar.booru");
        AutomationProperties.SetName(_booruOpen, Texts.T("media.toolbar.booru"));
        ToolTip.SetTip(_booruOpen, Texts.T("media.toolbar.booruTip"));
    }

    private void InvokeBooruOverflow()
    {
        // A menu may remain open across a profile/composition or visibility change. Recheck at invocation.
        if (!_lifetime.IsCancellationRequested && _canOpenBooru?.Invoke() == true)
            _openBooru?.Invoke();
    }

    private Grid BuildToolbar()
    {
        _nameFilter.ScopeChip = _searchScope; // inside the field, as in Archive and the reader (S5)
        // Search | the filter toggle | free space | the order | the view tools. The search column takes the free width first (1000* against 1*: no
        // visible gap until its maximum is reached) up to the fixed cap SearchMaxWidth. The free-space column keeps at least the group gap, so the
        // search gives up its width (down to its minimum) before the filter toggle comes closer to the order. The order sits in its own column on the
        // right and ends at the grid's right edge (MediaPage.ToolbarAlignment.cs), the view tools stay above the media panel at the page edge. The
        // filter toggle and the view tools lead with an 8 dip margin, the order with the free-space column (no ColumnSpacing: it would double them).
        Grid tools = new() { Name = "MediaToolbar", ColumnDefinitions = new ColumnDefinitions("1000*,Auto,*,Auto,Auto") };
        tools.ColumnDefinitions[0].MaxWidth = MediaToolbarLayout.SearchMaxWidth;
        tools.ColumnDefinitions[2].MinWidth = MediaToolbarLayout.GroupGap;
        _toolbar = tools;
        _nameFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        tools.Children.Add(_nameFilter);
        // The tools under More stay in the tree (hidden): their handlers, names and shortcuts keep working, the menu entries forward to them.
        // The filter toggle is the first tool after the search and never moves. The order (shuffle, criterion, direction) is the group on the right;
        // criterion and direction stand side by side with the group's 8 dip, one unit.
        StackPanel query = new() { Name = "MediaToolbarQuery", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        StackPanel order = new() { Name = "MediaToolbarSort", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // Right-aligned in its column: the column can be wider than the tools (it keeps the room of the media panel below, MediaPage.ToolbarAlignment.cs).
        StackPanel right = new() { Name = "MediaToolbarEnd", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        // MP4: the Booru mode switch (MP5: "Tag list") sits before the focus mode, MP5's "Layout" button after the switch; MP3: the queue walk and the Tagging page are entries under More only.
        foreach (Control control in new Control[] { _filters, _shuffle, _sort, _sortDirection, _refresh, _booruMode, _layoutButton, _focus, _exportAll, _manageRatings, _booruOpen,
            _queueWalk, _taggingPageOpen, _queueAll, _mediaFolders, _more })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            // Buttons match the 32 dip combo box beside them (Workspace.Size.Input) instead of sitting 1 dip inside its edges at 30 (G3).
            if (control is Button) control.MinHeight = 32;
            (control == _filters ? query : control == _shuffle || control == _sort || control == _sortDirection ? order : right).Children.Add(control);
        }
        _nameFilter.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(query); Avalonia.Controls.Grid.SetColumn(query, 1);
        tools.Children.Add(order); Avalonia.Controls.Grid.SetColumn(order, 3);
        tools.Children.Add(right); Avalonia.Controls.Grid.SetColumn(right, 4);
        InstallToolbarAlignment(tools, query, order, right);
        ApplyToolbarMinWidths();
        tools.SizeChanged += (_, e) => ApplyToolbarWidth(e.NewSize.Width);
        return tools;
    }

    /// <summary>Lays the row out for a page width: moves the less important tools under More and narrows the fields (when it changed the class).</summary>
    internal void ApplyToolbarWidth(double width)
    {
        MediaToolbarLayout.Level level = MediaToolbarLayout.For(width);
        if (level == _toolbarLevel || _lifetime.IsCancellationRequested) return;
        _toolbarLevel = level;
        ApplyToolbarMinWidths();
        RefreshWorkView();
    }

    private void ApplyToolbarMinWidths()
    {
        _nameFilter.MinWidth = MediaToolbarLayout.SearchMin(_toolbarLevel);
        if (_toolbar is { } row) row.ColumnDefinitions[0].MinWidth = _nameFilter.MinWidth;
        ApplySortPrefix();
    }

    // The "Sort" prefix inside the criterion box (N6: it goes with the Compact stage, and before that whenever the row has no room for it,
    // MediaToolbarLayout.ShowsSortPrefix). The box keeps its value area's minimum and gets the prefix's room on top, so the prefix never cuts the value.
    private void ApplySortPrefix()
    {
        double room = _sort.PrefixRoom;
        bool shows = _toolbar is { } row && _toolbarQuery is { } query && _toolbarOrder is { } order && _toolbarEnd is { } end && row.Bounds.Width > 0
            ? MediaToolbarLayout.ShowsSortPrefix(_toolbarLevel, row.Bounds.Width, query.DesiredSize.Width,
                order.DesiredSize.Width - (_sort.ShowsPrefix && _sort.IsVisible ? room : 0), end.DesiredSize.Width, room)
            : MediaToolbarLayout.ShowsSortLabel(_toolbarLevel);
        if (_sort.ShowsPrefix != shows) _sort.ShowsPrefix = shows;
        double minimum = PrefixedComboBox.MinWidthFor(MediaToolbarLayout.SortMin(_toolbarLevel), shows, room);
        if (Math.Abs(_sort.MinWidth - minimum) >= 0.5) _sort.MinWidth = minimum;
    }

    // After the work view decided: what the width moves under More is hidden like what the view moves (its state still wants it, so
    // HeaderOverflow lists it and its shortcut keeps working).
    private void ApplyToolbarLevel()
    {
        foreach (Control tool in new Control[] { _focus, _refresh, _shuffle, _booruMode, _layoutButton })
            if (MediaToolbarLayout.IsMovedUnderMore(_toolbarLevel, Desktop.WorkViews.WorkViewElement.GetId(tool))) tool.IsVisible = false;
    }
}
