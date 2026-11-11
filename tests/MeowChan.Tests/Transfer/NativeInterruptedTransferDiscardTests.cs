using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Confirmed discard of interrupted native transfers. Interruptions here are the existing synthetic
// checkpoints; real ENOSPC/SIGKILL coverage is in NativeV2ImportDiskFullTests/NativeV2ImportCrashTests.
public sealed class NativeInterruptedTransferDiscardTests
{
    [Theory]
    [InlineData(nameof(NativeMediaImportPhase.PreparingSaved))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateReady))]
    [InlineData(nameof(NativeMediaImportPhase.PreparedSaved))]
    [InlineData(nameof(NativeMediaImportPhase.BeforeSqlCommit))]
    public async Task DiscardRemovesOnlyTheInterruptedOperationAndItsParentThenTheProfileStarts(string interruption)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await InterruptAsync(f, interruption);
        string parent = Path.Combine(f.Files.Profile, "import-media-operations", f.Plan.OperationId.ToString("N"));
        InterruptedNativeMediaOperation listed = Assert.Single(await f.Catalog.ReadInterruptedNativeMediaOperationsAsync());
        Assert.Equal((false, f.Plan.OperationId, NativeV2ImportFixture.Id), (listed.Merge, listed.OperationId, listed.Resource));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Catalog.EnsureNativeMediaRecoveryClearAsync());

        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(1, await f.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(admission));

        Assert.False(Directory.Exists(parent));
        Assert.Empty(await f.Catalog.ReadInterruptedNativeMediaOperationsAsync());
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        await f.Catalog.EnsureNativeMediaRecoveryClearAsync();
        // Repeating is a no-op.
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(0, await f.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(admission));
    }

    [Fact]
    public async Task ACommittedImportIsNeverListedOrDiscarded()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await f.CommitAsync(admission)).Result.Status);
        var before = NativeV2ImportFixture.PayloadFiles(f.Files.Profile);
        Assert.Empty(await f.Catalog.ReadInterruptedNativeMediaOperationsAsync());
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(0, await f.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(admission));
        Assert.Equal(before, NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Equal(1L, await f.Catalog.ReadAsync(connection =>
        {
            using SqliteCommand command = ArchiveSql.Command(connection, null,
                "SELECT count(*) FROM archive_media_import_operations WHERE state='Committed'");
            return (long)command.ExecuteScalar()!;
        }));
        await f.Catalog.EnsureNativeMediaRecoveryClearAsync();
    }

    [Fact]
    public async Task ANonCanonicalOperationsParentIsNotDiscardableAndKeepsBlocking()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await InterruptAsync(f, nameof(NativeMediaImportPhase.PreparingSaved));
        string foreign = Path.Combine(f.Files.Root, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "not ours");
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
            {
                using SqliteCommand command = ArchiveSql.Command(connection, transaction,
                    "UPDATE archive_media_import_operations SET operations_parent=$parent");
                command.Parameters.AddWithValue("$parent", foreign);
                return command.ExecuteNonQuery();
            });
        Assert.Empty(await f.Catalog.ReadInterruptedNativeMediaOperationsAsync());
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(0, await f.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(admission));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Catalog.EnsureNativeMediaRecoveryClearAsync());
    }

    [Fact]
    public async Task OrdinaryAdmissionCannotDiscard()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await InterruptAsync(f, nameof(NativeMediaImportPhase.PreparedSaved));
        using ProfileAdmissionToken ordinary = f.Lease.Admission.EnterStoreWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(ordinary));
        Assert.Single(await f.Catalog.ReadInterruptedNativeMediaOperationsAsync());
    }

    private static async Task InterruptAsync(NativeV2ImportFixture f, string interruption)
    {
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var outcome = await f.CommitAsync(admission, checkpoint: phase =>
        { if (phase.ToString() == interruption) throw new IOException("synthetic interruption at " + interruption); });
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
    }
}
