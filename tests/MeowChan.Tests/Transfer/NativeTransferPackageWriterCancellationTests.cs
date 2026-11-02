using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferPackageWriterCancellationTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic package payload, never user media");
    private static readonly Guid PackageId = Guid.Parse("29106c80-d16d-4d1c-a345-861b6c85681b");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedCanReadCancellationWinsOverOwnedInputCleanupFailure(bool accessDeniedOnCleanup)
    {
        using CancellationTokenSource cancel = new();
        using MemoryStream stage = new();
        CanReadCancellationStream input = new(cancel, accessDeniedOnCleanup);
        Assert.False(cancel.IsCancellationRequested);

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(input)], PackageId, cancel.Token));

        Assert.True(input.CanReadChecked);
        Assert.True(input.CleanupAttempted);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.ToString(), StringComparison.Ordinal);
        Assert.True(stage.CanRead);
        Assert.True(stage.CanWrite);
    }

    [Fact]
    public async Task ReadIoFailureIsNotRelabeledAsCancellationFromTokenStateAlone()
    {
        using CancellationTokenSource cancel = new();
        using MemoryStream stage = new();
        CancelThenIoFailureStream input = new(cancel);
        Assert.False(cancel.IsCancellationRequested);

        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(
            stage, Catalog(), [Payload(input)], PackageId, cancel.Token);

        Assert.True(input.ReadAttempted);
        Assert.Equal(cancel.Token, input.ReadToken);
        Assert.True(cancel.IsCancellationRequested);
        Assert.True(input.CleanupAttempted);
        Assert.Equal(TransferWriteError.IoFailure, result.Error);
        Assert.False(result.IsComplete);
        Assert.Null(result.Package);
        Assert.Null(result.BytesWritten);
        Assert.DoesNotContain("SECRET", result.ToString(), StringComparison.Ordinal);
        Assert.True(stage.CanRead);
        Assert.True(stage.CanWrite);
    }

    // Small private copies of the existing writer tests' synthetic catalog/payload pattern.
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static NativeTransferCatalog Catalog()
    {
        CapturedAttachment metadata = new(0, "7001001", "original", "https://i.4cdn.org/b/7001001.jpg", "synthetic.jpg");
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(Original), null);
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null, "synthetic post", false, [metadata]);
        return new(At, [new(new("4chan", "b", "7001"), "https://boards.4chan.org/b/thread/7001", "synthetic", ThreadSourceState.NotFound,
            At, At, 1, true, [new(post, [attachment])])]);
    }

    private static TransferPayloadSource Payload(Stream input) => new(new(Original.Length, Hash(Original)),
        _ => Task.FromResult(input));

    private sealed class CanReadCancellationStream(CancellationTokenSource cancel, bool accessDeniedOnCleanup) : MemoryStream
    {
        internal bool CanReadChecked { get; private set; }
        internal bool CleanupAttempted { get; private set; }

        public override bool CanRead
        {
            get
            {
                CanReadChecked = true;
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }
        }

        public override ValueTask DisposeAsync()
        {
            CleanupAttempted = true;
            return accessDeniedOnCleanup
                ? ValueTask.FromException(new UnauthorizedAccessException("/SECRET/synthetic/cleanup"))
                : ValueTask.FromException(new IOException("/SECRET/synthetic/cleanup"));
        }
    }

    private sealed class CancelThenIoFailureStream(CancellationTokenSource cancel) : MemoryStream
    {
        internal bool ReadAttempted { get; private set; }
        internal CancellationToken ReadToken { get; private set; }
        internal bool CleanupAttempted { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            ReadToken = cancellationToken;
            cancel.Cancel();
            // Deliberately no OCE: an environmental failure remains an environmental failure.
            throw new IOException("/SECRET/synthetic/read");
        }

        public override ValueTask DisposeAsync()
        {
            CleanupAttempted = true;
            return base.DisposeAsync();
        }
    }
}
