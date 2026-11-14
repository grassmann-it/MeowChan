using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Criteria and direction controls for the archive-wide media presentation.</summary>
public sealed partial class MediaPage
{
    private readonly Button _sortDirection = new() { Name = "MediaSortDirection" };
    private string? _sortIcon;
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
            // Disabled: the arrow shows the direction the order would have, dimmed; the tooltip says why it is off.
            ShowSortIcon("arrow-up", Texts.T("media.sort.direction"), Texts.T("media.sort.direction.unavailable"));
            return;
        }

        bool descending = MediaLibraryView.IsDescendingByDefault(sort);
        if (_sortDirectionValue == MediaLibrarySortDirection.Reversed) descending = !descending;
        string key = descending ? "media.sort.direction.descending" : "media.sort.direction.ascending";
        ShowSortIcon(descending ? "arrow-down" : "arrow-up", Texts.T(key), Texts.T(key));
    }

    // P6: a drawn catalog arrow in the icon-only button (the glyph arrows draw differently on every platform). The icon is created when
    // the button is shown, because it needs the platform's asset loader.
    private void ShowSortIcon(string icon, string name, string tip)
    {
        IconButtons.Apply(_sortDirection, icon, name);
        ToolTip.SetTip(_sortDirection, tip);
        if (_sortIcon == icon) return;
        _sortIcon = icon;
        void Show() => _sortDirection.Content = CommandBindings.NamedIcon(icon);
        if (_sortDirection.IsAttachedToVisualTree()) Show();
        else
        {
            void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                _sortDirection.AttachedToVisualTree -= OnAttached;
                if (_sortIcon == icon) Show();
            }
            _sortDirection.AttachedToVisualTree += OnAttached;
        }
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
