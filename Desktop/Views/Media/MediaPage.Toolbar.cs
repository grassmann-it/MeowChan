using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using MeowChan.Gallery;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// S3: the media toolbar is one row in two groups (the shop pattern). Left: the file name search, which fills the free width of the row
// (MediaToolbarLayout: at least 240, at most 900 dip). Right: the filter toggle, then the order (criterion and direction) with "Shuffle again"
// between them while the order is random, then "Refresh" and "More" (⋯). M1: the shuffle and
// the refresh are drawn icons in square buttons (name as tooltip and accessible name), so the only text in the group is the criterion;
// the focus mode (M4, MediaPage.Focus.cs) is one more square between "Refresh" and "More". Export all and the
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
        // Search (fills the free width between its minimum and maximum) | free space beyond the maximum | the right group. The search column
        // takes the free width first (1000* against 1*: no visible gap until the maximum is reached), so a very wide window leaves its space between the search and the right group.
        // The gap between the two is the 8 dip margin of the right group (no ColumnSpacing: the free-space column would double it).
        Grid tools = new() { Name = "MediaToolbar", ColumnDefinitions = new ColumnDefinitions("1000*,*,Auto") };
        tools.ColumnDefinitions[0].MaxWidth = MediaToolbarLayout.SearchMaxWidth;
        _toolbar = tools;
        _nameFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        tools.Children.Add(_nameFilter);
        // The tools under More stay in the tree (hidden): their handlers, names and shortcuts keep working, the menu entries forward to them.
        // The filter toggle is the first tool of the right group, directly in front of the order (the criterion), and never moves.
        StackPanel right = new() { Name = "MediaToolbarEnd", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        // MP4: the Booru mode switch (MP5: "Tag list") sits before the focus mode, MP5's "Layout" button after the switch; MP3: the queue walk and the Tagging page are entries under More only.
        foreach (Control control in new Control[] { _filters, _shuffle, _sortLabel, _sort, _sortDirection, _refresh, _booruMode, _layoutButton, _focus, _exportAll, _manageRatings, _booruOpen,
            _queueWalk, _taggingPageOpen, _queueAll, _mediaFolders, _more })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            // Buttons match the 32 dip combo box beside them (Workspace.Size.Input) instead of sitting 1 dip inside its edges at 30 (G3).
            if (control is Button) control.MinHeight = 32;
            right.Children.Add(control);
        }
        _nameFilter.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(right); Avalonia.Controls.Grid.SetColumn(right, 2);
        // The label follows the criterion box, and goes with the Compact stage (N6).
        _sort.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) SyncSortLabel(); };
        SyncSortLabel();
        AutomationProperties.SetAccessibilityView(_sortLabel, AccessibilityView.Raw);
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

    private void SyncSortLabel() => _sortLabel.IsVisible = _sort.IsVisible && MediaToolbarLayout.ShowsSortLabel(_toolbarLevel);

    private void ApplyToolbarMinWidths()
    {
        _nameFilter.MinWidth = MediaToolbarLayout.SearchMin(_toolbarLevel);
        if (_toolbar is { } row) row.ColumnDefinitions[0].MinWidth = _nameFilter.MinWidth;
        _sort.MinWidth = MediaToolbarLayout.SortMin(_toolbarLevel);
        SyncSortLabel();
    }

    // After the work view decided: what the width moves under More is hidden like what the view moves (its state still wants it, so
    // HeaderOverflow lists it and its shortcut keeps working).
    private void ApplyToolbarLevel()
    {
        foreach (Control tool in new Control[] { _focus, _refresh, _shuffle, _booruMode, _layoutButton })
            if (MediaToolbarLayout.IsMovedUnderMore(_toolbarLevel, Desktop.WorkViews.WorkViewElement.GetId(tool))) tool.IsVisible = false;
    }
}
