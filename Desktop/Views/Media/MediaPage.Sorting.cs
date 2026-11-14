using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Criteria and direction controls for the archive-wide media presentation.</summary>
public sealed partial class MediaPage
{
    private readonly Button _sortDirection = new() { Name = "MediaSortDirection", MinWidth = 38, Padding = new Avalonia.Thickness(8, 0) };
    private MediaLibrarySortDirection _sortDirectionValue = MediaLibrarySortDirection.Default;

    private sealed record SortChoice(MediaLibrarySort Value, string Label)
    {
        public override string ToString() => Label;
    }

    internal MediaLibrarySortDirection SortDirection => _sortDirectionValue;
    internal Button SortDirectionButton => _sortDirection;

    private MediaLibrarySort SelectedSort => _sort.SelectedItem is SortChoice choice
        ? choice.Value
        : Enum.IsDefined(typeof(MediaLibrarySort), _sort.SelectedIndex)
            ? (MediaLibrarySort)_sort.SelectedIndex
            : MediaLibrarySort.ByThread;

    private static MediaLibrarySort[] SortChoices() => Enum.GetValues<MediaLibrarySort>();

    private string SortLabel(MediaLibrarySort sort) => sort switch
    {
        MediaLibrarySort.ByThread => Texts.T("media.sort.thread"),
        MediaLibrarySort.Newest => Texts.T("media.sort.newest"),
        MediaLibrarySort.Random => Texts.T("media.sort.random"),
        MediaLibrarySort.Captured => Texts.T("media.sort.captured"),
        MediaLibrarySort.Name => Texts.T("media.sort.name"),
        MediaLibrarySort.Size => Texts.T("media.sort.size"),
        MediaLibrarySort.References => Texts.T("media.sort.references"),
        MediaLibrarySort.TagCount => Texts.T("media.sort.tagCount"),
        MediaLibrarySort.Rating => Texts.T("media.sort.rating"),
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown media sort.")
    };

    private void OnSortChanged()
    {
        UpdateSortDirection();
        if (!_localizing) Present();
    }

    private void ToggleSortDirection()
    {
        if (!MediaLibraryView.HasDirection(Sort)) return;
        _sortDirectionValue = _sortDirectionValue == MediaLibrarySortDirection.Default
            ? MediaLibrarySortDirection.Reversed : MediaLibrarySortDirection.Default;
        UpdateSortDirection();
        Present();
    }

    private void UpdateSortDirection()
    {
        MediaLibrarySort sort = Sort;
        bool available = MediaLibraryView.HasDirection(sort);
        _sortDirection.IsEnabled = available;
        if (!available)
        {
            _sortDirection.Content = "↕";
            ToolTip.SetTip(_sortDirection, Texts.T("media.sort.direction.unavailable"));
            AutomationProperties.SetName(_sortDirection, Texts.T("media.sort.direction"));
            return;
        }

        bool descending = MediaLibraryView.IsDescendingByDefault(sort);
        if (_sortDirectionValue == MediaLibrarySortDirection.Reversed) descending = !descending;
        string key = descending ? "media.sort.direction.descending" : "media.sort.direction.ascending";
        _sortDirection.Content = descending ? "↓" : "↑";
        ToolTip.SetTip(_sortDirection, Texts.T(key));
        AutomationProperties.SetName(_sortDirection, Texts.T(key));
    }

    private void LocalizeSortControls()
    {
        MediaLibrarySort selected = Sort;
        SortChoice[] choices = SortChoices().Select(sort => new SortChoice(sort, SortLabel(sort))).ToArray();
        _sort.ItemsSource = choices;
        _sort.SelectedIndex = Array.FindIndex(choices, choice => choice.Value == selected);
        AutomationProperties.SetName(_sortDirection, Texts.T("media.sort.direction"));
        UpdateSortDirection();
    }

    private MediaLibraryUserOrder? UserOrderFor(MediaLibrarySnapshot snapshot)
    {
        if (_userOrder?.IsFor(snapshot.Entries, _userData) == true) return _userOrder;
        return _userOrder = MediaLibraryUserOrder.Of(snapshot.Entries, _userData);
    }
}
