using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferArchivePublisherTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("d99b6c74-1b52-49a8-8bad-7a6fd5bf04da");

    [Fact]
    public async Task NewFileReopensAsExactOfflinePackageWithoutMutatingSource()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "thread.meowchan.zip");
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
        Assert.True(result.IsPublished, result.Error.ToString()); Assert.False(result.StageRetained);
        Assert.Equal(new FileInfo(target).Length, result.BytesWritten);
        await using FileStream stream = File.OpenRead(target);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stream);
        Assert.True(inspected.IsPackageValid); Assert.Equal(inspected.Package!.Envelope.Fingerprint, result.PackageFingerprint);
        Assert.Equal(ThreadSourceState.NotFound, inspected.Package.Catalog.Threads.Single().SourceState);
        Assert.Equal(2, inspected.Package.Envelope.Payloads.Length);
        Assert.Equal(fixture.StrictBytes.ToArray(), await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
        Assert.Equal(fixture.ManifestGeneration, (await MeowChan.Storage.MediaAssetReader.OpenAsync(fixture.MediaDirectory)).Generation);
        Assert.Equal(0, fixture.UnexpectedProviderCalls); Assert.Empty(Pending(output.Path));
        Assert.DoesNotContain(output.Path, result.ToString());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExistingFileOrDirectoryIsNeverReplaced(bool directory)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "keep-existing");
        if (directory) { Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "KEEP"); }
        else await File.WriteAllTextAsync(target, "KEEP");
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
        Assert.False(result.IsPublished); Assert.Equal(TransferPublicationError.DestinationExists, result.Error);
        Assert.Equal("KEEP", await File.ReadAllTextAsync(directory ? Path.Combine(target, "keep.txt") : target));
        Assert.Null(result.PackageFingerprint); Assert.Null(result.BytesWritten); Assert.Empty(Pending(output.Path));
    }

    [Theory] [InlineData("")] [InlineData("  ")] [InlineData("relative.zip")] [InlineData("bad\0path")]
    public async Task MalformedOrRelativeDestinationDoesNotCreateAnything(string target)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(await FreezeAsync(reader, fixture), target);
        Assert.Equal(TransferPublicationError.InvalidDestination, result.Error); Assert.False(result.IsPublished);
        Assert.Null(result.PackageFingerprint); Assert.Null(result.BytesWritten);
    }

    [Fact]
    public async Task MissingParentIsNotCreatedAndDirectorySpellingIsNotAcceptedAsFile()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); string parent = Path.Combine(output.Path, "missing-parent");
        Assert.Equal(TransferPublicationError.InvalidDestination,
            (await NativeTransferArchivePublisher.PublishAsync(snapshot, Path.Combine(parent, "thread.zip"))).Error);
        Assert.False(Directory.Exists(parent));
        Assert.Equal(TransferPublicationError.InvalidDestination,
            (await NativeTransferArchivePublisher.PublishAsync(snapshot, output.Path + Path.DirectorySeparatorChar)).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output.Path));
    }

    [Fact]
    public async Task ChangedMediaAfterFreezeFailsWithoutPublishingOrRepair()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        byte[] changed = fixture.StrictBytes.ToArray(); changed[0] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.StrictCommit.Path, changed);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "thread.zip");
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
        Assert.False(result.IsPublished); Assert.Equal(TransferPublicationError.ExportFailed, result.Error);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.ArchiveError);
        Assert.False(File.Exists(target)); Assert.Empty(Pending(output.Path));
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
    }

    [Fact]
    public async Task DeletedThreadCannotPublishFrozenDataAndPrivateStageIsRemoved()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using (MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(fixture.ProfilePath))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(fixture.ProfilePath, lease))
            await catalog.DeleteThreadsAsync([fixture.ThreadId]);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "thread.zip");
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
        Assert.Equal(TransferPublicationError.ExportFailed, result.Error); Assert.Equal(TransferArchiveError.StaleSnapshot, result.ArchiveError);
        Assert.False(result.IsPublished); Assert.False(File.Exists(target)); Assert.Empty(Pending(output.Path));
    }

    [Fact]
    public async Task PrecancellationNeverCreatesTargetAndDoesNotRemoveAnUnrelatedPendingFile()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "thread.zip");
        string unrelated = Path.Combine(output.Path, ".meowchan-transfer-" + Guid.NewGuid().ToString("N") + ".pending");
        await File.WriteAllTextAsync(unrelated, "NOT OUR STAGE");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NativeTransferArchivePublisher.PublishAsync(snapshot, target, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken); Assert.False(File.Exists(target));
        Assert.Equal("NOT OUR STAGE", await File.ReadAllTextAsync(unrelated)); Assert.Single(Pending(output.Path));
    }

    [Fact]
    public async Task ConcurrentCreateOnlyAttemptsLeaveExactlyOneValidDestination()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); string target = Path.Combine(output.Path, "thread.zip");
        TransferArchivePublication[] attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            NativeTransferArchivePublisher.PublishAsync(snapshot, target)));
        Assert.Single(attempts, r => r.IsPublished); Assert.Equal(3, attempts.Count(r => !r.IsPublished));
        await using FileStream stream = File.OpenRead(target);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stream)).IsPackageValid);
        Assert.Empty(Pending(output.Path)); Assert.Single(Directory.EnumerateFileSystemEntries(output.Path));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LinkedDestinationOrParentIsRejectedWithoutTouchingOtherFiles(bool linkParent)
    {
        if (OperatingSystem.IsWindows()) return; // Windows link privileges/native gate remains separate.
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = await FreezeAsync(reader, fixture);
        using TempDirectory output = new(); using TempDirectory elsewhere = new();
        string original = Path.Combine(elsewhere.Path, "keep.txt"); await File.WriteAllTextAsync(original, "KEEP");
        string link = Path.Combine(output.Path, "linked"); string target;
        if (linkParent) { Directory.CreateSymbolicLink(link, elsewhere.Path); target = Path.Combine(link, "thread.zip"); }
        else { File.CreateSymbolicLink(link, original); target = link; }
        TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
        Assert.False(result.IsPublished); Assert.Equal(TransferPublicationError.IoFailure, result.Error);
        Assert.Equal("KEEP", await File.ReadAllTextAsync(original)); Assert.Single(Directory.EnumerateFileSystemEntries(elsewhere.Path));
        Assert.Empty(Pending(output.Path));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CleanupLinkRefusalPreservesPrimaryCancellationOrTypedFailure(bool cancel)
    {
        if (OperatingSystem.IsWindows()) return; // Native reparse/privilege test is still separate.
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot frozen = await FreezeAsync(reader, fixture);
        using TempDirectory output = new();
        string parent = Path.Combine(output.Path, "chosen-parent"); Directory.CreateDirectory(parent);
        string relocated = Path.Combine(output.Path, "relocated-parent");
        string target = Path.Combine(parent, "thread.zip");
        string sentinel = Path.Combine(parent, "keep.txt"); await File.WriteAllTextAsync(sentinel, "KEEP");
        using CancellationTokenSource cancelled = new();
        int callbacks = 0;
        // A trusted synthetic fault callback, NOT an imported DTO/capability: relocation is
        // deterministic while the publisher owns its open stage. No sleeps or background race.
        TransferPayloadSource failing = new(frozen.Payloads[0].Descriptor, token =>
        {
            Interlocked.Increment(ref callbacks);
            Directory.Move(parent, relocated);
            Directory.CreateSymbolicLink(parent, relocated);
            if (cancel)
            {
                cancelled.Cancel();
                return Task.FromException<Stream>(new OperationCanceledException(token));
            }
            return Task.FromException<Stream>(new IOException("SECRET synthetic callback failure"));
        });
        NativeTransferArchiveSnapshot snapshot = new(frozen.Reader, frozen.View, frozen.Metadata.ToArray(),
            frozen.Catalog, frozen.Manifests, frozen.Payloads.SetItem(0, failing), frozen.PackageId);
        try
        {
            if (cancel)
            {
                OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => NativeTransferArchivePublisher.PublishAsync(snapshot, target, cancelled.Token));
                Assert.Equal(cancelled.Token, error.CancellationToken); Assert.Null(error.InnerException);
                Assert.DoesNotContain(output.Path, error.Message); Assert.DoesNotContain("SECRET", error.Message);
            }
            else
            {
                TransferArchivePublication result = await NativeTransferArchivePublisher.PublishAsync(snapshot, target);
                Assert.False(result.IsPublished); Assert.Equal(TransferPublicationError.ExportFailed, result.Error);
                Assert.True(result.StageRetained); Assert.Null(result.BytesWritten); Assert.Null(result.PackageFingerprint);
                Assert.DoesNotContain(output.Path, result.ToString()); Assert.DoesNotContain("SECRET", result.ToString());
            }
            Assert.Equal(1, callbacks); Assert.Single(Pending(relocated));
            Assert.False(File.Exists(Path.Combine(relocated, "thread.zip")));
            Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(relocated, "keep.txt")));
            Assert.Equal(fixture.StrictBytes.ToArray(), await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
            Assert.Equal(0, fixture.UnexpectedProviderCalls);
        }
        finally
        {
            // Restore only this test-owned layout. Production must refuse cleanup through the link.
            if (new DirectoryInfo(parent).LinkTarget != null) Directory.Delete(parent);
            if (Directory.Exists(relocated)) Directory.Move(relocated, parent);
        }
    }

    private static string[] Pending(string directory) => Directory.GetFiles(directory, ".meowchan-transfer-*.pending");
    private static async Task<NativeTransferArchiveSnapshot> FreezeAsync(ArchiveReader reader, NativeTransferArchiveFixture fixture)
    {
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString()); return frozen.Snapshot!;
    }
}
