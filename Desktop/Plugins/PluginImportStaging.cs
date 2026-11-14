using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Private, same-filesystem staging for loose plugin sources. A separate exclusive lease stays open even while
/// the payload is closed for publication. Cleanup never infers inactivity from a timestamp or a process ID.
/// </summary>
internal sealed class PluginImportStaging : IDisposable
{
    internal const string DirectoryName = ".plugin-import-staging";
    private const string Prefix = ".plugin-import-";
    private readonly string _plugins;
    private readonly string _directory;
    private readonly string _leasePath;
    private readonly FileStream _lease;
    internal string PayloadPath { get; }

    private PluginImportStaging(string plugins, string directory, string leasePath, string payloadPath, FileStream lease)
    { _plugins = plugins; _directory = directory; _leasePath = leasePath; PayloadPath = payloadPath; _lease = lease; }

    internal static PluginImportStaging Create(string pluginDirectory)
    {
        string plugins = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDirectory));
        ValidatePluginDirectory(plugins);
        string directory = Path.Combine(plugins, DirectoryName);
        // Do not adopt a link, including a dangling link, or a regular file with our reserved directory name.
        if (new DirectoryInfo(directory).LinkTarget != null) throw new IOException("Plugin import staging cannot be a link.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateStaging(plugins, directory);
        if (OperatingSystem.IsWindows()) File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        string stem = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N"));
        FileStream lease = new(stem + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return new(plugins, directory, stem + ".lock", stem + ".tmp", lease);
    }

    internal async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStaging(_plugins, _directory);
        await using FileStream output = new(PayloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal void Publish(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStaging(_plugins, _directory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(destination)), _plugins, comparison))
            throw new IOException("Plugin import destination must be directly inside the plugin directory.");
        ValidateFile(PayloadPath);
        File.Move(PayloadPath, destination, overwrite: false);
    }

    /// <summary>
    /// Startup hook: only exact, paired names in our validated private directory are eligible. An unpaired payload,
    /// unknown file, directory, link, nonempty lease or actively held lease is left alone. Never recurses or creates.
    /// </summary>
    internal static void CleanupAbandoned(string pluginDirectory)
    {
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled) return;
        string? lockingDisabled = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        if (lockingDisabled == "1" || string.Equals(lockingDisabled, "true", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            string plugins = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDirectory));
            string directory = Path.Combine(plugins, DirectoryName);
            ValidateStaging(plugins, directory);
            foreach (string leasePath in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(leasePath);
                if (!IsLeaseName(name)) continue;
                try
                {
                    ValidateStaging(plugins, directory);
                    ValidateFile(leasePath);
                    // The no-follow, regular-file open respects the active importer's FileShare.None lease.
                    // It also refuses FIFOs/devices without blocking startup.
                    using (FileStream lease = RegularProfileFile.OpenRead(leasePath))
                    {
                        if (!lease.CanSeek || lease.Length != 0) continue;
                        string payload = leasePath[..^5] + ".tmp";
                        ValidateStaging(plugins, directory);
                        if (File.Exists(payload) || Directory.Exists(payload) || new FileInfo(payload).LinkTarget != null)
                        {
                            ValidateFile(payload);
                            File.Delete(payload);
                        }
                    }
                    ValidateStaging(plugins, directory);
                    ValidateFile(leasePath);
                    File.Delete(leasePath);
                }
                catch (Exception exception) when (IsFileError(exception)) { }
            }
        }
        catch (Exception exception) when (IsFileError(exception)) { }
    }

    private static bool IsLeaseName(string name)
    {
        if (name.Length != Prefix.Length + 32 + 5 || !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !name.EndsWith(".lock", StringComparison.Ordinal)) return false;
        foreach (char c in name.AsSpan(Prefix.Length, 32))
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static void ValidateStaging(string plugins, string directory)
    {
        ValidatePluginDirectory(plugins);
        ValidateDirectory(directory);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Plugin import staging must be private to its owner.");
    }

    private static void ValidatePluginDirectory(string path)
    {
        // Validate ancestors from the filesystem root down, before traversing a component below them.
        string root = Path.GetPathRoot(path)!;
        string current = root;
        ValidateDirectory(current);
        foreach (string component in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            ValidateDirectory(current);
        }
    }

    private static void ValidateDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("Plugin import requires a directory that is not a link or reparse point.");
    }

    private static void ValidateFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || RegularProfileFile.IsUnixSpecialFile(path))
            throw new IOException("Plugin import staging entry is not a regular file.");
    }

    private static bool IsFileError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    public void Dispose()
    {
        // Remove the payload before releasing its lease; cleanup can never mistake a live payload for a crash remnant.
        bool payloadRemoved = false;
        try
        {
            ValidateStaging(_plugins, _directory);
            if (File.Exists(PayloadPath) || Directory.Exists(PayloadPath) || new FileInfo(PayloadPath).LinkTarget != null)
            { ValidateFile(PayloadPath); File.Delete(PayloadPath); }
            payloadRemoved = true;
        }
        catch (Exception exception) when (IsFileError(exception)) { }
        finally { _lease.Dispose(); }
        // Keep a discoverable crash pair if removing the payload failed. Never strand an unpaired payload.
        if (!payloadRemoved) return;
        try
        {
            ValidateStaging(_plugins, _directory);
            ValidateFile(_leasePath);
            File.Delete(_leasePath);
        }
        catch (Exception exception) when (IsFileError(exception)) { }
    }
}
