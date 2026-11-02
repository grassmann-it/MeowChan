using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

internal sealed partial class PostBookmarkStore
{
    // Separate from legacy temps: an interrupted private publication must not be
    // silently removed by ordinary constructor cleanup. Not a backup or a journal.
    private const string PrivatePendingPrefix = "post-bookmarks.json.private-pending-";
    private const NativePrivateApplyStoreKind BookmarkStoreKind = NativePrivateApplyStoreKind.PostBookmarksAndNotes;

    // Internal persistence primitive only. No consent/target/lifecycle authority,
    // journal producer, retry scheduler or marker-retirement seam is supplied here.
    internal PrivateStoreBatch PrepareAdditiveBatch(Guid operationId, string contentPlanSha256,
        IReadOnlyList<PostBookmark> additions, CancellationToken token = default)
        => _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            var marker = new PrivateStoreOperationMarker(operationId, contentPlanSha256, BookmarkStoreKind);
            PrivateStoreOperationMarker.Validate(marker, BookmarkStoreKind);
            if (additions is null || additions.Count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();
            try
            {
                // Snapshot and precheck the entire incoming budget before building
                // an after document. Never normalize, trim or repair incoming text.
                List<PostBookmark> input = new(additions.Count);
                HashSet<PostBookmarkKey> seen = new();
                long incomingBytes = 0;
                foreach (PostBookmark entry in additions)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry is null || entry.Key is null || entry.Note is null) throw PrivateStoreBatchFiles.Invalid();
                    ValidateBatchKey(entry.Key);
                    if (!string.Equals(entry.Note, NormalizeNote(entry.Note), StringComparison.Ordinal)
                        || !seen.Add(entry.Key)) throw PrivateStoreBatchFiles.Invalid();
                    incomingBytes = checked(incomingBytes + StrictUtf8.GetByteCount(entry.Key.Thread.Source)
                        + StrictUtf8.GetByteCount(entry.Key.Thread.Board) + StrictUtf8.GetByteCount(entry.Key.Thread.Thread!)
                        + StrictUtf8.GetByteCount(entry.Key.Post) + StrictUtf8.GetByteCount(entry.Note));
                    if (incomingBytes > MaximumDocumentBytes) throw PrivateStoreBatchFiles.Invalid();
                    input.Add(entry);
                }

                EnsureNoPrivatePending();
                var current = ReadBookmarkBatchState();
                PrivateStoreBatchFiles.EnsureUnmarked(current.Document.OperationMarker);
                Dictionary<PostBookmarkKey, string> existing = current.Document.Snapshot.Bookmarks
                    .ToDictionary(entry => entry.Key, entry => entry.Note);
                List<PostBookmark> added = new();
                foreach (PostBookmark entry in input)
                {
                    token.ThrowIfCancellationRequested();
                    if (existing.TryGetValue(entry.Key, out string? note))
                    {
                        if (!string.Equals(note, entry.Note, StringComparison.Ordinal)) throw PrivateStoreBatchFiles.Invalid();
                    }
                    else added.Add(entry);
                }
                if ((long)existing.Count + added.Count > MaximumEntries) throw PrivateStoreBatchFiles.Invalid();
                if (added.Count == 0)
                    return new PrivateStoreBatch(_batchOwner, marker,
                        new(BookmarkStoreKind, current.Image, current.Image), null, 0);

