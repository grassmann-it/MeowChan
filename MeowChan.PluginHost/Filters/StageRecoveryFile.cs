using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Filters;

/// <summary>Recovery never opens a pipe/device or follows a link. The supported product RIDs are 64-bit.</summary>
internal static class StageRecoveryFile
{
    internal static byte[]? ReadJournal(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (GetFileType(handle) != 1) return null;
            }
            else
            {
                int flags = OperatingSystem.IsMacOS() ? 0x0004 | 0x0100 | 0x1000000 : 0x0800 | 0x20000 | 0x80000;
                int descriptor = Open(path, flags); // O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
                if (descriptor < 0) return null;
                handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                IntPtr stat = Marshal.AllocHGlobal(256);
                try
                {
                    if (FStat(descriptor, stat) != 0) return null;
                    int offset = OperatingSystem.IsMacOS() ? 4 : RuntimeInformation.ProcessArchitecture switch
                    { Architecture.X64 => 24, Architecture.Arm64 => 16, _ => throw new PlatformNotSupportedException("Unsupported stage recovery ABI.") };
                    int mode = OperatingSystem.IsMacOS() ? (ushort)Marshal.ReadInt16(stat, offset) : Marshal.ReadInt32(stat, offset);
                    if ((mode & 0xf000) != 0x8000) return null; // S_IFREG
                }
                finally { Marshal.FreeHGlobal(stat); }
            }
            using FileStream stream = new(handle, FileAccess.Read, 4096, isAsync: false);
            handle = null;
            if (stream.Length > 64 * 1024) return null;
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return stream.ReadByte() == -1 ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        finally { handle?.Dispose(); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(int descriptor, IntPtr buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
}
