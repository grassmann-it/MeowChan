using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

/// <summary>A bounded metadata response and the ephemeral material needed to reject echoes before publication.</summary>
internal sealed class BooruAccountAttempt(HttpResponseMessage response, SecretStoreRead? read, string? basic) : IDisposable
{
    private readonly byte[]? _encodedBasic = basic is null ? null : Encoding.ASCII.GetBytes(basic);

    internal HttpResponseMessage Response { get; } = response;
    internal bool IsAuthenticated => _encodedBasic is not null;

    internal bool ContainsCredential(ReadOnlySpan<byte> bytes)
    {
        if (_encodedBasic is null || read?.Bytes is not byte[] payload) return false;
        ReadOnlySpan<byte> credential = payload.AsSpan(BooruAccountBinding.ScopeFingerprintBytes);
        return bytes.IndexOf(credential[(credential.IndexOf((byte)':') + 1)..]) >= 0
            || bytes.IndexOf(_encodedBasic) >= 0;
    }

    internal bool ContainsCredential(string? value)
    {
        if (value is null || !IsAuthenticated) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { return ContainsCredential(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal bool IsBodySafe(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return true;
        if (ContainsCredential(body) || !Utf8.IsValid(body)) return false;

        // A provider can echo the key inside a JSON string using escapes.
        if (body.StartsWith("\uFEFF"u8)) body = body[3..];
        Utf8JsonReader reader = new(body);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName) || !reader.ValueIsEscaped)
                    continue;
                byte[] decoded = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
                try
                {
                    if (ContainsCredential(decoded.AsSpan(0, reader.CopyString(decoded)))) return false;
                }
                finally { ArrayPool<byte>.Shared.Return(decoded, clearArray: true); }
            }
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; } // Invalid Unicode escape during string decoding.
        return reader.TokenType != JsonTokenType.None;
    }

    public void Dispose()
    {
        try { Response.Dispose(); }
        finally
        {
            read?.Dispose();
            if (_encodedBasic is not null) CryptographicOperations.ZeroMemory(_encodedBasic);
        }
    }
}
