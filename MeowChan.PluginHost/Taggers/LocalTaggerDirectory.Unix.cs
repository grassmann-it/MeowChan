using System.Runtime.InteropServices;
using MeowChan.Persistence;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Taggers;

internal sealed partial class LocalTaggerDirectory
{
    private const int NoEntry = 2;
    private const int AlreadyExists = 17;
    private const int PrivateFileMode = 0x180; // 0600
    private const uint PrivateDirectoryMode = 0x1C0; // 0700
    private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int SafeOpenFlags => OperatingSystem.IsMacOS()
        ? 0x4 | 0x100 | 0x1000000 // O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
        : 0x800 | (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000) | 0x80000;

    private static SafeFileHandle UnixOpenProfile(string path)
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Handle-relative model storage is unavailable on this platform.");
        int fd = UnixOpen(path, SafeOpenFlags | DirectoryFlag);
        if (fd < 0) throw UnixError(Marshal.GetLastPInvokeError());
        return new((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle? UnixOpenDirectory(SafeFileHandle parent, string name, bool create)
    {
        if (create && UnixMkdirAt(parent, name, PrivateDirectoryMode) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != AlreadyExists) throw UnixError(error);
        }
        return UnixOpenEntry(parent, name, SafeOpenFlags | DirectoryFlag);
    }

    private static SafeFileHandle? UnixOpenFile(SafeFileHandle parent, string name, bool create)
    {
        int flags = SafeOpenFlags;
        if (create) flags |= OperatingSystem.IsMacOS() ? 0xA01 : 0xC1; // O_WRONLY | O_CREAT | O_EXCL
        SafeFileHandle? handle = null;
        try
        {
            handle = UnixOpenEntry(parent, name, flags);
            if (handle is not null && !RegularProfileFile.IsUnixRegularHandle(handle))
                throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
            SafeFileHandle? result = handle;
            handle = null;
            return result;
        }
        finally { handle?.Dispose(); }
    }

    private static SafeFileHandle? UnixOpenEntry(SafeFileHandle parent, string name, int flags)
    {
        // Darwin arm64 puts the variadic openat mode on the stack, after the eight argument registers.
        int fd = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? DarwinArmOpenAt(parent, name, flags, 0, 0, 0, 0, 0, PrivateFileMode)
            : UnixOpenAt(parent, name, flags, PrivateFileMode);
        if (fd >= 0) return new((IntPtr)fd, ownsHandle: true);
        int error = Marshal.GetLastPInvokeError();
        return error == NoEntry ? null : throw UnixError(error);
    }

    private static void UnixDeleteFile(SafeFileHandle parent, string name)
    {
        if (UnixUnlinkAt(parent, name, 0) == 0) return;
        int error = Marshal.GetLastPInvokeError();
        if (error != NoEntry) throw UnixError(error);
    }

    private static void UnixReplaceFile(SafeFileHandle parent, string source, string destination)
    {
        if (UnixRenameAt(parent, source, parent, destination) != 0)
            throw UnixError(Marshal.GetLastPInvokeError());
    }

    private static IOException UnixError(int error) => new($"Local tagger directory operation failed (errno {error}).");

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int DarwinArmOpenAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags,
        nint register3, nint register4, nint register5, nint register6, nint register7, long mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int UnixMkdirAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnixUnlinkAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int UnixRenameAt(SafeFileHandle sourceParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        SafeFileHandle destinationParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
