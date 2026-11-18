using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tags;

/// <summary>
/// The profile's tagging queue (<c>tag-queue.json</c>, version 2): the media still to be tagged, their state, the position of the
/// walk and the stamp set. Strict and atomic like the other private lists: an unknown property, a repeated property, a malformed
/// identity, a damaged file or a link in place of the file is refused, never repaired; a file of a newer version is read as empty
/// and kept untouched. Every write replaces the file atomically and rotates the last good copy into <c>.bak</c>. Limits (entries,
/// file size, kept finished entries) are the settings behind <see cref="ProfileStoreLimits"/>; lowering one never makes a saved
/// file unreadable, it only stops growth. The store never resolves, opens or writes media.
/// </summary>
internal sealed class TagQueueStore
{
    internal const int Version = 2;
    internal const int LegacyVersion = 1;
    internal const string FileName = "tag-queue.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProfileLease _lease;
    private readonly string _path;
    // W3-24: the bytes last read or written for the file and for its backup, with what they parse to. A read still loads the bytes and
    // compares them in full, so a file changed by anything else is parsed (and refused) as before; only an unchanged file skips the
    // parse. Accessed only inside the lease's exclusion.
    private byte[]? _primaryBytes, _backupBytes;
    private TagQueueSnapshot? _primarySnapshot;
    private int _parses;

    internal TagQueueStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    /// <summary>Reads the file. A missing file is an empty queue; anything unreadable throws <see cref="InvalidDataException"/>.</summary>
    internal TagQueueSnapshot Read() => _lease.RunExclusive(ReadCore);

    /// <summary>How often this store parsed a file in full (tests: an unchanged file is not parsed again).</summary>
    internal int ParsesForTests => _lease.RunExclusive(() => _parses);

