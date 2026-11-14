using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Filter of the media page. Immutable; an empty filter shows everything. Tags work like the archive
/// tag filter, with one difference: a media occurrence carries its own media tags plus the tags of
/// its thread, so a thread tag such as "rpt" finds every file in those threads.
/// </summary>
internal sealed record MediaLibraryFilter
{
    internal const int MaximumNameLength = 200;

    internal MediaLibraryFilter(ArchiveTagFilter? tags = null, MediaLibraryKindFilter kind = MediaLibraryKindFilter.Any,
        bool favoritesOnly = false, int? minimumRating = null, string? source = null, string? board = null,
        string? nameContains = null, ResourceId? thread = null, MediaLibrarySoundFilter sound = MediaLibrarySoundFilter.Any,
        bool withoutMediaTags = false)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (minimumRating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(minimumRating), "Ratings are whole values from 1 to 5.");
        if (!Enum.IsDefined(sound)) throw new ArgumentOutOfRangeException(nameof(sound));
        if (sound != MediaLibrarySoundFilter.Any && kind is not (MediaLibraryKindFilter.Any or MediaLibraryKindFilter.Video))
            throw new ArgumentException("A sound filter concerns videos only.", nameof(sound));
        if (board != null && source == null) throw new ArgumentException("A board filter needs its source.", nameof(board));
        string? name = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
        if (name is { Length: > MaximumNameLength }) throw new ArgumentException("The file name text is too long.", nameof(nameContains));
        Tags = tags;
        Kind = kind;
        FavoritesOnly = favoritesOnly;
        MinimumRating = minimumRating;
        Source = string.IsNullOrEmpty(source) ? null : source;
        Board = string.IsNullOrEmpty(board) ? null : board;
        NameContains = name;
        Thread = thread;
        Sound = sound;
        WithoutMediaTags = withoutMediaTags;
    }

    internal static MediaLibraryFilter None { get; } = new();

    internal ArchiveTagFilter? Tags { get; }
    internal MediaLibraryKindFilter Kind { get; }
    internal bool FavoritesOnly { get; }
    internal int? MinimumRating { get; }
    internal string? Source { get; }
    internal string? Board { get; }
    internal string? NameContains { get; }
    /// <summary>The thread as a meta tag: only media of exactly this thread.</summary>
    internal ResourceId? Thread { get; }
    /// <summary>With or without sound; videos whose sound is not known are shown for neither choice.</summary>
    internal MediaLibrarySoundFilter Sound { get; }
    /// <summary>Only media that could carry media tags but carry none yet (<see cref="MediaLibraryUserData.LacksMediaTags"/>); thread tags do not count.</summary>
    internal bool WithoutMediaTags { get; }

    internal bool IsEmpty => Tags == null && Kind == MediaLibraryKindFilter.Any && !FavoritesOnly && MinimumRating == null
        && Source == null && Board == null && NameContains == null && Thread == null && Sound == MediaLibrarySoundFilter.Any && !WithoutMediaTags;
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

    internal MediaLibraryUserData(PrivateTagStoreSnapshot? tags, IReadOnlyList<FavoriteEntry>? favorites,
        IReadOnlyList<MediaRatingEntry>? ratings)
    {
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

    /// <summary>True when both views were built from the very same store states.</summary>
    internal bool SameSourcesAs(MediaLibraryUserData? other) => other != null && ReferenceEquals(TagSnapshot, other.TagSnapshot)
        && ReferenceEquals(FavoriteSource, other.FavoriteSource) && ReferenceEquals(RatingSource, other.RatingSource);

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

    // The one reading of an occurrence's own tags: its thread's tags and those of its media subject (null: none; sets are never empty).
    private (HashSet<Guid>? Thread, HashSet<Guid>? Own, MediaTagSubject? Subject) Carried(MediaLibraryEntry entry)
    {
        if (_threadTags == null || _mediaTags == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
        MediaTagSubject? subject = MediaTagSubjects.Of(entry);
        HashSet<Guid>? own = subject switch
        {
            MediaTagSubject.Bound bound => _mediaTags.GetValueOrDefault(bound.Target),
            MediaTagSubject.Local local when TagSnapshot!.ResolveLocal(local.Observation) is { IsRegistered: true, Target: { } target } =>
                _localTags!.GetValueOrDefault(target),
            _ => null
        };
        return (_threadTags.GetValueOrDefault(entry.Thread), own, subject);
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
/// Entries that passed a filter, and how many videos could not be judged for a sound filter because their
/// sound is not known (not probed yet, or the headers did not tell). Those videos are in neither answer.
/// </summary>
internal sealed record MediaLibraryFilterResult(IReadOnlyList<MediaLibraryEntry> Passing, int UnknownSoundVideos);

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
    /// </summary>
    internal static MediaLibraryFilterResult Evaluate(IEnumerable<MediaLibraryEntry> entries, MediaLibraryFilter filter,
        MediaLibraryUserData data, MediaAudioMap? audio = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(data);
        if (filter.IsEmpty) return new(entries as IReadOnlyList<MediaLibraryEntry> ?? entries.ToArray(), 0);
        Validate(filter, data);
        List<MediaLibraryEntry> passing = new();
        HashSet<string> unknown = new(StringComparer.Ordinal);
        foreach (MediaLibraryEntry entry in entries)
            switch (Verdict(entry, filter, data, audio))
            {
                case Judgement.Pass: passing.Add(entry); break;
                case Judgement.SoundUnknown: unknown.Add(MediaAudioMap.KeyOf(entry)!); break;
            }
        return new(passing, unknown.Count);
    }

    /// <summary>Checks that every store the filter needs is available and every tag still exists.</summary>
    internal static void Validate(MediaLibraryFilter filter, MediaLibraryUserData data)
    {
        if (filter.Tags is { } tags)
        {
            if (data.TagSnapshot is not { } snapshot) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
            HashSet<Guid> known = snapshot.Tags.Select(tag => tag.Id.Value).ToHashSet();
            if (tags.Include.Concat(tags.Exclude).Any(id => !known.Contains(id)))
                throw new MediaLibraryFilterException(MediaLibraryFilterError.UnknownTag);
        }
        // "Without media tags" needs the tags as much as a tag filter: unavailable tags never read as "no tags".
        if (filter.WithoutMediaTags && data.TagSnapshot == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable);
        if (filter.FavoritesOnly && data.FavoriteSource == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.FavoritesUnavailable);
        if (filter.MinimumRating != null && data.RatingSource == null) throw new MediaLibraryFilterException(MediaLibraryFilterError.RatingsUnavailable);
    }

    private enum Judgement { Fail, Pass, SoundUnknown }

    private static Judgement Verdict(MediaLibraryEntry entry, MediaLibraryFilter filter, MediaLibraryUserData data, MediaAudioMap? audio)
    {
        if (!Matches(entry, filter, data)) return Judgement.Fail;
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

    private static bool Matches(MediaLibraryEntry entry, MediaLibraryFilter filter, MediaLibraryUserData data)
    {
        if (filter.Thread != null && entry.Thread != filter.Thread) return false;
        if (filter.Source != null && !string.Equals(entry.Thread.Source, filter.Source, StringComparison.Ordinal)) return false;
        if (filter.Board != null && !string.Equals(entry.Thread.Board, filter.Board, StringComparison.Ordinal)) return false;
        if (filter.Kind != MediaLibraryKindFilter.Any && KindOf(entry) != filter.Kind) return false;
        if (filter.NameContains != null && (entry.Name == null
            || entry.Name.IndexOf(filter.NameContains, StringComparison.OrdinalIgnoreCase) < 0)) return false;
        if (filter.FavoritesOnly && !data.IsFavorite(entry)) return false;
        if (filter.MinimumRating is int minimum && !(data.RatingOf(entry) >= minimum)) return false;
        if (filter.WithoutMediaTags && !data.LacksMediaTags(entry)) return false;
        if (filter.Tags is { } tags)
        {
            IReadOnlySet<Guid> carried = data.TagsOf(entry);
            if (tags.Exclude.Any(carried.Contains)) return false;
            if (tags.Include.Count > 0 && !(tags.Match == ArchiveTagMatch.Any ? tags.Include.Any(carried.Contains) : tags.Include.All(carried.Contains)))
                return false;
        }
        return true;
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
