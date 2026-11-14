using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _listLoaded;

    internal static bool HasArchiveConstraints(ArchiveThreadQuery query, int workFilter) =>
        !string.IsNullOrWhiteSpace(query.Text) || query.Source != null || query.Board != null
        || query.SourceState != null || query.CaptureEnabled != null || query.HasPosts != null
        || workFilter != 0;

    internal static bool HasArchiveConstraints(ArchiveSearchSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.Text) || spec.Sources.Count > 0 || spec.Boards.Count > 0
        || spec.SourceStates.Count > 0 || spec.CaptureEnabled != null || spec.HasPosts != null
        || spec.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All || spec.DateRange != null || spec.Tags != null;

    private void UpdateArchiveEmptyState()
    {
        bool constrained = HasArchiveConstraints(_query);
        ArchiveEmptyState.IsVisible = _listLoaded && _listTotal == 0;
        ArchiveEmpty.Text = Texts.T(constrained ? "archive.noResults" : "archive.empty");
        Button reset = ArchiveResetFilters;
        reset.Content = Texts.T("archive.resetFilters");
        reset.IsVisible = constrained;
        // An archive that has nothing yet is filled from Downloads; a filter that matches nothing is reset instead.
        ArchiveGoDownloads.IsVisible = !constrained;
        // AP2/A6: a text search that finds nothing here can be tried in Media in one step.
        string text = _query.Text ?? string.Empty;
        Button media = ArchiveEmptySearchInMedia;
        media.IsVisible = constrained && text.Length > 0 && SearchInMediaRequested != null;
        if (media.IsVisible)
        {
            media.Content = Texts.T("search.carry.toMedia", text);
            Avalonia.Automation.AutomationProperties.SetName(media, Texts.T("search.carry.toMedia", text));
        }
    }

    private void OnEmptySearchInMediaClick(object? sender, RoutedEventArgs e)
    {
        string text = _query.Text ?? string.Empty;
        if (!_ended && text.Length > 0) SearchInMediaRequested?.Invoke(text);
    }

    /// <summary>The empty archive's next step: the Downloads command, labelled as a destination.</summary>
    private void LocalizeEmptyState() =>
        CommandBindings.ApplyLabeled(ArchiveGoDownloads, "navigation.downloads", Texts.T("nav.goDownloads"));

    /// <summary>Set by the shell: opens Downloads like the footer button does.</summary>
    internal Action? GoToDownloadsRequested { get; set; }

    private void OnGoDownloadsClick(object? sender, RoutedEventArgs e) => GoToDownloadsRequested?.Invoke();

    private async void OnResetArchiveFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || !CanRead) return;
        ArchiveSearchSpec reset = new(sort: _query.Sort);
        WriteSearchDraft(reset);
        _showReader = false; UpdateLayoutMode();
        await LoadListAsync(NewListLoad(ArchiveSearchRequest.Create(reset)));
        ArchiveScopedBox.Focus();
    }
}
