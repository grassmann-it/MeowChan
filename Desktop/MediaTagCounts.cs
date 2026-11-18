using System;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>The two groups of the Booru mode's tag list: the profile's own tags and the provider's source tags (Danbooru).</summary>
internal enum MediaTagGroup { Own, Source }

/// <summary>One tag of the current results and how many of the shown tiles carry it. <see cref="Key"/>: the own tag id (format D) or the source tag name.</summary>
internal readonly record struct MediaTagCount(MediaTagGroup Group, string Key, string Name, int Count);

/// <summary>
/// The tags of one result set: the own tags and the source tags that at least one shown tile carries, with their counts (unordered;
/// <see cref="MediaTagList"/> orders them). <see cref="Media"/> is the number of tiles counted, <see cref="SourceMedia"/> how many of
/// them are downloaded provider media. An unavailable group (own tags unreadable, Booru catalog unreadable) is empty and says so.
/// </summary>
internal sealed record MediaTagCountResult(IReadOnlyList<MediaTagCount> Own, IReadOnlyList<MediaTagCount> Source, int Media, int SourceMedia,
    bool OwnAvailable, bool SourceAvailable)
{
    internal static MediaTagCountResult Empty { get; } = new([], [], 0, 0, true, true);
}

/// <summary>
/// MP4: counts the tags of the media page's current results for the Booru mode, off the UI thread. Every occurrence's tags are resolved
/// once (own tags through the profile's tag data, source tags through the catalog's index) into small integer ids and kept by the
/// occurrence's reference; a filter change only adds the ids of the shown tiles up again, so a recount over 50,000 tiles and 1,000 tags is
/// one pass over a few hundred thousand integers. A new index of the media page keeps the resolution of every occurrence it shares with
/// the last one (unchanged threads keep their entry objects); only new occurrences are resolved. A change of the own tags or of the
/// catalog starts over, since every resolution may differ. One tile counts once per tag: the occurrence the tile shows is what is counted
/// (the same tags the tile's chips show), so a click on a tag with count n shows about n tiles.
/// Thread-safe: counts of one index run one after the other.
/// </summary>
internal sealed class MediaTagCountIndex
{
    private readonly object _sync = new();
    private readonly Func<MediaLibraryEntry, IEnumerable<Guid>>? _ownTags;
    private readonly Func<Guid, string?> _ownName;
    private readonly Func<MediaLibraryEntry, IReadOnlyList<string>>? _sourceTags;
    private readonly Dictionary<Guid, int> _ownIds;
    private readonly List<Guid> _own;
    private readonly Dictionary<string, int> _sourceIds;
    private readonly List<string> _source;
    private readonly Dictionary<MediaLibraryEntry, Resolved> _resolved;
    private bool _ownFailed;

    private readonly record struct Resolved(int[] Own, int[] Source);

    private MediaTagCountIndex(IReadOnlyList<MediaLibraryEntry> all, object? ownSource, Func<MediaLibraryEntry, IEnumerable<Guid>>? ownTags,
        Func<Guid, string?> ownName, object? sourceSource, Func<MediaLibraryEntry, IReadOnlyList<string>>? sourceTags, MediaTagCountIndex? reuse)
    {
        All = all;
        OwnSource = ownSource;
        SourceSource = sourceSource;
        _ownTags = ownTags;
        _ownName = ownName;
        _sourceTags = sourceTags;
        if (reuse == null)
        {
            _ownIds = new Dictionary<Guid, int>();
            _own = new List<Guid>();
            _sourceIds = new Dictionary<string, int>(StringComparer.Ordinal);
            _source = new List<string>();
            _resolved = new Dictionary<MediaLibraryEntry, Resolved>(ReferenceEqualityComparer.Instance);
            return;
        }
        // The same own tags and catalog: the ids and every resolution of an occurrence that is still in the index stay valid.
        lock (reuse._sync)
        {
            _ownIds = new Dictionary<Guid, int>(reuse._ownIds);
            _own = new List<Guid>(reuse._own);
            _sourceIds = new Dictionary<string, int>(reuse._sourceIds, StringComparer.Ordinal);
            _source = new List<string>(reuse._source);
            _resolved = new Dictionary<MediaLibraryEntry, Resolved>(Math.Min(reuse._resolved.Count, all.Count), ReferenceEqualityComparer.Instance);
            foreach (MediaLibraryEntry entry in all)
                if (reuse._resolved.TryGetValue(entry, out Resolved resolved)) _resolved[entry] = resolved;
            Reused = _resolved.Count;
        }
    }

