using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

internal abstract class NativeBooruSecretStore : IBooruSecretStore
{
    internal static IBooruSecretStore Create() =>
        OperatingSystem.IsWindows() ? new WindowsBooruSecretStore()
        : OperatingSystem.IsMacOS() ? new MacBooruSecretStore()
        : OperatingSystem.IsLinux() ? new LinuxBooruSecretStore()
        : new UnavailableStore();

    public SecretStoreRead Read(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return ReadNative(target, cancellationToken); }
        catch (Exception error) when (IsInteropFailure(error)) { return new(SecretStoreStatus.Unavailable); }
    }

    public SecretStoreStatus Write(string target, byte[] secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.Length is 0 or > SecretStoreRead.MaximumBytes) return SecretStoreStatus.Unavailable;
        try { return WriteNative(target, secret, cancellationToken); }
        catch (Exception error) when (IsInteropFailure(error)) { return SecretStoreStatus.Unavailable; }
    }

    public SecretStoreStatus Delete(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return DeleteNative(target, cancellationToken); }
        catch (Exception error) when (IsInteropFailure(error)) { return SecretStoreStatus.Unavailable; }
    }

    private static bool IsInteropFailure(Exception error) => error is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;

    protected abstract SecretStoreRead ReadNative(string target, CancellationToken token);
    protected abstract SecretStoreStatus WriteNative(string target, byte[] secret, CancellationToken token);
    protected abstract SecretStoreStatus DeleteNative(string target, CancellationToken token);

    private sealed class UnavailableStore : NativeBooruSecretStore
    {
        protected override SecretStoreRead ReadNative(string target, CancellationToken token) => new(SecretStoreStatus.Unavailable);
        protected override SecretStoreStatus WriteNative(string target, byte[] secret, CancellationToken token) => SecretStoreStatus.Unavailable;
        protected override SecretStoreStatus DeleteNative(string target, CancellationToken token) => SecretStoreStatus.Unavailable;
    }
}
