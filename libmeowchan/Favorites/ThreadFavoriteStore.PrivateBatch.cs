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
        => _lease.RunExclusive(() =>
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
        });

    internal PrivateStoreBatchInspection CommitPreparedBatch(PrivateStoreBatch batch, CancellationToken token = default)
        => _lease.RunExclusive(() =>
        {
            EnsureOwnBatch(batch);
            token.ThrowIfCancellationRequested();
            PrivateStoreBatchInspection status = InspectPreparedBatch(batch);
            if (status is PrivateStoreBatchInspection.ExactAfterWithMarker or PrivateStoreBatchInspection.NoChange) return status;
            if (status != PrivateStoreBatchInspection.ExactBefore) throw PrivateStoreBatchFiles.Invalid();
            try
            {
                AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(batch.CopyAfterBytes()), backupCurrentFile: true, token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (InspectPreparedBatch(batch) == PrivateStoreBatchInspection.ExactAfterWithMarker)
                    return PrivateStoreBatchInspection.ExactAfterWithMarker;
                throw new IOException("The private store batch publication could not be verified.");
            }
            // Never turn a committed replacement into cancellation. This is an
            // independent bounded primary/marker read, not cached setter success.
            status = InspectPreparedBatch(batch);
            if (status != PrivateStoreBatchInspection.ExactAfterWithMarker)
                throw new IOException("The private store batch publication could not be verified.");
            return status;
        });

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
