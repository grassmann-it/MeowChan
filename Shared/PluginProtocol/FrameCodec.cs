using System.Buffers.Binary;
using System.Text.Json;

namespace MeowChan.PluginProtocol;

/// <summary>One reader per pipe; all writers share this instance's asynchronous lock.</summary>
internal sealed class FrameCodec : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writer = new(1, 1);
    public FrameCodec(Stream input, Stream output) { _input = input; _output = output; }

    public async Task<WireEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4];
        int first = await _input.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await _input.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > ProtocolVersion.MaximumFrameBytes)
            throw new FrameLimitException("The protocol frame length is outside the allowed range.");
        byte[] bytes = new byte[length];
        await _input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        WireEnvelope envelope = JsonSerializer.Deserialize<WireEnvelope>(bytes, WireJson.Options)
            ?? throw new JsonException("Empty protocol envelope.");
        if (envelope.Version != ProtocolVersion.Major || string.IsNullOrWhiteSpace(envelope.RequestId)
            || envelope.RequestId.Length > 128 || string.IsNullOrWhiteSpace(envelope.Operation))
            throw new InvalidDataException("Invalid protocol version or envelope identity.");
        return envelope;
    }

    public async Task WriteAsync(WireEnvelope envelope, CancellationToken cancellationToken)
    {
        using MemoryStream frame = new();
        frame.Position = 4; // length prefix, filled in below: one buffer, one write
        JsonSerializer.Serialize(frame, envelope, WireJson.Options);
        int length = (int)frame.Length - 4;
        if (length > ProtocolVersion.MaximumFrameBytes)
            throw new FrameLimitException("The protocol frame exceeds 16 MiB.");
        byte[] bytes = frame.GetBuffer();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Once started, a frame is always finished: a partial frame would desynchronize the peer.
        // A cancelled caller stops waiting; the lock stays held until the write ends or the
        // owner kills the worker on its cancellation grace, which breaks a hung pipe.
        Task write = WriteFrameAsync(bytes.AsMemory(0, length + 4));
        try { await write.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = write.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> frame)
    {
        try
        {
            await _output.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    public void Dispose() => _writer.Dispose();
}

internal sealed class FrameLimitException(string message) : IOException(message);
