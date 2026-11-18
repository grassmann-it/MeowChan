using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Storage;

namespace MeowChan.Tags;

/// <summary>Where an entry of the tagging queue stands. Only the user changes it; nothing is ever finished automatically.</summary>
internal enum TagQueueState { Open, Done, Skipped }

/// <summary>
/// The exact medium an entry stands for: the same fields the media index and the gallery use to find an occurrence again
/// (thread, media folder, asset, post and attachment position) plus the catalog identity of an imported or downloaded
/// provider medium. Two entries with the same key are the same medium; the queue never holds a key twice.
/// </summary>
internal readonly record struct TagQueueKey(ResourceId Thread, string Directory, string AssetId, string? PostId, int? Position,
    string? ImportItem, string? BooruPost);

/// <summary>
/// One entry of the tagging queue: a frozen description of a saved medium plus its state. It carries no bytes and is never an
/// authority for writing a tag; the host resolves it into a gallery entry when the queue is walked, and a medium whose file is
/// gone then shows as unavailable instead of silently leaving the queue. Everything here is validated on write and on read.
/// </summary>
internal sealed record TagQueueItem(ResourceId Thread, string Directory, string AssetId, string Sha256, long Length, string MediaId,
    string Variant, string? PostId, int? Position, string? ImportItem, string? BooruPost, bool Historical, string? LocalSlot,
    string Name, SavedMediaKind Kind, TagQueueState State, DateTimeOffset AddedAtUtc, DateTimeOffset? DoneAtUtc = null, string? GroupId = null)
{
    internal const int MaximumNameCharacters = 256;
    internal const int MaximumPathCharacters = 1_024;
    internal const int MaximumTokenCharacters = 256;
    internal const int MaximumGroupIdCharacters = 64;

    internal TagQueueKey Key => new(Thread, Directory, AssetId, PostId, Position, ImportItem, BooruPost);

    /// <summary>The entry of a media index entry, or null when that entry has no saved file to walk (not saved, or no asset).</summary>
    internal static TagQueueItem? From(MediaLibraryEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Presence != MediaLibraryPresence.Present || entry.Directory == null || entry.AssetId == null || entry.Sha256 == null
            || entry.Length == null || entry.MediaId == null) return null;
        TagQueueItem item = new(entry.Thread, entry.Directory, entry.AssetId, entry.Sha256.ToLowerInvariant(), entry.Length.Value,
            entry.MediaId, entry.Variant, entry.PostId, entry.Position, entry.ImportItemId, entry.BooruPostId, entry.Historical,
            entry.LocalSlot, CleanName(entry.Name), entry.Kind, TagQueueState.Open, now.ToUniversalTime());
        return item.IsValid() ? item : null;
    }

    /// <summary>The entry of a resolved gallery entry (the gallery's own "add to queue"), or null when it has no saved file.</summary>
    internal static TagQueueItem? From(GalleryEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        MediaAssetReference asset = entry.Asset;
        if (string.IsNullOrEmpty(asset.Sha256) || string.IsNullOrEmpty(entry.Manifest.Directory)) return null;
        string? import = ManualImportIdentity.IsImportThread(entry.Key.Thread) ? asset.MediaIdentifier : null;
        string? booru = BooruMediaIdentity.TryGetPostId(entry.Key.Thread, out string postId) ? postId : null;
        TagQueueItem item = new(entry.Key.Thread, entry.Manifest.Directory, asset.AssetId, asset.Sha256.ToLowerInvariant(), asset.Length,
            asset.MediaIdentifier, asset.Variant, entry.Post?.PostId, entry.Attachment?.Position, import, booru, !asset.IsCurrent,
            entry.LocalTagEligible ? asset.RequestedFileName : null, CleanName(asset.OriginalFileName ?? System.IO.Path.GetFileName(asset.Path)),
            entry.Kind, TagQueueState.Open, now.ToUniversalTime());
        return item.IsValid() ? item : null;
    }

    /// <summary>
    /// The media index entry this item stands for, as the gallery resolver expects it: a present entry with the identity fields.
    /// The thread title, revision and capture times are not part of the queue; the resolver does not read them.
    /// </summary>
    internal MediaLibraryEntry ToLibraryEntry() => new(Thread, string.Empty, 0, AddedAtUtc, null, PostId, null, null, Position, null,
        Directory, AssetId, Sha256, Length, MediaId, Variant, Name.Length == 0 ? null : Name, Kind, MediaLibraryPresence.Present,
        Unassigned: PostId == null, Historical: Historical, LocalSlot: LocalSlot)
    { ImportItemId = ImportItem, BooruPostId = BooruPost };

    /// <summary>Whether every field is within what the file format accepts. A false answer on write is a bug in the caller.</summary>
    internal bool IsValid() => Validate() == null;

    /// <summary>The first problem of this entry, or null. The text never contains the offending value.</summary>
    internal string? Validate()
    {
        static bool Token(string? value, int maximum, bool allowNull) => value == null ? allowNull
            : value.Length > 0 && value.Length <= maximum && !value.Any(char.IsControl);
        if (Thread == null || !Token(Thread.Source, MaximumTokenCharacters, false) || !Token(Thread.Board, MaximumTokenCharacters, false)
            || !Token(Thread.Thread, MaximumTokenCharacters, false)) return "thread";
        if (!Token(Directory, MaximumPathCharacters, false)) return "directory";
        if (!Token(AssetId, MaximumTokenCharacters, false)) return "asset";
        if (Sha256?.Length != 64 || !Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return "sha256";
        if (Length < 0) return "length";
        if (!Token(MediaId, MaximumTokenCharacters, false) || !Token(Variant, MaximumTokenCharacters, false)) return "media";
        if (!Token(PostId, MaximumTokenCharacters, true) || Position is < 0) return "post";
        if (ImportItem != null && !ManualImportIdentity.TryParseId(ImportItem, out _)) return "import";
        if (!Token(BooruPost, MaximumTokenCharacters, true) || ImportItem != null && BooruPost != null) return "provider";
        if (!Token(LocalSlot, MaximumTokenCharacters, true)) return "slot";
        if (Name == null || Name.Length > MaximumNameCharacters || Name.Any(char.IsControl)) return "name";
        if (GroupId != null && (GroupId.Length == 0 || GroupId.Length > MaximumGroupIdCharacters || GroupId.Any(char.IsControl))) return "group";
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(State)) return "enum";
        if (AddedAtUtc.Offset != TimeSpan.Zero || DoneAtUtc is { } done && done.Offset != TimeSpan.Zero) return "time";
        if ((State == TagQueueState.Done) != (DoneAtUtc != null)) return "done";
        return null;
    }

    /// <summary>A name that fits the file format: control characters dropped, cut at the limit without splitting a surrogate pair.</summary>
    internal static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        StringBuilder text = new(Math.Min(name.Length, MaximumNameCharacters));
        foreach (char c in name)
        {
            if (char.IsControl(c)) continue;
            if (text.Length == MaximumNameCharacters) break;
            text.Append(c);
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return text.ToString();
    }
}

