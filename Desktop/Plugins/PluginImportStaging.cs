using System;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly RegularProfileFileIdentity _leaseIdentity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RegularProfileFileIdentity? _payloadIdentity;
    private (string Path, RegularProfileFileIdentity Identity)? _published;
    private bool _writeStarted, _payloadReady, _disposed;
    internal string PayloadPath { get; }

    private PluginImportStaging(string plugins, string directory, string leasePath, string payloadPath, FileStream lease,
        RegularProfileFileIdentity leaseIdentity)
    {
        _plugins = plugins; _directory = directory; _leasePath = leasePath; PayloadPath = payloadPath;
        _lease = lease; _leaseIdentity = leaseIdentity;
    }

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
        try { return new(plugins, directory, stem + ".lock", stem + ".tmp", lease, Identity(stem + ".lock")); }
        catch { lease.Dispose(); throw; }
    }

    internal Task WriteAsync(byte[] bytes, CancellationToken cancellationToken) =>
        WriteAsync(bytes, cancellationToken, static path => new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous));

    // The factory seam lets regressions exercise real FileStream write/Flush(true)/close failures.
    // It must acquire this exact path with CreateNew; production always uses the overload above.
    internal async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken, Func<string, FileStream> createPayload)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_writeStarted) throw new InvalidOperationException("A staging payload can only be written once.");
            _writeStarted = true;
            ValidateStaging(_plugins, _directory);
            // Acquire ownership only after CreateNew succeeds, never from the reserved pathname alone.
            await using (FileStream output = createPayload(PayloadPath))
            {
                _payloadIdentity = Identity(PayloadPath);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            // Closing/flush failure leaves an owned partial file for disposal, but never a publishable payload.
            RequireOwnedFile(PayloadPath, _payloadIdentity);
            _payloadIdentity = Identity(PayloadPath);
            cancellationToken.ThrowIfCancellationRequested();
            _payloadReady = true;
        }
        finally { _gate.Release(); }
    }

    internal void Publish(string destination, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_payloadReady) throw new InvalidOperationException("The staging payload has not completed writing and flushing.");
            ValidateStaging(_plugins, _directory);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(destination)), _plugins, comparison))
                throw new IOException("Plugin import destination must be directly inside the plugin directory.");
            RequireOwnedFile(PayloadPath, _payloadIdentity);
            RegularProfileFileIdentity owned = _payloadIdentity!.Value;
            if (Identity(PayloadPath).Size != owned.Size)
                throw new IOException("The staged payload size changed before publication.");
            // On Unix, File.Move(..., overwrite: false) may observe an absent destination and then use rename(2),
            // which silently replaces whatever another writer placed there meanwhile (an installed plugin file).
            // It also "succeeds" without moving when the destination is already a hard link to the payload.
            // link(2) is the atomic no-clobber primitive; MoveFileEx without REPLACE_EXISTING is on Windows.
            if (OperatingSystem.IsWindows()) File.Move(PayloadPath, destination, overwrite: false);
            else if (LinkNoOverwrite(PayloadPath, destination) != 0)
                throw new IOException("The staged plugin file could not be published without replacing an existing entry.");
            // Commit point: the destination name is ours. Nothing below may report a failed publication.
            _published = (Path.GetFullPath(destination), owned);
            _payloadReady = false;
            if (OperatingSystem.IsWindows()) { _payloadIdentity = null; return; }
            try
            {
                // Drop our second (staging) name. If that fails it stays owned, so Dispose or the startup
                // crash-pair cleanup removes only this name; the published plugin file keeps its own link.
                RequireOwnedFile(PayloadPath, owned);
                File.Delete(PayloadPath);
                // The staging pathname is released. Dispose must not claim a later occupant of it.
                _payloadIdentity = null;
            }
            catch (Exception exception) when (IsFileError(exception)) { }
        }
        finally { _gate.Release(); }
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_Link", SetLastError = true)]
    private static extern int LinkNoOverwrite([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    /// <summary>Post-inspection rejection may discard only the exact file we published, never a replacement.</summary>
    internal void DiscardPublished()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_published is not { } published) return;
            try
            {
                ValidateStaging(_plugins, _directory);
                RequireOwnedFile(published.Path, published.Identity);
                File.Delete(published.Path);
            }
            catch (Exception exception) when (IsFileError(exception)) { }
            finally { _published = null; }
        }
        finally { _gate.Release(); }
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

    private static RegularProfileFileIdentity Identity(string path)
    {
        RegularProfileFileIdentity identity = RegularProfileFile.TryGetIdentity(path)
            ?? throw new IOException("Plugin import file ownership could not be verified.");
        // The path-only Windows fallback is a cache hint, not evidence of ownership.
        if (identity.Device == 0 && identity.Inode == 0 && identity.InodeHigh == 0)
            throw new IOException("Plugin import requires a stable file identity.");
        return identity;
    }

    private static void RequireOwnedFile(string path, RegularProfileFileIdentity? owned)
    {
        ValidateFile(path);
        RegularProfileFileIdentity current = Identity(path);
        if (owned is not { } expected || !SameObject(current, expected))
            throw new IOException("Plugin import staging entry is no longer owned by this import.");
    }

    private static bool SameObject(RegularProfileFileIdentity left, RegularProfileFileIdentity right) =>
        left.Device == right.Device && left.Inode == right.Inode && left.InodeHigh == right.InodeHigh;

    public void Dispose()
    {
        // Join an in-flight write before releasing its lease. A partial/failed flush never becomes publishable.
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            bool payloadRemoved = false;
            try
            {
                ValidateStaging(_plugins, _directory);
                if (_payloadIdentity is { } owned &&
                    (File.Exists(PayloadPath) || Directory.Exists(PayloadPath) || new FileInfo(PayloadPath).LinkTarget != null))
                {
                    ValidateFile(PayloadPath);
                    // A different regular file is not ours. Remove our lease below as well, so startup cleanup
                    // cannot later mistake that foreign entry for our crash remnant.
                    if (SameObject(Identity(PayloadPath), owned)) File.Delete(PayloadPath);
                }
                payloadRemoved = true;
            }
            catch (Exception exception) when (IsFileError(exception)) { }
            finally { _lease.Dispose(); }
            // Keep a discoverable crash pair if removing the owned payload failed. Never strand it unpaired.
            if (!payloadRemoved) return;
            try
            {
                ValidateStaging(_plugins, _directory);
                RequireOwnedFile(_leasePath, _leaseIdentity);
                File.Delete(_leasePath);
            }
            catch (Exception exception) when (IsFileError(exception)) { }
        }
        finally { _gate.Release(); }
    }
}
