using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// C3: every active media filter is a removable chip in one row under the toolbar, visible while the filter surface is closed,
// with "Reset all" directly behind the last chip and the count on the toggle ("Filters (3)"). Same chip factory as the archive's applied filters
// (ArchivePage.CreateFilterTag); removing a chip resets exactly that field, and keyboard focus stays in the row.
public sealed partial class MediaPage
{
    private readonly Button _filterResetAll = new()
    {
        Name = "MediaFilterResetAll", Classes = { "link" }, IsVisible = false, MinHeight = 24, Padding = new Thickness(6, 2),
        BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, VerticalContentAlignment = VerticalAlignment.Center
    };
    // A filter that matches nothing offers the way back in the empty grid as well.
    private readonly Button _emptyReset = new() { Name = "MediaEmptyReset", IsVisible = false };
    private string[] _renderedChips = Array.Empty<string>();

    private sealed record FilterChip(string Key, string Label, Action Remove, bool Exclude = false);

    /// <summary>The number of active filter values, one per chip; the toggle shows it.</summary>
    internal int ActiveFilterCount { get; private set; }
    internal Button FilterResetAll => _filterResetAll;

    /// <summary>The chip row (chips, then "Reset all" behind the last one) that replaces the bare chip panel in the header.</summary>
    private Control BuildFilterChipRow()
    {
        _filterError.Classes.Add("workspace-error");
        _filterResetAll.Click += (_, _) =>
        {
            bool focused = _filterResetAll.IsFocused || FocusWithin(_filterChips);
            ResetFilters();
            // The row is gone after a reset; the toggle is the nearest place to continue.
            if (focused) FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
        };
        _emptyReset.Click += (_, _) =>
        {
            bool focused = _emptyReset.IsFocused;
            ResetFilters();
            if (focused) FocusGrid();
        };
        _emptyActions.Children.Add(_emptyReset);
        // "Reset all" follows the last chip directly (M1): both columns are Auto, so the chips are held to the row width
        // minus the link by hand; they wrap there, and the link stays on the first line at the chip height.
        Grid row = new() { Name = "MediaFilterChipRow", ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(_filterChips);
        row.Children.Add(_filterResetAll); Avalonia.Controls.Grid.SetColumn(_filterResetAll, 1);
        _filterResetAll.HorizontalAlignment = HorizontalAlignment.Left;
        void FitChips()
        {
            double width = row.Bounds.Width - row.ColumnSpacing - Math.Ceiling(_filterResetAll.DesiredSize.Width);
            if (width < 120) width = 120;
            if (double.IsInfinity(_filterChips.MaxWidth) || Math.Abs(_filterChips.MaxWidth - width) > 0.5) _filterChips.MaxWidth = width;
        }
        row.SizeChanged += (_, _) => FitChips();
        _filterResetAll.SizeChanged += (_, _) => FitChips();
        return row;
    }

    /// <summary>
    /// Every value the filter controls hold, in the order of the existing chips (tags, thread, without media tags) followed by the
    /// fields that had no chip before. Labels reuse the field texts, so a chip reads like the choice it undoes.
    /// </summary>
    private List<FilterChip> ActiveFilterChips()
    {
        List<FilterChip> chips = new();
        chips.AddRange(_includeTags.ToArray().Select(id => new FilterChip("tag+" + id, Texts.T("tags.filter.includeChip", TagLabel(id)),
            () => { _includeTags.Remove(id); RenderTagOptions(); FilterEdited(); })));
        chips.AddRange(_excludeTags.ToArray().Select(id => new FilterChip("tag-" + id, Texts.T("tags.filter.excludeChip", TagLabel(id)),
            () => { _excludeTags.Remove(id); RenderTagOptions(); FilterEdited(); }, Exclude: true)));
        if (_includeTags.Count > 1 && _tagMode.SelectedIndex == 1)
            chips.Add(new FilterChip("tagMatch", Texts.T("tags.filter.anyChip"), () => _tagMode.SelectedIndex = 0));
        if (_threadFilter is { } thread)
            chips.Add(new FilterChip("thread", Texts.T("media.filter.threadChip", thread.Title), () => { _threadFilter = null; FilterEdited(); }));
        // Reached from the status line while the filter fields are closed, so it is removable without opening them.
        if (_withoutMediaTags.IsChecked == true)
            chips.Add(new FilterChip("untagged", Texts.T("media.filter.withoutTags"), () => _withoutMediaTags.IsChecked = false));
        if (!string.IsNullOrWhiteSpace(_nameFilter.Text))
            chips.Add(new FilterChip("name", Texts.T("media.filter.nameChip", _nameFilter.Text.Trim()), () =>
            {
                _nameFilter.Text = string.Empty;
                FilterEdited(); // Text changes reach the filter after a dispatcher turn; the chip goes at once.
            }));
        if (_kindFilter.SelectedIndex > 0 && _kindFilter.SelectedItem is string kind)
            chips.Add(new FilterChip("kind", Texts.T("media.filter.kindChip", kind), () => _kindFilter.SelectedIndex = 0));
        if (SoundFilterActive && _soundFilter.SelectedItem is string sound)
            chips.Add(new FilterChip("sound", sound, () => _soundFilter.SelectedIndex = 0));
        if (_sourceFilter.SelectedItem is Choice { Value: { } source })
            chips.Add(new FilterChip("source", Texts.T("archive.sourceChip", SourceLabel(source)), () => _sourceFilter.SelectedIndex = 0));
        if (_boardFilter.SelectedItem is BoardChoice { Board: { } board, Source: { } boardSource })
            chips.Add(new FilterChip("board", Texts.T("archive.boardChip", _sourceFilter.SelectedItem is Choice { Value: not null }
                ? "/" + board + "/" : Texts.T("media.filter.boardOfSource", board, boardSource)), () => _boardFilter.SelectedIndex = 0));
        if (_minRating.SelectedIndex > 0)
            chips.Add(new FilterChip("rating", Texts.T("media.filter.ratingChip", _minRating.SelectedIndex), () => _minRating.SelectedIndex = 0));
        if (_favoritesOnly.IsChecked == true)
            chips.Add(new FilterChip("favorites", Texts.T("media.filter.favorites"), () => _favoritesOnly.IsChecked = false));
        return chips;
    }

    private void RenderFilterChips()
    {
        _filterResetAll.Content = Texts.T("media.filter.resetAll");
        ToolTip.SetTip(_filterResetAll, Texts.T("media.filter.resetAllHint"));
        AutomationProperties.SetName(_filterResetAll, Texts.T("media.filter.resetAllHint"));
        _emptyReset.Content = Texts.T("media.filter.reset");
        List<FilterChip> chips = ActiveFilterChips();
        ActiveFilterCount = chips.Count;
        _filterChips.IsVisible = _filterResetAll.IsVisible = chips.Count > 0;
        // An unchanged row is kept as it is: a late text event must not rebuild the chips under the keyboard focus.
        string[] rendered = chips.Select(chip => chip.Key + "\u0001" + chip.Label).ToArray();
        if (rendered.SequenceEqual(_renderedChips) && _filterChips.Children.Count == chips.Count) return;
        _renderedChips = rendered;
        _filterChips.Children.Clear();
        for (int index = 0; index < chips.Count; index++)
        {
            FilterChip chip = chips[index];
            int position = index;
            _filterChips.Children.Add(ArchivePage.CreateFilterTag(chip.Label, () => RemoveFilterChip(position, chip.Remove), chip.Exclude));
        }
    }

    // Like the archive's chips: a removal made from the row keeps keyboard users in the row (the neighbour chip), or on the
    // filter toggle once the row is empty. Pointer removals and other re-renders leave focus alone.
    private void RemoveFilterChip(int position, Action remove)
    {
        bool focused = FocusWithin(_filterChips);
        remove();
        if (!focused) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            if (_filterChips.Children.Count > 0) _filterChips.Children[Math.Min(position, _filterChips.Children.Count - 1)].Focus(NavigationMethod.Tab);
            else FilterSurfaceToggle?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
