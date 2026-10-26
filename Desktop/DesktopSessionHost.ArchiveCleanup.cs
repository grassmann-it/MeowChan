using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private readonly string _archiveCleanupHostBinding = Guid.NewGuid().ToString("N");
    private string ArchiveCleanupProfileBinding => ProfileDirectory + "|" + _archiveCleanupHostBinding;

    // Internal measurement only; it cannot replace or bypass the mandatory directory verification.
    internal Action<string, long>? ArchiveCleanupMediaVerified { get; set; }

    internal Task<ArchiveCleanupPreview> PrepareArchiveCleanupAsync(
        IReadOnlyCollection<ResourceId>? openThreadIds = null, CancellationToken token = default)
    {
        ResourceId[] frozen = (openThreadIds ?? Array.Empty<ResourceId>()).ToArray();
        return RunInteractiveAsync(nameof(PrepareArchiveCleanupAsync), "profile", () => PrepareArchiveCleanupAdmittedCoreAsync(frozen, token));
    }

    private async Task<ArchiveCleanupPreview> PrepareArchiveCleanupAdmittedCoreAsync(
        IReadOnlyCollection<ResourceId>? openThreadIds = null, CancellationToken token = default)
    {
        ResourceId[] frozen = OrderedCleanupIds(openThreadIds ?? Array.Empty<ResourceId>());
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return await Task.Run(() => CollectArchiveCleanupPreviewCoreAsync(frozen, token), token).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    internal static bool HasMatchingCleanupMedia(ResourceId resource, MediaWork work, IEnumerable<MediaAssetSnapshot> manifests)
    {
        MediaAssetSnapshot[] frozen = manifests.ToArray();
        CapturedAttachment Attachment(int position, string? key) => new(position, work.MediaIdentifier, work.Variant,
            work.DownloadUrl, work.OriginalFileName, work.DestinationPath, work.ExpectedSize, work.ExpectedMd5)
            { ExpectedSha256 = work.ExpectedSha256, ExpectedSha1 = work.ExpectedSha1, ProviderSizeHint = work.ProviderSizeHint, AttachmentRevisionKey = key };
        bool Match(string postId, CapturedAttachment attachment, string? acquisition)
        {
            ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(frozen, resource, postId, attachment);
            return resolved != null && resolved.Asset.RequestedFileName == Path.GetFileName(work.DestinationPath)
                && (acquisition == null ? resolved.IsLegacy : resolved.Binding?.AcquisitionRevisionKey == acquisition
                    && resolved.Asset.AssetId == work.CommittedAssetId);
        }
        if (work.AcquisitionRevisionKey != null || work.AttachmentReferences.Count > 0)
            return work.AcquisitionRevisionKey != null && work.CommittedAssetId != null && work.AttachmentReferences.Count > 0
                && work.AttachmentReferences.All(reference => reference.Resource == resource
                    && Match(reference.PostId, Attachment(reference.Position, reference.RevisionKey), work.AcquisitionRevisionKey));
        return Match(resource.Thread!, Attachment(0, null), null);
    }

    // Execution owns _commands and its reservation when calling this method. Never reacquire either.
    private async Task<ArchiveCleanupPreview> CollectArchiveCleanupPreviewCoreAsync(
        IReadOnlyCollection<ResourceId> openThreadIds, CancellationToken token, ArchiveCleanupPreviewRow? scope = null)
    {
        List<string> errors = new();
        ResourceId[] open = OrderedCleanupIds(openThreadIds);
        ArchiveCleanupGuards before = await ReadArchiveCleanupGuardTokensAsync(open, token).ConfigureAwait(false);
        errors.AddRange(before.Errors);
        IReadOnlyList<ArchiveDeletedThread> catalog;
        try { catalog = await ReadCleanupCatalogAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (CleanupEvidenceError(error))
        {
            errors.Add("Archive catalog: " + error.Message);
            return new(ArchiveCleanupProfileBinding, 1, Array.Empty<ArchiveCleanupPreviewRow>(), Array.AsReadOnly(open),
                "unavailable-" + Guid.NewGuid().ToString("N"), before.ProtectionToken, before.WorkToken, false, false,
                Array.AsReadOnly(errors.ToArray()));
        }
        Dictionary<string, HashSet<ResourceId>> owners = new(ArchiveCleanupMediaInventory.Paths);
        foreach (ArchiveDeletedThread thread in catalog)
            foreach (string directory in thread.MediaDirectories)
            {
                if (!owners.TryGetValue(directory, out var set)) owners[directory] = set = new();
                set.Add(thread.Summary.Id);
            }
        IReadOnlyList<ArchiveDeletedThread> selectedCatalog = catalog;
        IEnumerable<string> mediaDirectories = owners.Keys;
        if (scope != null)
        {
            // Ownership still comes from the complete fresh catalog. Only byte verification is scoped.
            ArchiveDeletedThread? current = catalog.SingleOrDefault(thread => thread.Summary.Id == scope.Id);
            if (current == null || current.Summary != scope.Summary || !current.MediaDirectories.ToHashSet(ArchiveCleanupMediaInventory.Paths)
                .SetEquals(scope.Media.Directories.Select(directory => directory.Directory)))
                throw new InvalidDataException("The selected archive identity or historical directories changed.");
            ArchiveCleanupStorageAnchor[] anchors = scope.Media.Directories.Where(directory => directory.StorageAnchor != null)
                .Select(directory => directory.StorageAnchor!).ToArray();
            foreach (ArchiveCleanupStorageAnchor anchor in anchors)
                if (!catalog.Any(thread => thread.Summary.Id == anchor.Thread
                    && thread.MediaDirectories.Contains(anchor.Directory, ArchiveCleanupMediaInventory.Paths)))
                    throw new InvalidDataException("The bound storage witness is no longer owned by its archived thread.");
            selectedCatalog = new[] { current };
            mediaDirectories = scope.Media.Directories.Select(directory => directory.Directory)
                .Concat(anchors.Select(anchor => anchor.Directory)).Distinct(ArchiveCleanupMediaInventory.Paths);
        }
        async Task<Dictionary<string, ArchiveCleanupDirectoryEvidence>> ReadMedia()
        {
            Dictionary<string, ArchiveCleanupDirectoryEvidence> result = new(ArchiveCleanupMediaInventory.Paths);
            foreach (string directory in mediaDirectories.OrderBy(path => path, ArchiveCleanupMediaInventory.Paths))
            {
                token.ThrowIfCancellationRequested();
                result[directory] = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, ProfileDirectory, token).ConfigureAwait(false);
                ArchiveCleanupMediaVerified?.Invoke(directory, result[directory].VerifiedBytes);
            }
            return result;
        }
        var firstMedia = await ReadMedia().ConfigureAwait(false);
        var secondMedia = await ReadMedia().ConfigureAwait(false);
        var firstWitnesses = catalog.SelectMany(owner => owner.MediaDirectories.Where(firstMedia.ContainsKey).Select(directory =>
            (Thread: owner.Summary.Id, Directory: firstMedia[directory]))).ToArray();
        var secondWitnesses = catalog.SelectMany(owner => owner.MediaDirectories.Where(secondMedia.ContainsKey).Select(directory =>
            (Thread: owner.Summary.Id, Directory: secondMedia[directory]))).ToArray();
        ArchiveCleanupGuards after = await ReadArchiveCleanupGuardTokensAsync(open, token).ConfigureAwait(false);
        errors.AddRange(after.Errors);
        bool stale = before.ProtectionToken != after.ProtectionToken || before.WorkToken != after.WorkToken;
        try
        {
            var finalCatalog = await ReadCleanupCatalogAsync(token).ConfigureAwait(false);
            if (ArchiveCleanupFingerprint.Of(catalog) != ArchiveCleanupFingerprint.Of(finalCatalog)) stale = true;
        }
        catch (Exception error) when (CleanupEvidenceError(error))
        { errors.Add("Archive recheck: " + error.Message); stale = true; }

        ArchiveCleanupMediaEvidence MediaFor(ArchiveDeletedThread thread, Dictionary<string, ArchiveCleanupDirectoryEvidence> inventory,
            IReadOnlyList<(ResourceId Thread, ArchiveCleanupDirectoryEvidence Directory)> witnesses)
        {
            string outputRoot = before.Work?.Entries.FirstOrDefault(entry => entry.Resource.IsThread
                && entry.Resource.Url == thread.Summary.Url)?.Resource.OutputRoot ?? Config.DownloadPath;
            ArchiveCleanupDirectoryEvidence[] directories = thread.MediaDirectories.Select(directory =>
            {
                IEnumerable<(ResourceId Thread, ArchiveCleanupDirectoryEvidence Directory)> boundWitnesses = witnesses;
                if (scope != null)
                {
                    ArchiveCleanupStorageAnchor? bound = scope.Media.Directories.Single(item =>
                        ArchiveCleanupMediaInventory.Paths.Equals(item.Directory, directory)).StorageAnchor;
                    // Never select a different witness merely because it is now healthier or preferred.
                    boundWitnesses = bound == null ? Array.Empty<(ResourceId, ArchiveCleanupDirectoryEvidence)>()
                        : witnesses.Where(witness => witness.Thread == bound.Thread
                            && ArchiveCleanupMediaInventory.Paths.Equals(witness.Directory.Directory, bound.Directory));
                }
                return ArchiveCleanupMediaInventory.WithMissingWitness(inventory[directory], thread.Summary.Id, outputRoot, boundWitnesses, token);
            }).ToArray();
            ArchiveCleanupMediaEvidence media = ArchiveCleanupMediaInventory.Combine(Array.AsReadOnly(directories));
            var succeeded = before.Work?.Entries.Where(entry => entry.Resource.IsThread && entry.Resource.Url == thread.Summary.Url)
                .SelectMany(entry => entry.Media).Where(work => work.Status == MediaWorkStatus.Succeeded).ToArray()
                ?? Array.Empty<MediaWork>();
            if (succeeded.Any(work => !HasMatchingCleanupMedia(thread.Summary.Id, work,
                directories.Where(directory => directory.Manifest != null).Select(directory => directory.Manifest!))))
                media = media with { SavedAssetCount = null, Error = "Succeeded media work has no matching verified stored asset.",
                    EvidenceToken = ArchiveCleanupFingerprint.Of(new { media.EvidenceToken, Succeeded = succeeded }) };
            return media;
        }

        List<ArchiveCleanupPreviewRow> rows = new();
        foreach (ArchiveDeletedThread thread in selectedCatalog)
        {
            token.ThrowIfCancellationRequested();
            ArchiveCleanupMediaEvidence media = MediaFor(thread, firstMedia, firstWitnesses);
            if (media.EvidenceToken != MediaFor(thread, secondMedia, secondWitnesses).EvidenceToken) stale = true;
            List<ArchiveCleanupReason> reasons = new();
            if (before.Protections.TryGetValue(thread.Summary.Id, out var protectedReasons)) reasons.AddRange(protectedReasons);
            ArchiveCleanupWorkEntry[] work = before.Work?.Entries.Where(entry => entry.Resource.IsThread
                && entry.Resource.Url == thread.Summary.Url).ToArray() ?? Array.Empty<ArchiveCleanupWorkEntry>();
            bool active = before.Work?.ActiveThreads.Contains(thread.Summary.Id) == true;
            if (active) reasons.Add(ArchiveCleanupReason.ActiveWatch);
            bool pending = work.Any(entry => entry.Resource.Intent is WorkIntent.Watching or WorkIntent.Repairing
                || entry.Resource.CapturePending || entry.Media.Any(item => item.ManualAttempt || item.Status is not
                    (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded or MediaWorkStatus.Expired)));
            if (pending) reasons.Add(ArchiveCleanupReason.PendingWork);
            if (thread.MediaDirectories.Any(directory => owners[directory].Count > 1)) reasons.Add(ArchiveCleanupReason.SharedDirectory);
            if (!before.Reliable || !after.Reliable)
            {
                reasons.Add(ArchiveCleanupReason.ProtectionUnknown);
                if (before.Work == null || after.Work == null) reasons.Add(ArchiveCleanupReason.WorkUnknown);
            }
            if (media.SavedAssetCount == null) reasons.Add(ArchiveCleanupReason.MediaUnknown);
            if (media.SavedAssetCount > ArchiveCleanupPreview.MaximumSavedAssets) reasons.Add(ArchiveCleanupReason.MoreThanTwoAssets);
            ArchiveCleanupEligibility eligibility = !before.Reliable || !after.Reliable ? ArchiveCleanupEligibility.Unknown
                : active || pending ? ArchiveCleanupEligibility.Active
                : protectedReasons?.Count > 0 || reasons.Contains(ArchiveCleanupReason.SharedDirectory) ? ArchiveCleanupEligibility.Protected
                : media.SavedAssetCount == null ? ArchiveCleanupEligibility.Unknown
                : media.SavedAssetCount > ArchiveCleanupPreview.MaximumSavedAssets ? ArchiveCleanupEligibility.Kept
                : ArchiveCleanupEligibility.Candidate;
            var frozenReasons = Array.AsReadOnly(reasons.Distinct().Order().ToArray());
            string rowToken = ArchiveCleanupFingerprint.Of(new { thread.Summary, Media = media, Reasons = frozenReasons,
                Work = work, Active = active, Ownership = thread.MediaDirectories.Select(directory => new
                { Directory = directory, Owners = OrderedCleanupIds(owners[directory]) }).ToArray() });
            rows.Add(new(thread.Summary, media, eligibility, frozenReasons, rowToken));
        }
        if (stale) errors.Add("Archive, media, protection or work evidence changed. Refresh the preview.");
        string evidence = ArchiveCleanupFingerprint.Of(new { Profile = ArchiveCleanupProfileBinding, Policy = 1,
            Rows = rows.Select(row => row.EvidenceToken).ToArray(), before.ProtectionToken, before.WorkToken });
        return new(ArchiveCleanupProfileBinding, 1, Array.AsReadOnly(rows.ToArray()), Array.AsReadOnly(open), evidence,
            before.ProtectionToken, before.WorkToken, before.Reliable && after.Reliable, stale,
            Array.AsReadOnly(errors.Distinct().ToArray()));
    }

    private async Task<IReadOnlyList<ArchiveDeletedThread>> ReadCleanupCatalogAsync(CancellationToken token)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(ProfileDirectory, token).ConfigureAwait(false);
        List<ArchiveDeletedThread> threads = new();
        HashSet<ResourceId> seen = new();
        int total = -1;
        for (int offset = 0; total < 0 || offset < total; offset += 500)
        {
            token.ThrowIfCancellationRequested();
            ArchiveThreadPage page = await reader.ListThreadsAsync(offset, 500, token).ConfigureAwait(false);
            if (total < 0) total = page.TotalCount;
            if (page.TotalCount != total || page.Items.Count == 0 && offset < total)
                throw new InvalidDataException("The archive changed during paging.");
            foreach (ArchiveThreadSummary summary in page.Items)
            {
                if (!seen.Add(summary.Id)) throw new InvalidDataException("The archive changed during paging.");
                var directories = await reader.GetMediaDirectoriesAsync(summary.Id, token).ConfigureAwait(false);
                string[] paths = directories.Append(summary.MediaDirectory).Select(directory =>
                {
                    try { return ArchiveCleanupMediaInventory.Canonical(directory); }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException) { return directory; }
                }).Distinct(ArchiveCleanupMediaInventory.Paths).OrderBy(path => path, ArchiveCleanupMediaInventory.Paths).ToArray();
                threads.Add(new(summary, Array.AsReadOnly(paths)));
            }
        }
        if (seen.Count != total) throw new InvalidDataException("The archive inventory is incomplete.");
        return Array.AsReadOnly(threads.OrderBy(item => item.Summary.Id.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Summary.Id.Board, StringComparer.Ordinal).ThenBy(item => item.Summary.Id.Thread, StringComparer.Ordinal).ToArray());
    }
}
