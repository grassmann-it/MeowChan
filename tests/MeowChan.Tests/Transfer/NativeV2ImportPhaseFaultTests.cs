using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// W32b3 coordinator fault matrix. The fixture and owner-issued plan are the only
// admission path; this file intentionally does not seed SQL or manufacture residue.
public sealed class NativeV2ImportPhaseFaultTests
{
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

    public static IEnumerable<object[]> PhaseFaults()
    {
        foreach (NativeMediaImportPhase phase in Phases)
        {
            yield return [(int)phase, false];
            yield return [(int)phase, true];
        }
    }

    [Theory]
    [MemberData(nameof(PhaseFaults))]
    public async Task EveryV2CheckpointFaultPreservesTheCorrectDurableBoundary(
        int faultPhaseValue, bool cancellationCheckpoint)
    {
        NativeMediaImportPhase faultPhase = (NativeMediaImportPhase)faultPhaseValue;
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync();
        Guid operation = fixture.Plan.OperationId;
        string request = fixture.Plan.RequestSha256;
        using CancellationTokenSource cancellation = new();
        List<NativeMediaImportPhase> seen = [];

        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome;
        using (ProfileAdmissionToken admission = await fixture.ExclusiveAsync())
        {
            outcome = await fixture.CommitAsync(admission,
                cancellation.Token,
                checkpoint: phase =>
                {
                    seen.Add(phase);
                    if (phase != faultPhase) return;
                    if (cancellationCheckpoint)
                    {
                        cancellation.Cancel();
                        throw new OperationCanceledException(cancellation.Token);
                    }
                    throw new IOException("synthetic W32b3 phase fault");
                });
        }

        int phaseIndex = Array.IndexOf(Phases, faultPhase);
        Assert.True(phaseIndex >= 0);
        Assert.Equal(Phases.Take(phaseIndex + 1), seen);

        if (faultPhase != NativeMediaImportPhase.SqlCommitted)
        {
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
            Assert.False(outcome.Result.IsReplay);

            // The public legacy operation projection is intentionally v1-only. Read the
            // versioned v2 evidence through the bounded persisted reader instead.
            NativeMediaPersistedImportOperation operationRow;
            using (SqliteConnection connection = ArchiveSql.OpenConnection(
                fixture.Catalog.DatabasePath, SqliteOpenMode.ReadOnly))
            {
                operationRow = Assert.Single(ArchiveSql.ReadPersistedMediaImportOperationsPage(
                    connection, null).Items);
            }
            Assert.Equal(operation, operationRow.Identity.OperationId);
            Assert.Equal(request, operationRow.Identity.RequestSha256);
            Assert.Equal(ExpectedState(faultPhase), operationRow.State);

            if (Array.IndexOf(Phases, faultPhase) < Array.IndexOf(Phases, NativeMediaImportPhase.PreparedSaved))
                Assert.Null(operationRow.Full.Directory);
            else
                Assert.NotNull(operationRow.Full.Directory);

            Assert.Null(await fixture.Catalog.ReadNativeMediaImportReceiptAsync(operation, request, 2));
            AssertSqlContentEmpty(fixture.Catalog.DatabasePath);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => fixture.Catalog.EnsureNativeMediaRecoveryClearAsync());

            AssertRetainedResidue(fixture.Plan.OperationsParent, faultPhase);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Files.Profile);
            Assert.Null(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        }
        else
        {
            // The callback runs after the caller transaction has committed. A lost
            // response/cancellation here is history, not an unfinished operation.
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            Assert.True(outcome.Result.IsReplay);

            NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(
                await fixture.Catalog.ReadNativeMediaImportReceiptAsync(operation, request, 2));
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
            Assert.True(receipt.IsReplay);
            Assert.Equal(3, receipt.NewPosts);
            Assert.Equal(1, receipt.NewAttachments);

            // This is the startup-clear proof for the committed v2 history. It
            // must not depend on the original stage remaining available.
            await fixture.Catalog.EnsureNativeMediaRecoveryClearAsync();
            Assert.True(Directory.Exists(fixture.Plan.OperationsParent));
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(fixture.Plan.OperationsParent));

            await fixture.Stage.DisposeAsync();
            using ProfileAdmissionToken replayAdmission = await fixture.ExclusiveAsync();
            int replayCheckpoints = 0;
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> replay = await fixture.CommitAsync(
                replayAdmission, new CancellationToken(true), _ => replayCheckpoints++);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Result.Status);
            Assert.True(replay.Result.IsReplay);
            Assert.Equal(0, replayCheckpoints);
        }
    }

    private static NativeMediaImportOperationState ExpectedState(NativeMediaImportPhase phase)
        => Array.IndexOf(Phases, phase) < Array.IndexOf(Phases, NativeMediaImportPhase.PreparedSaved)
            ? NativeMediaImportOperationState.Preparing
            : NativeMediaImportOperationState.Prepared;

    private static void AssertRetainedResidue(string parent, NativeMediaImportPhase phase)
    {
        if (phase == NativeMediaImportPhase.PreparingSaved)
        {
            Assert.False(Directory.Exists(parent));
            return;
        }

        Assert.True(Directory.Exists(parent));
        string[] children = Directory.GetDirectories(parent);
        if (phase == NativeMediaImportPhase.OperationsParentCreated)
        {
            Assert.Empty(children);
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            return;
        }

        string child = Assert.Single(children);
        if (phase == NativeMediaImportPhase.CandidateDirectoryCreated)
            Assert.Empty(Directory.EnumerateFileSystemEntries(child));
        else
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(child));
    }

    private static void AssertSqlContentEmpty(string databasePath)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        foreach (string table in ContentTables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table;
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
    }

    private static readonly ImmutableArray<string> ContentTables =
    [
        "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
        "archive_source_contracts", "archive_quotes", "archive_thread_search", "archive_post_search",
        "media_decisions"
    ];
}
