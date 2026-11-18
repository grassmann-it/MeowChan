using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

internal sealed record PostBookmarkKey(ResourceId Thread, string Post)
{
    internal void Validate()
    {
        ArchiveValidation.ValidateId(Thread);
        if (string.IsNullOrWhiteSpace(Post) || Post.Length > 256 || Post.Any(char.IsControl))
            throw new ArgumentException("A bookmark needs a valid post identity.");
    }
    public override string ToString() => Thread.Source + " /" + Thread.Board + "/ " + Thread.Thread + " >>" + Post;
}
internal sealed record PostBookmark(PostBookmarkKey Key, string Note);
internal sealed record PostBookmarkSnapshot(long Revision, IReadOnlyList<PostBookmark> Bookmarks);

/// <summary>A read-only, owner-bound projection for private bookmark transfer.</summary>
internal sealed class PostBookmarkTransferSnapshot
{
    private readonly ProfileLease _ownerLease;
    private readonly IReadOnlyList<PostBookmark> _bookmarks;

    internal PostBookmarkTransferSnapshot(
        IReadOnlyList<PostBookmark> bookmarks, long revision, string fingerprint, ProfileLease ownerLease)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(ownerLease);
        _bookmarks = Array.AsReadOnly(bookmarks.ToArray());
        Revision = revision;
        Fingerprint = fingerprint;
        _ownerLease = ownerLease;
    }

    internal IReadOnlyList<PostBookmark> Bookmarks => _bookmarks;
    internal long Revision { get; }
    internal string Fingerprint { get; }
    internal bool IsOwnedBy(ProfileLease lease) => ReferenceEquals(_ownerLease, lease);
}
internal sealed class PostBookmarkConflictException : IOException
{
    internal PostBookmarkConflictException() : base("Post bookmarks changed. Reload before saving; your draft has been retained.") { }
}

