using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Ratings;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Gallery;

/// <summary>Media type choice of the media page. GIF is an image whose file name ends in .gif.</summary>
internal enum MediaLibraryKindFilter { Any, Image, Gif, Video, Other }

/// <summary>Sound property of a video. A sound choice implies videos only; images and GIFs have no sound property.</summary>
internal enum MediaLibrarySoundFilter { Any, WithSound, WithoutSound }

/// <summary>
/// The fields the free text of the media search is looked for in (<see cref="MediaLibraryFilter.TextFields"/>): the file name, the thread title,
/// the board, the source, and the own tags of the file or its thread. <see cref="All"/> is every field (the default); the page narrows it
/// with the <c>in:</c> token. <see cref="None"/> is no field and never a valid scope.
/// </summary>
[Flags]
internal enum MediaTextFields { None = 0, Name = 1, Title = 2, Board = 4, Source = 8, Tag = 16, All = 31 }

/// <summary>A board together with its source: board names are only unique within a source (used to exclude one board).</summary>
internal readonly record struct MediaLibraryBoardKey(string Source, string Board);

/// <summary>
/// Filter of the media page. Immutable; an empty filter shows everything. Tags work like the archive
/// tag filter, with one difference: a media occurrence carries its own media tags plus the tags of
/// its thread, so a thread tag such as "rpt" finds every file in those threads.
/// <para>Type, source and board can be excluded as well as included. The type is a set (<see cref="Kinds"/>, empty = every type):
/// "without type video" is the set of the other types (<see cref="AllKindsExcept"/>). The source and the board are one included value
/// each (a board needs its source) plus sets of excluded sources and excluded boards; every part narrows, so an excluded source that is
/// also the included one shows nothing, like two filters that cannot both hold.</para>
/// </summary>
internal sealed record MediaLibraryFilter
{
    internal const int MaximumNameLength = 200;

    private static readonly MediaLibraryKindFilter[] ConcreteKinds =
        [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other];