/// <summary>
/// A new walk position given together with a state change: an absolute position (<see cref="At"/>) or a position relative to an entry
/// (<see cref="Relative"/>, 1 = the entry after it), resolved after finished entries beyond the history limit dropped out.
/// </summary>
internal readonly record struct TagQueueCursorTarget(TagQueueKey? Key, int Offset)
{
    internal static TagQueueCursorTarget At(int position) => new(null, position);
    internal static TagQueueCursorTarget Relative(TagQueueKey key, int offset) => new(key, offset);
}

/// <summary>A named part of the tagging queue. The order in <see cref="TagQueueSnapshot.Groups"/> is the user-visible order.</summary>
internal sealed record TagQueueGroup(string Id, string Name, int Order, IReadOnlyList<string> Stamp)
{
    internal TagQueueGroup(string id, string name, IReadOnlyList<string> stamp) : this(id, name, 0, stamp) { }

    internal const int MaximumIdCharacters = 64;
    internal const int MaximumNameCharacters = 256;

    internal bool IsValid() => !string.IsNullOrEmpty(Id) && Id.Length <= MaximumIdCharacters && !Id.Any(char.IsControl)
        && Name != null && Name.Length > 0 && Name.Length <= MaximumNameCharacters && !Name.Any(char.IsControl)
        && Order >= 0 && Stamp != null;
}

/// <summary>An immutable copy of the queue as read from, or written to, its file.</summary>
internal sealed class TagQueueSnapshot
{
    /// <summary>The ceiling of the stamp set; the user limit (<c>TagStampSize</c>) lies below it.</summary>
    internal const int MaximumStamp = 18;
    internal const int MaximumGroups = Persistence.ProfileStoreLimits.TagQueueGroupCeiling;

    internal static TagQueueSnapshot Empty { get; } = new(0, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>(), TagQueueLoadState.Ready);

    private Dictionary<TagQueueKey, int>? _index;

    internal TagQueueSnapshot(long revision, int cursor, IReadOnlyList<string> stamp, IReadOnlyList<TagQueueItem> items, TagQueueLoadState state)
        : this(revision, cursor, stamp, items, state, Array.Empty<TagQueueGroup>()) { }

