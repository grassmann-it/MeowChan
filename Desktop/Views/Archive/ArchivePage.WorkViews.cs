using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (plan AP5, WV2): what the archive list shows per view. The rows are WorkViewPolicy.Archive; this file applies them
// to the header tools, the search scope chip, the filter surface and its field groups. The results bar and the columns keep
// their own appliers (ArchivePage.ActionToolBar.cs, ArchivePage.ViewPreferences.cs). The view reaches the page through
// ArchiveColumnModePreference, which the window sets from the same setting WorkViews.Current reads.
public partial class ArchivePage
{
    private WorkView? _appliedWorkView;
    private bool _filterGroupsReady;

    // The filter fields in surface order (WorkViewPolicy.ArchiveFilterGroupIds); each is a group registered under its policy id.
    private Control[] FilterGroups => new Control[]
    {
        ArchiveFilterGroupSource, ArchiveFilterGroupBoard, ArchiveFilterGroupState, ArchiveFilterGroupTags, ArchiveFilterGroupTagMode,
        ArchiveFilterGroupCapture, ArchiveFilterGroupPosts, ArchiveFilterGroupWork, ArchiveFilterGroupSort, ArchiveFilterGroupDate,
        ArchiveFilterGroupZone
    };

    private void InitializeArchiveWorkView()
    {
        // The surface opens with the groups placed for what the fields hold right now (R6); a closed surface is not reshuffled.
        ArchiveFilters.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty && e.NewValue is true) PlaceArchiveFilterGroups();
        };
        _appliedWorkView = ArchiveWorkView;
        ApplyArchiveWorkView(viewChanged: false);
    }

    /// <summary>Applies the view to the parts of the page that are not toolbars of the results bar; the results bar follows in RefreshArchiveResultActions.</summary>
    private void ApplyArchiveWorkView(bool viewChanged)
    {
        WorkView view = ArchiveWorkView;
        ApplyArchiveSearchScope();
        // R13: entering a view closes the filter surface and "More filters"; later the user's choice stays.
        WorkViewPresenter.ApplyToggle(ArchiveFilters, view, viewChanged);
        WorkViewPresenter.ApplyExpander(ArchiveMoreFilters, view, viewChanged);
        PlaceArchiveFilterGroups();
    }

    /// <summary>Called by <see cref="ArchiveColumnModePreference"/>: true when this was a switch to another view.</summary>
    private bool NoteArchiveWorkView()
    {
        WorkView view = ArchiveWorkView;
        bool changed = _appliedWorkView is { } previous && previous != view;
        _appliedWorkView = view;
        return changed;
    }

    // ---- Header tools: one toolbar, so a reduced tool sits under the same "More" ----

    private void ApplyArchiveHeaderActions()
    {
        WorkView view = ArchiveWorkView;
        ActionToolBar toolbar = ArchiveHeaderActions;
        WorkViewPresenter.ApplyToolBar(toolbar, view);
        // Under More an icon alone would not say what the entry is; on the bar it stays the compact icon.
        bool underMore = WorkViewPolicy.Resolve("archive.header.refresh", view) == Presence.Overflow;
        CommandBindings.Apply(RefreshArchiveButton, "archive.refresh", iconOnly: !underMore, iconWithLabel: underMore);
        foreach (Button button in new[] { ArchiveTransferPreview, ArchiveCleanupOpen, RefreshArchiveButton }) CommandBindings.Refresh(button);
        AutomationProperties.SetName(toolbar, Texts.T("nav.archive"));
        toolbar.RefreshOverflowLabel();
        toolbar.InvalidateMeasure();
    }

    // ---- Search scope chip (R6: any scope other than Text is shown, whatever the view) ----

    private void ApplyArchiveSearchScope()
    {
        if (ArchiveSearchScope is null) return;
        WorkViewGate.Apply(ArchiveSearchScope, ArchiveWorkView, stateVisible: true, nonDefaultState: ArchiveScope != SearchScope.Text);
        // Without the chip the field says how to reach the scopes.
        bool chipShown = ArchiveSearchScope.IsVisible;
        ToolTip.SetTip(ArchiveSearchText, chipShown ? null : Texts.T("search.scope.tip") + " · Alt+↓");
    }

    // ---- Filter surface: fields the view reduces go under "More filters" (the same surface) ----

    /// <summary>Whether the field group holds a value, an applied filter or a draft error: such a group is shown directly (R6).</summary>
    private bool FilterGroupHoldsState(string id)
    {
        ArchiveSearchSpec? applied = AppliedSearchSpec;
        bool Date() => !string.IsNullOrWhiteSpace(ArchiveStartDate.RawText) || !string.IsNullOrWhiteSpace(ArchiveEndDate.RawText)
            || applied?.DateRange != null || ReferenceEquals(_invalidSearchEditor, ArchiveStartDate) || ReferenceEquals(_invalidSearchEditor, ArchiveEndDate);
        return id switch
        {
            "archive.filters.state" => _draftStates.Count > 0 || ArchiveStateFilter.SelectedIndex > 0 || applied?.SourceStates.Count > 0,
            "archive.filters.tag-mode" => ArchiveTagMode.SelectedIndex > 0 || applied?.Tags?.Match == ArchiveTagMatch.Any,
            "archive.filters.capture" => ArchiveCaptureFilter.SelectedIndex > 0 || applied?.CaptureEnabled != null,
            "archive.filters.posts" => ArchivePostsFilter.SelectedIndex > 0 || applied?.HasPosts != null,
            "archive.filters.work" => ArchiveWorkFilter.SelectedIndex > 0 || (applied != null && applied.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All),
            "archive.filters.date" => Date(),
            // The zone explains how the dates are read: it comes with any date value.
            "archive.filters.zone" => Date() || _draftTimeZone != null,
            _ => false
        };
    }

    /// <summary>Whether <paramref name="group"/> sits on the surface (true) or under "More filters" (false) in this view.</summary>
    internal bool FilterGroupOnSurface(Control group)
    {
        string id = WorkViewElement.GetId(group)!;
        return WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, ArchiveWorkView, FilterGroupHoldsState(id)));
    }

    // Rebuilds the two lists only when something has to move, so an unchanged placement never touches focus.
    private void PlaceArchiveFilterGroups()
    {
        if (ArchiveMoreFilters is null) return;
        Control[] groups = FilterGroups;
        Control[] direct = groups.Where(FilterGroupOnSurface).ToArray();
        Control[] reduced = groups.Except(direct).ToArray();
        Control[] mainTarget = direct.Append(ArchiveMoreFilters).ToArray();
        bool firstTime = !_filterGroupsReady;
        _filterGroupsReady = true;
        if (!ArchiveFilterFields.Children.SequenceEqual(mainTarget))
        {
            ArchiveFilterFields.Children.Clear();
            ArchiveMoreFilterFields.Children.Clear();
            ArchiveFilterFields.Children.AddRange(mainTarget);
            ArchiveMoreFilterFields.Children.AddRange(reduced);
        }
        else if (!ArchiveMoreFilterFields.Children.SequenceEqual(reduced))
        {
            ArchiveMoreFilterFields.Children.Clear();
            ArchiveMoreFilterFields.Children.AddRange(reduced);
        }
        ArchiveMoreFilters.IsVisible = reduced.Length > 0;
        ArchiveMoreFilters.Header = Texts.T("workview.moreFilters");
        AutomationProperties.SetName(ArchiveMoreFilters, Texts.T("workview.moreFilters"));
        if (firstTime) ArchiveMoreFilters.IsExpanded = false;
    }

    /// <summary>
    /// A field with an error is shown (R6): the group moves out of "More filters" when its draft holds something, and the
    /// section opens as a last resort so the field is reachable before the reveal focuses it.
    /// </summary>
    private void EnsureFilterEditorShown(Control editor)
    {
        PlaceArchiveFilterGroups();
        if (editor.GetLogicalAncestors().Contains(ArchiveMoreFilterFields)) ArchiveMoreFilters.IsExpanded = true;
    }

    /// <summary>
    /// Self-check for the work-views smoke: the page's controls sit where WorkViewPolicy puts them in the current view.
    /// Returns one line per deviation (empty when everything matches).
    /// </summary>
    internal IReadOnlyList<string> WorkViewPlacementProblems()
    {
        List<string> problems = new();
        WorkView view = ArchiveWorkView;
        foreach (Control group in FilterGroups)
        {
            string id = WorkViewElement.GetId(group)!;
            bool onSurface = FilterGroupOnSurface(group);
            bool inMore = ReferenceEquals(group.Parent, ArchiveMoreFilterFields);
            if (inMore == onSurface) problems.Add($"{id} sits {(inMore ? "under More filters" : "on the surface")} but {WorkViewPolicy.Resolve(id, view, FilterGroupHoldsState(id))} says the opposite.");
        }
        if (ArchiveMoreFilters.IsVisible != (ArchiveMoreFilterFields.Children.Count > 0))
            problems.Add("The More filters section is visible without fields, or hidden with fields.");
        bool chipExpected = WorkViewGate.Visible(ArchiveSearchScope, view, ArchiveScope != SearchScope.Text);
        if (ArchiveSearchScope.IsVisible != chipExpected) problems.Add($"archive.search.scope is {(ArchiveSearchScope.IsVisible ? "shown" : "hidden")} but the view says {chipExpected}.");
        foreach (Control item in ArchiveHeaderActions.Items.OfType<Control>())
        {
            string id = WorkViewElement.GetId(item)!;
            Presence presence = WorkViewPolicy.Resolve(id, view);
            if (Ursa.Controls.ToolBar.GetOverflowMode(item) != WorkViewPresenter.ToolBarMode(presence))
                problems.Add($"{id} has the overflow mode of another view ({presence} expected).");
        }
        return problems;
    }
}
