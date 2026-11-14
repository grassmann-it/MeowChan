using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan;

// C3: every active media filter is a removable chip in one row under the toolbar, visible while the filter surface is closed,
// with "Reset all" directly behind the last chip and the count on the toggle ("Filters (3)"). M2: the chips are the term set
// (MediaFilterTerm, one chip per term), built by the one chip factory of the archive and the media page
// (WorkspaceChips.CreateTermChip): tag, type, source and board chips switch between include and exclude (their toggle, a click on
// the body, Enter, or the context menu), the others only remove. "+ Filter" behind the chips opens the picker over every field.
// Removing a chip removes exactly that term, and keyboard focus stays in the row.
public sealed partial class MediaPage
{
    private readonly Button _filterResetAll = new()
    {
        Name = "MediaFilterResetAll", Classes = { "link" }, IsVisible = false, MinHeight = 24, Padding = new Thickness(8, 2),
        BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, VerticalContentAlignment = VerticalAlignment.Center
    };
    // "+ Filter": the picker over every field (types, sounds, sources, boards, ratings, flags and the own tags), behind the last chip.
    private readonly FilterTermPicker _filterAdd = new() { Name = "MediaFilterAdd", VerticalAlignment = VerticalAlignment.Top };
    // A filter that matches nothing offers the way back in the empty grid as well.
    private readonly Button _emptyReset = new() { Name = "MediaEmptyReset", IsVisible = false };
    private readonly Button _emptySearchInArchive = new() { Name = "MediaEmptySearchInArchive", IsVisible = false };
    private string[] _renderedChips = Array.Empty<string>();

    // Term: the chip's term (null for the tag mode chip); ValueName: what its switch names ("Switch to exclude: Videos").
    private sealed record FilterChip(string Key, string Label, Action Remove, MediaFilterTerm? Term = null, string? ValueName = null)
    {
        internal bool Negatable => Term != null && MediaFilterTerm.CanExclude(Term.Field);
    }

    /// <summary>The number of active filter values, one per chip; the toggle shows it.</summary>
    internal int ActiveFilterCount { get; private set; }
    internal Button FilterResetAll => _filterResetAll;
    /// <summary>The "+ Filter" picker at the end of the chip row.</summary>
    internal FilterTermPicker FilterAdd => _filterAdd;

    /// <summary>The chip row (chips, "+ Filter", then "Reset all" behind them) that replaces the bare chip panel in the header.</summary>
    private Control BuildFilterChipRow()
    {
        _filterError.Classes.Add("workspace-error");
        _filterResetAll.Click += (_, _) =>
        {
            bool focused = _filterResetAll.IsFocused || FocusWithin(_filterChips);
            ResetFilters();
            // The row is gone after a reset; the toggle is the nearest place to continue.
            if (focused) FilterSurfaceToggle.Focus(NavigationMethod.Tab);
        };
        _emptyReset.Click += (_, _) =>
        {
            bool focused = _emptyReset.IsFocused;
            ResetFilters();
            if (focused) FocusGrid();
        };
        _emptyActions.Children.Add(_emptyReset);
        _emptySearchInArchive.Click += (_, _) =>
        {
            string name = _nameFilter.Text ?? string.Empty;
            if (!_lifetime.IsCancellationRequested && name.Length > 0) SearchInArchiveRequested?.Invoke(name);
        };
        _emptyActions.Children.Add(_emptySearchInArchive);
        _filterAdd.Snapshot = () => _userData.TagSnapshot;
        _filterAdd.Used = UsedTags;
        _filterAdd.Others = PickerChoices;
        _filterAdd.PickedTerm = AddFilterTerm;
        _filterAdd.IsVisible = false;
        // "+ Filter" and "Reset all" follow the last chip directly (M1): the columns are Auto, so the chips are held to the row width
        // minus both by hand; they wrap there, and the two stay on the first line at the chip height.
        Grid row = new() { Name = "MediaFilterChipRow", ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Background = Avalonia.Media.Brushes.Transparent };
        // M3: a right click on the row writes the whole filter as a query (every chip's menu does the same).
        _copyFiltersItem.Click += (_, _) => _ = CopyFiltersAsTextAsync();
        row.ContextMenu = new ContextMenu { ItemsSource = new Control[] { _copyFiltersItem } };
        row.Children.Add(_filterChips);
        row.Children.Add(_filterAdd); Avalonia.Controls.Grid.SetColumn(_filterAdd, 1);
        row.Children.Add(_filterResetAll); Avalonia.Controls.Grid.SetColumn(_filterResetAll, 2);
        _filterResetAll.HorizontalAlignment = HorizontalAlignment.Left;
        void FitChips()
        {
            double add = _filterAdd.IsVisible ? Math.Ceiling(_filterAdd.DesiredSize.Width) + row.ColumnSpacing : 0;
            double width = row.Bounds.Width - row.ColumnSpacing - add - Math.Ceiling(_filterResetAll.DesiredSize.Width);
            if (width < 120) width = 120;
            if (double.IsInfinity(_filterChips.MaxWidth) || Math.Abs(_filterChips.MaxWidth - width) > 0.5) _filterChips.MaxWidth = width;
        }
        row.SizeChanged += (_, _) => FitChips();
        _filterResetAll.SizeChanged += (_, _) => FitChips();
        _filterAdd.SizeChanged += (_, _) => FitChips();
        return row;
    }

