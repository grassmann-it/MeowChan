using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.Persistence;

/// <summary>
/// Opens a profile-owned read file without allowing a Unix special file to turn a
/// synchronous metadata read into a blocking operation. The returned stream owns
/// the already-validated handle; callers still perform their normal bounded and
/// EOF-checked read.
/// </summary>
internal static class RegularProfileFile
{
    private const int UnixRegularFileMode = 0x8000;
    private const int UnixSpecialFileMask = 0xF000;

    // errno values shared by the supported Unix targets.
    private const int ErrorPermissionDenied = 1;
    private const int ErrorNoSuchFile = 2;
    private const int ErrorAccessDenied = 13;
    private const int ErrorLoop = 40; // Linux ELOOP; lstat catches final links first.
    private static readonly bool FileLockingDisabled = ReadFileLockingDisabled();

    internal static FileStream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OperatingSystem.IsWindows() ? OpenWindows(path) : OpenUnix(path);
    }

    /// <summary>
    /// The identity of a regular file at <paramref name="path"/> without following a final link, for cache
    /// validation only: device, inode, size, modification and status-change time on Unix; size, last-write and
    /// creation time on Windows. Null when the entry is missing, is not a regular file or cannot be inspected; a
    /// caller then reads the file through <see cref="OpenRead"/>, which reports the precise failure.
    /// </summary>
    internal static RegularProfileFileIdentity? TryGetIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileInfo info = new(path);
                if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory
                        | FileAttributes.Device)) != 0)
                    return null;
                return new(0, 0, info.Length, info.LastWriteTimeUtc.Ticks, 0, info.CreationTimeUtc.Ticks, 0);
            }
            if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                return null;
            if (UnixLStat(path, out FileStatus status) != 0 || (status.Mode & UnixSpecialFileMask) != UnixRegularFileMode)
                return null;
            return new(status.Dev, status.Ino, status.Size, status.MTime, status.MTimeNsec, status.CTime, status.CTimeNsec);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileStream OpenWindows(string path)
    {
        // Inspect the opened object itself, never a second path lookup. Opening
        // the final reparse point rather than its target closes the name-swap gap.
        try
        {
            SafeFileHandle handle = WindowsCreateFile(path, 0x80000000 /* GENERIC_READ */,
                1 /* FILE_SHARE_READ */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error == 2) throw new FileNotFoundException("The profile file is missing.");
                if (error == 5) throw new UnauthorizedAccessException("The profile file could not be read.");
                throw new IOException("The profile file could not be read.");
            }
            try
            {
                if (WindowsGetFileType(handle) != 1 /* FILE_TYPE_DISK */
                    || !WindowsGetAttributeTag(handle, 9 /* FileAttributeTagInfo */,
                        out WindowsAttributeTag tag, (uint)Marshal.SizeOf<WindowsAttributeTag>()))
                    throw new InvalidDataException("The profile file is not a regular file.");
                if ((tag.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("The profile file is not a regular file.");
                return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException("The profile file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new IOException("The profile file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("The profile file could not be read.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new IOException("The profile file could not be read.");
        }
    }

    private static FileStream OpenUnix(string path)
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Safe profile reads are unavailable on this platform.");
        try { return OpenUnixCore(path); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new PlatformNotSupportedException("The runtime cannot safely inspect profile files."); }
    }

    private static FileStream OpenUnixCore(string path)
    {
        FileStatus status;
        if (UnixLStat(path, out status) != 0)
            ThrowUnixError(Marshal.GetLastPInvokeError(), path);
        if ((status.Mode & UnixSpecialFileMask) != UnixRegularFileMode)
            throw new InvalidDataException("The profile file is not a regular file.");

        // O_NONBLOCK is deliberately supplied to the real open. The lstat check is
        // needed for fail-closed diagnostics, while this handle-open boundary means
        // a path swap to a FIFO cannot park the thread before fstat can reject it.
        int flags = OperatingSystem.IsMacOS()
            ? 0x0004 | 0x0100 | 0x1000000 // O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC (Darwin)
            : 0x0800 | 0x20000 | 0x80000; // same flags on Linux
        int descriptor = UnixOpen(path, flags, 0);
        if (descriptor == -1)
            ThrowUnixError(Marshal.GetLastPInvokeError(), path);

        SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (UnixFStat(handle, out status) != 0)
                ThrowUnixError(Marshal.GetLastPInvokeError(), path);
            if ((status.Mode & UnixSpecialFileMask) != UnixRegularFileMode)
                throw new InvalidDataException("The profile file is not a regular file.");

            // Match FileStream's cooperative sharing boundary. Constructing a
            // stream from an existing handle does not acquire its usual shared
            // advisory lock, so acquire a nonblocking PAL LOCK_SH ourselves.
            if (!FileLockingDisabled && UnixFLock(handle, 1 | 4 /* LOCK_SH | LOCK_NB */) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                // Like .NET FileStream, sharing locks are best-effort on Unix.
                // Unsupported/denied advisory locking must not break readable
                // regular files; only actual contention rejects this read.
                if (error == (OperatingSystem.IsMacOS() ? 35 : 11) /* EWOULDBLOCK */)
                    throw new IOException("The profile file could not be read.");
            }

            // The descriptor points at the object validated by fstat, not merely at
            // the name that happened to pass lstat. FileStream owns this handle.
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool ReadFileLockingDisabled()
    {
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled)) return disabled;
        string? value = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowUnixError(int error, string path)
    {
        if (error == ErrorNoSuchFile)
        {
            // ENOENT is also returned when an ancestor disappeared. Treat only a
            // missing final entry under an established parent as store absence;
            // otherwise a live lease must fail closed instead of becoming empty.
            string? parent = Path.GetDirectoryName(path);
            if (parent == null || !Directory.Exists(parent))
                throw new IOException("The profile file could not be read.");
            throw new FileNotFoundException("The profile file is missing.");
        }
        if (error is ErrorPermissionDenied or ErrorAccessDenied)
            throw new UnauthorizedAccessException("The profile file could not be read.");
        if (error == ErrorLoop || OperatingSystem.IsMacOS() && error == 62)
            throw new InvalidDataException("The profile file is not a regular file.");
        throw new IOException("The profile file could not be read.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle WindowsCreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint WindowsGetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetAttributeTag(SafeFileHandle handle, int informationClass,
        out WindowsAttributeTag information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsAttributeTag
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    // The normalized System.Native ABI is stable across the .NET Unix targets;
    // unlike libc's struct stat it is not target-libc-layout dependent.
    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int UnixLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus output);

    [DllImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int UnixFStat(SafeFileHandle descriptor, out FileStatus output);

    [DllImport("System.Native", EntryPoint = "SystemNative_FLock", SetLastError = true)]
    private static extern int UnixFLock(SafeFileHandle descriptor, int operation);

    [DllImport("libc", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec;
        internal long BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }
}

/// <summary>Cache-validation identity of a regular profile file (see <see cref="RegularProfileFile.TryGetIdentity"/>).</summary>
internal readonly record struct RegularProfileFileIdentity(long Device, long Inode, long Size,
    long ModifiedSeconds, long ModifiedNanoseconds, long ChangedSeconds, long ChangedNanoseconds);
