using System;
using System.Collections.Generic;
using MeowChan.Gallery;

namespace MeowChan.Tags;

/// <summary>
/// Resolves the attachment revision key omitted by queue versions 1 and 2 from the media index, using a present occurrence with the same thread, folder, asset, post, attachment position and bytes.
/// Nothing is guessed: no match, or two index entries with different keys for the same occurrence, leaves the entry as it is (its
/// media tags stay unknown). The resolver reads the index only; it never writes the queue.
/// </summary>
internal sealed class TagQueueKeyResolver
{
    private readonly Dictionary<Occurrence, string?> _keys = new();

    private readonly record struct Occurrence(ResourceId Thread, string Directory, string AssetId, string PostId, int Position, string Sha256);

    internal TagQueueKeyResolver(IEnumerable<MediaLibraryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (MediaLibraryEntry entry in entries)
        {
            if (entry.Presence != MediaLibraryPresence.Present || entry.AttachmentRevisionKey == null || entry.PostId == null
                || entry.Position is not int position || entry.Directory == null || entry.AssetId == null || entry.Sha256 == null) continue;
            Occurrence occurrence = new(entry.Thread, entry.Directory, entry.AssetId, entry.PostId, position, entry.Sha256.ToLowerInvariant());
            // A second, different key makes the occurrence ambiguous (null): it is never resolved by chance.
            _keys[occurrence] = _keys.TryGetValue(occurrence, out string? known) && known != entry.AttachmentRevisionKey ? null : entry.AttachmentRevisionKey;
        }
    }

    /// <summary>The entry with its revision key filled in from the index, or the entry itself when it has one, needs none or none is found.</summary>
    internal TagQueueItem Resolve(TagQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AttachmentRevisionKey != null || item.PostId == null || item.Position is not int position) return item;
        return _keys.TryGetValue(new Occurrence(item.Thread, item.Directory, item.AssetId, item.PostId, position, item.Sha256), out string? key) && key != null
            ? item with { AttachmentRevisionKey = key } : item;
    }
}
