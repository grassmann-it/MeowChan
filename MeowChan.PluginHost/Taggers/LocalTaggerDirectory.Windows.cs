using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Taggers;

internal sealed partial class LocalTaggerDirectory
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000 | ReadAttributes;
    private const uint DirectoryAccess = 0x20 | ReadAttributes; // FILE_TRAVERSE
    private const uint ReadAccess = 0x1 | ReadAttributes;
    private const uint WriteAccess = 0x2 | ReadAttributes;
    private const uint Synchronize = 0x100000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 1; // NtCreateFile dispositions
    private const uint CreateNew = 2;
    private const uint OpenIf = 3;
    private const uint OpenReparsePoint = 0x00200000;

    private static SafeFileHandle WindowsOpenProfile(string path)
    {
        SafeFileHandle handle = WindowsCreateFile(path, DirectoryAccess, ShareAll, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* BACKUP_SEMANTICS */ | OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"The profile directory could not be opened (Windows error {error}).");
    }

    private static SafeFileHandle? WindowsOpenRelative(SafeFileHandle parent, string name, bool directory, uint disposition, uint access)
    {
        // Relative NT opens resolve only this component and do not process a final reparse point.
        int nameSize = checked(name.Length * sizeof(char));
        IntPtr unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        IntPtr buffer = IntPtr.Zero;
        bool parentPinned = false;
        try
        {
            buffer = Marshal.StringToHGlobalUni(name);
            Marshal.StructureToPtr(new UnicodeString
            {
                Buffer = buffer, Length = checked((ushort)nameSize), Capacity = checked((ushort)(nameSize + sizeof(char)))
            }, unicode, fDeleteOld: false);
            parent.DangerousAddRef(ref parentPinned);
            ObjectAttributes attributes = new()
            {
                Size = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = unicode,
                Flags = 0x40 // OBJ_CASE_INSENSITIVE
            };
            uint options = OpenReparsePoint | 0x20 /* SYNCHRONOUS_IO_NONALERT */
                | (directory ? 1u /* DIRECTORY_FILE */ : 0x40u /* NON_DIRECTORY_FILE */);
            int status = WindowsNtCreateFile(out IntPtr file, access | Synchronize, ref attributes, out _, IntPtr.Zero,
                0x80 /* FILE_ATTRIBUTE_NORMAL */, disposition == CreateNew ? 0u : ShareAll, disposition, options, IntPtr.Zero, 0);
            if (status is unchecked((int)0xC0000034) or unchecked((int)0xC000000F)) return null;
            if (status < 0) throw WindowsError(status);
            SafeFileHandle handle = new(file, ownsHandle: true);
            if (WindowsGetAttributes(handle, 9 /* FileAttributeTagInfo */, out AttributeTag tag, 8)
                && (tag.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0) return handle;
            handle.Dispose();
            throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        }
        finally
        {
            if (parentPinned) parent.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(unicode);
        }
    }

    private static void WindowsDeleteFile(SafeFileHandle parent, string name)
    {
        using SafeFileHandle? handle = WindowsOpenRelative(parent, name, directory: false, OpenExisting, DeleteAccess);
        if (handle is null) return;
        byte delete = 1;
        if (!WindowsSetDelete(handle, 4 /* FileDispositionInfo */, ref delete, 1))
            throw new IOException($"The model file could not be deleted (Windows error {Marshal.GetLastPInvokeError()}).");
    }

    private static void WindowsReplaceFile(SafeFileHandle parent, string source, string destination)
    {
        using SafeFileHandle handle = WindowsOpenRelative(parent, source, directory: false, OpenExisting, DeleteAccess)
            ?? throw new IOException("The pending model pointer is missing.");
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int rootOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Root));
        int lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.NameLength));
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FirstNameCharacter));
        int size = Math.Max(Marshal.SizeOf<RenameInformation>(), nameOffset + name.Length);
        IntPtr info = Marshal.AllocHGlobal(size);
        bool parentPinned = false;
        try
        {
            parent.DangerousAddRef(ref parentPinned);
            Marshal.WriteInt32(info, 1); // ReplaceIfExists
            Marshal.WriteIntPtr(info, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(info, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
            int status = WindowsNtSetInformationFile(handle, out _, info, (uint)size, 10 /* FileRenameInformation */);
            if (status < 0) throw WindowsError(status);
        }
        finally
        {
            if (parentPinned) parent.DangerousRelease();
            Marshal.FreeHGlobal(info);
        }
    }

    private static IOException WindowsError(int status) => new($"Local tagger directory operation failed (NTSTATUS 0x{status:X8}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length, Capacity;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Size;
        internal IntPtr Root, Name;
        internal uint Flags;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status, Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        internal FileAttributes Attributes;
        internal uint Tag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        internal int Replace;
        internal IntPtr Root;
        internal int NameLength;
        internal ushort FirstNameCharacter;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle WindowsCreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static extern int WindowsNtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options,
        IntPtr extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static extern int WindowsNtSetInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatus,
        IntPtr information, uint size, int informationClass);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsGetAttributes(SafeFileHandle handle, int informationClass, out AttributeTag tag, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsSetDelete(SafeFileHandle handle, int informationClass, ref byte delete, uint size);
}
