using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// W32b5 fault/recovery lane, authored before v4 merge integration. These tests
// deliberately require success from Prepare; UnsupportedPackage is NOT a pass/skip.
// Target content comes only from NativeV2ImportFixture's real admitted v2 import.
// No SQL seeding, invented v2 executor, forged admission, provider or decoder.
public sealed class NativeV2MergeFaultRecoveryTests
{
    private const string AddedPostId = "18";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly NativeMediaImportPhase[] Phases =
    [
        NativeMediaImportPhase.PreparingSaved,
        NativeMediaImportPhase.OperationsParentCreated,
        NativeMediaImportPhase.CandidateDirectoryCreated,
        NativeMediaImportPhase.CandidateReady,
        NativeMediaImportPhase.PreparedSaved,
        NativeMediaImportPhase.CandidateVerified,
        NativeMediaImportPhase.BeforeSqlCommit,
        NativeMediaImportPhase.SqlCommitted
    ];

    public static IEnumerable<object[]> BeforeCommitFaults()
    {
        foreach (NativeMediaImportPhase phase in Phases.Take(Phases.Length - 1))
        foreach (bool cancellation in new[] { false, true })
        foreach (bool originalContained in new[] { false, true })
            yield return [(int)phase, cancellation, originalContained];
    }

    [Theory]
    [MemberData(nameof(BeforeCommitFaults))]
    public async Task FaultBeforeCommitRetainsV2EvidenceWithoutChangingTheExisting404Target(
        int phaseValue, bool cancellation, bool originalContained)
    {
        NativeMediaImportPhase faultPhase = (NativeMediaImportPhase)phaseValue;
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        TargetSnapshot before = await CaptureTargetAsync(f);
        MergeInput merge = await PrepareMergeAsync(f, originalContained);
        await using NativeTransferImportStage stage = merge.Stage;
        using CancellationTokenSource cancel = new();
        List<NativeMediaImportPhase> seen = [];

        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
                await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, merge.Plan, f.Store,
                    cancel.Token, checkpoint: phase =>
                    {
                        seen.Add(phase);
                        if (phase == faultPhase) Interrupt(cancel, cancellation);
                    });

            Assert.Equal(Phases.Take(Array.IndexOf(Phases, faultPhase) + 1), seen);
            Assert.Equal(cancellation, cancel.IsCancellationRequested);
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
            Assert.False(outcome.Result.IsReplay);
            f.Catalog.EnsureTransferAdmission(admission); // The borrower did not release our owner.

            NativeMediaImportOperationState state = Array.IndexOf(Phases, faultPhase)
                < Array.IndexOf(Phases, NativeMediaImportPhase.PreparedSaved)
                ? NativeMediaImportOperationState.Preparing : NativeMediaImportOperationState.Prepared;
            NativeMediaPersistedMergeOperation operation = AssertMergeEvidence(f.Catalog.DatabasePath,
                merge, state, receipt: false);
            await AssertTargetUnchangedAsync(f, before);

            string[] retainedSql = SnapshotSql(f.Catalog.DatabasePath, AllTables);
            string[] retainedFiles = SnapshotTree(merge.Plan.OperationsParent);
            await AssertResidueAsync(f, merge, operation, faultPhase);
            await AssertRecoveryGateAsync(f.Catalog, blocked: true);

