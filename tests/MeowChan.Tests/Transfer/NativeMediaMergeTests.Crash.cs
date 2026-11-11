using System.IO;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Xunit.Abstractions;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Real SIGKILL during an additive 404 merge, mirroring NativeV2ImportCrashTests. Opt-in.
public sealed partial class NativeMediaMergeTests(ITestOutputHelper output)
{
    [CrashRunTheory]
    [InlineData(nameof(NativeMediaImportPhase.PreparingSaved))]
    [InlineData(nameof(NativeMediaImportPhase.OperationsParentCreated))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateDirectoryCreated))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateReady))]
    [InlineData(nameof(NativeMediaImportPhase.PreparedSaved))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateVerified))]
    [InlineData(nameof(NativeMediaImportPhase.BeforeSqlCommit))]
    [InlineData(nameof(NativeMediaImportPhase.SqlCommitted))]
    public async Task KillingTheProcessAtAMergeCheckpointKeepsTheTargetAndAnHonestGuard(string phase)
    {
        string root = await NativeV2ImportCrashTests.KillAtCheckpointAsync(
            nameof(NativeMediaMergeTests) + "." + nameof(MergeChildCommitsUntilTheCheckpointAndWaits), phase);
        try
        {
            string profile = Path.Combine(root, "profile");
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            string guard;
            try { await catalog.EnsureNativeMediaRecoveryClearAsync(); guard = "clear"; }
            catch (InvalidDataException error) { guard = "blocked: " + error.Message; }
            // The 404 target published by the seed import survives every kill.
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
            ArchivedThreadView target = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
            output.WriteLine($"phase={phase} guard={guard} targetPosts={target.PostCount}");
            if (phase == nameof(NativeMediaImportPhase.SqlCommitted)) Assert.Equal("clear", guard);
            else Assert.StartsWith("blocked", guard);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [CrashChildFact]
    public async Task MergeChildCommitsUntilTheCheckpointAndWaits()
    {
        string target = Environment.GetEnvironmentVariable("MEOWCHAN_CRASH_CHILD")!;
        // Never disposed: the parent kills this process while the checkpoint holds.
        Workspace files = new();
        ProfileLease lease = ProfileLease.Acquire(files.Profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed404Target(lease, catalog, files);
        NativeTransferImportStage stage = await Stage(files, ContentWithAddedPost("19", "added by merge"));
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>((await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase.ToString() == target) NativeV2ImportCrashTests.HoldAtCheckpoint(Path.GetDirectoryName(files.Profile)!);
        });
        throw new InvalidOperationException("The merge checkpoint " + target + " was not reached.");
    }
}
