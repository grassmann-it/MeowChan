using System;
using System.Security.Cryptography;
using System.Threading;

namespace MeowChan.Secrets;

internal enum SecretStoreStatus
{
    Available,
    Missing,
    Locked,
    Unavailable,
    Disabled
}

// Native operations are synchronous. The host calls them off the UI and transport continuations.
internal interface IBooruSecretStore
{
    SecretStoreStatus Write(string target, byte[] secret, CancellationToken cancellationToken);
    SecretStoreRead Read(string target, CancellationToken cancellationToken);
    SecretStoreStatus Delete(string target, CancellationToken cancellationToken);
}

internal sealed class SecretStoreRead(SecretStoreStatus status, byte[]? bytes = null) : IDisposable
{
    // Credential Manager's generic credential blob limit also bounds the shared format.
    internal const int MaximumBytes = 5 * 512;

    internal SecretStoreStatus Status { get; } = status;
    internal byte[]? Bytes { get; } = bytes;

    public void Dispose()
    {
        if (Bytes is not null) CryptographicOperations.ZeroMemory(Bytes);
    }
}
