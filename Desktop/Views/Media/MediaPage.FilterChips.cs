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
using MeowChan.Desktop.MediaActions;
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
        Name = "MediaFilterResetAll", Classes = { "link" }, IsVisible = false, MinHeight = 32, Padding = new Thickness(8, 2),
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
        // M3: a right click on the row (or the Menu key on "+ Filter" or "Reset all") writes the whole filter as a query and resets it;
        // every chip's menu offers the same two.
        WorkspaceChips.AttachMenu(row, () => _lifetime.IsCancellationRequested || ActiveFilterCount == 0 ? null
            : FilterChipMenu.ComposeRow(Texts.T("media.filter.active"), () => _ = CopyFiltersAsTextAsync(), ResetFiltersFromChip),
            () => FilterSurfaceToggle.Focus(NavigationMethod.Tab));
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
            case MediaFilterField.Queue:
                // MP1: in the tagging queue, or (excluded) not in it; the switch names the queue.
                return (Texts.T(term.Exclude ? "media.filter.queueExcludeChip" : "media.filter.queueChip"), Texts.T("media.filter.queueChip"));
            case MediaFilterField.SourceTag:
                // MP4: a tag of the provider (Danbooru), as the tag list of the Booru mode adds it.
                return (Texts.T(term.Exclude ? "media.filter.sourceTagExcludeChip" : "media.filter.sourceTagChip", term.Value), term.Value);
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
        List<FilterChip> chips = ActiveFilterChips();
        ActiveFilterCount = chips.Count;
        _filterAdd.IsVisible = chips.Count > 0; // the work view re-applies it below (media.filter.add)
        RefreshWorkView(); // an active filter keeps its field shown in every view (R6)
        _filterChips.IsVisible = _filterResetAll.IsVisible = chips.Count > 0;
        UpdateChipRowVisibility(); // no chip: no row (and no empty gap in the header)
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
                menu: MediaFilterChipMenu.Options(term, _terms.ToArray(), term?.Field is MediaFilterField.Tag or MediaFilterField.SourceTag ? chip.ValueName : null, ChipActions));
            if (term != null) tag.Tag = MediaFilterTermSet.IdentityKey(term);
            _filterChips.Children.Add(tag);
        }
    }

    // Keyboard focus is in the chip row, or a chip's menu is open (its popup holds the focus): a removal or switch from the menu keeps
    // keyboard users in the row, like one made on the chip.
    private bool ChipRowHasFocus() => FocusWithin(_filterChips)
        || _filterChips.Children.OfType<ClosableTag>().Any(chip => WorkspaceChips.MenuHostOf(chip)?.OpenMenu != null);

    // What the entries of a chip's menu do. The menu is composed at every opening from the term set as it is then (the chip may outlive a change).
    private MediaFilterChipActions ChipActions => _chipActions ??= new MediaFilterChipActions(
        ShowOnly: ShowOnlyFilterChip, Edit: EditFilterChip, CopyTagName: name => _ = CopyTagNameAsync(name), CopyFilters: () => _ = CopyFiltersAsTextAsync(),
        QueueAll: () => _ = AddAllShownToQueueAsync(),
        QueueBlocked: () => AddToTagQueue != null && _tiles.Count > 0 && _building == null ? null : MediaMenuReasons.Unavailable,
        ResetAll: ResetFiltersFromChip, IsSuppressed: () => _lifetime.IsCancellationRequested);
    private MediaFilterChipActions? _chipActions;

    // "Show only this": the other terms of the chip's field go; a set that would no longer be one filter is refused with its reason.
    private void ShowOnlyFilterChip(MediaFilterTerm term)
    {
        if (_lifetime.IsCancellationRequested) return;
        bool focused = ChipRowHasFocus();
        if (ShowOnlyFilterTerm(term) != null || !focused) return;
        string identity = MediaFilterTermSet.IdentityKey(term);
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            ClosableTag? chip = _filterChips.Children.OfType<ClosableTag>().FirstOrDefault(tag => tag.Tag as string == identity);
            if (chip != null) chip.Focus(NavigationMethod.Tab);
            else FilterSurfaceToggle.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    // "Copy tag name": the own tag's name, with the same failure line as "Copy filters as text".
    private async System.Threading.Tasks.Task CopyTagNameAsync(string name)
    {
        if (_lifetime.IsCancellationRequested) return;
        await MenuClipboard.CopyAsync(this, name, message => { if (!_lifetime.IsCancellationRequested) ShowQueryHint(null, message); });
    }

    // "Reset all" in a menu: like the link behind the chips; a menu opened from the keyboard leaves the focus on the filter toggle.
    private void ResetFiltersFromChip()
    {
        if (_lifetime.IsCancellationRequested) return;
        ResetFilters();
        // The row is gone; the toggle is the nearest place. Posted ahead of the menu's own focus return, which then leaves it alone.
        Dispatcher.UIThread.Post(() => { if (!_lifetime.IsCancellationRequested) FilterSurfaceToggle.Focus(NavigationMethod.Tab); }, DispatcherPriority.Loaded);
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
        bool focused = ChipRowHasFocus();
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
        bool focused = ChipRowHasFocus();
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
