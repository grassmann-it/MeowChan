using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // This is a second plan representation, NOT a second executor. The existing
    // coordinator owns claim, materialization, commit and independent reconciliation.
    private async Task<NativeMediaMergePreparation> PrepareNativeMediaInventoryMergeAsync(
        NativeTransferImportStage stage, CancellationToken token)
    {
        NativeTransferPackage package = stage.Package;
        TransferThread thread = package.Catalog.Threads[0];
        if (thread.PageUrl == null) return new(null, NativeMediaMergeError.UnsupportedPackage);
        NativeMediaMaterializationPlan materialization = NativeMediaMaterializationPlan.Create(stage, thread.Id, token);
        if (materialization.FullSelection.IsEmpty) return new(null, NativeMediaMergeError.UnsupportedPackage);
        var read = await ReadMediaMergeTargetAsync(thread.Id, token).ConfigureAwait(false);
        if (read.error != NativeMediaMergeError.None) return new(null, read.error);
        ArchivedThreadView target = read.target!;
        try
        {
            return await new MediaStore().WithPassiveMergeInventoryTargetRootsAsync(target.MediaDirectories, snapshots =>
                Task.FromResult(Plan(snapshots)), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or OverflowException)
        { return new(null, NativeMediaMergeError.TargetUnavailable); }

        NativeMediaMergePreparation Plan(ImmutableArray<MediaAssetSnapshot> snapshots)
        {
            token.ThrowIfCancellationRequested();
            // A pre-existing inconsistent union is never repaired by incoming data.
            var projection = NativeTransferMediaInventoryProjection.Create(
                NativeTransferArchiveProjection.From(target, token), snapshots, token);
            var targetMembers = projection.Members.ToDictionary(m => new MediaOccurrence(thread.Id, m.PostId,
                m.Attachment.Metadata.Position, m.Attachment.Metadata.AttachmentRevisionKey));
            var observed = snapshots.SelectMany(s => s.OriginalEvidence).Select(e => e.Occurrence).ToHashSet();
            var targetPosts = target.Posts.ToDictionary(p => p.PostId, StringComparer.Ordinal);
            var targetSlots = target.Posts.SelectMany(p => (p.Attachments ?? Array.Empty<CapturedAttachment>())
                .Select(a => (Slot: (p.PostId, a.Position), Attachment: a))).ToDictionary(x => x.Slot, x => x.Attachment);
            var fullMembers = materialization.FullSelection.Members.ToDictionary(m => m.Id);
            var selected = new HashSet<NativeMediaMemberId>();
            var added = ImmutableArray.CreateBuilder<CapturedPost>();
            var updated = ImmutableArray.CreateBuilder<CapturedPost>();
            var attachments = ImmutableArray.CreateBuilder<NativeMediaMergeAddedAttachment>();
            int conflicts = thread.PageUrl != target.Summary.Url || thread.Title != target.Summary.Title
                || thread.DecimalPostOrder != target.DecimalPostOrder ? 1 : 0;

            foreach (TransferPost incoming in thread.Posts)
            {
                token.ThrowIfCancellationRequested();
                bool exists = targetPosts.TryGetValue(incoming.Metadata.PostId, out CapturedPost? previous);
                var accepted = ImmutableArray.CreateBuilder<CapturedAttachment>();
                bool acceptedOrReused = false;
                foreach (TransferAttachment attachment in incoming.Attachments)
                {
                    string post = incoming.Metadata.PostId;
                    CapturedAttachment raw = attachment.Metadata;
                    // An occupied current slot is immutable. Different revisions go only
                    // into Full, not into active retained history via a conflict back door.
                    if (targetSlots.TryGetValue((post, raw.Position), out CapturedAttachment? old)
                        && old with { PlannedPath = null } != raw)
                    { conflicts++; continue; }
                    if (!SelectOccurrence(post, attachment)) continue;
                    acceptedOrReused = true;
                    if (old != null) continue;
                    if (exists) attachments.Add(new(post, raw)); else accepted.Add(raw);
                }
                if (!exists)
                {
                    added.Add(incoming.Metadata with
                    { Attachments = incoming.AttachmentsWereNull ? null : accepted.ToImmutable() });
                    continue;
                }
                CapturedPost merged = MergeNativeTextPost(previous!, incoming.Metadata with
                    { Fields = incoming.Metadata.Fields & ~PostFieldPresence.Attachments }, out bool changed, out bool conflict);
                if (conflict) conflicts++;
                bool asserted = (incoming.Metadata.Fields & PostFieldPresence.Attachments) != 0;
                if (asserted && incoming.Attachments.IsEmpty && (previous!.Attachments?.Count ?? 0) != 0) conflicts++;
                if (asserted && (acceptedOrReused || incoming.Attachments.IsEmpty)
                    && (previous!.Fields & PostFieldPresence.Attachments) == 0)
                {
                    merged = merged with { Fields = merged.Fields | PostFieldPresence.Attachments,
                        Attachments = incoming.AttachmentsWereNull ? previous.Attachments
                            : previous.Attachments ?? ImmutableArray<CapturedAttachment>.Empty };
                    changed = true;
                }
                if (changed) updated.Add(merged);
            }
            foreach (TransferRetainedBinding retained in thread.RetainedBindings)
            {
                token.ThrowIfCancellationRequested();
                // Retained observations never manufacture a post or occupy a SQL slot.
                SelectOccurrence(retained.PostId, retained.Attachment);
            }

            NativeMediaCandidateSelection safe = materialization.Select(selected);
            Guid operation = Guid.NewGuid();
            DateTimeOffset importedAt = DateTimeOffset.UtcNow;
            NativeMediaCandidateInventory inventory = safe.CreateInventory(operation, package.Envelope.Fingerprint, importedAt);
            // Prove active root additions cannot make ANY previously unkeyed SQL
            // association ambiguous, even if original bytes happen to hash equally.
            CapturedAttachment[] legacy = targetSlots.Values.Where(a => a.MediaId != null && a.AttachmentRevisionKey == null).ToArray();
            long targetAssets = snapshots.Sum(s => (long)s.Count);
            if (checked((long)legacy.Length * (targetAssets + inventory.Bindings.Length)) > 1_000_000)
                return new(null, NativeMediaMergeError.TargetBudgetExceeded);
            var legacyByMedia = legacy.ToLookup(a => (a.MediaId!, a.Variant));
            foreach (MediaAttachmentBinding binding in inventory.Bindings)
            foreach (CapturedAttachment old in legacyByMedia[(binding.Provider.MediaId, binding.Provider.Variant)])
            {
                token.ThrowIfCancellationRequested();
                if (MediaAttachmentResolver.WouldMatchLegacy(old, binding.Provider, binding.Actual))
                    return new(null, NativeMediaMergeError.TargetUnavailable);
            }
            long items = snapshots.Sum(s => (long)s.Count + s.AttachmentBindings.Count
                + s.OriginalEvidence.Length + s.DerivedRepresentations.Length)
                + 2L * inventory.Bindings.Length + inventory.OriginalEvidence.Length + inventory.DerivedRepresentations.Length;
            int addedAttachmentCount = added.Sum(p => p.Attachments?.Count ?? 0) + attachments.Count;
            if (target.PostCount + added.Count > 100_000 || targetSlots.Count + addedAttachmentCount > 100_000
                || items > 100_000 || snapshots.Length + (safe.IsEmpty ? 0 : 1) > 32
                || !InventoryMergePhysicalBudget(snapshots, inventory, token))
                return new(null, NativeMediaMergeError.TargetBudgetExceeded);

            var prospectivePosts = target.Posts.ToDictionary(p => p.PostId, StringComparer.Ordinal);
            foreach (CapturedPost p in added) prospectivePosts.Add(p.PostId, p);
            foreach (CapturedPost p in updated) prospectivePosts[p.PostId] = p;
            foreach (NativeMediaMergeAddedAttachment a in attachments)
                prospectivePosts[a.PostId] = prospectivePosts[a.PostId] with
                { Attachments = (prospectivePosts[a.PostId].Attachments ?? Array.Empty<CapturedAttachment>()).Append(a.Attachment).ToImmutableArray() };
            bool contentChanges = added.Count != 0 || updated.Count != 0 || attachments.Count != 0 || !safe.IsEmpty;
            var prospective = new ArchivedThreadView(contentChanges
                ? target.Summary with { Revision = checked(target.Summary.Revision + 1) } : target.Summary,
                prospectivePosts.Values, target.MediaDirectories, target.DecimalPostOrder);
            NativeTransferPackageWriter.TransferPackageBudget budget;
            try { budget = NativeMediaMergeInventoryExportBudget.Measure(materialization, prospective, snapshots, safe,
                operation, importedAt, token); }
            catch (InvalidDataException) { return new(null, NativeMediaMergeError.TargetBudgetExceeded); }
            if (!budget.WithinContainerCap) return new(null, NativeMediaMergeError.TargetBudgetExceeded);
            var roots = snapshots.Select(s => new NativeMediaMergeRoot(s.Directory, s.Generation!)).ToImmutableArray();
            var counts = new NativeMediaMergeCounts(added.Count, updated.Count, addedAttachmentCount,
                inventory.Bindings.Length, conflicts,
                safe.Members.Count(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence && !m.ContextOnly),
                inventory.DerivedRepresentations.Length,
                inventory.DerivedRepresentations.Count(d => d.IncomingStatus == MediaDerivedStatus.Contained));
            string parent = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "merge-media-operations", operation.ToString("N"));
            string request = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "native-media-merge-v2\0" + operation.ToString("D") + "\0" + package.Envelope.Fingerprint + "\0"
                + read.instance + "\0" + read.generation.ToString(CultureInfo.InvariantCulture) + "\0"
                + JsonSerializer.Serialize(roots) + "\0" + materialization.FullSelection.Sha256 + "\0" + safe.Sha256)))
                .ToLowerInvariant();
            NativeMediaMergePlan plan = new(this, materialization, read.instance, read.generation, target, roots,
                added.ToImmutable(), updated.ToImmutable(), attachments.ToImmutable(), safe, counts,
                operation, request, parent, budget, importedAt);
            _issuedNativeMediaMergePlans.Add(plan, new object());
            return new(plan, NativeMediaMergeError.None);

            bool SelectOccurrence(string postId, TransferAttachment incoming)
            {
                MediaOccurrence occurrence = new(thread.Id, postId, incoming.Metadata.Position, incoming.Metadata.AttachmentRevisionKey);
                NativeMediaMemberId id = new(occurrence, incoming.OriginalByteVersion, NativeMediaMemberRole.OriginalEvidence);
                if (!fullMembers.ContainsKey(id)) throw new InvalidDataException("An incoming occurrence has no frozen descriptor.");
                targetMembers.TryGetValue(occurrence, out var old);
                if (old != null && (old.Attachment.Metadata with { IsDeleted = incoming.Metadata.IsDeleted } != incoming.Metadata
                    || observed.Contains(occurrence) && old.Attachment.OriginalByteVersion != incoming.OriginalByteVersion))
                { conflicts++; return false; }
                // A newly supplied source tuple must agree with retained real originals,
                // but measured SHA1 never enriches the nullable exact sender tuple.
                if (old != null && incoming.OriginalByteVersion is { } version
                    && old.Originals.Any(o => !MediaIntegrityValidation.CompatibleEvidence(
                        new(version.Length, version.Md5, version.Sha256) { Sha1 = version.Sha1 },
                        new(o.Original!.Length, o.Original.Md5, o.Original.Sha256) { Sha1 = o.Original.Sha1 })))
                { conflicts++; return false; }
                if (incoming.Status == TransferContentStatus.Contained && old != null && !old.Originals.IsEmpty)
                {
                    TransferMeasuredBlob measured = fullMembers[id].Descriptor.MeasuredPayload!;
                    if (old.Originals.Any(o => !MediaIntegrityValidation.MatchesMeasured(
                        new(o.Original!.Length, o.Original.Md5, o.Original.Sha256) { Sha1 = o.Original.Sha1 },
                        new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 })))
                    { conflicts++; return false; }
                }
                if (!observed.Contains(occurrence)) selected.Add(id);
                if (incoming.Status == TransferContentStatus.Contained && (old == null || old.Originals.IsEmpty))
                    selected.Add(id with { Role = NativeMediaMemberRole.OriginalPayload });
                foreach (TransferDerivedRepresentation derived in incoming.DerivedRepresentations)
                {
                    NativeMediaMemberRole role = derived.Kind == TransferDerivedRepresentationKind.Preview
                        ? NativeMediaMemberRole.Preview : NativeMediaMemberRole.VideoPosterPng;
                    NativeMediaMemberId child = id with { Role = role };
                    var previous = old?.Attachment.DerivedRepresentations.FirstOrDefault(d => d.Kind == derived.Kind);
                    if (previous == null) { selected.Add(child); continue; }
                    ImmutableArray<NativeTransferInventoryOrigin> origins = old!.Derived[derived.Kind];
                    if (derived.Status != TransferContentStatus.Contained) continue; // never downgrade existing bytes/descriptors
                    if (origins.IsEmpty) { selected.Add(child); continue; }
                    TransferMeasuredBlob measured = fullMembers[child].Descriptor.MeasuredPayload!;
                    if (origins.Any(o => !MediaIntegrityValidation.MatchesMeasured(o.Derived!.Blob!.Actual,
                        new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 }))) conflicts++;
                }
                return true;
            }
        }
    }

    private static bool InventoryMergePhysicalBudget(ImmutableArray<MediaAssetSnapshot> snapshots,
        NativeMediaCandidateInventory candidate, CancellationToken token)
    {
        var paths = new Dictionary<string, long>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long total = 0;
        bool Add(string path, long length)
        {
            if (paths.TryGetValue(path, out long old)) return old == length;
            if (length < 0 || length > 1024L * 1024 * 1024 || length > 2L * 1024 * 1024 * 1024 - total) return false;
            paths.Add(path, length); total += length; return true;
        }
        foreach (MediaAssetSnapshot snapshot in snapshots)
        {
            for (int offset = 0; offset < snapshot.Count; offset += 500)
            foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
            { token.ThrowIfCancellationRequested(); if (!Add(asset.Path, asset.Length)) return false; }
            foreach (MediaDerivedRepresentation d in snapshot.DerivedRepresentations)
            { token.ThrowIfCancellationRequested(); if (d.Blob is { } b && !Add(Path.Combine(snapshot.Directory, b.RelativeFileName), b.Actual.Length)) return false; }
        }
        // Within a candidate dedup is by real filename (role + SHA), NOT just SHA:
        // original and derived copies can legitimately have equal hashes.
        foreach (NativeMediaCandidatePayload payload in candidate.Payloads)
        {
            token.ThrowIfCancellationRequested();
            if (payload.Measured.Length > 2L * 1024 * 1024 * 1024 - total) return false;
            total += payload.Measured.Length;
        }
        return true;
    }
    private Task<(string instance, long generation, ArchivedThreadView? target, NativeMediaMergeError error)>
        ReadMediaMergeTargetAsync(ResourceId resource, CancellationToken cancellationToken)
        => ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
            long work = 0, bytes = 0;
            foreach ((string table, string[] columns, int cap) in new (string, string[], int)[] {
                ("archive_threads", ["source", "board", "thread", "url", "title", "media_directory", "first_seen_at", "last_captured_at"], 1),
                ("archive_posts", ["post", "subject", "author", "html", "posted_at"], 100_000),
                ("archive_attachments", ["post", "media_id", "variant", "url", "original_name", "planned_path", "expected_md5", "expected_sha256", "attachment_revision_key", "expected_sha1", "provider_size_hint_json"], 100_000),
                ("archive_media_directories", ["directory"], 32) })
            {
                using SqliteCommand budget = ArchiveSql.Command(connection, transaction,
                    "SELECT count(*),COALESCE(sum(" + string.Join("+", columns.Select(column => "COALESCE(length(CAST(" + column + " AS BLOB)),0)"))
                    + $"),0) FROM {table} WHERE source=$source AND board=$board AND thread=$thread");
                ArchiveSql.AddId(budget, resource);
                using SqliteDataReader counts = budget.ExecuteReader(); counts.Read();
                long rows = counts.GetInt64(0);
                work = checked(work + rows); bytes = checked(bytes + counts.GetInt64(1));
                if (rows > cap || work > 201_000 || bytes > 32L * 1024 * 1024)
                    return (instance, generation, (ArchivedThreadView?)null, NativeMediaMergeError.TargetBudgetExceeded);
            }
            ArchivedThreadView? target = ArchiveSql.ReadThread(connection, transaction, resource);
            transaction.Commit();
            return (instance, generation, target, target == null ? NativeMediaMergeError.MissingTarget : NativeMediaMergeError.None);
        }, cancellationToken);
}
