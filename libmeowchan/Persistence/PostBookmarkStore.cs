using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;

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
internal sealed class PostBookmarkConflictException : IOException
{
    internal PostBookmarkConflictException() : base("Post bookmarks changed. Reload before saving; your draft has been retained.") { }
}

/// <summary>Explicit local references and user notes. No content copies, history or backup files.</summary>
internal sealed class PostBookmarkStore
{
    internal const int MaximumEntries = 10_000;
    internal const int MaximumNoteCodepoints = 8_000;
    internal const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private readonly ProfileLease _lease;
    internal string Path { get; }
    private readonly string _pendingPrefix;

    internal PostBookmarkStore(string profileDirectory, ProfileLease lease)
    {
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        Path = System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json");
        _pendingPrefix = "post-bookmarks.json.pending-";
        // Only our own exact GUID temp names, and only while this profile is exclusively leased.
        _lease.RunExclusive(() =>
        {
            foreach (string file in Directory.EnumerateFiles(lease.ProfileDirectory, _pendingPrefix + "*"))
                if (Guid.TryParseExact(System.IO.Path.GetFileName(file)[_pendingPrefix.Length..], "N", out _))
                    File.Delete(file);
            return true;
        });
    }

    internal PostBookmarkSnapshot Read() => _lease.RunExclusive(ReadCore);

    // Passive callers must not invoke the constructor's pending-file cleanup.
    internal static PostBookmarkSnapshot ReadOnly(string profileDirectory, ProfileLease lease)
    {
        lease.EnsureMatches(profileDirectory);
        return lease.RunExclusive(() => ReadFile(System.IO.Path.Combine(lease.ProfileDirectory, "post-bookmarks.json")));
    }

    internal PostBookmarkSnapshot Upsert(PostBookmarkKey key, string note, long expectedRevision,
        bool expectedExists, CancellationToken token = default)
    {
        key.Validate();
        string normalized = NormalizeNote(note);
        return Mutate(expectedRevision, entries =>
        {
            int index = entries.FindIndex(entry => entry.Key == key);
            if ((index >= 0) != expectedExists) throw new PostBookmarkConflictException();
            var entry = new PostBookmark(key, normalized);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
        }, token);
    }

    internal PostBookmarkSnapshot Remove(PostBookmarkKey key, long expectedRevision, CancellationToken token = default)
    {
        key.Validate();
        return Mutate(expectedRevision, entries =>
        {
            int index = entries.FindIndex(entry => entry.Key == key);
            if (index < 0) throw new PostBookmarkConflictException();
            entries.RemoveAt(index);
        }, token);
    }

    private PostBookmarkSnapshot Mutate(long expectedRevision, Action<List<PostBookmark>> change, CancellationToken token)
        => _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            PostBookmarkSnapshot current = ReadCore();
            if (current.Revision != expectedRevision) throw new PostBookmarkConflictException();
            List<PostBookmark> entries = current.Bookmarks.ToList();
            change(entries);
            if (entries.Count > MaximumEntries) throw new InvalidDataException("At most 10,000 post bookmarks are supported.");
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
        });

    private PostBookmarkSnapshot ReadCore() => ReadFile(Path);

    private static PostBookmarkSnapshot ReadFile(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget != null) throw new InvalidDataException("Post bookmarks must not be a symbolic link.");
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumDocumentBytes)
                throw new InvalidDataException("Post bookmarks exceed the size limit or are a symbolic link.");
            using JsonDocument json = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = json.RootElement;
            RequireFields(root, "schemaVersion", "revision", "bookmarks");
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported post-bookmark schema; the file is read-only.");
            long revision = root.GetProperty("revision").GetInt64();
            if (revision < 0) throw new InvalidDataException("Invalid post-bookmark revision.");
            JsonElement items = root.GetProperty("bookmarks");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaximumEntries)
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
                entries.Add(new PostBookmark(key, NormalizeNote(S("note"))));
            }
            return new PostBookmarkSnapshot(revision, Array.AsReadOnly(entries.ToArray()));
        }
        catch (FileNotFoundException) { return new PostBookmarkSnapshot(0, Array.Empty<PostBookmark>()); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException("Invalid post-bookmark file: " + path, error); }
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
