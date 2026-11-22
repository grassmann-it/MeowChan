using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
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
        Name = "MediaMore",
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Flyout = new OverflowMenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight }
    };
    private readonly Button _selMore = new()
    {
        Name = "MediaSelectionMore",
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Flyout = new OverflowMenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight }
    };
    // The fields of the filter surface that a view may hold back, opened by this toggle until the view changes (R13: nothing is saved).
    private readonly ToggleButton _moreFilters = new() { Name = "MediaMoreFilters", IsVisible = false, Classes = { "toolbar-toggle" } };

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
        WorkViewElement.SetId(_focus, "media.toolbar.focus");
        WorkViewElement.SetId(_refresh, "media.toolbar.refresh");
        WorkViewElement.SetId(_shuffle, "media.toolbar.shuffle");
        WorkViewElement.SetId(_missing, "media.filter.missing"); // a filter since S3: a field of the section and a chip of the active filters
        WorkViewElement.SetId(_exportAll, "media.toolbar.export-all");
        WorkViewElement.SetId(_manageRatings, "media.toolbar.manage-ratings");
        WorkViewElement.SetId(_booruOpen, "media.toolbar.booru");
        _booruOpen.Click += (_, _) => InvokeBooruOverflow();
        WorkViewElement.SetId(_booruMode, "media.toolbar.tag-list"); // MP4 (the "Booru mode" switch, since MP5 "Tag list")
        WorkViewElement.SetId(_layoutButton, "media.toolbar.layout"); // MP5
        WorkViewElement.SetId(_panelToggle, "media.toolbar.panel"); // S-4: direct media-sidebar affordance beside Layout
        WorkViewElement.SetId(_queueWalk, "media.toolbar.queue-walk"); // MP3
        WorkViewElement.SetId(_taggingPageOpen, "media.toolbar.tagging-page");
        WorkViewElement.SetId(_queueAll, "media.toolbar.queue-all"); // QW-A #10: "Add all shown to the tagging queue…" was only in the chip menu and the context menus
        WorkViewElement.SetId(_mediaFolders, "media.toolbar.folders"); // QW-A #1
        _mediaFolders.Click += (_, _) => { if (!_lifetime.IsCancellationRequested) OpenMediaFoldersRequested?.Invoke(); };
        WireQueueTools();
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
        WorkViewElement.SetId(_filterAdd, "media.filter.add"); // M2: "+ Filter" behind the chips, shown with the row
        WorkViewElement.SetId(_untaggedLink, "media.status.untagged");
        WorkViewElement.SetId(_selectionText, "media.selection.status");
        WorkViewElement.SetId(_selGallery, "media.selection.gallery");
        WorkViewElement.SetId(_selClear, "media.selection.clear");
        WorkViewElement.SetId(_selMore, "media.selection.more");
        WorkViewElement.SetId(_selTag, "media.selection.tag");
        WorkViewElement.SetId(_selExport, "media.selection.export");
        WorkViewElement.SetId(_selQueue, "media.selection.queue");
        ((MenuFlyout)_more.Flyout!).Opening += (_, _) =>
        {
            // Availability may change without a view switch. Keep the count and menu in the same projection.
            RefreshWorkView();
            List<Control> items = HeaderOverflow();
            UpdateMore(_more, items);
            FillMore((MenuFlyout)_more.Flyout!, items);
            // Never leave an empty overflow button or a bare flyout frame behind when
            // availability changed between layout and opening.
            if (items.Count == 0)
            {
                _more.IsVisible = false;
                Dispatcher.UIThread.Post(() => _more.Flyout?.Hide());
            }
        };
        ((MenuFlyout)_selMore.Flyout!).Opening += (_, _) =>
        {
            // Same rule as the header's More: never an empty "…" button or a bare flyout frame (design language, UX-LEDGER-01).
            List<Control> items = SelectionOverflow();
            UpdateMore(_selMore, items);
            FillMore((MenuFlyout)_selMore.Flyout!, items);
            if (items.Count == 0)
            {
                _selMore.IsVisible = false;
                Dispatcher.UIThread.Post(() => _selMore.Flyout?.Hide());
            }
        };
        _moreFilters.IsCheckedChanged += (_, _) => { if (!_localizing) RefreshWorkView(); };
        // The drawn "more" icon in the one icon-only square button (P6); the count in the name is set by UpdateMore.
        WorkViewMoreSurface.Apply(_more, Texts.T("workview.more"));
        WorkViewMoreSurface.Apply(_selMore, Texts.T("workview.more"));
        _workViewRegistered = true;
    }

    private void LocalizeWorkView()
    {
        _moreFilters.Content = Texts.T("workview.moreFilters");
        LocalizeBooruOverflow();
        RefreshWorkView(); // UpdateMore sets name and tooltip of both "More" buttons in the current language.
    }

    // ---- Presence ----

    // Which filter fields hold a value other than the default: a view never hides them (R6).
    private bool FieldActive(Control field) => WorkViewElement.GetId(field) switch
    {
        "media.filter.kind" => HasField(MediaFilterField.Type),
        "media.filter.source" => HasField(MediaFilterField.Source),
        "media.filter.board" => HasField(MediaFilterField.Board),
        "media.filter.rating" => HasField(MediaFilterField.Rating),
        "media.filter.tags" => HasField(MediaFilterField.Tag),
        "media.filter.sound" => HasField(MediaFilterField.Sound),
        "media.filter.without-media-tags" => HasField(MediaFilterField.Untagged),
        "media.filter.tag-mode" => TagModeNeeded,
        "media.filter.missing" => HasField(MediaFilterField.Missing),
        _ => false
    };

    private IEnumerable<Control> FilterFields() => new Control[]
    {
        _kindFilter, _favoritesOnly, _sourceFilter, _boardFilter, _minRating, _tagAdd,
        _soundFilter, _withoutMediaTags, _tagMode, _missing
    };

    // M5: the "all | any" switch matters from two included tags on (and while it is set to "any", so it can be set back).
    private bool TagModeNeeded => TagTerms(false).Count() > 1 || _tagMode.SelectedIndex > 0;

    private void RefreshWorkView()
    {
        if (!_workViewRegistered || _lifetime.IsCancellationRequested) return;
        WorkView view = _workView;
        // Header: the page's own state decides whether a tool applies at all; the view decides where it sits.
        WorkViewGate.Apply(_nameFilter, view);
        WorkViewGate.Apply(_filters, view);
        WorkViewGate.Apply(_sort, view);
        WorkViewGate.Apply(_focus, view);
        WorkViewGate.Apply(_refresh, view);
        WorkViewGate.Apply(_shuffle, view, stateVisible: Sort == MediaLibrarySort.Random);
        WorkViewGate.Apply(_exportAll, view);
        WorkViewGate.Apply(_manageRatings, view);
        WorkViewGate.Apply(_booruOpen, view, stateVisible: _canOpenBooru?.Invoke() == true);
        WorkViewGate.Apply(_booruMode, view);
        WorkViewGate.Apply(_layoutButton, view);
        WorkViewGate.Apply(_panelToggle, view, stateVisible: CanTogglePanel);
        WorkViewGate.Apply(_queueWalk, view, stateVisible: OpenTagQueueWalk != null);
        WorkViewGate.Apply(_taggingPageOpen, view, stateVisible: OpenTaggingPage != null);
        WorkViewGate.Apply(_queueAll, view, stateVisible: AddToTagQueue != null);
        WorkViewGate.Apply(_mediaFolders, view, stateVisible: OpenMediaFoldersRequested != null);
        ApplyToolbarLevel(); // what the page width moves under More as well (MediaPage.Toolbar.cs)
        UpdateMore(_more, HeaderOverflow());
        // Filter fields.
        bool opened = _moreFilters.IsChecked == true, held = false;
        foreach (Control field in FilterFields())
        {
            WorkViewGate.Apply(field, view, stateVisible: field != _tagMode || TagModeNeeded, nonDefaultState: opened || FieldActive(field));
            held |= WorkViewGate.HiddenByView(field);
        }
        _moreFilters.IsVisible = held || opened;
        WorkViewGate.Apply(_filterAdd, view, stateVisible: ActiveFilterCount > 0); // shown with the chip row
        AutomationProperties.SetName(_moreFilters, Texts.T("workview.moreFilters"));
        // UpdateUntaggedLink owns the link's state; the view only re-applies what the page last asked for.
        WorkViewGate.Apply(_untaggedLink, view, WorkViewElement.GetStateVisible(_untaggedLink));
        // Selection bar: the row itself is state (something is selected), so its More needs no state of its own.
        WorkViewGate.Apply(_selTag, view);
        WorkViewGate.Apply(_selExport, view);
        WorkViewGate.Apply(_selQueue, view);
        _selectionViewOverflow = Overflowed(_selTag, _selQueue, _selExport); // taken before the width hides more (a hidden control reads as "hidden by view")
        ApplySelectionLevel(); // what the selection line's width moves under its More as well (MediaPage.Selection.cs)
        UpdateMore(_selMore, SelectionOverflow());
    }

    // The tools of the header a view moved under More, in policy order, with their own state still wanting them (R6 raised the rest).
    // A tool the view leaves out of every surface (Hidden) is in no menu, so it neither fills More nor keeps its button alive (MediaToolbarLayout.IsUnderMore).
    private List<Control> HeaderOverflow() => Overflowed(_layoutButton, _focus, _refresh, _shuffle, _exportAll, _manageRatings, _booruOpen, _queueWalk, _taggingPageOpen, _queueAll, _mediaFolders)
        .Where(control => MediaToolbarLayout.IsUnderMore(_toolbarLevel, WorkViewElement.GetId(control), WorkViewPolicy.Resolve(WorkViewElement.GetId(control)!, _workView))).ToList();

    // The selection line's More: what the view moved there and what the line's width moved there (FitSelectionLine), in policy order.
    private List<Control> SelectionOverflow() => _selectionViewOverflow.Union(SelectionMovedByWidth(_selectionLevel))
        .OrderBy(control => WorkViewPolicy.Get(WorkViewElement.GetId(control)!).OverflowOrder).ToList();

    // What the view itself moved under the selection line's More, as RefreshWorkView found it before the line's width hid more.
    private List<Control> _selectionViewOverflow = new();

    private static List<Control> Overflowed(params Control[] controls) => controls.Where(WorkViewGate.HiddenByView)
        .OrderBy(control => WorkViewPolicy.Get(WorkViewElement.GetId(control)!).OverflowOrder).ToList();

    // R7: the button says how many actions it holds.
    private void UpdateMore(Button more, List<Control> items)
    {
        more.IsVisible = items.Count > 0;
        string name = Texts.Count("workview.moreCount", items.Count);
        WorkViewMoreSurface.Apply(more, name);
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
            // MP5: the layout button is a menu itself; under More it is a submenu with the same entries.
            if (ReferenceEquals(control, _layoutButton)) { flyout.Items.Add(LayoutSubmenu()); continue; }
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
            // A check box and a switch (the Booru mode, MP4) keep their state as a checked entry.
            if (control is ToggleButton check)
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
            if (ReferenceEquals(control, _booruOpen)) item.Name = "MediaBooruOpen";
            flyout.Items.Add(item);
        }
    }

    /// <summary>The overflow entries the header's More holds right now (tests and smokes).</summary>
    internal IReadOnlyList<string> HeaderMoreIds => HeaderOverflow().Select(control => WorkViewElement.GetId(control)!).ToArray();

    /// <summary>The overflow entries of the selection bar's More right now (tests and smokes).</summary>
    internal IReadOnlyList<string> SelectionMoreIds => SelectionOverflow().Select(control => WorkViewElement.GetId(control)!).ToArray();
}
