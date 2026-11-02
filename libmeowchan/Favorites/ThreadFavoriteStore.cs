using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.Transfer;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Favorites;

/// <summary>A complete, immutable set of manually marked thread identities in one profile.</summary>
public sealed record ThreadFavoriteSnapshot(IReadOnlyList<ResourceId> Threads, long Revision, string Fingerprint)
{
    private IReadOnlyList<ResourceId> _threads = CopyThreads(Threads);
    public IReadOnlyList<ResourceId> Threads
    {
        get => _threads;
        init => _threads = CopyThreads(value);
    }

    private static IReadOnlyList<ResourceId> CopyThreads(IReadOnlyList<ResourceId> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        return Array.AsReadOnly(threads.ToArray());
    }
}
public sealed record ThreadFavoriteCommit(ResourceId Thread, bool Favorite, bool Changed, ThreadFavoriteSnapshot Snapshot);

/// <summary>Profile-owned thread marks, independent of media, archive membership and watch state.</summary>
public sealed partial class ThreadFavoriteStore
{
    public const int MaximumEntries = 50_000;
    public const int MaximumBytes = 8_000_000;
    private sealed record Document(int Version, long Revision, List<ResourceId> Threads,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? InstanceId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PrivateStoreOperationMarker? OperationMarker = null);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProfileLease _lease;
    private readonly string _path;