                long revision = checked(current.Image.Revision + 1);
                Guid instance = current.Document.InstanceId ?? operationId;
                List<PostBookmark> values = current.Document.Snapshot.Bookmarks.ToList();
                values.AddRange(added.OrderBy(e => e.Key.Thread.Source, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Thread.Board, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Thread.Thread, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Post, StringComparer.Ordinal));
                byte[] bytes = SerializeBookmarkBatch(values, revision, instance, marker);
                token.ThrowIfCancellationRequested();
                return new PrivateStoreBatch(_batchOwner, marker,
                    new(BookmarkStoreKind, current.Image, PrivateStoreBatchFiles.Image(bytes, 2, instance, revision)),
                    bytes, added.Count);
            }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { throw PrivateStoreBatchFiles.Invalid(); }
        });

    internal PrivateStoreBatchInspection InspectPreparedBatch(PrivateStoreBatch batch)
        => _lease.RunExclusive(() =>
        {
            EnsureOwnBookmarkBatch(batch);
            return InspectBookmarkBatchCore(batch);
        });

    internal PrivateStoreBatchInspection CommitPreparedBatch(PrivateStoreBatch batch, CancellationToken token = default)
        => _lease.RunExclusive(() =>
        {
            EnsureOwnBookmarkBatch(batch);
            token.ThrowIfCancellationRequested();
            PrivateStoreBatchInspection status = InspectBookmarkBatchCore(batch);
            if (status is PrivateStoreBatchInspection.ExactAfterWithMarker or PrivateStoreBatchInspection.NoChange)
                return status;
            if (status != PrivateStoreBatchInspection.ExactBefore) throw PrivateStoreBatchFiles.Invalid();
            EnsureNoPrivatePending();
            PrivateStoreBatchFiles.EnsureUnmarked(ReadBookmarkBatchState().Document.OperationMarker);

            // Keep the established bookmark flush + single-file replacement path.
            // No AtomicJsonFile (which could introduce note backups) is used.
            string pending = System.IO.Path.Combine(_lease.ProfileDirectory,
                PrivatePendingPrefix + Guid.NewGuid().ToString("N"));
            bool created = false, publicationAttempted = false, publicationVerified = false;
            try
            {
                byte[] bytes = batch.CopyAfterBytes();
                using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                // Recheck exact primary immediately before the publication attempt,
                // after writing our temp. This is a cooperative lease fence, not CAS
                // against an arbitrary external editor or an old application.
                if (InspectBookmarkBatchCore(batch) != PrivateStoreBatchInspection.ExactBefore)
                    throw PrivateStoreBatchFiles.Invalid();
                publicationAttempted = true;
                File.Move(pending, Path, overwrite: true);
                status = InspectBookmarkBatchCore(batch); // Deliberately uncancelled.
                publicationVerified = status == PrivateStoreBatchInspection.ExactAfterWithMarker;
                if (!publicationVerified) throw new IOException("Private bookmark publication could not be verified.");
                return status;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A possible publication is not failure/nonpublication merely
                // because the replace acknowledgement/read was lost.
                if (publicationAttempted && InspectBookmarkBatchCore(batch) == PrivateStoreBatchInspection.ExactAfterWithMarker)
                {
                    publicationVerified = true;
                    return PrivateStoreBatchInspection.ExactAfterWithMarker;
                }
                throw new IOException("Private bookmark publication could not be verified.");
            }
            finally
            {
                // Delete only this invocation's temp, never unknown pending evidence.
                // Unknown post-replace outcomes stay blocked for later integration.
                if (created && (!publicationAttempted || publicationVerified))
                {
                    try { File.Delete(pending); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        });

    private PrivateStoreBatchInspection InspectBookmarkBatchCore(PrivateStoreBatch batch)
    {
        try
        {
            var current = ReadBookmarkBatchState();
            if (batch.HasChanges && PrivateStoreBatchFiles.Same(current.Image, batch.Fence.ExpectedAfter)
                && current.Document.OperationMarker == batch.Marker)
                return PrivateStoreBatchInspection.ExactAfterWithMarker;
            if (current.Document.OperationMarker is null && PrivateStoreBatchFiles.Same(current.Image, batch.Fence.Before))
                return batch.HasChanges ? PrivateStoreBatchInspection.ExactBefore : PrivateStoreBatchInspection.NoChange;
            return PrivateStoreBatchInspection.Drifted;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return PrivateStoreBatchInspection.Unreadable; }
    }

    private void EnsureOwnBookmarkBatch(PrivateStoreBatch batch)
    {
        if (batch is null || !batch.IsOwnedBy(_batchOwner) || batch.Fence.Kind != BookmarkStoreKind
            || batch.Fence.BeforeBackup is not null)
            throw PrivateStoreBatchFiles.Invalid();
    }

    private (ParsedFile Document, NativePrivateApplyStoreImage Image) ReadBookmarkBatchState()
    {
        byte[]? bytes = PrivateStoreBatchFiles.ReadBytes(Path, MaximumDocumentBytes);
        ParsedFile document = ParseBookmarkBytes(bytes, exactNotes: true);
        return (document, bytes is null ? PrivateStoreBatchFiles.Missing()
            : PrivateStoreBatchFiles.Image(bytes, document.SchemaVersion, document.InstanceId, document.Snapshot.Revision));
    }

    private static void ValidateBatchKey(PostBookmarkKey key)
    {
        key.Validate();
        StrictUtf8.GetByteCount(key.Thread.Source);
        StrictUtf8.GetByteCount(key.Thread.Board);
        StrictUtf8.GetByteCount(key.Thread.Thread!);
        StrictUtf8.GetByteCount(key.Post);
    }

    private static byte[] SerializeBookmarkBatch(IReadOnlyList<PostBookmark> entries, long revision,
        Guid instance, PrivateStoreOperationMarker marker)
        => PrivateStoreBatchFiles.SerializeBounded(new
        {
            schemaVersion = 2,
            revision,
            bookmarks = entries.Select(e => new
            {
                source = e.Key.Thread.Source, board = e.Key.Thread.Board, thread = e.Key.Thread.Thread,
                post = e.Key.Post, note = e.Note
            }).ToArray(),
            instanceId = instance,
            operationMarker = marker
        }, MaximumDocumentBytes);

    private bool HasPrivatePending() => Directory.EnumerateFileSystemEntries(_lease.ProfileDirectory,
        PrivatePendingPrefix + "*").Any(file => Guid.TryParseExact(
            System.IO.Path.GetFileName(file)[PrivatePendingPrefix.Length..], "N", out _));

    private void EnsureNoPrivatePending()
    {
        try
        {
            if (HasPrivatePending()) throw PrivateStoreBatchFiles.Invalid();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("Private bookmark recovery evidence could not be verified."); }
    }
}
