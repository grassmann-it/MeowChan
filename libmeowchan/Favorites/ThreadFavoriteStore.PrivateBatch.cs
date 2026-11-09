using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Favorites;

public sealed partial class ThreadFavoriteStore
{
    private readonly object _batchOwner = new();
    // Internal persistence primitive, deliberately not connected to import admission.
    // Caller must later supply owner/lifecycle/consent and durable intent protocol.
    internal PrivateStoreBatch PrepareAdditiveBatch(Guid operationId, string contentPlanSha256,
        IReadOnlyList<ResourceId> additions, CancellationToken token = default)
        => _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            var marker = new PrivateStoreOperationMarker(operationId, contentPlanSha256, NativePrivateApplyStoreKind.ThreadFavorites);
            PrivateStoreOperationMarker.Validate(marker, NativePrivateApplyStoreKind.ThreadFavorites);
            ArgumentNullException.ThrowIfNull(additions);
            if (additions.Count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();
            var current = ReadBatchState();
            PrivateStoreBatchFiles.EnsureUnmarked(current.Primary?.OperationMarker);
            PrivateStoreBatchFiles.EnsureUnmarked(current.Backup?.OperationMarker);
            HashSet<ResourceId> input = new(), existing = current.Primary?.Threads.ToHashSet() ?? [];
            List<ResourceId> added = new();
            long inputBytes = 0;
            foreach (ResourceId id in additions)
            {
                token.ThrowIfCancellationRequested(); Validate(id);
                if (!input.Add(id)) throw PrivateStoreBatchFiles.Invalid();
                if (existing.Add(id))
                {
                    inputBytes = checked(inputBytes + StrictUtf8.GetByteCount(id.Source)
                        + StrictUtf8.GetByteCount(id.Board) + StrictUtf8.GetByteCount(id.Thread!));
                    if (inputBytes > MaximumBytes) throw PrivateStoreBatchFiles.Invalid();
                    added.Add(id);
                }
            }
            if (existing.Count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();
            if (added.Count == 0)
                return new PrivateStoreBatch(_batchOwner, marker, new(NativePrivateApplyStoreKind.ThreadFavorites,
                    current.Image, current.Image, current.BackupImage), null, 0);
            ResourceId[] ordered = added.OrderBy(i => i.Source, StringComparer.Ordinal)
                .ThenBy(i => i.Board, StringComparer.Ordinal).ThenBy(i => i.Thread, StringComparer.Ordinal).ToArray();
            List<ResourceId> values = current.Primary?.Threads.ToList() ?? [];
            values.AddRange(ordered);
            var next = new Document(2, checked(current.Image.Revision + 1), values,
                current.Primary?.InstanceId ?? operationId, marker);
            byte[] bytes = PrivateStoreBatchFiles.SerializeBounded(next, MaximumBytes);
            if (bytes.Length > MaximumBytes) throw PrivateStoreBatchFiles.Invalid();
            token.ThrowIfCancellationRequested();
            return new PrivateStoreBatch(_batchOwner, marker, new(NativePrivateApplyStoreKind.ThreadFavorites,
                current.Image, PrivateStoreBatchFiles.Image(bytes, 2, next.InstanceId, next.Revision), current.BackupImage), bytes, added.Count);
        });

    internal PrivateStoreBatchInspection InspectPreparedBatch(PrivateStoreBatch batch)
        => _lease.RunExclusive(() => InspectPreparedBatchCore(batch));