            // An unresolved claim is not an inactive retry. Remove only the owned
            // staging handle; neither reconciliation nor retry may copy it again.
            await stage.DisposeAsync();
            int retryPhases = 0;
            ProfileExclusiveOutcome<NativeMediaMergeResult> retry =
                await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, merge.Plan, f.Store,
                    checkpoint: _ => retryPhases++);
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, retry.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, retry.Result.Status);
            Assert.False(retry.Result.IsReplay);
            Assert.Equal(0, retryPhases);
            f.Catalog.EnsureTransferAdmission(admission);
            AssertMergeEvidence(f.Catalog.DatabasePath, merge, state, receipt: false);
            Assert.Equal(retainedSql, SnapshotSql(f.Catalog.DatabasePath, AllTables));
            Assert.Equal(retainedFiles, SnapshotTree(merge.Plan.OperationsParent));
            await AssertTargetUnchangedAsync(f, before);
        }

        // Release the original catalog/lease, not just the stage. OpenAsync alone
        // is insufficient: both public Registry startup paths must fail closed.
        await f.CloseOwnerAsync();
        await AssertTwoRealStartupsAsync(f, merge, blocked: true);
        await AssertTargetUnchangedAsync(f, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LostSqlCommittedResponseReplaysTheReceiptAndAllowsRealOfflineStartup(
        bool cancellation, bool originalContained)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        TargetSnapshot before = await CaptureTargetAsync(f);
        MergeInput merge = await PrepareMergeAsync(f, originalContained);
        await using NativeTransferImportStage stage = merge.Stage;
        using CancellationTokenSource cancel = new();
        List<NativeMediaImportPhase> seen = [];
        NativeMediaMergeResult recovered;

        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
                await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, merge.Plan, f.Store,
                    cancel.Token, checkpoint: phase =>
                    {
                        seen.Add(phase);
                        if (phase == NativeMediaImportPhase.SqlCommitted) Interrupt(cancel, cancellation);
                    });
            Assert.Equal(Phases, seen);
            Assert.Equal(cancellation, cancel.IsCancellationRequested);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            recovered = outcome.Result;
            Assert.Equal(NativeMediaImportCommitStatus.Committed, recovered.Status);
            Assert.True(recovered.IsReplay); // The response was lost AFTER SQL committed.
            Assert.Equal(cancellation ? nameof(OperationCanceledException) : nameof(IOException), recovered.Diagnostic);
            Assert.Equal(1, recovered.NewPosts);
            Assert.Equal(0, recovered.UpdatedPosts);
            Assert.Equal(1, recovered.NewAttachments);
            Assert.Equal(originalContained ? 1 : 0, recovered.NewBindings);
            Assert.Equal(0, recovered.Conflicts);
            f.Catalog.EnsureTransferAdmission(admission);

            NativeMediaPersistedMergeOperation operation = AssertMergeEvidence(f.Catalog.DatabasePath,
                merge, NativeMediaImportOperationState.Committed, receipt: true);
            await AssertResidueAsync(f, merge, operation, NativeMediaImportPhase.SqlCommitted);
            await AssertCommittedTargetAsync(f, before, operation);
            await AssertRecoveryGateAsync(f.Catalog, blocked: false);
        }

        // A separately acquired real Exclusive must see history before cancellation,
        // callbacks or disposed stage IO; no plan is transferred to another catalog.
        await stage.DisposeAsync();
        await f.Stage.DisposeAsync();
        string[] committedSql = SnapshotSql(f.Catalog.DatabasePath, AllTables);
        string[] committedFiles = SnapshotMediaTrees(f, merge);
        using (ProfileAdmissionToken replayAdmission = await f.ExclusiveAsync())
        {
            int beforeAdmissions = 0, replayPhases = 0;
            ProfileExclusiveOutcome<NativeMediaMergeResult> replay =
                await f.Catalog.CommitNativeMediaMergeAdmittedAsync(replayAdmission, merge.Plan, f.Store,
                    new CancellationToken(true), beforeAdmission: () => beforeAdmissions++,
                    checkpoint: _ => replayPhases++);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
            Assert.Equal(recovered with { Diagnostic = null }, replay.Result);
            Assert.Equal(0, beforeAdmissions);
            Assert.Equal(0, replayPhases);
            f.Catalog.EnsureTransferAdmission(replayAdmission);
        }
        AssertMergeEvidence(f.Catalog.DatabasePath, merge, NativeMediaImportOperationState.Committed, receipt: true);
        Assert.Equal(committedSql, SnapshotSql(f.Catalog.DatabasePath, AllTables));
        Assert.Equal(committedFiles, SnapshotMediaTrees(f, merge));

        await f.CloseOwnerAsync();
        await AssertTwoRealStartupsAsync(f, merge, blocked: false);
        AssertMergeEvidence(f.Catalog.DatabasePath, merge, NativeMediaImportOperationState.Committed, receipt: true);
    }

    private static void Interrupt(CancellationTokenSource cancel, bool cancellation)
    {
        if (!cancellation) throw new IOException("synthetic W32b5 merge checkpoint response loss");
        cancel.Cancel();
        // Same explicit checkpoint cancellation as NativeV2ImportPhaseFaultTests;
        // throwing here also models response cancellation after the final commit.
        cancel.Token.ThrowIfCancellationRequested();
    }

    private static async Task SeedTargetAsync(NativeV2ImportFixture f)
    {
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> imported = await f.CommitAsync(admission);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
            Assert.False(imported.Result.IsReplay);
            Assert.Equal(3, imported.Result.NewPosts);
            Assert.Equal(1, imported.Result.NewAttachments);
            f.Catalog.EnsureTransferAdmission(admission);
        }
        await AssertRecoveryGateAsync(f.Catalog, blocked: false);
    }

    private static async Task<MergeInput> PrepareMergeAsync(NativeV2ImportFixture f, bool originalContained)
    {
        NativeTransferCatalog content = NativeV2ImportFixture.Content();
        TransferThread thread = content.Threads[0];
        TransferAttachment added = NativeV2ImportFixture.Attachment(AddedPostId,
            originalContained ? TransferContentStatus.Contained : TransferContentStatus.Omitted);
        NativeTransferImportStage stage = await NativeV2ImportFixture.StageAsync(f.Files,
            content with { Threads = [thread with { Posts = thread.Posts.Add(NativeV2ImportFixture.Post(AddedPostId, added)) }] },
            NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
        try
        {
            NativeMediaMergePreparation preparation = await f.Catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, preparation.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
            Assert.Equal(ThreadSourceState.NotFound, plan.Target.Summary.SourceState);
            Assert.Equal(0, plan.Conflicts);
            Assert.False(Directory.Exists(plan.OperationsParent));

            // Expected descriptors are derived independently from the validated stage,
            // not from a future merge-plan property or an original-only key projection.
            NativeMediaCandidateSelection full = NativeMediaMaterializationPlan.Create(stage,
                NativeV2ImportFixture.Id).FullSelection;
            NativeMediaCandidateSelection safe = full.Select(full.Members
                .Where(member => member.Id.Occurrence.PostId == AddedPostId).Select(member => member.Id));
            Assert.False(safe.IsEmpty);
            Assert.NotEqual(full.CanonicalJson, safe.CanonicalJson);
            Assert.Equal(originalContained ? 3 : 2, safe.Members.Length);
            // Old and new occurrences share payload hashes. They must not share member
            // identities; the old occurrence remains only in Full, not in active Safe.
            Assert.DoesNotContain(safe.Members, member => member.Id.Occurrence.PostId == "15");
            return new(stage, plan, full, safe, originalContained);
        }
        catch { await stage.DisposeAsync(); throw; }
    }

    private static NativeMediaPersistedMergeOperation AssertMergeEvidence(string databasePath,
        MergeInput merge, NativeMediaImportOperationState state, bool receipt)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        NativeMediaPersistedPage<NativeMediaPersistedMergeOperation> page =
            ArchiveSql.ReadPersistedMediaMergeOperationsPage(connection, null);
        Assert.Single(page.Items);
        Assert.Null(page.NextOperationId);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        NativeMediaMergePlan plan = merge.Plan;
        NativeMediaPersistedMergeOperation operation = Assert.IsType<NativeMediaPersistedMergeOperation>(
            ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction, plan.OperationId, plan.RequestSha256));
        Assert.Equal(new NativeMediaPersistedIdentity(2, plan.OperationId, plan.RequestSha256,
            plan.Package.Envelope.Fingerprint, plan.Instance, plan.Generation, plan.Resource,
            plan.OperationsParent, plan.ImportedAtUtc), operation.Identity);
        Assert.Equal(state, operation.State);
        Assert.Equal(plan.Roots.ToArray(), operation.Roots.ToArray());
        Assert.Equal(plan.CatalogBytes.ToArray(), operation.CatalogBytes.ToArray());
        Assert.Equal(new NativeMediaMergeCounts(1, 0, 1, merge.OriginalContained ? 1 : 0, 0, 1, 1, 1), operation.Counts);
        bool prepared = state != NativeMediaImportOperationState.Preparing;
        AssertSelection(operation.Full, merge.Full, operation.Identity, prepared);
        AssertSelection(operation.Safe, merge.Safe, operation.Identity, prepared);
        if (prepared) Assert.NotEqual(operation.Full.Directory, operation.Safe.Directory);

        // Existing typed reader binds plan version and ALL additive counters; no
        // speculative result properties or nonexistent receipt overload are needed.
        NativeMediaPersistedMergeReceipt? stored = ArchiveSql.ReadPersistedMediaMergeReceipt(connection,
            transaction, plan.OperationId, plan.RequestSha256, 2);
        if (receipt)
        {
            Assert.NotNull(stored);
            Assert.Equal(new NativeMediaReceiptIdentity(plan.OperationId, plan.RequestSha256, 2), stored.Identity);
            Assert.Equal(operation.Counts, stored.Counts);
            Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, stored));
        }
        else Assert.Null(stored);
        return operation;
    }

    private static void AssertSelection(NativeMediaPersistedRoot root, NativeMediaCandidateSelection expected,
        NativeMediaPersistedIdentity identity, bool prepared)
    {
        Assert.Equal("[]", root.LegacyKeysJson);
        Assert.Empty(root.LegacyKeys);
        Assert.Null(root.LegacyBindingsSha256);
        Assert.Equal(expected.CanonicalJson, Assert.IsType<NativeMediaCandidateSelection>(root.Selection).CanonicalJson);
        Assert.Equal(expected.Sha256, root.SelectionSha256);
        Assert.Equal(expected.Members.Length, root.MemberCount);
        if (prepared)
        {
            Assert.False(string.IsNullOrWhiteSpace(root.Directory));
            Assert.Equal(identity.OperationsParent, Path.GetDirectoryName(root.Directory));
            Assert.False(string.IsNullOrWhiteSpace(root.ManifestGeneration));
            Assert.Equal(expected.CreateInventory(identity.OperationId, identity.PackageFingerprint,
                identity.ImportedAtUtc).Sha256, root.InventorySha256);
        }
        else
        {
            Assert.Null(root.Directory);
            Assert.Null(root.ManifestGeneration);
            Assert.Null(root.InventorySha256);
        }
    }

    private static async Task AssertResidueAsync(NativeV2ImportFixture f, MergeInput merge,
        NativeMediaPersistedMergeOperation operation, NativeMediaImportPhase phase)
    {
        string parent = merge.Plan.OperationsParent;
        if (phase == NativeMediaImportPhase.PreparingSaved)
        {
            Assert.False(Directory.Exists(parent));
            return;
        }
        Assert.True(Directory.Exists(parent));
        string[] children = Directory.GetDirectories(parent);
        if (phase == NativeMediaImportPhase.OperationsParentCreated)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            return;
        }
        if (phase == NativeMediaImportPhase.CandidateDirectoryCreated)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(Assert.Single(children)));
            return;
        }
        if (phase == NativeMediaImportPhase.CandidateReady)
        {
            // Existing coordinator checkpoint is Full Ready, BEFORE Safe creation
            // and BEFORE the Prepared SQL write. Ready files are still inactive.
            await AssertReadyCandidateAsync(f.Store, Assert.Single(children), merge.Full, operation.Identity);
            return;
        }
        Assert.Equal(2, children.Length);
        Assert.Contains(operation.Full.Directory!, children);
        Assert.Contains(operation.Safe.Directory!, children);
        await AssertReadyCandidateAsync(f.Store, operation.Full.Directory!, merge.Full, operation.Identity,
            operation.Full.ManifestGeneration);
        await AssertReadyCandidateAsync(f.Store, operation.Safe.Directory!, merge.Safe, operation.Identity,
            operation.Safe.ManifestGeneration);
    }

    private static async Task AssertReadyCandidateAsync(MediaStore store, string directory,
        NativeMediaCandidateSelection expected, NativeMediaPersistedIdentity identity, string? generation = null)
    {
        // Uses the real Journal2 inspector after coordinator scopes have unwound;
        // neither constructs a Ready DTO nor creates a missing candidate lock.
        PassiveMediaCandidateInspection inspection = await store.InspectPassiveImportCandidateAsync(directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspection.Status);
        PassiveMediaImportCandidate candidate = Assert.IsType<PassiveMediaImportCandidate>(inspection.Candidate);
        Assert.Equal(2, candidate.PlanVersion);
        Assert.Equal(identity.OperationId, candidate.OperationId);
        Assert.Equal(identity.PackageFingerprint, candidate.PackageFingerprint);
        Assert.Equal(identity.Resource, candidate.Resource);
        Assert.Equal(identity.ImportedAtUtc, candidate.ImportedAtUtc);
        Assert.Equal(expected.CanonicalJson, Assert.IsType<NativeMediaCandidateSelection>(candidate.Selection).CanonicalJson);
        NativeMediaCandidateInventory inventory = expected.CreateInventory(identity.OperationId,
            identity.PackageFingerprint, identity.ImportedAtUtc);
        Assert.Equal(inventory.Sha256, candidate.InventorySha256);
        Assert.Equal(inventory.Bindings.ToArray(), candidate.Bindings.ToArray());
        Assert.Equal(inventory.OriginalEvidence.ToArray(), candidate.OriginalEvidence.ToArray());
        Assert.Equal(inventory.DerivedRepresentations.ToArray(), candidate.DerivedRepresentations.ToArray());
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(MediaManifestStatus.Available, manifest.Status);
        Assert.Equal(6, manifest.ManifestVersion);
        Assert.Equal(candidate.ManifestGeneration, manifest.Generation);
        if (generation != null) Assert.Equal(generation, manifest.Generation);
    }

    private static async Task AssertRecoveryGateAsync(ArchiveCatalog catalog, bool blocked)
    {
        await catalog.ReadAsync(connection =>
        {
            using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
            {
                Assert.Equal(blocked, ArchiveSql.HasNativeMediaTransferRecoveryBlocker(connection, transaction));
                using SqliteCommand probe = ArchiveSql.Command(connection, transaction, "SELECT 1");
                Assert.Equal(1L, probe.ExecuteScalar()); // Recovery did not consume the caller's transaction.
                transaction.Rollback();
            }
            Assert.Equal(blocked, ArchiveSql.HasNativeMediaTransferRecoveryBlocker(connection, null));
            return true;
        });
        if (!blocked) { await catalog.EnsureNativeMediaRecoveryClearAsync(); return; }
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureNativeMediaRecoveryClearAsync());
        Assert.Equal("Native media transfer reconciliation is required.", error.Message);
        Assert.Null(error.InnerException);
    }

    private static async Task AssertTwoRealStartupsAsync(NativeV2ImportFixture f, MergeInput merge, bool blocked)
    {
        string[] sql = SnapshotSql(f.Catalog.DatabasePath, AllTables);
        string[] media = SnapshotMediaTrees(f, merge);
        // Each iteration acquires a fresh lease. Exercise both production startup
        // branches: content runtime, then WorkStore-only temporary archive owner.
        foreach (bool withContentRuntime in new[] { true, false })
        {
            using (ProfileLease lease = ProfileLease.Acquire(f.Files.Profile))
            {
                Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
                await using ThreadContentRuntime? runtime = withContentRuntime ? new(f.Files.Profile, lease) : null;
                ObservedSession session = new(new SessionStore(f.Files.Profile, lease));
                DownloadQueue queue = new(1, 1048576);
                await using Registry registry = new([], queue, Path.Combine(f.Files.Profile, "offline-output"),
                    TimeSpan.FromHours(1), 3600, sessionStore: session, contentRuntime: runtime,
                    workStore: new WorkStateStore(f.Files.Profile, lease));
                // No providers, URLs or durable work are registered: this is real
                // startup/recovery without permission for any network activity.
                Assert.Equal(0, session.Loads);
                Task start = registry.StartAsync(lease);
                if (blocked)
                {
                    InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => start.WaitAsync(Deadline));
                    Assert.Equal("Native media transfer reconciliation is required.", error.Message);
                    Assert.Null(error.InnerException);
                    Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, lease.Admission.Phase);
                    Assert.Equal(0, session.Loads); // Guard failed BEFORE restore, not just before producers.
                    await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.RestoreAsync().WaitAsync(Deadline));
                }
                else
                {
                    await start.WaitAsync(Deadline);
                    Assert.Equal(ProfileAdmissionPhase.Ready, lease.Admission.Phase);
                    Assert.Equal(1, session.Loads); // A genuine restore, not an OpenAsync-only assertion.
                    if (runtime != null) Assert.True(runtime.IsInitialized);
                    AssertMergeEvidence(f.Catalog.DatabasePath, merge, NativeMediaImportOperationState.Committed, receipt: true);
                }
                Assert.Same(start, registry.StartAsync(lease));
                Assert.Empty(registry.Threads);
                Assert.Empty(registry.Boards);
                Assert.Equal(0L, registry.ReceivedMediaBytes);
                Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
                await registry.StopAsync().WaitAsync(Deadline);
            }
            // Include disposal in the no-repair/no-cleanup oracle. Journals, receipts,
            // imported target bytes and inactive Full/Safe candidates must all remain.
            Assert.Equal(sql, SnapshotSql(f.Catalog.DatabasePath, AllTables));
            Assert.Equal(media, SnapshotMediaTrees(f, merge));
        }
    }

    private static async Task<TargetSnapshot> CaptureTargetAsync(NativeV2ImportFixture f)
    {
        ArchivedThreadView view = await ReadTargetAsync(f.Files.Profile);
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.Equal(3, view.PostCount);
        Assert.Single(view.MediaDirectories);
        return new(view, SnapshotSql(f.Catalog.DatabasePath, TargetTables), SnapshotRoots(view.MediaDirectories));
    }

    private static async Task<ArchivedThreadView> ReadTargetAsync(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
    }

    private static async Task AssertTargetUnchangedAsync(NativeV2ImportFixture f, TargetSnapshot before)
    {
        ArchivedThreadView after = await ReadTargetAsync(f.Files.Profile);
        Assert.Equal(before.View.Summary, after.Summary);
        Assert.Equal(before.View.DecimalPostOrder, after.DecimalPostOrder);
        Assert.Equal(before.View.MediaDirectories, after.MediaDirectories);
        Assert.Equal(before.View.PostCount, after.PostCount);
        AssertOldPostsUnchanged(before.View, after);
        Assert.DoesNotContain(after.GetPosts(), post => post.PostId == AddedPostId);
        Assert.Equal(before.Sql, SnapshotSql(f.Catalog.DatabasePath, TargetTables));
        Assert.Equal(before.RootFiles, SnapshotRoots(before.View.MediaDirectories));
    }

    private static async Task AssertCommittedTargetAsync(NativeV2ImportFixture f, TargetSnapshot before,
        NativeMediaPersistedMergeOperation operation)
    {
        ArchivedThreadView after = await ReadTargetAsync(f.Files.Profile);
        Assert.Equal(before.View.Summary with { PostCount = 4, Revision = before.View.Summary.Revision + 1 }, after.Summary);
        Assert.Equal(before.View.DecimalPostOrder, after.DecimalPostOrder);
        Assert.Equal(4, after.PostCount);
        AssertOldPostsUnchanged(before.View, after);
        Assert.Single(Assert.Single(after.GetPosts(), post => post.PostId == AddedPostId).Attachments!);
        Assert.Equal(before.View.MediaDirectories.Append(operation.Safe.Directory!).Order(StringComparer.Ordinal),
            after.MediaDirectories.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(operation.Full.Directory!, after.MediaDirectories);
        Assert.Equal(before.RootFiles, SnapshotRoots(before.View.MediaDirectories));
    }

    private static void AssertOldPostsUnchanged(ArchivedThreadView before, ArchivedThreadView after)
    {
        foreach (CapturedPost expected in before.GetPosts())
        {
            CapturedPost actual = Assert.Single(after.GetPosts(), post => post.PostId == expected.PostId);
            Assert.Equal(expected with { Attachments = null }, actual with { Attachments = null });
            Assert.Equal(expected.Attachments, actual.Attachments); // Includes known-null vs explicit-empty.
        }
    }

    // Logical SQL rows, rather than database/WAL file bytes, distinguish the allowed
    // journal writes from forbidden target changes and include trigger/fence effects.
    private static string[] SnapshotSql(string databasePath, IEnumerable<string> tables)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        List<string> rows = [];
        foreach (string table in tables)
        {
            // Every table identifier comes exclusively from the fixed arrays below.
            using SqliteCommand command = ArchiveSql.Command(connection, transaction, "SELECT rowid,* FROM " + table + " ORDER BY rowid");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                object?[] values = new object?[reader.FieldCount];
                for (int i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(table + ":" + JsonSerializer.Serialize(values));
            }
        }
        return rows.ToArray();
    }

    private static string[] SnapshotTree(string root)
    {
        if (!Directory.Exists(root)) return ["missing:" + root];
        return new[] { "directory:" + root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path)
                ? "directory:" + Path.GetRelativePath(root, path)
                : "file:" + Path.GetRelativePath(root, path) + ":" + NativeV2ImportFixture.Hash(File.ReadAllBytes(path)))).ToArray();
    }

    private static string[] SnapshotRoots(IEnumerable<string> roots) => roots.Order(StringComparer.Ordinal)
        .SelectMany(root => SnapshotTree(root).Select(entry => root + ":" + entry)).ToArray();

    private static string[] SnapshotMediaTrees(NativeV2ImportFixture f, MergeInput merge)
        => SnapshotRoots([f.Plan.OperationsParent, merge.Plan.OperationsParent]);

    private static readonly string[] TargetTables =
    [
        "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
        "archive_source_contracts", "archive_quotes", "archive_thread_search", "archive_post_search",
        "media_decisions", "archive_import_candidates", "archive_import_receipts", "archive_import_fence",
        "archive_media_import_operations", "archive_media_import_receipts"
    ];
    private static readonly string[] AllTables = [.. TargetTables, "archive_media_merge_operations", "archive_media_merge_receipts"];

    private sealed record TargetSnapshot(ArchivedThreadView View, string[] Sql, string[] RootFiles);
    private sealed record MergeInput(NativeTransferImportStage Stage, NativeMediaMergePlan Plan,
        NativeMediaCandidateSelection Full, NativeMediaCandidateSelection Safe, bool OriginalContained);

    private sealed class ObservedSession(SessionStore inner) : ISessionStore
    {
        internal int Loads;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Loads); return inner.LoadAsync(cancellationToken); }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
            => inner.SaveAsync(snapshot, cancellationToken);
    }
}
