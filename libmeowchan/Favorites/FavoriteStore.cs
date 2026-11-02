using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Favorites;

/// <summary>A particular committed original, independent of its current filesystem location.</summary>
public sealed record FavoriteAssetKey(ResourceId Thread, string MediaIdentifier, string Variant, string Sha256)
{
    public static FavoriteAssetKey From(ResourceId thread, MediaAssetReference asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new FavoriteAssetKey(thread, asset.MediaIdentifier, asset.Variant, asset.Sha256.ToLowerInvariant());
    }
}

public sealed record FavoriteEntry(FavoriteAssetKey Key, long Length, string? OriginalFileName);

/// <summary>Opaque, owner-bound read fence; never serialize this local authority into a transfer.</summary>
internal sealed class FavoriteStoreSnapshot
{
    private readonly ProfileLease _owner;

    internal FavoriteStoreSnapshot(ProfileLease owner, IReadOnlyList<FavoriteEntry> entries,
        string fingerprint, Guid? instanceId, long revision)
    {
        _owner = owner;
        Entries = Array.AsReadOnly(entries.ToArray());
        Fingerprint = fingerprint;
        InstanceId = instanceId;
        Revision = revision;
    }

    internal IReadOnlyList<FavoriteEntry> Entries { get; }
    internal string Fingerprint { get; }
    internal Guid? InstanceId { get; }
    internal long Revision { get; }
    internal bool IsOwnedBy(ProfileLease owner) => ReferenceEquals(_owner, owner);
}

/// <summary>Profile-owned favorite identities. Reads perform no media discovery or file checks.</summary>
public sealed partial class FavoriteStore
{
    internal const int MaximumBytes = 8_000_000;
    internal const int MaximumEntries = 50_000;
    private sealed record Document(int Version, List<FavoriteEntry> Entries, Guid? InstanceId, long Revision,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PrivateStoreOperationMarker? OperationMarker = null);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProfileLease _lease;
    private readonly string _path;