/// <summary>Explicit local references and user notes. No content copies, history or backup files.</summary>
internal sealed partial class PostBookmarkStore
{
    /// <summary>Default bookmark limit. The limit is a setting; reads accept up to <see cref="ProfileStoreLimits.BookmarkCeiling"/>.</summary>
    internal const int DefaultEntries = ProfileStoreLimits.DefaultBookmarks;
    internal const int MaximumNoteCodepoints = 8_000;
    internal const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private readonly ProfileLease _lease;
    // Prepared batches are owned by an opaque per-store capability, not by the
    // public store object that an internal caller could otherwise reuse.
    private readonly object _batchOwner = new();
    internal string Path { get; }
    private readonly string _pendingPrefix;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal PostBookmarkStore(string profileDirectory, ProfileLease lease)
        : this(profileDirectory, lease, passive: true)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        _lease.RunStoreWrite(admission, CleanupOrdinaryPendingCore);
    }

    private PostBookmarkStore(string profileDirectory, ProfileLease lease, bool passive)
    {
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        Path = System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json");
        _pendingPrefix = "post-bookmarks.json.pending-";
    }

    internal PostBookmarkStore(string profileDirectory, ProfileLease lease, ProfileAdmissionToken admission)
        : this(profileDirectory, lease, passive: true)
    {
        _lease.RunStoreWrite(admission, CleanupOrdinaryPendingCore);
    }

    internal static PostBookmarkStore OpenWithoutCleanup(string profileDirectory, ProfileLease lease)
        => new(profileDirectory, lease, passive: true);

    private bool CleanupOrdinaryPendingCore()
    {
        // Only our own exact GUID temp names, and only while this profile is exclusively leased.
        string[] ownPending = Directory.EnumerateFiles(_lease.ProfileDirectory, _pendingPrefix + "*")
            .Where(file => Guid.TryParseExact(
                System.IO.Path.GetFileName(file)[_pendingPrefix.Length..], "N", out _))
            .ToArray();
        if (ownPending.Length == 0) return true;
        if (HasPrivatePending()) return true;

        // A marked/uncertain primary is recovery evidence. Never erase an exact
        // pending artifact merely because this ordinary store constructor ran.
        bool mayClean = true;
        try
        {
            ParsedFile current = ReadParsedFile(Path);
            mayClean = current.OperationMarker is null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            mayClean = false;
        }
        if (!mayClean) return true;

        foreach (string file in ownPending)
            File.Delete(file);
        return true;
    }

    internal PostBookmarkSnapshot Read() => _lease.RunExclusive(ReadCore);

    // Passive callers must not invoke the constructor's pending-file cleanup.
    internal static PostBookmarkSnapshot ReadOnly(string profileDirectory, ProfileLease lease)
    {
        lease.EnsureMatches(profileDirectory);
        return lease.RunExclusive(() => ReadFile(System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json")));
    }

    internal static PostBookmarkTransferSnapshot ReadTransferSnapshot(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        return lease.RunExclusive(() =>
        {
            ParsedFile parsed = ReadParsedFile(System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json"));
            return new PostBookmarkTransferSnapshot(parsed.Snapshot.Bookmarks, parsed.Snapshot.Revision, parsed.Fingerprint, lease);
        });
    }

    internal static bool IsTransferSnapshotCurrent(
        string profileDirectory, ProfileLease lease, PostBookmarkTransferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Reject an owner mismatch before validating or touching the supplied profile path.
        if (!snapshot.IsOwnedBy(lease)) return false;
        try
        {
            lease.EnsureMatches(profileDirectory);
            return lease.RunExclusive(() =>
            {
                ParsedFile current = ReadParsedFile(System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json"));
                return current.Snapshot.Revision == snapshot.Revision
                    && string.Equals(current.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal);
            });
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal PostBookmarkSnapshot Upsert(PostBookmarkKey key, string note, long expectedRevision,
        bool expectedExists, CancellationToken token = default)
    {
        key.Validate();
        _ = NormalizeNote(note);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return UpsertAdmitted(key, note, expectedRevision, expectedExists, admission, token);
    }

    internal PostBookmarkSnapshot UpsertAdmitted(PostBookmarkKey key, string note, long expectedRevision,
        bool expectedExists, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        key.Validate();
        string normalized = NormalizeNote(note);
        return _lease.RunStoreWrite(admission, () => MutateCore(expectedRevision, entries =>
        {
            int index = entries.FindIndex(entry => entry.Key == key);
            if ((index >= 0) != expectedExists) throw new PostBookmarkConflictException();
            var entry = new PostBookmark(key, normalized);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
        }, token));
    }

    internal PostBookmarkSnapshot Remove(PostBookmarkKey key, long expectedRevision, CancellationToken token = default)
    {
        key.Validate();
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RemoveAdmitted(key, expectedRevision, admission, token);
    }

    internal PostBookmarkSnapshot RemoveAdmitted(PostBookmarkKey key, long expectedRevision,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        key.Validate();
        return _lease.RunStoreWrite(admission, () => MutateCore(expectedRevision, entries =>
        {
            int index = entries.FindIndex(entry => entry.Key == key);
            if (index < 0) throw new PostBookmarkConflictException();
            entries.RemoveAt(index);
        }, token));
    }

    private PostBookmarkSnapshot MutateCore(long expectedRevision, Action<List<PostBookmark>> change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Preserve the existing mutation failure contract for an unusable
        // destination; passive snapshot reads classify it as invalid data.
        if (Directory.Exists(Path))
            throw new IOException("The post-bookmark destination is not a file.");
        EnsureNoPrivatePending();
        ParsedFile parsed = ReadParsedFile(Path);
        PrivateStoreBatchFiles.EnsureUnmarked(parsed.OperationMarker);
        PostBookmarkSnapshot current = parsed.Snapshot;
        if (current.Revision != expectedRevision) throw new PostBookmarkConflictException();
        List<PostBookmark> entries = current.Bookmarks.ToList();
        change(entries);
        // Only growth is refused: with the limit lowered below the saved count, notes and removals still work.
        if (entries.Count > _lease.StoreLimits.Bookmarks && entries.Count > current.Bookmarks.Count)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.Bookmarks, _lease.StoreLimits.Bookmarks,
                "The post bookmark limit is reached.");
        PostBookmarkSnapshot next = new(checked(current.Revision + 1), Array.AsReadOnly(entries.ToArray()));
        byte[] bytes = Serialize(next);
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Post bookmarks exceed 32 MiB.");
        string pending = Path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            // Commit point. Deliberately no .bak, .corrupt or previous-note history.
            try { File.Move(pending, Path, overwrite: true); }
            catch (IOException)
            {
                // An ambiguous replace outcome is resolved from disk before reporting failure.
                if (!File.ReadAllBytes(Path).AsSpan().SequenceEqual(bytes)) throw;
            }
            return next; // Never report cancellation after publication.
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { /* Retry cleanup of this exact own temp on next feature load. */ }
            catch (UnauthorizedAccessException) { /* Never turn an already committed write into a failure. */ }
        }
    }

    private PostBookmarkSnapshot ReadCore() => ReadFile(Path);

    private sealed class ParsedFile(
        PostBookmarkSnapshot snapshot, string fingerprint, int schemaVersion, Guid? instanceId,
        PrivateStoreOperationMarker? operationMarker)
    {
        internal PostBookmarkSnapshot Snapshot { get; } = snapshot;
        internal string Fingerprint { get; } = fingerprint;
        internal int SchemaVersion { get; } = schemaVersion;
        internal Guid? InstanceId { get; } = instanceId;
        internal PrivateStoreOperationMarker? OperationMarker { get; } = operationMarker;
    }

    private static PostBookmarkSnapshot ReadFile(string path) => ReadParsedFile(path).Snapshot;

    private static ParsedFile ReadParsedFile(string path) => ParseBookmarkBytes(ReadBoundedBytes(path));

    private static ParsedFile ParseBookmarkBytes(byte[]? bytes, bool exactNotes = false)
    {
        // Only an explicitly absent file is an empty store. An existing empty file is invalid.
        if (bytes == null)
            return new ParsedFile(new PostBookmarkSnapshot(0, Array.Empty<PostBookmark>()),
                Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), 0, null, null);
        if (bytes.Length == 0)
            throw new InvalidDataException("The post-bookmark document is empty.");

        try
        {
            // Validate all raw bytes, including JSON strings, before the JSON reader can replace
            // invalid UTF-8. The digest below is over these exact bytes, not normalized notes.
            StrictUtf8.GetCharCount(bytes);
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid post-bookmark document.");
            int schema = root.GetProperty("schemaVersion").GetInt32();
            RequireFields(root, schema == 1
                ? new[] { "schemaVersion", "revision", "bookmarks" }
                : new[] { "schemaVersion", "revision", "bookmarks", "instanceId", "operationMarker" });
            if (schema is not (1 or 2))
                throw new InvalidDataException("Unsupported post-bookmark schema; the file is read-only.");
            long revision = root.GetProperty("revision").GetInt64();
            if (revision < 0 || schema == 2 && revision == 0)
                throw new InvalidDataException("Invalid post-bookmark revision.");
            Guid? instance = schema == 2 ? root.GetProperty("instanceId").GetGuid() : null;
            if (schema == 2 && instance == Guid.Empty)
                throw new InvalidDataException("Invalid post-bookmark instance.");
            PrivateStoreOperationMarker? marker = schema == 2
                ? PrivateStoreOperationMarker.Parse(root.GetProperty("operationMarker"),
                    NativePrivateApplyStoreKind.PostBookmarksAndNotes) : null;
            JsonElement items = root.GetProperty("bookmarks");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > ProfileStoreLimits.BookmarkCeiling)
                throw new InvalidDataException("Invalid post-bookmark list.");
            List<PostBookmark> entries = new();
            HashSet<PostBookmarkKey> seen = new();
            foreach (JsonElement item in items.EnumerateArray())
            {
                RequireFields(item, "source", "board", "thread", "post", "note");
                string S(string name) => item.GetProperty(name).GetString() ?? throw new InvalidDataException("Bookmark text is missing.");
                PostBookmarkKey key = new(new ResourceId(S("source"), S("board"), S("thread")), S("post"));
                key.Validate();
                if (!seen.Add(key)) throw new InvalidDataException("Duplicate post-bookmark identity.");
                string note = S("note");
                string normalized = NormalizeNote(note);
                if ((exactNotes || schema == 2) && !string.Equals(note, normalized, StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid post-bookmark document.");
                if (exactNotes || schema == 2) ValidateBatchKey(key);
                entries.Add(new PostBookmark(key, normalized));
            }
            return new ParsedFile(
                new PostBookmarkSnapshot(revision, Array.AsReadOnly(entries.ToArray())),
                Convert.ToHexString(SHA256.HashData(bytes)), schema, instance, marker);
        }
        catch (Exception error) when (error is JsonException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            // Parser/decoder messages may quote private text. Do not retain them as inner errors.
            throw new InvalidDataException("Invalid post-bookmark document.");
        }
    }

    private static byte[]? ReadBoundedBytes(string path)
    {
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(path);
            long length = file.Length;
            if (length < 0 || length > MaximumDocumentBytes)
                throw new InvalidDataException("Post bookmarks exceed the size limit.");
            byte[] bytes = new byte[checked((int)length)];
            file.ReadExactly(bytes);
            // Do not parse a length-sampled prefix if the file grew. At most one overflow
            // byte is probed; no unbounded stream parser or CopyTo allocation is involved.
            if (file.ReadByte() != -1 || file.Length != length)
                throw new InvalidDataException("The post-bookmark document changed while it was being read.");
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException)
        {
            // IO exceptions can contain the absolute profile path. Keep read diagnostics opaque.
            throw new IOException("The post-bookmark document could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("The post-bookmark document could not be read.");
        }
    }

    private static void RequireFields(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a bookmark object.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty field in item.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new InvalidDataException("Unknown or duplicate bookmark field.");
        if (seen.Count != names.Length) throw new InvalidDataException("Missing bookmark field.");
    }

    internal static string NormalizeNote(string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        string normalized = note.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int count = 0;
        for (int i = 0; i < normalized.Length;)
        {
            if (Rune.DecodeFromUtf16(normalized.AsSpan(i), out Rune rune, out int consumed) != OperationStatus.Done)
                throw new ArgumentException("The note contains invalid Unicode.");
            if (Rune.IsControl(rune) && rune.Value is not (9 or 10))
                throw new ArgumentException("The note contains an unsupported control character.");
            if (++count > MaximumNoteCodepoints) throw new ArgumentException("A note may contain at most 8,000 Unicode codepoints.");
            i += consumed;
        }
        return normalized;
    }

    private static byte[] Serialize(PostBookmarkSnapshot snapshot)
    {
        using MemoryStream memory = new();
        using (Utf8JsonWriter json = new(memory))
        {
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 1); json.WriteNumber("revision", snapshot.Revision);
            json.WriteStartArray("bookmarks");
            foreach (PostBookmark entry in snapshot.Bookmarks)
            {
                json.WriteStartObject();
                json.WriteString("source", entry.Key.Thread.Source); json.WriteString("board", entry.Key.Thread.Board);
                json.WriteString("thread", entry.Key.Thread.Thread); json.WriteString("post", entry.Key.Post);
                json.WriteString("note", entry.Note); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        return memory.ToArray();
    }
}
