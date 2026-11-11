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
        return Handovers(manifest.AttachmentBindings, committed);
    }

    /// <summary>
    /// Hand-overs from every earlier binding of the same attachment and bytes to each newly committed key.
    /// This is the one rule; commit time, start-up reconcile and capture-time reconcile all use it.
    /// </summary>
    internal static IReadOnlyList<MediaTagKeyHandover> Handovers(
        IReadOnlyDictionary<string, MediaAttachmentBinding> bindings, IEnumerable<AttachmentRevisionReference> committed)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(committed);
        List<MediaTagKeyHandover> result = new();
        foreach (AttachmentRevisionReference reference in committed)
        {
            if (!bindings.TryGetValue(reference.RevisionKey, out MediaAttachmentBinding? current) || current.Reference != reference) continue;
            foreach (MediaAttachmentBinding earlier in bindings.Values)
            {
                if (earlier.Reference.RevisionKey == reference.RevisionKey || !SameAttachmentAndBytes(earlier, current)) continue;
                result.Add(new MediaTagKeyHandover(earlier.Reference.RevisionKey, reference, current.Actual.Sha256.ToLowerInvariant()));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool SameAttachmentAndBytes(MediaAttachmentBinding earlier, MediaAttachmentBinding current)
        => earlier.Reference.Resource == current.Reference.Resource && earlier.Reference.PostId == current.Reference.PostId
            && earlier.Reference.Position == current.Reference.Position
            && earlier.Provider.MediaId == current.Provider.MediaId && earlier.Provider.Variant == current.Provider.Variant
            && earlier.Provider.Url == current.Provider.Url && earlier.AssetId == current.AssetId
            && string.Equals(earlier.Actual.Sha256, current.Actual.Sha256, StringComparison.OrdinalIgnoreCase);

    private readonly record struct ChainKey(ResourceId Resource, string PostId, int Position, string MediaId, string Variant,
        string Url, string AssetId, string Sha256);

    /// <summary>
    /// Hand-overs that complete the tags of every chain of bindings (same attachment, same bytes, several keys)
    /// when some key of the chain already carries tags. Used where the commit-time hand-over may have been lost, for
    /// example to a stop between the media commit and the tag write. Only keys in <paramref name="taggedKeys"/>
    /// are sources; keys of a chain that already carry the tags change nothing when applied.
    /// </summary>
    internal static IReadOnlyList<MediaTagKeyHandover> ReconcileHandovers(
        IReadOnlyDictionary<string, MediaAttachmentBinding> bindings, IReadOnlySet<string> taggedKeys)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(taggedKeys);
        Dictionary<ChainKey, List<MediaAttachmentBinding>> chains = new();
        foreach (MediaAttachmentBinding binding in bindings.Values)
        {
            ChainKey key = new(binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position,
                binding.Provider.MediaId, binding.Provider.Variant, binding.Provider.Url, binding.AssetId,
                binding.Actual.Sha256.ToLowerInvariant());
            (chains.TryGetValue(key, out var list) ? list : chains[key] = new()).Add(binding);
        }
        List<MediaTagKeyHandover> result = new();
        foreach (List<MediaAttachmentBinding> chain in chains.Values)
        {
            if (chain.Count < 2) continue;
            foreach (MediaAttachmentBinding from in chain)
            {
                if (!taggedKeys.Contains(from.Reference.RevisionKey)) continue;
                foreach (MediaAttachmentBinding to in chain)
                    if (to.Reference.RevisionKey != from.Reference.RevisionKey)
                        result.Add(new MediaTagKeyHandover(from.Reference.RevisionKey, to.Reference, to.Actual.Sha256.ToLowerInvariant()));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>
    /// Reads the manifests of the threads that carry media tags and returns the hand-overs still missing.
    /// Untagged threads are not touched, so the cost follows the number of tagged threads, not the archive.
    /// Unreadable manifests are skipped. Cooperative: yields between manifests so it never holds a thread.
    /// </summary>
    internal static async Task<IReadOnlyList<MediaTagKeyHandover>> FindPendingAsync(PrivateTagStoreSnapshot tags,
        Func<ResourceId, CancellationToken, Task<IReadOnlyList<string>>> mediaDirectoriesOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(mediaDirectoriesOf);
        Dictionary<ResourceId, HashSet<string>> keysByThread = new();
        foreach (PrivateMediaTagAssignment assignment in tags.MediaAssignments)
        {
            PrivateMediaTagTarget target = assignment.Target;
            (keysByThread.TryGetValue(target.Reference.Resource, out var set) ? set : keysByThread[target.Reference.Resource] = new(StringComparer.Ordinal))
                .Add(target.Reference.RevisionKey);
        }
        List<MediaTagKeyHandover> pending = new();
        foreach ((ResourceId thread, HashSet<string> keys) in keysByThread)
            pending.AddRange(await FindPendingForThreadAsync(thread, keys, mediaDirectoriesOf, cancellationToken).ConfigureAwait(false));
        return Array.AsReadOnly(pending.ToArray());
    }

    internal static async Task<IReadOnlyList<MediaTagKeyHandover>> FindPendingForThreadAsync(ResourceId thread,
        IReadOnlySet<string> taggedKeys, Func<ResourceId, CancellationToken, Task<IReadOnlyList<string>>> mediaDirectoriesOf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        List<MediaTagKeyHandover> pending = new();
        IReadOnlyList<string> directories;
        try { directories = await mediaDirectoriesOf(thread, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return pending; }
        foreach (string directory in directories.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaAssetSnapshot manifest;
            try { manifest = await MediaAssetReader.OpenAsync(directory, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
            if (manifest.Status != MediaManifestStatus.Available) continue;
            pending.AddRange(ReconcileHandovers(manifest.AttachmentBindings, taggedKeys));
        }
        return pending;
    }

    /// <summary>Writes the hand-overs, if any, in one admitted write. Returns whether the tag file changed.</summary>
    internal static bool Apply(ProfileLease lease, ProfileAdmissionToken admission, IReadOnlyCollection<MediaTagKeyHandover> handovers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(handovers);
        if (handovers.Count == 0) return false;
        return new PrivateTagStore(lease.ProfileDirectory, lease).CarryMediaAssignmentsAdmitted(handovers, admission, cancellationToken).Changed;
    }
}
