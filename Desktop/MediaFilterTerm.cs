using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>The fields a media filter term can be about (M2): every filter of the media page is one of these.</summary>
internal enum MediaFilterField { Tag, Type, Sound, Source, Board, Rating, Favorite, Missing, Untagged, Thread, Name }

/// <summary>
/// One filter of the media page as data: a field, its value and whether it excludes (M2 groundwork). A set of terms is what the chips of
/// the page show and what <see cref="MediaFilterTerms.ToFilter"/> turns into a <see cref="MediaLibraryFilter"/>; the set is the one
/// truth the controls and the chips will read and write. Pure, culture-neutral values:
/// <list type="bullet">
/// <item>Tag: <see cref="Value"/> is the tag id (format D); Type: image, gif, video or other; Sound: with or without;</item>
/// <item>Source: the source name; Board: the board name with its source in <see cref="Scope"/>;</item>
/// <item>Rating: the least rating, 1 to 5; Name: the text the file name contains; Thread: <see cref="Resource"/>;</item>
/// <item>Favorite, Missing, Untagged: flags, the value is empty.</item>
/// </list>
/// Only Tag, Type, Source and Board can be excluded (<see cref="CanExclude"/>); the other fields have no meaningful "not".
/// </summary>
internal sealed record MediaFilterTerm(MediaFilterField Field, string Value = "", bool Exclude = false, string? Scope = null, ResourceId? Resource = null)
{
    /// <summary>Whether a term of this field may exclude (the chips of these fields get the include/exclude switch).</summary>
    internal static bool CanExclude(MediaFilterField field) => field is MediaFilterField.Tag or MediaFilterField.Type or MediaFilterField.Source or MediaFilterField.Board;

    /// <summary>The same term, including instead of excluding or the other way round; a field that cannot exclude is returned unchanged.</summary>
    internal MediaFilterTerm Toggled() => CanExclude(Field) ? this with { Exclude = !Exclude } : this;

    /// <summary>The spelling of a field in a query (M3 will read the same names): lower case, English, never translated.</summary>
    internal static string KeyOf(MediaFilterField field) => field switch
    {
        MediaFilterField.Tag => "tag",
        MediaFilterField.Type => "type",
        MediaFilterField.Sound => "sound",
        MediaFilterField.Source => "source",
        MediaFilterField.Board => "board",
        MediaFilterField.Rating => "rating",
        MediaFilterField.Favorite => "fav",
        MediaFilterField.Missing => "missing",
        MediaFilterField.Untagged => "untagged",
        MediaFilterField.Thread => "thread",
        _ => "name"
    };

