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

    private void UpdateArchiveEmptyState()
    {
        bool constrained = HasArchiveConstraints(_query, _workFilter);
        Control<StackPanel>("ArchiveEmptyState").IsVisible = _listLoaded && _listTotal == 0;
        Control<TextBlock>("ArchiveEmpty").Text = Texts.T(constrained ? "archive.noResults" : "archive.empty");
        Button reset = Control<Button>("ArchiveResetFilters");
        reset.Content = Texts.T("archive.resetFilters");
        reset.IsVisible = constrained;
    }

    private async void OnResetArchiveFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || !_available) return;
        foreach (string name in new[] { "ArchiveSearchText", "ArchiveSourceFilter", "ArchiveBoardFilter" })
            Control<TextBox>(name).Text = string.Empty;
        foreach (string name in new[] { "ArchiveStateFilter", "ArchiveCaptureFilter", "ArchivePostsFilter", "ArchiveWorkFilter" })
            Control<ComboBox>(name).SelectedIndex = 0;
        // Sorting does not restrict results. Preserve it and use the normal explicit search path.
        await ApplySearchAsync();
        Control<TextBox>("ArchiveSearchText").Focus();
    }
}
