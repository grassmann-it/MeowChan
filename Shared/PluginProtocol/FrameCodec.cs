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
            throw new InvalidDataException("The protocol frame length is outside the allowed range.");
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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, WireJson.Options);
        if (bytes.Length > ProtocolVersion.MaximumFrameBytes)
            throw new InvalidDataException("The protocol frame exceeds 16 MiB.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    public void Dispose() => _writer.Dispose();
}
