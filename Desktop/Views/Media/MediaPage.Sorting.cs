using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Criteria and direction controls for the archive-wide media presentation.</summary>
public sealed partial class MediaPage
{
    private readonly Button _sortDirection = new() { Name = "MediaSortDirection" };
    // The visible label of the order box ("Sort"), muted like the scope label inside the search field. The combo's accessible name is the same
    // text (the label is hidden from the accessibility tree so it is not read twice) and it follows the combo's visibility.
    private readonly TextBlock _sortLabel = new() { Name = "MediaSortLabel", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -2, 0) };
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

    // The order is kept in the settings (MediaSortPreference): the shell saves what the user chose and hands it back when the page is made.
    // The random order is not kept, so it reads back as the default order.
    private bool _restoringSort;

    /// <summary>Saves the criterion and direction the user just chose (the shell writes them to the settings). Null: not kept.</summary>
    internal Func<MediaLibrarySort, MediaLibrarySortDirection, Task>? SaveSortAsync { get; set; }

    /// <summary>
    /// Starts the page with a saved order, before it presents anything; nothing is saved again. The random order and a direction for an
    /// order without one are not restored (the default order is).
    /// </summary>
    internal void RestoreSort(MediaLibrarySort sort, MediaLibrarySortDirection direction)
    {
        // A random or unknown order falls back as a whole (its direction belonged to it, not to the default order).
        (sort, direction) = MediaSortPreference.Applicable(sort, direction);
        _restoringSort = true;
        try
        {
            _sortDirectionValue = direction;
            _sort.SelectedIndex = Array.IndexOf(SortChoices(), sort);
        }
        finally { _restoringSort = false; }
        UpdateSortDirection();
        RefreshWorkView();
    }

    private void OnSortChanged()
    {
        UpdateSortDirection();
        if (_localizing || _restoringSort) return;
        Present();
        KeepSortChoice();
    }

    private void ToggleSortDirection()
    {
        if (!MediaLibraryView.HasDirection(Sort)) return;
        _sortDirectionValue = _sortDirectionValue == MediaLibrarySortDirection.Default
            ? MediaLibrarySortDirection.Reversed : MediaLibrarySortDirection.Default;
        UpdateSortDirection();
        Present();
        KeepSortChoice();
    }

    private async void KeepSortChoice()
    {
        if (SaveSortAsync is not { } save || _lifetime.IsCancellationRequested) return;
        MediaLibrarySort sort = Sort;
        MediaLibrarySortDirection direction = _sortDirectionValue;
        try { await save(sort, direction); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.sort.save")) { /* Logged and shown; the choice still applies to this visit. */ }
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
        _sortLabel.Text = Texts.T("media.sort");
        AutomationProperties.SetName(_sort, _sortLabel.Text);
        UpdateSortDirection();
    }
}
