using System.Runtime.InteropServices;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

internal sealed class WindowsBooruSecretStore : NativeBooruSecretStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    protected override SecretStoreRead ReadNative(string target, CancellationToken token)
    {
        if (!CredRead(target, Generic, 0, out IntPtr pointer)) return new(Status());
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize is 0 or > SecretStoreRead.MaximumBytes || credential.Blob == IntPtr.Zero)
                return new(SecretStoreStatus.Unavailable);

            byte[] secret = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, secret, 0, secret.Length);
            return new(SecretStoreStatus.Available, secret);
        }
        finally { CredFree(pointer); }
    }

    protected override SecretStoreStatus WriteNative(string target, byte[] secret, CancellationToken token)
    {
        GCHandle pinned = GCHandle.Alloc(secret, GCHandleType.Pinned);
        try
        {
            Credential credential = new()
            {
                Type = Generic,
                TargetName = target,
                BlobSize = (uint)secret.Length,
                Blob = pinned.AddrOfPinnedObject(),
                Persist = PersistLocalMachine
            };
            return CredWrite(ref credential, 0) ? SecretStoreStatus.Available : Status();
        }
        finally { pinned.Free(); }
    }

    protected override SecretStoreStatus DeleteNative(string target, CancellationToken token) =>
        CredDelete(target, Generic, 0) ? SecretStoreStatus.Available : Status();

    private static SecretStoreStatus Status() => Marshal.GetLastPInvokeError() switch
    {
        1168 => SecretStoreStatus.Missing,    // ERROR_NOT_FOUND
        5 or 1909 => SecretStoreStatus.Locked, // ERROR_ACCESS_DENIED, ERROR_ACCOUNT_LOCKED_OUT
        _ => SecretStoreStatus.Unavailable
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal uint Flags, Type;
        internal string? TargetName;
        internal string? Comment;
        internal uint LastWrittenLow, LastWrittenHigh, BlobSize;
        internal IntPtr Blob;
        internal uint Persist, AttributeCount;
        internal IntPtr Attributes;
        internal string? TargetAlias;
        internal string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