    /// <summary>The field a query name stands for (ignoring case); null when it is none.</summary>
    internal static MediaFilterField? FieldOf(string? key) =>
        Enum.GetValues<MediaFilterField>().Cast<MediaFilterField?>().FirstOrDefault(field => string.Equals(KeyOf(field!.Value), key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The value spelling of a type (the names of <see cref="MediaLibraryKindFilter"/> in lower case, never translated).</summary>
    internal static string TypeValue(MediaLibraryKindFilter kind) => kind == MediaLibraryKindFilter.Any ? throw new ArgumentOutOfRangeException(nameof(kind)) : kind.ToString().ToLowerInvariant();

    /// <summary>The type a value stands for; null for anything else (including "any", which is no term).</summary>
    internal static MediaLibraryKindFilter? TypeOf(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "image" => MediaLibraryKindFilter.Image,
        "gif" => MediaLibraryKindFilter.Gif,
        "video" => MediaLibraryKindFilter.Video,
        "other" => MediaLibraryKindFilter.Other,
        _ => null
    };

    internal static MediaFilterTerm Tag(Guid id, bool exclude = false) => new(MediaFilterField.Tag, id.ToString("D"), exclude);
    internal static MediaFilterTerm Type(MediaLibraryKindFilter kind, bool exclude = false) => new(MediaFilterField.Type, TypeValue(kind), exclude);
    internal static MediaFilterTerm Sound(MediaLibrarySoundFilter sound) => sound switch
    {
        MediaLibrarySoundFilter.WithSound => new(MediaFilterField.Sound, "with"),
        MediaLibrarySoundFilter.WithoutSound => new(MediaFilterField.Sound, "without"),
        _ => throw new ArgumentOutOfRangeException(nameof(sound), "Any sound is no term.")
    };
    internal static MediaFilterTerm Source(string source, bool exclude = false) => new(MediaFilterField.Source, source, exclude);
    internal static MediaFilterTerm Board(string source, string board, bool exclude = false) => new(MediaFilterField.Board, board, exclude, source);
    internal static MediaFilterTerm Rating(int least) => new(MediaFilterField.Rating, least.ToString(CultureInfo.InvariantCulture));
    internal static MediaFilterTerm Name(string text) => new(MediaFilterField.Name, text);
    internal static MediaFilterTerm InThread(ResourceId thread) => new(MediaFilterField.Thread, thread.Thread ?? string.Empty, Resource: thread);
    internal static MediaFilterTerm Flag(MediaFilterField field) => field is MediaFilterField.Favorite or MediaFilterField.Missing or MediaFilterField.Untagged
        ? new(field) : throw new ArgumentOutOfRangeException(nameof(field), "Only favorites, missing and untagged are flags.");
}

/// <summary>Turns a set of <see cref="MediaFilterTerm"/>s into the filter the core runs, and back. Pure; no controls.</summary>
internal static class MediaFilterTerms
{
    /// <summary>
    /// The filter the terms describe. Throws <see cref="ArgumentException"/> for a set that cannot be one filter, the way
    /// <c>MediaPage.CurrentFilter</c> does: a field that cannot exclude doing so, an unreadable value, two values for a field that holds one
    /// (source, board, thread, name, sound), a board whose source differs from the source term, types that leave nothing, or sound
    /// without videos. Several ratings count as the highest (all of them must hold). The "missing" flag is no part of the filter:
    /// the page presents missing files or not (<see cref="IncludesMissing"/>).
    /// </summary>
    internal static MediaLibraryFilter ToFilter(IEnumerable<MediaFilterTerm> terms, ArchiveTagMatch tagMatch = ArchiveTagMatch.All)
    {
        ArgumentNullException.ThrowIfNull(terms);
        List<Guid> includeTags = [], excludeTags = [];
        HashSet<MediaLibraryKindFilter> includeTypes = [], excludeTypes = [];
        HashSet<string> excludeSources = new(StringComparer.Ordinal);
        HashSet<MediaLibraryBoardKey> excludeBoards = [];
        string? source = null, board = null, boardSource = null, name = null;
        ResourceId? thread = null;
        int? rating = null;
        MediaLibrarySoundFilter sound = MediaLibrarySoundFilter.Any;
        bool favorites = false, untagged = false;
        foreach (MediaFilterTerm term in terms)
        {
            if (term == null) throw new ArgumentException("A term cannot be null.", nameof(terms));
            if (term.Exclude && !MediaFilterTerm.CanExclude(term.Field))
                throw new ArgumentException("The field '" + MediaFilterTerm.KeyOf(term.Field) + "' cannot be excluded.", nameof(terms));
            switch (term.Field)
            {
                case MediaFilterField.Tag:
                    if (!Guid.TryParseExact(term.Value, "D", out Guid id) || id == Guid.Empty) throw new ArgumentException("A tag term needs a tag id.", nameof(terms));
                    (term.Exclude ? excludeTags : includeTags).Add(id);
                    break;
                case MediaFilterField.Type:
                    MediaLibraryKindFilter kind = MediaFilterTerm.TypeOf(term.Value) ?? throw new ArgumentException("Unknown type '" + term.Value + "'.", nameof(terms));
                    (term.Exclude ? excludeTypes : includeTypes).Add(kind);
                    break;
                case MediaFilterField.Sound:
                    MediaLibrarySoundFilter wanted = term.Value switch
                    {
                        "with" => MediaLibrarySoundFilter.WithSound,
                        "without" => MediaLibrarySoundFilter.WithoutSound,
                        _ => throw new ArgumentException("Unknown sound value '" + term.Value + "'.", nameof(terms))
                    };
                    if (sound != MediaLibrarySoundFilter.Any && sound != wanted) throw new ArgumentException("Sound cannot be both with and without.", nameof(terms));
                    sound = wanted;
                    break;
                case MediaFilterField.Source:
                    if (string.IsNullOrEmpty(term.Value)) throw new ArgumentException("A source term needs a source.", nameof(terms));
                    if (term.Exclude) excludeSources.Add(term.Value);
                    else if (source != null && source != term.Value) throw new ArgumentException("Only one source can be included.", nameof(terms));
                    else source = term.Value;
                    break;
                case MediaFilterField.Board:
                    if (string.IsNullOrEmpty(term.Value) || string.IsNullOrEmpty(term.Scope)) throw new ArgumentException("A board term needs a board and its source.", nameof(terms));
                    if (term.Exclude) excludeBoards.Add(new MediaLibraryBoardKey(term.Scope, term.Value));
                    else if (board != null && (board != term.Value || boardSource != term.Scope)) throw new ArgumentException("Only one board can be included.", nameof(terms));
                    else { board = term.Value; boardSource = term.Scope; }
                    break;
                case MediaFilterField.Rating:
                    if (!int.TryParse(term.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int least) || least is < 1 or > 5)
                        throw new ArgumentException("A rating term needs a whole value from 1 to 5.", nameof(terms));
                    rating = Math.Max(rating ?? 0, least);
                    break;
                case MediaFilterField.Name:
                    if (string.IsNullOrWhiteSpace(term.Value)) throw new ArgumentException("A name term needs text.", nameof(terms));
                    if (name != null && !string.Equals(name, term.Value.Trim(), StringComparison.Ordinal)) throw new ArgumentException("Only one name text can be set.", nameof(terms));
                    name = term.Value.Trim();
                    break;
                case MediaFilterField.Thread:
                    if (term.Resource is not { Thread: { Length: > 0 } } resource) throw new ArgumentException("A thread term needs a thread.", nameof(terms));
                    if (thread != null && thread != resource) throw new ArgumentException("Only one thread can be set.", nameof(terms));
                    thread = resource;
                    break;
                case MediaFilterField.Favorite: favorites = true; break;
                case MediaFilterField.Untagged: untagged = true; break;
                case MediaFilterField.Missing: break;
                default: throw new ArgumentOutOfRangeException(nameof(terms), term.Field, "Unknown field.");
            }
        }
        // A board names its source (the board of the control carries it); a source term for another source cannot hold together with it.
        if (board != null)
        {
            if (source != null && source != boardSource) throw new ArgumentException("The board belongs to another source than the included one.", nameof(terms));
            source = boardSource;
        }
        // The types: what is included minus what is excluded, or every type but the excluded ones; nothing left is refused (an empty set means "all").
        IReadOnlySet<MediaLibraryKindFilter>? kinds = null;
        if (includeTypes.Count > 0)
        {
            includeTypes.ExceptWith(excludeTypes);
            if (includeTypes.Count == 0) throw new ArgumentException("The included types are all excluded.", nameof(terms));
            kinds = includeTypes;
        }
        else if (excludeTypes.Count > 0) kinds = MediaLibraryFilter.AllKindsExcept([.. excludeTypes]);
        return new MediaLibraryFilter(ArchiveTagFilter.OrNull(includeTags, excludeTags, tagMatch, ArchiveTagFilter.MaximumConfigurableTagsPerList), MediaLibraryKindFilter.Any, favorites, rating,
            source, board, name, thread, sound, untagged, kinds, excludeSources, excludeBoards);
    }

    /// <summary>Whether the terms ask to show missing files (a flag of the presentation, not of the filter).</summary>
    internal static bool IncludesMissing(IEnumerable<MediaFilterTerm> terms) => terms.Any(term => term.Field == MediaFilterField.Missing);

    /// <summary>
    /// The terms of a filter, in the order of <see cref="MediaFilterField"/> (tags: included before excluded). A type set of up to two
    /// types reads as included types, a larger one as the excluded rest ("without video"), so <see cref="ToFilter"/> gives the same
    /// filter back. <paramref name="includeMissing"/> adds the missing flag.
    /// </summary>
    internal static IReadOnlyList<MediaFilterTerm> FromFilter(MediaLibraryFilter filter, bool includeMissing = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        List<MediaFilterTerm> terms = [];
        if (filter.Tags is { } tags)
        {
            terms.AddRange(tags.Include.Select(id => MediaFilterTerm.Tag(id)));
            terms.AddRange(tags.Exclude.Select(id => MediaFilterTerm.Tag(id, exclude: true)));
        }
        if (filter.Kinds.Count > 0)
        {
            if (filter.Kinds.Count <= 2) terms.AddRange(filter.Kinds.OrderBy(kind => kind).Select(kind => MediaFilterTerm.Type(kind)));
            else terms.AddRange(Enum.GetValues<MediaLibraryKindFilter>().Where(kind => kind != MediaLibraryKindFilter.Any && !filter.Kinds.Contains(kind))
                .Select(kind => MediaFilterTerm.Type(kind, exclude: true)));
        }
        if (filter.Sound != MediaLibrarySoundFilter.Any) terms.Add(MediaFilterTerm.Sound(filter.Sound));
        if (filter.Source != null && filter.Board == null) terms.Add(MediaFilterTerm.Source(filter.Source));
        terms.AddRange(filter.ExcludeSources.Order(StringComparer.Ordinal).Select(value => MediaFilterTerm.Source(value, exclude: true)));
        // A board term carries its source, so an included source beside it needs no term of its own.
        if (filter.Board != null) terms.Add(MediaFilterTerm.Board(filter.Source!, filter.Board));
        terms.AddRange(filter.ExcludeBoards.OrderBy(key => key.Source, StringComparer.Ordinal).ThenBy(key => key.Board, StringComparer.Ordinal)
            .Select(key => MediaFilterTerm.Board(key.Source, key.Board, exclude: true)));
        if (filter.MinimumRating is int rating) terms.Add(MediaFilterTerm.Rating(rating));
        if (filter.FavoritesOnly) terms.Add(MediaFilterTerm.Flag(MediaFilterField.Favorite));
        if (includeMissing) terms.Add(MediaFilterTerm.Flag(MediaFilterField.Missing));
        if (filter.WithoutMediaTags) terms.Add(MediaFilterTerm.Flag(MediaFilterField.Untagged));
        if (filter.Thread != null) terms.Add(MediaFilterTerm.InThread(filter.Thread));
        if (filter.NameContains != null) terms.Add(MediaFilterTerm.Name(filter.NameContains));
        return terms;
    }
}

/// <summary>
/// M2: the rules of the media page's term set (the one truth its chips, its section combos and its "+ Filter" picker write). Pure:
/// each operation returns the next set and never touches a control. The rules keep the set one filter where a field holds one value:
/// <list type="bullet">
/// <item>tags, types and the flags are added (a tag or type of the other polarity is replaced);</item>
/// <item>sound, rating and thread replace their field; an included source or board replaces the included one;</item>
/// <item>an included source drops an included board of another source, and an excluded source drops an included board of it;</item>
/// <item>a sound implies videos: choosing one makes the type "Videos" unless videos are already among the shown types, and a type
/// change that leaves videos out (or every type in) drops the sound, as the section's combos did before.</item>
/// </list>
/// </summary>
internal static class MediaFilterTermSet
{
    /// <summary>A term as a value of its field regardless of include/exclude (what "already in the filter" compares).</summary>
    internal static MediaFilterTerm Identity(MediaFilterTerm term) => term.Exclude ? term with { Exclude = false } : term;

    /// <summary>A stable text key of a term's identity ("type:video", "board:4chan/g"), e.g. to find its chip again after a change.</summary>
    internal static string IdentityKey(MediaFilterTerm term) => MediaFilterTerm.KeyOf(term.Field) + ":" + (term.Scope == null ? "" : term.Scope + "/")
        + (term.Field == MediaFilterField.Thread ? term.Resource?.ToString() ?? term.Value : term.Value);

    /// <summary>Whether the set holds this value of the field, included or excluded.</summary>
    internal static bool Contains(IEnumerable<MediaFilterTerm> terms, MediaFilterTerm term) => terms.Any(other => Identity(other) == Identity(term));

    /// <summary>The set with <paramref name="term"/> in it, following the field rules above.</summary>
    internal static IReadOnlyList<MediaFilterTerm> With(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.Exclude && !MediaFilterTerm.CanExclude(term.Field)) term = term with { Exclude = false };
        MediaFilterTerm identity = Identity(term);
        List<MediaFilterTerm> next = terms.Where(other => Identity(other) != identity).ToList();
        switch (term.Field)
        {
            case MediaFilterField.Sound or MediaFilterField.Rating or MediaFilterField.Thread or MediaFilterField.Name:
                next.RemoveAll(other => other.Field == term.Field);
                break;
            case MediaFilterField.Source when !term.Exclude:
                next.RemoveAll(other => other.Field == MediaFilterField.Source && !other.Exclude);
                next.RemoveAll(other => other.Field == MediaFilterField.Board && !other.Exclude && other.Scope != term.Value);
                break;
            case MediaFilterField.Source:
                next.RemoveAll(other => other.Field == MediaFilterField.Board && !other.Exclude && other.Scope == term.Value);
                break;
            case MediaFilterField.Board when !term.Exclude:
                next.RemoveAll(other => other.Field == MediaFilterField.Board && !other.Exclude);
                // The board names its source: an included source of another one cannot hold beside it.
                next.RemoveAll(other => other.Field == MediaFilterField.Source && !other.Exclude && other.Value != term.Scope);
                next.RemoveAll(other => other.Field == MediaFilterField.Source && other.Exclude && other.Value == term.Scope);
                break;
        }
        next.Add(term);
        if (term.Field == MediaFilterField.Sound) return WithVideos(next);
        return term.Field == MediaFilterField.Type ? DropSoundWithoutVideos(next) : next;
    }

    /// <summary>The set without this value of the field (either polarity).</summary>
    internal static IReadOnlyList<MediaFilterTerm> Without(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        MediaFilterTerm identity = Identity(term);
        List<MediaFilterTerm> next = terms.Where(other => Identity(other) != identity).ToList();
        return term.Field == MediaFilterField.Type ? DropSoundWithoutVideos(next) : next;
    }

    /// <summary>The set without any term of the field (e.g. the combo's "All types").</summary>
    internal static IReadOnlyList<MediaFilterTerm> WithoutField(IReadOnlyList<MediaFilterTerm> terms, MediaFilterField field, bool? exclude = null)
    {
        List<MediaFilterTerm> next = terms.Where(other => other.Field != field || (exclude != null && other.Exclude != exclude)).ToList();
        return field == MediaFilterField.Type ? DropSoundWithoutVideos(next) : next;
    }

    /// <summary>The set with this term switched between include and exclude (moved behind its new polarity); unchanged for a field that cannot exclude.</summary>
    internal static IReadOnlyList<MediaFilterTerm> Toggled(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        if (!MediaFilterTerm.CanExclude(term.Field)) return terms;
        MediaFilterTerm? held = terms.FirstOrDefault(other => Identity(other) == Identity(term));
        return held == null ? terms : With(terms, held.Toggled());
    }

    /// <summary>
    /// "Show only this": the set without every other term of this term's field (the other tags, included and excluded, the other types,
    /// sources or boards), the term itself kept. A type change that leaves videos out drops the sound, as for any other type change.
    /// The set itself when it does not hold the term.
    /// </summary>
    internal static IReadOnlyList<MediaFilterTerm> OnlyThis(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        MediaFilterTerm? held = terms.FirstOrDefault(other => Identity(other) == Identity(term));
        if (held == null) return terms;
        List<MediaFilterTerm> next = terms.Where(other => other.Field != held.Field || ReferenceEquals(other, held)).ToList();
        return held.Field == MediaFilterField.Type ? DropSoundWithoutVideos(next) : next;
    }

    /// <summary>
    /// Whether "Show only this" does something for this chip: an included value of a field that holds several (tag, type, source,
    /// board) while the set has another term of that field. An excluded chip has no "only" (it hides, it does not single out).
    /// </summary>
    internal static bool CanShowOnly(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term) =>
        !term.Exclude && MediaFilterTerm.CanExclude(term.Field) && OnlyThis(terms, term).Count < terms.Count;

    /// <summary>The set whose types are exactly <paramref name="kinds"/> included (none: every type), as the section's type combo chooses.</summary>
    internal static IReadOnlyList<MediaFilterTerm> WithTypes(IReadOnlyList<MediaFilterTerm> terms, params MediaLibraryKindFilter[] kinds)
    {
        List<MediaFilterTerm> next = terms.Where(other => other.Field != MediaFilterField.Type).ToList();
        next.AddRange(kinds.Distinct().Select(kind => MediaFilterTerm.Type(kind)));
        return DropSoundWithoutVideos(next);
    }

    /// <summary>
    /// The types the set shows (null: every type). Included types minus excluded ones, or every type but the excluded ones; an empty
    /// set means the types leave nothing (<see cref="MediaFilterTerms.ToFilter"/> refuses that set).
    /// </summary>
    internal static IReadOnlySet<MediaLibraryKindFilter>? ShownKinds(IEnumerable<MediaFilterTerm> terms)
    {
        HashSet<MediaLibraryKindFilter> include = [], exclude = [];
        foreach (MediaFilterTerm term in terms.Where(term => term.Field == MediaFilterField.Type))
            if (MediaFilterTerm.TypeOf(term.Value) is { } kind) (term.Exclude ? exclude : include).Add(kind);
        if (include.Count == 0 && exclude.Count == 0) return null;
        HashSet<MediaLibraryKindFilter> shown = include.Count > 0 ? include
            : [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other];
        shown.ExceptWith(exclude);
        return shown;
    }

    /// <summary>
    /// The place of a term's chip in the row (the order the chips had before M2): included tags, excluded tags, [the tag mode chip, 2],
    /// thread, without media tags, name, type, sound, source, board, rating, favourites, missing; within a field included before excluded.
    /// </summary>
    internal static int DisplayRank(MediaFilterTerm term) => term.Field switch
    {
        MediaFilterField.Tag => term.Exclude ? 1 : 0,
        MediaFilterField.Thread => 3,
        MediaFilterField.Untagged => 4,
        MediaFilterField.Name => 5,
        MediaFilterField.Type => 6,
        MediaFilterField.Sound => 7,
        MediaFilterField.Source => 8,
        MediaFilterField.Board => 9,
        MediaFilterField.Rating => 10,
        MediaFilterField.Favorite => 11,
        _ => 12
    };

    /// <summary>The terms in chip order (stable: within a rank and polarity the set's order).</summary>
    internal static IReadOnlyList<MediaFilterTerm> Ordered(IEnumerable<MediaFilterTerm> terms) =>
        terms.Select((term, index) => (term, index)).OrderBy(item => DisplayRank(item.term)).ThenBy(item => item.term.Exclude).ThenBy(item => item.index)
            .Select(item => item.term).ToList();

    // A sound needs videos among the shown types: unless they are (and the type is restricted), the type becomes "Videos".
    private static List<MediaFilterTerm> WithVideos(List<MediaFilterTerm> terms)
    {
        if (ShownKinds(terms) is { } shown && shown.Contains(MediaLibraryKindFilter.Video)) return terms;
        terms.RemoveAll(other => other.Field == MediaFilterField.Type);
        terms.Add(MediaFilterTerm.Type(MediaLibraryKindFilter.Video));
        return terms;
    }

    private static List<MediaFilterTerm> DropSoundWithoutVideos(List<MediaFilterTerm> terms)
    {
        if (!terms.Any(term => term.Field == MediaFilterField.Sound)) return terms;
        if (ShownKinds(terms) is not { } shown || !shown.Contains(MediaLibraryKindFilter.Video))
            terms.RemoveAll(term => term.Field == MediaFilterField.Sound);
        return terms;
    }
}
