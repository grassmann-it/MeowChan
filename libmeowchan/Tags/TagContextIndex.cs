using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Tags;

/// <summary>
/// Read-only context lookups over one published tag snapshot, built in a single pass over the assignments and cached
/// per snapshot (a tag change publishes a new snapshot, so nothing stale is served; the cache entry dies with it).
/// It answers, without scanning: which tags a thread carries, which tags other files of a thread carry (with the number
/// of distinct files), which tags files with the same bytes carry, which tags exactly one media subject carries, and
/// which own tag a normalized name belongs to. Immutable after construction, so any thread may use it. No UI, no IO,
/// nothing is assigned or created here.
/// </summary>
internal sealed class TagContextIndex
{
    private static readonly ConditionalWeakTable<PrivateTagStoreSnapshot, TagContextIndex> Cache = new();
    private static readonly IReadOnlyList<TagId> NoTags = Array.Empty<TagId>();
    private static readonly IReadOnlyDictionary<TagId, int> NoCounts = new Dictionary<TagId, int>();

    private readonly Dictionary<ResourceId, TagId[]> _threadTags;
    private readonly Dictionary<ResourceId, Dictionary<TagId, int>> _threadMedia;
    private readonly Dictionary<string, TagId[]> _byHash;
    private readonly Dictionary<PrivateMediaTagTarget, TagId[]> _bound;
    private readonly Dictionary<LocalMediaTagTarget, TagId[]> _local;
    private readonly Dictionary<TagId, PrivateTagDefinition> _byId;
    private readonly LocalMediaRegistry _registry;

    private TagContextIndex(PrivateTagStoreSnapshot snapshot, CancellationToken cancellation)
    {
        _registry = snapshot.LocalRegistry;
        _byId = new Dictionary<TagId, PrivateTagDefinition>(snapshot.Tags.Count);
        foreach (PrivateTagDefinition tag in snapshot.Tags) _byId[tag.Id] = tag;
        Names = new TagNameLookup(snapshot.Tags.Select(tag => new TagNameEntry(tag.Name.Key, tag.Name, tag.Id, 0)));

        Dictionary<ResourceId, List<TagId>> threadTags = new();
        foreach (PrivateThreadTagAssignment item in snapshot.ThreadAssignments)
        {
            if (!threadTags.TryGetValue(item.Thread, out List<TagId>? list)) threadTags[item.Thread] = list = new List<TagId>();
            TagId tag = item.TagId;
            if (!list.Contains(tag)) list.Add(tag);
        }
        _threadTags = threadTags.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        _threadMedia = new Dictionary<ResourceId, Dictionary<TagId, int>>();
        Dictionary<string, List<TagId>> byHash = new(StringComparer.Ordinal);
        Dictionary<PrivateMediaTagTarget, List<TagId>> bound = new();
        // One count per distinct file and tag: a handed-over revision key of the same file does not count twice.
        HashSet<(TagId, MediaIdentity)> seenBound = new();
        int step = 0;
        foreach (PrivateMediaTagAssignment item in snapshot.MediaAssignments)
        {
            if ((++step & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            TagId tag = item.TagId;
            PrivateMediaTagTarget target = item.Target;
            Add(bound, target, tag);
            Add(byHash, target.Sha256, tag);
            if (seenBound.Add((tag, target.Identity))) Count(target.Reference.Resource, tag);
        }
        _bound = Freeze(bound);

        Dictionary<LocalMediaTagTarget, List<TagId>> local = new();
        // Like bound files: one count per file (occurrence) and tag, however many of its revisions carry the tag.
        HashSet<(TagId, LocalMediaOccurrenceId)> seenLocal = new();
        foreach (PrivateLocalMediaTagAssignment item in snapshot.LocalAssignments)
        {
            if ((++step & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            Add(local, item.Target, item.TagId);
            LocalMediaOccurrence? occurrence = _registry.Find(item.Target.Occurrence);
            if (occurrence == null) continue;
            if (occurrence.Find(item.Target.Revision) is { } revision) Add(byHash, revision.Sha256, item.TagId);
            if (seenLocal.Add((item.TagId, item.Target.Occurrence))) Count(occurrence.Locator.Thread, item.TagId);
        }
        _local = Freeze(local);
        _byHash = Freeze(byHash);
        ApproximateEntries = _threadTags.Values.Sum(items => items.Length) + _threadMedia.Values.Sum(items => items.Count)
            + _byHash.Values.Sum(items => items.Length) + _bound.Count + _local.Count + _byId.Count;
    }

    /// <summary>Own tags by normalized name, for exact and prefix lookups of a name key.</summary>
    internal TagNameLookup Names { get; }

    /// <summary>Total entries of all lookups; a rough size for tests and budgets, not a memory measurement.</summary>
    internal int ApproximateEntries { get; }

    /// <summary>True when the index of this snapshot is already built, so <see cref="For"/> returns at once (a UI thread asks before it would build).</summary>
    internal static bool IsBuilt(PrivateTagStoreSnapshot snapshot) => Cache.TryGetValue(snapshot, out _);

    /// <summary>The index of a snapshot, built once. The build is O(assignments) and may run on any thread.</summary>
    internal static TagContextIndex For(PrivateTagStoreSnapshot snapshot, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Cache.TryGetValue(snapshot, out TagContextIndex? known)) return known;
        // A cancelled build is not cached; a concurrent duplicate build is harmless (pure function of the snapshot).
        return Cache.GetValue(snapshot, current => new TagContextIndex(current, cancellation));
    }

    internal PrivateTagDefinition? Find(TagId id) => _byId.GetValueOrDefault(id);

    /// <summary>The own tag with this comparison key (<see cref="PrivateTagName.Key"/>), if any.</summary>
    internal PrivateTagDefinition? FindByKey(string key) =>
        Names.TryExact(key, out TagNameEntry entry) && entry.Id is { } id ? _byId.GetValueOrDefault(id) : null;

    /// <summary>Tags of the thread itself (not of its media). Empty for null or unknown.</summary>
    internal IReadOnlyList<TagId> ThreadTags(ResourceId? thread) =>
        thread != null && _threadTags.TryGetValue(thread, out TagId[]? tags) ? tags : NoTags;

    /// <summary>Tags carried by media of the thread with the number of distinct files carrying each.</summary>
    internal IReadOnlyDictionary<TagId, int> ThreadMediaTags(ResourceId? thread) =>
        thread != null && _threadMedia.TryGetValue(thread, out Dictionary<TagId, int>? tags) ? tags : NoCounts;

    /// <summary>Tags carried by any file recorded with these exact bytes (SHA-256, hex, either case). Empty if none or malformed.</summary>
    internal IReadOnlyList<TagId> TagsOfHash(string? sha256)
    {
        if (sha256 is not { Length: 64 }) return NoTags;
        string key = sha256.ToLowerInvariant();
        return _byHash.TryGetValue(key, out TagId[]? tags) ? tags : NoTags;
    }

    /// <summary>
    /// Tags of exactly this subject, by the same rules as <see cref="PrivateTagStoreSnapshot.TagsOf(MediaTagSubject)"/>
    /// but without scanning the assignments. Null or unregistered local subjects carry nothing.
    /// </summary>
    internal IReadOnlyList<TagId> TagsOf(MediaTagSubject? subject)
    {
        switch (subject)
        {
            case MediaTagSubject.Bound bound:
                return _bound.TryGetValue(bound.Target, out TagId[]? ids) ? ids : NoTags;
            case MediaTagSubject.Local { Expected: { } expected }:
                return _local.TryGetValue(expected, out TagId[]? expectedIds) ? expectedIds : NoTags;
            case MediaTagSubject.Local local:
                return _registry.Resolve(local.Observation) is { IsRegistered: true, Target: { } target }
                    && _local.TryGetValue(target, out TagId[]? resolved) ? resolved : NoTags;
            default:
                return NoTags;
        }
    }

    private void Count(ResourceId thread, TagId tag)
    {
        if (!_threadMedia.TryGetValue(thread, out Dictionary<TagId, int>? counts)) _threadMedia[thread] = counts = new Dictionary<TagId, int>();
        counts[tag] = counts.GetValueOrDefault(tag) + 1;
    }

    private static void Add<TKey>(Dictionary<TKey, List<TagId>> map, TKey key, TagId tag) where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TagId>? list)) map[key] = list = new List<TagId>(2);
        if (!list.Contains(tag)) list.Add(tag);
    }

