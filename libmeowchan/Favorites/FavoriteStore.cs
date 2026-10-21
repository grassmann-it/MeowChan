using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Storage;

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

/// <summary>Profile-owned favorite identities. Reads perform no media discovery or file checks.</summary>
public sealed class FavoriteStore
{
    private sealed record Document(int Version, List<FavoriteEntry> Entries);
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
        (IReadOnlyList<FavoriteEntry>)Read().Entries.ToArray());

    public bool Contains(FavoriteAssetKey key)
    {
        Validate(key);
        return _lease.RunExclusive(() => Read().Entries.Any(entry => entry.Key == key));
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
            Document document = Read();
            int index = document.Entries.FindIndex(item => item.Key == entry.Key);
            if (favorite && index >= 0) return false;
            if (!favorite && index < 0) return false;
            if (favorite) document.Entries.Add(entry);
            else document.Entries.RemoveAt(index);
            AtomicJsonFile.WriteAllText(_path, JsonSerializer.Serialize(document), backupCurrentFile: true,
                cancellationToken);
            return true;
        });
    }

    public static FavoriteEntry From(ResourceId thread, MediaAssetReference asset)
        => new(FavoriteAssetKey.From(thread, asset), asset.Length, asset.OriginalFileName ?? asset.RequestedFileName);

    private Document Read()
    {
        if (!File.Exists(_path)) return new Document(1, new List<FavoriteEntry>());
        FileInfo info = new(_path);
        if (info.Length > 8_000_000 || info.LinkTarget != null)
            throw new InvalidDataException("The favorites index is invalid.");
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path)); }
        catch (JsonException ex) { throw new InvalidDataException("The favorites index is invalid.", ex); }
        if (document?.Version != 1 || document.Entries == null || document.Entries.Count > 50_000)
            throw new InvalidDataException("The favorites index is invalid.");
        HashSet<FavoriteAssetKey> seen = new();
        foreach (FavoriteEntry entry in document.Entries)
        {
            if (entry == null || entry.Length < 0) throw new InvalidDataException("The favorites index is invalid.");
            try { Validate(entry.Key); }
            catch (ArgumentException ex) { throw new InvalidDataException("The favorites index is invalid.", ex); }
            if (!seen.Add(entry.Key)) throw new InvalidDataException("The favorites index has duplicate identities.");
        }
        return document;
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
