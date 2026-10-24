using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Favorites;

/// <summary>A complete, immutable set of manually marked thread identities in one profile.</summary>
public sealed record ThreadFavoriteSnapshot(IReadOnlyList<ResourceId> Threads, long Revision, string Fingerprint);
public sealed record ThreadFavoriteCommit(ResourceId Thread, bool Favorite, bool Changed, ThreadFavoriteSnapshot Snapshot);

/// <summary>Profile-owned thread marks, independent of media, archive membership and watch state.</summary>
public sealed class ThreadFavoriteStore
{
    public const int MaximumEntries = 50_000;
    public const int MaximumBytes = 8_000_000;
    private sealed record Document(int Version, long Revision, List<ResourceId> Threads);
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
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            (Document document, ThreadFavoriteSnapshot current) = Read();
            int index = document.Threads.FindIndex(id => id == thread);
            if (favorite == (index >= 0)) return new ThreadFavoriteCommit(thread, favorite, false, current);
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
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException) { }
            AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
            return new ThreadFavoriteCommit(thread, favorite, true, Snapshot(document, bytes));
        });
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
        RejectLink(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("The thread favorites index exceeds its size limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Document? document;
        try
        {
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            document = JsonSerializer.Deserialize<Document>(json);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("The thread favorites index is invalid.", error); }
        if (document?.Version != 1 || document.Revision < 0 || document.Threads == null
            || document.Threads.Count > MaximumEntries)
            throw new InvalidDataException("The thread favorites index version, revision or entry count is invalid.");
        HashSet<ResourceId> seen = new();
        foreach (ResourceId id in document.Threads)
        {
            try { Validate(id); }
            catch (ArgumentException error)
            { throw new InvalidDataException("The thread favorites index contains an invalid identity.", error); }
            if (!seen.Add(id)) throw new InvalidDataException("The thread favorites index contains duplicate identities.");
        }
        return (document, Snapshot(document, bytes));
    }

    private static ThreadFavoriteSnapshot Snapshot(Document document, byte[] bytes) =>
        new(Array.AsReadOnly(document.Threads.ToArray()), document.Revision, Convert.ToHexString(SHA256.HashData(bytes)));

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget != null || Directory.Exists(path))
            throw new InvalidDataException("The thread favorites index path is not a regular file.");
    }
}
