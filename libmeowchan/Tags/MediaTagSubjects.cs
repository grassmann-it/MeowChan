using System;
using MeowChan.Gallery;

namespace MeowChan.Tags;

/// <summary>
/// The one media tag target resolver of every view (gallery, archive, media page state, filter and selection).
/// A file shown bound to a post version gets its exact bound target, unchanged. A loose file (unassigned, or only
/// legacy-associated, and not shown bound anywhere in the same thread) gets a local subject made from its exact
/// manifest record. Everything else (mismatched bindings, missing files, invalid identities) gets nothing: there is
/// no fallback from a failed bound resolution to a local one, and nothing is guessed from names, hashes or paths.
/// </summary>
internal static class MediaTagSubjects
{
    internal static MediaTagSubject? Of(GalleryEntry? entry, out GalleryMediaTagUnavailable reason)
    {
        if (GalleryMediaTagTarget.Resolve(entry, out reason) is { } bound) return new MediaTagSubject.Bound(bound);
        // Only "no bound version" may become a local target; a mismatched or invalid binding fails closed.
        if (entry == null || reason != GalleryMediaTagUnavailable.NoPostVersion || !entry.LocalTagEligible) return null;
        if (entry.Key.Directory != entry.Manifest.Directory || entry.Key.AssetId != entry.Asset.AssetId)
        {
            reason = GalleryMediaTagUnavailable.Mismatch;
            return null;
        }
        if (!LocalMediaObservation.TryCreate(entry.Key.Thread, entry.Manifest.Directory, entry.Asset.RequestedFileName,
            entry.Asset.AssetId, entry.Asset.Length, entry.Asset.Sha256, out LocalMediaObservation? observation))
        {
            reason = GalleryMediaTagUnavailable.InvalidIdentity;
            return null;
        }
        reason = GalleryMediaTagUnavailable.None;
        return new MediaTagSubject.Local(observation!, null);
    }

    internal static MediaTagSubject? Of(GalleryEntry? entry) => Of(entry, out _);

    /// <summary>The same rules for an index entry of the media page; the index carries the facts the gallery entry had.</summary>
    internal static MediaTagSubject? Of(MediaLibraryEntry? entry)
    {
        if (entry == null || entry.Presence != MediaLibraryPresence.Present) return null;
        if (MediaLibraryFilterEngine.TagTarget(entry) is { } bound) return new MediaTagSubject.Bound(bound);
        // A bound key whose target is invalid never falls back to a local target.
        if (entry.AttachmentRevisionKey != null || entry.LocalSlot == null) return null;
        return LocalMediaObservation.TryCreate(entry.Thread, entry.Directory, entry.LocalSlot, entry.AssetId, entry.Length,
            entry.Sha256, out LocalMediaObservation? observation) ? new MediaTagSubject.Local(observation!, null) : null;
    }

    /// <summary>
    /// Freezes what the caller sees: a local subject is bound to its current registration (if any), so a later write
    /// is refused rather than redirected when the file resolves to another occurrence or revision meanwhile.
    /// </summary>
    internal static MediaTagSubject Freeze(MediaTagSubject subject, PrivateTagStoreSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (snapshot == null || subject is not MediaTagSubject.Local { Expected: null } local) return subject;
        return snapshot.ResolveLocal(local.Observation) is { IsRegistered: true, Target: { } target }
            ? local with { Expected = target } : subject;
    }
}
