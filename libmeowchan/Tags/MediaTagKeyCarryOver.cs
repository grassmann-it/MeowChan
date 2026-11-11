using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tags;

/// <summary>
/// One earlier attachment revision key whose media tags move on to <see cref="To"/>. The bytes are named
/// by <see cref="Sha256"/>; a tag target with other bytes never matches.
/// </summary>
internal sealed record MediaTagKeyHandover(string FromRevisionKey, AttachmentRevisionReference To, string Sha256);

/// <summary>
/// The attachment revision key hashes provider metadata, so a source that changes what it reports (for
/// example stops reporting an MD5) gives the same attachment a new key on its next re-check. Media tags are
/// assigned to the exact key, so without help they would stay on the old key and no longer show.
/// After a media commit binds a new key, every older binding of the same attachment (same thread, post,
/// position, media id, variant and URL) that points to the same asset and the same SHA-256 hands its tag
/// assignments over: the same tags are added for the new key. Nothing is removed or rewritten, the operation
/// is a no-op when the assignments already exist, and different bytes are a different attachment version.
/// </summary>
internal static class MediaTagKeyCarryOver
{
    internal static IReadOnlyList<MediaTagKeyHandover> Handovers(MediaAssetSnapshot manifest,
        IEnumerable<AttachmentRevisionReference> committed)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(committed);
        List<MediaTagKeyHandover> result = new();
        foreach (AttachmentRevisionReference reference in committed)
        {
            MediaAttachmentBinding? current = manifest.FindAttachmentBinding(reference.RevisionKey);
            if (current == null || current.Reference != reference) continue;
            foreach (MediaAttachmentBinding earlier in manifest.AttachmentBindings.Values)
            {
                if (earlier.Reference.RevisionKey == reference.RevisionKey
                    || earlier.Reference.Resource != reference.Resource || earlier.Reference.PostId != reference.PostId
                    || earlier.Reference.Position != reference.Position
                    || earlier.Provider.MediaId != current.Provider.MediaId || earlier.Provider.Variant != current.Provider.Variant
                    || earlier.Provider.Url != current.Provider.Url
                    || earlier.AssetId != current.AssetId
                    || !string.Equals(earlier.Actual.Sha256, current.Actual.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new MediaTagKeyHandover(earlier.Reference.RevisionKey, reference, current.Actual.Sha256.ToLowerInvariant()));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Hands over the tags after a commit. Returns whether the tag file changed.</summary>
    internal static async Task<bool> ApplyAsync(ProfileLease lease, ProfileAdmissionToken admission, string mediaDirectory,
        IEnumerable<AttachmentRevisionReference> committed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(admission);
        AttachmentRevisionReference[] references = committed.ToArray();
        if (references.Length == 0) return false;
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(mediaDirectory, cancellationToken).ConfigureAwait(false);
        if (manifest.Status != MediaManifestStatus.Available) return false;
        IReadOnlyList<MediaTagKeyHandover> handovers = Handovers(manifest, references);
        if (handovers.Count == 0) return false;
        return new PrivateTagStore(lease.ProfileDirectory, lease)
            .CarryMediaAssignmentsAdmitted(handovers, admission, cancellationToken).Changed;
    }
}
