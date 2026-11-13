using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.Persistence;

/// <summary>
/// Windows cache-validation identity from the opened object itself: volume serial number, 128-bit file id,
/// end of file, last-write and change time, matching the Unix device/inode/size/mtime/ctime identity. Change
/// time is not settable through the usual timestamp APIs, so a same-size rewrite that restores the
/// last-write time (backup restore tools) and a rename-over that NTFS tunneling gives the old creation time
/// are still detected, and a handle reports the current size of a file another process is still writing.
/// When the handle queries are unavailable (older file systems, access), the path-based fields are used.
/// </summary>
internal static partial class RegularProfileFile
{
    private const FileAttributes WindowsNonRegularAttributes =
        FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device;

    [SupportedOSPlatform("windows")]
    private static RegularProfileFileIdentity? TryGetWindowsIdentity(string path)
    {
        if (TryGetWindowsHandleIdentity(path, out RegularProfileFileIdentity? identity)) return identity;
        FileInfo info = new(path);
        if (!info.Exists || !IsWindowsRegularFile(info.Attributes)) return null;
        return WindowsPathIdentity(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    /// <summary>
    /// True when the opened object decided the result: its identity, or null for an object that is not a regular
    /// file. False when the handle or one of its queries is unavailable; the caller then uses the path fields.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsHandleIdentity(string path, out RegularProfileFileIdentity? identity)
    {
        identity = null;
        // Attribute access only, sharing everything: a probe must never block the store's own rename or a reader.
        using SafeFileHandle handle = WindowsCreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */,
            7 /* FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        if (WindowsGetFileType(handle) != 1 /* FILE_TYPE_DISK */) return true;
        if (!WindowsGetAttributeTag(handle, 9 /* FileAttributeTagInfo */, out WindowsAttributeTag tag,
                (uint)Marshal.SizeOf<WindowsAttributeTag>()))
            return false;
        if (!IsWindowsRegularFile(tag.Attributes)) return true;
        if (!WindowsGetBasicInfo(handle, 0 /* FileBasicInfo */, out WindowsFileBasicInfo basic,
                (uint)Marshal.SizeOf<WindowsFileBasicInfo>())
            || !WindowsGetStandardInfo(handle, 1 /* FileStandardInfo */, out WindowsFileStandardInfo standard,
                (uint)Marshal.SizeOf<WindowsFileStandardInfo>())
            || !WindowsGetIdInfo(handle, 18 /* FileIdInfo */, out WindowsFileIdInfo id,
                (uint)Marshal.SizeOf<WindowsFileIdInfo>()))
            return false;
        identity = WindowsHandleIdentity(id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh, standard.EndOfFile,
            basic.LastWriteTime, basic.ChangeTime);
        return true;
    }

    internal static bool IsWindowsRegularFile(FileAttributes attributes) => (attributes & WindowsNonRegularAttributes) == 0;

    /// <summary>
    /// The handle identity. Device and inode carry the volume serial number and the low half of the 128-bit file
    /// id, <see cref="RegularProfileFileIdentity.InodeHigh"/> its high half (ReFS); the time fields carry raw
    /// FILETIME values (100 ns units), so the nanosecond fields stay zero.
    /// </summary>
    internal static RegularProfileFileIdentity WindowsHandleIdentity(ulong volumeSerialNumber, ulong fileIdLow,
        ulong fileIdHigh, long endOfFile, long lastWriteTime, long changeTime) =>
        new(unchecked((long)volumeSerialNumber), unchecked((long)fileIdLow), endOfFile, lastWriteTime, 0, changeTime, 0,
            unchecked((long)fileIdHigh));

    /// <summary>The weaker path identity used when the handle queries are unavailable (size, last-write, creation).</summary>
    internal static RegularProfileFileIdentity WindowsPathIdentity(long length, long lastWriteTicks, long creationTicks) =>
        new(0, 0, length, lastWriteTicks, 0, creationTicks, 0);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetBasicInfo(SafeFileHandle handle, int informationClass,
        out WindowsFileBasicInfo information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetStandardInfo(SafeFileHandle handle, int informationClass,
        out WindowsFileStandardInfo information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetIdInfo(SafeFileHandle handle, int informationClass,
        out WindowsFileIdInfo information, uint size);

    /// <summary>FILE_BASIC_INFO (40 bytes): LARGE_INTEGER times, DWORD attributes, padded to 8.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowsFileBasicInfo
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint FileAttributes;
    }

    /// <summary>FILE_STANDARD_INFO (24 bytes): LARGE_INTEGER sizes, DWORD links, two BOOLEANs, padded to 8.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowsFileStandardInfo
    {
        internal long AllocationSize, EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending, Directory;
    }

    /// <summary>FILE_ID_INFO (24 bytes): ULONGLONG volume serial number, FILE_ID_128 as two little-endian halves.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowsFileIdInfo
    {
        internal ulong VolumeSerialNumber, FileIdLow, FileIdHigh;
    }
}