    internal TagQueueSnapshot(long revision, int cursor, IReadOnlyList<string> stamp, IReadOnlyList<TagQueueItem> items, TagQueueLoadState state, IReadOnlyList<TagQueueGroup>? groups)
    {
        Revision = revision;
        Items = items;
        Cursor = Math.Clamp(cursor, 0, items.Count);
        Stamp = stamp;
        Groups = groups ?? Array.Empty<TagQueueGroup>();
        State = state;
        int open = 0, done = 0, skipped = 0;
        foreach (TagQueueItem item in items)
            switch (item.State) { case TagQueueState.Open: open++; break; case TagQueueState.Done: done++; break; default: skipped++; break; }
        Open = open; Done = done; Skipped = skipped;
    }

    internal long Revision { get; }
    /// <summary>The position the walk stands at (0 to <see cref="Count"/>); it is where "continue" starts.</summary>
    internal int Cursor { get; }
    internal IReadOnlyList<string> Stamp { get; }
    internal IReadOnlyList<TagQueueGroup> Groups { get; }
    internal IReadOnlyList<TagQueueItem> Items { get; }
    internal TagQueueGroup? Group(string id) => Groups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
    internal int CountFor(string? groupId) => Items.Count(item => item.GroupId == groupId);
    internal int OpenFor(string? groupId) => Items.Count(item => item.GroupId == groupId && item.State == TagQueueState.Open);
    internal int DoneFor(string? groupId) => Items.Count(item => item.GroupId == groupId && item.State == TagQueueState.Done);
    internal int SkippedFor(string? groupId) => Items.Count(item => item.GroupId == groupId && item.State == TagQueueState.Skipped);
    internal TagQueueLoadState State { get; }
    internal int Count => Items.Count;
    internal int Open { get; }
    internal int Done { get; }
    internal int Skipped { get; }
    /// <summary>False when the file is of a newer version: it is shown as empty, kept as it is and never written.</summary>
    internal bool CanWrite => State == TagQueueLoadState.Ready;

    internal bool Contains(TagQueueKey key) => IndexOf(key) >= 0;

    internal int IndexOf(TagQueueKey key)
    {
        _index ??= BuildIndex();
        return _index.TryGetValue(key, out int index) ? index : -1;
    }

    private Dictionary<TagQueueKey, int> BuildIndex()
    {
        Dictionary<TagQueueKey, int> index = new(Items.Count);
        for (int i = 0; i < Items.Count; i++) index[Items[i].Key] = i;
        return index;
    }

    /// <summary>
    /// Where "continue" starts: the cursor when it stands on an open entry, otherwise the next open entry after it, otherwise the first
    /// open entry before it. Null when nothing is open. Skipped and finished entries are never revisited by it.
    /// </summary>
    internal int? ResumeIndex() => ResumeIndex(groupId: null, includeUngrouped: false);

    /// <summary>Where a group or ungrouped walk starts, using the global cursor but only entries in the selected scope.</summary>
    internal int? ResumeIndex(string? groupId, bool includeUngrouped = false)
    {
        bool InScope(TagQueueItem item) => groupId != null ? item.GroupId == groupId : includeUngrouped ? item.GroupId == null : true;
        for (int i = Cursor; i < Items.Count; i++)
            if (InScope(Items[i]) && Items[i].State == TagQueueState.Open) return i;
        for (int i = 0; i < Math.Min(Cursor, Items.Count); i++)
            if (InScope(Items[i]) && Items[i].State == TagQueueState.Open) return i;
        return null;
    }


}

internal enum TagQueueLoadState { Ready, NewerVersion }

/// <summary>What an add did. Nothing is refused silently: every input entry is counted exactly once.</summary>
internal sealed record TagQueueAddResult(TagQueueSnapshot Snapshot, int Added, int AlreadyPresent, int OverLimit)
{
    internal bool Changed => Added > 0;
}

/// <summary>The tagging queue file is of a newer version than this build reads: it stays as it is and is not written.</summary>
internal sealed class TagQueueNewerVersionException : InvalidOperationException
{
    internal TagQueueNewerVersionException() : base("The tagging queue file was written by a newer version and is read-only.") { }
}

/// <summary>
/// The tagging queue file (or its backup) exists but could not be read (locked, access denied, an I/O error). The host tells it from a
/// refused write by this type, never by its message.
/// </summary>
internal sealed class TagQueueReadException : IOException
{
    internal TagQueueReadException(Exception inner) : base("The tagging queue could not be read.", inner) { }
}