    private PrivateStoreBatchInspection InspectPreparedBatchCore(PrivateStoreBatch batch)
    {
        EnsureOwnBatch(batch);
        try
        {
            var current = ReadBatchState();
            if (batch.HasChanges && PrivateStoreBatchFiles.Same(current.Image, batch.Fence.ExpectedAfter)
                && current.Primary?.OperationMarker == batch.Marker
                && PrivateStoreBatchFiles.Same(current.BackupImage,
                    batch.Fence.Before.Exists ? batch.Fence.Before : batch.Fence.BeforeBackup!))
                return PrivateStoreBatchInspection.ExactAfterWithMarker;
            if (PrivateStoreBatchFiles.Same(current.Image, batch.Fence.Before)
                && PrivateStoreBatchFiles.Same(current.BackupImage, batch.Fence.BeforeBackup!))
                return batch.HasChanges ? PrivateStoreBatchInspection.ExactBefore : PrivateStoreBatchInspection.NoChange;
            return PrivateStoreBatchInspection.Drifted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { return PrivateStoreBatchInspection.Unreadable; }
    }

    internal PrivateStoreMarkerRetirementResult RetirePreparedBatchMarker(PrivateStoreBatch batch,
        Guid operationId, string contentPlanSha256, NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage? expectedBackup,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStartupWriter(token);
        return RetirePreparedBatchMarkerAdmitted(batch, operationId, contentPlanSha256, storeKind,
            expectedAfter, expectedBackup, admission, token);
    }

    internal PrivateStoreMarkerRetirementResult RetirePreparedBatchMarkerAdmitted(PrivateStoreBatch batch,
        Guid operationId, string contentPlanSha256, NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage? expectedBackup,
        ProfileAdmissionToken admission, CancellationToken token = default)
        => _lease.RunPrivateStoreCommit(admission, () => RetirePreparedBatchMarkerCore(batch, operationId,
            contentPlanSha256, storeKind, expectedAfter, expectedBackup, token));

    private PrivateStoreMarkerRetirementResult RetirePreparedBatchMarkerCore(PrivateStoreBatch batch,
        Guid operationId, string contentPlanSha256, NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage? expectedBackup,
        CancellationToken token)
    {
        EnsureOwnBatch(batch);
        PrivateStoreMarkerRetirement.ValidateRequest(batch, operationId, contentPlanSha256, storeKind,
            expectedAfter, expectedBackup);
        token.ThrowIfCancellationRequested();
        PrivateStoreMarkerRetirement.EnsureNoPending(_lease, "thread-favorites.json");

        // Derive the retry fence from the bound marker-bearing after-image. Do
        // not canonicalize the current unmarked document: a later unrelated
        // write must never be acknowledged as this batch's retirement.
        Document boundAfter = ParseDocument(batch.CopyAfterBytes());
        byte[] retiredBytes = CreateRetiredBytes(boundAfter);
        NativePrivateApplyStoreImage expectedRetired = PrivateStoreBatchFiles.Image(retiredBytes, 1,
            null, boundAfter.Revision);
        var current = ReadBatchState();
        NativePrivateApplyStoreImage backupFence = expectedBackup!;
        if (current.Primary is { } primary && primary.OperationMarker is null
            && PrivateStoreBatchFiles.Same(current.Image, expectedRetired)
            && PrivateStoreBatchFiles.Same(current.BackupImage, backupFence))
            return PrivateStoreMarkerRetirementResult.AlreadyRetired;

        if (current.Primary?.OperationMarker != batch.Marker
            || !PrivateStoreBatchFiles.Same(current.Image, expectedAfter)
            || !PrivateStoreBatchFiles.Same(current.BackupImage, backupFence))
            throw PrivateStoreBatchFiles.Invalid();

        PrivateStoreMarkerRetirement.EnsureReadback(_lease);
        try
        {
            AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(retiredBytes),
                backupCurrentFile: false, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (ObserveRetiredBatch(retiredBytes, backupFence))
                return PrivateStoreMarkerRetirementResult.Retired;
            throw new IOException("The private store marker retirement could not be verified.");
        }

        if (!ObserveRetiredBatch(retiredBytes, backupFence))
            throw new IOException("The private store marker retirement could not be verified.");
        return PrivateStoreMarkerRetirementResult.Retired;
    }

    private bool ObserveRetiredBatch(byte[] retiredBytes, NativePrivateApplyStoreImage backupFence)
    {
        try
        {
            var current = ReadBatchState();
            if (current.Primary is not { } primary) return false;
            bool exact = primary.OperationMarker is null
                && PrivateStoreBatchFiles.Same(current.Image, PrivateStoreBatchFiles.Image(retiredBytes, 1,
                    null, primary.Revision))
                && PrivateStoreBatchFiles.Same(current.BackupImage, backupFence);
            PrivateStoreMarkerRetirement.EnsureReadback(_lease);
            return exact;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static byte[] CreateRetiredBytes(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var retired = document with { Version = 1, InstanceId = null, OperationMarker = null };
        return PrivateStoreBatchFiles.SerializeBounded(retired, MaximumBytes);
    }

    internal PrivateStoreBatchInspection CommitPreparedBatch(PrivateStoreBatch batch, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStartupWriter(token);
        return CommitPreparedBatchAdmitted(batch, admission, token);
    }

    internal PrivateStoreBatchInspection CommitPreparedBatchAdmitted(PrivateStoreBatch batch,
        ProfileAdmissionToken admission, CancellationToken token = default)
        => _lease.RunPrivateStoreCommit(admission, () => CommitPreparedBatchCore(batch, token));

    private PrivateStoreBatchInspection CommitPreparedBatchCore(PrivateStoreBatch batch, CancellationToken token)
    {
        EnsureOwnBatch(batch);
        token.ThrowIfCancellationRequested();
        PrivateStoreBatchInspection status = InspectPreparedBatchCore(batch);
        if (status is PrivateStoreBatchInspection.ExactAfterWithMarker or PrivateStoreBatchInspection.NoChange) return status;
        if (status != PrivateStoreBatchInspection.ExactBefore) throw PrivateStoreBatchFiles.Invalid();
        try
        {
            AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(batch.CopyAfterBytes()), backupCurrentFile: true, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (InspectPreparedBatchCore(batch) == PrivateStoreBatchInspection.ExactAfterWithMarker)
                return PrivateStoreBatchInspection.ExactAfterWithMarker;
            throw new IOException("The private store batch publication could not be verified.");
        }
        // Never turn a committed replacement into cancellation. This is an
        // independent bounded primary/marker read, not cached setter success.
        status = InspectPreparedBatchCore(batch);
        if (status != PrivateStoreBatchInspection.ExactAfterWithMarker)
            throw new IOException("The private store batch publication could not be verified.");
        return status;
    }

    private void EnsureOwnBatch(PrivateStoreBatch batch)
    {
        if (batch is null || !batch.IsOwnedBy(_batchOwner) || batch.Fence.Kind != NativePrivateApplyStoreKind.ThreadFavorites)
            throw PrivateStoreBatchFiles.Invalid();
    }

    private (Document? Primary, NativePrivateApplyStoreImage Image, Document? Backup, NativePrivateApplyStoreImage BackupImage) ReadBatchState()
    {
        (Document? p, NativePrivateApplyStoreImage image) = ReadBatchImage(_path);
        (Document? b, NativePrivateApplyStoreImage backup) = ReadBatchImage(_path + ".bak");
        if (p is null && b is not null) throw PrivateStoreBatchFiles.Invalid();
        return (p, image, b, backup);
    }

    private static (Document? Document, NativePrivateApplyStoreImage Image) ReadBatchImage(string path)
    {
        byte[]? bytes = PrivateStoreBatchFiles.ReadBytes(path, MaximumBytes);
        if (bytes is null) return (null, PrivateStoreBatchFiles.Missing());
        Document document = ParseDocument(bytes);
        return (document, PrivateStoreBatchFiles.Image(bytes, document.Version, document.InstanceId, document.Revision));
    }
}
