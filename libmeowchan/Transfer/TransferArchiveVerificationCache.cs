using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

internal enum TransferAssetVerification { Available, Unavailable, Limit }

/// <summary>
/// One sequential freeze's verification budget, never a cross-operation trust cache.
/// Equal payload hashes from different frozen origins still incur separate verification.
/// The writer must reopen/verify and hash copied bytes regardless of this cache.
/// </summary>
internal sealed class TransferArchiveVerificationCache(long maximumBytes = 2L * 1024 * 1024 * 1024)
{
    private readonly Dictionary<Origin, TransferAssetVerification> _verified = new();
    private readonly Dictionary<DerivedOrigin, TransferAssetVerification> _derivedVerified = new();
    private long _reservedBytes;

    internal async Task<TransferAssetVerification> VerifyAsync(MediaAssetSnapshot manifest,
        MediaAssetReference asset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (manifest.FindById(asset.AssetId) != asset)
            throw new InvalidDataException("The selected asset does not match its frozen origin.");
        Origin origin = new(manifest.Directory, manifest.Generation, asset.AssetId, asset.Path, asset.Length, asset.Sha256);
        if (_verified.TryGetValue(origin, out TransferAssetVerification previous)) return previous;
        // Charge distinct-origin work BEFORE another full-file hash (including missing-file attempts).
        if (asset.Length < 0 || maximumBytes is < 0 or > 2L * 1024 * 1024 * 1024 || asset.Length > maximumBytes - _reservedBytes)
            return TransferAssetVerification.Limit;
        _reservedBytes += asset.Length;
        TransferAssetVerification result;
        try
        {
            ArchiveExportMetadataFile.RejectLinks(asset.Path);
            await using Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false);
            result = TransferAssetVerification.Available;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { result = TransferAssetVerification.Unavailable; }
        token.ThrowIfCancellationRequested();
        _verified.Add(origin, result);
        return result;
    }

    internal async Task<TransferAssetVerification> VerifyDerivedAsync(MediaAssetSnapshot manifest,
        MediaDerivedRepresentation member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(member);
        // Check full value membership before consulting cached success or the budget.
        if (manifest.Status != MediaManifestStatus.Available || !manifest.DerivedRepresentations.Contains(member))
            throw new InvalidDataException("The selected representation does not match its frozen origin.");
        if (member.IncomingStatus != MediaDerivedStatus.Contained || member.Blob is not { Actual: { } actual } blob)
            throw new InvalidDataException("The selected representation has no contained bytes.");
        DerivedOrigin origin = new(manifest.Directory, manifest.Generation,
            Path.Combine(manifest.Directory, blob.RelativeFileName), member);
        if (_derivedVerified.TryGetValue(origin, out TransferAssetVerification previous)) return previous;
        // Originals and derived members consume the same physical verification allowance.
        // Full member identity also keeps Preview success from bypassing a Poster PNG check.
        if (actual.Length < 0 || maximumBytes is < 0 or > 2L * 1024 * 1024 * 1024
            || actual.Length > maximumBytes - _reservedBytes)
            return TransferAssetVerification.Limit;
        _reservedBytes += actual.Length;
        TransferAssetVerification result;
        try
        {
            await using Stream verified = await manifest.OpenVerifiedDerivedReadAsync(member, token).ConfigureAwait(false);
            result = TransferAssetVerification.Available;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { result = TransferAssetVerification.Unavailable; }
        token.ThrowIfCancellationRequested();
        _derivedVerified.Add(origin, result);
        return result;
    }

    private readonly record struct DerivedOrigin(string Directory, string? Generation, string Path,
        MediaDerivedRepresentation Member);

    private readonly record struct Origin(string Directory, string? Generation, string AssetId,
        string Path, long Length, string Sha256);
}
