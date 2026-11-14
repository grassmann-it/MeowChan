using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Desktop.Localization;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Ursa.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public partial class ArchivePage
{
    private bool ResultActionsReady => _available && !_busy && !_ended && !IsFilenameSearchOpen;
    private bool CanExportResults => ResultActionsReady && !_urlTextExportBusy && _openReader != null && _listTotal > 0
        && !ArchiveExportDialog.IsVisible;

    private void InitializeArchiveResultActions()
    {
        Bind("ArchiveLayoutList", OnArchiveLayoutClick);
        Bind("ArchiveLayoutTable", OnArchiveLayoutClick);
        Bind("ArchiveThreadColumnsButton", OnArchiveColumnsClick);
        Bind("ArchiveResultsGallery", OnResultsGalleryClick, () => ResultActionsReady && _listTotal > 0);
        Bind("ArchiveFavoritesOverview", OnFavoritesOverviewClick, () => ResultActionsReady);
        Bind("ArchiveResultsExport", OnResultsExportClick, () => CanExportResults);

        void Bind(string name, Action<object?, RoutedEventArgs> execute, Func<bool>? enabled = null)
        {
            Button button = Control<Button>(name);
            button.Command = new DelegateCommand(() => execute(button, new RoutedEventArgs()), enabled);
        }
    }

    // The original action remains the semantic origin, but a closed overflow
    // has no focusable window presentation. Resolve at return time after layout
    // or mode changes, without reopening the popup or executing any action.
    internal Control? GalleryOriginFocusTarget(Control? origin) => ActionOriginFocusTarget(origin);

    // Works for every ActionToolBar item of this page (results and reader toolbars).
    internal Control? ActionOriginFocusTarget(Control? origin)
    {
        if (origin == null) return null;
        // Items stay logical children of their toolbar in the direct panel and in a closed popup alike.
        if (origin.GetLogicalAncestors().OfType<ActionToolBar>().FirstOrDefault() is not { } toolbar
            || !toolbar.Items.Contains(origin)) return origin;
        TopLevel? root = TopLevel.GetTopLevel(toolbar);
        if (root != null && origin.IsEffectivelyVisible
            && ReferenceEquals(TopLevel.GetTopLevel(origin), root)
            && origin.GetVisualAncestors().Contains(toolbar)) return origin;
        // A reduced origin (A19: a shortcut or the context menu ran an action the work view moved off the bar): it sits in the closed
        // "More" popup or is left out by the view ("K"). Either way focus returns to the toolbar's "More", never to the hidden control.
        Control? more = toolbar.More is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } toggle
            && root != null && ReferenceEquals(TopLevel.GetTopLevel(toggle), root) ? toggle : null;
        if (toolbar.OverflowHost?.Children.Contains(origin) == true) return more;
        if (more != null) return more;
        // Left out and nothing overflows (no "More" on the bar): the first action still shown keeps the keyboard in the toolbar.
        return root == null ? origin : toolbar.Items.OfType<Control>().FirstOrDefault(item =>
            item.Focusable && item.IsEffectivelyVisible && item.IsEffectivelyEnabled && ReferenceEquals(TopLevel.GetTopLevel(item), root));
    }

    // What each results-bar action was last presented for: its disabled reason and the language. The presentation is rewritten
    // only when that changes (shortcut changes reach it through the CommandBindings observer).
    private readonly Dictionary<string, (string? Reason, string Language)> _resultActionPresented = new(StringComparer.Ordinal);

    private bool ResultActionStale(string name, string? reason)
    {
        (string? Reason, string Language) now = (reason, Texts.Language);
        if (_resultActionPresented.TryGetValue(name, out var last) && last == now) return false;
        _resultActionPresented[name] = now;
        return true;
    }

    private void RefreshArchiveResultActions()
    {
        ActionToolBar toolbar = ArchiveResultActions;
        bool placementChanged = false;
        foreach (object? item in toolbar.Items)
        {
            if (item is SplitButton split)
            {
                // AP2: the saved-searches split button overflows like every other feature action (its policy row decides).
                WorkViewPresenter.ApplyToolBarItem(split, ArchiveWorkView, "archive.results.other", false, out bool splitChanged);
                placementChanged |= splitChanged;
                if (split.Name == "ArchiveSavedSearches") ApplySavedSearchSplit();
                continue;
            }
            if (item is not Button button) continue;
            // All future feature actions participate, including saved searches and
            // reader groups. Never is reserved for the compact view selection.
            // The List/Table choice is Required (never moves); items without an id use archive.results.other.
            WorkViewPresenter.ApplyToolBarItem(button, ArchiveWorkView, "archive.results.other", false, out bool buttonChanged);
            placementChanged |= buttonChanged;
            CommandBindings.Refresh(button);
            // Feature actions must refresh with the page even when they were
            // created before the current language or hosted outside MainWindow.
            string? featureId = button.Name switch
            {
                "ArchiveReaderGroups" => "reader-groups.open-manager",
                "ArchivePostBookmarks" => "post-bookmarks.open-manager",
                _ => null
            };
            if (featureId != null && ResultActionStale(button.Name + "#feature", null))
                CommandBindings.Apply(button, featureId, iconWithLabel: button.Name == "ArchivePostBookmarks");
            string? reason = button.Name switch
            {
                "ArchiveResultsGallery" or "ArchiveResultsExport" when !ResultActionsReady => Texts.T("archive.command.busy"),
                "ArchiveResultsGallery" or "ArchiveResultsExport" when _listTotal == 0 => Texts.T("archive.noResults"),
                "ArchiveFavoritesOverview" when !ResultActionsReady => Texts.T("archive.command.busy"),
                _ => null
            };
            if (button.Name is "ArchiveResultsGallery" or "ArchiveFavoritesOverview" or "ArchiveResultsExport"
                && !ResultActionStale(button.Name, reason)) continue;
            if (button.Name == "ArchiveResultsGallery")
                CommandBindings.Apply(button, "gallery.open-results", () => reason, iconWithLabel: true);
            else if (button.Name == "ArchiveFavoritesOverview")
                CommandBindings.Apply(button, "favorites.open", () => reason, iconWithLabel: true);
            else if (button.Name == "ArchiveResultsExport")
            {
                string tip = CommandBindings.Tooltip("export.open-all-results-html-dialog", reason)
                    + " · " + Texts.T("export.resultsScope");
                ToolTip.SetTip(button, tip);
                AutomationProperties.SetHelpText(button, tip);
            }
        }
        AutomationProperties.SetName(toolbar, Texts.T("nav.archive"));
        toolbar.RefreshOverflowLabel();
        if (placementChanged) toolbar.InvalidateMeasure();
        ApplyArchiveHeaderActions();
    }
}