    /// <summary>The index entries this was made for.</summary>
    internal IReadOnlyList<MediaLibraryEntry> All { get; }
    /// <summary>What the own tags were read from (the tag snapshot of the page's user data, never its favorites or ratings); a different one means a new index.</summary>
    internal object? OwnSource { get; }
    /// <summary>What the source tags were read from (the catalog's tag index); a different one means a new index.</summary>
    internal object? SourceSource { get; }
    /// <summary>How many resolutions were taken over from the previous index (tests).</summary>
    internal int Reused { get; }
    /// <summary>How many occurrences are resolved now (tests).</summary>
    internal int ResolvedCount { get { lock (_sync) return _resolved.Count; } }

    /// <summary>
    /// The index for <paramref name="all"/>: <paramref name="previous"/> itself when nothing changed, a new one that keeps the previous
    /// resolutions when only the media index changed, else a new empty one. <paramref name="ownTags"/> null: own tags are unavailable;
    /// <paramref name="sourceTags"/> null: the catalog is unavailable (or there is none).
    /// </summary>
    internal static MediaTagCountIndex Create(IReadOnlyList<MediaLibraryEntry> all, object? ownSource, Func<MediaLibraryEntry, IEnumerable<Guid>>? ownTags,
        Func<Guid, string?> ownName, object? sourceSource, Func<MediaLibraryEntry, IReadOnlyList<string>>? sourceTags, MediaTagCountIndex? previous)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(ownName);
        bool same = previous != null && ReferenceEquals(previous.OwnSource, ownSource) && ReferenceEquals(previous.SourceSource, sourceSource)
            && (previous._ownTags == null) == (ownTags == null) && (previous._sourceTags == null) == (sourceTags == null);
        if (same && ReferenceEquals(previous!.All, all)) return previous;
        return new MediaTagCountIndex(all, ownSource, ownTags, ownName, sourceSource, sourceTags, same ? previous : null);
    }

    /// <summary>The tags of <paramref name="shown"/> (one occurrence per tile) with their counts.</summary>
    internal MediaTagCountResult Count(IReadOnlyList<MediaLibraryEntry> shown, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        lock (_sync)
        {
            Resolved[] rows = new Resolved[shown.Count];
            int sourceMedia = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
                MediaLibraryEntry entry = shown[i];
                if (!_resolved.TryGetValue(entry, out Resolved resolved)) _resolved[entry] = resolved = Resolve(entry);
                rows[i] = resolved;
                if (entry.BooruPostId != null) sourceMedia++;
            }
            token.ThrowIfCancellationRequested();
            int[] own = new int[_own.Count], source = new int[_source.Count];
            foreach (Resolved row in rows)
            {
                foreach (int id in row.Own) own[id]++;
                foreach (int id in row.Source) source[id]++;
            }
            List<MediaTagCount> ownCounts = new(), sourceCounts = new();
            for (int id = 0; id < own.Length; id++)
                if (own[id] > 0 && _ownName(_own[id]) is { } name) ownCounts.Add(new MediaTagCount(MediaTagGroup.Own, _own[id].ToString("D"), name, own[id]));
            for (int id = 0; id < source.Length; id++)
                if (source[id] > 0) sourceCounts.Add(new MediaTagCount(MediaTagGroup.Source, _source[id], _source[id], source[id]));
            return new MediaTagCountResult(ownCounts, sourceCounts, shown.Count, sourceMedia, _ownTags != null && !_ownFailed,
                _sourceTags != null || sourceMedia == 0);
        }
    }

    private static readonly int[] None = Array.Empty<int>();

    private Resolved Resolve(MediaLibraryEntry entry)
    {
        int[] own = None, source = None;
        if (_ownTags != null && !_ownFailed)
        {
            try { own = Ids(_ownTags(entry), _ownIds, _own); }
            catch (MediaLibraryFilterException) { _ownFailed = true; } // The tag data went away: the group says so instead of counting less.
        }
        if (_sourceTags != null && entry.BooruPostId != null) source = Ids(_sourceTags(entry), _sourceIds, _source);
        return new Resolved(own, source);
    }

    private static int[] Ids<T>(IEnumerable<T> tags, Dictionary<T, int> ids, List<T> names) where T : notnull
    {
        List<int>? found = null;
        foreach (T tag in tags)
        {
            if (!ids.TryGetValue(tag, out int id)) { id = names.Count; ids[tag] = id; names.Add(tag); }
            (found ??= new List<int>()).Add(id);
        }
        return found == null ? None : found.ToArray();
    }
}
