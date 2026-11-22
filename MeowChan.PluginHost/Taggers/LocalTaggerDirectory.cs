using MeowChan.Persistence;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Taggers;

/// <summary>Single-component model store I/O rooted at an already opened physical directory.</summary>
internal sealed partial class LocalTaggerDirectory : IDisposable
{
    private LocalTaggerDirectory(SafeFileHandle handle) => Handle = handle;

    internal SafeFileHandle Handle { get; }

    internal static LocalTaggerDirectory OpenProfile(string path)
    {
        SafeFileHandle handle = OperatingSystem.IsWindows() ? WindowsOpenProfile(path) : UnixOpenProfile(path);
        return new(handle);
    }

    internal LocalTaggerDirectory? OpenDirectory(string name, bool create = false)
    {
        CheckName(name);
        SafeFileHandle? handle = null;
        try
        {
            handle = OperatingSystem.IsWindows()
                ? WindowsOpenRelative(Handle, name, directory: true, create ? OpenIf : OpenExisting, DirectoryAccess)
                : UnixOpenDirectory(Handle, name, create);
            if (handle is null) return null;
            LocalTaggerDirectory result = new(handle);
            handle = null;
            return result;
        }
        finally { handle?.Dispose(); }
    }

    internal void EnsureChildMatches(string name, LocalTaggerDirectory child)
    {
        using LocalTaggerDirectory? current = OpenDirectory(name);
        if (current is null) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        ProfileDirectoryIdentity.Capture(child.Handle).EnsureMatches(current.Handle);
    }

    internal FileStream? OpenRead(string name) => OpenFile(name, FileAccess.Read);

    internal FileStream CreateFile(string name) => OpenFile(name, FileAccess.Write)
        ?? throw new IOException("The local tagger file could not be created.");

    private FileStream? OpenFile(string name, FileAccess access)
    {
        CheckName(name);
        bool create = access == FileAccess.Write;
        SafeFileHandle? file = null;
        try
        {
            file = OperatingSystem.IsWindows()
                ? WindowsOpenRelative(Handle, name, directory: false, create ? CreateNew : OpenExisting, create ? WriteAccess : ReadAccess)
                : UnixOpenFile(Handle, name, create);
            if (file is null) return null;
            FileStream result = new(file, access, 64 * 1024, isAsync: false);
            file = null;
            return result;
        }
        finally { file?.Dispose(); }
    }

    internal void DeleteFile(string name)
    {
        CheckName(name);
        if (OperatingSystem.IsWindows()) WindowsDeleteFile(Handle, name);
        else UnixDeleteFile(Handle, name);
    }

    internal void ReplaceFile(string source, string destination)
    {
        CheckName(source);
        CheckName(destination);
        if (OperatingSystem.IsWindows()) WindowsReplaceFile(Handle, source, destination);
        else UnixReplaceFile(Handle, source, destination);
    }

    public void Dispose() => Handle.Dispose();

    private static void CheckName(string name)
    {
        if (name is "" or "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new ArgumentException("A model store entry must be a single relative name.", nameof(name));
    }
}
