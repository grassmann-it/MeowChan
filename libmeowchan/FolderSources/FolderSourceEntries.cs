using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MeowChan.FolderSources;

/// <summary>What a directory entry of a folder source is, as far as following it is concerned.</summary>
internal enum FolderSourceEntryKind
{
    /// <summary>An ordinary file or folder: listed, followed and read.</summary>
    Plain,
    /// <summary>A link of any kind (symbolic link, junction, mount point, WSL link) or a reparse point that cannot be told apart: never followed.</summary>
    Link,
    /// <summary>
    /// A Windows cloud files placeholder (OneDrive and other sync clients, hydrated or not): its data is the file itself, so it is
    /// listed and followed like a plain entry. Whether reading it would download it first is <see cref="FolderSourceEntries.NeedsRecall"/>.
    /// </summary>
    CloudPlaceholder
}

/// <summary>
/// Tells links from other reparse points (W3-08). On Unix a reparse point is always a symbolic link. On Windows the reparse tag
/// decides: name surrogates (symbolic links, junctions, mount points, WSL links) are links; cloud files placeholders, deduplicated and
/// compressed files keep their data in place and are plain; any other or unreadable tag is treated as a link (fail closed). Reading the
/// tag lists the directory entry only (<c>FindFirstFileExW</c>); the file is never opened, so a placeholder is never downloaded by it.
/// </summary>
internal static class FolderSourceEntries
{
    internal const uint ReparseTagMountPoint = 0xA0000003, ReparseTagSymlink = 0xA000000C, ReparseTagWslSymlink = 0xA000001D;
    internal const uint ReparseTagDedup = 0x80000013, ReparseTagWof = 0x80000017;
    /// <summary>IO_REPARSE_TAG_CLOUD; the sixteen IO_REPARSE_TAG_CLOUD_1…F variants differ only in bits 12–15.</summary>
    internal const uint ReparseTagCloud = 0x9000001A, ReparseTagCloudMask = 0xFFFF0FFF;
    /// <summary>Bit 29 of a reparse tag (IsReparseTagNameSurrogate): the entry names another entry.</summary>
    internal const uint ReparseTagNameSurrogateBit = 0x20000000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (not named in <see cref="FileAttributes"/>).</summary>
    internal const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// The classification itself, free of any file system access so it can be tested on every platform. <paramref name="reparseTag"/>
    /// is the entry's reparse tag on Windows (null when it could not be read).
    /// </summary>
    internal static FolderSourceEntryKind Classify(FileAttributes attributes, uint? reparseTag, bool windows)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return FolderSourceEntryKind.Plain;
        if (!windows || reparseTag is not { } tag) return FolderSourceEntryKind.Link;
        if ((tag & ReparseTagNameSurrogateBit) != 0) return FolderSourceEntryKind.Link;
        if ((tag & ReparseTagCloudMask) == ReparseTagCloud) return FolderSourceEntryKind.CloudPlaceholder;
        return tag is ReparseTagDedup or ReparseTagWof ? FolderSourceEntryKind.Plain : FolderSourceEntryKind.Link;
    }

    /// <summary>
    /// True when reading the file's content would first fetch it from elsewhere (an online-only cloud placeholder, an offline file):
    /// only a view or a user action reads such a file, never the background pass.
    /// </summary>
    internal static bool NeedsRecall(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>The kind of an existing entry whose attributes were already read (from the listing or a stat).</summary>
    internal static FolderSourceEntryKind Of(FileSystemInfo entry, FileAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return FolderSourceEntryKind.Plain;
        if (!OperatingSystem.IsWindows()) return FolderSourceEntryKind.Link;
        return Classify(attributes, WindowsReparseTag(entry.FullName), windows: true);
    }

    /// <summary>True when the entry is a link that must not be followed.</summary>
    internal static bool IsLink(FileSystemInfo entry, FileAttributes attributes) => Of(entry, attributes) == FolderSourceEntryKind.Link;

    [SupportedOSPlatform("windows")]
    private static uint? WindowsReparseTag(string path)
    {
        IntPtr handle = FindFirstFileExW(Path.TrimEndingDirectorySeparator(path), 1 /* FindExInfoBasic */, out Win32FindData data,
            0 /* FindExSearchNameMatch */, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1)) return null;
        FindClose(handle);
        return ((FileAttributes)data.FileAttributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow;
        public uint Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