    /// <summary>
    /// <paramref name="kind"/> (one type, as before) and <paramref name="kinds"/> (a set) both restrict the type: a medium must be one of
    /// them together. Naming Any or all four types is no restriction. The new parts come last, so existing calls keep their meaning.
    /// </summary>
    internal MediaLibraryFilter(ArchiveTagFilter? tags = null, MediaLibraryKindFilter kind = MediaLibraryKindFilter.Any,
        bool favoritesOnly = false, int? minimumRating = null, string? source = null, string? board = null,
        string? nameContains = null, ResourceId? thread = null, MediaLibrarySoundFilter sound = MediaLibrarySoundFilter.Any,
        bool withoutMediaTags = false, IEnumerable<MediaLibraryKindFilter>? kinds = null,
        IEnumerable<string>? excludeSources = null, IEnumerable<MediaLibraryBoardKey>? excludeBoards = null,
        MediaTextFields textFields = MediaTextFields.All, IEnumerable<ResourceId>? threads = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (minimumRating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(minimumRating), "Ratings are whole values from 1 to 5.");
        if (!Enum.IsDefined(sound)) throw new ArgumentOutOfRangeException(nameof(sound));
        if (textFields == MediaTextFields.None || (textFields & ~MediaTextFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(textFields));
        FrozenSet<MediaLibraryKindFilter> kindSet = CanonicalKinds(kind, kinds);
        // A sound filter concerns videos only: the type set must leave videos in (an empty set does).
        if (sound != MediaLibrarySoundFilter.Any && kindSet.Count > 0 && !kindSet.Contains(MediaLibraryKindFilter.Video))
            throw new ArgumentException("A sound filter concerns videos only.", nameof(sound));
        if (board != null && source == null) throw new ArgumentException("A board filter needs its source.", nameof(board));
        string? name = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
        if (name is { Length: > MaximumNameLength }) throw new ArgumentException("The file name text is too long.", nameof(nameContains));
        // A text without a word (an empty phrase "" or one made of quote marks only) is no text: the filter stays inactive, as for white space.
        string[] words = name == null ? [] : WordsOf(name);
        if (words.Length == 0) name = null;
        Tags = tags;
        Kinds = kindSet;
        FavoritesOnly = favoritesOnly;
        MinimumRating = minimumRating;
        Source = string.IsNullOrEmpty(source) ? null : source;
        Board = string.IsNullOrEmpty(board) ? null : board;
        NameContains = name;
        TextWords = words;
        // Where to look means nothing without a word: such a filter is the same as no text, whatever scope was named.
        TextFields = words.Length == 0 ? MediaTextFields.All : textFields;
        Thread = thread;
        // A set of threads is a lookup, however large; null is "no such part", an empty set is "no thread" (nothing passes).
        Threads = threads?.Where(item => item != null).ToFrozenSet();
        Sound = sound;
        WithoutMediaTags = withoutMediaTags;
        ExcludeSources = (excludeSources ?? []).Where(value => !string.IsNullOrEmpty(value)).ToFrozenSet(StringComparer.Ordinal);
        ExcludeBoards = (excludeBoards ?? []).Where(key => !string.IsNullOrEmpty(key.Source) && !string.IsNullOrEmpty(key.Board)).ToFrozenSet();
    }

    private static FrozenSet<MediaLibraryKindFilter> CanonicalKinds(MediaLibraryKindFilter kind, IEnumerable<MediaLibraryKindFilter>? kinds)
    {
        HashSet<MediaLibraryKindFilter>? restriction = kind == MediaLibraryKindFilter.Any ? null : [kind];
        if (kinds != null)
        {
            HashSet<MediaLibraryKindFilter> named = [];
            bool any = false;
            foreach (MediaLibraryKindFilter item in kinds)
            {
                if (!Enum.IsDefined(item)) throw new ArgumentOutOfRangeException(nameof(kinds));
                if (item == MediaLibraryKindFilter.Any) any = true; else named.Add(item);
            }
            // An empty set, or Any among the others, restricts nothing; otherwise the set narrows what the single type allowed.
            if (!any && named.Count > 0)
            {
                if (restriction == null) restriction = named; else restriction.IntersectWith(named);
            }
        }
        // Nothing left means the two restrictions exclude each other; that is no filter that can be told apart from "all", so it is refused.
        if (restriction is { Count: 0 }) throw new ArgumentException("The named types exclude each other.", nameof(kinds));
        return restriction == null || ConcreteKinds.All(restriction.Contains) ? Unrestricted : restriction.ToFrozenSet();
    }

    private static readonly FrozenSet<MediaLibraryKindFilter> Unrestricted = FrozenSet<MediaLibraryKindFilter>.Empty;

    /// <summary>The words of a free text (see <see cref="TextWords"/>); linear in its length.</summary>
    internal static string[] WordsOf(string text)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\') { word.Append(text[++i]); continue; }
                if (c == '"') { inQuote = false; continue; }
                word.Append(c);
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
                continue;
            }
            word.Append(c);
        }
        if (word.Length > 0) words.Add(word.ToString());
        return [.. words.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether a text of the search field has at least one word (<see cref="WordsOf"/>): white space and an empty phrase <c>""</c> have none.</summary>
    internal static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text) && WordsOf(text).Length > 0;

    internal static MediaLibraryFilter None { get; } = new();

    /// <summary>Every concrete type except the given ones: the set for "without type X". All four given is refused (nothing would be left).</summary>
    internal static IReadOnlySet<MediaLibraryKindFilter> AllKindsExcept(params MediaLibraryKindFilter[] excluded)
    {
        HashSet<MediaLibraryKindFilter> rest = [.. ConcreteKinds];
        rest.ExceptWith(excluded);
        if (rest.Count == 0) throw new ArgumentException("Excluding every type leaves nothing to show.", nameof(excluded));
        return rest;
    }

    internal ArchiveTagFilter? Tags { get; }
    /// <summary>The types shown. Empty = every type (never "none"); never holds Any and never all four types.</summary>
    internal IReadOnlySet<MediaLibraryKindFilter> Kinds { get; }
    internal bool FavoritesOnly { get; }
    internal int? MinimumRating { get; }
    internal string? Source { get; }
    internal string? Board { get; }
    /// <summary>Sources whose media is not shown (empty: none).</summary>
    internal IReadOnlySet<string> ExcludeSources { get; }
    /// <summary>Boards (with their source) whose media is not shown (empty: none).</summary>
    internal IReadOnlySet<MediaLibraryBoardKey> ExcludeBoards { get; }
    /// <summary>
    /// The free text of the search field (trimmed; null: none, also for a text without a word such as <c>""</c>). Despite the historical name it
    /// is not limited to the file name: every word of it (<see cref="TextWords"/>) must occur, ignoring case, in the file name or the thread
    /// title, or name the board or the source of the occurrence as a whole.
    /// </summary>
    internal string? NameContains { get; }
    /// <summary>
    /// The words of <see cref="NameContains"/>: split at white space outside double quotes; a quoted run is one word without its quotes
    /// (<c>\"</c> and <c>\\</c> escape inside them, as in the query syntax). Slashes and every other character are literal ("/kot/").
    /// </summary>
    internal IReadOnlyList<string> TextWords { get; }
    /// <summary>
    /// The fields <see cref="TextWords"/> are looked for in (default <see cref="MediaTextFields.All"/>; always All without words). Every word
    /// must hit in at least one of them. A scope that holds <see cref="MediaTextFields.Tag"/> needs the tag index (<see cref="MediaLibraryFilterEngine.Validate"/>).
    /// </summary>
    internal MediaTextFields TextFields { get; }
    /// <summary>The thread as a meta tag: only media of exactly this thread.</summary>
    internal ResourceId? Thread { get; }
    /// <summary>
    /// Only media of exactly these threads (a set lookup per entry, so a set of tens of thousands costs the same as one of three). Null: no
    /// such restriction, every thread. An empty set is a restriction too: it lets nothing pass, and is no "all" (<see cref="IsEmpty"/> is false).
    /// It narrows together with <see cref="Thread"/>, source and board.
    /// </summary>
    internal IReadOnlySet<ResourceId>? Threads { get; }
    /// <summary>With or without sound; videos whose sound is not known are shown for neither choice.</summary>
    internal MediaLibrarySoundFilter Sound { get; }
    /// <summary>Only media that could carry media tags but carry none yet (<see cref="MediaLibraryUserData.LacksMediaTags"/>); thread tags do not count.</summary>
    internal bool WithoutMediaTags { get; }

    internal bool IsEmpty => Tags == null && Kinds.Count == 0 && !FavoritesOnly && MinimumRating == null
        && Source == null && Board == null && ExcludeSources.Count == 0 && ExcludeBoards.Count == 0
        && NameContains == null && Thread == null && Threads == null && Sound == MediaLibrarySoundFilter.Any && !WithoutMediaTags;

    // The sets compare by content (the synthesized record equality would compare the set objects).
    public bool Equals(MediaLibraryFilter? other) => other != null && Equals(Tags, other.Tags) && Kinds.SetEquals(other.Kinds)
        && FavoritesOnly == other.FavoritesOnly && MinimumRating == other.MinimumRating && Source == other.Source && Board == other.Board
        && ExcludeSources.SetEquals(other.ExcludeSources) && ExcludeBoards.SetEquals(other.ExcludeBoards)
        && (Threads == null ? other.Threads == null : other.Threads != null && Threads.SetEquals(other.Threads))
        && NameContains == other.NameContains && TextFields == other.TextFields && Equals(Thread, other.Thread) && Sound == other.Sound && WithoutMediaTags == other.WithoutMediaTags;

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Tags); hash.Add(FavoritesOnly); hash.Add(MinimumRating); hash.Add(Source); hash.Add(Board);
        hash.Add(NameContains); hash.Add(TextFields); hash.Add(Thread); hash.Add(Sound); hash.Add(WithoutMediaTags);
        // Order-independent over the sets, so equal sets hash alike whatever their enumeration order.
        int kinds = 0, sources = 0, boards = 0;
        foreach (MediaLibraryKindFilter kind in Kinds) kinds ^= (int)kind * 397 + 17;
        foreach (string source in ExcludeSources) sources ^= StringComparer.Ordinal.GetHashCode(source);
        foreach (MediaLibraryBoardKey board in ExcludeBoards) boards ^= board.GetHashCode();
        hash.Add(kinds); hash.Add(sources); hash.Add(boards);
        if (Threads != null)
        {
            int threads = 0;
            foreach (ResourceId item in Threads) threads ^= item.GetHashCode();
            hash.Add(threads); hash.Add(Threads.Count);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Why a filter could not be applied. The page then shows nothing instead of a wider list.</summary>
internal enum MediaLibraryFilterError { TagsUnavailable, UnknownTag, FavoritesUnavailable, RatingsUnavailable }

internal sealed class MediaLibraryFilterException : Exception
{
    internal MediaLibraryFilterException(MediaLibraryFilterError error) : base(error switch
    {
        MediaLibraryFilterError.UnknownTag => "The filter refers to a tag that no longer exists.",
        MediaLibraryFilterError.TagsUnavailable => "The filter uses tags, but the tag index is unavailable.",
        MediaLibraryFilterError.FavoritesUnavailable => "The filter uses favorites, but favorites are unavailable.",
        _ => "The filter uses ratings, but ratings are unavailable."
    }) => Error = error;

    internal MediaLibraryFilterError Error { get; }
}

/// <summary>
/// The profile's own data the filter reads: tags, favorites, ratings. Null means unavailable, which is
/// different from empty: an unavailable store refuses a filter that needs it.
/// </summary>
internal sealed class MediaLibraryUserData
{
    private readonly Dictionary<ResourceId, HashSet<Guid>>? _threadTags;
    private readonly Dictionary<PrivateMediaTagTarget, HashSet<Guid>>? _mediaTags;
    private readonly Dictionary<LocalMediaTagTarget, HashSet<Guid>>? _localTags;
    private readonly HashSet<FavoriteAssetKey>? _favorites;
    private readonly Dictionary<FavoriteAssetKey, int>? _ratings;
    private readonly Dictionary<FavoriteAssetKey, string>? _labels;

    internal MediaLibraryUserData(PrivateTagStoreSnapshot? tags, IReadOnlyList<FavoriteEntry>? favorites,
        IReadOnlyList<MediaRatingEntry>? ratings, IReadOnlyList<MediaLabelEntry>? labels = null)
    {
        LabelSource = labels;
        if (labels != null) _labels = labels.ToDictionary(entry => entry.Key, entry => entry.Color);
        TagSnapshot = tags;
        FavoriteSource = favorites;
        RatingSource = ratings;
        if (tags != null)
        {
            _threadTags = new();
            foreach (PrivateThreadTagAssignment item in tags.ThreadAssignments)
                (_threadTags.TryGetValue(item.Thread, out var set) ? set : _threadTags[item.Thread] = new()).Add(item.TagId.Value);
            _mediaTags = new();
            foreach (PrivateMediaTagAssignment item in tags.MediaAssignments)
                (_mediaTags.TryGetValue(item.Target, out var set) ? set : _mediaTags[item.Target] = new()).Add(item.TagId.Value);
            _localTags = new();
            foreach (PrivateLocalMediaTagAssignment item in tags.LocalAssignments)
                (_localTags.TryGetValue(item.Target, out var set) ? set : _localTags[item.Target] = new()).Add(item.TagId.Value);
        }
        if (favorites != null) _favorites = favorites.Select(entry => entry.Key).ToHashSet();
        if (ratings != null)
        {
            _ratings = new();
            foreach (MediaRatingEntry entry in ratings) _ratings[entry.Key] = entry.Value;
        }
    }

    internal static MediaLibraryUserData Unavailable { get; } = new(null, null, null);

    internal bool FavoritesAvailable => _favorites != null;
    internal bool RatingsAvailable => _ratings != null;
    internal PrivateTagStoreSnapshot? TagSnapshot { get; }
    internal IReadOnlyList<FavoriteEntry>? FavoriteSource { get; }
    internal IReadOnlyList<MediaRatingEntry>? RatingSource { get; }
    internal IReadOnlyList<MediaLabelEntry>? LabelSource { get; }

    /// <summary>The colour label of the occurrence, or null (also when labels are unavailable: a label is only shown, never filtered by).</summary>
    internal string? LabelOf(MediaLibraryEntry entry) =>
        _labels != null && MediaLibraryFilterEngine.AssetKey(entry) is { } key && _labels.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// What <see cref="TagsOf"/>, <see cref="TagCountOf"/> and <see cref="LacksMediaTags"/> were built from (the tag store's snapshot; null: tags
    /// unavailable). Favorites and ratings are not part of it, so a star click does not look like a change of the tags.
    /// </summary>
    internal object? TagSourceKey => TagSnapshot;

    /// <summary>True when both views were built from the very same store states.</summary>
    internal bool SameSourcesAs(MediaLibraryUserData? other) => other != null && ReferenceEquals(TagSnapshot, other.TagSnapshot)
        && ReferenceEquals(FavoriteSource, other.FavoriteSource) && ReferenceEquals(RatingSource, other.RatingSource)
        && ReferenceEquals(LabelSource, other.LabelSource);

    /// <summary>
    /// Own tags of the occurrence (its bound version, or its registered local revision) plus the tags of its thread.
    /// Requires available tags. Equal bytes elsewhere never contribute (no hash fan-out).
    /// </summary>
    internal IReadOnlySet<Guid> TagsOf(MediaLibraryEntry entry)
    {
        (HashSet<Guid>? threadTags, HashSet<Guid>? ownTags, _) = Carried(entry);
        HashSet<Guid> result = new();
        if (threadTags != null) result.UnionWith(threadTags);
        if (ownTags != null) result.UnionWith(ownTags);
        return result;
    }

    /// <summary>
    /// Whether the occurrence carries any of the given tags (its own, or those of its thread): the same tags as <see cref="TagsOf"/>, asked
    /// without building the set. Requires available tags.
    /// </summary>
    internal bool CarriesAnyTag(MediaLibraryEntry entry, IReadOnlySet<Guid> wanted)
    {
        (HashSet<Guid>? threadTags, HashSet<Guid>? ownTags, _) = Carried(entry);
        foreach (Guid tag in wanted)
            if (threadTags?.Contains(tag) == true || ownTags?.Contains(tag) == true) return true;
        return false;
    }

    /// <summary>The number of tags <see cref="TagsOf"/> would hold, counted without building the set (the tag count sort asks for every entry).</summary>
    internal int TagCountOf(MediaLibraryEntry entry)
    {
        (HashSet<Guid>? threadTags, HashSet<Guid>? ownTags, _) = Carried(entry);
        if (ownTags == null) return threadTags?.Count ?? 0;
        if (threadTags == null) return ownTags.Count;
        int count = threadTags.Count;
        foreach (Guid tag in ownTags) if (!threadTags.Contains(tag)) count++;
        return count;
    }

    /// <summary>
    /// True when the occurrence could carry own media tags (it has a <see cref="MediaTagSubjects"/> subject) but carries no media tag:
    /// neither on its bound version nor on its registered local revision. Its thread's tags do not count here, so a file in a tagged thread
    /// is still waiting to be tagged, although the tag filter finds it by the thread tag (<see cref="TagsOf"/>). A file that cannot carry
    /// media tags (missing, invalid binding, downloaded provider media) is never "without media tags": there is nothing to tag. Provider
    /// tags of a Booru medium are catalog claims, never own tags. Requires available tags.
    /// </summary>
    internal bool LacksMediaTags(MediaLibraryEntry entry)
    {
        (_, HashSet<Guid>? ownTags, MediaTagSubject? subject) = Carried(entry);
        return subject != null && ownTags == null;
    }

    internal int? OwnTagCount(MediaTagSubject? subject) =>
        _mediaTags == null || subject == null ? null : OwnTagsOf(subject)?.Count ?? 0;

    // The one reading of an occurrence's own tags: its thread's tags and those of its media subject (null: none; sets are never empty).
    private (HashSet<Guid>? Thread, HashSet<Guid>? Own, MediaTagSubject? Subject) Carried(MediaLibraryEntry entry)
    {
        if (_threadTags == null || _mediaTags == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
        MediaTagSubject? subject = MediaTagSubjects.Of(entry);
        return (_threadTags.GetValueOrDefault(entry.Thread), OwnTagsOf(subject), subject);
    }

    // The own media tags of a subject (null: none; the set is never empty). Requires available tags.
    private HashSet<Guid>? OwnTagsOf(MediaTagSubject? subject) => subject switch
    {
        MediaTagSubject.Bound bound => _mediaTags!.GetValueOrDefault(bound.Target),
        MediaTagSubject.Local local when TagSnapshot!.ResolveLocal(local.Observation) is { IsRegistered: true, Target: { } target } =>
            _localTags!.GetValueOrDefault(target),
        _ => null
    };

    /// <summary>
    /// <see cref="LacksMediaTags(MediaLibraryEntry)"/> for a subject the caller resolved itself (the gallery's filmstrip flags: the same rule,
    /// the same index, no rebuild per medium). Null when that cannot be said: tags unavailable or no subject (nothing to tag).
    /// </summary>
    internal bool? LacksMediaTags(MediaTagSubject? subject)
    {
        if (_mediaTags == null || subject == null) return null;
        return OwnTagsOf(subject) == null;
    }

    internal bool IsFavorite(MediaLibraryEntry entry)
    {
        if (_favorites == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.FavoritesUnavailable);
        return MediaLibraryFilterEngine.AssetKey(entry) is { } key && _favorites.Contains(key);
    }

    internal int? RatingOf(MediaLibraryEntry entry)
    {
        if (_ratings == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.RatingsUnavailable);
        return MediaLibraryFilterEngine.AssetKey(entry) is { } key && _ratings.TryGetValue(key, out int value) ? value : null;
    }
}

/// <summary>
/// One occurrence of the text tally: it passes every part of the filter that is not the text, and every word of the text hits somewhere
/// (the scope "All"). <paramref name="Fields"/> are the fields in which <i>every</i> word hits on its own; empty when the words only match
/// spread over several fields.
/// </summary>
internal readonly record struct MediaTextTally(MediaLibraryEntry Entry, MediaTextFields Fields);

/// <summary>
/// Entries that passed a filter, and how many videos could not be judged for a sound filter because their
/// sound is not known (not probed yet, or the headers did not tell). Those videos are in neither answer.
/// <see cref="TextTally"/> (null without a text) lists the occurrences that pass everything but the text scope, whatever
/// <see cref="MediaLibraryFilter.TextFields"/> says: the numbers of the page's "where was it found" row, which do not depend on the chosen scope.
/// </summary>
internal sealed record MediaLibraryFilterResult(IReadOnlyList<MediaLibraryEntry> Passing, int UnknownSoundVideos, IReadOnlyList<MediaTextTally>? TextTally = null);

/// <summary>Pure filter step of the media page; runs before sorting. No IO.</summary>
internal static class MediaLibraryFilterEngine
{
    /// <summary>
    /// Entries that pass every part of the filter, in input order. Throws when a part cannot be
    /// evaluated (store unavailable, tag deleted): dropping that part would silently show more.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryEntry> Apply(IEnumerable<MediaLibraryEntry> entries, MediaLibraryFilter filter,
        MediaLibraryUserData data, MediaAudioMap? audio = null) => Evaluate(entries, filter, data, audio).Passing;

    /// <summary>
    /// Like <see cref="Apply"/>, and counts the videos (distinct bytes) that pass every other part of the filter but
    /// whose sound is unknown, so the page can say how many it could not judge instead of counting them as silent.
    /// A video that is not saved has no file to look at and is neither shown nor counted for a sound filter.
    /// With a text it also tallies, in the same pass, where the words were found (<see cref="MediaLibraryFilterResult.TextTally"/>).
    /// </summary>
    internal static MediaLibraryFilterResult Evaluate(IEnumerable<MediaLibraryEntry> entries, MediaLibraryFilter filter,
        MediaLibraryUserData data, MediaAudioMap? audio = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(data);
        if (filter.IsEmpty) return new(entries as IReadOnlyList<MediaLibraryEntry> ?? entries.ToArray(), 0);
        Validate(filter, data);
        ArchiveTagFilter.Resolved? tags = filter.Tags is { } wanted && data.TagSnapshot is { } snapshot ? wanted.Resolve(snapshot) : null;
        List<MediaLibraryEntry> passing = new();
        HashSet<string> unknown = new(StringComparer.Ordinal);
        TextMatch? text = filter.TextWords.Count > 0 ? new TextMatch(filter.TextWords, filter.TextFields, data) : null;
        List<MediaTextTally>? tally = text == null ? null : new();
        foreach (MediaLibraryEntry entry in entries)
            switch (Verdict(entry, filter, data, audio, text, tags, tally))
            {
                case Judgement.Pass: passing.Add(entry); break;
                case Judgement.SoundUnknown: unknown.Add(MediaAudioMap.KeyOf(entry)!); break;
            }
        return new(passing, unknown.Count, tally);
    }

    /// <summary>Checks that every store the filter needs is available and every tag still exists.</summary>
    internal static void Validate(MediaLibraryFilter filter, MediaLibraryUserData data)
    {
        if (filter.Tags is { } tags)
        {
            if (data.TagSnapshot is not { } snapshot) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
            // A tag that was merged into another one still exists as that tag (the same rule as the thread filter), so a saved chip keeps working.
            if (tags.Resolve(snapshot) == null)
                throw new MediaLibraryFilterException(MediaLibraryFilterError.UnknownTag);
        }
        // "Without media tags" needs the tags as much as a tag filter: unavailable tags never read as "no tags".
        if (filter.WithoutMediaTags && data.TagSnapshot == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
        // A text that names the tags as a place to look (in:tag) needs them like a tag filter. The default scope (every field) does not:
        // without tags the other four fields answer, and the page's tag part is left out quietly.
        if (filter.TextWords.Count > 0 && filter.TextFields != MediaTextFields.All && (filter.TextFields & MediaTextFields.Tag) != 0 && data.TagSnapshot == null)
            throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
        if (filter.FavoritesOnly && data.FavoriteSource == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.FavoritesUnavailable);
        if (filter.MinimumRating != null && data.RatingSource == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.RatingsUnavailable);
    }

    private enum Judgement { Fail, Pass, SoundUnknown }

    private static Judgement Verdict(MediaLibraryEntry entry, MediaLibraryFilter filter, MediaLibraryUserData data, MediaAudioMap? audio,
        TextMatch? text, ArchiveTagFilter.Resolved? tags, List<MediaTextTally>? tally)
    {
        if (!Matches(entry, filter, data, text, tags, out TextOutcome outcome)) return Judgement.Fail;
        Judgement sound = SoundJudgement(entry, filter, audio);
        // The tally holds what passes everything but the text scope: a video whose sound is unknown is in no answer, so it is not in it either.
        if (tally != null && sound == Judgement.Pass) tally.Add(new MediaTextTally(entry, outcome.Fields));
        return outcome.InScope ? sound : Judgement.Fail;
    }

    private static Judgement SoundJudgement(MediaLibraryEntry entry, MediaLibraryFilter filter, MediaAudioMap? audio)
    {
        if (filter.Sound == MediaLibrarySoundFilter.Any) return Judgement.Pass;
        // Sound is a property of saved videos only; the type check is part of "implies videos".
        if (KindOf(entry) != MediaLibraryKindFilter.Video || !MediaLibraryAudioScanner.IsProbeable(entry)) return Judgement.Fail;
        return (audio?.Get(entry) ?? MediaAudio.Unknown) switch
        {
            MediaAudio.HasSound => filter.Sound == MediaLibrarySoundFilter.WithSound ? Judgement.Pass : Judgement.Fail,
            MediaAudio.Silent => filter.Sound == MediaLibrarySoundFilter.WithoutSound ? Judgement.Pass : Judgement.Fail,
            _ => Judgement.SoundUnknown
        };
    }

    /// <summary>
    /// True when the entry passes every part of the filter but the text scope: the text must hit under "All" (every word in some field),
    /// <paramref name="outcome"/> then says whether it also hits in the chosen scope and where every word hits on its own.
    /// </summary>
    private static bool Matches(MediaLibraryEntry entry, MediaLibraryFilter filter, MediaLibraryUserData data, TextMatch? text,
        ArchiveTagFilter.Resolved? resolved, out TextOutcome outcome)
    {
        outcome = TextOutcome.NoText;
        if (filter.Thread != null && entry.Thread != filter.Thread) return false;
        if (filter.Threads != null && !filter.Threads.Contains(entry.Thread)) return false;
        if (filter.Source != null && !string.Equals(entry.Thread.Source, filter.Source, StringComparison.Ordinal)) return false;
        if (filter.Board != null && !string.Equals(entry.Thread.Board, filter.Board, StringComparison.Ordinal)) return false;
        if (filter.ExcludeSources.Count > 0 && filter.ExcludeSources.Contains(entry.Thread.Source)) return false;
        if (filter.ExcludeBoards.Count > 0 && filter.ExcludeBoards.Contains(new MediaLibraryBoardKey(entry.Thread.Source, entry.Thread.Board))) return false;
        if (filter.Kinds.Count > 0 && !filter.Kinds.Contains(KindOf(entry))) return false;
        if (text != null)
        {
            outcome = text.Evaluate(entry);
            if (!outcome.UnderAll) return false;
        }
        if (filter.FavoritesOnly && !data.IsFavorite(entry)) return false;
        if (filter.MinimumRating is int minimum && !(data.RatingOf(entry) >= minimum)) return false;
        if (filter.WithoutMediaTags && !data.LacksMediaTags(entry)) return false;
        if (resolved is { } tags)
        {
            IReadOnlySet<Guid> carried = data.TagsOf(entry);
            if (tags.Exclude.Any(carried.Contains)) return false;
            if (tags.Include.Length > 0 && !(tags.Any ? tags.Include.Any(carried.Contains) : tags.Include.All(carried.Contains)))
                return false;
        }
        return true;
    }

    /// <summary>What the text said about one entry: every word hits somewhere (<paramref name="UnderAll"/>), also in the chosen scope
    /// (<paramref name="InScope"/>), and the fields in which every word hits on its own.</summary>
    private readonly record struct TextOutcome(bool UnderAll, bool InScope, MediaTextFields Fields)
    {
        internal static readonly TextOutcome NoText = new(true, true, MediaTextFields.All);
    }

    /// <summary>Test seam: the number of thread searches of the text on the calling thread so far (see <see cref="TextMatch"/>).</summary>
    internal static int ThreadSearchCount => TextMatch.ThreadSearches;

    /// <summary>
    /// Whether a word hits in one field of an entry: the one place that decides it, so the filter and the counts of the page agree.
    /// File name and thread title are searched as text (ignoring case); the board and the source are identifiers and match as a whole
    /// ("g" and "/g/" are the board g, "4chan" the source, "chan" none of them). The tag is no field of the entry itself: see <see cref="TextMatch"/>.
    /// </summary>
    internal static bool FieldHit(MediaTextFields field, MediaLibraryEntry entry, string word) => field switch
    {
        MediaTextFields.Name => Contains(entry.Name, word),
        MediaTextFields.Title => Contains(entry.ThreadTitle, word),
        MediaTextFields.Board => entry.Thread.Board is { } board && string.Equals(board, word.Trim('/'), StringComparison.OrdinalIgnoreCase),
        MediaTextFields.Source => entry.Thread.Source is { } source && string.Equals(source, word, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool Contains(string? field, string word) => field != null && field.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The free text part of one evaluation: every word must hit (ignoring case) in a field of the scope. The three thread fields (title,
    /// board, source) are the same for every occurrence of a thread, so they are searched once per thread (a large library has many files
    /// in few threads) and the answer is kept for this evaluation only; the file name is searched per occurrence. The tag field is the
    /// own tags of the occurrence and its thread: the tags whose name contains a word are found once per evaluation, then each occurrence
    /// asks for a set lookup. Without the tag index the tag field does not exist (the other fields answer).
    /// </summary>
    private sealed class TextMatch
    {
        private readonly IReadOnlyList<string> _words;
        private readonly MediaTextFields _scope, _available;
        private readonly MediaLibraryUserData _data;
        private readonly HashSet<Guid>?[]? _tagIds;
        private readonly Dictionary<ResourceId, (string? Title, MediaTextFields[] Hits)> _threads = new();

        internal TextMatch(IReadOnlyList<string> words, MediaTextFields scope, MediaLibraryUserData data)
        {
            _words = words;
            _scope = scope;
            _data = data;
            bool tags = data.TagSnapshot != null;
            _available = tags ? MediaTextFields.All : MediaTextFields.All & ~MediaTextFields.Tag;
            if (!tags) return;
            _tagIds = new HashSet<Guid>?[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                HashSet<Guid> ids = data.TagSnapshot!.Tags.Where(tag => Contains(tag.Name.Display, words[i])).Select(tag => tag.Id.Value).ToHashSet();
                _tagIds[i] = ids.Count == 0 ? null : ids;
            }
        }

        internal TextOutcome Evaluate(MediaLibraryEntry entry)
        {
            MediaTextFields common = _available; // the fields in which every word hits so far
            bool inScope = true;
            MediaTextFields[]? thread = null;
            for (int i = 0; i < _words.Count; i++)
            {
                string word = _words[i];
                thread ??= ThreadHits(entry);
                MediaTextFields hits = thread[i];
                if (FieldHit(MediaTextFields.Name, entry, word)) hits |= MediaTextFields.Name;
                if (_tagIds?[i] is { } ids && _data.CarriesAnyTag(entry, ids)) hits |= MediaTextFields.Tag;
                hits &= _available;
                if (hits == MediaTextFields.None) return new TextOutcome(false, false, MediaTextFields.None); // a word nowhere: no scope can answer
                common &= hits;
                if ((hits & _scope) == MediaTextFields.None) inScope = false;
            }
            return new TextOutcome(true, inScope, common);
        }

        private MediaTextFields[] ThreadHits(MediaLibraryEntry entry)
        {
            // Keyed by the thread; the title is compared too, so an index that carries two titles for one thread is never answered wrongly.
            if (_threads.TryGetValue(entry.Thread, out (string? Title, MediaTextFields[] Hits) known) && string.Equals(known.Title, entry.ThreadTitle, StringComparison.Ordinal))
                return known.Hits;
            MediaTextFields[] hits = new MediaTextFields[_words.Count];
            for (int i = 0; i < hits.Length; i++)
                foreach (MediaTextFields field in ThreadFields)
                    if (FieldHit(field, entry, _words[i])) hits[i] |= field;
            _threads[entry.Thread] = (entry.ThreadTitle, hits);
            _threadSearches++;
            return hits;
        }

        // Test seam: how often a thread's own fields were searched on this thread (once per thread and evaluation, not once per file).
        [ThreadStatic] private static int _threadSearches;
        internal static int ThreadSearches => _threadSearches;

        private static readonly MediaTextFields[] ThreadFields = [MediaTextFields.Title, MediaTextFields.Board, MediaTextFields.Source];
    }

    /// <summary>Type for the filter. GIF only by the .gif ending; unknown kinds count as other.</summary>
    internal static MediaLibraryKindFilter KindOf(MediaLibraryEntry entry) => entry.Kind switch
    {
        SavedMediaKind.Image => string.Equals(Path.GetExtension(entry.Name), ".gif", StringComparison.OrdinalIgnoreCase)
            ? MediaLibraryKindFilter.Gif : MediaLibraryKindFilter.Image,
        SavedMediaKind.Video => MediaLibraryKindFilter.Video,
        _ => MediaLibraryKindFilter.Other
    };

    /// <summary>Favorite and rating key of a saved occurrence; null for missing files.</summary>
    internal static FavoriteAssetKey? AssetKey(MediaLibraryEntry entry) =>
        entry.Presence == MediaLibraryPresence.Present && entry.MediaId != null && entry.Sha256 != null
            ? new FavoriteAssetKey(entry.Thread, entry.MediaId, entry.Variant, entry.Sha256) : null;

    /// <summary>
    /// Exact BOUND media tag target, built the same way the gallery builds it: the bound post attachment
    /// version plus the saved asset. Null for missing, unassigned or unbound (legacy) occurrences; those are
    /// resolved as local subjects by <see cref="MediaTagSubjects.Of(MediaLibraryEntry)"/>, the resolver views use.
    /// </summary>
    internal static PrivateMediaTagTarget? TagTarget(MediaLibraryEntry entry)
    {
        if (entry.Presence != MediaLibraryPresence.Present || entry.Unassigned || entry.PostId == null
            || entry.Position is not int position || entry.AttachmentRevisionKey == null) return null;
        TransferMediaRole role = entry.Variant == "original" ? TransferMediaRole.Original : TransferMediaRole.Preview;
        return PrivateMediaTagTarget.TryCreate(new Storage.AttachmentRevisionReference(entry.Thread, entry.PostId, position,
            entry.AttachmentRevisionKey), entry.MediaId, entry.Variant, role, entry.Sha256, out PrivateMediaTagTarget? target, out _)
            ? target : null;
    }
}
