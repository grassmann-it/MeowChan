using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Filters of the media page. They apply live: the index is in memory, so every change filters again.
// A filter part that cannot be evaluated (deleted tag, store unavailable) empties the grid with an
// explanation; it is never dropped, because that would silently show more than asked for.
public sealed partial class MediaPage
{
    private readonly TextBox _nameFilter = new() { Name = "MediaNameFilter", MinWidth = 180, MaxLength = MediaLibraryFilter.MaximumNameLength };
    private readonly ComboBox _kindFilter = new() { Name = "MediaKindFilter", MinWidth = 130 };
    private readonly ComboBox _sourceFilter = new() { Name = "MediaSourceFilter", MinWidth = 130 };
    private readonly ComboBox _boardFilter = new() { Name = "MediaBoardFilter", MinWidth = 130 };
    private readonly CheckBox _favoritesOnly = new() { Name = "MediaFavoritesOnly" };
    private readonly CheckBox _withoutOwnTags = new() { Name = "MediaWithoutOwnTags" };
    private readonly ComboBox _minRating = new() { Name = "MediaMinRating", MinWidth = 130 };
    private readonly ComboBox _tagChoice = new() { Name = "MediaTagChoice", MinWidth = 160 };
    private readonly Button _tagInclude = new() { Name = "MediaTagInclude" };
    private readonly Button _tagExclude = new() { Name = "MediaTagExclude" };
    private readonly ComboBox _tagMode = new() { Name = "MediaTagMode", MinWidth = 160 };
    private readonly Button _filterReset = new() { Name = "MediaFilterReset" };
    private readonly WrapPanel _filterChips = new() { Name = "MediaFilterChips", ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _filterError = new() { Name = "MediaFilterError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly List<Guid> _includeTags = new();
    private readonly List<Guid> _excludeTags = new();
    private (ResourceId Thread, string Title)? _threadFilter;
    private MediaLibraryUserData _userData = MediaLibraryUserData.Unavailable;
    // T1: which occurrences lack own tags, decided once per index and tag state; the status line counts the tiles among the matches.
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
    private sealed record TagOption(Guid? Id, string Label) { public override string ToString() => Label; }

    /// <summary>Reads the profile's own tags, favorites and ratings; null parts are unavailable.</summary>
    internal Func<MediaLibraryUserData>? ReadUserData { get; set; }
    internal string? FilterError => _filterError.IsVisible ? _filterError.Text : null;
    internal WrapPanel FilterChips => _filterChips;

    private Control BuildFilterRow()
    {
        WrapPanel row = new() { ItemSpacing = 8, LineSpacing = 6 };
        foreach (Control control in new Control[] { _nameFilter, _kindFilter, _soundFilter, _sourceFilter, _boardFilter, _favoritesOnly, _withoutOwnTags, _minRating,
            _tagChoice, _tagInclude, _tagExclude, _tagMode, _filterReset, _audioNote })
        { control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(control); }
        AutomationProperties.SetLiveSetting(_filterError, AutomationLiveSetting.Assertive);
        _filterReset.IsEnabled = false;
        _tagInclude.IsEnabled = _tagExclude.IsEnabled = false;
        _nameFilter.TextChanged += (_, _) => FilterEdited();
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
        _withoutOwnTags.IsCheckedChanged += (_, _) => FilterEdited();
        _tagChoice.SelectionChanged += (_, _) => UpdateTagButtons();
        _tagInclude.Click += (_, _) => AddTag(include: true);
        _tagExclude.Click += (_, _) => AddTag(include: false);
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
            _withoutOwnTags.IsChecked == true);
    }

    /// <summary>Entries that pass the filter; empty with an explanation when the filter cannot be applied.</summary>
    private IReadOnlyList<MediaLibraryEntry> ApplyFilter(IReadOnlyList<MediaLibraryEntry> entries)
    {
        if (!ReferenceEquals(_optionsFor, _snapshot)) RenderSourceOptions();
        string? error = null;
        IReadOnlyList<MediaLibraryEntry> passing;
        _audioUnknown = 0;
        try
        {
            MediaLibraryFilter filter = CurrentFilter();
            _filterActive = !filter.IsEmpty;
            MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate(entries, filter, _userData, _audio);
            passing = result.Passing;
            _audioUnknown = result.UnknownSoundVideos;
            _untaggedTiles = filter.WithoutOwnTags ? null : UntaggedOf(entries)?.CountTiles(passing);
        }
        catch (MediaLibraryFilterException exception)
        {
            error = Texts.T(exception.Error switch
            {
                MediaLibraryFilterError.UnknownTag => "media.filter.error.unknownTag",
                MediaLibraryFilterError.TagsUnavailable => "media.filter.error.tags",
                MediaLibraryFilterError.FavoritesUnavailable => "media.filter.error.favorites",
                _ => "media.filter.error.ratings"
            });
            passing = Array.Empty<MediaLibraryEntry>();
        }
        catch (ArgumentException)
        {
            error = Texts.T("media.filter.error.invalid");
            _filterActive = true;
            passing = Array.Empty<MediaLibraryEntry>();
        }
        _filterFailed = error != null;
        if (_filterFailed) _untaggedTiles = null;
        _filterError.Text = error;
        _filterError.IsVisible = error != null;
        _filterReset.IsEnabled = _filterActive;
        UpdateAudioNote();
        return passing;
    }

    private MediaLibraryUntagged? UntaggedOf(IReadOnlyList<MediaLibraryEntry> entries)
    {
        if (_untagged?.IsFor(entries, _userData) != true) _untagged = MediaLibraryUntagged.Of(entries, _userData);
        return _untagged;
    }

    /// <summary>The "N without tags" link of the status line: shown while it would narrow the matches to untagged media.</summary>
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
        _untaggedLink.IsVisible = _snapshot != null && !_filterFailed && count > 0;
        if (!_untaggedLink.IsVisible) return;
        _untaggedText.Text = Texts.T("media.untagged.count", count);
        string hint = Texts.T("media.untagged.hint", count);
        ToolTip.SetTip(_untaggedLink, hint);
        AutomationProperties.SetName(_untaggedLink, hint);
    }

