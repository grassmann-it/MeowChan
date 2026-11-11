using System.Text.Json;
using MeowChan.PluginProtocol;

namespace MeowChan.PluginFilters.Tests;

public sealed class FrameCodecTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancellationDuringAWriteStillCompletesTheFrameSoTheNextFrameIsReadable()
    {
        StalledPipe pipe = new();
        using FrameCodec writer = new(Stream.Null, pipe);
        using CancellationTokenSource cancel = new();
        Task first = writer.WriteAsync(Envelope("first", "a"), cancel.Token);
        await pipe.Stalled.Task.WaitAsync(Deadline); // part of the frame is already on the pipe
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        Task second = writer.WriteAsync(Envelope("second", "b"), CancellationToken.None);
        pipe.Resume();
        await second.WaitAsync(Deadline);

        using FrameCodec reader = new(new MemoryStream(pipe.Written.ToArray()), Stream.Null);
        Assert.Equal("first", (await reader.ReadAsync(CancellationToken.None))!.RequestId);
        WireEnvelope next = (await reader.ReadAsync(CancellationToken.None))!;
        Assert.Equal("second", next.RequestId);
        Assert.Equal("b", WireJson.Read<string>(next.Payload));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    private static WireEnvelope Envelope<T>(string requestId, T payload) => new(ProtocolVersion.Major, "response", "thread", requestId,
        "worker", "build", "config", null, null, null, null, null, WireJson.Element(payload));

    // A pipe whose buffer accepts part of the first write and then blocks until the reader drains it.
    // Like a real pipe, the blocked write honours the token it was given.
    private sealed class StalledPipe : Stream
    {
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _stalled;
        public MemoryStream Written { get; } = new();
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume() => _resume.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_stalled)
            {
                _stalled = true;
                int accepted = buffer.Length / 2;
                Written.Write(buffer.Span[..accepted]);
                buffer = buffer[accepted..];
                Stalled.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            Written.Write(buffer.Span);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
