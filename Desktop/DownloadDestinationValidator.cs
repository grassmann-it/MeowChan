using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

public enum DownloadDestinationStatus
{
    Ready, NotChecked, InvalidSyntax, Missing, NotDirectory, AccessDenied, Unavailable,
    Full, BelowReserve, Unknown, Busy, TimedOut, Cancelled, CleanupFailed
}

public sealed record DownloadDestinationResult(DownloadDestinationStatus Status, string? NormalizedPath = null,
    long? AvailableBytes = null, string? CleanupPath = null)
{
    public bool CanApply => Status == DownloadDestinationStatus.Ready;
}

/// <summary>
/// Explicit Check/Apply only. Normalization is lexical, not a filesystem/symlink check.
/// A timeout/cancellation ends the caller's wait, NOT an in-flight OS operation.
/// Physical slots remain occupied until that operation and its owned-file cleanup finish.
/// </summary>
public sealed class DownloadDestinationValidator
{
    public static DownloadDestinationValidator Shared { get; } = new();
    private readonly SemaphoreSlim _slots;
    private readonly Func<string, long, DownloadDestinationResult> _probe;
    public event Action? CleanupFailed;
    public bool HasCleanupFailure => Volatile.Read(ref _cleanupFailed) != 0;
    private int _cleanupFailed;
    private string? _lastCleanupFailurePath;
    public string? LastCleanupFailurePath => Volatile.Read(ref _lastCleanupFailurePath);

    public DownloadDestinationValidator() : this(2, ProbeFileSystem) { }

    // Each fixture owns its own bounded worker pool; production always shares the single pool above.
    internal DownloadDestinationValidator(int maximumPhysicalProbes, Func<string, long, DownloadDestinationResult> probe)
    {
        if (maximumPhysicalProbes is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumPhysicalProbes));
        _slots = new(maximumPhysicalProbes, maximumPhysicalProbes);
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public static DownloadDestinationResult Normalize(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(DownloadDestinationStatus.InvalidSyntax);
        try
        {
            // No environment/shell expansion, CWD lookup, directory creation or physical IO.
            return new(DownloadDestinationStatus.NotChecked, Path.GetFullPath(path, baseDirectory));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(DownloadDestinationStatus.InvalidSyntax); }
    }

    public async Task<DownloadDestinationResult> CheckAsync(string path, string baseDirectory, long reserveBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (reserveBytes < 0) throw new ArgumentOutOfRangeException(nameof(reserveBytes));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        DownloadDestinationResult syntax = Normalize(path, baseDirectory);
        if (syntax.Status != DownloadDestinationStatus.NotChecked) return syntax;
        string normalized = syntax.NormalizedPath!;
        if (cancellationToken.IsCancellationRequested) return new(DownloadDestinationStatus.Cancelled, normalized);
        // No waiting queue or Task.Run allocation when all physical slots are occupied.
        if (!_slots.Wait(0)) return new(DownloadDestinationStatus.Busy, normalized);
        Task<DownloadDestinationResult> physical;
        try
        {
            physical = Task.Run(() =>
            {
                try
                {
                    DownloadDestinationResult result = _probe(normalized, reserveBytes);
                    if (result.Status == DownloadDestinationStatus.CleanupFailed)
                    {
                        Volatile.Write(ref _lastCleanupFailurePath, result.CleanupPath);
                        Interlocked.Exchange(ref _cleanupFailed, 1);
                        // Retain the warning even when the original waiter timed out/was retired.
                        // Observers cannot fault the physical worker or prevent slot release.
                        if (CleanupFailed is { } observers)
                            foreach (Action observer in observers.GetInvocationList())
                                try { observer(); } catch { /* notification is best effort */ }
                    }
                    return result;
                }
                catch (Exception error) { return ClassifyException(error, normalized); }
                finally { _slots.Release(); }
            });
        }
        catch { _slots.Release(); throw; }
        try { return await physical.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(DownloadDestinationStatus.TimedOut, normalized); }
        catch (OperationCanceledException) { return new(DownloadDestinationStatus.Cancelled, normalized); }
    }

    internal static DownloadDestinationResult Capacity(string path, long? available, long reserveBytes)
    {
        if (!available.HasValue || available < 0) return new(DownloadDestinationStatus.Unknown, path);
        if (available == 0) return new(DownloadDestinationStatus.Full, path, available);
        // Compare directly: no reserve+size overflow, no claim of a capacity reservation.
        return new(available < reserveBytes ? DownloadDestinationStatus.BelowReserve : DownloadDestinationStatus.Ready,
            path, available);
    }

