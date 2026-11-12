using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Tags;

/// <summary>Outcome of revalidating one loose file.</summary>
internal enum LocalMediaCheck
{
    /// <summary>The checks could not run (catalog or manifest unreadable, damaged evidence); nothing is concluded.</summary>
    Unreadable,
    /// <summary>The thread, directory and manifest record still exist, the view is still loose and the bytes verify.</summary>
    Verified,
    /// <summary>The owning thread, its media directory or the manifest record is gone.</summary>
    Missing,
    /// <summary>The file is now shown bound to a post version (another tag namespace) or its slot record changed.</summary>
    NotLoose,
    /// <summary>The file on disk is missing or its bytes no longer match the recorded length and SHA-256.</summary>
    Changed
}

/// <summary>
/// Fresh, exact revalidation of loose files against the catalog and their media manifests, using the same gallery
/// selection every view uses. A frozen view entry or a cached index entry is never authority for a write.
/// </summary>
internal static class LocalMediaTagRevalidation
{
    /// <summary>
    /// Checks every observation, reading each thread and its manifests once. With <paramref name="verifyBytes"/> the
    /// complete file is hashed (before an add); without, existence and length are checked (cheap orphan inspection).
    /// </summary>
    internal static async Task<IReadOnlyList<LocalMediaCheck>> CheckAsync(IReadOnlyList<LocalMediaObservation> observations,
        Func<CancellationToken, Task<ArchiveReader>> openReader, Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        bool verifyBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentNullException.ThrowIfNull(openManifest);
        // Every slot starts as Unreadable; only completed checks overwrite it.
        LocalMediaCheck[] results = new LocalMediaCheck[observations.Count];
        if (observations.Count == 0) return results;
        try
        {
            await using ArchiveReader reader = await openReader(token).ConfigureAwait(false);
            foreach (IGrouping<ResourceId, int> thread in Enumerable.Range(0, observations.Count)
                .GroupBy(index => observations[index].Locator.Thread))
            {
                token.ThrowIfCancellationRequested();
                ThreadEvidence evidence;
                try { evidence = await ReadThreadAsync(reader, thread.Key, openManifest, token).ConfigureAwait(false); }
                catch (Exception error) when (Evidence(error)) { continue; }
                foreach (int index in thread)
                    results[index] = await CheckOneAsync(observations[index], evidence, verifyBytes, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (Evidence(error)) { /* Unchecked slots stay Unreadable. */ }
        return results;
    }

    private sealed record ThreadEvidence(bool Exists, IReadOnlyList<MediaAssetSnapshot> Manifests, IReadOnlyList<GalleryEntry> Entries);

    private static async Task<ThreadEvidence> ReadThreadAsync(ArchiveReader reader, ResourceId id,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken token)
    {
        ArchivedThreadView? thread = await reader.ReadThreadAsync(id, token).ConfigureAwait(false);
        if (thread == null) return new(false, Array.Empty<MediaAssetSnapshot>(), Array.Empty<GalleryEntry>());
        IReadOnlyList<string> directories = await reader.GetMediaDirectoriesAsync(id, token).ConfigureAwait(false);
        List<MediaAssetSnapshot> manifests = new();
        foreach (string directory in directories.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            manifests.Add(await openManifest(directory, token).ConfigureAwait(false));
        }
        return new(true, manifests, GallerySelection.Build(thread, manifests, allSavedVersions: true, includeOtherMedia: true).Entries);
    }

    private static async Task<LocalMediaCheck> CheckOneAsync(LocalMediaObservation observation, ThreadEvidence evidence,
        bool verifyBytes, CancellationToken token)
    {
        if (!evidence.Exists) return LocalMediaCheck.Missing;
        MediaAssetSnapshot? manifest = evidence.Manifests.FirstOrDefault(item => item.Directory == observation.Locator.Directory);
        if (manifest == null) return LocalMediaCheck.Missing; // No longer a media directory of this thread.
        // A damaged manifest of the thread hides bindings: never conclude anything from it.
        if (evidence.Manifests.Any(item => item.Status == MediaManifestStatus.Corrupt)) return LocalMediaCheck.Unreadable;
        if (manifest.Status != MediaManifestStatus.Available || manifest.FindById(observation.AssetId) is not { } asset)
            return LocalMediaCheck.Missing;
        GalleryEntry[] views = evidence.Entries.Where(entry => entry.Manifest.Directory == observation.Locator.Directory
            && entry.Asset.AssetId == observation.AssetId).ToArray();
        if (views.Length == 0 || !views.All(view => view.LocalTagEligible) || asset.RequestedFileName != observation.Locator.RequestedFileName)
            return LocalMediaCheck.NotLoose;
        if (asset.Length != observation.Length || !string.Equals(asset.Sha256, observation.Sha256, StringComparison.OrdinalIgnoreCase))
            return LocalMediaCheck.Changed;
        if (!verifyBytes)
        {
            try
            {
                FileInfo file = new(asset.Path);
                return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length == asset.Length
                    ? LocalMediaCheck.Verified : LocalMediaCheck.Changed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return LocalMediaCheck.Unreadable; }
        }
        try
        {
            await using Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false);
            return LocalMediaCheck.Verified;
        }
        catch (OperationCanceledException) { throw; }
        // Missing files (FileNotFound/DirectoryNotFound are IOExceptions) and changed bytes alike.
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return LocalMediaCheck.Changed; }
    }

    /// <summary>
    /// Which local assignments of the snapshot no longer reach a loose file in any view: owner thread or directory gone,
    /// manifest record gone, now bound, file missing or changed in length. These stay stored as visible orphans until the
    /// user removes them explicitly; nothing is retargeted. Unreadable evidence is counted, never called an orphan.
    /// </summary>
    internal static async Task<LocalMediaTagInspection> InspectAsync(PrivateTagStoreSnapshot snapshot,
        Func<CancellationToken, Task<ArchiveReader>> openReader, Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LocalMediaTagTarget[] targets = snapshot.LocalAssignments.Select(item => item.Target).Distinct().ToArray();
        List<LocalMediaObservation> observations = new();
        List<LocalMediaTagTarget> observed = new();
        HashSet<LocalMediaTagTarget> orphaned = new();
        foreach (LocalMediaTagTarget target in targets)
        {
            LocalMediaOccurrence? occurrence = snapshot.LocalRegistry.Find(target.Occurrence);
            LocalMediaRevision? revision = occurrence?.Find(target.Revision);
            if (occurrence == null || revision == null
                || !LocalMediaObservation.TryCreate(occurrence.Locator.Thread, occurrence.Locator.Directory, occurrence.Locator.RequestedFileName,
                    revision.AssetId, revision.Length, revision.Sha256, out LocalMediaObservation? observation))
            { orphaned.Add(target); continue; }
            observations.Add(observation!);
            observed.Add(target);
        }
        IReadOnlyList<LocalMediaCheck> checks = await CheckAsync(observations, openReader, openManifest, verifyBytes: false, token)
            .ConfigureAwait(false);
        int notChecked = 0;
        for (int index = 0; index < checks.Count; index++)
        {
            if (checks[index] == LocalMediaCheck.Unreadable) notChecked++;
            else if (checks[index] != LocalMediaCheck.Verified) orphaned.Add(observed[index]);
        }
        return new LocalMediaTagInspection(snapshot.Revision,
            Array.AsReadOnly(snapshot.LocalAssignments.Where(item => orphaned.Contains(item.Target)).ToArray()),
            orphaned.Count, notChecked);
    }

    private static bool Evidence(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or Microsoft.Data.Sqlite.SqliteException;
}

/// <summary>
/// Result of an orphan inspection against one tag snapshot revision: the orphaned assignments, how many distinct files
/// they concern, and how many tagged files could not be checked. Removal must name <see cref="Revision"/>.
/// </summary>
internal sealed record LocalMediaTagInspection(long Revision, IReadOnlyList<PrivateLocalMediaTagAssignment> Orphans,
    int OrphanedFiles, int UncheckedFiles);
