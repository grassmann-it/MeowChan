using MeowChan.Gallery;
using MeowChan.Transfer;

namespace MeowChan.Tags;

/// <summary>Why a gallery entry cannot carry an own media tag.</summary>
internal enum GalleryMediaTagUnavailable { None, NoPostVersion, Mismatch, InvalidIdentity }

/// <summary>
/// Turns a gallery entry into the exact tag target: the bound post attachment version plus the very
/// asset shown. Entries without a bound version (legacy captures, unassigned files) cannot be tagged;
/// nothing is guessed from file names, hashes or the latest version of a post.
/// </summary>
internal static class GalleryMediaTagTarget
{
    internal static PrivateMediaTagTarget? Resolve(GalleryEntry? entry, out GalleryMediaTagUnavailable reason)
    {
        reason = GalleryMediaTagUnavailable.NoPostVersion;
        if (entry?.AttachmentResolution is not { IsLegacy: false, Binding: { } binding } resolution) return null;
        // The binding must describe exactly the asset this entry shows, in this entry's thread.
        if (binding.AssetId != entry.Asset.AssetId || resolution.Asset.AssetId != entry.Asset.AssetId
            || binding.Reference.Resource != entry.Key.Thread)
        {
            reason = GalleryMediaTagUnavailable.Mismatch;
            return null;
        }
        TransferMediaRole role = entry.Asset.Variant == "original" ? TransferMediaRole.Original : TransferMediaRole.Preview;
        if (!PrivateMediaTagTarget.TryCreate(binding.Reference, entry.Asset.MediaIdentifier, entry.Asset.Variant, role,
            entry.Asset.Sha256.ToLowerInvariant(), out PrivateMediaTagTarget? target, out _))
        {
            reason = GalleryMediaTagUnavailable.InvalidIdentity;
            return null;
        }
        reason = GalleryMediaTagUnavailable.None;
        return target;
    }
}