    public FavoriteStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "media-favorites.json");
    }

    public IReadOnlyList<FavoriteEntry> List() => _lease.RunExclusive(() =>
        (IReadOnlyList<FavoriteEntry>)Read().Document.Entries.ToArray());

    public bool Contains(FavoriteAssetKey key)
    {
        Validate(key);
        return _lease.RunExclusive(() => Read().Document.Entries.Any(entry => entry.Key == key));
    }

    internal FavoriteStoreSnapshot ReadTransferSnapshot() => _lease.RunExclusive(() =>
    {
        (Document document, string fingerprint) = Read();
        return new FavoriteStoreSnapshot(_lease, document.Entries, fingerprint, document.InstanceId, document.Revision);
    });

    internal bool IsTransferSnapshotCurrent(FavoriteStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return _lease.RunExclusive(() =>
        {
            if (!snapshot.IsOwnedBy(_lease)) return false;
            (Document document, string fingerprint) = Read();
            return snapshot.InstanceId == document.InstanceId && snapshot.Revision == document.Revision
                && string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal);
        });
    }

    /// <summary>Updates one identity atomically; unmarking does not touch any media file.</summary>
    public bool SetFavorite(FavoriteEntry entry, bool favorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry.Key);
        if (entry.Length < 0) throw new ArgumentOutOfRangeException(nameof(entry));
        return _lease.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document document = Read().Document;
            PrivateStoreBatchFiles.EnsureUnmarked(document.OperationMarker);
            int index = document.Entries.FindIndex(item => item.Key == entry.Key);
            if (favorite == (index >= 0))
            {
                EnsureNoopBackupUnmarked();
                return false;
            }
            if (favorite)
            {
                if (document.Entries.Count >= MaximumEntries)
                    throw new InvalidDataException("The favorites index has reached its entry limit.");
                document.Entries.Add(entry);
            }
            else document.Entries.RemoveAt(index);
            Write(document, cancellationToken);
            return true;
        });
    }

    /// <summary>Removes all favorites of deleted archive threads in one profile update.</summary>
    public int RemoveThreads(IReadOnlyCollection<ResourceId> threads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        HashSet<ResourceId> selected = threads.ToHashSet();
        return _lease.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document document = Read().Document;
            PrivateStoreBatchFiles.EnsureUnmarked(document.OperationMarker);
            int removed = document.Entries.RemoveAll(entry => selected.Contains(entry.Key.Thread));
            if (removed > 0)
                Write(document, cancellationToken);
            else
                EnsureNoopBackupUnmarked();
            return removed;
        });
    }

    public static FavoriteEntry From(ResourceId thread, MediaAssetReference asset)
        => new(FavoriteAssetKey.From(thread, asset), asset.Length, asset.OriginalFileName ?? asset.RequestedFileName);

    private void EnsureNoopBackupUnmarked()
    {
        PrivateStoreOperationMarker? marker = null;
        try { marker = ReadDocument(_path + ".bak").Document.OperationMarker; }
        catch (FileNotFoundException) { }
        catch (InvalidDataException)
        {
            // No bytes can be replaced on this path. A damaged legacy backup
            // may coexist with an unchanged valid primary, but a valid marker
            // must not be swallowed as a parse error.
        }
        PrivateStoreBatchFiles.EnsureUnmarked(marker);
    }

    private void Write(Document document, CancellationToken cancellationToken)
    {
        PrivateStoreBatchFiles.EnsureUnmarked(document.OperationMarker);
        // Only a real mutation upgrades v1. Retain the instance for every v2 write,
        // including the write that removes the last entry (no delete/recreate ABA).
        document = document with
        {
            Version = 2,
            InstanceId = document.InstanceId ?? Guid.NewGuid(),
            Revision = checked(document.Revision + 1)
        };
        string json = JsonSerializer.Serialize(document);
        if (StrictUtf8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("The favorites index has reached its size limit.");
        // Never rotate away unsupported or damaged backup evidence. Keep the
        // established IO-failure contract for a directory blocking publication.
        if (Directory.Exists(_path + ".bak"))
            throw new IOException("The favorites backup destination is not a file.");
        try { PrivateStoreBatchFiles.EnsureUnmarked(ReadDocument(_path + ".bak").Document.OperationMarker); }
        catch (FileNotFoundException) { }
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, cancellationToken);
    }

    private (Document Document, string Fingerprint) Read()
    {
        try { return ReadDocument(_path); }
        catch (FileNotFoundException)
        {
            // Only explicit absence of BOTH files means empty. In particular, do
            // not restore the backup: it may resurrect a deliberately removed mark.
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException)
            {
                return (new Document(1, new(), null, 0), "missing");
            }
            throw new InvalidDataException("The favorites index is missing but a backup exists. Explicit recovery is required.");
        }
    }

    private static (Document Document, string Fingerprint) ReadDocument(string path)
    {
        try { return ReadDocumentCore(path); }
        catch (FileNotFoundException)
        { throw new FileNotFoundException("The favorites index file is missing."); }
        catch (UnauthorizedAccessException)
        { throw new UnauthorizedAccessException("The favorites index could not be read."); }
        catch (InvalidDataException) { throw; }
        catch (IOException)
        { throw new IOException("The favorites index could not be read."); }
    }

    private static (Document Document, string Fingerprint) ReadDocumentCore(string path)
    {
        using FileStream stream = RegularProfileFile.OpenRead(path);
        long length = stream.Length;
        if (length > MaximumBytes)
            throw new InvalidDataException("The favorites index exceeds its size limit.");
        byte[] bytes = new byte[checked((int)length)];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException)
        { throw new InvalidDataException("The favorites index changed while reading."); }
        // ReadExactly alone would silently accept an initial prefix if the file grew.
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("The favorites index changed while reading.");

        return (ParseDocument(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    // Reuse this exact parser for read-only primary/backup inventory consumers;
    // accepting v2 there must not relax any other store schema or skip its fence.
    internal static void ValidateDocumentBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _ = ParseDocument(bytes);
    }

    private static Document ParseDocument(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The favorites index exceeds its size limit.");
        Document document;
        try
        {
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            CheckObject(root, ["Version", "Entries"], ["InstanceId", "Revision", "OperationMarker"]);
            int version = root.GetProperty("Version").GetInt32();
            Guid? instanceId = null;
            long revision = 0;
            PrivateStoreOperationMarker? operationMarker = null;
            if (version is 2 or 3)
            {
                instanceId = root.GetProperty("InstanceId").GetGuid();
                revision = root.GetProperty("Revision").GetInt64();
                if (instanceId == Guid.Empty || revision < 1)
                    throw new InvalidDataException("The favorites index fence is invalid.");
                bool hasMarker = root.TryGetProperty("OperationMarker", out JsonElement marker);
                if (version == 3)
                {
                    if (!hasMarker) throw new InvalidDataException("The favorites index marker is missing.");
                    operationMarker = PrivateStoreOperationMarker.Parse(marker, NativePrivateApplyStoreKind.MediaFavorites);
                }
                else if (hasMarker)
                    throw new InvalidDataException("The favorites index version is unsupported.");
            }
            else if (version != 1 || root.TryGetProperty("InstanceId", out _)
                || root.TryGetProperty("Revision", out _) || root.TryGetProperty("OperationMarker", out _))
                throw new InvalidDataException("The favorites index version is unsupported.");

            JsonElement entries = root.GetProperty("Entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("The favorites index entry count is invalid.");
            List<FavoriteEntry> values = new(entries.GetArrayLength());
            HashSet<FavoriteAssetKey> seen = new();
            foreach (JsonElement item in entries.EnumerateArray())
            {
                CheckObject(item, ["Key", "Length"], ["OriginalFileName"]);
                JsonElement key = item.GetProperty("Key");
                CheckObject(key, ["Thread", "MediaIdentifier", "Variant", "Sha256"], []);
                JsonElement thread = key.GetProperty("Thread");
                CheckObject(thread, ["Source", "Board", "Thread"], []);
                FavoriteAssetKey identity = new(new ResourceId(thread.GetProperty("Source").GetString()!,
                    thread.GetProperty("Board").GetString()!, thread.GetProperty("Thread").GetString()),
                    key.GetProperty("MediaIdentifier").GetString()!, key.GetProperty("Variant").GetString()!,
                    key.GetProperty("Sha256").GetString()!);
                Validate(identity);
                long size = item.GetProperty("Length").GetInt64();
                if (size < 0 || !seen.Add(identity))
                    throw new InvalidDataException("The favorites index contains invalid or duplicate entries.");
                string? name = item.TryGetProperty("OriginalFileName", out JsonElement original) ? original.GetString() : null;
                values.Add(new FavoriteEntry(identity, size, name));
            }
            document = new Document(version, values, instanceId, revision, operationMarker);
        }
        catch (Exception error) when (error is JsonException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            // Do not expose parser paths, private filenames or entry values through
            // an exception chain. The bounded store error is intentionally opaque.
            throw new InvalidDataException("The favorites index is invalid.");
        }
        return document;
    }

    private static void CheckObject(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The favorites index contains an invalid object.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal))
                || !seen.Add(property.Name))
                throw new InvalidDataException("The favorites index contains unknown or duplicate fields.");
        if (required.Any(name => !seen.Contains(name)))
            throw new InvalidDataException("The favorites index contains missing fields.");
    }

    internal static void Validate(FavoriteAssetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Thread == null || string.IsNullOrWhiteSpace(key.Thread.Source)
            || string.IsNullOrWhiteSpace(key.Thread.Board) || string.IsNullOrWhiteSpace(key.Thread.Thread)
            || string.IsNullOrWhiteSpace(key.MediaIdentifier) || string.IsNullOrWhiteSpace(key.Variant)
            || key.Sha256?.Length != 64 || !key.Sha256.All(Uri.IsHexDigit)
            || key.Sha256 != key.Sha256.ToLowerInvariant())
            throw new ArgumentException("A favorite needs a thread, media identity, variant and SHA-256.", nameof(key));
    }
}
