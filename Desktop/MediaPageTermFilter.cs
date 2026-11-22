using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>The key of the tagging queue a media index entry would have (the fields <see cref="TagQueueItem.From(MediaLibraryEntry, DateTimeOffset)"/> uses).</summary>
internal static class TagQueueKeys
{
    /// <summary>The queue key of the occurrence, or null when it has no saved asset to be queued.</summary>
    internal static TagQueueKey? Of(MediaLibraryEntry entry) =>
        entry.Directory is { } directory && entry.AssetId is { } asset
            ? TagQueueKey.From(entry.Thread, directory, asset, entry.PostId, entry.Position, entry.ImportItemId, entry.BooruPostId)
            : null;

    /// <summary>The keys of every entry of a queue snapshot (all states: a finished entry is still in the queue until it is removed).</summary>
    internal static IReadOnlySet<TagQueueKey> Set(TagQueueSnapshot snapshot) => snapshot.Items.Select(item => item.Key).ToHashSet();
}

/// <summary>
/// MP4: the provider's tags of the downloaded Booru media, by post id, read from one catalog snapshot (read-only, no network). Names are
/// kept in lower case, the way <see cref="MediaFilterTerm.SourceTag"/> spells them. An entry that is no provider medium has none.
/// </summary>
internal sealed class MediaSourceTagIndex
{
    private readonly Dictionary<string, string[]> _byPost;

    private MediaSourceTagIndex(BooruCatalogSnapshot? catalog, Dictionary<string, string[]> byPost)
    {
        Catalog = catalog;
        _byPost = byPost;
    }

    internal static MediaSourceTagIndex Empty { get; } = new(null, new Dictionary<string, string[]>(StringComparer.Ordinal));

    /// <summary>The snapshot this index was made from (null for <see cref="Empty"/>).</summary>
    internal BooruCatalogSnapshot? Catalog { get; }

    internal int PostCount => _byPost.Count;

    internal static MediaSourceTagIndex From(BooruCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<string, string[]> byPost = new(catalog.Posts.Count, StringComparer.Ordinal);
        foreach (BooruPostRecord record in catalog.Posts)
        {
            if (record.SourceId != BooruMediaIdentity.Source || byPost.ContainsKey(record.PostId)) continue;
            byPost[record.PostId] = record.Post.Tags.Select(tag => tag.Name?.Trim() ?? string.Empty).Where(MediaFilterTerm.IsSourceTagName)
                .Select(name => name.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        }
        return new MediaSourceTagIndex(catalog, byPost);
    }

    /// <summary>Test seam: an index over fixed post tags.</summary>
    internal static MediaSourceTagIndex Of(IReadOnlyDictionary<string, string[]> posts) =>
        new(null, posts.ToDictionary(pair => pair.Key, pair => pair.Value.Select(MediaFilterTerm.SourceTagValue).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal));

    /// <summary>The source tags of the occurrence (empty for anything that is not a downloaded provider medium, or a post the catalog lacks).</summary>
    internal IReadOnlyList<string> TagsOf(MediaLibraryEntry entry) =>
        entry.BooruPostId is { } post && _byPost.TryGetValue(post, out string[]? tags) ? tags : Array.Empty<string>();
}

/// <summary>Keeps the source tag index of the last catalog snapshot, so a filter change does not read the catalog's tags again. Thread-safe.</summary>
internal sealed class MediaSourceTagIndexCache
{
    private readonly object _sync = new();
    private MediaSourceTagIndex? _last;

    /// <summary>The index of <paramref name="catalog"/>; the same object while the store hands out the same snapshot.</summary>
    internal MediaSourceTagIndex For(BooruCatalogSnapshot? catalog)
    {
        if (catalog == null) return MediaSourceTagIndex.Empty;
        lock (_sync)
        {
            if (_last != null && ReferenceEquals(_last.Catalog, catalog)) return _last;
        }
        MediaSourceTagIndex made = MediaSourceTagIndex.From(catalog);
        lock (_sync) return _last = made;
    }

