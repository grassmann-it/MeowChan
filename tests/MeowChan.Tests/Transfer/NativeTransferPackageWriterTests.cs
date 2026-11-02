using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferPackageWriterTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic package payload, never user media");
    private static readonly Guid PackageId = Guid.Parse("29106c80-d16d-4d1c-a345-861b6c85681b");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static NativeTransferCatalog Catalog(bool payload = true)
    {
        CapturedAttachment metadata = new(0, "7001001", "original", "https://i.4cdn.org/b/7001001.jpg", "synthetic.jpg");
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
            payload ? TransferContentStatus.Contained : TransferContentStatus.Omitted, payload ? Hash(Original) : null, null);
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null, "synthetic post", false, [metadata]);
        return new(At, [new(new("4chan", "b", "7001"), "https://boards.4chan.org/b/thread/7001", "synthetic", ThreadSourceState.NotFound,
            At, At, 1, true, [new(post, [attachment])])]);
    }
    private static TransferPayloadSource Payload(Func<Stream>? open = null) => new(new(Original.Length, Hash(Original)),
        _ => Task.FromResult(open?.Invoke() ?? new MemoryStream(Original, false)));
    [Fact]
    public async Task WritesDeterministicStoredPackageAndChecksSameValidatorBeforeSuccess()
    {
        using MemoryStream first = new(); using MemoryStream second = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(first, Catalog(), [Payload()], PackageId);
        Assert.True(result.IsComplete); Assert.Equal(first.Length, result.BytesWritten); Assert.Equal(0, first.Position);
        Assert.True(first.CanWrite); Assert.True(first.CanRead);
        Assert.Equal(ThreadSourceState.NotFound, result.Package!.Catalog.Threads[0].SourceState);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(first)).IsPackageValid);
        Assert.True((await NativeTransferPackageWriter.WriteAsync(second, Catalog(), [Payload()], PackageId)).IsComplete);
        Assert.Equal(first.ToArray(), second.ToArray());
    }
    [Fact]
    public async Task MetadataOnlyPackageKeepsOmittedDistinctFromUnknown()
    {
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(false), [], PackageId);
        Assert.True(result.IsComplete);
        Assert.Equal(TransferContentStatus.Omitted, result.Package!.Catalog.Threads[0].Posts[0].Attachments[0].Status);
        Assert.Empty(result.Package.Envelope.Payloads);
    }
    [Fact]
    public async Task ExistingOutputIsNotOverwrittenOrDisposed()
    {
        using MemoryStream stage = new(); stage.WriteByte(7);
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload()], PackageId);
        Assert.Equal(TransferWriteError.OutputNotEmpty, result.Error); Assert.False(result.IsComplete);
        Assert.Equal(new byte[] { 7 }, stage.ToArray()); Assert.True(stage.CanWrite);
    }
    [Theory]
    [InlineData(-1)] [InlineData(1)]
    public async Task SourceLengthChangesLeaveOnlyAFailedUnpublishedStage(int delta)
    {
        using MemoryStream stage = new();
        TrackingMemoryStream input = new(new byte[Original.Length + delta]);
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(() => input)], PackageId);
        Assert.Equal(TransferWriteError.SourceLengthMismatch, result.Error); Assert.Null(result.Package);
        Assert.Null(result.BytesWritten); Assert.True(input.Disposed); Assert.True(stage.CanWrite);
        Assert.True(stage.Length > 0); // explicitly not advertised as empty/atomic output
    }
    [Fact]
    public async Task ChangedSourceBytesFailAndAreDisposed()
    {
        using MemoryStream stage = new(); TrackingMemoryStream input = new(new byte[Original.Length]);
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(() => input)], PackageId);
        Assert.Equal(TransferWriteError.SourceHashMismatch, result.Error); Assert.Null(result.Package); Assert.True(input.Disposed);
    }
    [Fact]
    public async Task InvalidCatalogCannotBecomeSuccessfulJustBecauseZipWasWritten()
    {
        using MemoryStream stage = new(); NativeTransferCatalog catalog = Catalog();
        TransferThread thread = catalog.Threads[0] with { PageUrl = "https://SECRET@boards.4chan.org/b/thread/7001" };
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, catalog with { Threads = [thread] }, [Payload()], PackageId);
        Assert.Equal(TransferWriteError.ValidationFailed, result.Error); Assert.Null(result.Package);
        Assert.DoesNotContain("SECRET", result.ToString()); Assert.True(stage.CanWrite);
    }
    [Fact]
    public async Task DuplicateOrNoncanonicalSelectionIsRejectedBeforeOutput()
    {
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(), Payload()], PackageId);
        Assert.Equal(TransferWriteError.InvalidSelection, result.Error); Assert.Equal(0, stage.Length);
        TransferPayloadSource bad = Payload() with { Descriptor = new(1, "../SECRET") };
        result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [bad], PackageId);
        Assert.Equal(TransferWriteError.InvalidSelection, result.Error); Assert.DoesNotContain("SECRET", result.ToString());
    }
    [Fact]
    public async Task SourceIoFailureDoesNotReflectExceptionPathOrDisposeOutput()
    {
        using MemoryStream stage = new();
        TransferPayloadSource failure = Payload() with { OpenRead = _ => throw new IOException("/SECRET/private/path") };
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [failure], PackageId);
        Assert.Equal(TransferWriteError.IoFailure, result.Error); Assert.Null(result.Package);
        Assert.DoesNotContain("SECRET", result.ToString()); Assert.True(stage.CanWrite);
    }
    [Fact]
    public async Task CancellationNeverPublishesSuccessAndKeepsCallerOutputOwned()
    {
        using MemoryStream stage = new(); using CancellationTokenSource cancel = new();
        TransferPayloadSource delayed = Payload() with { OpenRead = ct => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new MemoryStream()); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [delayed], PackageId, cancel.Token));
        Assert.True(stage.CanWrite); Assert.True(stage.Length > 0);
    }

    [Fact]
    public async Task ObservedReadCancellationSurvivesOwnedInputCleanupFailure()
    {
        using CancellationTokenSource cancel = new(); using MemoryStream stage = new();
        CancelReadStream input = new(cancel, cleanupFailure: true);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(() => input)], PackageId, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.True(input.CleanupAttempted);
        Assert.True(stage.CanWrite); Assert.DoesNotContain("SECRET", error.Message);
    }
    [Fact]
    public async Task ObservedReadCancellationSurvivesZipFinalizationFailure()
    {
        using CancellationTokenSource cancel = new(); using CancellationFaultOutput stage = new(cancel.Token);
        CancelReadStream input = new(cancel, cleanupFailure: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(() => input)], PackageId, cancel.Token));
        Assert.True(input.CleanupAttempted); Assert.True(stage.Faulted); Assert.True(stage.CanWrite);
    }
    [Fact]
    public async Task AccessDeniedCannotEscapeWithPrivatePath()
    {
        using MemoryStream stage = new();
        TransferPayloadSource failure = Payload() with { OpenRead = _ => throw new UnauthorizedAccessException("Access denied /SECRET/private/original.jpg") };
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [failure], PackageId);
        Assert.Equal(TransferWriteError.IoFailure, result.Error); Assert.Null(result.Package); Assert.DoesNotContain("SECRET", result.ToString());
    }

    [Fact]
    public async Task ValidatorCancellationCannotBeMaskedByAccessDeniedPositionRestoration()
    {
        using CancellationTokenSource cancel = new(); using ValidationFaultStage stage = new(cancel);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload()], PackageId, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.True(stage.CanWrite); Assert.True(stage.RestoreAttempted);
        Assert.DoesNotContain("SECRET", error.Message);
    }
    private sealed class ValidationFaultStage(CancellationTokenSource cancel) : MemoryStream
    {
        private bool validation;
        internal bool RestoreAttempted { get; private set; }
        public override long Position
        {
            get => base.Position;
            set
            {
                if (validation) { RestoreAttempted = true; throw new UnauthorizedAccessException("/SECRET/restore"); }
                base.Position = value;
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { validation = true; cancel.Cancel(); return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken)); }
    }
    private sealed class CancelReadStream(CancellationTokenSource cancel, bool cleanupFailure) : MemoryStream
    {
        internal bool CleanupAttempted { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancel.Cancel(); return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken)); }
        public override ValueTask DisposeAsync()
        {
            CleanupAttempted = true;
            return cleanupFailure ? ValueTask.FromException(new IOException("/SECRET/cleanup")) : base.DisposeAsync();
        }
    }
    private sealed class CancellationFaultOutput(CancellationToken token) : MemoryStream
    {
        internal bool Faulted { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) { Check(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(); base.Write(buffer); }
        private void Check() { if (token.IsCancellationRequested) { Faulted = true; throw new IOException("/SECRET/finalization"); } }
    }
    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
