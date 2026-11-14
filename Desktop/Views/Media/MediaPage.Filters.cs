using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Filters of the media page. They apply live: the index is in memory, so every change filters again.
// A filter part that cannot be evaluated (deleted tag, store unavailable) empties the grid with an
// explanation; it is never dropped, because that would silently show more than asked for.
public sealed partial class MediaPage
{
    private int _tagFilterTagsPerList = ArchiveTagFilter.DefaultTagsPerList;
    internal int ArchiveTagFilterTagsPerListPreference
    {
        get => _tagFilterTagsPerList;
        set => _tagFilterTagsPerList = Math.Clamp(value, ArchiveTagFilter.MinimumTagsPerList, ArchiveTagFilter.MaximumConfigurableTagsPerList);
    }

    // Over a large index every keystroke would filter, sort and group again on the UI thread: the name waits 200 ms
    // after the last edit. Up to SearchImmediateEntries entries filtering is cheap and follows every keystroke.
    private static readonly TimeSpan NameFilterDebounce = TimeSpan.FromMilliseconds(200);
    private int _searchImmediateEntries = 2000;
    internal int SearchImmediateEntries { get => _searchImmediateEntries; set { _searchImmediateEntries = value; UpdateSearchResult(); } }
    private readonly SearchBox _nameFilter = new() { Name = "MediaNameFilter", MinWidth = 180, MaxLength = MediaLibraryFilter.MaximumNameLength };
    private readonly SearchScopeChip _searchScope = new() { Name = "MediaSearchScope", Scopes = new[] { SearchScope.FileName } };
    private readonly SearchCarryOffer _searchCarryOffer = new() { Name = "MediaSearchCarryOffer" };
    private readonly ComboBox _kindFilter = new() { Name = "MediaKindFilter" };
    private readonly ComboBox _sourceFilter = new() { Name = "MediaSourceFilter" };
    private readonly ComboBox _boardFilter = new() { Name = "MediaBoardFilter" };
    private readonly CheckBox _favoritesOnly = new() { Name = "MediaFavoritesOnly" };
    private readonly CheckBox _withoutMediaTags = new() { Name = "MediaWithoutMediaTags" };
    private readonly ComboBox _minRating = new() { Name = "MediaMinRating" };
    // The tag part of the surface: one "Add tag…" popover with search; the tags in the filter are the chips of the header row.
    private readonly TagFilterPicker _tagAdd = new() { Name = "MediaTagAdd" };
    // The tag mode is a switch "all | any" beside the tag button (M5); index 0 = every included tag, 1 = at least one.
    private readonly TwoWayToggle _tagMode = new() { Name = "MediaTagMode" };
    private readonly WrapPanel _filterChips = new() { Name = "MediaFilterChips", ItemSpacing = 8, LineSpacing = 8 };
    private readonly TextBlock _filterError = new() { Name = "MediaFilterError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    // M2: the term set is the one truth of the filter. The section's combos and check boxes show it (SyncFilterControls) and write
    // it (EditTerms); the chips and the "+ Filter" picker read and write it the same way. Two parts stay outside the set: the
    // file name lives in the search field (it filters live and debounced) and joins the set when the filter is read, and the tag
    // mode (all / at least one) is a property of the tag terms, not a term.
    private readonly List<MediaFilterTerm> _terms = new();
    private string? _threadTitle;
    private MediaLibraryUserData _userData = MediaLibraryUserData.Unavailable;
    // T1: which occurrences lack media tags, decided once per index and tag state; the status line counts the tiles among the matches.
    private MediaLibraryUntagged? _untagged;
    private int? _untaggedTiles;
    private readonly TextBlock _untaggedText = new() { TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
    private readonly Button _untaggedLink = new() { Name = "MediaUntaggedCount", Padding = new Thickness(0), MinHeight = 0, BorderThickness = new Thickness(0),
        Classes = { "link" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private MediaLibrarySnapshot? _optionsFor;
    private bool _filterFailed;
    private bool _filterActive;
    private Window? _owner;

    // Summary: the entry a combo shows while the set holds more than one value of its field ("2 types", "Excluded: 1"); it adds nothing.
    private sealed record Choice(string? Value, string Label, bool Summary = false) { public override string ToString() => Label; }
    private sealed record BoardChoice(string? Source, string? Board, string Label, bool Summary = false) { public override string ToString() => Label; }

    /// <summary>Reads the profile's own tags, favorites and ratings; null parts are unavailable.</summary>
    internal Func<MediaLibraryUserData>? ReadUserData { get; set; }
    internal string? FilterError => _filterError.IsVisible ? _filterError.Text : null;
    internal WrapPanel FilterChips => _filterChips;

    /// <summary>The filter as terms, in chip order: the set plus the file name typed in the search field (tests and smokes).</summary>
    internal IReadOnlyList<MediaFilterTerm> FilterTerms => MediaFilterTermSet.Ordered(AllTerms());

    private IEnumerable<MediaFilterTerm> AllTerms()
    {
        foreach (MediaFilterTerm term in _terms) yield return term;
        if (!string.IsNullOrWhiteSpace(_nameFilter.Text)) yield return MediaFilterTerm.Name(_nameFilter.Text);
    }

    private bool HasField(MediaFilterField field) => _terms.Any(term => term.Field == field);
    private MediaFilterTerm? Included(MediaFilterField field) => _terms.FirstOrDefault(term => term.Field == field && !term.Exclude);
    private IEnumerable<Guid> TagTerms(bool exclude) =>
        _terms.Where(term => term.Field == MediaFilterField.Tag && term.Exclude == exclude).Select(term => Guid.Parse(term.Value));
    private bool HasTag(Guid id) => MediaFilterTermSet.Contains(_terms, MediaFilterTerm.Tag(id));
    private IReadOnlySet<TagId> UsedTags() => _terms.Where(term => term.Field == MediaFilterField.Tag).Select(term => new TagId(Guid.Parse(term.Value))).ToHashSet();

    /// <summary>Replaces the set by <paramref name="change"/>'s result, mirrors it into the section and filters again.</summary>
    private void EditTerms(Func<IReadOnlyList<MediaFilterTerm>, IReadOnlyList<MediaFilterTerm>> change)
    {
        IReadOnlyList<MediaFilterTerm> next = change(_terms.ToArray());
        _terms.Clear(); _terms.AddRange(next);
        if (!HasField(MediaFilterField.Thread)) _threadTitle = null;
        SyncFilterControls();
        RenderTagOptions();
        FilterEdited();
    }

    // Whether the set with the file name still makes one filter (ToFilter refuses e.g. every type excluded).
    private bool IsValidFilter(IEnumerable<MediaFilterTerm> terms)
    {
        try { _ = MediaFilterTerms.ToFilter(terms.Concat(AllTerms().Where(term => term.Field == MediaFilterField.Name))); return true; }
        catch (ArgumentException) { return false; }
    }

    private Control BuildFilterRow()
    {
        Control row = BuildFilterFields(); // the grid of the fields (MediaPage.FilterGrid.cs)
        InstallFilterHandoffInterruption();
        AutomationProperties.SetLiveSetting(_filterError, AutomationLiveSetting.Assertive);
        _tagAdd.Snapshot = () => _userData.TagSnapshot;
        _tagAdd.Used = UsedTags;
        _tagAdd.Picked = (id, exclude) => TryIncludeOrExclude(id, !exclude);
        _nameFilter.QueryChanged += (_, _) => { if (!_lifetime.IsCancellationRequested) { ClearQueryHint(); FilterEdited(); } };
        _searchScope.CarryActions = SearchCarryActions;
        _tagMode.SelectionChanged += (_, _) => FilterEdited();
        // The fields write the term set; a sound choice implies videos (MediaFilterTermSet), and another type drops the sound.
        _kindFilter.SelectionChanged += (_, _) =>
        {
            int index = _kindFilter.SelectedIndex;
            if (_localizing || index is < 0 or > (int)MediaLibraryKindFilter.Other) return; // the "2 types" summary adds nothing
            EditTerms(terms => index == 0 ? MediaFilterTermSet.WithTypes(terms) : MediaFilterTermSet.WithTypes(terms, (MediaLibraryKindFilter)index));
        };
        _soundFilter.SelectionChanged += (_, _) =>
        {
            if (_localizing || _soundFilter.SelectedIndex < 0) return;
            EditTerms(terms => _soundFilter.SelectedIndex == 0 ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Sound)
                : MediaFilterTermSet.With(terms, MediaFilterTerm.Sound((MediaLibrarySoundFilter)_soundFilter.SelectedIndex)));
        };
        _sourceFilter.SelectionChanged += (_, _) =>
        {
            if (_localizing || _sourceFilter.SelectedItem is not Choice { Summary: false } choice) return;
            EditTerms(terms => choice.Value == null ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Source, exclude: false)
                : MediaFilterTermSet.With(terms, MediaFilterTerm.Source(choice.Value)));
        };
        _boardFilter.SelectionChanged += (_, _) =>
        {
            if (_localizing || _boardFilter.SelectedItem is not BoardChoice { Summary: false } choice) return;
            EditTerms(terms => choice is { Board: { } board, Source: { } source } ? MediaFilterTermSet.With(terms, MediaFilterTerm.Board(source, board))
                : MediaFilterTermSet.WithoutField(terms, MediaFilterField.Board, exclude: false));
        };
        _minRating.SelectionChanged += (_, _) =>
        {
            if (_localizing || _minRating.SelectedIndex < 0) return;
            EditTerms(terms => _minRating.SelectedIndex == 0 ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Rating)
                : MediaFilterTermSet.With(terms, MediaFilterTerm.Rating(_minRating.SelectedIndex)));
        };
        WireFlag(_favoritesOnly, MediaFilterField.Favorite);
        WireFlag(_withoutMediaTags, MediaFilterField.Untagged);
        WireFlag(_missing, MediaFilterField.Missing);
        AttachedToVisualTree += (_, _) =>
        {
            _owner = TopLevel.GetTopLevel(this) as Window;
            if (_owner != null) _owner.Activated += OnOwnerActivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_owner != null) _owner.Activated -= OnOwnerActivated;
            _owner = null;
        };
        return row;
    }

