using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Favorites;

public sealed partial class FavoriteStore
{
    // Only this store instance can mint a usable batch. A public FavoriteStore
    // reference, profile path or caller-constructed fence is not that capability.
    private readonly object _batchOwner = new();

    // Internal store primitive, NOT import admission, consent or a media resolver.
    // Length/name must already have been derived from independently verified local
    // media. This slice cannot establish host/catalog/journal/lifecycle authority.
    internal PrivateStoreBatch PrepareAdditiveBatch(Guid operationId, string contentPlanSha256,
        IReadOnlyList<FavoriteEntry> additions, CancellationToken token = default)
        => _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var marker = new PrivateStoreOperationMarker(operationId, contentPlanSha256,
                    NativePrivateApplyStoreKind.MediaFavorites);
                PrivateStoreOperationMarker.Validate(marker, NativePrivateApplyStoreKind.MediaFavorites);
                if (additions is null) throw PrivateStoreBatchFiles.Invalid();
                int count = additions.Count;
                if (count < 0 || count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();

                // Validate/copy just the supplied additions, never a whole incoming
                // collection. Bound cumulative UTF8 values BEFORE indices, sorting or
                // whole-store serialization. No normalization of identifiers/names.
                var selected = new List<FavoriteEntry>(count);
                var seen = new HashSet<FavoriteAssetKey>();
                long inputBytes = 0;
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    FavoriteEntry entry = additions[i];
                    if (entry is null || entry.Key is null || entry.Key.Thread is null || entry.Length < 0)
                        throw PrivateStoreBatchFiles.Invalid();
                    CountBatchText(entry.Key.Thread.Source, ref inputBytes);
                    CountBatchText(entry.Key.Thread.Board, ref inputBytes);
                    CountBatchText(entry.Key.Thread.Thread, ref inputBytes);
                    CountBatchText(entry.Key.MediaIdentifier, ref inputBytes);
                    CountBatchText(entry.Key.Variant, ref inputBytes);
                    CountBatchText(entry.Key.Sha256, ref inputBytes);
                    if (entry.OriginalFileName is not null) CountBatchText(entry.OriginalFileName, ref inputBytes);
                    Validate(entry.Key);
                    if (!seen.Add(entry.Key)) throw PrivateStoreBatchFiles.Invalid();
                    selected.Add(entry);
                }
                var current = ReadBatchState();
                PrivateStoreBatchFiles.EnsureUnmarked(current.Primary?.OperationMarker);
                PrivateStoreBatchFiles.EnsureUnmarked(current.Backup?.OperationMarker);
                HashSet<FavoriteAssetKey> existing = current.Primary?.Entries.Select(e => e.Key).ToHashSet() ?? [];
                var added = new List<FavoriteEntry>();
                foreach (FavoriteEntry entry in selected)
                {
                    token.ThrowIfCancellationRequested();
                    // Identity already present: retain its EXACT prior length/name.
                    // Positive-only union has no replace, unmark or delete operation.
                    if (existing.Add(entry.Key)) added.Add(entry);
                }
                if (existing.Count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();
                token.ThrowIfCancellationRequested();
                if (added.Count == 0)
                    return new PrivateStoreBatch(_batchOwner, marker, new(NativePrivateApplyStoreKind.MediaFavorites,
                        current.Image, current.Image, current.BackupImage), null, 0);

                // Retain prior order/metadata, append additions in ordinal component
                // order. Same before-image + selected values + operation = same bytes.
                var values = current.Primary?.Entries.ToList() ?? [];
                values.AddRange(added.OrderBy(e => e.Key.Thread.Source, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Thread.Board, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Thread.Thread, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.MediaIdentifier, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Variant, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Sha256, StringComparer.Ordinal));
                var next = new Document(3, values, current.Primary?.InstanceId ?? operationId,
                    checked(current.Image.Revision + 1), marker);
                byte[] bytes = PrivateStoreBatchFiles.SerializeBounded(next, MaximumBytes);
                token.ThrowIfCancellationRequested();
                return new PrivateStoreBatch(_batchOwner, marker, new(NativePrivateApplyStoreKind.MediaFavorites,
                    current.Image, PrivateStoreBatchFiles.Image(bytes, 3, next.InstanceId, next.Revision),
                    current.BackupImage), bytes, added.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
            { throw PrivateStoreBatchFiles.Invalid(); }
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
        if (status is PrivateStoreBatchInspection.ExactAfterWithMarker or PrivateStoreBatchInspection.NoChange)
            return status;
        if (status != PrivateStoreBatchInspection.ExactBefore) throw PrivateStoreBatchFiles.Invalid();
        try
        {
            AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(batch.CopyAfterBytes()),
                backupCurrentFile: true, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Publication may already have happened. Observe uncancelled; never
            // replay a write or infer success from the setter returning/throwing.
            if (InspectPreparedBatchCore(batch) == PrivateStoreBatchInspection.ExactAfterWithMarker)
                return PrivateStoreBatchInspection.ExactAfterWithMarker;
            throw new IOException("The private store batch publication could not be verified.");
        }
        // No cancellation check after publication; strictly read primary+marker
        // AND rotated backup, rather than reporting cached preparation evidence.
        status = InspectPreparedBatchCore(batch);
        if (status != PrivateStoreBatchInspection.ExactAfterWithMarker)
            throw new IOException("The private store batch publication could not be verified.");
        return status;
    }

    private static void CountBatchText(string? text, ref long total)
    {
        if (text is null || text.Length > MaximumBytes - total) throw PrivateStoreBatchFiles.Invalid();
        total = checked(total + StrictUtf8.GetByteCount(text));
        if (total > MaximumBytes) throw PrivateStoreBatchFiles.Invalid();
    }

    private void EnsureOwnBatch(PrivateStoreBatch batch)
    {
        if (batch is null || !batch.IsOwnedBy(_batchOwner) || batch.Fence.Kind != NativePrivateApplyStoreKind.MediaFavorites)
            throw PrivateStoreBatchFiles.Invalid();
    }

    private (Document? Primary, NativePrivateApplyStoreImage Image,
        Document? Backup, NativePrivateApplyStoreImage BackupImage) ReadBatchState()
    {
        var primary = ReadBatchImage(_path);
        var backup = ReadBatchImage(_path + ".bak");
        if (primary.Document is null && backup.Document is not null) throw PrivateStoreBatchFiles.Invalid();
        return (primary.Document, primary.Image, backup.Document, backup.Image);
    }

    private static (Document? Document, NativePrivateApplyStoreImage Image) ReadBatchImage(string path)
    {
        byte[]? bytes = PrivateStoreBatchFiles.ReadBytes(path, MaximumBytes);
        if (bytes is null) return (null, PrivateStoreBatchFiles.Missing());
        Document document = ParseDocument(bytes);
        return (document, PrivateStoreBatchFiles.Image(bytes, document.Version, document.InstanceId, document.Revision));
    }
}
