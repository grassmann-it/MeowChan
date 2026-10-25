using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
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
        || spec.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All || spec.DateRange != null;

    private void UpdateArchiveEmptyState()
    {
        bool constrained = HasArchiveConstraints(_query);
        Control<StackPanel>("ArchiveEmptyState").IsVisible = _listLoaded && _listTotal == 0;
        Control<TextBlock>("ArchiveEmpty").Text = Texts.T(constrained ? "archive.noResults" : "archive.empty");
        Button reset = Control<Button>("ArchiveResetFilters");
        reset.Content = Texts.T("archive.resetFilters");
        reset.IsVisible = constrained;
    }

    private async void OnResetArchiveFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || !_available) return;
        ArchiveSearchSpec reset = new(sort: _query.Sort);
        WriteSearchDraft(reset);
        _showReader = false; UpdateLayoutMode();
        await LoadListAsync(NewListLoad(ArchiveSearchRequest.Create(reset)));
        Control<TextBox>("ArchiveSearchText").Focus();
    }
}
