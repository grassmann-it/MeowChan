using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MeowChan.Persistence;

/// <summary>
/// Owns exclusive write access to one profile for its entire lifetime. A profile
/// without a protocol marker is adopted while holding both the new lock
/// and, if legacy data exists, the old lock. The marker is published atomically
/// only after that adoption succeeds. Subsequent owners use only the new lock.
/// Old software restarted after adoption does not honor the new lock; concurrent
/// old/new use and downgrade of adopted data are unsupported.
/// </summary>
public sealed class ProfileLease : IDisposable
{
    private const string LockName = ".meowchan-profile.lock";
    private const string LegacyLockName = ".ochan-profile.lock";
    private const string MarkerName = ".meowchan-profile.protocol";
    private const string MarkerValue = "MeowChan profile protocol v1\n";
    private readonly object _sync = new object();
    private FileStream? _lockStream;

    public string ProfileDirectory { get; }
    // Captured under the exclusive lease, before this acquisition publishes protocol traces.
    internal bool WasEmptyAtAcquisition { get; }

    private ProfileLease(string profileDirectory, FileStream lockStream, bool wasEmpty)
    {
        ProfileDirectory = profileDirectory;
        _lockStream = lockStream;
        WasEmptyAtAcquisition = wasEmpty;
    }

    /// <summary>
    /// Explicitly creates and locks the profile; independent profiles can coexist.
    /// For pre-existing profiles, an active legacy owner prevents adoption before
    /// existing user data is changed. A failed or interrupted adoption can retry.
    /// </summary>
    public static ProfileLease Acquire(string profileDirectory)
    {
        string directory = NormalizeDirectory(profileDirectory);
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, LockName);

        FileStream stream;
        bool createdLock;
        try
        {
            try
            {
                stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                createdLock = true;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // CreateNew is the atomic provenance decision, never a prior existence sample.
                stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                createdLock = false;
            }
        }
        catch (IOException ex)
        {
            throw new IOException($"The profile '{directory}' could not be locked. Another MeowChan instance may already be using it.", ex);
        }

        try
        {
            bool wasEmpty = !Directory.EnumerateFileSystemEntries(directory)
                .Any(entry => !createdLock || Path.GetFileName(entry) != LockName);
            if (!HasProtocolMarker(directory))
            {
                if (HasLegacyData(directory))
                {
                    try
                    {
                        using FileStream legacy = new FileStream(
                            Path.Combine(directory, LegacyLockName),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        PublishProtocolMarker(directory);
                    }
                    catch (IOException ex)
                    {
                        throw new IOException(
                            $"The profile '{directory}' could not be adopted. An older app may still be using it, or the migration marker could not be written.", ex);
                    }
                }
                else
                {
                    PublishProtocolMarker(directory);
                }
            }

            return new ProfileLease(directory, stream, wasEmpty);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool HasProtocolMarker(string directory)
    {
        try
        {
            string value = File.ReadAllText(Path.Combine(directory, MarkerName), Encoding.ASCII);
            if (value != MarkerValue)
                throw new InvalidDataException("The MeowChan profile protocol marker is invalid.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static bool HasLegacyData(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            if (name != LockName && name != MarkerName &&
                !name.StartsWith(MarkerName + ".pending-", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void PublishProtocolMarker(string directory)
    {
        string pending = Path.Combine(directory, MarkerName + ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(MarkerValue);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            File.Move(pending, Path.Combine(directory, MarkerName));
        }
        finally
        {
            try
            {
                File.Delete(pending);
            }
            catch (FileNotFoundException)
            {
                // The pending file was already removed; the published marker stays intact.
            }
        }
    }

    internal void EnsureMatches(string profileDirectory)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(ProfileDirectory, NormalizeDirectory(profileDirectory), comparison))
            throw new ArgumentException("The profile lease belongs to a different directory.", nameof(profileDirectory));

        RunExclusive(() => true);
    }

    internal T RunExclusive<T>(Func<T> operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_lockStream == null, this);
            return operation();
        }
    }

    internal static string NormalizeDirectory(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _lockStream?.Dispose();
            _lockStream = null;
        }

        // Keep the lock file: deleting it can let another process lock a different inode.
    }
}
