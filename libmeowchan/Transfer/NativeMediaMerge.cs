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
using MeowChan.Archive;
using MeowChan.Transfer;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NativeMediaMergePlan, object> _issuedNativeMediaMergePlans = new();

    /// <summary>Prepares a bounded current/retained-original merge for an existing archived resource.</summary>
    internal async Task<NativeMediaMergePreparation> PrepareNativeMediaMergeAsync(NativeTransferImportStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        cancellationToken.ThrowIfCancellationRequested();
        NativeTransferPackage package = stage.Package;
        // These original-only materializers cannot preserve nested representations or
        // original byte-version evidence. Reject the admitted wire schema before any
        // target lookup, even when hashes alias or the DTO could downlevel.
        if (package.IsChanV4) return new(null, NativeMediaMergeError.UnsupportedPackage);
        if (package.Catalog.Threads.Length != 1) return new(null, NativeMediaMergeError.UnsupportedPackage);
        TransferThread thread = package.Catalog.Threads[0];
        if (thread.PageUrl == null || thread.Posts.Any(post => post.AttachmentsWereNull))
            return new(null, NativeMediaMergeError.UnsupportedPackage);
        var inputs = ImmutableArray.CreateBuilder<NativeMediaImportAttachment>();
        HashSet<string> keys = new(StringComparer.Ordinal), payloads = new(StringComparer.Ordinal);
        foreach ((string postId, TransferAttachment attachment) in thread.Posts
            .SelectMany(post => post.Attachments.Select(attachment => (post.Metadata.PostId, attachment)))
            .Concat(thread.RetainedBindings.Select(binding => (binding.PostId, binding.Attachment))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedAttachment raw = attachment.Metadata;
            if (attachment.Role != TransferMediaRole.Original || attachment.Status != TransferContentStatus.Contained
                || raw.Variant != "original" || raw.MediaId == null || raw.Url == null || raw.PlannedPath != null
                || attachment.PayloadSha256 == null || !package.Envelope.MeasuredPayloads.ContainsKey(attachment.PayloadSha256))
                return new(null, NativeMediaMergeError.UnsupportedPackage);
            MediaProviderMetadata provider = MergeProvider(raw);
            string key = MediaRevisionKeys.CreateAttachmentKey(thread.Id, postId, raw.Position, provider);
            if (!keys.Add(key)) return new(null, NativeMediaMergeError.UnsupportedPackage);
            payloads.Add(attachment.PayloadSha256);
            inputs.Add(new(postId, key, attachment, provider));
        }
        if (inputs.Count == 0 || payloads.Count != package.Envelope.Payloads.Length
            || package.Envelope.Payloads.Any(payload => !payloads.Contains(payload.Sha256)))
            return new(null, NativeMediaMergeError.UnsupportedPackage);

        var read = await ReadAsync(connection =>
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
                ArchiveSql.AddId(budget, thread.Id);
                using SqliteDataReader counts = budget.ExecuteReader(); counts.Read();
                long rows = counts.GetInt64(0);
                work = checked(work + rows); bytes = checked(bytes + counts.GetInt64(1));
                if (rows > cap || work > 201_000 || bytes > 32L * 1024 * 1024)
                    return (instance, generation, (ArchivedThreadView?)null, NativeMediaMergeError.TargetBudgetExceeded);
            }
            ArchivedThreadView? target = ArchiveSql.ReadThread(connection, transaction, thread.Id);
            transaction.Commit();
            return (instance, generation, target, target == null ? NativeMediaMergeError.MissingTarget : NativeMediaMergeError.None);
        }, cancellationToken).ConfigureAwait(false);
        if (read.Item4 != NativeMediaMergeError.None) return new(null, read.Item4);
        ArchivedThreadView target = read.Item3!;
        var frozenInputs = inputs.ToImmutable();
        try
        {
            return await new MediaStore().WithPassiveMergeTargetRootsAsync(target.MediaDirectories, snapshots =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetSlots = target.Posts.SelectMany(post => (post.Attachments ?? Array.Empty<CapturedAttachment>())
                    .Select(attachment => ((post.PostId, attachment.Position), attachment)))
                    .ToDictionary(pair => pair.Item1, pair => pair.Item2);
                var allBindings = new Dictionary<string, MediaAttachmentBinding>(StringComparer.Ordinal);
                foreach (MediaAssetSnapshot snapshot in snapshots)
                foreach (var binding in snapshot.AttachmentBindings)
                {
                    if (allBindings.TryGetValue(binding.Key, out var previousBinding) && previousBinding != binding.Value)
                        return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                    allBindings[binding.Key] = binding.Value;
                }
                // Every existing association must already be coherent. In particular, a legacy
                // null revision is not permission to adopt new bytes or silently repair a slot.
                // Bound the legacy resolver's asset scans before doing any of them.
                long targetAssets = snapshots.Sum(snapshot => (long)snapshot.Count);
                CapturedAttachment[] legacy = targetSlots.Values
                    .Where(attachment => attachment.MediaId != null && attachment.AttachmentRevisionKey == null).ToArray();
                if (checked((long)legacy.Length * targetAssets) > 1_000_000)
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                foreach (var slot in targetSlots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (slot.Value.MediaId == null) continue;
                    if (MediaAttachmentResolver.Resolve(snapshots, thread.Id, slot.Key.PostId, slot.Value) == null)
                        return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                }
                // Retained observations can have the SAME post/position as current content,
                // with different revisions. Index the union by occurrence identity, never slot.
                var byKey = frozenInputs.ToDictionary(input => input.RevisionKey, StringComparer.Ordinal);
                var addedPosts = ImmutableArray.CreateBuilder<CapturedPost>();
                var updatedPosts = ImmutableArray.CreateBuilder<CapturedPost>();
                var addedAttachments = ImmutableArray.CreateBuilder<NativeMediaMergeAddedAttachment>();
                var safe = new HashSet<string>(StringComparer.Ordinal);
                int conflicts = thread.PageUrl != target.Summary.Url || thread.Title != target.Summary.Title
                    || thread.DecimalPostOrder != target.DecimalPostOrder ? 1 : 0;
                var targetPosts = target.Posts.ToDictionary(post => post.PostId, StringComparer.Ordinal);
                foreach (TransferPost incoming in thread.Posts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool exists = targetPosts.TryGetValue(incoming.Metadata.PostId, out CapturedPost? previous);
                    var newAttachments = ImmutableArray.CreateBuilder<CapturedAttachment>();
                    bool acceptedOrReusedAttachment = false;
                    foreach (TransferAttachment attachment in incoming.Attachments)
                    {
                        NativeMediaImportAttachment input = byKey[MediaRevisionKeys.CreateAttachmentKey(thread.Id,
                            incoming.Metadata.PostId, attachment.Metadata.Position, MergeProvider(attachment.Metadata))];
                        CapturedAttachment normalized = attachment.Metadata with { PlannedPath = null, AttachmentRevisionKey = input.RevisionKey };
                        if (targetSlots.TryGetValue((input.PostId, attachment.Metadata.Position), out CapturedAttachment? old))
                        {
                            if (old with { PlannedPath = null } != normalized) { conflicts++; continue; }
                            ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(snapshots, thread.Id, input.PostId, old);
                            TransferMeasuredBlob measured = package.Envelope.MeasuredPayloads[input.Incoming.PayloadSha256!];
                            if (resolved?.Binding == null)
                                return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                            if (!MediaIntegrityValidation.MatchesMeasured(resolved.Binding.Actual,
                                new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 })) conflicts++;
                            else acceptedOrReusedAttachment = true;
                            continue; // Reuse old exact binding, never duplicate its provenance.
                        }
                        // A vacant SQL slot may refer to an EXACT existing historical binding.
                        // Keep that binding's asset/path/provenance instead of publishing a copy.
                        if (allBindings.ContainsKey(input.RevisionKey))
                        {
                            ResolvedMediaAttachment? retained = MediaAttachmentResolver.Resolve(snapshots, thread.Id, input.PostId, normalized);
                            if (retained?.Binding == null)
                                return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                            if (!MergeInputMatchesBytes(package, input, retained.Binding)) { conflicts++; continue; }
                        }
                        else safe.Add(input.RevisionKey);
                        acceptedOrReusedAttachment = true;
                        if (exists) addedAttachments.Add(new(input.PostId, normalized));
                        else newAttachments.Add(normalized);
                    }
                    if (!exists)
                    {
                        addedPosts.Add(incoming.Metadata with { Attachments = newAttachments.ToImmutable() });
                        continue;
                    }
                    CapturedPost merged = MergeNativeTextPost(previous!, incoming.Metadata with
                        { Fields = incoming.Metadata.Fields & ~PostFieldPresence.Attachments }, out bool changed, out bool conflict);
                    if (conflict) conflicts++;
                    // Rejected conflict inputs remain in the inactive full candidate; they
                    // cannot promote the active post's attachment-presence assertion.
                    if ((acceptedOrReusedAttachment || incoming.Attachments.IsEmpty)
                        && (incoming.Metadata.Fields & PostFieldPresence.Attachments) != 0
                        && (previous!.Fields & PostFieldPresence.Attachments) == 0)
                    { merged = merged with { Fields = merged.Fields | PostFieldPresence.Attachments }; changed = true; }
                    if (incoming.Attachments.IsEmpty && (incoming.Metadata.Fields & PostFieldPresence.Attachments) != 0
                        && (previous!.Attachments?.Count ?? 0) != 0) conflicts++;
                    if (changed) updatedPosts.Add(merged);
                }
                // Historical observations remain manifest-only even when their post is absent
                // or the current SQL slot contains a different revision. Only a repeated exact
                // key needs comparison with target bytes; a different revision is additive history.
                foreach (TransferRetainedBinding incoming in thread.RetainedBindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativeMediaImportAttachment input = byKey[incoming.Attachment.Metadata.AttachmentRevisionKey!];
                    if (!allBindings.ContainsKey(input.RevisionKey)) { safe.Add(input.RevisionKey); continue; }
                    CapturedAttachment normalized = incoming.Attachment.Metadata with
                        { PlannedPath = null, AttachmentRevisionKey = input.RevisionKey };
                    ResolvedMediaAttachment? existing = MediaAttachmentResolver.Resolve(snapshots, thread.Id, input.PostId, normalized);
                    if (existing?.Binding == null)
                        return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                    if (!MergeInputMatchesBytes(package, input, existing.Binding)) conflicts++;
                }
                // Prove that the safe root cannot add a match for ANY old legacy association.
                // Use the same predicate as normal resolution, including URL-only, size and all
                // hashes. Even identical bytes in a second occurrence can introduce ambiguity;
                // never re-key the old row or replace its asset/provenance to make this pass.
                if (checked((long)legacy.Length * (targetAssets + safe.Count)) > 1_000_000)
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                var legacyByMedia = legacy.ToLookup(attachment => (attachment.MediaId!, attachment.Variant));
                foreach (string key in safe)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativeMediaImportAttachment input = byKey[key];
                    TransferMeasuredBlob measured = package.Envelope.MeasuredPayloads[input.Incoming.PayloadSha256!];
                    MediaActualIntegrity actual = new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 };
                    foreach (CapturedAttachment previous in legacyByMedia[(input.Provider.MediaId, input.Provider.Variant)])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (MediaAttachmentResolver.WouldMatchLegacy(previous, input.Provider, actual))
                            return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetUnavailable));
                    }
                }
                // A safe candidate is a new physical root. Export SHA deduplication does not
                // deduplicate bytes across roots, so reserve the target's physical original
                // bytes plus the candidate's within-root SHA-deduplicated payloads separately.
                long physicalBytes = 0;
                foreach (MediaAssetSnapshot snapshot in snapshots)
                {
                    HashSet<string> paths = new(OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    for (int offset = 0; offset < snapshot.Count; offset += 500)
                    foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (asset.Variant == "original" && paths.Add(asset.Path))
                            physicalBytes = checked(physicalBytes + asset.Length);
                    }
                }
                if (!NativeMediaMergePhysicalBudget.WithinLimit(physicalBytes, safe.Select(key =>
                {
                    TransferMeasuredBlob measured = package.Envelope.MeasuredPayloads[byKey[key].Incoming.PayloadSha256!];
                    return new TransferBlobDescriptor(measured.Length, measured.Sha256);
                }), cancellationToken))
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                long resultingAssets = snapshots.Sum(snapshot => (long)snapshot.Count) + safe.Count;
                long resultingBindings = snapshots.Sum(snapshot => (long)snapshot.AttachmentBindings.Count) + safe.Count;
                int newAttachmentCount = addedPosts.Sum(post => post.Attachments?.Count ?? 0) + addedAttachments.Count;
                if (target.PostCount + addedPosts.Count > 100_000 || targetSlots.Count + newAttachmentCount > 100_000
                    || resultingAssets + resultingBindings > 100_000 || snapshots.Length + (safe.Count == 0 ? 0 : 1) > 32)
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                // Budget the complete prospective native re-export, not just current payload
                // bytes: retained target bindings, current history, catalog, manifest, and the
                // stored-ZIP directory/header overhead all count against the writer cap.
                var projected = target.Posts.ToDictionary(post => post.PostId, StringComparer.Ordinal);
                foreach (CapturedPost update in updatedPosts) projected[update.PostId] = update;
                foreach (CapturedPost added in addedPosts) projected.Add(added.PostId, added);
                foreach (var attachment in addedAttachments)
                    projected[attachment.PostId] = projected[attachment.PostId] with
                    { Attachments = (projected[attachment.PostId].Attachments ?? Array.Empty<CapturedAttachment>()).Append(attachment.Attachment).ToArray() };
                Guid operation = Guid.NewGuid();
                bool contentChanges = addedPosts.Count != 0 || updatedPosts.Count != 0 || addedAttachments.Count != 0 || safe.Count != 0;
                var prospective = new ArchivedThreadView(contentChanges
                    ? target.Summary with { Revision = checked(target.Summary.Revision + 1) } : target.Summary,
                    projected.Values, target.MediaDirectories, target.DecimalPostOrder);
                NativeTransferPackageWriter.TransferPackageBudget exportBudget;
                try
                {
                    exportBudget = NativeMediaMergeExportBudget.Measure(package, frozenInputs, prospective,
                        snapshots, safe, operation, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                }
                if (!exportBudget.WithinContainerCap)
                    return Task.FromResult(new NativeMediaMergePreparation(null, NativeMediaMergeError.TargetBudgetExceeded));
                var roots = snapshots.Select(snapshot => new NativeMediaMergeRoot(snapshot.Directory, snapshot.Generation!)).ToImmutableArray();
                var safeKeys = safe.OrderBy(key => key, StringComparer.Ordinal).ToImmutableArray();
                string parent = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "merge-media-operations", operation.ToString("N"));
                string request = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "native-media-merge-v1\0" + operation.ToString("D") + "\0" + package.Envelope.Fingerprint + "\0"
                    + read.instance + "\0" + read.generation.ToString(CultureInfo.InvariantCulture) + "\0" + JsonSerializer.Serialize(roots)
                    + "\0" + string.Join("\0", safeKeys)))).ToLowerInvariant();
                NativeMediaMergePlan plan = new(this, stage, read.instance, read.generation, target, roots, frozenInputs,
                    addedPosts.ToImmutable(), updatedPosts.ToImmutable(), addedAttachments.ToImmutable(), safeKeys, conflicts, operation, request, parent, exportBudget);
                _issuedNativeMediaMergePlans.Add(plan, new object());
                return Task.FromResult(new NativeMediaMergePreparation(plan, NativeMediaMergeError.None));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return new(null, NativeMediaMergeError.TargetUnavailable); }
    }

    private static bool MergeInputMatchesBytes(NativeTransferPackage package, NativeMediaImportAttachment input,
        MediaAttachmentBinding binding)
    {
        TransferMeasuredBlob measured = package.Envelope.MeasuredPayloads[input.Incoming.PayloadSha256!];
        return MediaIntegrityValidation.MatchesMeasured(binding.Actual,
            new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 });
    }

    private static MediaProviderMetadata MergeProvider(CapturedAttachment attachment)
        => new(attachment.MediaId!, attachment.Variant, attachment.Url!, attachment.OriginalFileName,
            attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256)
        { ExpectedSha1 = attachment.ExpectedSha1, ProviderSizeHint = attachment.ProviderSizeHint };

    // The caller retains this borrowed Exclusive through all SQL, filesystem scopes and
    // independent reconciliation. No tokenless profile mutation entry is available.
    internal Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> CommitNativeMediaMergeAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaMergePlan plan, MediaStore store,
        CancellationToken cancellationToken = default, Action? beforeAdmission = null,
        Action<NativeMediaImportPhase>? checkpoint = null)
    {
        EnsureTransferAdmission(admission);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(store);
        if (!ReferenceEquals(plan.Owner, this) || !_issuedNativeMediaMergePlans.TryGetValue(plan, out _))
            return RejectUnissuedNativeMediaMergePlanAsync();
        return CommitNativeMediaMergeAdmittedCoreAsync(admission, plan, store, cancellationToken, beforeAdmission, checkpoint);
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> RejectUnissuedNativeMediaMergePlanAsync()
    {
        // Do not use unissued operation identities or paths as recovery evidence.
        try
        {
            bool blocked = await ReadMediaMergeRecoveryAsync(connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                bool result = HasNativeMediaImportRecoveryBlocker(connection, transaction);
                transaction.Commit();
                return result;
            }, CancellationToken.None).ConfigureAwait(false);
            NativeMediaMergeResult stale = new(NativeMediaImportCommitStatus.StalePlan);
            return blocked ? ProfileExclusiveOutcome<NativeMediaMergeResult>.RecoveryRequired(stale)
                : ProfileExclusiveOutcome<NativeMediaMergeResult>.SafeToResume(stale);
        }
        catch (Exception error)
        {
            return ProfileExclusiveOutcome<NativeMediaMergeResult>.RecoveryRequired(
                new(NativeMediaImportCommitStatus.Unknown, Diagnostic: error.GetType().Name));
        }
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> CommitNativeMediaMergeAdmittedCoreAsync(
        ProfileAdmissionToken admission, NativeMediaMergePlan plan, MediaStore store,
        CancellationToken cancellationToken, Action? beforeAdmission, Action<NativeMediaImportPhase>? checkpoint)
    {
        bool claimAttempted = false, durableOperationObserved = false;
        NativeMediaMergeResult? completed = null;
        Exception? failure = null;
        try { completed = await ExecuteAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // All awaited target/candidate callbacks and handle disposal have unwound here.
        return await ReconcileNativeMediaMergeOutcomeAsync(plan, completed, failure,
            claimAttempted || durableOperationObserved).ConfigureAwait(false);

        async Task<NativeMediaMergeResult> ExecuteAsync()
        {
            NativeMediaMergeResult? replay = await ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256, CancellationToken.None).ConfigureAwait(false);
            if (replay != null) return replay;
            // A callback may win a SQL race or mutate a cooperative media root. Invoke it
            // before acquiring the stable root leases; the snapshot below then either binds the
            // current generations or rejects the plan before any durable operation claim.
            beforeAdmission?.Invoke();
            // Resolve replay/unresolved/stale SQL state before touching target paths, then
            // repeat the same transaction check under the verified root leases before Claim.
            NativeMediaImportCommitStatus preliminary = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                Admit(connection, transaction, claim: false), cancellationToken).ConfigureAwait(false);
            if (preliminary == NativeMediaImportCommitStatus.Committed)
                return await ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256, CancellationToken.None).ConfigureAwait(false)
                    ?? new(NativeMediaImportCommitStatus.Unknown);
            if (preliminary != NativeMediaImportCommitStatus.Inactive) return new(preliminary);
            return await store.WithPassiveMergeTargetRootsAdmittedAsync(admission, plan, async snapshots =>
            {
                if (snapshots.Length != plan.Roots.Length
                    || snapshots.Select(snapshot => (snapshot.Directory, snapshot.Generation))
                        .OrderBy(pair => pair.Directory, StringComparer.Ordinal)
                        .SequenceEqual(plan.Roots.Select(root => (root.Directory, (string?)root.Generation))
                            .OrderBy(pair => pair.Directory, StringComparer.Ordinal)) == false)
                    return await ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256, CancellationToken.None).ConfigureAwait(false)
                        ?? new NativeMediaMergeResult(NativeMediaImportCommitStatus.StalePlan);

                // Receipt/recovery and freshness remain first inside the SQL admission. The
                // target leases are held from this check through final SQL, so a cooperative
                // manifest change cannot turn into a claimed-but-never-startable operation.
                NativeMediaImportCommitStatus claimStatus = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                    Admit(connection, transaction, claim: true), cancellationToken).ConfigureAwait(false);
                if (claimStatus == NativeMediaImportCommitStatus.Committed)
                    return await ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256, CancellationToken.None).ConfigureAwait(false)
                        ?? new(NativeMediaImportCommitStatus.Unknown);
                if (claimStatus != NativeMediaImportCommitStatus.Inactive) return new(claimStatus);

                checkpoint?.Invoke(NativeMediaImportPhase.PreparingSaved);
                cancellationToken.ThrowIfCancellationRequested();
                CreateMergeOperationsParent(plan.OperationsParent);
                checkpoint?.Invoke(NativeMediaImportPhase.OperationsParentCreated);
                PassiveMediaImportCandidate full = await store.MaterializePassiveMergeCandidateAdmittedAsync(admission, plan, false, cancellationToken,
                    _ => checkpoint?.Invoke(NativeMediaImportPhase.CandidateDirectoryCreated)).ConfigureAwait(false);
                checkpoint?.Invoke(NativeMediaImportPhase.CandidateReady);
                PassiveMediaImportCandidate? safe = null;
                if (!plan.SafeKeys.IsEmpty)
                    safe = plan.ReuseFullAsSafe ? full
                        : await store.MaterializePassiveMergeCandidateAdmittedAsync(admission, plan, true, cancellationToken).ConfigureAwait(false);
                await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                { ArchiveSql.MarkMediaMergePrepared(connection, transaction, plan, full, safe); return true; }, cancellationToken)
                    .ConfigureAwait(false);
                checkpoint?.Invoke(NativeMediaImportPhase.PreparedSaved);
                return await store.WithVerifiedPassiveMergeCandidatesAdmittedAsync(admission, plan, full, safe, async () =>
                {
                    checkpoint?.Invoke(NativeMediaImportPhase.CandidateVerified);
                    NativeMediaMergeResult result = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                    {
                        NativeMediaMergeResult? receipt = ArchiveSql.ReadMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256);
                        if (receipt != null) return receipt;
                        if (!ArchiveSql.MediaMergeOperationMatches(connection, transaction, plan, full, safe)
                            || !MediaMergeFresh(connection, transaction, plan)) return new(NativeMediaImportCommitStatus.StalePlan);
                        ArchiveSql.ApplyMediaMergeContent(connection, transaction, plan, full, safe);
                        ArchiveSql.FinishMediaMerge(connection, transaction, plan);
                        checkpoint?.Invoke(NativeMediaImportPhase.BeforeSqlCommit);
                        return new(NativeMediaImportCommitStatus.Committed, plan.AddedPosts.Length, plan.UpdatedPosts.Length,
                            plan.AddedAttachments.Length + plan.AddedPosts.Sum(p => p.Attachments?.Count ?? 0), plan.SafeKeys.Length, plan.Conflicts);
                    }, cancellationToken).ConfigureAwait(false);
                    if (result.Status == NativeMediaImportCommitStatus.Committed) checkpoint?.Invoke(NativeMediaImportPhase.SqlCommitted);
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        NativeMediaImportCommitStatus Admit(SqliteConnection connection, SqliteTransaction transaction, bool claim)
        {
            if (ArchiveSql.ReadMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256) != null)
                return NativeMediaImportCommitStatus.Committed;
            NativeMediaMergeOperation? existing = ArchiveSql.ReadMediaMergeOperation(connection, transaction, plan.OperationId, plan.RequestSha256);
            if (existing != null)
            {
                durableOperationObserved = true;
                return existing.State == NativeMediaImportOperationState.Committed
                    ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired;
            }
            if (ArchiveSql.HasUnresolvedMediaOperation(connection, transaction, plan.Resource))
                return NativeMediaImportCommitStatus.ReconciliationRequired;
            if (!MediaMergeFresh(connection, transaction, plan)) return NativeMediaImportCommitStatus.StalePlan;
            if (!claim) return NativeMediaImportCommitStatus.Inactive;
            // Record uncertainty BEFORE INSERT/Commit/return, including commit-return loss.
            claimAttempted = true;
            return ArchiveSql.ClaimMediaMerge(connection, transaction, plan)
                ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.ReconciliationRequired;
        }
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> ReconcileNativeMediaMergeOutcomeAsync(
        NativeMediaMergePlan plan, NativeMediaMergeResult? completed, Exception? failure, bool claimMayHavePersisted)
    {
        NativeMediaMergeResult? historicalReceipt = null;
        try
        {
            return await ReadMediaMergeRecoveryAsync(connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                historicalReceipt = ArchiveSql.ReadMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256);
                // Same snapshot, both families, whole profile: a historical success does not
                // authorize resuming over a separate unfinished or receipt-inconsistent operation.
                bool blocked = HasNativeMediaImportRecoveryBlocker(connection, transaction);
                var fence = ArchiveSql.ReadImportFence(connection, transaction);
                if (historicalReceipt != null)
                {
                    NativeMediaMergeResult success = historicalReceipt with
                    {
                        IsReplay = completed?.Status == NativeMediaImportCommitStatus.Committed
                            ? completed.IsReplay : true,
                        Diagnostic = failure?.GetType().Name ?? completed?.Diagnostic
                    };
                    transaction.Commit();
                    // Generation may legitimately advance after a historical receipt; an
                    // entirely different catalog instance cannot attest this owner's resume.
                    return blocked || fence.Item1 != plan.Instance
                        ? ProfileExclusiveOutcome<NativeMediaMergeResult>.RecoveryRequired(success)
                        : ProfileExclusiveOutcome<NativeMediaMergeResult>.SafeToResume(success);
                }
                NativeMediaMergeOperation? operation = ArchiveSql.ReadMediaMergeOperation(connection, transaction,
                    plan.OperationId, plan.RequestSha256);
                NativeMediaImportCommitStatus status;
                bool safeToResume = false;
                if (operation?.State == NativeMediaImportOperationState.Committed)
                    status = NativeMediaImportCommitStatus.Unknown;
                else if (fence.Item1 != plan.Instance)
                    // A completed freshness rejection is still the truthful attempt result.
                    // Replaced identity defeats resume evidence, not that observed StalePlan;
                    // faults without a completed rejection remain Unknown. Never mark safe here.
                    status = completed?.Status == NativeMediaImportCommitStatus.StalePlan
                        ? NativeMediaImportCommitStatus.StalePlan : NativeMediaImportCommitStatus.Unknown;
                else if (operation != null || blocked)
                    status = completed?.Status == NativeMediaImportCommitStatus.StalePlan
                        ? NativeMediaImportCommitStatus.StalePlan : NativeMediaImportCommitStatus.ReconciliationRequired;
                else if (claimMayHavePersisted || completed?.Status is NativeMediaImportCommitStatus.Committed
                    or NativeMediaImportCommitStatus.Unknown or NativeMediaImportCommitStatus.ReconciliationRequired)
                    // Even absent metadata cannot prove rollback after a possibly committed claim.
                    // The filesystem may already contain children whose SQL row disappeared.
                    status = NativeMediaImportCommitStatus.Unknown;
                else if (completed?.Status == NativeMediaImportCommitStatus.StalePlan)
                {
                    status = NativeMediaImportCommitStatus.StalePlan; // explicit pre-effect rejection
                    safeToResume = true;
                }
                else
                {
                    safeToResume = MediaMergeFresh(connection, transaction, plan);
                    status = safeToResume ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.Unknown;
                }
                transaction.Commit();
                NativeMediaMergeResult result = new(status, Diagnostic: failure?.GetType().Name ?? completed?.Diagnostic);
                return safeToResume
                    ? ProfileExclusiveOutcome<NativeMediaMergeResult>.SafeToResume(result)
                    : ProfileExclusiveOutcome<NativeMediaMergeResult>.RecoveryRequired(result);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception reconciliationError)
        {
            // A receipt already read is historical success even if the blocker/read tail fails;
            // without a complete negative blocker proof the lane must nevertheless remain sealed.
            string diagnostic = (failure?.GetType().Name ?? completed?.Diagnostic ?? "reconciliation")
                + "/receipt:" + reconciliationError.GetType().Name;
            return ProfileExclusiveOutcome<NativeMediaMergeResult>.RecoveryRequired(
                historicalReceipt is null
                    ? new(NativeMediaImportCommitStatus.Unknown, Diagnostic: diagnostic)
                    : historicalReceipt with { Diagnostic = diagnostic });
        }
    }

    private static bool MediaMergeFresh(SqliteConnection connection, SqliteTransaction transaction, NativeMediaMergePlan plan)
    {
        // The fence is deliberately the first read. A stale plan must not load an unbounded
        // post/attachment graph while it is already known to be ineligible.
        (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
        if (instance != plan.Instance || generation != plan.Generation) return false;
        return ArchiveSql.IsMediaMergeTargetFresh(connection, transaction, plan.Resource,
            plan.Target.Summary.Revision, plan.Target.Summary.MediaDirectory);
    }

    private static void CreateMergeOperationsParent(string parent)
    {
        ArchiveExportMetadataFile.RejectLinks(parent);
        if (Directory.Exists(parent) || File.Exists(parent)) throw new InvalidDataException("The media merge operations parent is already occupied.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(parent);
        else Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ArchiveExportMetadataFile.RejectLinks(parent);
        if (Directory.EnumerateFileSystemEntries(parent).Any()) throw new InvalidDataException("The media merge parent is not empty.");
    }

    internal Task<NativeMediaMergeResult?> ReadNativeMediaMergeReceiptAsync(Guid operationId, string requestSha256,
        CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaMergeIdentity(operationId, requestSha256);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            return ArchiveSql.ReadMediaMergeReceipt(connection, null, operationId, requestSha256);
        }, cancellationToken);
    }
}
