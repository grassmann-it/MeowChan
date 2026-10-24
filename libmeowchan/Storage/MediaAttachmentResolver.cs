using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MeowChan.Archive;

namespace MeowChan.Storage;

/// <summary>A metadata association; callers still verify bytes with Snapshot.OpenVerifiedReadAsync.</summary>
public sealed record ResolvedMediaAttachment(MediaAssetSnapshot Snapshot, MediaAssetReference Asset,
    MediaAttachmentBinding? Binding, bool IsLegacy);

public static class MediaAttachmentResolver
{
    public static ResolvedMediaAttachment? Resolve(MediaAssetSnapshot snapshot, ResourceId resource,
        string postId, CapturedAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attachment);
        if (snapshot.Status != MediaManifestStatus.Available || attachment.MediaId == null) return null;
        if (attachment.AttachmentRevisionKey != null)
        {
            MediaProviderMetadata provider = Provider(attachment);
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, postId, attachment.Position, provider);
            if (key != attachment.AttachmentRevisionKey) return null;
            MediaAttachmentBinding? binding = snapshot.FindAttachmentBinding(key);
            if (binding == null) return null;
            try { MediaIntegrityValidation.ValidateBinding(key, binding); }
            catch (Exception error) when (EvidenceError(error)) { return null; }
            MediaAssetReference? asset = snapshot.FindById(binding.AssetId);
            if (binding.Reference.Resource != resource || binding.Reference.PostId != postId
                || binding.Reference.Position != attachment.Position || binding.Provider != provider
                || asset == null || !BindingMatchesAsset(binding, asset)) return null;
            return new(snapshot, asset, binding, false);
        }

        // Legacy data has no host revision. Check every available provider expectation
        // and require an unambiguous existing manifest identity; never adopt loose files.
        if (attachment.ExpectedSize == null && attachment.ExpectedMd5 == null && attachment.ExpectedSha256 == null
            && attachment.Url == null) return null;
        List<MediaAssetReference> matches = new();
        for (int offset = 0; offset < snapshot.Count; offset += 500)
            foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                if (LegacyMatches(attachment, asset)) matches.Add(asset);
        return matches.Count == 1 ? new(snapshot, matches[0], null, true) : null;
    }

    public static ResolvedMediaAttachment? Resolve(IEnumerable<MediaAssetSnapshot> snapshots, ResourceId resource,
        string postId, CapturedAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        MediaAssetSnapshot[] ordered = snapshots.OrderBy(value => value.Directory, StringComparer.Ordinal).ToArray();
        if (ordered.Any(snapshot => snapshot.Status == MediaManifestStatus.Corrupt)) return null;
        if (attachment.AttachmentRevisionKey == null)
        {
            if (attachment.ExpectedSize == null && attachment.ExpectedMd5 == null && attachment.ExpectedSha256 == null
                && attachment.Url == null) return null;
            ResolvedMediaAttachment? legacy = null;
            foreach (MediaAssetSnapshot snapshot in ordered)
                for (int offset = 0; offset < snapshot.Count; offset += 500)
                    foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                    {
                        if (!LegacyMatches(attachment, asset)) continue;
                        if (legacy != null && (legacy.Asset.AssetId != asset.AssetId
                            || legacy.Asset.Length != asset.Length || legacy.Asset.Sha256 != asset.Sha256)) return null;
                        legacy ??= new(snapshot, asset, null, true);
                    }
            return legacy;
        }
        ResolvedMediaAttachment? selected = null;
        foreach (MediaAssetSnapshot snapshot in ordered)
        {
            if (snapshot.Status == MediaManifestStatus.Corrupt) return null;
            ResolvedMediaAttachment? match = Resolve(snapshot, resource, postId, attachment);
            if (match == null)
            {
                if (attachment.AttachmentRevisionKey != null && snapshot.FindAttachmentBinding(attachment.AttachmentRevisionKey) != null)
                    return null;
                continue;
            }
            if (selected != null && (selected.Asset.AssetId != match.Asset.AssetId
                || selected.Asset.Length != match.Asset.Length || selected.Asset.Sha256 != match.Asset.Sha256
                || selected.Binding != match.Binding)) return null;
            selected ??= match;
        }
        return selected;
    }

    internal static bool BindingMatchesAsset(MediaAttachmentBinding binding, MediaAssetReference asset)
    {
        try
        {
            return binding.AssetId == asset.AssetId && binding.Provider.MediaId == asset.MediaIdentifier
                && binding.Provider.Variant == asset.Variant && binding.Actual.Length == asset.Length
                && string.Equals(binding.Actual.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                && Md5Equals(binding.Actual.Md5, asset.Md5);
        }
        catch (Exception error) when (EvidenceError(error)) { return false; }
    }

    private static MediaProviderMetadata Provider(CapturedAttachment attachment) => new(attachment.MediaId!, attachment.Variant,
        attachment.Url!, attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256);

    private static bool LegacyMatches(CapturedAttachment attachment, MediaAssetReference asset)
    {
        try
        {
            return asset.MediaIdentifier == attachment.MediaId && asset.Variant == attachment.Variant
                && (attachment.ExpectedSize != null || attachment.ExpectedMd5 != null || attachment.ExpectedSha256 != null
                    || attachment.Url != null && attachment.Url == asset.SourceUrl)
                && (attachment.ExpectedSize == null || attachment.ExpectedSize == asset.Length)
                && (attachment.ExpectedMd5 == null || Md5Equals(attachment.ExpectedMd5, asset.Md5))
                && (attachment.ExpectedSha256 == null || MediaIntegrityValidation.IsSha256(attachment.ExpectedSha256)
                    && string.Equals(attachment.ExpectedSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (EvidenceError(error)) { return false; }
    }

    private static bool Md5Equals(string first, string second)
    {
        byte[] a = Convert.FromBase64String(first), b = Convert.FromBase64String(second);
        return a.Length == 16 && b.Length == 16 && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static bool EvidenceError(Exception error) => error is ArgumentException or InvalidDataException or FormatException or NullReferenceException;
}