    internal interface IProbeFileSystem
    {
        FileAttributes Attributes(string path);
        long? AvailableBytes(string path);
        Stream CreateNew(string path);
        void DeleteOwned(string path);
    }

    private sealed class PhysicalFileSystem : IProbeFileSystem
    {
        public FileAttributes Attributes(string path) => File.GetAttributes(path);
        public long? AvailableBytes(string path)
        {
            // Use the longest mount/root match, not '/' merely because it is ready.
            string target = Path.TrimEndingDirectorySeparator(path);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo? drive = DriveInfo.GetDrives().OrderByDescending(d => d.Name.Length).FirstOrDefault(d =>
            {
                string root = Path.TrimEndingDirectorySeparator(d.Name);
                return target.Equals(root, comparison) || target.StartsWith(Path.EndsInDirectorySeparator(root)
                    ? root : root + Path.DirectorySeparatorChar, comparison);
            });
            if (drive == null) return null;
            if (!drive.IsReady) throw new DriveNotFoundException();
            // Lexical mount matching does not prove a symlink target's volume/free space.
            // For links in the path, preserve Unknown rather than reporting the wrong drive.
            for (DirectoryInfo? part = new(path); part != null; part = part.Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return drive.AvailableFreeSpace;
        }
        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public void DeleteOwned(string path) => File.Delete(path);
    }

    private static DownloadDestinationResult ProbeFileSystem(string path, long reserveBytes)
        => ProbeFileSystem(path, reserveBytes, new PhysicalFileSystem());

    internal static DownloadDestinationResult ProbeFileSystem(string path, long reserveBytes, IProbeFileSystem fileSystem)
    {
        string? owned = null;
        DownloadDestinationResult result;
        try
        {
            FileAttributes attributes = fileSystem.Attributes(path);
            if ((attributes & FileAttributes.Directory) == 0) return new(DownloadDestinationStatus.NotDirectory, path);
            // Capacity failure cannot certify writeability, but the explicit own-file check
            // still distinguishes ACL/quota failures when free space cannot be queried.
            long? available;
            try { available = fileSystem.AvailableBytes(path); }
            catch (UnauthorizedAccessException) { available = null; }
            catch (IOException error) when (error is not DriveNotFoundException) { available = null; }
            string probePath = Path.Combine(path, ".meowchan-writecheck-" + Guid.NewGuid().ToString("N"));
            // A collision fails CreateNew: ownership is NEVER acquired, so never delete it.
            using (Stream stream = fileSystem.CreateNew(probePath))
            {
                owned = probePath;
                stream.WriteByte(0);
                stream.Flush();
            }
            result = Capacity(path, available, reserveBytes);
        }
        catch (Exception error) { result = ClassifyException(error, path); }
        // Delete only the exact file whose CreateNew succeeded. No mkdir, glob,
        // retry loop, existing-file open, move or cleanup of anybody else's .tmp.
        if (owned != null)
            try { fileSystem.DeleteOwned(owned); }
            catch { return new(DownloadDestinationStatus.CleanupFailed, path, CleanupPath: owned); }
        return result;
    }

    private static bool IsFullError(IOException error) => OperatingSystem.IsWindows()
        ? (error.HResult & 0xffff) is 39 or 112 or 1816
        : (error.HResult & 0xffff) is 28 or 122;

    private static DownloadDestinationResult ClassifyException(Exception error, string path) => new(error switch
    {
        FileNotFoundException or DirectoryNotFoundException => DownloadDestinationStatus.Missing,
        DriveNotFoundException => DownloadDestinationStatus.Unavailable,
        UnauthorizedAccessException or System.Security.SecurityException => DownloadDestinationStatus.AccessDenied,
        // ENOSPC/EDQUOT and Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL.
        IOException io when IsFullError(io) => DownloadDestinationStatus.Full,
        IOException io when !OperatingSystem.IsWindows() && (io.HResult & 0xffff) == 30 => DownloadDestinationStatus.AccessDenied,
        // Offline/network/unknown IO is not evidence of either ACL denial or success.
        _ => DownloadDestinationStatus.Unknown
    }, path);
}