    /// <summary>Applies "without own tags" on top of the current filters: the starting point for tagging.</summary>
    internal void ShowUntagged()
    {
        bool focused = _untaggedLink.IsFocused;
        _withoutOwnTags.IsChecked = true;
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

    /// <summary>Thread as a meta tag: only the media of this thread.</summary>
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
            _nameFilter.Text = string.Empty;
            _kindFilter.SelectedIndex = 0;
            _soundFilter.SelectedIndex = 0;
            _sourceFilter.SelectedIndex = 0;
            _boardFilter.SelectedIndex = 0;
            RenderSourceOptions();
            _favoritesOnly.IsChecked = false;
            _withoutOwnTags.IsChecked = false;
            _minRating.SelectedIndex = 0;
            _tagMode.SelectedIndex = 0;
            _includeTags.Clear(); _excludeTags.Clear();
            _threadFilter = null;
        }
        finally { _localizing = false; }
        RenderTagOptions();
        FilterEdited();
    }

    private void AddTag(bool include)
    {
        if (_tagChoice.SelectedItem is TagOption { Id: Guid id }) IncludeOrExclude(id, include);
    }

    private bool IncludeOrExclude(Guid id, bool include)
    {
        try
        {
            _ = new ArchiveTagFilter(include ? _includeTags.Append(id) : _includeTags, include ? _excludeTags : _excludeTags.Append(id));
        }
        catch (ArgumentException)
        {
            _filterError.Text = Texts.T("media.filter.error.tooManyTags");
            _filterError.IsVisible = true;
            return false;
        }
        (include ? _includeTags : _excludeTags).Add(id);
        RenderTagOptions();
        FilterEdited();
        return true;
    }

    private string TagLabel(Guid id) => _userData.TagSnapshot?.Find(new TagId(id))?.Name.Display ?? Texts.T("tags.filter.deleted");

    private void RenderFilterChips()
    {
        List<(string Label, Action Remove)> chips = new();
        chips.AddRange(_includeTags.ToArray().Select(id => (Texts.T("tags.filter.includeChip", TagLabel(id)),
            (Action)(() => { _includeTags.Remove(id); RenderTagOptions(); FilterEdited(); }))));
        chips.AddRange(_excludeTags.ToArray().Select(id => (Texts.T("tags.filter.excludeChip", TagLabel(id)),
            (Action)(() => { _excludeTags.Remove(id); RenderTagOptions(); FilterEdited(); }))));
        if (_includeTags.Count > 1 && _tagMode.SelectedIndex == 1)
            chips.Add((Texts.T("tags.filter.anyChip"), () => { _tagMode.SelectedIndex = 0; }));
        if (_threadFilter is { } thread)
            chips.Add((Texts.T("media.filter.threadChip", thread.Title), () => { _threadFilter = null; FilterEdited(); }));
        // Reached from the status line while the filter fields are closed, so it is removable without opening them.
        if (_withoutOwnTags.IsChecked == true)
            chips.Add((Texts.T("media.filter.withoutTags"), () => { _withoutOwnTags.IsChecked = false; }));
        _filterChips.Children.Clear();
        foreach ((string label, Action remove) in chips) _filterChips.Children.Add(ArchivePage.CreateFilterTag(label, remove));
        _filterChips.IsVisible = chips.Count > 0;
    }

    private void RenderTagOptions()
    {
        PrivateTagStoreSnapshot? tags = _userData.TagSnapshot;
        HashSet<Guid> used = _includeTags.Concat(_excludeTags).ToHashSet();
        // A tag already in the filter is not offered again, so include and exclude cannot overlap.
        TagOption[] options = new[] { new TagOption(null, Texts.T(tags == null ? "tags.filter.unavailable"
                : tags.Tags.Count == 0 ? "tags.filter.none" : "tags.filter.choose")) }
            .Concat((tags?.Tags ?? Array.Empty<PrivateTagDefinition>()).Where(tag => !used.Contains(tag.Id.Value))
                .Select(tag => new TagOption(tag.Id.Value, tag.Name.Display))).ToArray();
        Guid? selected = (_tagChoice.SelectedItem as TagOption)?.Id;
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            _tagChoice.ItemsSource = options;
            _tagChoice.SelectedItem = options.FirstOrDefault(option => selected != null && option.Id == selected) ?? options[0];
        }
        finally { _localizing = localizing; }
        UpdateTagButtons();
    }

    private void UpdateTagButtons() =>
        _tagInclude.IsEnabled = _tagExclude.IsEnabled = _tagChoice.SelectedItem is TagOption { Id: not null };

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
    private static string SourceLabel(string source) => source == BooruMediaIdentity.Source ? Texts.T("booru.media.source") : source;

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
        _nameFilter.Watermark = Texts.T("media.filter.name");
        _favoritesOnly.Content = Texts.T("media.filter.favorites");
        _withoutOwnTags.Content = Texts.T("media.filter.withoutTags");
        ToolTip.SetTip(_withoutOwnTags, Texts.T("media.filter.withoutTagsHint"));
        _tagInclude.Content = Texts.T("tags.filter.include");
        _tagExclude.Content = Texts.T("tags.filter.exclude");
        _filterReset.Content = Texts.T("media.filter.reset");
        ToolTip.SetTip(_soundFilter, Texts.T("media.filter.sound.hint"));
        ToolTip.SetTip(_tagInclude, Texts.T("media.filter.includeHint"));
        ToolTip.SetTip(_tagExclude, Texts.T("media.filter.excludeHint"));
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (_nameFilter, "media.filter.name"), (_kindFilter, "media.filter.kind"), (_soundFilter, "media.filter.sound"), (_sourceFilter, "media.filter.source"),
            (_boardFilter, "media.filter.board"), (_minRating, "media.filter.rating"), (_tagChoice, "tags.filter.label"),
            (_tagMode, "tags.filter.matchLabel"), (_tagInclude, "media.filter.includeHint"), (_tagExclude, "media.filter.excludeHint"),
            (_filterChips, "media.filter.active")
        }) AutomationProperties.SetName(control, Texts.T(key));
        RenderSourceOptions();
        RenderTagOptions();
        RenderFilterChips();
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
