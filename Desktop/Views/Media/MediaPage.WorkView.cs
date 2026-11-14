using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan;

// Work views (plan AP5 §1.7, WV5): what the media page shows directly in Barebones / Simple / Advanced. The table is
// WorkViewPolicy (WorkViewPolicy.Media.cs); this file only applies it. Rules kept here:
//  - a tool the view reduces moves under the header's "More" (⋯) or the selection bar's "More"; nothing is removed;
//  - a filter field the view holds back sits under "More filters"; a field whose value is not the default stays shown (R6);
//  - data, errors and the chips of active filters are never reduced (R4/R5), and shortcuts keep their own routes (R2);
//  - the page keeps owning IsVisible for state (WorkViewGate.Apply); a standalone page starts in Advanced (everything direct).
public sealed partial class MediaPage
{
    private WorkView _workView = WorkView.Advanced;
    private bool _workViewRegistered;
    private readonly Button _more = new()
    {
        Name = "MediaMore", Content = "⋯", MinWidth = 32, MinHeight = 32, Padding = new Avalonia.Thickness(8, 0),
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight }
    };
    private readonly Button _selMore = new()
    {
        Name = "MediaSelectionMore", Content = "⋯", MinWidth = 32, Padding = new Avalonia.Thickness(8, 0),
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight }
    };
    // The fields of the filter surface that a view may hold back, opened by this toggle until the view changes (R13: nothing is saved).
    private readonly ToggleButton _moreFilters = new() { Name = "MediaMoreFilters", IsVisible = false };

    /// <summary>The view this page last applied (Advanced until the shell applies the window's view).</summary>
    internal WorkView CurrentWorkView => _workView;

    /// <summary>Applies <paramref name="view"/>; <paramref name="viewChanged"/> also closes what the user opened for the previous view.</summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (_lifetime.IsCancellationRequested) return;
        _workView = view;
        if (viewChanged) _moreFilters.IsChecked = false;
        RefreshWorkView();
    }

    private void RegisterWorkViewIds()
    {
        WorkViewElement.SetId(_nameFilter, "media.search");
        WorkViewElement.SetId(_filters, "media.filter.toggle");
        WorkViewElement.SetId(_sort, "media.sort");
        WorkViewElement.SetId(_more, "media.toolbar.more");
        WorkViewElement.SetId(_refresh, "media.toolbar.refresh");
        WorkViewElement.SetId(_shuffle, "media.toolbar.shuffle");
        WorkViewElement.SetId(_missing, "media.toolbar.missing");
        WorkViewElement.SetId(_exportAll, "media.toolbar.export-all");
        WorkViewElement.SetId(_manageRatings, "media.toolbar.manage-ratings");
        WorkViewElement.SetId(_kindFilter, "media.filter.kind");
        WorkViewElement.SetId(_favoritesOnly, "media.filter.favorites");
        WorkViewElement.SetId(_sourceFilter, "media.filter.source");
        WorkViewElement.SetId(_boardFilter, "media.filter.board");
        WorkViewElement.SetId(_minRating, "media.filter.rating");
        // S7: one picker adds a tag as included or excluded, so the two former include/exclude rows are this one.
        WorkViewElement.SetId(_tagAdd, "media.filter.tags");
        WorkViewElement.SetId(_soundFilter, "media.filter.sound");
        WorkViewElement.SetId(_withoutMediaTags, "media.filter.without-media-tags");
        WorkViewElement.SetId(_tagMode, "media.filter.tag-mode");
        WorkViewElement.SetId(_moreFilters, "media.filter.more");
        WorkViewElement.SetId(_untaggedLink, "media.status.untagged");
        WorkViewElement.SetId(_selectionText, "media.selection.status");
        WorkViewElement.SetId(_selGallery, "media.selection.gallery");
        WorkViewElement.SetId(_selClear, "media.selection.clear");
        WorkViewElement.SetId(_selMore, "media.selection.more");
        WorkViewElement.SetId(_selTag, "media.selection.tag");
        WorkViewElement.SetId(_selExport, "media.selection.export");
        ((MenuFlyout)_more.Flyout!).Opening += (_, _) => FillMore((MenuFlyout)_more.Flyout!, HeaderOverflow());
        ((MenuFlyout)_selMore.Flyout!).Opening += (_, _) => FillMore((MenuFlyout)_selMore.Flyout!, SelectionOverflow());
        _moreFilters.IsCheckedChanged += (_, _) => { if (!_localizing) RefreshWorkView(); };
        _workViewRegistered = true;
    }

    private void LocalizeWorkView()
    {
        _moreFilters.Content = Texts.T("workview.moreFilters");
        ToolTip.SetTip(_more, Texts.T("workview.more"));
        ToolTip.SetTip(_selMore, Texts.T("workview.more"));
        RefreshWorkView();
    }

    // ---- Presence ----

    // Which filter fields hold a value other than the default: a view never hides them (R6).
    private bool FieldActive(Control field) => WorkViewElement.GetId(field) switch
    {
        "media.filter.source" => _sourceFilter.SelectedItem is Choice { Value: not null },
        "media.filter.board" => _boardFilter.SelectedItem is BoardChoice { Board: not null },
        "media.filter.rating" => _minRating.SelectedIndex > 0,
        "media.filter.tags" => _includeTags.Count + _excludeTags.Count > 0,
        "media.filter.sound" => SoundFilterActive,
        "media.filter.without-media-tags" => _withoutMediaTags.IsChecked == true,
        "media.filter.tag-mode" => _tagMode.SelectedIndex > 0,
        _ => false
    };

    private IEnumerable<Control> FilterFields() => new Control[]
    {
        _kindFilter, _favoritesOnly, _sourceFilter, _boardFilter, _minRating, _tagAdd,
        _soundFilter, _withoutMediaTags, _tagMode
    };

    private void RefreshWorkView()
    {
        if (!_workViewRegistered || _lifetime.IsCancellationRequested) return;
        WorkView view = _workView;
        // Header: the page's own state decides whether a tool applies at all; the view decides where it sits.
        WorkViewGate.Apply(_nameFilter, view);
        WorkViewGate.Apply(_filters, view);
        WorkViewGate.Apply(_sort, view);
        WorkViewGate.Apply(_refresh, view);
        WorkViewGate.Apply(_shuffle, view, stateVisible: Sort == MediaLibrarySort.Random);
        WorkViewGate.Apply(_missing, view, nonDefaultState: _missing.IsChecked == true);
        WorkViewGate.Apply(_exportAll, view);
        WorkViewGate.Apply(_manageRatings, view);
        UpdateMore(_more, HeaderOverflow());
        // Filter fields.
        bool opened = _moreFilters.IsChecked == true, held = false;
        foreach (Control field in FilterFields())
        {
            WorkViewGate.Apply(field, view, nonDefaultState: opened || FieldActive(field));
            held |= WorkViewGate.HiddenByView(field);
        }
        _moreFilters.IsVisible = held || opened;
        AutomationProperties.SetName(_moreFilters, Texts.T("workview.moreFilters"));
        // UpdateUntaggedLink owns the link's state; the view only re-applies what the page last asked for.
        WorkViewGate.Apply(_untaggedLink, view, WorkViewElement.GetStateVisible(_untaggedLink));
        // Selection bar: the row itself is state (something is selected), so its More needs no state of its own.
        WorkViewGate.Apply(_selTag, view);
        WorkViewGate.Apply(_selExport, view);
        UpdateMore(_selMore, SelectionOverflow());
    }

    // The tools of the header a view moved under More, in policy order, with their own state still wanting them (R6 raised the rest).
    private List<Control> HeaderOverflow() => Overflowed(_refresh, _shuffle, _missing, _exportAll, _manageRatings);

    private List<Control> SelectionOverflow() => Overflowed(_selTag, _selExport);

    private static List<Control> Overflowed(params Control[] controls) => controls.Where(WorkViewGate.HiddenByView)
        .OrderBy(control => WorkViewPolicy.Get(WorkViewElement.GetId(control)!).OverflowOrder).ToList();

    // R7: the button says how many actions it holds.
    private void UpdateMore(Button more, List<Control> items)
    {
        more.IsVisible = items.Count > 0;
        string name = Texts.Count("workview.moreCount", items.Count);
        AutomationProperties.SetName(more, name);
        AutomationProperties.SetHelpText(more, name);
    }

    // The entries forward to the moved control itself: label, enabled state and disabled reason are mirrored when the menu opens,
    // there is no second feature logic (the button, the check box or the catalog command stays the executor).
    private void FillMore(MenuFlyout flyout, List<Control> items)
    {
        flyout.Items.Clear();
        foreach (Control control in items)
        {
            string id = WorkViewElement.GetId(control)!;
            WorkViewEntry entry = WorkViewPolicy.Get(id);
            if (control is Button button && entry.CommandId != null)
            {
                flyout.Items.Add(WorkViewOverflow.CreateForwardingItem(button, entry.CommandId));
                continue;
            }
            MenuItem item = new()
            {
                Header = control is ContentControl { Content: string text } ? text : AutomationProperties.GetName(control),
                IsEnabled = control.IsEnabled,
                Command = new DelegateCommand(() => CommandBindings.InvokeAction(control), () => control.IsEnabled)
            };
            if (control is CheckBox check)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = check.IsChecked == true;
            }
            if (ToolTip.GetTip(control) is string tip && tip.Length > 0)
            {
                ToolTip.SetTip(item, tip);
                ToolTip.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, tip);
            }
            flyout.Items.Add(item);
        }
    }

    /// <summary>The overflow entries the header's More holds right now (tests and smokes).</summary>
    internal IReadOnlyList<string> HeaderMoreIds => HeaderOverflow().Select(control => WorkViewElement.GetId(control)!).ToArray();

    /// <summary>The overflow entries of the selection bar's More right now (tests and smokes).</summary>
    internal IReadOnlyList<string> SelectionMoreIds => SelectionOverflow().Select(control => WorkViewElement.GetId(control)!).ToArray();
}