    /// <summary>
    /// One chip per term of the filter (the set plus the file name), in the order the chips had before M2: tags, the tag mode, thread,
    /// without media tags, name, type, sound, source, board, rating, favourites, missing. Labels reuse the field texts, so a chip reads
    /// like the choice it undoes; an excluded value says "Without …".
    /// </summary>
    private List<FilterChip> ActiveFilterChips()
    {
        List<FilterChip> chips = new();
        bool modeShown = false;
        void AddMode()
        {
            if (modeShown) return;
            modeShown = true;
            if (TagTerms(false).Count() > 1 && _tagMode.SelectedIndex == 1)
                chips.Add(new FilterChip("tagMatch", Texts.T("tags.filter.anyChip"), () => _tagMode.SelectedIndex = 0));
        }
        foreach (MediaFilterTerm term in MediaFilterTermSet.Ordered(AllTerms()))
        {
            if (term.Field != MediaFilterField.Tag) AddMode();
            string key = (term.Exclude ? "-" : "+") + MediaFilterTermSet.IdentityKey(term);
            void Add(string label, string? valueName = null) => chips.Add(new FilterChip(key, label, () => RemoveFilterTerm(term), term, valueName));
            if (TermLabel(term) is { } text) Add(text.Label, text.ValueName);
        }
        AddMode();
        return chips;
    }

