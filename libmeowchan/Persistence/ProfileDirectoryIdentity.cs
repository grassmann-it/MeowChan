using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.Persistence;

/// <summary>
/// In-process physical directory fence. Not a durable profile incarnation, owner
/// capability or hostile-process race-proof sandbox. No path is serialized.
/// </summary>
internal sealed class ProfileDirectoryIdentity
{
    private readonly ulong _volume;
    private readonly ulong _file;

    private ProfileDirectoryIdentity(ulong volume, ulong file)
    { _volume = volume; _file = file; }

    // Ordinary legacy lease acquisition remains supported even if this optional
    // stricter fence cannot be established. Journal admission must require it.
    internal static ProfileDirectoryIdentity? TryCapture(string path)
    {
        try { return Capture(path); }
        catch (InvalidDataException) { return null; }
    }

    internal static ProfileDirectoryIdentity Capture(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CaptureWindows(path);
            if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw Invalid();
            if (UnixLStat(path, out FileStatus status) != 0 || (status.Mode & 0xF000) != 0x4000
                || status.Ino == 0) throw Invalid();
            return new(unchecked((ulong)status.Dev), unchecked((ulong)status.Ino));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { throw Invalid(); }
    }

    internal void EnsureMatches(string path)
    {
        ProfileDirectoryIdentity current = Capture(path);
        if (_volume != current._volume || _file != current._file) throw Invalid();
    }

    private static ProfileDirectoryIdentity CaptureWindows(string path)
    {
        using SafeFileHandle handle = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */,
            7 /* SHARE_READ | SHARE_WRITE | SHARE_DELETE */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformation(handle, out WindowsFileInformation information)
            || (information.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || (information.Attributes & FileAttributes.Directory) == 0) throw Invalid();
        ulong file = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if (file == 0) throw Invalid();
        return new(information.VolumeSerialNumber, file);
    }

    private static InvalidDataException Invalid() => new("The profile directory identity is unavailable or changed.");

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int UnixLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus output);

    // Normalized .NET PAL ABI, not platform-libc struct stat.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec;
        internal long BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformation(SafeFileHandle handle, out WindowsFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        internal FileAttributes Attributes;
        // FILETIME consists of two DWORDs (alignment 4), not an aligned Int64.
        internal uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
}
