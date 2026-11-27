using System.Security.Cryptography;
using System.Text;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

/// <summary>A bounded metadata response and the ephemeral material needed to reject echoes before publication.</summary>
internal sealed class BooruAccountAttempt(HttpResponseMessage response, SecretStoreRead read, string? basic) : IDisposable
{
    private readonly byte[]? _encodedBasic = basic is null ? null : Encoding.ASCII.GetBytes(basic);

    internal HttpResponseMessage Response { get; } = response;
    internal bool IsAuthenticated => _encodedBasic is not null;

    internal bool ContainsCredential(ReadOnlySpan<byte> bytes)
    {
        if (_encodedBasic is null) return false;
        ReadOnlySpan<byte> credential = read.Bytes.AsSpan(BooruAccountBinding.ScopeFingerprintBytes);
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

    public void Dispose()
    {
        try { Response.Dispose(); }
        finally
        {
            read.Dispose();
            if (_encodedBasic is not null) CryptographicOperations.ZeroMemory(_encodedBasic);
        }
    }
}