    public ThreadFavoriteStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "thread-favorites.json");
    }

    public ThreadFavoriteSnapshot ReadSnapshot() => _lease.RunExclusive(() => Read().Snapshot);

    /// <summary>Commits the entire new set before reporting the new mark. Unmarking never deletes content.</summary>
    public ThreadFavoriteCommit Set(ResourceId thread, bool favorite, CancellationToken token = default)
    {
        Validate(thread);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetAdmitted(thread, favorite, admission, token);
    }

    internal ThreadFavoriteCommit SetAdmitted(ResourceId thread, bool favorite,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        Validate(thread);
        return _lease.RunStoreWrite(admission, () => SetCore(thread, favorite, token));
    }

    private ThreadFavoriteCommit SetCore(ResourceId thread, bool favorite, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        (Document document, ThreadFavoriteSnapshot current) = Read();
        PrivateStoreBatchFiles.EnsureUnmarked(document.OperationMarker);
        int index = document.Threads.FindIndex(id => id == thread);
        bool unchanged = favorite == (index >= 0);
        PrivateStoreOperationMarker? backupMarker = null;
        try { backupMarker = ReadDocument(_path + ".bak").Document.OperationMarker; }
        catch (FileNotFoundException) { }
        catch (InvalidDataException) when (unchanged)
        {
            // An invalid legacy backup is never rotated by a no-op. Keep
            // that established behavior, but a valid operation marker below
            // still bars even same-value calls until explicit recovery.
        }
        PrivateStoreBatchFiles.EnsureUnmarked(backupMarker);
        if (unchanged) return new ThreadFavoriteCommit(thread, favorite, false, current);
        if (favorite)
        {
            if (document.Threads.Count >= MaximumEntries)
                throw new InvalidDataException("The thread favorites index has reached its entry limit.");
            document.Threads.Add(thread);
        }
        else document.Threads.RemoveAt(index);
        document = document with { Revision = checked(document.Revision + 1) };
        string json = JsonSerializer.Serialize(document);
        byte[] bytes = StrictUtf8.GetBytes(json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The thread favorites index has reached its size limit.");
        // A future or malformed backup must never be rotated away by an older writer.
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
        return new ThreadFavoriteCommit(thread, favorite, true, Snapshot(document, bytes));
    }

    public static void Validate(ResourceId thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (string.IsNullOrWhiteSpace(thread.Source) || string.IsNullOrWhiteSpace(thread.Board)
            || string.IsNullOrWhiteSpace(thread.Thread))
            throw new ArgumentException("A thread favorite needs a source, board and thread identity.", nameof(thread));
        try
        {
            StrictUtf8.GetByteCount(thread.Source);
            StrictUtf8.GetByteCount(thread.Board);
            StrictUtf8.GetByteCount(thread.Thread);
        }
        catch (EncoderFallbackException error)
        { throw new ArgumentException("A thread favorite identity must contain valid Unicode.", nameof(thread), error); }
    }

    private (Document Document, ThreadFavoriteSnapshot Snapshot) Read()
    {
        try { return ReadDocument(_path); }
        catch (FileNotFoundException)
        {
            // File.Exists also returns false for some access and IO failures. Only an
            // explicitly missing primary AND backup establish a new, empty index.
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException)
            {
                Document empty = new(1, 0, new());
                return (empty, Snapshot(empty, Array.Empty<byte>()));
            }
            throw new InvalidDataException("The thread favorites index is missing but a backup exists. Explicit recovery is required.");
        }
    }

    private static (Document Document, ThreadFavoriteSnapshot Snapshot) ReadDocument(string path)
    {
        try { return ReadDocumentCore(path); }
        // Paths and parser exception messages can expose private profile values.
        // Preserve the absence signal for Read(), but do not attach raw exceptions.
        catch (FileNotFoundException)
        { throw new FileNotFoundException("The thread favorites index file is missing."); }
        catch (UnauthorizedAccessException)
        { throw new UnauthorizedAccessException("The thread favorites index could not be read."); }
        catch (InvalidDataException) { throw; }
        catch (IOException)
        { throw new IOException("The thread favorites index could not be read."); }
    }

    private static (Document Document, ThreadFavoriteSnapshot Snapshot) ReadDocumentCore(string path)
    {
        using FileStream stream = RegularProfileFile.OpenRead(path);
        long length = stream.Length;
        if (length > MaximumBytes)
            throw new InvalidDataException("The thread favorites index exceeds its size limit.");

        byte[] bytes = new byte[checked((int)length)];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException)
        { throw new InvalidDataException("The thread favorites index changed while reading."); }
        // ReadExactly only proves that the original prefix was available. Check both
        // EOF and the length again so a concurrent growth cannot be accepted.
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("The thread favorites index changed while reading.");

        Document document = ParseDocument(bytes);
        return (document, Snapshot(document, bytes));
    }

    private static Document ParseDocument(byte[] bytes)
    {
        Document document;
        try
        {
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            int version = root.GetProperty("Version").GetInt32();
            CheckObject(root, version == 2
                ? ["Version", "Revision", "Threads", "InstanceId", "OperationMarker"]
                : ["Version", "Revision", "Threads"]);
            long revision = root.GetProperty("Revision").GetInt64();
            Guid? instance = version == 2 ? root.GetProperty("InstanceId").GetGuid() : null;
            PrivateStoreOperationMarker? marker = version == 2
                ? PrivateStoreOperationMarker.Parse(root.GetProperty("OperationMarker"), NativePrivateApplyStoreKind.ThreadFavorites) : null;
            if (version is not (1 or 2) || revision < 0 || version == 2 && (instance == Guid.Empty || revision == 0))
                throw new InvalidDataException("The thread favorites index version or revision is invalid.");

            JsonElement threads = root.GetProperty("Threads");
            if (threads.ValueKind != JsonValueKind.Array || threads.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("The thread favorites index entry count is invalid.");

            List<ResourceId> values = new(threads.GetArrayLength());
            HashSet<ResourceId> seen = new();
            foreach (JsonElement item in threads.EnumerateArray())
            {
                CheckObject(item, ["Source", "Board", "Thread"]);
                ResourceId identity = new(
                    RequiredString(item.GetProperty("Source")),
                    RequiredString(item.GetProperty("Board")),
                    RequiredString(item.GetProperty("Thread")));
                Validate(identity);
                if (!seen.Add(identity))
                    throw new InvalidDataException("The thread favorites index contains duplicate identities.");
                values.Add(identity);
            }

            document = new Document(version, revision, values, instance, marker);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException("The thread favorites index is invalid."); }

        return document;
    }

    private static string RequiredString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
            throw new InvalidDataException("The thread favorites index contains an invalid identity.");
        return text;
    }

    private static void CheckObject(JsonElement value, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The thread favorites index contains an invalid object.");

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("The thread favorites index contains unknown or duplicate fields.");

        if (required.Any(name => !names.Contains(name)))
            throw new InvalidDataException("The thread favorites index contains missing fields.");
    }

    private static ThreadFavoriteSnapshot Snapshot(Document document, byte[] bytes) =>
        new(document.Threads, document.Revision, Convert.ToHexString(SHA256.HashData(bytes)));

}
