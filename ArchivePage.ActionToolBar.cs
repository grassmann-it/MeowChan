using System;
using System.Linq;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Desktop.Localization;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Ursa.Controls;

namespace MeowChan;

public partial class ArchivePage
{
    private bool ResultActionsReady => _available && !_busy && !_ended;
    private bool CanExportResults => ResultActionsReady && _openReader != null && _listTotal > 0
        && !Control<Border>("ArchiveExportDialog").IsVisible;

    private void InitializeArchiveResultActions()
    {
        Bind("ArchiveLayoutList", OnArchiveLayoutClick, () => _archiveLayout != "List");
        Bind("ArchiveLayoutTable", OnArchiveLayoutClick, () => _archiveLayout != "Table");
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
    internal Control? GalleryOriginFocusTarget(Control? origin)
    {
        ActionToolBar toolbar = Control<ActionToolBar>("ArchiveResultActions");
        if (origin == null || !toolbar.Items.Contains(origin)) return origin;
        TopLevel? root = TopLevel.GetTopLevel(toolbar);
        if (root != null && origin.IsEffectivelyVisible
            && ReferenceEquals(TopLevel.GetTopLevel(origin), root)
            && origin.GetVisualAncestors().Contains(toolbar)) return origin;
        if (toolbar.OverflowHost?.Children.Contains(origin) != true) return origin;
        return toolbar.More is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } more
            && root != null && ReferenceEquals(TopLevel.GetTopLevel(more), root) ? more : null;
    }

    private void RefreshArchiveResultActions()
    {
        ActionToolBar toolbar = Control<ActionToolBar>("ArchiveResultActions");
        foreach (object? item in toolbar.Items)
        {
            if (item is not Button button) continue;
            // All future feature actions participate, including saved searches and
            // reader groups. Never is reserved for the compact view selection.
            bool viewChoice = button.Name is "ArchiveLayoutList" or "ArchiveLayoutTable";
            ToolBar.SetOverflowMode(button, viewChoice ? OverflowMode.Never
                : _archiveColumnMode == "Barebones" && button.Name != "ArchiveResultsGallery"
                    ? OverflowMode.Always : OverflowMode.AsNeeded);
            CommandBindings.Refresh(button);
            // Feature actions must refresh with the page even when they were
            // created before the current language or hosted outside MainWindow.
            string? featureId = button.Name switch
            {
                "ArchiveSaveSearch" => "archive.saved-searches.save-current",
                "ArchiveSavedSearches" => "archive.saved-searches.open-manager",
                "ArchiveReaderGroups" => "reader-groups.open-manager",
                "ArchivePostBookmarks" => "post-bookmarks.open-manager",
                _ => null
            };
            if (featureId != null)
                CommandBindings.Apply(button, featureId, iconWithLabel: button.Name == "ArchivePostBookmarks");
            string? reason = button.Name switch
            {
                "ArchiveResultsGallery" or "ArchiveResultsExport" when !ResultActionsReady => Texts.T("archive.command.busy"),
                "ArchiveResultsGallery" or "ArchiveResultsExport" when _listTotal == 0 => Texts.T("archive.noResults"),
                "ArchiveFavoritesOverview" when !ResultActionsReady => Texts.T("archive.command.busy"),
                _ => null
            };
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
        toolbar.InvalidateMeasure();
    }
}
