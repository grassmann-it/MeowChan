using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Archive;

internal enum MediaPromoteStatus { Promoted, AlreadyPromoted, Missing, NotAllowed, BytesUnavailable, Changed }
internal sealed record MediaPromoteResult(MediaPromoteStatus Status, string? Directory = null);

public sealed partial class ArchiveCatalog
{
    private sealed record PromotePlan(PersistedMediaConflict Conflict, string FullDirectory,
        IReadOnlyList<string> Roots, long ThreadRevision);

    /// <summary>
    /// Makes a conflict's inactive alternative the active version of its slot. The new root is written and
    /// verified first, then registered together with the ledger decision in one transaction; a crash before
    /// that transaction leaves a removable, unreferenced root and an undecided ledger row. Repeatable.
    /// </summary>
    internal Task<MediaPromoteResult> PromoteMediaConflictAsync(string identitySha256, MediaStore store,
        CancellationToken cancellationToken = default)
    {
        ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return CompleteOwnedWriteAsync(admission,
            () => PromoteMediaConflictAdmittedAsync(admission, identitySha256, store, cancellationToken));
    }

    internal async Task<MediaPromoteResult> PromoteMediaConflictAdmittedAsync(ProfileAdmissionToken admission,
        string identitySha256, MediaStore store, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identitySha256);
        ArgumentNullException.ThrowIfNull(store);
        PromoteRead read = await WriteAdmittedAsync(admission, (connection, transaction) =>
        {
            PersistedMediaConflict? row = ArchiveSql.ReadMediaConflict(connection, transaction, identitySha256);
            if (row == null) return new PromoteRead(MediaPromoteStatus.Missing, null);
            NativeMediaConflictResolutionStatus status = row.Entry.Resolution;
            if (status == NativeMediaConflictResolutionStatus.Promoted) return new PromoteRead(MediaPromoteStatus.AlreadyPromoted, null);
            if (status is not (NativeMediaConflictResolutionStatus.Open or NativeMediaConflictResolutionStatus.Kept))
                return new PromoteRead(MediaPromoteStatus.NotAllowed, null);
            ResourceId id = row.Entry.Identity.Resource;
            NativeMediaPersistedMergeOperation? operation = ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction,
                row.Provenance.OperationId, row.Provenance.RequestSha256);
            if (operation?.Full.Directory is not { } full) return new PromoteRead(MediaPromoteStatus.BytesUnavailable, null);
            long revision = ArchiveSql.ReadThreadRevision(connection, transaction, id);
            return new PromoteRead(MediaPromoteStatus.Promoted,
                new PromotePlan(row, full, ArchiveSql.ReadMediaDirectories(connection, transaction, id), revision));
        }, cancellationToken).ConfigureAwait(false);
        if (read.Plan is not { } plan) return new(read.Status);

        ResourceId resource = plan.Conflict.Entry.Identity.Resource;
        string active = plan.Conflict.Entry.Identity.ActiveVariantSha256;
        string candidate = plan.Conflict.Entry.Identity.CandidateVariantSha256;
        MediaAssetSnapshot full = await MediaAssetReader.OpenAsync(plan.FullDirectory, cancellationToken).ConfigureAwait(false);
        if (full.Status != MediaManifestStatus.Available
            || !full.AttachmentBindings.Values.Any(b => string.Equals(b.Actual.Sha256, candidate, StringComparison.OrdinalIgnoreCase)))
            return new(MediaPromoteStatus.BytesUnavailable);

        // The occurrence digest identifies the slot; the sender's evidence in the Full root maps it to post/position.
        HashSet<(string Post, int Position)> slots = full.OriginalEvidence
            .Where(e => OccurrenceDigest(e.Occurrence) == plan.Conflict.Entry.Identity.OccurrenceSha256)
            .Select(e => (e.Occurrence.PostId, e.Occurrence.Position)).ToHashSet();
        List<MediaAttachmentBinding> replaced = new();
        foreach (string directory in plan.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status != MediaManifestStatus.Available) continue;
            replaced.AddRange(snapshot.AttachmentBindings.Values.Where(b => b.Reference.Resource == resource
                && slots.Contains((b.Reference.PostId, b.Reference.Position))
                && string.Equals(b.Actual.Sha256, active, StringComparison.OrdinalIgnoreCase)));
        }
        if (replaced.Count == 0) return new(MediaPromoteStatus.Changed);

        string target = Path.Combine(Path.GetDirectoryName(plan.FullDirectory)!,
            "promote-" + plan.Conflict.Entry.Identity.IdentitySha256[..32]);
        if (!Directory.Exists(target))
            await store.CreatePromoteRootAsync(plan.FullDirectory, target, candidate, replaced, cancellationToken).ConfigureAwait(false);
        else
        {
            // A retry after a crash between root creation and the transaction: accept only an identical root.
            MediaAssetSnapshot existing = await MediaAssetReader.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (existing.Status != MediaManifestStatus.Available
                || !existing.Supersedes.OrderBy(s => s.RevisionKey, StringComparer.Ordinal).SequenceEqual(
                    replaced.Select(b => new MediaSupersession(b.Reference.RevisionKey, b.Actual.Sha256)).OrderBy(s => s.RevisionKey, StringComparer.Ordinal)))
                throw new InvalidDataException("A different promote root already occupies the target.");
        }

        return await WriteAdmittedAsync(admission, (connection, transaction) =>
        {
            PersistedMediaConflict? now = ArchiveSql.ReadMediaConflict(connection, transaction, identitySha256);
            if (now == null) return new MediaPromoteResult(MediaPromoteStatus.Missing);
            if (now.Entry.Resolution == NativeMediaConflictResolutionStatus.Promoted) return new(MediaPromoteStatus.AlreadyPromoted, target);
            // Fence: the slot's roots or revision moved since the verified plan, so the decision is not applied.
            if (ArchiveSql.ReadThreadRevision(connection, transaction, resource) != plan.ThreadRevision
                || !ArchiveSql.ReadMediaDirectories(connection, transaction, resource).SequenceEqual(plan.Roots))
                return new(MediaPromoteStatus.Changed);
            ArchiveSql.RegisterMediaDirectory(connection, transaction, resource, target);
            ArchiveSql.MediaConflictDecisionOutcome outcome = ArchiveSql.DecideMediaConflict(connection, transaction,
                identitySha256, NativeMediaConflictResolutionStatus.Promoted, DateTimeOffset.UtcNow);
            if (outcome is not (ArchiveSql.MediaConflictDecisionOutcome.Applied or ArchiveSql.MediaConflictDecisionOutcome.AlreadyApplied))
                throw new InvalidDataException("The promote could not be recorded.");
            ArchiveSql.BumpThreadRevision(connection, transaction, resource);
            return new(MediaPromoteStatus.Promoted, target);
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PromoteRead(MediaPromoteStatus Status, PromotePlan? Plan);

    // Must match the digest the merge planner records in the ledger.
    private static string OccurrenceDigest(MediaOccurrence occurrence) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\0', "native-media-conflict-occurrence-v1", occurrence.Resource.Source, occurrence.Resource.Board,
            occurrence.Resource.Thread, occurrence.PostId, occurrence.Position.ToString(CultureInfo.InvariantCulture),
            occurrence.OriginalRevisionKey ?? "")))).ToLowerInvariant();
}
