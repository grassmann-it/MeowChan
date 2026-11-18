using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;
using MeowChan.Testing;

namespace MeowChan.Tests.Transfer;

// Deliberately independent of the real-writer happy-path suite. All paths and bytes are synthetic.
public sealed class NativeTransferImportStageAdversarialTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly byte[] Payload = Enumerable.Range(0, 192 * 1024 + 37)
        .Select(i => (byte)(i * 31 + 7)).ToArray();
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContainerCapCountsActualNonseekableReadsAndStopsBeforeEof()
    {
        using Workspace workspace = new();
        using NonseekableSource source = new(new byte[64 * 1024], chunkSize: 17);

        var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging,
            limits: new() { ContainerBytes = 97 });

        AssertFailure(result, NativeTransferStageError.ContainerLimit);
        Assert.Equal(98, source.BytesRead); // Only the single overflow-detection byte is consumed.
        Assert.True(source.BytesRead < 64 * 1024); // Not an unbounded spool followed by validation.
        Assert.Equal(0, source.MetadataReads);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Fact]
    public async Task ExactContainerCapAcceptsNonseekableBytesWithoutConsultingLength()
    {
        using Workspace workspace = new();
        byte[] bytes = Package();
        using NonseekableSource source = new(bytes, chunkSize: 997);
        var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging,
            limits: new() { ContainerBytes = bytes.Length });
        Assert.Equal(NativeTransferStageError.None, result.Error);
        var stage = Assert.IsType<NativeTransferImportStage>(result.Stage);
        await using (stage)
        {
            Assert.False(result.StageRetained);
            Assert.Equal(bytes.Length, source.BytesRead);
            Assert.Equal(0, source.MetadataReads);
            Assert.False(source.WasDisposed);
            using MemoryStream destination = new();
            AssertMeasured(await stage.CopyPayloadToAsync(Hash(Payload), destination));
            Assert.Equal(Payload, destination.ToArray());
        }
        Assert.False(stage.StageRetained);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("local-header")]
    [InlineData("catalog-hash")]
    [InlineData("payload-hash")]
    [InlineData("catalog-json")]
    [InlineData("catalog-semantic")]
    public async Task InvalidFramingHashesAndCatalogsNeverIssueAStage(string fault)
    {
        using Workspace workspace = new();
        byte[] bytes = Package(fault);
        // Establish the intended validation layer rather than letting a broken fixture pass the test.
        using (MemoryStream control = new(bytes, writable: false))
        {
            var expected = await NativeTransferPackageValidator.InspectAsync(control);
            Assert.False(expected.IsPackageValid);
            if (fault == "catalog-json")
            {
                Assert.Equal(TransferEnvelopeError.None, expected.EnvelopeError);
                Assert.Equal(TransferCatalogError.InvalidJson, expected.CatalogError);
            }
            else if (fault == "catalog-semantic")
            {
                Assert.Equal(TransferEnvelopeError.None, expected.EnvelopeError);
                Assert.Equal(TransferCatalogError.UnsafeUrl, expected.CatalogError);
            }
            else if (fault is "catalog-hash" or "payload-hash")
                Assert.Equal(TransferEnvelopeError.HashMismatch, expected.EnvelopeError);
            else Assert.NotEqual(TransferEnvelopeError.None, expected.EnvelopeError);

            using NonseekableSource source = new(bytes, chunkSize: 4093);
            var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging);
            AssertFailure(result, NativeTransferStageError.InvalidPackage);
            Assert.Equal(expected.EnvelopeError, result.EnvelopeError);
            Assert.Equal(expected.CatalogError, result.CatalogError);
            Assert.False(source.WasDisposed);
        }
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("catalog")]
    [InlineData("total")]
    public async Task LowerValidatorBudgetsArePassedThroughInsteadOfSilentlyUsingDefaults(string cap)
    {
        using Workspace workspace = new();
        using MemoryStream source = new(Package(), writable: false);
        TransferEnvelopeLimits limits = cap switch
        {
            "payload" => new() { PayloadBytes = Payload.Length - 1 },
            "catalog" => new() { CatalogBytes = 1 },
            _ => new() { TotalBytes = 1 }
        };

        var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging, limits: limits);

        AssertFailure(result, NativeTransferStageError.InvalidPackage);
        Assert.Equal(TransferEnvelopeError.PayloadLimit, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, result.CatalogError);
        Assert.True(source.CanRead);
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData("container")]
    [InlineData("payload")]
    [InlineData("entries")]
    [InlineData("zero")]
    public async Task InvalidOrRaisedSafetyCapsAreRejectedBeforeSourceConsumption(string cap)
    {
        using Workspace workspace = new();
        using NonseekableSource source = new(Package());
        TransferEnvelopeLimits limits = cap switch
        {
            "container" => new() { ContainerBytes = 2L * 1024 * 1024 * 1024 + 1 },
            "payload" => new() { PayloadBytes = 1024L * 1024 * 1024 + 1 },
            "entries" => new() { Entries = 10_001 },
            _ => new() { ContainerBytes = 0 }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await NativeTransferImportStage.CreateAsync(source, workspace.Staging, limits: limits));

        Assert.Equal(0, source.BytesRead);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nul")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("relative-existing")]
    public async Task InvalidStagingDirectoryIsRejectedWithoutReadingOrCreatingDirectories(string kind)
    {
        using Workspace workspace = new();
        string missing = Path.Combine(workspace.Root, "not-created");
        string path = kind switch
        {
            "empty" => "",
            "nul" => "\0",
            "missing" => missing,
            "file" => workspace.Sentinel,
            // The relative path resolves to a real directory, so existence alone cannot pass it.
            // GetRelativePath returns a rooted path across Windows volumes (runner temp is
            // commonly C: while the checkout is D:), so use the current directory in that case.
            _ => Path.GetRelativePath(Environment.CurrentDirectory, workspace.Staging) is string relative
                && !Path.IsPathFullyQualified(relative) ? relative : "."
        };
        using NonseekableSource source = new(Package());

        var result = await NativeTransferImportStage.CreateAsync(source, path);

        AssertFailure(result, NativeTransferStageError.InvalidStagingDirectory);
        Assert.Equal(0, source.BytesRead);
        Assert.False(source.WasDisposed);
        Assert.False(Directory.Exists(missing));
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymlinkAtDirectoryOrInItsParentChainIsRejected(bool parentLink)
    {
        using Workspace workspace = new();
        string target = Path.Combine(workspace.Root, "link-target");
        Directory.CreateDirectory(target);
        string child = Path.Combine(target, "child");
        Directory.CreateDirectory(child);
        string sentinel = Path.Combine(child, "caller.keep");
        File.WriteAllText(sentinel, "synthetic link target sentinel");
        string link = Path.Combine(workspace.Root, "alias");
        // Fail visibly on runners lacking symlink privileges; do not silently turn this into a pass.
        Directory.CreateSymbolicLink(link, parentLink ? target : child);
        try
        {
            using NonseekableSource source = new(Package());
            var result = await NativeTransferImportStage.CreateAsync(source,
                parentLink ? Path.Combine(link, "child") : link);

            AssertFailure(result, NativeTransferStageError.InvalidStagingDirectory);
            Assert.Equal(0, source.BytesRead);
            Assert.False(source.WasDisposed);
            Assert.Equal("synthetic link target sentinel", File.ReadAllText(sentinel));
            Assert.Equal(new[] { sentinel }, Directory.GetFiles(child));
            workspace.AssertOnlyCallerFiles();
        }
        finally
        {
            // Unlink explicitly before the test workspace is cleaned. Never recurse through a link.
            Directory.Delete(link);
            File.Delete(sentinel);
            Directory.Delete(child);
            Directory.Delete(target);
        }
    }

    [Fact]
    public async Task CancellationDuringSourceCopyPropagatesAndRemovesOnlyItsOwnPendingFile()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancel = new();
        using InterruptedSource source = new(Package(), failWithIo: false);
        var creating = NativeTransferImportStage.CreateAsync(source, workspace.Staging, cancel.Token);
        try
        {
            await source.Interrupted.Task.WaitAsync(Watchdog);
            Assert.True(source.BytesRead > 0);
            Assert.False(creating.IsCompleted);
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await creating.WaitAsync(Watchdog));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.False(source.WasDisposed);
            workspace.AssertOnlyCallerFiles();
        }
        finally
        {
            cancel.Cancel();
            await Observe(creating);
        }
    }

    [NonWindowsDirectoryRenameFact]
    public async Task CancelAfterParentRedirectReportsRetainedStageWithoutFollowingLinkOrLeakingPaths()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancel = new();
        using InterruptedSource source = new(Package(), failWithIo: false);
        var creating = NativeTransferImportStage.CreateAsync(source, workspace.Staging, cancel.Token);
        ParentRedirect? redirect = null;
        try
        {
            await source.Interrupted.Task.WaitAsync(Watchdog);
            Assert.True(source.BytesRead > 0);
            Assert.False(creating.IsCompleted);
            string own = Assert.Single(Directory.GetFiles(workspace.Staging), path => path != workspace.Sentinel);
            redirect = new ParentRedirect(workspace, Path.GetFileName(own));

            cancel.Cancel();
            NativeTransferStageCanceledException error = await Assert.ThrowsAsync<NativeTransferStageCanceledException>(
                async () => await creating.WaitAsync(Watchdog));

            Assert.True(error.StageRetained);
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(workspace.Root, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", error.ToString(), StringComparison.Ordinal);
            Assert.False(source.WasDisposed);
            redirect.AssertRetainedAndDecoyUntouched();
        }
        finally
        {
            cancel.Cancel();
            try
            {
                await Observe(creating);
                if (creating.IsCompletedSuccessfully && (await creating).Stage is { } unexpectedStage)
                    await unexpectedStage.DisposeAsync();
            }
            finally { redirect?.Dispose(); }
        }
        workspace.AssertOnlyCallerFiles();
    }

    [NonWindowsDirectoryRenameFact]
    public async Task DisposeAfterParentRedirectReportsRetentionAndNeverDeletesSameNamedLinkTargetFile()
    {
        using Workspace workspace = new();
        var stage = await CreateStage(workspace);
        ParentRedirect? redirect = null;
        try
        {
            string own = Assert.Single(Directory.GetFiles(workspace.Staging), path => path != workspace.Sentinel);
            redirect = new ParentRedirect(workspace, Path.GetFileName(own));

            await stage.DisposeAsync();

            Assert.True(stage.StageRetained);
            redirect.AssertRetainedAndDecoyUntouched();
            using MemoryStream destination = new();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await stage.CopyPayloadToAsync(Hash(Payload), destination));
            Assert.Equal(0, destination.Length);
            await stage.DisposeAsync();
            Assert.True(stage.StageRetained);
            redirect.AssertRetainedAndDecoyUntouched();
        }
        finally
        {
            try { await stage.DisposeAsync(); }
            finally { redirect?.Dispose(); }
        }
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData(0)] // EOF still must observe cancellation raised by the read.
    [InlineData(17)]
    [InlineData(256)] // Cancellation must win over the normal container-limit result.
    public async Task SourceThatIgnoresTokenCannotHideCancellationAfterReturningBytesOrEof(int byteCount)
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancel = new();
        using CancelAfterReadSource source = new(new byte[byteCount], cancel);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NativeTransferImportStage.CreateAsync(source, workspace.Staging, cancel.Token,
                new() { ContainerBytes = 97 }));

        Assert.True(source.ReadReturned);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Fact]
    public async Task PrecancelledCreateNeverConsumesOrOwnsCallerSource()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        using NonseekableSource source = new(Package());

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NativeTransferImportStage.CreateAsync(source, workspace.Staging, cancel.Token));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(0, source.BytesRead);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Fact]
    public async Task MidSourceIoFailureReturnsNoStageAndDoesNotDeleteCallerFiles()
    {
        using Workspace workspace = new();
        using InterruptedSource source = new(Package(), failWithIo: true);

        var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging);

        AssertFailure(result, NativeTransferStageError.IoFailure);
        Assert.True(source.BytesRead > 0);
        Assert.False(source.WasDisposed);
        workspace.AssertOnlyCallerFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrPathShapedHashCannotWriteDestinationAndDoesNotPoisonStage(bool pathShaped)
    {
        using Workspace workspace = new();
        await using var stage = await CreateStage(workspace);
        using MemoryStream destination = new();
        destination.WriteByte(123);

        Exception? error = await Record.ExceptionAsync(async () =>
            await stage.CopyPayloadToAsync(pathShaped ? "../SECRET/caller.keep" : new string('0', 64), destination));

        // The contract promises rejection, not a particular argument-exception subtype.
        Assert.NotNull(error);
        Assert.False(error is OperationCanceledException);
        Assert.DoesNotContain("SECRET", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(new byte[] { 123 }, destination.ToArray());
        Assert.True(destination.CanWrite);
        using MemoryStream recovery = new();
        AssertMeasured(await stage.CopyPayloadToAsync(Hash(Payload), recovery));
        Assert.Equal(Payload, recovery.ToArray());
    }

    [Fact]
    public async Task CancellationInsidePayloadWriteLeavesPartialCallerOutputButAllowsFullRecoveryCopy()
    {
        using Workspace workspace = new();
        await using var stage = await CreateStage(workspace);
        using CancellationTokenSource cancel = new();
        using GatedDestination destination = new();
        Task<TransferMeasuredBlob> copying = Copy(stage, destination, cancel.Token);
        try
        {
            await destination.Entered.Task.WaitAsync(Watchdog);
            Assert.InRange(destination.Bytes.Length, 1, Payload.Length - 1);
            Assert.False(copying.IsCompleted);
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await copying.WaitAsync(Watchdog));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.False(destination.WasDisposed);
            Assert.InRange(destination.Bytes.Length, 1, Payload.Length - 1);
            Assert.False(stage.StageRetained);

            using MemoryStream recovery = new();
            AssertMeasured(await stage.CopyPayloadToAsync(Hash(Payload), recovery));
            Assert.Equal(Payload, recovery.ToArray());
        }
        finally
        {
            cancel.Cancel();
            destination.Release.TrySetResult();
            await Observe(copying);
        }
    }

    [Fact]
    public async Task DestinationIoFailureLeavesCallerOwnedPartialBytesAndDoesNotPoisonStage()
    {
        using Workspace workspace = new();
        await using var stage = await CreateStage(workspace);
        using FaultingDestination destination = new();

        await Assert.ThrowsAsync<IOException>(async () =>
            await stage.CopyPayloadToAsync(Hash(Payload), destination));

        Assert.Equal(Payload[..7], destination.ToArray());
        Assert.True(destination.CanWrite);
        Assert.False(stage.StageRetained);
        using MemoryStream recovery = new();
        AssertMeasured(await stage.CopyPayloadToAsync(Hash(Payload), recovery));
        Assert.Equal(Payload, recovery.ToArray());
    }

    [Theory]
    [InlineData(false)] // Same-length payload corruption, with intact ZIP framing.
    [InlineData(true)] // A short payload must fail before returning any measured success.
    public async Task CopyRemeasuresActualStageBytesInsteadOfTrustingPassivePackageView(bool truncate)
    {
        using Workspace workspace = new();
        var stage = await CreateStage(workspace);
        try
        {
            TransferMeasuredBlob before = stage.Package.Envelope.MeasuredPayloads[Hash(Payload)];
            AssertMeasured(before);
            // Test-only fault injection: no concurrent copy/dispose is running, so the gate is
            // quiescent. Do not add a product hook or reopen the exclusive file through the OS.
            FileStream file = Assert.IsType<FileStream>(typeof(NativeTransferImportStage)
                .GetField("_file", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(stage));
            var entries = await NativeTransferZip.ReadDirectoryAsync(file, new TransferEnvelopeLimits(), CancellationToken.None);
            TransferZipEntry payload = Assert.Single(entries, entry => entry.Name == "payload/" + Hash(Payload));
            Assert.Equal(Payload.Length, payload.Length);
            if (truncate)
                file.SetLength(payload.DataOffset + 19);
            else
            {
                file.Position = payload.DataOffset + 19;
                int original = file.ReadByte();
                Assert.NotEqual(-1, original);
                file.Position = payload.DataOffset + 19;
                file.WriteByte((byte)(original ^ 1));
            }
            await file.FlushAsync();
            file.Flush(flushToDisk: true);
            // The immutable passive projection is unchanged; it is not byte authority.
            Assert.Equal(before, stage.Package.Envelope.MeasuredPayloads[Hash(Payload)]);
            using MemoryStream destination = new();

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await stage.CopyPayloadToAsync(Hash(Payload), destination));

            Assert.True(destination.CanWrite);
            if (truncate)
            {
                Assert.Equal(19, destination.Length);
                Assert.Equal(Payload[..19], destination.ToArray());
            }
            else
            {
                Assert.Equal(Payload.Length, destination.Length);
                byte[] expectedCorruption = (byte[])Payload.Clone();
                expectedCorruption[19] ^= 1;
                Assert.Equal(expectedCorruption, destination.ToArray());
            }
            // Explicitly discard all failed-copy bytes, including a fully written corrupt copy.
            destination.SetLength(0);
            Assert.Equal(0, destination.Length);
        }
        finally { await stage.DisposeAsync(); }
        Assert.False(stage.StageRetained);
        workspace.AssertOnlyCallerFiles();
    }

    [Fact]
    public async Task DisposeDrainsActiveCopyButRejectsAlreadyQueuedAndNewCopies()
    {
        using Workspace workspace = new();
        var stage = await CreateStage(workspace);
        using GatedDestination activeDestination = new();
        using MemoryStream queuedDestination = new();
        using MemoryStream newDestination = new();
        Task<TransferMeasuredBlob> active = Copy(stage, activeDestination);
        Task<TransferMeasuredBlob>? queued = null, afterDisposeIntent = null;
        Task? disposing = null;
        try
        {
            await activeDestination.Entered.Task.WaitAsync(Watchdog);
            // Copy() enters the real method synchronously through its first await: it is queued,
            // not a Task.Run operation whose start could lose a scheduler race with Dispose.
            queued = Copy(stage, queuedDestination);
            Assert.False(queued.IsCompleted);
            disposing = stage.DisposeAsync().AsTask();
            afterDisposeIntent = Copy(stage, newDestination);
            Assert.True(afterDisposeIntent.IsFaulted); // New calls reject before waiting on the held gate.
            Assert.False(active.IsCompleted);
            Assert.False(disposing.IsCompleted); // A signal-held destination, never a timing sleep.
            Assert.Equal(0, queuedDestination.Length);
            Assert.Equal(0, newDestination.Length);

            activeDestination.Release.TrySetResult();
            AssertMeasured(await active.WaitAsync(Watchdog));
            Assert.Equal(Payload, activeDestination.Bytes);
            await disposing.WaitAsync(Watchdog);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queued.WaitAsync(Watchdog));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await afterDisposeIntent.WaitAsync(Watchdog));
            Assert.Equal(0, queuedDestination.Length);
            Assert.Equal(0, newDestination.Length);
            Assert.False(activeDestination.WasDisposed);
            Assert.True(queuedDestination.CanWrite);
            Assert.True(newDestination.CanWrite);
            Assert.False(stage.StageRetained);
            workspace.AssertOnlyCallerFiles();

            await stage.DisposeAsync(); // Idempotent, without deleting the caller's directory.
            using MemoryStream closedDestination = new();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await stage.CopyPayloadToAsync(Hash(Payload), closedDestination));
            Assert.Equal(0, closedDestination.Length);
            workspace.AssertOnlyCallerFiles();
        }
        finally
        {
            activeDestination.Release.TrySetResult();
            await Observe(active);
            if (queued is not null) await Observe(queued);
            if (afterDisposeIntent is not null) await Observe(afterDisposeIntent);
            await (disposing ?? stage.DisposeAsync().AsTask()).WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task CancellingAQueuedCopyDoesNotInterruptActiveCopyOrPreventLaterCopy()
    {
        using Workspace workspace = new();
        await using var stage = await CreateStage(workspace);
        using GatedDestination destination = new();
        using MemoryStream queuedDestination = new();
        using CancellationTokenSource cancel = new();
        Task<TransferMeasuredBlob> active = Copy(stage, destination);
        Task<TransferMeasuredBlob>? queued = null;
        try
        {
            await destination.Entered.Task.WaitAsync(Watchdog);
            queued = Copy(stage, queuedDestination, cancel.Token);
            Assert.False(queued.IsCompleted);
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await queued.WaitAsync(Watchdog));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.Equal(0, queuedDestination.Length);
            Assert.False(active.IsCompleted);

            destination.Release.TrySetResult();
            AssertMeasured(await active.WaitAsync(Watchdog));
            Assert.Equal(Payload, destination.Bytes);
            using MemoryStream recovery = new();
            AssertMeasured(await stage.CopyPayloadToAsync(Hash(Payload), recovery));
            Assert.Equal(Payload, recovery.ToArray());
        }
        finally
        {
            cancel.Cancel();
            destination.Release.TrySetResult();
            await Observe(active);
            if (queued is not null) await Observe(queued);
        }
    }

    [Fact]
    public async Task DisposeDeletesExactlyItsPendingFileAndNeverRecursivelyCleansCallerDirectory()
    {
        using Workspace workspace = new();
        var stage = await CreateStage(workspace);
        try
        {
            string[] own = Directory.GetFiles(workspace.Staging)
                .Where(p => p != workspace.Sentinel).ToArray();
            Assert.Single(own);
            // Plant a caller-owned file AFTER creation with the same naming shape, so a glob
            // cleanup cannot accidentally pass this test. It is not owned by the stage.
            string added = Path.Combine(workspace.Staging, ".meowchan-import-" + Guid.NewGuid().ToString("N") + ".pending");
            string nested = Path.Combine(workspace.Nested, "late.keep");
            File.WriteAllText(added, "synthetic late sibling");
            File.WriteAllText(nested, "synthetic late descendant");

            await stage.DisposeAsync();

            Assert.False(File.Exists(own[0]));
            Assert.False(stage.StageRetained);
            Assert.True(Directory.Exists(workspace.Staging));
            Assert.Equal("synthetic late sibling", File.ReadAllText(added));
            Assert.Equal("synthetic late descendant", File.ReadAllText(nested));
            File.Delete(added);
            File.Delete(nested);
            workspace.AssertOnlyCallerFiles();
            await stage.DisposeAsync();
            workspace.AssertOnlyCallerFiles();
        }
        finally { await stage.DisposeAsync(); }
    }

    private static void AssertFailure(NativeTransferStageResult result, NativeTransferStageError error)
    {
        Assert.Equal(error, result.Error);
        Assert.Null(result.Stage);
        Assert.False(result.StageRetained);
        Assert.DoesNotContain("SECRET", result.ToString(), StringComparison.Ordinal);
    }

    private static async Task<NativeTransferImportStage> CreateStage(Workspace workspace)
    {
        using MemoryStream source = new(Package(), writable: false);
        var result = await NativeTransferImportStage.CreateAsync(source, workspace.Staging);
        Assert.Equal(NativeTransferStageError.None, result.Error);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, result.CatalogError);
        Assert.False(result.StageRetained);
        return Assert.IsType<NativeTransferImportStage>(result.Stage);
    }

    private static async Task<TransferMeasuredBlob> Copy(NativeTransferImportStage stage,
        Stream destination, CancellationToken ct = default) => await stage.CopyPayloadToAsync(Hash(Payload), destination, ct);

    private static void AssertMeasured(TransferMeasuredBlob measured)
    {
        Assert.Equal(Payload.Length, measured.Length);
        Assert.Equal(Hash(Payload), measured.Sha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Payload)), measured.Md5);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(Payload)), measured.Sha1);
    }

    private static async Task Observe(Task task)
    {
        // Observe faults in teardown without replacing the test's original assertion failure.
        try { await task.WaitAsync(Watchdog); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static JsonObject Descriptor(byte[] value) => new() { ["length"] = value.Length, ["sha256"] = Hash(value) };

    private static byte[] Package(string? fault = null)
    {
        CapturedAttachment metadata = new(0, "7001001", "original", "https://i.4cdn.org/b/7001001.jpg", "synthetic.jpg");
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(Payload), null);
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null, "synthetic post", false, [metadata]);
        NativeTransferCatalog projection = new(At, [new(new("4chan", "b", "7001"),
            fault == "catalog-semantic" ? "https://SECRET@boards.4chan.org/b/thread/7001" : "https://boards.4chan.org/b/thread/7001",
            "synthetic", ThreadSourceState.NotFound, At, At, 1, true, [new(post, [attachment])])]);
        byte[] catalog = fault == "catalog-json" ? Encoding.UTF8.GetBytes("not JSON")
            : NativeTransferCatalogSerializer.Serialize(projection);
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1,
            ["packageId"] = "ab025af3-5036-4b76-9b7f-a406d9cb21f2",
            ["contentSchema"] = "chan-v1", ["requiredFeatures"] = new JsonArray("chan-v1"),
            ["catalog"] = Descriptor(catalog), ["payloads"] = new JsonArray(Descriptor(Payload))
        };
        // Keep ZIP CRC and framing consistent: only the manifest's digest binding is wrong.
        if (fault == "catalog-hash") manifest["catalog"]!["sha256"] = new string('0', 64);
        byte[] contents = Payload;
        if (fault == "payload-hash") { contents = (byte[])Payload.Clone(); contents[19] ^= 1; }
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add("catalog.json", catalog);
            Add("payload/" + Hash(Payload), contents);
            void Add(string name, byte[] data)
            {
                using Stream target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                target.Write(data);
            }
        }
        byte[] bytes = output.ToArray();
        if (fault == "truncated") return bytes[..^11];
        if (fault == "local-header") bytes[14] ^= 1; // Contradictory local/central CRC.
        return bytes;
    }

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-stage-adversarial-");
        internal string Staging { get; }
        internal string Sentinel { get; }
        internal string Nested { get; }
        private string NestedSentinel { get; }
        internal Workspace()
        {
            Staging = Path.Combine(Root, "staging");
            Nested = Path.Combine(Staging, "caller-subdirectory");
            Directory.CreateDirectory(Nested);
            Sentinel = Path.Combine(Staging, "caller.keep");
            NestedSentinel = Path.Combine(Nested, "nested.keep");
            File.WriteAllText(Sentinel, "synthetic caller sentinel");
            File.WriteAllText(NestedSentinel, "synthetic nested sentinel");
        }
        internal void AssertOnlyCallerFiles()
        {
            Assert.True(Directory.Exists(Staging));
            Assert.Equal("synthetic caller sentinel", File.ReadAllText(Sentinel));
            Assert.Equal("synthetic nested sentinel", File.ReadAllText(NestedSentinel));
            Assert.Equal(new[] { Sentinel }, Directory.GetFiles(Staging));
            Assert.Equal(new[] { Nested }, Directory.GetDirectories(Staging));
            Assert.Equal(new[] { NestedSentinel }, Directory.GetFiles(Nested));
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); // Only this GUID-owned fixture.
        }
    }

    // Fault injection is limited to this fixture's own GUID tree. The redirect points at a
    // DIFFERENT directory with a same-named decoy: following the link would delete caller data.
    private sealed class ParentRedirect : IDisposable
    {
        private const string DecoyContents = "SECRET synthetic caller-owned replacement bytes";
        private readonly Workspace workspace;
        private readonly string movedParent;
        private readonly string replacement;
        private readonly string retainedStage;
        private readonly string decoy;
        private bool moved, linked;
        internal ParentRedirect(Workspace workspace, string stageName)
        {
            this.workspace = workspace;
            movedParent = Path.Combine(workspace.Root, "renamed-staging");
            replacement = Path.Combine(workspace.Root, "replacement-parent");
            retainedStage = Path.Combine(movedParent, stageName);
            decoy = Path.Combine(replacement, stageName);
            Directory.CreateDirectory(replacement);
            File.WriteAllText(decoy, DecoyContents);
            try
            {
                Directory.Move(workspace.Staging, movedParent);
                moved = true;
                Directory.CreateSymbolicLink(workspace.Staging, replacement);
                linked = true;
            }
            catch
            {
                // Restore fixture topology even if the runner cannot rename an open-file parent
                // or lacks symlink privileges. Such a runner fails visibly rather than passing.
                if (moved) Directory.Move(movedParent, workspace.Staging);
                File.Delete(decoy);
                Directory.Delete(replacement);
                throw;
            }
        }
        internal void AssertRetainedAndDecoyUntouched()
        {
            Assert.True(File.Exists(retainedStage));
            Assert.Equal(DecoyContents, File.ReadAllText(decoy));
            Assert.Equal(new[] { decoy }, Directory.GetFiles(replacement));
            Assert.Equal("synthetic caller sentinel", File.ReadAllText(Path.Combine(movedParent, "caller.keep")));
            Assert.Equal("synthetic nested sentinel", File.ReadAllText(Path.Combine(movedParent, "caller-subdirectory", "nested.keep")));
        }
        public void Dispose()
        {
            // Never hand the link to recursive deletion. All product operations have drained
            // before this helper explicitly deletes the retained fixture file at its REAL path.
            if (linked) { Directory.Delete(workspace.Staging); linked = false; }
            if (moved)
            {
                File.Delete(retainedStage);
                Directory.Move(movedParent, workspace.Staging);
                moved = false;
            }
            File.Delete(decoy);
            Directory.Delete(replacement);
        }
    }

    private class NonseekableSource(byte[] bytes, int chunkSize = 4093) : Stream
    {
        private int offset;
        internal long BytesRead { get; private set; }
        internal int MetadataReads { get; private set; }
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { MetadataReads++; throw new NotSupportedException(); } }
        public override long Position
        {
            get { MetadataReads++; throw new NotSupportedException(); }
            set { MetadataReads++; throw new NotSupportedException(); }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { MetadataReads++; throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class CancelAfterReadSource(byte[] bytes, CancellationTokenSource cancel) : NonseekableSource(bytes)
    {
        internal bool ReadReturned { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, CancellationToken.None);
            cancel.Cancel();
            ReadReturned = true;
            return count; // Deliberately ignore cancellation instead of throwing it in the source.
        }
    }

    private sealed class FaultingDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            base.Write(buffer.Span[..Math.Min(7, buffer.Length)]);
            return ValueTask.FromException(new IOException("/SECRET/synthetic/destination-write"));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class InterruptedSource(byte[] bytes, bool failWithIo) : NonseekableSource(bytes, chunkSize: 17)
    {
        internal TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BytesRead == 0) return await base.ReadAsync(buffer, cancellationToken);
            Interrupted.TrySetResult();
            if (failWithIo) throw new IOException("/SECRET/synthetic/source-read");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-only wait must not finish normally.");
        }
    }

    private sealed class GatedDestination : Stream
    {
        private readonly MemoryStream inner = new();
        private bool firstWrite = true;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Bytes => inner.ToArray();
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !WasDisposed;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (firstWrite && buffer.Length > 0)
            {
                firstWrite = false;
                int prefix = Math.Min(19, buffer.Length);
                await inner.WriteAsync(buffer[..prefix], cancellationToken);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                buffer = buffer[prefix..];
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
