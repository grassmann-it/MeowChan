using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Gallery;

/// <summary>
/// Disposable file copy of the media index so the media page does not rescan every thread on each
/// open. Any problem (missing, damaged, newer, oversized) means "no cache": the caller rebuilds.
/// The cache never decides anything: a thread is reused only when the builder sees an equal state.
/// The document is read and written as a stream, element by element: neither the whole file nor a UTF-16 copy
/// of it is ever held in memory, and repeated values (thread ids, titles, folders, variants) are shared on load.
/// </summary>
internal static class MediaLibraryIndexCache
{
    internal const string FileName = "media-index-v1.json";
    /// <summary>
    /// Fixed technical ceiling of the cache file: a larger file is never read, nothing larger is ever written. The user's
    /// write limit (<see cref="DefaultLimitBytes"/>, a setting) is at most this, so lowering the setting never makes an
    /// existing cache unreadable; it only stops a larger one from being written.
    /// </summary>
    internal const long MaximumBytes = 1024L * 1024 * 1024;
    /// <summary>Default write limit: the former fixed limit, so default behavior does not change.</summary>
    internal const long DefaultLimitBytes = 256L * 1024 * 1024;
    internal const long MinimumLimitBytes = 16L * 1024 * 1024;
    /// <summary>From this share of the write limit on, the page warns before the cache stops being saved.</summary>
    internal const double WarningShare = 0.75;
    /// <summary>
    /// Entries and threads of the in-memory index a cache file may describe. The write limits are settings (defaults: the
    /// former fixed values, so default behavior does not change); reads accept up to the fixed technical ceilings, so
    /// lowering a setting never makes an existing cache unreadable.
    /// </summary>
    internal const int DefaultEntryLimit = 1_000_000;
    internal const int MinimumEntryLimit = 100_000;
    internal const int EntryCeiling = 5_000_000;
    internal const int DefaultThreadLimit = 200_000;
    internal const int MinimumThreadLimit = 20_000;
    internal const int ThreadCeiling = 1_000_000;
    // Version 2 adds the local tag slot of loose entries; version 3 adds the Danbooru post id of downloaded provider media;
    // version 4 adds the manifest file identities ("I") and the provider manifests ("Pm"), so an unchanged manifest is not
    // read again, and the item id ("Im") of manually imported files. A version 3 document is still read (it simply has
    // neither identities nor imported files: the next build reads the manifests once, as before, and writes version 4;
    // no cold rebuild). An older or newer document is no cache: rebuild.
    /// <summary>
    /// Longest string value (UTF-16 chars) a cache document may hold: thread titles, folders, file names, ids, tags. The writer
    /// refuses to save an index with a longer value and the reader treats a longer one as a damaged document, so a manipulated
    /// file cannot make the load allocate a huge string and a value the writer produced is never rejected on load. The longest
    /// legitimate values (a title of 160 text elements, a folder path, a 255-character file name) stay far below it.
    /// </summary>
    internal const int MaximumStringChars = 32 * 1024;
    // The JSON writer escapes every non-ASCII char as \uXXXX (a pair as 12 bytes): the longest raw token a legal value can have.
    private const int MaximumRawStringBytes = MaximumStringChars * 6;
    private const int CurrentVersion = 4;
    private const int PreviousVersion = 3;
    private static readonly JsonSerializerOptions WriteOptions = CreateOptions(null);