    /// <summary>
    /// The chip text of a term and what its switch names ("Switch to exclude: Videos"); null for a term without a chip. The chips and the
    /// "added from the search field" note (M3) read the same wording.
    /// </summary>
    private (string Label, string? ValueName)? TermLabel(MediaFilterTerm term)
    {
        switch (term.Field)
        {
            case MediaFilterField.Tag:
                string tag = TagLabel(Guid.Parse(term.Value));
                return (Texts.T(term.Exclude ? "tags.filter.excludeChip" : "tags.filter.includeChip", tag), tag);
            case MediaFilterField.Thread:
                return (Texts.T("media.filter.threadChip", _threadTitle ?? "#" + term.Value), null);
            case MediaFilterField.Untagged:
                // Reached from the status line while the filter fields are closed, so it is removable without opening them.
                return (Texts.T("media.filter.withoutTags"), null);
            case MediaFilterField.Name:
                return (Texts.T("media.filter.nameChip", term.Value.Trim()), null);
            case MediaFilterField.Type when MediaFilterTerm.TypeOf(term.Value) is { } kind:
                return (Texts.T(term.Exclude ? "media.filter.kindExcludeChip" : "media.filter.kindChip", KindLabel(kind)), KindLabel(kind));
            case MediaFilterField.Sound:
                return (Texts.T(term.Value == "with" ? "media.filter.sound.with" : "media.filter.sound.without"), null);
            case MediaFilterField.Source:
                return (Texts.T(term.Exclude ? "media.filter.sourceExcludeChip" : "archive.sourceChip", SourceLabel(term.Value)), SourceLabel(term.Value));
            case MediaFilterField.Board:
                string board = BoardLabel(term.Scope!, term.Value);
                return (Texts.T(term.Exclude ? "media.filter.boardExcludeChip" : "archive.boardChip", board), board);
            case MediaFilterField.Rating:
                return (Texts.T("media.filter.ratingChip", int.Parse(term.Value, System.Globalization.CultureInfo.InvariantCulture)), null);
            case MediaFilterField.Favorite:
                return (Texts.T("media.filter.favorites"), null);
            case MediaFilterField.Missing:
                // Showing captured but missing files is a filter like the others (S3): the chip says why the grid holds files that are gone.
                return (Texts.T("media.filter.missingChip"), null);
            default:
                return null;
        }
    }

    private void RenderFilterChips()
    {
        _filterResetAll.Content = Texts.T("media.filter.resetAll");
        ToolTip.SetTip(_filterResetAll, Texts.T("media.filter.resetAllHint"));
        AutomationProperties.SetName(_filterResetAll, Texts.T("media.filter.resetAllHint"));
        _emptyReset.Content = Texts.T("media.filter.reset");
        _copyFiltersItem.Header = Texts.T("media.filter.chip.copyText");
        List<FilterChip> chips = ActiveFilterChips();
        ActiveFilterCount = chips.Count;
        _filterAdd.IsVisible = chips.Count > 0; // the work view re-applies it below (media.filter.add)
        RefreshWorkView(); // an active filter keeps its field shown in every view (R6)
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
            MediaFilterTerm? term = chip.Term;
            ClosableTag tag = WorkspaceChips.CreateTermChip(chip.Label, chip.Negatable, term?.Exclude == true,
                () => RemoveFilterChip(position, chip.Remove), chip.Negatable ? () => ToggleFilterChip(term!) : null, chip.ValueName,
                menu: true, edit: term != null && FilterTermPicker.GroupOrder(term.Field) <= FilterTermPicker.GroupOrder(MediaFilterField.Untagged)
                    ? () => EditFilterChip(term.Field) : null, copyText: () => _ = CopyFiltersAsTextAsync());
            if (term != null) tag.Tag = MediaFilterTermSet.IdentityKey(term);
            _filterChips.Children.Add(tag);
        }
    }

    // "Edit…" on a chip: the picker opens on that field's entries (another type, another rating, …).
    private void EditFilterChip(MediaFilterField field)
    {
        if (_lifetime.IsCancellationRequested || !_filterAdd.IsEffectivelyVisible) return;
        _filterAdd.OpenFor(field);
    }

    // A switch rebuilds the chip (its key and place change); keyboard users stay on the toggle of the same value.
    private void ToggleFilterChip(MediaFilterTerm term)
    {
        bool focused = FocusWithin(_filterChips);
        if (ToggleFilterTerm(term) != null || !focused) return;
        string identity = MediaFilterTermSet.IdentityKey(term);
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            ClosableTag? chip = _filterChips.Children.OfType<ClosableTag>().FirstOrDefault(tag => tag.Tag as string == identity);
            if (chip != null && WorkspaceChips.TermChipToggle(chip) is { } toggle) toggle.Focus(NavigationMethod.Tab);
            else FilterSurfaceToggle.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Test seam of a chip's switch: switches the term with this identity (as the chip's toggle and body click do).</summary>
    internal string? ToggleFilterChipFor(MediaFilterTerm term) => ToggleFilterTerm(term);

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
            else FilterSurfaceToggle.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
