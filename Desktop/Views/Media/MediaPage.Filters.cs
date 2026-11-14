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
    // Over a large index every keystroke would filter, sort and group again on the UI thread: the name waits 200 ms
    // after the last edit. Up to SearchImmediateEntries entries filtering is cheap and follows every keystroke.
    private static readonly TimeSpan NameFilterDebounce = TimeSpan.FromMilliseconds(200);
    private int _searchImmediateEntries = 2000;
    internal int SearchImmediateEntries { get => _searchImmediateEntries; set { _searchImmediateEntries = value; UpdateSearchResult(); } }
    private readonly SearchBox _nameFilter = new() { Name = "MediaNameFilter", MinWidth = 180, MaxLength = MediaLibraryFilter.MaximumNameLength };
    private readonly SearchScopeChip _searchScope = new() { Name = "MediaSearchScope", Scopes = new[] { SearchScope.FileName } };
    private readonly SearchCarryOffer _searchCarryOffer = new() { Name = "MediaSearchCarryOffer" };
    private readonly ComboBox _kindFilter = new() { Name = "MediaKindFilter", MinWidth = 130 };
    private readonly ComboBox _sourceFilter = new() { Name = "MediaSourceFilter", MinWidth = 130 };
    private readonly ComboBox _boardFilter = new() { Name = "MediaBoardFilter", MinWidth = 130 };
    private readonly CheckBox _favoritesOnly = new() { Name = "MediaFavoritesOnly" };
    private readonly CheckBox _withoutMediaTags = new() { Name = "MediaWithoutMediaTags" };
    private readonly ComboBox _minRating = new() { Name = "MediaMinRating", MinWidth = 130 };
    // The tag part of the surface: one "Add tag…" popover with search; the tags in the filter are the chips of the header row.
    private readonly TagFilterPicker _tagAdd = new() { Name = "MediaTagAdd" };
    private readonly ComboBox _tagMode = new() { Name = "MediaTagMode", MinWidth = 160 };
    private readonly Button _filterReset = new() { Name = "MediaFilterReset" };
    private readonly WrapPanel _filterChips = new() { Name = "MediaFilterChips", ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _filterError = new() { Name = "MediaFilterError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly List<Guid> _includeTags = new();
    private readonly List<Guid> _excludeTags = new();
    private (ResourceId Thread, string Title)? _threadFilter;
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

    private sealed record Choice(string? Value, string Label) { public override string ToString() => Label; }
    private sealed record BoardChoice(string? Source, string? Board, string Label) { public override string ToString() => Label; }

    /// <summary>Reads the profile's own tags, favorites and ratings; null parts are unavailable.</summary>
    internal Func<MediaLibraryUserData>? ReadUserData { get; set; }
    internal string? FilterError => _filterError.IsVisible ? _filterError.Text : null;
    internal WrapPanel FilterChips => _filterChips;

    private Control BuildFilterRow()
    {
        WrapPanel row = new() { ItemSpacing = 8, LineSpacing = 6 };
        foreach (Control control in new Control[] { _kindFilter, _soundFilter, _sourceFilter, _boardFilter, _favoritesOnly, _withoutMediaTags, _missing, _minRating,
            _tagAdd, _tagMode, _filterReset, _moreFilters, _audioNote })
        { control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(control); }
        AutomationProperties.SetLiveSetting(_filterError, AutomationLiveSetting.Assertive);
        _filterReset.IsEnabled = false;
        _tagAdd.Snapshot = () => _userData.TagSnapshot;
        _tagAdd.Used = () => _includeTags.Concat(_excludeTags).Select(id => new TagId(id)).ToHashSet();
        _tagAdd.Picked = (id, exclude) => TryIncludeOrExclude(id, !exclude);
        _nameFilter.QueryChanged += (_, _) => { if (!_lifetime.IsCancellationRequested) FilterEdited(); };
        _searchScope.CarryActions = SearchCarryActions;
        foreach (ComboBox combo in new[] { _minRating, _tagMode })
            combo.SelectionChanged += (_, _) => FilterEdited();
        // A sound choice implies videos: it sets the type to Videos, and choosing another type drops the sound choice.
        _kindFilter.SelectionChanged += (_, _) =>
        {
            if (!_localizing && SoundFilterActive && _kindFilter.SelectedIndex != (int)MediaLibraryKindFilter.Video) SetSelection(_soundFilter, 0);
            FilterEdited();
        };
        _soundFilter.SelectionChanged += (_, _) =>
        {
            if (!_localizing && SoundFilterActive) SetSelection(_kindFilter, (int)MediaLibraryKindFilter.Video);
            FilterEdited();
        };
        _sourceFilter.SelectionChanged += (_, _) => { if (_localizing) return; RenderSourceOptions(); FilterEdited(); };
        _boardFilter.SelectionChanged += (_, _) => { if (_localizing) return; RenderBoardOptions(); FilterEdited(); };
        _favoritesOnly.IsCheckedChanged += (_, _) => FilterEdited();
        _withoutMediaTags.IsCheckedChanged += (_, _) => FilterEdited();
        _filterReset.Click += (_, _) => ResetFilters();
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
        // This pass reads the name as typed; a debounced run for the same text would only repeat it.
        _nameFilter.CancelPendingQuery();
        RenderFilterChips();
        Present();
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

    /// <summary>The filter the controls describe. Throws ArgumentException for an invalid combination.</summary>
    internal MediaLibraryFilter CurrentFilter()
    {
        BoardChoice? board = _boardFilter.SelectedItem as BoardChoice;
        string? source = (_sourceFilter.SelectedItem as Choice)?.Value ?? board?.Source;
        return new MediaLibraryFilter(
            ArchiveTagFilter.OrNull(_includeTags, _excludeTags, _tagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All),
            (MediaLibraryKindFilter)Math.Clamp(_kindFilter.SelectedIndex, 0, (int)MediaLibraryKindFilter.Other),
            _favoritesOnly.IsChecked == true, _minRating.SelectedIndex > 0 ? _minRating.SelectedIndex : null,
            source, board?.Board, _nameFilter.Text, _threadFilter?.Thread,
            (MediaLibrarySoundFilter)Math.Clamp(_soundFilter.SelectedIndex, 0, (int)MediaLibrarySoundFilter.WithoutSound),
            _withoutMediaTags.IsChecked == true);
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
        if (_includeTags.Contains(id) || _excludeTags.Contains(id) || _userData.TagSnapshot?.Find(new TagId(id)) == null) return;
        bool focused = FocusWithin(_grid);
        if (!IncludeOrExclude(id, include: true)) return;
        if (focused) FocusGrid();
    }

    /// <summary>Shift+click on a tag chip of a tile: hide the media with this tag (S7). Excluding takes the shown tile out of the grid.</summary>
    internal void ExcludeTag(Guid id)
    {
        if (_includeTags.Contains(id) || _excludeTags.Contains(id) || _userData.TagSnapshot?.Find(new TagId(id)) == null) return;
        bool focused = FocusWithin(_grid);
        if (!IncludeOrExclude(id, include: false)) return;
        if (focused) FocusGrid();
    }

    /// <summary>Thread as a meta tag: only the media of this thread.</summary>
    /// <summary>Shows only one import batch (or any borrowed thread identity) by its identity and title.</summary>
    internal void FilterToThread(ResourceId thread, string title)
    {
        ArgumentNullException.ThrowIfNull(thread);
        _threadFilter = (thread, string.IsNullOrWhiteSpace(title) ? "#" + thread.Thread : title);
        FilterEdited();
    }

    internal void FilterToThread(MediaLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _threadFilter = (entry.Thread, string.IsNullOrWhiteSpace(entry.ThreadTitle) ? "#" + entry.Thread.Thread : entry.ThreadTitle);
        FilterEdited();
    }

    internal void ResetFilters()
    {
        _localizing = true;
        try
        {
            _nameFilter.SetTextSilently(string.Empty);
            _kindFilter.SelectedIndex = 0;
            _soundFilter.SelectedIndex = 0;
            _sourceFilter.SelectedIndex = 0;
            _boardFilter.SelectedIndex = 0;
            RenderSourceOptions();
            _favoritesOnly.IsChecked = false;
            _withoutMediaTags.IsChecked = false;
            _missing.IsChecked = false;
            _minRating.SelectedIndex = 0;
            _tagMode.SelectedIndex = 0;
            _includeTags.Clear(); _excludeTags.Clear();
            _threadFilter = null;
        }
        finally { _localizing = false; }
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
    internal string[] AddableTagNames => TagFilterPicker.Choices(_userData.TagSnapshot, _includeTags.Concat(_excludeTags).Select(id => new TagId(id)).ToHashSet(), null)
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
        if (_includeTags.Contains(id) || _excludeTags.Contains(id)) return null;
        try
        {
            _ = new ArchiveTagFilter(include ? _includeTags.Append(id) : _includeTags, include ? _excludeTags : _excludeTags.Append(id));
        }
        catch (ArgumentException) { return Texts.T("media.filter.error.tooManyTags"); }
        (include ? _includeTags : _excludeTags).Add(id);
        RenderTagOptions();
        FilterEdited();
        return null;
    }

    /// <summary>
    /// The explicit include/exclude toggle of a tag chip: moves the tag to the other list. Clicking the chip body to do the same is
    /// an open owner decision (follow-up); the toggle button on the chip and the popover's "Add as" are the explicit routes.
    /// </summary>
    internal string? ToggleTagFilter(Guid id)
    {
        bool wasIncluded = _includeTags.Contains(id);
        if (!wasIncluded && !_excludeTags.Contains(id)) return null;
        try
        {
            _ = new ArchiveTagFilter(wasIncluded ? _includeTags.Where(tag => tag != id) : _includeTags.Append(id),
                wasIncluded ? _excludeTags.Append(id) : _excludeTags.Where(tag => tag != id));
        }
        catch (ArgumentException)
        {
            string refusal = Texts.T("media.filter.error.tooManyTags");
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
            return refusal;
        }
        (wasIncluded ? _includeTags : _excludeTags).Remove(id);
        (wasIncluded ? _excludeTags : _includeTags).Add(id);
        RenderTagOptions();
        FilterEdited();
        return null;
    }

    private string TagLabel(Guid id) => _userData.TagSnapshot?.Find(new TagId(id))?.Name.Display ?? Texts.T("tags.filter.deleted");

    // The popover reads the tags and the filter when it opens; the button only says whether a tag is left to add.
    private void RenderTagOptions() => _tagAdd.Refresh();

    // Keep a selected identity even if a new index no longer contains it: dropping it would widen the results.
    private static string UnavailableOption(string label) => label + " (" + Texts.T("mediahash.unavailable") + ")";
    private void RenderSourceOptions()
    {
        _optionsFor = _snapshot;
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        string? selected = (_sourceFilter.SelectedItem as Choice)?.Value;
        List<Choice> options = new[] { new Choice(null, Texts.T("media.filter.anySource")) }
            .Concat(entries.Select(entry => entry.Thread.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(source => new Choice(source, SourceLabel(source)))).ToList();
        if (selected != null && !options.Any(option => option.Value == selected))
            options.Add(new Choice(selected, UnavailableOption(SourceLabel(selected))));
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            _sourceFilter.ItemsSource = options;
            _sourceFilter.SelectedItem = options.FirstOrDefault(option => option.Value == selected) ?? options[0];
        }
        finally { _localizing = localizing; }
        RenderBoardOptions();
    }

    // The provider source is shown by its name; chan sources keep their identifier.
    private static string SourceLabel(string source) => source == BooruMediaIdentity.Source ? Texts.T("booru.media.source")
        : source == Import.ManualImportIdentity.Source ? Texts.T("import.media.source") : source;

    private void RenderBoardOptions()
    {
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        string? source = (_sourceFilter.SelectedItem as Choice)?.Value;
        BoardChoice? selected = _boardFilter.SelectedItem as BoardChoice;
        List<BoardChoice> options = new[] { new BoardChoice(null, null, Texts.T("media.filter.anyBoard")) }
            .Concat(entries.Select(entry => (entry.Thread.Source, entry.Thread.Board)).Distinct()
                .Where(pair => source == null || pair.Source == source)
                .OrderBy(pair => pair.Board, StringComparer.Ordinal).ThenBy(pair => pair.Source, StringComparer.Ordinal)
                .Select(pair => new BoardChoice(pair.Source, pair.Board,
                    source == null ? Texts.T("media.filter.boardOfSource", pair.Board, pair.Source) : "/" + pair.Board + "/")))
            .ToList();
        if (selected is { Source: not null, Board: not null }
            && (source == null || source == selected.Source)
            && !options.Any(option => option.Board == selected.Board && option.Source == selected.Source))
        {
            string label = source == null ? Texts.T("media.filter.boardOfSource", selected.Board, selected.Source) : "/" + selected.Board + "/";
            options.Add(new BoardChoice(selected.Source, selected.Board, UnavailableOption(label)));
        }
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            _boardFilter.ItemsSource = options;
            // A board of another source no longer matches and is cleared.
            _boardFilter.SelectedItem = options.FirstOrDefault(option => selected?.Board != null
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
            SetKeys(_kindFilter, "media.filter.anyKind", "media.filter.kind.image", "media.filter.kind.gif", "media.filter.kind.video", "media.filter.kind.other");
            SetTexts(_minRating, new[] { Texts.T("media.filter.anyRating") }.Concat(Enumerable.Range(1, 5).Select(value => Texts.T("media.filter.minRating", value))).ToArray());
            SetKeys(_soundFilter, "media.filter.anySound", "media.filter.sound.with", "media.filter.sound.without");
            SetKeys(_tagMode, "tags.filter.matchAll", "tags.filter.matchAny");
        }
        finally { _localizing = localizing; }
        _nameFilter.PlaceholderText = Texts.T("media.filter.name");
        _searchCarryOffer.Localize();
        UpdateSearchTip();
        _favoritesOnly.Content = Texts.T("media.filter.favorites");
        _withoutMediaTags.Content = Texts.T("media.filter.withoutTags");
        ToolTip.SetTip(_withoutMediaTags, Texts.T("media.filter.withoutTagsHint"));
        _filterReset.Content = Texts.T("media.filter.reset");
        ToolTip.SetTip(_soundFilter, Texts.T("media.filter.sound.hint"));
        _tagAdd.IncludeHint = Texts.T("media.filter.includeHint");
        _tagAdd.ExcludeHint = Texts.T("media.filter.excludeHint");
        _tagAdd.ApplyTexts();
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (_nameFilter, "media.filter.name"), (_kindFilter, "media.filter.kind"), (_soundFilter, "media.filter.sound"), (_sourceFilter, "media.filter.source"),
            (_boardFilter, "media.filter.board"), (_minRating, "media.filter.rating"),
            (_tagMode, "tags.filter.matchLabel"),
            (_filterChips, "media.filter.active")
        }) AutomationProperties.SetName(control, Texts.T(key));
        RenderSourceOptions();
        RenderTagOptions();
        RenderFilterChips();
    }

    /// <summary>Primary+F: puts the caret in the file name search field at the head of the tools (AP2: the same
    /// place as the archive field; the filter surface stays as it is).</summary>
    internal void FocusSearch()
    {
        if (_lifetime.IsCancellationRequested) return;
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
    private void UpdateSearchTip() => ToolTip.SetTip(_nameFilter, CommandBindings.Tooltip("input.focus-media-search"));

    // The count belongs to the text that produced it: none for an empty name or a refused filter.
    private void UpdateSearchResult()
    {
        _nameFilter.Debounce = (_snapshot?.Entries.Count ?? 0) > SearchImmediateEntries ? NameFilterDebounce : TimeSpan.Zero;
        _nameFilter.ResultText = string.IsNullOrEmpty(_nameFilter.Text) || _filterFailed || _snapshot == null ? null : Texts.Count("search.count", _shown.Count);
    }

    // Sets another filter combo without that combo's own change reaction.
    private void SetSelection(ComboBox combo, int index)
    {
        bool localizing = _localizing;
        _localizing = true;
        try { combo.SelectedIndex = index; }
        finally { _localizing = localizing; }
    }

    private static void SetKeys(ComboBox combo, params string[] keys) => SetTexts(combo, keys.Select(key => Texts.T(key)).ToArray());

    private static void SetTexts(ComboBox combo, string[] items)
    {
        int index = Math.Max(0, combo.SelectedIndex);
        combo.ItemsSource = items;
        combo.SelectedIndex = Math.Min(index, items.Length - 1);
    }
}
