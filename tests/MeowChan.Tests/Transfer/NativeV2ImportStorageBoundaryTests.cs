using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Direct storage-owner boundary witnesses. The fixture remains the real writer -> validated
// stage -> owner-issued plan; this file does not seed SQL or manufacture package evidence.
public sealed class NativeV2ImportStorageBoundaryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AdmittedVerificationHoldsTheExistingChildLeaseThroughTheAwaitedCallback()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        PreparedCandidate prepared = await PrepareCandidateAsync(f, admission);
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> verification = f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
            admission, f.Plan, prepared.Operation, async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return true;
            });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            MediaStore inspector = new(File.Move, directoryLeaseTimeout: TimeSpan.FromMilliseconds(75));
            PassiveMediaCandidateInspection busy = await inspector
                .InspectPassiveImportCandidateAsync(prepared.Candidate.Directory).WaitAsync(Deadline);
            Assert.Equal(PassiveMediaCandidateStatus.Busy, busy.Status);
            Assert.False(verification.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            await verification.WaitAsync(Deadline);
        }

        Assert.True(await verification.WaitAsync(Deadline));
        PassiveMediaCandidateInspection ready = await new MediaStore()
            .InspectPassiveImportCandidateAsync(prepared.Candidate.Directory).WaitAsync(Deadline);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, ready.Status);
    }

    [Fact]
    public async Task JournalMismatchBlocksTheAwaitedCallback()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        PreparedCandidate prepared = await PrepareCandidateAsync(f, admission);
        string journalPath = Path.Combine(prepared.Candidate.Directory, ".meowchan-import-candidate.json");
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        string generation = journal["ManifestGeneration"]!.GetValue<string>();
        journal["ManifestGeneration"] = generation == new string('0', 64) ? new string('1', 64) : new string('0', 64);
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        int callbacks = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store
            .WithVerifiedPassiveImportCandidateAdmittedAsync(admission, f.Plan, prepared.Operation,
                () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task PayloadMismatchBlocksTheAwaitedCallback()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        PreparedCandidate prepared = await PrepareCandidateAsync(f, admission);
        string payloadPath = Assert.Single(Directory.GetFiles(prepared.Candidate.Directory, "*.derived.bin"));
        byte[] payload = await File.ReadAllBytesAsync(payloadPath);
        Assert.NotEmpty(payload);
        payload[0] ^= 0xff;
        await File.WriteAllBytesAsync(payloadPath, payload);

        int callbacks = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store
            .WithVerifiedPassiveImportCandidateAdmittedAsync(admission, f.Plan, prepared.Operation,
                () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("request")]
    [InlineData("state")]
    public async Task PreparedIdentityAndStateAreRejectedBeforeCandidateIo(string mismatch)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        PreparedCandidate prepared = await PrepareCandidateAsync(f, admission);
        NativeMediaPersistedImportOperation mismatched = mismatch switch
        {
            "operation" => prepared.Operation with
            {
                Identity = prepared.Operation.Identity with { OperationId = Guid.NewGuid() }
            },
            "request" => prepared.Operation with
            {
                Identity = prepared.Operation.Identity with
                {
                    RequestSha256 = prepared.Operation.Identity.RequestSha256 == new string('a', 64)
                        ? new string('b', 64) : new string('a', 64)
                }
            },
            "state" => prepared.Preparing,
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        // A missing path makes the failure ordering observable: candidate I/O would
        // report a missing directory, not the prepared-operation identity rejection.
        string missing = Path.Combine(f.Plan.OperationsParent, ".meowchan-passive-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));
        mismatched = mismatched with { Full = mismatched.Full with { Directory = missing } };
        int callbacks = 0;
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => f.Store
            .WithVerifiedPassiveImportCandidateAdmittedAsync(admission, f.Plan, mismatched,
                () => { callbacks++; return Task.FromResult(true); }));
        Assert.Contains("prepared operation", error.Message);
        Assert.Equal(0, callbacks);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ForeignPlanAndAdmissionAreRejectedBeforeCandidateIo()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await using NativeV2ImportFixture foreign = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        using ProfileAdmissionToken foreignAdmission = await foreign.ExclusiveAsync();
        int callbacks = 0;
        // Null typed evidence selects the v2 API and proves that the owner guard
        // rejects the foreign plan/token before DTO validation or filesystem access.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store
            .WithVerifiedPassiveImportCandidateAdmittedAsync(admission, foreign.Plan, prepared: null!,
                callback: () => { callbacks++; return Task.FromResult(true); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store
            .WithVerifiedPassiveImportCandidateAdmittedAsync(foreignAdmission, f.Plan, prepared: null!,
                callback: () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.False(Directory.Exists(foreign.Plan.OperationsParent));
    }

    [Fact]
    public async Task ZeroOriginalCandidateRetainsEvidenceAndDerivedPayloadWithoutOriginalBinding()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(originalContained: false, derived: true, presence: false),
            [NativeV2ImportFixture.Preview]);
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        PreparedCandidate prepared = await PrepareCandidateAsync(f, admission);
        PassiveMediaImportCandidate candidate = prepared.Candidate;

        Assert.Empty(candidate.Bindings);
        Assert.Single(candidate.OriginalEvidence);
        Assert.Single(candidate.DerivedRepresentations);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.original.bin"));
        Assert.Single(Directory.GetFiles(candidate.Directory, "*.derived.bin"));
        Assert.True(await f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
            admission, f.Plan, prepared.Operation, () => Task.FromResult(true)));
    }

    private static async Task<PreparedCandidate> PrepareCandidateAsync(
        NativeV2ImportFixture f, ProfileAdmissionToken admission)
    {
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> interrupted = await f.CommitAsync(admission,
            checkpoint: phase =>
            {
                if (phase == NativeMediaImportPhase.CandidateReady)
                    throw new IOException("synthetic stop before Prepared");
            });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, interrupted.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, interrupted.Result.Status);

        NativeMediaPersistedImportOperation preparing = ReadPersistedOperation(f);
        Assert.Equal(NativeMediaImportOperationState.Preparing, preparing.State);
        string directory = Assert.Single(Directory.GetDirectories(f.Plan.OperationsParent));
        PassiveMediaCandidateInspection inspected = await f.Store.InspectPassiveImportCandidateAsync(directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        PassiveMediaImportCandidate candidate = Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate);
        Assert.False(candidate.IsLegacyCandidate);
        Assert.Equal(2, candidate.PlanVersion);

        NativeMediaPersistedImportOperation prepared = await f.Catalog.WriteTransferAdmittedAsync(
            admission, (connection, transaction) => ArchiveSql.MarkMediaImportPreparedV2(
                connection, transaction, f.Plan, candidate));
        Assert.Equal(NativeMediaImportOperationState.Prepared, prepared.State);
        return new(preparing, prepared, candidate);
    }

    private static NativeMediaPersistedImportOperation ReadPersistedOperation(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        return Assert.IsType<NativeMediaPersistedImportOperation>(ArchiveSql.ReadPersistedMediaImportOperation(
            connection, null, f.Plan.OperationId, f.Plan.RequestSha256));
    }

    private sealed record PreparedCandidate(NativeMediaPersistedImportOperation Preparing,
        NativeMediaPersistedImportOperation Operation, PassiveMediaImportCandidate Candidate);
}
