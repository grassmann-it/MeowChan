using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace oChan.Storage;

public enum MediaManifestStatus { Missing, Available, Corrupt }

/// <summary>
/// One manifest-published asset. Path is the actual published file, including any conflict suffix.
/// Length and Sha256 describe its bytes at commit time; use OpenVerifiedReadAsync before consuming it.
/// IsCurrent is the last committed stand for RequestedFileName in this manifest view, not the latest
/// observed post or a live pointer. The current store records originals.
/// </summary>
public sealed record MediaAssetReference(string AssetId, string MediaIdentifier, string Variant,
    string RequestedFileName, string Path, long Length, string Sha256, string SourceUrl,
    string? OriginalFileName, bool IsCurrent);

/// <summary>
/// An immutable manifest view independent of providers, watches and the profile writer.
/// Later media commits do not change its pages, current flags or asset references.
/// </summary>
public sealed class MediaAssetSnapshot
{
    private readonly MediaAssetReference[] _assets;
    private readonly Dictionary<string, MediaAssetReference> _byId;

    internal MediaAssetSnapshot(string directory, MediaManifestStatus status, int? manifestVersion,
        string? generation, IEnumerable<MediaAssetReference> assets, string? warning = null)
    {
        Directory = directory;
        Status = status;
        ManifestVersion = manifestVersion;
        Generation = generation;
        Warning = warning;
        _assets = assets.OrderBy(asset => asset.MediaIdentifier, StringComparer.Ordinal)
            .ThenBy(asset => asset.RequestedFileName, StringComparer.Ordinal)
            .ThenBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray();
        _byId = _assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
    }

    public string Directory { get; }
    public MediaManifestStatus Status { get; }
    public int? ManifestVersion { get; }
    /// <summary>SHA-256 of the exact manifest bytes read, or null when no valid manifest was loaded.</summary>
    public string? Generation { get; }
    public string? Warning { get; }
    public int Count => _assets.Length;

    /// <summary>Returns at most 500 references from this same immutable view. No filesystem work occurs.</summary>
    public IReadOnlyList<MediaAssetReference> GetPage(int offset = 0, int limit = 100)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset >= _assets.Length) return Array.Empty<MediaAssetReference>();
        int count = Math.Min(limit, _assets.Length - offset);
        MediaAssetReference[] page = new MediaAssetReference[count];
        Array.Copy(_assets, offset, page, 0, count);
        return Array.AsReadOnly(page);
    }

    public MediaAssetReference? FindById(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        return _byId.GetValueOrDefault(assetId);
    }

    /// <summary>
    /// Opens an asset belonging to this view, verifies its length and complete SHA-256, and returns
    /// that same handle positioned at zero. The caller disposes the stream. Missing/modified assets
    /// fail without repair or network access; external programs ignoring file sharing remain outside
    /// the store's coordination contract. Symlink files are not followed.
    /// </summary>
    public async Task<Stream> OpenVerifiedReadAsync(string assetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaAssetReference asset = FindById(assetId)
            ?? throw new ArgumentException("The asset does not belong to this manifest snapshot.", nameof(assetId));
        if ((File.GetAttributes(asset.Path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A recorded media asset must not be a symbolic link.");
        FileStream stream = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != asset.Length)
                throw new InvalidDataException("The recorded media asset has changed length.");
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(digest), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recorded media asset no longer matches its committed checksum.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Reads only manifest-declared assets. It never creates directories, locks, imports or repairs files.</summary>
public static class MediaAssetReader
{
    /// <summary>
    /// Reads one complete manifest of at most 32 MiB. Version 1 is projected without rewriting it.
    /// Missing/corrupt manifests are explicit states; unsupported versions and IO failures throw.
    /// Unknown files, including pre-existing conflict files, are not assigned to any media identity.
    /// </summary>
    public static Task<MediaAssetSnapshot> OpenAsync(string threadDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadDirectory);
        return MediaStore.ReadAssetSnapshotAsync(Path.GetFullPath(threadDirectory), cancellationToken);
    }
}

public sealed partial class MediaStore
{
    internal static async Task<MediaAssetSnapshot> ReadAssetSnapshotAsync(string directory, CancellationToken cancellationToken)
    {
        ManifestRead read = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (read.Missing)
            return new MediaAssetSnapshot(directory, MediaManifestStatus.Missing, null, null,
                Array.Empty<MediaAssetReference>());
        if (read.Corrupt)
            return CorruptAssetSnapshot(directory);

        Dictionary<string, string> currentAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, MediaManifestEntry> current in read.Manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidAssetEntry(directory, current.Key, current.Value)) return CorruptAssetSnapshot(directory);
            currentAssets.Add(current.Key, GetAssetId(current.Key, current.Value));
        }
        List<MediaAssetReference> assets = new List<MediaAssetReference>(read.Manifest.Assets.Count);
        foreach (KeyValuePair<string, MediaManifestAsset> pair in read.Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaManifestAsset? asset = pair.Value;
            if (asset == null || !IsValidAssetEntry(directory, asset.RequestedFileName, asset.Entry)
                || pair.Key != GetAssetId(asset.RequestedFileName, asset.Entry))
                return CorruptAssetSnapshot(directory);
            MediaManifestEntry entry = asset.Entry;
            bool current = currentAssets.TryGetValue(asset.RequestedFileName, out string? currentId)
                && currentId == pair.Key;
            assets.Add(new MediaAssetReference(pair.Key, entry.MediaIdentifier, "original", asset.RequestedFileName,
                Path.Combine(directory, entry.FileName), entry.Length, entry.Sha256.ToLowerInvariant(),
                entry.SourceUrl, entry.OriginalFileName, current));
        }
        return new MediaAssetSnapshot(directory, MediaManifestStatus.Available, read.SourceVersion,
            read.Generation, assets);
    }

    private static MediaAssetSnapshot CorruptAssetSnapshot(string directory)
        => new MediaAssetSnapshot(directory, MediaManifestStatus.Corrupt, null, null,
            Array.Empty<MediaAssetReference>(), "The media manifest is invalid; no assets were inferred or repaired.");

    private static bool IsValidAssetEntry(string directory, string requestedName, MediaManifestEntry? entry)
    {
        if (!TryGetEntryPath(directory, requestedName, out _) || entry == null
            || !TryGetEntryPath(directory, entry.FileName, out _) || string.IsNullOrWhiteSpace(entry.MediaIdentifier)
            || entry.Length < 0 || entry.Sha256 == null || entry.Sha256.Length != 64
            || !Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out _)) return false;
        foreach (char character in entry.Sha256)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }
}