    private static Dictionary<TKey, TagId[]> Freeze<TKey>(Dictionary<TKey, List<TagId>> map) where TKey : notnull
    {
        Dictionary<TKey, TagId[]> frozen = new(map.Count, map.Comparer);
        foreach ((TKey key, List<TagId> list) in map) frozen[key] = list.ToArray();
        return frozen;
    }
}

/// <summary>One name of a <see cref="TagNameLookup"/>: comparison key, name, own tag id (null for a provider source tag) and a weight.</summary>
internal readonly record struct TagNameEntry(string Key, PrivateTagName Name, TagId? Id, int Weight);

/// <summary>
/// Immutable sorted name lookup: an exact lookup by comparison key and a bounded prefix scan, both without touching
/// unrelated names. Built once from a name list; the same key keeps its first entry.
/// </summary>
internal sealed class TagNameLookup
{
    private readonly TagNameEntry[] _sorted;
    private readonly Dictionary<string, int> _position;

    internal TagNameLookup(IEnumerable<TagNameEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<string, TagNameEntry> unique = new(StringComparer.Ordinal);
        foreach (TagNameEntry entry in entries) unique.TryAdd(entry.Key, entry);
        _sorted = unique.Values.ToArray();
        Array.Sort(_sorted, static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        _position = new Dictionary<string, int>(_sorted.Length, StringComparer.Ordinal);
        for (int index = 0; index < _sorted.Length; index++) _position[_sorted[index].Key] = index;
    }

    internal int Count => _sorted.Length;

    internal bool TryExact(string key, out TagNameEntry entry)
    {
        if (_position.TryGetValue(key, out int index)) { entry = _sorted[index]; return true; }
        entry = default;
        return false;
    }

    /// <summary>
    /// Names that start with <paramref name="prefix"/> but are longer than it, in key order, looking at no more than
    /// <paramref name="maxScan"/> of them.
    /// </summary>
    internal void Prefix(string prefix, int maxScan, List<TagNameEntry> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (prefix.Length == 0 || maxScan <= 0) return;
        int low = 0, high = _sorted.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (string.CompareOrdinal(_sorted[middle].Key, prefix) < 0) low = middle + 1; else high = middle;
        }
        int scanned = 0;
        for (int index = low; index < _sorted.Length && scanned < maxScan; index++)
        {
            string key = _sorted[index].Key;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) break;
            if (key.Length == prefix.Length) continue;
            into.Add(_sorted[index]);
            scanned++;
        }
    }
}