    /// <summary>Forgets a failed read's derived view before an explicit page retry.</summary>
    internal void Clear()
    {
        lock (_sync) _last = null;
    }
}

/// <summary>Why the page-side terms could not be applied; the page then shows nothing instead of more (like the core filter's refusals).</summary>
internal sealed class MediaPageTermException(string textKey) : Exception(textKey)
{
    /// <summary>The text of the refusal (<c>media.filter.error.queue</c> or <c>media.filter.error.sourceTags</c>).</summary>
    internal string TextKey { get; } = textKey;
}

/// <summary>
/// The terms of the media filter that the page applies after the core filter (<see cref="MediaFilterTerms.IsPageTerm"/>): membership in
/// the tagging queue (MP1) and the provider's source tags of downloaded Booru media (MP4). Pure: the queue keys and the source tags are
/// handed in. Every included source tag must be carried (like a booru search: the tags narrow), no excluded one may be; a medium that is
/// no provider medium carries no source tags. Unreadable data refuses the filter (it never silently shows more).
/// </summary>
internal sealed class MediaPageTermFilter
{
    private readonly bool? _inQueue;
    private readonly string[] _include;
    private readonly string[] _exclude;

    private MediaPageTermFilter(bool? inQueue, string[] include, string[] exclude)
    {
        _inQueue = inQueue;
        _include = include;
        _exclude = exclude;
    }

    /// <summary>The page-side part of the terms; null when there is none (the core filter is all).</summary>
    internal static MediaPageTermFilter? From(IEnumerable<MediaFilterTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        bool? inQueue = null;
        List<string> include = [], exclude = [];
        foreach (MediaFilterTerm term in terms)
            switch (term.Field)
            {
                case MediaFilterField.Queue: inQueue = !term.Exclude; break;
                case MediaFilterField.SourceTag: (term.Exclude ? exclude : include).Add(MediaFilterTerm.SourceTagValue(term.Value)); break;
            }
        return inQueue == null && include.Count == 0 && exclude.Count == 0 ? null
            : new MediaPageTermFilter(inQueue, include.Distinct(StringComparer.Ordinal).ToArray(), exclude.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal bool NeedsQueue => _inQueue != null;
    internal bool NeedsSourceTags => _include.Length > 0 || _exclude.Length > 0;

    /// <summary>
    /// The entries of <paramref name="passing"/> (in order) the page-side terms keep. <paramref name="queue"/> null with a queue term, or
    /// <paramref name="sourceTags"/> null with a source tag term, throws <see cref="MediaPageTermException"/>.
    /// </summary>
    internal IReadOnlyList<MediaLibraryEntry> Apply(IReadOnlyList<MediaLibraryEntry> passing, IReadOnlySet<TagQueueKey>? queue, MediaSourceTagIndex? sourceTags)
    {
        ArgumentNullException.ThrowIfNull(passing);
        if (NeedsQueue && queue == null) throw new MediaPageTermException("media.filter.error.queue");
        if (NeedsSourceTags && sourceTags == null) throw new MediaPageTermException("media.filter.error.sourceTags");
        List<MediaLibraryEntry> kept = new(passing.Count);
        foreach (MediaLibraryEntry entry in passing)
            if (Keeps(entry, queue, sourceTags)) kept.Add(entry);
        return kept;
    }

    private bool Keeps(MediaLibraryEntry entry, IReadOnlySet<TagQueueKey>? queue, MediaSourceTagIndex? sourceTags)
    {
        if (_inQueue is bool wanted && (TagQueueKeys.Of(entry) is { } key && queue!.Contains(key)) != wanted) return false;
        if (!NeedsSourceTags) return true;
        IReadOnlyList<string> carried = sourceTags!.TagsOf(entry);
        foreach (string tag in _exclude)
            if (Carries(carried, tag)) return false;
        foreach (string tag in _include)
            if (!Carries(carried, tag)) return false;
        return true;
    }

    private static bool Carries(IReadOnlyList<string> carried, string tag)
    {
        for (int i = 0; i < carried.Count; i++)
            if (string.Equals(carried[i], tag, StringComparison.Ordinal)) return true;
        return false;
    }
}
