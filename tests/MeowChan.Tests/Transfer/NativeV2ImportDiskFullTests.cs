using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;
using Xunit.Abstractions;

namespace MeowChan.Tests.Transfer;

// Real ENOSPC on a small dedicated filesystem, not a synthetic IOException. Run with
// MEOWCHAN_FS_FIXTURE_ROOT=<mount> and TMPDIR=<mount>/tmp so profile and stage live there.
public sealed class NativeV2ImportDiskFullTests(ITestOutputHelper output)
{
    [DiskFullFixtureTheory]
    [InlineData(0)]
    [InlineData(4 * 1024)]
    [InlineData(64 * 1024)]
    [InlineData(256 * 1024)]
    public async Task AFullDiskNeverPublishesPartialContentAndEveryOutcomeEndsInAUsableProfile(long freeBytes)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        string filler = Path.Combine(f.Files.Root, "filler.bin");
        ProfileExclusiveOutcome<NativeMediaImportCommitResult>? full = null;
        Exception? thrown = null;
        try
        {
            Fill(filler, freeBytes);
            using ProfileAdmissionToken admission = await f.ExclusiveAsync();
            try { full = await f.CommitAsync(admission); }
            catch (Exception error) { thrown = error; }
        }
        finally { File.Delete(filler); }
        output.WriteLine($"free={freeBytes} disposition={full?.Disposition} status={full?.Result.Status} " +
            $"thrown={thrown?.GetType().Name}: {thrown?.Message}");

        // A committed result must have its bytes; anything else stays an unpublished candidate.
        NativeMediaImportCommitStatus status = Assert.IsType<ProfileExclusiveOutcome<NativeMediaImportCommitResult>>(full).Result.Status;
        if (status == NativeMediaImportCommitStatus.Committed) { AssertPublished(f); return; }
        Assert.Null(thrown);
        bool durable = status == NativeMediaImportCommitStatus.ReconciliationRequired;
        if (!durable)
        {
            // Failed before a durable claim: once space returns, the same plan completes.
            using ProfileAdmissionToken admission = await f.ExclusiveAsync();
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> resumed = await f.CommitAsync(admission);
            output.WriteLine($"resume disposition={resumed.Disposition} status={resumed.Result.Status}");
            Assert.Equal(NativeMediaImportCommitStatus.Committed, resumed.Result.Status);
            AssertPublished(f);
        }

        // Restart on the same profile with space available: the catalog opens. An interrupted durable
        // operation blocks startup until it is discarded; then the same package imports again.
        string profile = f.Files.Profile;
        await f.CloseOwnerAsync();
        if (durable)
        {
            await NativeV2ImportFixture.AssertDiscardClearsStartupAndReimportCommitsAsync(profile, f.Files.StageRoot);
            output.WriteLine("restart: blocked -> discarded -> clear -> re-import committed");
            return;
        }
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(profile, lease);
        await reopened.EnsureNativeMediaRecoveryClearAsync();
    }

    private static void AssertPublished(NativeV2ImportFixture f) =>
        Assert.Contains(NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original),
            NativeV2ImportFixture.PayloadFiles(f.Files.Profile).Values);

    // Leaves at most freeBytes (page-rounded by the filesystem) available on the fixture mount.
    private static void Fill(string path, long freeBytes)
    {
        DriveInfo drive = new(Path.GetDirectoryName(path)!);
        byte[] chunk = new byte[1024 * 1024];
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            for (int size = chunk.Length; size >= 4096; size /= 2)
                while (drive.AvailableFreeSpace - size >= freeBytes) stream.Write(chunk, 0, size);
            stream.Flush(true);
        }
        catch (IOException) { /* Already full: the strictest level. */ }
    }
}

public sealed class DiskFullFixtureTheoryAttribute : TheoryAttribute
{
    public DiskFullFixtureTheoryAttribute()
    {
        string? root = Environment.GetEnvironmentVariable("MEOWCHAN_FS_FIXTURE_ROOT");
        if (string.IsNullOrEmpty(root))
            Skip = "Requires MEOWCHAN_FS_FIXTURE_ROOT, a small dedicated filesystem; no real disk-full coverage is claimed without it.";
        else if (!Path.GetFullPath(TemporaryWorkspacePaths.TempRoot).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
            Skip = "TMPDIR must point inside MEOWCHAN_FS_FIXTURE_ROOT so profile and stage live on the fixture filesystem.";
    }
}
