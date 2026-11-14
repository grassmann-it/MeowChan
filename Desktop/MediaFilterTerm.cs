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
        return new MediaLibraryFilter(ArchiveTagFilter.OrNull(includeTags, excludeTags, tagMatch), MediaLibraryKindFilter.Any, favorites, rating,
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
