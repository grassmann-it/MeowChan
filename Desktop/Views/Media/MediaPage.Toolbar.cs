using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using MeowChan.Gallery;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// S3: the media toolbar is one row in two groups (the shop pattern). Left: the file name search and the filter toggle. Right: the order
// (criterion and direction), "Shuffle again" in front of it while the order is random, then "Refresh" and "More" (⋯). M1: the shuffle and
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
        // Search (grows up to its maximum) | filter toggle | free space | the right group. The search column takes the free width
        // first, so the space left over beyond its maximum stays between the groups.
        Grid tools = new() { Name = "MediaToolbar", ColumnSpacing = 8, ColumnDefinitions = new ColumnDefinitions("100*,Auto,*,Auto") };
        tools.ColumnDefinitions[0].MaxWidth = MediaToolbarLayout.SearchMaxWidth;
        _nameFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        tools.Children.Add(_nameFilter);
        tools.Children.Add(_filters); Avalonia.Controls.Grid.SetColumn(_filters, 1);
        // The tools under More stay in the tree (hidden): their handlers, names and shortcuts keep working, the menu entries forward to them.
        StackPanel right = new() { Name = "MediaToolbarEnd", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // MP4: the Booru mode switch sits before the focus mode; MP3: the queue walk and the Tagging page are entries under More only.
        foreach (Control control in new Control[] { _shuffle, _sortLabel, _sort, _sortDirection, _refresh, _booruMode, _focus, _exportAll, _manageRatings, _booruOpen,
            _queueWalk, _taggingPageOpen, _more })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            // Buttons match the 32 dip combo box beside them (Workspace.Size.Input) instead of sitting 1 dip inside its edges at 30 (G3).
            if (control is Button) control.MinHeight = 32;
            right.Children.Add(control);
        }
        _nameFilter.VerticalAlignment = _filters.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(right); Avalonia.Controls.Grid.SetColumn(right, 3);
        // The label follows the criterion box, and goes with the Compact stage (N6).
        _sort.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) SyncSortLabel(); };
        SyncSortLabel();
        AutomationProperties.SetAccessibilityView(_sortLabel, AccessibilityView.Raw);
        ApplyToolbarMinWidths();
        tools.SizeChanged += (_, e) => ApplyToolbarWidth(e.NewSize.Width);
        return _toolbar = tools;
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
        _sort.MinWidth = MediaToolbarLayout.SortMin(_toolbarLevel);
        SyncSortLabel();
    }

    // After the work view decided: what the width moves under More is hidden like what the view moves (its state still wants it, so
    // HeaderOverflow lists it and its shortcut keeps working).
    private void ApplyToolbarLevel()
    {
        foreach (Control tool in new Control[] { _focus, _refresh, _shuffle, _booruMode })
            if (MediaToolbarLayout.IsMovedUnderMore(_toolbarLevel, Desktop.WorkViews.WorkViewElement.GetId(tool))) tool.IsVisible = false;
    }
}