    private static JsonSerializerOptions CreateOptions(Reader? reader) => new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
        Converters = { new ThreadConverter(reader), new EntryConverter(reader), new ManifestConverter(reader) }
    };

    private sealed class Document
    {
        public int Version { get; set; }
        public IReadOnlyList<MediaLibraryThreadState>? Threads { get; set; }
        public IReadOnlyList<MediaLibraryEntry>? Entries { get; set; }
        public IReadOnlyList<MediaLibraryManifestState>? Pm { get; set; }
    }

    /// <summary>The cached index, the file's size and its identity while it was read; null when there is no usable cache.</summary>
    internal static async Task<MediaLibraryIndexCacheRead?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            RegularProfileFileIdentity? before = RegularProfileFile.TryGetIdentity(path);
            long length;
            Document? document;
            Reader reader = new(cancellationToken);
            await using (FileStream stream = RegularProfileFile.OpenRead(path))
            {
                length = stream.Length;
                if (length > MaximumBytes) return null;
                document = await JsonSerializer.DeserializeAsync<Document>(stream, CreateOptions(reader), cancellationToken).ConfigureAwait(false);
            }
            MediaLibrarySnapshot? snapshot = Validate(document, reader);
            if (snapshot == null) return null;
            RegularProfileFileIdentity? after = RegularProfileFile.TryGetIdentity(path);
            return new(snapshot, length, before is { } stable && after == stable ? stable : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException
            or InvalidOperationException or FormatException)
        { return null; }
    }

    private static MediaLibrarySnapshot? Validate(Document? document, Reader reader)
    {
        if (document is not { Version: CurrentVersion or PreviousVersion } || document.Threads == null || document.Entries == null
            || document.Threads.Count > ThreadCeiling || document.Entries.Count > EntryCeiling) return null;
        // A version 3 document never carries what version 4 added.
        if (document.Version == PreviousVersion && (reader.SawIdentity || reader.SawImport || document.Pm != null)) return null;
        HashSet<ResourceId> known = new();
        foreach (MediaLibraryThreadState? thread in document.Threads)
            if (thread == null || !known.Add(thread.Thread)) return null;
        foreach (MediaLibraryEntry? entry in document.Entries)
        {
            if (entry == null) return null;
            // A downloaded provider post is no archived thread: its identity must be exactly the one its post id names,
            // and it never carries a post binding (a present one names its loose slot; an older cache without it is rebuilt on use).
            if (entry.BooruPostId is { } postId && (!DanbooruPostReference.IsValidPostId(postId) || entry.Thread != BooruMediaIdentity.Thread(postId)
                || entry.PostId != null || entry.AttachmentRevisionKey != null || entry.Position != null)) return null;
            // An imported file is no archived thread either: its batch identity is the borrowed thread, it never carries a
            // post binding, and a present one always names its loose slot.
            if (entry.ImportItemId is { } itemId && (entry.BooruPostId != null || !Import.ManualImportIdentity.TryParseId(itemId, out _)
                || !Import.ManualImportIdentity.IsImportThread(entry.Thread) || entry.PostId != null || entry.AttachmentRevisionKey != null
                || entry.Position != null || entry.Presence != MediaLibraryPresence.Missing && entry.LocalSlot == null)) return null;
            // Every other entry belongs to a listed thread.
            if (entry.BooruPostId == null && entry.ImportItemId == null && !known.Contains(entry.Thread)) return null;
        }
        HashSet<string> providerFolders = new(StringComparer.Ordinal);
        foreach (MediaLibraryManifestState? manifest in document.Pm ?? (IReadOnlyList<MediaLibraryManifestState>)Array.Empty<MediaLibraryManifestState>())
            if (manifest == null || !providerFolders.Add(manifest.Directory)) return null;
        // A cached index is a starting point for reuse, not a complete answer on its own.
        return new MediaLibrarySnapshot(document.Entries, document.Threads, Array.Empty<string>(),
            complete: document.Threads.All(thread => thread.Complete), providerManifests: document.Pm);
    }

    /// <summary>
    /// Writes the index as a stream to a temporary file and replaces the cache atomically. Stops as soon as the document
    /// would exceed <paramref name="limitBytes"/> (clamped to the technical ceiling) and then leaves the existing cache as
    /// it was; the result says so, so the page can tell the user instead of silently rebuilding on every start.
    /// </summary>
    internal static Task<MediaLibraryIndexCacheWrite> SaveAsync(string path, MediaLibrarySnapshot snapshot, long limitBytes,
        CancellationToken token = default) => SaveAsync(path, snapshot, limitBytes, DefaultEntryLimit, DefaultThreadLimit, token);

    /// <summary>As above, with the configured limits of entries and threads (clamped to the technical ceilings).</summary>
    internal static async Task<MediaLibraryIndexCacheWrite> SaveAsync(string path, MediaLibrarySnapshot snapshot, long limitBytes,
        int entryLimit, int threadLimit, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = WithoutFolderItems(snapshot);
        long limit = Math.Clamp(limitBytes, 1, MaximumBytes);
        if (ExceedsCountLimits(snapshot, entryLimit, threadLimit))
            return new(MediaLibraryIndexCacheWriteStatus.TooManyEntries, 0, limit, null);
        Document document = new()
        {
            Version = CurrentVersion, Threads = snapshot.Threads, Entries = snapshot.Entries,
            Pm = snapshot.ProviderManifests.Count == 0 ? null : snapshot.ProviderManifests
        };
        // A cache needs neither a backup nor a preserved copy of the previous version: plain atomic replace.
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long written;
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                LimitedWriteStream limited = new(file, limit);
                try { await JsonSerializer.SerializeAsync(limited, document, WriteOptions, token).ConfigureAwait(false); }
                // A value longer than any a cache may hold is refused like an index over the limit: nothing is written.
                catch (Exception error) when (error is LimitExceededException or ValueTooLongException)
                { return new(MediaLibraryIndexCacheWriteStatus.TooLarge, limited.Written, limit, null); }
                written = limited.Written;
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return new(MediaLibraryIndexCacheWriteStatus.Written, written, limit, RegularProfileFile.TryGetIdentity(path));
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The index without the files of folder sources: they are never saved (an unhashed one has no digest, and the shadow
    /// catalog projects them again in one query). The same snapshot when it has none, so an unchanged index still compares equal.
    /// </summary>
    internal static MediaLibrarySnapshot WithoutFolderItems(MediaLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool any = false;
        foreach (MediaLibraryEntry entry in snapshot.Entries) if (entry.FolderItemId != null) { any = true; break; }
        if (!any) return snapshot;
        return new MediaLibrarySnapshot(snapshot.Entries.Where(entry => entry.FolderItemId == null), snapshot.Threads, snapshot.Warnings,
            snapshot.Complete, snapshot.ProviderManifests);
    }

    /// <summary>True when the index has more entries or threads than the configured limits allow a cache file to hold.</summary>
    internal static bool ExceedsCountLimits(MediaLibrarySnapshot snapshot, int entryLimit, int threadLimit) =>
        snapshot.Entries.Count > Math.Clamp(entryLimit, 1, EntryCeiling) || snapshot.Threads.Count > Math.Clamp(threadLimit, 1, ThreadCeiling);

    private static ResourceId Id(string? source, string? board, string? thread)
    {
        ResourceId id = new(source!, board!, thread);
        ArchiveValidation.ValidateId(id);
        return id;
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonException Damaged() => new("The media index cache is damaged.");

    /// <summary>State of one load: shared strings and ids, the cancellation token and what the document contained.</summary>
    private sealed class Reader(CancellationToken token)
    {
        private readonly HashSet<string> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<ResourceId, ResourceId> _ids = new();
        private char[] _buffer = new char[256];
        private ResourceId? _lastId;
        private int _elements;
        internal bool SawIdentity { get; set; }
        internal bool SawImport { get; set; }
        internal int Threads { get; set; }
        internal int Entries { get; set; }

        internal void Tick()
        {
            if ((++_elements & 0xFFF) == 0) token.ThrowIfCancellationRequested();
        }

        /// <summary>The string value at the reader, shared with every equal value read before; null for JSON null.</summary>
        internal string? Shared(ref Utf8JsonReader json)
        {
            if (json.TokenType == JsonTokenType.Null) return null;
            if (json.TokenType != JsonTokenType.String) throw Damaged();
            long raw = json.HasValueSequence ? json.ValueSequence.Length : json.ValueSpan.Length;
            if (raw > MaximumRawStringBytes) throw Damaged();
            int length = (int)raw;
            if (length > 4096)
            {
                string text = json.GetString()!;
                if (text.Length > MaximumStringChars) throw Damaged();
                return Intern(text);
            }
            if (_buffer.Length < length) _buffer = new char[Math.Max(length, _buffer.Length * 2)];
            int count = json.CopyString(_buffer);
            ReadOnlySpan<char> value = _buffer.AsSpan(0, count);
            if (_strings.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(value, out string? existing)) return existing;
            string created = new(value);
            _strings.Add(created);
            return created;
        }

        private string Intern(string value)
        {
            if (_strings.TryGetValue(value, out string? existing)) return existing;
            _strings.Add(value);
            return value;
        }

        /// <summary>One shared, validated id per thread: entries of a thread reuse the thread's own instance.</summary>
        internal ResourceId Id(string? source, string? board, string? thread)
        {
            if (_lastId is { } last && ReferenceEquals(last.Source, source) && ReferenceEquals(last.Board, board) && ReferenceEquals(last.Thread, thread))
                return last;
            ResourceId id = MediaLibraryIndexCache.Id(source, board, thread);
            if (_ids.TryGetValue(id, out ResourceId? existing)) id = existing;
            else _ids.Add(id, id);
            return _lastId = id;
        }
    }

    private static string? ReadText(ref Utf8JsonReader json)
    {
        if (json.TokenType == JsonTokenType.Null) return null;
        if (json.TokenType != JsonTokenType.String) throw Damaged();
        if ((json.HasValueSequence ? json.ValueSequence.Length : json.ValueSpan.Length) > MaximumRawStringBytes) throw Damaged();
        string text = json.GetString()!;
        return text.Length > MaximumStringChars ? throw Damaged() : text;
    }

    private static long Int64(ref Utf8JsonReader json) =>
        json.TokenType == JsonTokenType.Number && json.TryGetInt64(out long value) ? value : throw Damaged();

    private static long? NullableInt64(ref Utf8JsonReader json) => json.TokenType == JsonTokenType.Null ? null : Int64(ref json);

    private static int? NullableInt32(ref Utf8JsonReader json) => json.TokenType == JsonTokenType.Null ? null
        : json.TokenType == JsonTokenType.Number && json.TryGetInt32(out int value) ? value : throw Damaged();

    private static bool Boolean(ref Utf8JsonReader json) => json.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        _ => throw Damaged()
    };

    private static DateTimeOffset Date(ref Utf8JsonReader json) =>
        json.TokenType == JsonTokenType.String && json.TryGetDateTimeOffset(out DateTimeOffset value) ? value : throw Damaged();

    private static DateTimeOffset? NullableDate(ref Utf8JsonReader json) => json.TokenType == JsonTokenType.Null ? null : Date(ref json);

    // Each object is read whole (the serializer buffers one element at a time). Names are matched without allocating;
    // as with the serializer before, a repeated member keeps its last value.
    private static void Properties(ref Utf8JsonReader json, ReadProperty read)
    {
        if (json.TokenType != JsonTokenType.StartObject) throw Damaged();
        Span<char> buffer = stackalloc char[8];
        while (json.Read() && json.TokenType != JsonTokenType.EndObject)
        {
            if (json.TokenType != JsonTokenType.PropertyName || json.HasValueSequence || json.ValueSpan.Length > buffer.Length) throw Damaged();
            int length = json.CopyString(buffer);
            ReadOnlySpan<char> name = buffer[..length];
            if (!json.Read()) throw Damaged();
            if (!read(name, ref json)) throw Damaged(); // An unknown member means a damaged or foreign document.
        }
        if (json.TokenType != JsonTokenType.EndObject) throw Damaged();
    }

    private delegate bool ReadProperty(scoped ReadOnlySpan<char> name, ref Utf8JsonReader json);

    private static RegularProfileFileIdentity Identity(ref Utf8JsonReader json)
    {
        if (json.TokenType != JsonTokenType.StartArray) throw Damaged();
        Span<long> values = stackalloc long[8];
        int count = 0;
        while (json.Read() && json.TokenType != JsonTokenType.EndArray)
        {
            if (count == values.Length) throw Damaged();
            values[count++] = Int64(ref json);
        }
        if (count != values.Length) throw Damaged();
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
    }

    private static void WriteIdentity(Utf8JsonWriter writer, RegularProfileFileIdentity identity)
    {
        writer.WriteStartArray("I");
        writer.WriteNumberValue(identity.Device);
        writer.WriteNumberValue(identity.Inode);
        writer.WriteNumberValue(identity.Size);
        writer.WriteNumberValue(identity.ModifiedSeconds);
        writer.WriteNumberValue(identity.ModifiedNanoseconds);
        writer.WriteNumberValue(identity.ChangedSeconds);
        writer.WriteNumberValue(identity.ChangedNanoseconds);
        writer.WriteNumberValue(identity.InodeHigh);
        writer.WriteEndArray();
    }

    private static void Optional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value == null) return;
        if (value.Length > MaximumStringChars) throw new ValueTooLongException();
        writer.WriteString(name, value);
    }

    /// <summary>{"D": folder, "G": generation, "I": identity}; G and I are left out when unknown.</summary>
    private sealed class ManifestConverter(Reader? reader) : JsonConverter<MediaLibraryManifestState>
    {
        public override MediaLibraryManifestState Read(ref Utf8JsonReader json, Type typeToConvert, JsonSerializerOptions options)
        {
            if (json.TokenType == JsonTokenType.Null) throw Damaged();
            string? directory = null, generation = null;
            RegularProfileFileIdentity? identity = null;
            Reader shared = reader ?? throw new NotSupportedException();
            shared.Tick();
            Properties(ref json, (scoped ReadOnlySpan<char> name, ref Utf8JsonReader value) =>
            {
                switch (name)
                {
                    case "D": directory = shared.Shared(ref value); return true;
                    case "G": generation = ReadText(ref value); return true;
                    case "I":
                        if (value.TokenType != JsonTokenType.Null) { identity = Identity(ref value); shared.SawIdentity = true; }
                        return true;
                    default: return false;
                }
            });
            if (string.IsNullOrWhiteSpace(directory)) throw Damaged();
            return new(directory, generation) { Identity = identity };
        }

        public override void Write(Utf8JsonWriter writer, MediaLibraryManifestState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value.Directory.Length > MaximumStringChars) throw new ValueTooLongException();
            writer.WriteString("D", value.Directory);
            Optional(writer, "G", value.Generation);
            if (value.Identity is { } identity) WriteIdentity(writer, identity);
            writer.WriteEndObject();
        }
    }

    /// <summary>{"S","B","T": thread id, "R": revision, "C": complete, "M": manifests}.</summary>
    private sealed class ThreadConverter(Reader? reader) : JsonConverter<MediaLibraryThreadState>
    {
        private readonly ManifestConverter _manifest = new(reader);

        public override MediaLibraryThreadState Read(ref Utf8JsonReader json, Type typeToConvert, JsonSerializerOptions options)
        {
            if (json.TokenType == JsonTokenType.Null) throw Damaged();
            Reader shared = reader ?? throw new NotSupportedException();
            if (++shared.Threads > ThreadCeiling) throw Damaged();
            shared.Tick();
            string? source = null, board = null, thread = null;
            long revision = 0;
            bool complete = false;
            List<MediaLibraryManifestState>? manifests = null;
            ManifestConverter manifest = _manifest;
            Properties(ref json, (scoped ReadOnlySpan<char> name, ref Utf8JsonReader value) =>
            {
                switch (name)
                {
                    case "S": source = shared.Shared(ref value); return true;
                    case "B": board = shared.Shared(ref value); return true;
                    case "T": thread = shared.Shared(ref value); return true;
                    case "R": revision = Int64(ref value); return true;
                    case "C": complete = Boolean(ref value); return true;
                    case "M":
                        if (value.TokenType == JsonTokenType.Null) return true;
                        if (value.TokenType != JsonTokenType.StartArray) throw Damaged();
                        manifests = new();
                        while (value.Read() && value.TokenType != JsonTokenType.EndArray)
                            manifests.Add(manifest.Read(ref value, typeof(MediaLibraryManifestState), options));
                        return true;
                    default: return false;
                }
            });
            if (manifests == null || revision < 0) throw Damaged();
            return new(shared.Id(source, board, thread), revision, manifests.ToArray(), complete);
        }

        public override void Write(Utf8JsonWriter writer, MediaLibraryThreadState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            Optional(writer, "S", value.Thread.Source);
            Optional(writer, "B", value.Thread.Board);
            Optional(writer, "T", value.Thread.Thread);
            writer.WriteNumber("R", value.Revision);
            writer.WriteBoolean("C", value.Complete);
            writer.WriteStartArray("M");
            foreach (MediaLibraryManifestState manifest in value.Manifests) _manifest.Write(writer, manifest, options);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    /// <summary>The entry fields under the short names version 1–3 used, in the same order.</summary>
    private sealed class EntryConverter(Reader? reader) : JsonConverter<MediaLibraryEntry>
    {
        public override MediaLibraryEntry Read(ref Utf8JsonReader json, Type typeToConvert, JsonSerializerOptions options)
        {
            if (json.TokenType == JsonTokenType.Null) throw Damaged();
            Reader shared = reader ?? throw new NotSupportedException();
            if (++shared.Entries > EntryCeiling) throw Damaged();
            shared.Tick();
            string? source = null, board = null, thread = null, title = null, post = null, key = null, dir = null, asset = null, sha = null,
                mid = null, variant = null, fileName = null, slot = null, providerPost = null, importItem = null;
            long revision = 0;
            long? ordinal = null, length = null;
            int? position = null, kind = null;
            DateTimeOffset first = default;
            DateTimeOffset? last = null, at = null;
            bool missing = false, loose = false, old = false;
            Properties(ref json, (scoped ReadOnlySpan<char> name, ref Utf8JsonReader value) =>
            {
                switch (name)
                {
                    case "S": source = shared.Shared(ref value); break;
                    case "B": board = shared.Shared(ref value); break;
                    case "T": thread = shared.Shared(ref value); break;
                    case "Title": title = shared.Shared(ref value); break;
                    case "Rev": revision = Int64(ref value); break;
                    case "First": first = Date(ref value); break;
                    case "Last": last = NullableDate(ref value); break;
                    case "Post": post = ReadText(ref value); break;
                    case "Ord": ordinal = NullableInt64(ref value); break;
                    case "At": at = NullableDate(ref value); break;
                    case "Pos": position = NullableInt32(ref value); break;
                    case "Key": key = ReadText(ref value); break;
                    case "Dir": dir = shared.Shared(ref value); break;
                    case "Asset": asset = ReadText(ref value); break;
                    case "Sha": sha = ReadText(ref value); break;
                    case "Len": length = NullableInt64(ref value); break;
                    case "Mid": mid = ReadText(ref value); break;
                    case "Var": variant = shared.Shared(ref value); break;
                    case "Name": fileName = ReadText(ref value); break;
                    case "Kind": kind = NullableInt32(ref value) ?? throw Damaged(); break;
                    case "Missing": missing = Boolean(ref value); break;
                    case "Loose": loose = Boolean(ref value); break;
                    case "Old": old = Boolean(ref value); break;
                    case "Slot": slot = ReadText(ref value); break;
                    case "Bp": providerPost = ReadText(ref value); break;
                    case "Im": importItem = ReadText(ref value); if (importItem != null) shared.SawImport = true; break;
                    default: return false;
                }
                return true;
            });
            SavedMediaKind mediaKind = (SavedMediaKind)(kind ?? 0);
            if (title == null || variant == null || !Enum.IsDefined(mediaKind)
                // A present entry names its file and bytes.
                || !missing && (dir == null || asset == null || !Sha256(sha))
                // A local slot exists only on a present, unbound entry; anything else is a damaged cache.
                || slot != null && (missing || key != null || slot.Length == 0)) throw Damaged();
            return new MediaLibraryEntry(shared.Id(source, board, thread), title, revision, first, last, post, ordinal, at, position, key, dir,
                asset, sha, length, mid, variant, fileName, mediaKind, missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present,
                loose, old, slot) { BooruPostId = providerPost, ImportItemId = importItem };
        }

        public override void Write(Utf8JsonWriter writer, MediaLibraryEntry entry, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            Optional(writer, "S", entry.Thread.Source);
            Optional(writer, "B", entry.Thread.Board);
            Optional(writer, "T", entry.Thread.Thread);
            Optional(writer, "Title", entry.ThreadTitle);
            writer.WriteNumber("Rev", entry.ThreadRevision);
            writer.WriteString("First", entry.FirstSeenAt);
            if (entry.LastCapturedAt is { } last) writer.WriteString("Last", last);
            Optional(writer, "Post", entry.PostId);
            if (entry.PostOrdinal is { } ordinal) writer.WriteNumber("Ord", ordinal);
            if (entry.PostedAt is { } at) writer.WriteString("At", at);
            if (entry.Position is { } position) writer.WriteNumber("Pos", position);
            Optional(writer, "Key", entry.AttachmentRevisionKey);
            Optional(writer, "Dir", entry.Directory);
            Optional(writer, "Asset", entry.AssetId);
            Optional(writer, "Sha", entry.Sha256);
            if (entry.Length is { } length) writer.WriteNumber("Len", length);
            Optional(writer, "Mid", entry.MediaId);
            Optional(writer, "Var", entry.Variant);
            Optional(writer, "Name", entry.Name);
            writer.WriteNumber("Kind", (int)entry.Kind);
            writer.WriteBoolean("Missing", entry.Presence == MediaLibraryPresence.Missing);
            writer.WriteBoolean("Loose", entry.Unassigned);
            writer.WriteBoolean("Old", entry.Historical);
            Optional(writer, "Slot", entry.LocalSlot);
            Optional(writer, "Bp", entry.BooruPostId);
            Optional(writer, "Im", entry.ImportItemId);
            writer.WriteEndObject();
        }
    }

    private sealed class ValueTooLongException : IOException
    {
        internal ValueTooLongException() : base("A media index cache value is longer than a cache may hold.") { }
    }

    private sealed class LimitExceededException : IOException
    {
        internal LimitExceededException() : base("The media index cache would exceed its limit.") { }
    }

    /// <summary>Counts what is written and refuses to go past the limit (nothing beyond it reaches the disk).</summary>
    private sealed class LimitedWriteStream(Stream inner, long limit) : Stream
    {
        internal long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Count(int count)
        {
            if (Written + count > limit) { Written += count; throw new LimitExceededException(); }
            Written += count;
        }

        public override void Write(byte[] buffer, int offset, int count) { Count(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer.Length); inner.Write(buffer); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

/// <summary>A loaded cache: the index, the file's size in bytes and its identity while it was read (null: unknown).</summary>
internal sealed record MediaLibraryIndexCacheRead(MediaLibrarySnapshot Snapshot, long Bytes, RegularProfileFileIdentity? Identity);

internal enum MediaLibraryIndexCacheWriteStatus
{
    /// <summary>The file now holds the index.</summary>
    Written,
    /// <summary>The file already held this index; nothing was written.</summary>
    Unchanged,
    /// <summary>The index would exceed the write limit; the existing file was left as it was.</summary>
    TooLarge,
    /// <summary>The index exceeds the fixed technical ceiling of entries or threads; nothing was written.</summary>
    TooManyEntries
}

/// <summary>The outcome of one save: how large the saved index is (or how far writing got) and the limit it was held to.</summary>
internal sealed record MediaLibraryIndexCacheWrite(MediaLibraryIndexCacheWriteStatus Status, long Bytes, long LimitBytes,
    RegularProfileFileIdentity? Identity)
{
    /// <summary>Whether the user should hear about the limit: it stopped the save, or the saved index is near it.</summary>
    internal bool NearOrOverLimit => Status is MediaLibraryIndexCacheWriteStatus.TooLarge
        || Status is MediaLibraryIndexCacheWriteStatus.Written or MediaLibraryIndexCacheWriteStatus.Unchanged
            && Bytes >= LimitBytes * MediaLibraryIndexCache.WarningShare;
}

/// <summary>
/// One page's cache file. Remembers which index the file holds (loaded or last written) together with the file's identity,
/// so visiting the media page again does not rewrite an unchanged index of 100+ MB; a file that was replaced or removed
/// meanwhile is written again. Loads and saves are serialized.
/// </summary>
internal sealed class MediaLibraryIndexCacheFile(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaLibrarySnapshot? _persisted;
    private RegularProfileFileIdentity? _identity;
    private long _bytes;
    // The last index refused for its size, so an unchanged oversized index is not written up to the limit on every visit.
    private MediaLibraryIndexCacheWrite? _refused;
    private MediaLibrarySnapshot? _refusedSnapshot;

    internal string Path => path;
    /// <summary>How many saves were skipped because the file already held the index; lets a test see the skip.</summary>
    internal int SkippedWrites { get; private set; }
    internal int Writes { get; private set; }

    internal async Task<MediaLibrarySnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MediaLibraryIndexCacheRead? read = await MediaLibraryIndexCache.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (read == null) return null;
            (_persisted, _identity, _bytes) = (read.Snapshot, read.Identity, read.Bytes);
            return read.Snapshot;
        }
        finally { _gate.Release(); }
    }

    internal Task<MediaLibraryIndexCacheWrite> SaveAsync(MediaLibrarySnapshot snapshot, long limitBytes, CancellationToken cancellationToken = default) =>
        SaveAsync(snapshot, limitBytes, MediaLibraryIndexCache.DefaultEntryLimit, MediaLibraryIndexCache.DefaultThreadLimit, cancellationToken);

    internal async Task<MediaLibraryIndexCacheWrite> SaveAsync(MediaLibrarySnapshot snapshot, long limitBytes, int entryLimit, int threadLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = MediaLibraryIndexCache.WithoutFolderItems(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long limit = Math.Clamp(limitBytes, 1, MediaLibraryIndexCache.MaximumBytes);
            if (_identity is { } known && MediaLibrarySnapshot.SameContent(_persisted, snapshot) && RegularProfileFile.TryGetIdentity(path) == known)
            {
                SkippedWrites++;
                // The file stays readable even above a lowered limit; the page still says how close it is.
                return new(MediaLibraryIndexCacheWriteStatus.Unchanged, _bytes, limit, known);
            }
            // A refusal for too many entries stands only while the counts still exceed the (possibly raised) limits.
            if (_refused is { } refused && refused.LimitBytes == limit && MediaLibrarySnapshot.SameContent(_refusedSnapshot, snapshot)
                && (refused.Status != MediaLibraryIndexCacheWriteStatus.TooManyEntries || MediaLibraryIndexCache.ExceedsCountLimits(snapshot, entryLimit, threadLimit)))
            {
                SkippedWrites++;
                return refused;
            }
            MediaLibraryIndexCacheWrite result = await MediaLibraryIndexCache.SaveAsync(path, snapshot, limit, entryLimit, threadLimit, cancellationToken).ConfigureAwait(false);
            if (result.Status == MediaLibraryIndexCacheWriteStatus.Written)
            {
                Writes++;
                (_persisted, _identity, _bytes) = (snapshot, result.Identity, result.Bytes);
                (_refused, _refusedSnapshot) = (null, null);
            }
            else if (result.Status is MediaLibraryIndexCacheWriteStatus.TooLarge or MediaLibraryIndexCacheWriteStatus.TooManyEntries)
                (_refused, _refusedSnapshot) = (result, snapshot);
            return result;
        }
        finally { _gate.Release(); }
    }
}