    /// <summary>
    /// Appends the entries that are valid, not yet in the queue and fit under the entry limit, in the given order. Duplicates (also
    /// within <paramref name="items"/>) and entries over the limit are counted, not added. Entries are always added as open.
    /// </summary>
    internal TagQueueAddResult Add(IReadOnlyList<TagQueueItem> items, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            List<TagQueueItem> list = current.Items.ToList();
            HashSet<TagQueueKey> known = list.Select(item => item.Key).ToHashSet();
            int limit = _lease.StoreLimits.TagQueueEntries, added = 0, duplicates = 0, over = 0;
            HashSet<string> groupIds = current.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            foreach (TagQueueItem item in items)
            {
                if (item.Validate() is { } problem) throw new ArgumentException("The tagging queue entry is invalid: " + problem + ".", nameof(items));
                if (item.GroupId != null && !groupIds.Contains(item.GroupId))
                    throw new ArgumentException("The tagging queue entry references a missing group.", nameof(items));
                if (known.Contains(item.Key)) { duplicates++; continue; }
                // Only growth is refused: with the limit lowered below the saved count nothing more is added, nothing is removed.
                if (list.Count >= limit) { over++; continue; }
                list.Add(item with { State = TagQueueState.Open, DoneAtUtc = null });
                known.Add(item.Key);
                added++;
            }
            if (added == 0) return new TagQueueAddResult(current, 0, duplicates, over);
            return new TagQueueAddResult(Commit(current, list, current.Cursor, current.Stamp, token), added, duplicates, over);
        });
    }

    /// <summary>Removes the entries with these keys (unknown keys are ignored). The cursor keeps standing on the same entry.</summary>
    internal TagQueueSnapshot Remove(IReadOnlyCollection<TagQueueKey> keys, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        HashSet<TagQueueKey> remove = keys.ToHashSet();
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            if (!current.Items.Any(item => remove.Contains(item.Key))) return current;
            List<TagQueueItem> list = new(current.Items.Count);
            int cursor = current.Cursor;
            for (int i = 0; i < current.Items.Count; i++)
            {
                if (remove.Contains(current.Items[i].Key)) { if (i < current.Cursor) cursor--; }
                else list.Add(current.Items[i]);
            }
            return Commit(current, list, cursor, current.Stamp, token);
        });
    }

    /// <summary>
    /// Sets the state of entries; <paramref name="now"/> stamps entries that become finished. An unknown key or an unchanged state is
    /// ignored. Finished entries beyond the history limit drop out, oldest first. The cursor keeps standing on the same entry unless
    /// <paramref name="cursor"/> names a new position, which is applied in the same write (a decision and its step on are one change).
    /// </summary>
    internal TagQueueSnapshot SetStates(IReadOnlyList<KeyValuePair<TagQueueKey, TagQueueState>> changes, DateTimeOffset now,
        TagQueueCursorTarget? cursor = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (KeyValuePair<TagQueueKey, TagQueueState> change in changes)
            if (!Enum.IsDefined(change.Value)) throw new ArgumentOutOfRangeException(nameof(changes), "Unknown state.");
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            List<TagQueueItem> list = current.Items.ToList();
            bool changed = false;
            foreach ((TagQueueKey key, TagQueueState state) in changes)
            {
                int index = current.IndexOf(key);
                if (index < 0 || list[index].State == state) continue;
                list[index] = list[index] with { State = state, DoneAtUtc = state == TagQueueState.Done ? now.ToUniversalTime() : null };
                changed = true;
            }
            HashSet<int> dropped = TrimHistory(list);
            int Mapped(int index) => index - dropped.Count(drop => drop < index);
            int newCursor = Mapped(current.Cursor);
            if (cursor is { } target)
            {
                if (target.Key is { } anchor)
                {
                    int at = current.IndexOf(anchor);
                    newCursor = at < 0 ? newCursor : Mapped(at) + (dropped.Contains(at) ? 0 : target.Offset);
                }
                else newCursor = target.Offset;
            }
            if (!changed && dropped.Count == 0 && Math.Clamp(newCursor, 0, list.Count) == current.Cursor) return current;
            return Commit(current, KeepNotDropped(list, dropped), newCursor, current.Stamp, token);
        });
    }

    /// <summary>Moves the walk position (0 to the entry count; larger values are refused).</summary>
    internal TagQueueSnapshot SetCursor(int cursor, CancellationToken token = default) => _lease.RunExclusive(() =>
    {
        token.ThrowIfCancellationRequested();
        TagQueueSnapshot current = ReadWritable();
        if (cursor < 0 || cursor > current.Count) throw new ArgumentOutOfRangeException(nameof(cursor), "The cursor is outside the queue.");
        return cursor == current.Cursor ? current : Commit(current, current.Items.ToList(), cursor, current.Stamp, token);
    });

    /// <summary>Moves one entry so that it stands at <paramref name="index"/>; the cursor keeps standing on the same entry.</summary>
    internal TagQueueSnapshot Move(TagQueueKey key, int index, CancellationToken token = default) => _lease.RunExclusive(() =>
    {
        token.ThrowIfCancellationRequested();
        TagQueueSnapshot current = ReadWritable();
        int from = current.IndexOf(key);
        if (from < 0) throw new InvalidOperationException("The entry is no longer in the tagging queue.");
        if (index < 0 || index >= current.Count) throw new ArgumentOutOfRangeException(nameof(index), "The position is outside the queue.");
        if (index == from) return current;
        TagQueueKey? cursorKey = current.Cursor < current.Count ? current.Items[current.Cursor].Key : null;
        List<TagQueueItem> list = current.Items.ToList();
        TagQueueItem moved = list[from];
        list.RemoveAt(from);
        list.Insert(index, moved);
        int cursor = cursorKey is { } stands ? list.FindIndex(item => item.Key == stands) : list.Count;
        return Commit(current, list, cursor, current.Stamp, token);
    });

    /// <summary>
    /// Removes every entry (<paramref name="finishedOnly"/> false) or only the finished ones. On an unreadable file the damaged file is
    /// preserved next to it and a fresh empty queue is written, so a damaged queue can always be cleared; a file of a newer version is
    /// refused.
    /// </summary>
    internal TagQueueSnapshot Clear(bool finishedOnly, CancellationToken token = default) => _lease.RunExclusive(() =>
    {
        token.ThrowIfCancellationRequested();
        TagQueueSnapshot current;
        try { current = ReadWritable(); }
        catch (InvalidDataException) when (!finishedOnly)
        {
            // The recovery save never rotates damaged bytes into the backup: WriteAllText without a backup preserves them next to the
            // file (one .corrupt-* copy), then the empty queue replaces them.
            RejectLink(_path);
            string empty = Serialize(Version, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>(), Array.Empty<TagQueueGroup>());
            _primaryBytes = null;
            _primarySnapshot = null;
            AtomicJsonFile.WriteAllText(_path, empty, backupCurrentFile: false, token);
            return new TagQueueSnapshot(1, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>(), TagQueueLoadState.Ready, Array.Empty<TagQueueGroup>());
        }
        if (current.Count == 0) return current;
        List<TagQueueItem> list = finishedOnly ? current.Items.Where(item => item.State != TagQueueState.Done).ToList() : new();
        if (list.Count == current.Count) return current;
        int cursor = finishedOnly ? current.Items.Take(current.Cursor).Count(item => item.State != TagQueueState.Done) : 0;
        return Commit(current, list, cursor, current.Stamp, token);
    });

    /// <summary>Replaces the general (all/ungrouped) stamp set.</summary>
    internal TagQueueSnapshot SetStamp(IReadOnlyList<string> names, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] normalized = NormalizeStamp(names);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            return normalized.SequenceEqual(current.Stamp, StringComparer.Ordinal) ? current
                : Commit(current, current.Items.ToList(), current.Cursor, normalized, token);
        });
    }

    /// <summary>Creates a group and assigns the supplied entries to it. The group starts with an empty private stamp.</summary>
    internal TagQueueSnapshot CreateGroup(string name, IReadOnlyCollection<TagQueueKey>? keys = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        IReadOnlyCollection<TagQueueKey> selectedKeys = keys ?? Array.Empty<TagQueueKey>();
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            string normalized = NormalizeGroupName(name, _lease.StoreLimits.TagQueueGroupNameCharacters);
            if (current.Groups.Count >= _lease.StoreLimits.TagQueueGroups)
                throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.TagQueueGroups, _lease.StoreLimits.TagQueueGroups,
                    "The tagging queue group limit is reached.");
            if (current.Groups.Any(group => string.Equals(group.Name, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A tagging group with that name already exists.", nameof(name));
            TagQueueGroup group = new(Guid.NewGuid().ToString("N"), normalized, current.Groups.Count, Array.Empty<string>());
            HashSet<TagQueueKey> selected = selectedKeys.ToHashSet();
            List<TagQueueItem> items = current.Items.Select(item => selected.Contains(item.Key) ? item with { GroupId = group.Id } : item).ToList();
            return Commit(current, items, current.Cursor, current.Stamp, token, current.Groups.Append(group).ToArray());
        });
    }

    /// <summary>Renames a group. A lowered name limit never makes an existing group unreadable.</summary>
    internal TagQueueSnapshot RenameGroup(string groupId, string name, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        ArgumentNullException.ThrowIfNull(name);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            int index = GroupIndex(current, groupId);
            string normalized = NormalizeGroupName(name, _lease.StoreLimits.TagQueueGroupNameCharacters);
            if (current.Groups.Where((_, i) => i != index).Any(group => string.Equals(group.Name, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A tagging group with that name already exists.", nameof(name));
            List<TagQueueGroup> groups = current.Groups.ToList();
            groups[index] = groups[index] with { Name = normalized };
            return Commit(current, current.Items.ToList(), current.Cursor, current.Stamp, token, groups);
        });
    }

    /// <summary>Deletes a group and leaves its entries in the queue without a group.</summary>
    internal TagQueueSnapshot DeleteGroup(string groupId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            int index = GroupIndex(current, groupId);
            List<TagQueueGroup> groups = current.Groups.Where((_, i) => i != index)
                .Select((group, order) => group with { Order = order }).ToList();
            List<TagQueueItem> items = current.Items.Select(item => item.GroupId == groupId ? item with { GroupId = null } : item).ToList();
            return Commit(current, items, current.Cursor, current.Stamp, token, groups);
        });
    }

    /// <summary>Assigns entries to one existing group, or null to remove their group.</summary>
    internal TagQueueSnapshot AssignGroup(IReadOnlyCollection<TagQueueKey> keys, string? groupId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            if (groupId != null) _ = GroupIndex(current, groupId);
            HashSet<TagQueueKey> selected = keys.ToHashSet();
            List<TagQueueItem> items = current.Items.Select(item => selected.Contains(item.Key) ? item with { GroupId = groupId } : item).ToList();
            if (items.SequenceEqual(current.Items)) return current;
            return Commit(current, items, current.Cursor, current.Stamp, token, current.Groups);
        });
    }

    /// <summary>Replaces a group's private stamp set.</summary>
    internal TagQueueSnapshot SetGroupStamp(string groupId, IReadOnlyList<string> names, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        ArgumentNullException.ThrowIfNull(names);
        string[] normalized = NormalizeStamp(names);
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            TagQueueSnapshot current = ReadWritable();
            int index = GroupIndex(current, groupId);
            if (normalized.SequenceEqual(current.Groups[index].Stamp, StringComparer.Ordinal)) return current;
            List<TagQueueGroup> groups = current.Groups.ToList();
            groups[index] = groups[index] with { Stamp = normalized };
            return Commit(current, current.Items.ToList(), current.Cursor, current.Stamp, token, groups);
        });
    }

    private static int GroupIndex(TagQueueSnapshot snapshot, string groupId)
    {
        int index = snapshot.Groups.ToList().FindIndex(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));
        return index >= 0 ? index : throw new ArgumentException("The tagging group does not exist.", nameof(groupId));
    }

    private static string NormalizeGroupName(string name, int configuredLimit)
    {
        string normalized = name.Trim();
        if (normalized.Length == 0 || normalized.Length > TagQueueGroup.MaximumNameCharacters || normalized.Any(char.IsControl))
            throw new ArgumentException("The tagging group name is invalid.", nameof(name));
        if (normalized.Length > configuredLimit)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.TagQueueGroupName, configuredLimit, "The tagging group name is too long.");
        return normalized;
    }

    private static string[] NormalizeStamp(IReadOnlyList<string> names)
    {
        if (names.Count > TagQueueSnapshot.MaximumStamp) throw new ArgumentException("The stamp set is too large.", nameof(names));
        List<string> result = new(names.Count);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!PrivateTagName.TryCreate(name, out PrivateTagName? tag, out _) || tag == null || !keys.Add(tag.Key))
                throw new ArgumentException("The stamp set holds an invalid or repeated tag name.", nameof(names));
            result.Add(tag.Display);
        }
        return result.ToArray();
    }

    // The oldest finished entries (by finish time, then position) beyond the history limit drop out: their positions are returned.
    private HashSet<int> TrimHistory(List<TagQueueItem> list)
    {
        int history = _lease.StoreLimits.TagQueueHistory;
        int finished = list.Count(item => item.State == TagQueueState.Done);
        if (finished <= history) return new HashSet<int>();
        return Enumerable.Range(0, list.Count).Where(i => list[i].State == TagQueueState.Done)
            .OrderBy(i => list[i].DoneAtUtc).ThenBy(i => i).Take(finished - history).ToHashSet();
    }

    private static List<TagQueueItem> KeepNotDropped(List<TagQueueItem> list, HashSet<int> dropped)
    {
        if (dropped.Count == 0) return list;
        List<TagQueueItem> kept = new(list.Count - dropped.Count);
        for (int i = 0; i < list.Count; i++) if (!dropped.Contains(i)) kept.Add(list[i]);
        return kept;
    }

    private TagQueueSnapshot Commit(TagQueueSnapshot current, List<TagQueueItem> items, int cursor, IReadOnlyList<string> stamp, CancellationToken token,
        IReadOnlyList<TagQueueGroup>? groups = null)
    {
        long revision = checked(current.Revision + 1);
        cursor = Math.Clamp(cursor, 0, items.Count);
        IReadOnlyList<TagQueueGroup> savedGroups = groups ?? current.Groups;
        string json = Serialize(revision, cursor, stamp, items, savedGroups);
        long written = StrictUtf8.GetByteCount(json);
        // Only growth is refused: a smaller or equal file is always written.
        if (written > _lease.StoreLimits.TagQueueFileBytes && written > ProfileStoreLimits.FileLength(_path))
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.TagQueueFile, _lease.StoreLimits.TagQueueFileBytes,
                "The tagging queue has reached its size limit.");
        PrepareBackupForRotation();
        bool rotates = File.Exists(_path);
        byte[]? previous = _primaryBytes;
        _primaryBytes = null;
        _primarySnapshot = null;
        _backupBytes = null;
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
        TagQueueSnapshot committed = new(revision, cursor, stamp.ToArray(), Array.AsReadOnly(items.ToArray()), TagQueueLoadState.Ready, Array.AsReadOnly(savedGroups.ToArray()));
        // The write rotated the file just read (and validated) into the backup; the file now holds exactly these bytes.
        if (rotates) _backupBytes = previous;
        _primaryBytes = Encoding.UTF8.GetBytes(json);
        _primarySnapshot = committed;
        return committed;
    }

    private TagQueueSnapshot ReadWritable()
    {
        TagQueueSnapshot snapshot = ReadCore();
        if (!snapshot.CanWrite) throw new TagQueueNewerVersionException();
        return snapshot;
    }

    private TagQueueSnapshot ReadCore()
    {
        RejectLink(_path);
        if (!File.Exists(_path))
        {
            RejectLink(_path + ".bak");
            if (File.Exists(_path + ".bak"))
                throw new InvalidDataException("The tagging queue is missing but a backup exists. Clear the queue to start over; nothing is restored automatically.");
            return TagQueueSnapshot.Empty;
        }
        byte[] bytes = ReadBytes(_path);
        if (_primarySnapshot != null && _primaryBytes != null && bytes.AsSpan().SequenceEqual(_primaryBytes)) return _primarySnapshot;
        _primaryBytes = null;
        _primarySnapshot = null;
        _parses++;
        TagQueueSnapshot snapshot = ParseBytes(bytes);
        (_primaryBytes, _primarySnapshot) = (bytes, snapshot);
        return snapshot;
    }

    // A backup of a newer version or one that cannot be opened is not ours to move: the write is refused with a message that names
    // the file. A damaged backup is set aside as .bad first, so the good primary is never lost and the evidence is kept.
    private void PrepareBackupForRotation()
    {
        string backupPath = _path + ".bak";
        if (!File.Exists(_path)) return;
        try
        {
            if (File.Exists(backupPath))
            {
                // A backup with the bytes this store validated or rotated into place itself needs no second parse.
                byte[] bytes = ReadBytes(backupPath);
                if (_backupBytes == null || !bytes.AsSpan().SequenceEqual(_backupBytes))
                {
                    _backupBytes = null;
                    _parses++;
                    if (ParseBytes(bytes).State == TagQueueLoadState.NewerVersion)
                        throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.NewerVersion);
                    _backupBytes = bytes;
                }
            }
        }
        catch (FileNotFoundException) { _backupBytes = null; }
        catch (InvalidDataException)
        {
            _backupBytes = null;
            if (AtomicJsonFile.SetAsideDamagedBackup(backupPath) == null)
                throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.Unreadable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.Unreadable); }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget != null || Directory.Exists(path))
            throw new InvalidDataException("The tagging queue path is not a regular file.");
    }

    private static byte[] ReadBytes(string path)
    {
        RejectLink(path);
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new TagQueueReadException(error); }
        if (length > ProfileStoreLimits.TagQueueFileCeilingBytes) throw new InvalidDataException("The tagging queue exceeds its size limit.");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new TagQueueReadException(error); }
        return bytes;
    }

    private static TagQueueSnapshot ParseBytes(byte[] bytes)
    {
        try
        {
            // Malformed bytes are never replaced with U+FFFD inside an identity: decode strictly, then parse.
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '﻿') json = json[1..];
            if (JsonDuplicateProperties.Any(bytes)) throw new InvalidDataException("The tagging queue repeats a property.");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return ParseRoot(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException
            or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("The tagging queue is invalid.", error); }
    }

    private static TagQueueSnapshot ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The tagging queue is not an object.");
        // A newer file may hold anything: only its version is looked at, nothing else is interpreted.
        if (!root.TryGetProperty("Version", out JsonElement versionValue) || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out int version))
            throw new InvalidDataException("The tagging queue has no version.");
        if (version > Version) return new TagQueueSnapshot(0, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>(), TagQueueLoadState.NewerVersion);
        if (version is not (LegacyVersion or Version)) throw new InvalidDataException("The tagging queue version is invalid.");
        bool groupsInFile = version == Version;
        ExactProperties(root, groupsInFile ? new[] { "Version", "Revision", "Cursor", "Stamp", "Groups", "Items" }
            : new[] { "Version", "Revision", "Cursor", "Stamp", "Items" });
        long revision = root.GetProperty("Revision").GetInt64();
        int cursor = root.GetProperty("Cursor").GetInt32();
        if (revision < 0 || cursor < 0) throw new InvalidDataException("The tagging queue position is invalid.");
        JsonElement stampArray = root.GetProperty("Stamp"), itemArray = root.GetProperty("Items");
        if (stampArray.ValueKind != JsonValueKind.Array || itemArray.ValueKind != JsonValueKind.Array
            || stampArray.GetArrayLength() > TagQueueSnapshot.MaximumStamp || itemArray.GetArrayLength() > ProfileStoreLimits.TagQueueEntryCeiling)
            throw new InvalidDataException("The tagging queue lists are invalid.");
        List<string> names = new();
        foreach (JsonElement name in stampArray.EnumerateArray())
            names.Add(name.ValueKind == JsonValueKind.String ? name.GetString()! : throw new InvalidDataException("The stamp set is invalid."));
        string[] stamp;
        try { stamp = NormalizeStamp(names); }
        catch (ArgumentException error) { throw new InvalidDataException("The stamp set is invalid.", error); }
        if (!stamp.SequenceEqual(names, StringComparer.Ordinal)) throw new InvalidDataException("The stamp set is not normalized.");
        List<TagQueueGroup> groups = groupsInFile ? ParseGroups(root.GetProperty("Groups")) : new();
        HashSet<string> groupIds = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        List<TagQueueItem> items = new(itemArray.GetArrayLength());
        HashSet<TagQueueKey> keys = new();
        foreach (JsonElement element in itemArray.EnumerateArray())
        {
            TagQueueItem item = ParseItem(element, groupsInFile);
            if (item.GroupId != null && !groupIds.Contains(item.GroupId)) throw new InvalidDataException("The tagging queue references a missing group.");
            if (!keys.Add(item.Key)) throw new InvalidDataException("The tagging queue holds an entry twice.");
            items.Add(item);
        }
        if (cursor > items.Count) throw new InvalidDataException("The tagging queue position is outside the queue.");
        return new TagQueueSnapshot(revision, cursor, stamp, Array.AsReadOnly(items.ToArray()), TagQueueLoadState.Ready, Array.AsReadOnly(groups.ToArray()));
    }

    private static List<TagQueueGroup> ParseGroups(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ProfileStoreLimits.TagQueueGroupCeiling)
            throw new InvalidDataException("The tagging groups list is invalid.");
        List<TagQueueGroup> groups = new(array.GetArrayLength());
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement element in array.EnumerateArray())
        {
            ExactProperties(element, "Id", "Name", "Order", "Stamp");
            string id = RequiredString(element, "Id"), name = RequiredString(element, "Name");
            int order = element.GetProperty("Order").GetInt32();
            if (order != groups.Count || !ids.Add(id) || !names.Add(name)) throw new InvalidDataException("The tagging groups are not normalized.");
            JsonElement stampArray = element.GetProperty("Stamp");
            if (stampArray.ValueKind != JsonValueKind.Array || stampArray.GetArrayLength() > TagQueueSnapshot.MaximumStamp)
                throw new InvalidDataException("The group stamp is invalid.");
            List<string> namesInStamp = new();
            foreach (JsonElement stampName in stampArray.EnumerateArray())
                namesInStamp.Add(stampName.ValueKind == JsonValueKind.String ? stampName.GetString()! : throw new InvalidDataException("The group stamp is invalid."));
            string[] stamp;
            try { stamp = NormalizeStamp(namesInStamp); }
            catch (ArgumentException error) { throw new InvalidDataException("The group stamp is invalid.", error); }
            if (!stamp.SequenceEqual(namesInStamp, StringComparer.Ordinal)) throw new InvalidDataException("The group stamp is not normalized.");
            TagQueueGroup group = new(id, name, order, stamp);
            if (!group.IsValid()) throw new InvalidDataException("The tagging group is invalid.");
            groups.Add(group);
        }
        return groups;
    }

    private static void ExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The tagging queue holds a value of the wrong kind.");
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            count++;
            if (Array.IndexOf(names, property.Name) < 0) throw new InvalidDataException("The tagging queue holds an unknown property.");
        }
        if (count != names.Length) throw new InvalidDataException("The tagging queue lacks a property.");
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("A property has the wrong kind.");
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new InvalidDataException("A property has the wrong kind.");

    private static DateTimeOffset Time(string text) =>
        DateTimeOffset.TryParseExact(text, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset value)
        && value.Offset == TimeSpan.Zero ? value : throw new InvalidDataException("A time is invalid.");

    private static TagQueueItem ParseItem(JsonElement element, bool groupsInFile)
    {
        ExactProperties(element, groupsInFile ? new[] { "Thread", "Directory", "AssetId", "Sha256", "Length", "MediaId", "Variant", "PostId", "Position", "ImportItem",
            "BooruPost", "Historical", "LocalSlot", "Name", "Kind", "State", "AddedAtUtc", "DoneAtUtc", "GroupId" } : new[] { "Thread", "Directory", "AssetId", "Sha256", "Length", "MediaId", "Variant", "PostId", "Position", "ImportItem",
            "BooruPost", "Historical", "LocalSlot", "Name", "Kind", "State", "AddedAtUtc", "DoneAtUtc" });
        JsonElement thread = element.GetProperty("Thread");
        ExactProperties(thread, "Source", "Board", "Thread");
        JsonElement position = element.GetProperty("Position");
        int? positionValue = position.ValueKind == JsonValueKind.Null ? null : position.ValueKind == JsonValueKind.Number ? position.GetInt32() : throw new InvalidDataException("A property has the wrong kind.");
        JsonElement historical = element.GetProperty("Historical");
        if (historical.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("A property has the wrong kind.");
        JsonElement length = element.GetProperty("Length");
        if (length.ValueKind != JsonValueKind.Number) throw new InvalidDataException("A property has the wrong kind.");
        if (!TryParseName(RequiredString(element, "Kind"), out SavedMediaKind kind) || !TryParseName(RequiredString(element, "State"), out TagQueueState state))
            throw new InvalidDataException("A state or kind is unknown.");
        string? done = OptionalString(element, "DoneAtUtc");
        TagQueueItem item = new(new ResourceId(RequiredString(thread, "Source"), RequiredString(thread, "Board"), RequiredString(thread, "Thread")),
            RequiredString(element, "Directory"), RequiredString(element, "AssetId"), RequiredString(element, "Sha256"), length.GetInt64(),
            RequiredString(element, "MediaId"), RequiredString(element, "Variant"), OptionalString(element, "PostId"), positionValue,
            OptionalString(element, "ImportItem"), OptionalString(element, "BooruPost"), historical.GetBoolean(), OptionalString(element, "LocalSlot"),
            RequiredString(element, "Name"), kind, state, Time(RequiredString(element, "AddedAtUtc")), done == null ? null : Time(done),
            groupsInFile ? OptionalString(element, "GroupId") : null);
        return item.Validate() == null ? item : throw new InvalidDataException("The tagging queue holds an invalid entry.");
    }

    // Only the exact name the writer uses: Enum.TryParse would also take "1", " Done", "done" or "Open, Done", which the strict file refuses.
    private static bool TryParseName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            if (!string.Equals(candidate.ToString(), text, StringComparison.Ordinal)) continue;
            value = candidate;
            return true;
        }
        value = default;
        return false;
    }

    private static string Serialize(long revision, int cursor, IReadOnlyList<string> stamp, IReadOnlyList<TagQueueItem> items, IReadOnlyList<TagQueueGroup> groups)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", Version);
            writer.WriteNumber("Revision", revision);
            writer.WriteNumber("Cursor", cursor);
            writer.WriteStartArray("Stamp");
            foreach (string name in stamp) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteStartArray("Groups");
            foreach (TagQueueGroup group in groups)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", group.Id);
                writer.WriteString("Name", group.Name);
                writer.WriteNumber("Order", group.Order);
                writer.WriteStartArray("Stamp");
                foreach (string name in group.Stamp) writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Items");
            foreach (TagQueueItem item in items)
            {
                writer.WriteStartObject();
                writer.WriteStartObject("Thread");
                writer.WriteString("Source", item.Thread.Source);
                writer.WriteString("Board", item.Thread.Board);
                writer.WriteString("Thread", item.Thread.Thread);
                writer.WriteEndObject();
                writer.WriteString("Directory", item.Directory);
                writer.WriteString("AssetId", item.AssetId);
                writer.WriteString("Sha256", item.Sha256);
                writer.WriteNumber("Length", item.Length);
                writer.WriteString("MediaId", item.MediaId);
                writer.WriteString("Variant", item.Variant);
                writer.WriteString("PostId", item.PostId);
                if (item.Position is { } position) writer.WriteNumber("Position", position); else writer.WriteNull("Position");
                writer.WriteString("ImportItem", item.ImportItem);
                writer.WriteString("BooruPost", item.BooruPost);
                writer.WriteBoolean("Historical", item.Historical);
                writer.WriteString("LocalSlot", item.LocalSlot);
                writer.WriteString("Name", item.Name);
                writer.WriteString("Kind", item.Kind.ToString());
                writer.WriteString("State", item.State.ToString());
                writer.WriteString("AddedAtUtc", item.AddedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("DoneAtUtc", item.DoneAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("GroupId", item.GroupId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return StrictUtf8.GetString(stream.ToArray());
    }
}