    private void FilterEdited()
    {
        if (_localizing) return;
        NoteBooruHandoffInteraction();
        PresentEditedFilters();
    }

    // The Booru reset is an expected programmatic edit, not user takeover. It shares the same rendering
    // transaction, without advancing the interaction revision captured before the handoff.
    private void PresentEditedFilters()
    {
        // This pass reads the name as typed; a debounced run for the same text would only repeat it.
        _nameFilter.CancelPendingQuery();
        RenderFilterChips();
        Present();
    }

    private void InstallFilterHandoffInterruption()
    {
        // TextChanged is deferred until rendering: IME/accessibility/paste must revoke the handoff now,
        // even while QueryChanged is debounced. Programmatic term/control synchronization is _localizing.
        _nameFilter.TextChanging += (_, _) => NoteBooruHandoffInteraction();
        // Click can be delivered by accessibility/commands without a page pointer/key event. These
        // actions change the ordering, not the filter terms, so they do not go through FilterEdited.
        _sortDirection.Click += (_, _) => NoteBooruHandoffInteraction();
        _shuffle.Click += (_, _) => NoteBooruHandoffInteraction();
    }

    // Coming back from the gallery (favorite, rating or tag changed there) re-reads the own data.
    private void OnOwnerActivated(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible) RefreshUserData();
    }

    /// <summary>Re-reads tags, favorites and ratings; filters again only when one of them changed.</summary>
    internal void RefreshUserData()
    {
        if (ReadUserData == null || _lifetime.IsCancellationRequested) return;
        MediaLibraryUserData next;
        try { next = ReadUserData(); }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException) { return; }
        if (next.SameSourcesAs(_userData)) return;
        _userData = next;
        RenderTagOptions();
        RenderFilterChips();
        if (_snapshot != null) Present();
    }

    private void WireFlag(CheckBox box, MediaFilterField field) => box.IsCheckedChanged += (_, _) =>
    {
        if (_localizing) return;
        EditTerms(terms => box.IsChecked == true ? MediaFilterTermSet.With(terms, MediaFilterTerm.Flag(field)) : MediaFilterTermSet.WithoutField(terms, field));
    };

    /// <summary>The filter the terms describe. Throws ArgumentException for an invalid combination.</summary>
    internal MediaLibraryFilter CurrentFilter() =>
        MediaFilterTerms.ToFilter(AllTerms(), _tagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All);

    /// <summary>Shows the term set in the section's fields, without their change reactions.</summary>
    private void SyncFilterControls()
    {
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            RenderKindOptions();
            _soundFilter.SelectedIndex = _terms.FirstOrDefault(term => term.Field == MediaFilterField.Sound)?.Value switch { "with" => 1, "without" => 2, _ => 0 };
            _minRating.SelectedIndex = Included(MediaFilterField.Rating) is { } rating ? int.Parse(rating.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            _favoritesOnly.IsChecked = HasField(MediaFilterField.Favorite);
            _withoutMediaTags.IsChecked = HasField(MediaFilterField.Untagged);
            _missing.IsChecked = HasField(MediaFilterField.Missing);
            RenderSourceOptions();
        }
        finally { _localizing = localizing; }
    }

    // The type combo: All types, the four types, and while the set holds anything else than one included type a summary
    // ("2 types", "3 types" for "without videos") that stays selected until the user picks a single type again.
    private void RenderKindOptions()
    {
        List<string> items = new[] { "media.filter.anyKind", "media.filter.kind.image", "media.filter.kind.gif", "media.filter.kind.video", "media.filter.kind.other" }
            .Select(key => Texts.T(key)).ToList();
        MediaFilterTerm[] types = _terms.Where(term => term.Field == MediaFilterField.Type).ToArray();
        int index = 0;
        if (types.Length == 1 && !types[0].Exclude && MediaFilterTerm.TypeOf(types[0].Value) is { } kind) index = (int)kind;
        else if (types.Length > 0)
        {
            items.Add(Texts.Count("media.filter.kind.count", MediaFilterTermSet.ShownKinds(types)?.Count ?? 0));
            index = items.Count - 1;
        }
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            if (_kindFilter.ItemsSource is not IEnumerable<string> shown || !shown.SequenceEqual(items)) _kindFilter.ItemsSource = items.ToArray();
            _kindFilter.SelectedIndex = index;
        }
        finally { _localizing = localizing; }
    }

    /// <summary>The "N without media tags" link of the status line: shown while it would narrow the matches to untagged media.</summary>
    private Control BuildStatusRow(TextBlock status)
    {
        _untaggedLink.Content = _untaggedText;
        _untaggedLink.Click += (_, _) => ShowUntagged();
        WrapPanel row = new() { ItemSpacing = 8, LineSpacing = 2 };
        status.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(status); row.Children.Add(_untaggedLink);
        return row;
    }

    private void UpdateUntaggedLink()
    {
        int count = _untaggedTiles ?? 0;
        bool shown = _snapshot != null && !_filterFailed && count > 0;
        WorkViewGate.Apply(_untaggedLink, _workView, shown); // the view may leave the count out (Barebones); state is `shown`
        if (!shown) return;
        _untaggedText.Text = Texts.T("media.untagged.count", count);
        string hint = Texts.T("media.untagged.hint", count);
        ToolTip.SetTip(_untaggedLink, hint);
        AutomationProperties.SetName(_untaggedLink, hint);
    }

    /// <summary>Applies "without media tags" on top of the current filters: the starting point for tagging.</summary>
    internal void ShowUntagged()
    {
        bool focused = _untaggedLink.IsFocused;
        _withoutMediaTags.IsChecked = true;
        // The link hides once the filter is on; keyboard focus continues on the tiles to tag.
        if (focused) FocusGrid();
    }

    /// <summary>A tag chip of a tile: only media that carry this tag, added to the tags already included.</summary>
    internal void FilterToTag(Guid id)
    {
        // A shown tile never carries an excluded tag, so a chip can only be new or already included.
        if (HasTag(id) || _userData.TagSnapshot?.Find(new TagId(id)) == null) return;
        bool focused = FocusWithin(_grid);
        if (!IncludeOrExclude(id, include: true)) return;
        if (focused) FocusGrid();
    }

    /// <summary>Shift+click on a tag chip of a tile: hide the media with this tag (S7). Excluding takes the shown tile out of the grid.</summary>
    internal void ExcludeTag(Guid id)
    {
        if (HasTag(id) || _userData.TagSnapshot?.Find(new TagId(id)) == null) return;
        bool focused = FocusWithin(_grid);
        if (!IncludeOrExclude(id, include: false)) return;
        if (focused) FocusGrid();
    }

    /// <summary>Thread as a meta tag: only the media of this thread.</summary>
    /// <summary>Shows only one import batch (or any borrowed thread identity) by its identity and title.</summary>
    internal void FilterToThread(ResourceId thread, string title)
    {
        ArgumentNullException.ThrowIfNull(thread);
        _threadTitle = string.IsNullOrWhiteSpace(title) ? "#" + thread.Thread : title;
        EditTerms(terms => MediaFilterTermSet.With(terms, MediaFilterTerm.InThread(thread)));
    }

    internal void FilterToThread(MediaLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        FilterToThread(entry.Thread, entry.ThreadTitle ?? string.Empty);
    }

    internal void ResetFilters()
    {
        _localizing = true;
        try
        {
            _nameFilter.SetTextSilently(string.Empty);
            _tagMode.SelectedIndex = 0;
            _terms.Clear();
            _threadTitle = null;
            SyncFilterControls();
        }
        finally { _localizing = false; }
        ClearQueryHint();
        RenderTagOptions();
        FilterEdited();
    }

    /// <summary>Adds a tag to the filter as an inclusion or an exclusion (the popover and the tests); null when added, else why not.</summary>
    internal string? AddTagFilter(Guid id, bool include) => TryIncludeOrExclude(id, include);

    /// <summary>Test seam of the tag popover: adds the own tag with this display name; null when added, else why not.</summary>
    internal string? AddTagFilterByName(string name, bool include) =>
        _userData.TagSnapshot?.Tags.FirstOrDefault(tag => tag.Name.Display == name) is { } found
            ? TryIncludeOrExclude(found.Id.Value, include) : "Unknown tag: " + name;

    /// <summary>Test seam of the chip toggle: switches the own tag with this display name between include and exclude.</summary>
    internal string? ToggleTagFilterByName(string name) =>
        _userData.TagSnapshot?.Tags.FirstOrDefault(tag => tag.Name.Display == name) is { } found ? ToggleTagFilter(found.Id.Value) : "Unknown tag: " + name;

    /// <summary>Test seam: the names the tag popover offers for an empty search, in ordinal order.</summary>
    internal string[] AddableTagNames => TagFilterPicker.Choices(_userData.TagSnapshot, UsedTags(), null)
        .Select(item => item.Name.Display).Order(StringComparer.Ordinal).ToArray();

    private bool IncludeOrExclude(Guid id, bool include)
    {
        string? refusal = TryIncludeOrExclude(id, include);
        if (refusal == null) return true;
        _filterError.Text = refusal;
        _filterError.IsVisible = true;
        return false;
    }

    private string? TryIncludeOrExclude(Guid id, bool include)
    {
        if (HasTag(id)) return null;
        Guid[] included = TagTerms(false).ToArray(), excluded = TagTerms(true).ToArray();
        // SP5: the configured tags-per-list limit refuses with its reason; the ceiling guards the filter itself.
        if (ArchivePage.DraftTagRefusal(include ? included : excluded, Array.Empty<Guid>(), id,
                ArchiveTagFilterTagsPerListPreference) is string refusal) return refusal;
        try
        {
            _ = new ArchiveTagFilter(include ? included.Append(id) : included, include ? excluded : excluded.Append(id),
                ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);
        }
        catch (ArgumentException) { return Texts.T("coreLimits.tags.invalid"); }
        EditTerms(terms => MediaFilterTermSet.With(terms, MediaFilterTerm.Tag(id, exclude: !include)));
        return null;
    }

    /// <summary>
    /// The include/exclude switch of a tag chip (its toggle, a click on its body, Enter, or its menu): moves the tag to the other list.
    /// Null when switched (or not in the filter), else the localized reason, also shown on the error line.
    /// </summary>
    internal string? ToggleTagFilter(Guid id)
    {
        MediaFilterTerm? held = _terms.FirstOrDefault(term => term.Field == MediaFilterField.Tag && term.Value == id.ToString("D"));
        if (held == null) return null;
        Guid[] included = TagTerms(false).ToArray(), excluded = TagTerms(true).ToArray();
        string? refusal = ArchivePage.DraftTagRefusal(!held.Exclude ? excluded : included, Array.Empty<Guid>(), id,
            ArchiveTagFilterTagsPerListPreference);
        if (refusal == null)
        {
            try
            {
                _ = new ArchiveTagFilter(!held.Exclude ? included.Where(tag => tag != id) : included.Append(id),
                    !held.Exclude ? excluded.Append(id) : excluded.Where(tag => tag != id), ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);
            }
            catch (ArgumentException) { refusal = Texts.T("coreLimits.tags.invalid"); }
        }
        if (refusal != null)
        {
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
            return refusal;
        }
        EditTerms(terms => MediaFilterTermSet.Toggled(terms, held));
        return null;
    }

    /// <summary>
    /// The include/exclude switch of any negatable chip (tag, type, source, board). Null when switched, else the localized reason
    /// (a set that would no longer be one filter, e.g. every type excluded), also shown on the error line.
    /// </summary>
    internal string? ToggleFilterTerm(MediaFilterTerm term)
    {
        if (term.Field == MediaFilterField.Tag) return ToggleTagFilter(Guid.Parse(term.Value));
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.Toggled(_terms.ToArray(), term);
        // MP4: a source tag that switches lists counts against the tags-per-list limit of its new list.
        if (term.Field == MediaFilterField.SourceTag && _terms.FirstOrDefault(other => MediaFilterTermSet.Identity(other) == MediaFilterTermSet.Identity(term)) is { } held
            && SourceTagRefusal(held.Toggled()) is { } limit)
        {
            _filterError.Text = limit;
            _filterError.IsVisible = true;
            return limit;
        }
        if (!IsValidFilter(next))
        {
            string refusal = Texts.T("media.filter.add.refused");
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
            return refusal;
        }
        EditTerms(_ => next);
        return null;
    }

    /// <summary>
    /// "Show only this" on a chip: drops the other terms of its field (the other tags, types, sources or boards). Null when done, else the
    /// localized reason (a set that would no longer be one filter), also shown on the error line.
    /// </summary>
    internal string? ShowOnlyFilterTerm(MediaFilterTerm term)
    {
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.OnlyThis(_terms.ToArray(), term);
        if (!IsValidFilter(next))
        {
            string refusal = Texts.T("media.filter.add.refused");
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
            return refusal;
        }
        EditTerms(_ => next);
        return null;
    }

    /// <summary>
    /// Adds a term from the "+ Filter" picker (or a test). Null when added, else the localized reason it was refused: a tag beyond
    /// the list limit, or a term that would not make one filter with the others.
    /// </summary>
    internal string? AddFilterTerm(MediaFilterTerm term)
    {
        if (term.Field == MediaFilterField.Tag) return TryIncludeOrExclude(Guid.Parse(term.Value), include: !term.Exclude);
        if (term.Field == MediaFilterField.Thread && term.Resource != null)
        {
            FilterToThread(term.Resource, term.Value);
            return null;
        }
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.With(_terms.ToArray(), term);
        if (!IsValidFilter(next)) return Texts.T("media.filter.add.refused");
        EditTerms(_ => next);
        return null;
    }

    /// <summary>Removes a term (a chip's close mark, Delete, or its menu).</summary>
    private void RemoveFilterTerm(MediaFilterTerm term)
    {
        if (term.Field == MediaFilterField.Name)
        {
            _nameFilter.Text = string.Empty;
            FilterEdited(); // Text changes reach the filter after a dispatcher turn; the chip goes at once.
            return;
        }
        EditTerms(terms => MediaFilterTermSet.Without(terms, term));
    }

    /// <summary>
    /// What the "+ Filter" picker offers besides the own tags: every type, sound, source and board of the index, the ratings and the
    /// three flags, without what the set already holds. Read from the index snapshot; no store access.
    /// </summary>
    private IReadOnlyList<FilterTermChoice> PickerChoices()
    {
        List<FilterTermChoice> choices = new();
        void Offer(MediaFilterTerm term, string label) { if (!MediaFilterTermSet.Contains(_terms, term)) choices.Add(new FilterTermChoice(term.Field, label, term)); }
        foreach (MediaLibraryKindFilter kind in new[] { MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other })
            Offer(MediaFilterTerm.Type(kind), KindLabel(kind));
        Offer(MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), Texts.T("media.filter.sound.with"));
        Offer(MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound), Texts.T("media.filter.sound.without"));
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        foreach (string source in entries.Select(entry => entry.Thread.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Offer(MediaFilterTerm.Source(source), SourceLabel(source));
        string? included = Included(MediaFilterField.Source)?.Value;
        foreach ((string source, string board) in entries.Select(entry => (entry.Thread.Source, entry.Thread.Board)).Distinct()
            .Where(pair => included == null || pair.Source == included)
            .OrderBy(pair => pair.Board, StringComparer.Ordinal).ThenBy(pair => pair.Source, StringComparer.Ordinal))
            Offer(MediaFilterTerm.Board(source, board), BoardLabel(source, board));
        for (int least = 1; least <= 5; least++) Offer(MediaFilterTerm.Rating(least), Texts.T("media.filter.minRating", least));
        Offer(MediaFilterTerm.Flag(MediaFilterField.Favorite), Texts.T("media.filter.favorites"));
        Offer(MediaFilterTerm.Flag(MediaFilterField.Untagged), Texts.T("media.filter.withoutTags"));
        Offer(MediaFilterTerm.Flag(MediaFilterField.Missing), Texts.T("media.showMissing"));
        if (ReadTagQueue != null) Offer(MediaFilterTerm.InQueue(), Texts.T("media.filter.queueChip")); // MP1
        return choices;
    }

    private static string KindLabel(MediaLibraryKindFilter kind) => Texts.T("media.filter.kind." + MediaFilterTerm.TypeValue(kind));

    // A board is "/g/" while its source is the included one, else "/g/ (source)", as in the board combo.
    private string BoardLabel(string source, string board) =>
        Included(MediaFilterField.Source)?.Value == source ? "/" + board + "/" : Texts.T("media.filter.boardOfSource", board, source);

    private string TagLabel(Guid id) => _userData.TagSnapshot?.Find(new TagId(id))?.Name.Display ?? Texts.T("tags.filter.deleted");

    // The popovers read the tags and the filter when they open; the buttons only say whether something is left to add.
    private void RenderTagOptions() { _tagAdd.Refresh(); _filterAdd.Refresh(); }

    // Keep a selected identity even if a new index no longer contains it: dropping it would widen the results.
    private static string UnavailableOption(string label) => label + " (" + Texts.T("mediahash.unavailable") + ")";
    private void RenderSourceOptions()
    {
        _optionsFor = _snapshot;
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        string? selected = Included(MediaFilterField.Source)?.Value;
        List<Choice> options = new[] { new Choice(null, Texts.T("media.filter.anySource")) }
            .Concat(entries.Select(entry => entry.Thread.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(source => new Choice(source, SourceLabel(source)))).ToList();
        if (selected != null && !options.Any(option => option.Value == selected))
            options.Add(new Choice(selected, UnavailableOption(SourceLabel(selected))));
        int excluded = _terms.Count(term => term.Field == MediaFilterField.Source && term.Exclude);
        Choice? summary = selected == null && excluded > 0 ? new Choice(null, Texts.T("media.filter.combo.excluded", excluded), Summary: true) : null;
        if (summary != null) options.Add(summary);
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            _sourceFilter.ItemsSource = options;
            _sourceFilter.SelectedItem = summary ?? options.FirstOrDefault(option => option.Value == selected) ?? options[0];
        }
        finally { _localizing = localizing; }
        RenderBoardOptions();
    }

    // The provider source is shown by its name; chan sources keep their identifier.
    private static string SourceLabel(string source) => source == BooruMediaIdentity.Source ? Texts.T("booru.media.source")
        : source == Import.ManualImportIdentity.Source ? Texts.T("import.media.source")
        : source == FolderSources.FolderSourceIdentity.Source ? Texts.T("folderSource.media.source") : source;

    // A folder source is one "board" of the folder source: it is shown by its name ("Folder: Holiday"), never by its id.
    private string BoardLabel(string source, string board, bool withSource)
    {
        if (source == FolderSources.FolderSourceIdentity.Source)
        {
            string? name = _snapshot?.Entries.FirstOrDefault(entry => entry.Thread.Source == source && entry.Thread.Board == board)?.ThreadTitle;
            return Texts.T("folderSource.media.board", name ?? Texts.T("folderSource.media.unknown"));
        }
        return withSource ? Texts.T("media.filter.boardOfSource", board, source) : "/" + board + "/";
    }

    private void RenderBoardOptions()
    {
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        string? source = Included(MediaFilterField.Source)?.Value;
        BoardChoice? selected = Included(MediaFilterField.Board) is { Scope: { } boardSource } board ? new BoardChoice(boardSource, board.Value, board.Value) : null;
        List<BoardChoice> options = new[] { new BoardChoice(null, null, Texts.T("media.filter.anyBoard")) }
            .Concat(entries.Select(entry => (entry.Thread.Source, entry.Thread.Board)).Distinct()
                .Where(pair => source == null || pair.Source == source)
                .OrderBy(pair => pair.Board, StringComparer.Ordinal).ThenBy(pair => pair.Source, StringComparer.Ordinal)
                .Select(pair => new BoardChoice(pair.Source, pair.Board, BoardLabel(pair.Source, pair.Board, source == null))))
            .ToList();
        if (selected is { Source: not null, Board: not null }
            && (source == null || source == selected.Source)
            && !options.Any(option => option.Board == selected.Board && option.Source == selected.Source))
        {
            string label = BoardLabel(selected.Source, selected.Board, source == null);
            options.Add(new BoardChoice(selected.Source, selected.Board, UnavailableOption(label)));
        }
        int excluded = _terms.Count(term => term.Field == MediaFilterField.Board && term.Exclude);
        BoardChoice? summary = selected == null && excluded > 0 ? new BoardChoice(null, null, Texts.T("media.filter.combo.excluded", excluded), Summary: true) : null;
        if (summary != null) options.Add(summary);
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            _boardFilter.ItemsSource = options;
            // The set never holds a board of another source than the included one (MediaFilterTermSet drops it).
            _boardFilter.SelectedItem = summary ?? options.FirstOrDefault(option => selected?.Board != null
                && option.Board == selected.Board && option.Source == selected.Source) ?? options[0];
        }
        finally { _localizing = localizing; }
    }

    private void LocalizeFilters()
    {
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            RenderKindOptions();
            SetTexts(_minRating, new[] { Texts.T("media.filter.anyRating") }.Concat(Enumerable.Range(1, 5).Select(value => Texts.T("media.filter.minRating", value))).ToArray());
            SetKeys(_soundFilter, "media.filter.anySound", "media.filter.sound.with", "media.filter.sound.without");
        }
        finally { _localizing = localizing; }
        _nameFilter.PlaceholderText = Texts.T("media.filter.name");
        _queryAliases = null; // the aliases follow the language
        _searchScope.FieldHelpNote = Texts.T("media.query.tip");
        ClearQueryHint();
        _searchCarryOffer.Localize();
        UpdateSearchTip();
        _favoritesOnly.Content = Texts.T("media.filter.favorites");
        _withoutMediaTags.Content = Texts.T("media.filter.withoutTags");
        ToolTip.SetTip(_withoutMediaTags, Texts.T("media.filter.withoutTagsHint"));
        ToolTip.SetTip(_soundFilter, Texts.T("media.filter.sound.hint"));
        _tagAdd.IncludeHint = Texts.T("media.filter.includeHint");
        _tagAdd.ExcludeHint = Texts.T("media.filter.excludeHint");
        _tagAdd.ApplyTexts();
        _filterAdd.ApplyTexts();
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (_nameFilter, "media.filter.name"), (_kindFilter, "media.filter.kind"), (_soundFilter, "media.filter.sound"), (_sourceFilter, "media.filter.source"),
            (_boardFilter, "media.filter.board"), (_minRating, "media.filter.rating"),
            (_tagMode, "tags.filter.matchLabel"),
            (_filterChips, "media.filter.active")
        }) AutomationProperties.SetName(control, Texts.T(key));
        LocalizeFilterGrid();
        SyncFilterControls();
        RenderTagOptions();
        RenderFilterChips();
    }

    /// <summary>Primary+F: puts the caret in the file name search field at the head of the tools (AP2: the same
    /// place as the archive field; the filter surface stays as it is).</summary>
    internal void FocusSearch()
    {
        if (_lifetime.IsCancellationRequested) return;
        SetFocusMode(false); // the search field is one of the rows the focus mode hides (M4): asking for it shows them again
        void Focus()
        {
            if (_lifetime.IsCancellationRequested || !_nameFilter.IsEffectivelyVisible) return;
            _nameFilter.Focus(NavigationMethod.Tab);
            _nameFilter.SelectAll();
        }
        if (_nameFilter.IsEffectivelyVisible) Focus();
        else Dispatcher.UIThread.Post(Focus, DispatcherPriority.Loaded);
    }

    internal SearchScopeChip SearchScopeChip => _searchScope;
    internal SearchBox NameFilter => _nameFilter;
    internal SearchCarryOffer SearchCarryOffer => _searchCarryOffer;

    /// <summary>Host: show Archive and hand it this query for its File name scope.</summary>
    internal Action<string>? SearchInArchiveRequested { get; set; }

    private IEnumerable<SearchCarryAction> SearchCarryActions()
    {
        if (SearchInArchiveRequested == null) yield break;
        string draft = _nameFilter.Text ?? string.Empty;
        string? reason = draft.Length == 0 ? Texts.T("search.carry.empty") : null;
        yield return new SearchCarryAction("MediaSearchInArchive",
            draft.Length == 0 ? Texts.T("search.carry.toArchivePlain") : Texts.T("search.carry.toArchive", draft), reason == null,
            () => { if (!_lifetime.IsCancellationRequested) SearchInArchiveRequested?.Invoke(draft); }, reason);
    }

    /// <summary>A query carried from Archive or the reader: an empty name field takes it at once, a different one gets an offer.</summary>
    internal bool ReceiveCarriedSearch(string query, string source)
    {
        if (_lifetime.IsCancellationRequested) return false;
        return _searchCarryOffer.Carry(source, query, _nameFilter.Text ?? string.Empty, () =>
        {
            if (_lifetime.IsCancellationRequested) return;
            _nameFilter.Text = query;
            _nameFilter.FlushQuery();
            FocusSearch();
        });
    }

    // The shortcut in the tooltip follows the user's current binding.
    private void UpdateSearchTip() => ToolTip.SetTip(_nameFilter, CommandBindings.Tooltip("input.focus-media-search") + "\n" + Texts.T("media.query.tip"));

    // The count belongs to the text that produced it: none for an empty name or a refused filter.
    private void UpdateSearchResult()
    {
        _nameFilter.Debounce = (_snapshot?.Entries.Count ?? 0) > SearchImmediateEntries ? NameFilterDebounce : TimeSpan.Zero;
        _nameFilter.ResultText = string.IsNullOrEmpty(_nameFilter.Text) || _filterFailed || _snapshot == null ? null : Texts.Count("search.count", _shown.Count);
    }

    private static void SetKeys(ComboBox combo, params string[] keys) => SetTexts(combo, keys.Select(key => Texts.T(key)).ToArray());

    private static void SetTexts(ComboBox combo, string[] items)
    {
        int index = Math.Max(0, combo.SelectedIndex);
        combo.ItemsSource = items;
        combo.SelectedIndex = Math.Min(index, items.Length - 1);
    }
}
