using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace MeowChan.Storage;

internal enum MediaSnapshotHashSearchStatus { Available, MissingManifest, CorruptManifest }

/// <summary>
/// Creates an immutable, in-memory query over one already captured media manifest snapshot.
/// The digest must be a complete SHA-256 hex string; provider MD5/SHA-1 claims are not searched.
/// </summary>
internal static class MediaSnapshotHashSearch
{
    internal const int MaximumPageSize = 500;

    internal static MediaSnapshotHashQuery CreateQuery(MediaAssetSnapshot snapshot, string sha256)
        => new(snapshot, sha256);
}

/// <summary>
/// An exact SHA-256 query bound to one immutable snapshot. Repeated searches see that same
/// captured manifest view even if a later manifest is published. No path is opened here.
/// </summary>
internal sealed class MediaSnapshotHashQuery
{
    private readonly MediaAssetSnapshot _snapshot;

    internal MediaSnapshotHashQuery(MediaAssetSnapshot snapshot, string sha256)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64)
            throw new ArgumentException("The SHA-256 value must contain exactly 64 hex characters.", nameof(sha256));

        char[] normalized = new char[64];
        for (int index = 0; index < sha256.Length; index++)
        {
            char character = sha256[index];
            if (!IsAsciiHexDigit(character))
                throw new ArgumentException("The SHA-256 value must contain only ASCII hex characters.", nameof(sha256));
            normalized[index] = character is >= 'A' and <= 'F'
                ? (char)(character + 32)
                : character;
        }

        _snapshot = snapshot;
        Sha256 = new string(normalized);
    }

    public string Sha256 { get; }

    /// <summary>
    /// Matches committed SHA-256 metadata only, enumerating the snapshot in pages of at most 500.
    /// A hit does not verify current disk bytes; call MediaAssetSnapshot.OpenVerifiedReadAsync before consumption.
    /// </summary>
    public MediaSnapshotHashSearchResult Search(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _snapshot.Status switch
        {
            MediaManifestStatus.Missing => Unavailable(MediaSnapshotHashSearchStatus.MissingManifest),
            MediaManifestStatus.Corrupt => Unavailable(MediaSnapshotHashSearchStatus.CorruptManifest),
            MediaManifestStatus.Available => SearchAvailableManifest(cancellationToken),
            _ => throw new InvalidOperationException("The media manifest status is not supported by hash search.")
        };

        MediaSnapshotHashSearchResult Unavailable(MediaSnapshotHashSearchStatus status)
            => new(status, Sha256, generation: null, matches: null);
    }

    private MediaSnapshotHashSearchResult SearchAvailableManifest(CancellationToken cancellationToken)
    {
        List<MediaAssetReference> matches = new();
        int offset = 0;
        while (offset < _snapshot.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (MediaAssetReference asset in _snapshot.GetPage(offset, MediaSnapshotHashSearch.MaximumPageSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(asset.Sha256, Sha256, StringComparison.Ordinal))
                    matches.Add(asset);
            }
            offset += Math.Min(MediaSnapshotHashSearch.MaximumPageSize, _snapshot.Count - offset);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(MediaSnapshotHashSearchStatus.Available, Sha256, _snapshot.Generation, matches.ToArray());
    }

    private static bool IsAsciiHexDigit(char character)
        => character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}

/// <summary>
/// Frozen search output. Missing/corrupt manifests are explicit states and expose no match count/page.
/// For an available snapshot, Count and GetPage remain bound to the query's captured manifest view.
/// </summary>
internal sealed class MediaSnapshotHashSearchResult
{
    private readonly MediaAssetReference[]? _matches;

    internal MediaSnapshotHashSearchResult(MediaSnapshotHashSearchStatus status, string sha256,
        string? generation, MediaAssetReference[]? matches)
    {
        if ((status == MediaSnapshotHashSearchStatus.Available) != (matches != null))
            throw new InvalidOperationException("Hash search output must match its manifest availability.");
        Status = status;
        Sha256 = sha256;
        Generation = generation;
        _matches = matches;
    }

    public MediaSnapshotHashSearchStatus Status { get; }
    public string Sha256 { get; }
    public string? Generation { get; }
    public int Count => _matches?.Length
        ?? throw new InvalidOperationException("Hash match count is unavailable for a missing or corrupt manifest.");

    /// <summary>Returns at most 500 unchanged asset references from the captured snapshot. No filesystem work occurs.</summary>
    public IReadOnlyList<MediaAssetReference> GetPage(int offset = 0, int limit = 100)
    {
        MediaAssetReference[] matches = _matches
            ?? throw new InvalidOperationException("Hash search results are unavailable for a missing or corrupt manifest.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > MediaSnapshotHashSearch.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset >= matches.Length) return Array.Empty<MediaAssetReference>();

        int count = Math.Min(limit, matches.Length - offset);
        MediaAssetReference[] page = new MediaAssetReference[count];
        Array.Copy(matches, offset, page, 0, count);
        return Array.AsReadOnly(page);
    }
}
