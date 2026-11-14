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
/// The profile's tagging queue (<c>tag-queue.json</c>, version 1): the media still to be tagged, their state, the position of the
/// walk and the stamp set. Strict and atomic like the other private lists: an unknown property, a repeated property, a malformed
/// identity, a damaged file or a link in place of the file is refused, never repaired; a file of a newer version is read as empty
/// and kept untouched. Every write replaces the file atomically and rotates the last good copy into <c>.bak</c>. Limits (entries,
/// file size, kept finished entries) are the settings behind <see cref="ProfileStoreLimits"/>; lowering one never makes a saved
/// file unreadable, it only stops growth. The store never resolves, opens or writes media.
/// </summary>
internal sealed class TagQueueStore
{
    internal const int Version = 1;
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
            foreach (TagQueueItem item in items)
            {
                if (item.Validate() is { } problem) throw new ArgumentException("The tagging queue entry is invalid: " + problem + ".", nameof(items));
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
            string empty = Serialize(1, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>());
            _primaryBytes = null;
            _primarySnapshot = null;
            AtomicJsonFile.WriteAllText(_path, empty, backupCurrentFile: false, token);
            return new TagQueueSnapshot(1, 0, Array.Empty<string>(), Array.Empty<TagQueueItem>(), TagQueueLoadState.Ready);
        }
        if (current.Count == 0) return current;
        List<TagQueueItem> list = finishedOnly ? current.Items.Where(item => item.State != TagQueueState.Done).ToList() : new();
        if (list.Count == current.Count) return current;
        int cursor = finishedOnly ? current.Items.Take(current.Cursor).Count(item => item.State != TagQueueState.Done) : 0;
        return Commit(current, list, cursor, current.Stamp, token);
    });

    /// <summary>Replaces the stamp set: at most <see cref="TagQueueSnapshot.MaximumStamp"/> distinct valid tag names (the user limit is lower).</summary>
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

    private TagQueueSnapshot Commit(TagQueueSnapshot current, List<TagQueueItem> items, int cursor, IReadOnlyList<string> stamp, CancellationToken token)
    {
        long revision = checked(current.Revision + 1);
        cursor = Math.Clamp(cursor, 0, items.Count);
        string json = Serialize(revision, cursor, stamp, items);
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
        TagQueueSnapshot committed = new(revision, cursor, stamp.ToArray(), Array.AsReadOnly(items.ToArray()), TagQueueLoadState.Ready);
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
        if (version != Version) throw new InvalidDataException("The tagging queue version is invalid.");
        ExactProperties(root, "Version", "Revision", "Cursor", "Stamp", "Items");
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
        List<TagQueueItem> items = new(itemArray.GetArrayLength());
        HashSet<TagQueueKey> keys = new();
        foreach (JsonElement element in itemArray.EnumerateArray())
        {
            TagQueueItem item = ParseItem(element);
            if (!keys.Add(item.Key)) throw new InvalidDataException("The tagging queue holds an entry twice.");
            items.Add(item);
        }
        if (cursor > items.Count) throw new InvalidDataException("The tagging queue position is outside the queue.");
        return new TagQueueSnapshot(revision, cursor, stamp, Array.AsReadOnly(items.ToArray()), TagQueueLoadState.Ready);
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

    private static TagQueueItem ParseItem(JsonElement element)
    {
        ExactProperties(element, "Thread", "Directory", "AssetId", "Sha256", "Length", "MediaId", "Variant", "PostId", "Position", "ImportItem",
            "BooruPost", "Historical", "LocalSlot", "Name", "Kind", "State", "AddedAtUtc", "DoneAtUtc");
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
            RequiredString(element, "Name"), kind, state, Time(RequiredString(element, "AddedAtUtc")), done == null ? null : Time(done));
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

    private static string Serialize(long revision, int cursor, IReadOnlyList<string> stamp, IReadOnlyList<TagQueueItem> items)
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
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return StrictUtf8.GetString(stream.ToArray());
    }
}
