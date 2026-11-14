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
    private readonly ProfileDirectoryIdentity? _directoryIdentity;
    private readonly ProfileAdmission _admission;
    private object? _startupOwner;

    public string ProfileDirectory { get; }
    // Captured under the exclusive lease, before this acquisition publishes protocol traces.
    internal bool WasEmptyAtAcquisition { get; }

    // One cooperative admission domain belongs to exactly one lease lifetime.
    // It is intentionally internal: the kernel is not a public import/apply API.
    internal ProfileAdmission Admission => _admission;

    /// <summary>Write limits of the profile's private lists (user settings); the defaults are the former fixed values.</summary>
    internal ProfileStoreLimits StoreLimits { get; } = new();

    private ProfileLease(string profileDirectory, FileStream lockStream, bool wasEmpty)
    {
        ProfileDirectory = profileDirectory;
        _lockStream = lockStream;
        WasEmptyAtAcquisition = wasEmpty;
        _directoryIdentity = ProfileDirectoryIdentity.TryCapture(profileDirectory);
        _admission = new ProfileAdmission();
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

    /// <summary>
    /// Whether an owner (another window of this app, or this one) holds the lease of <paramref name="profileDirectory"/>
    /// right now. A probe only: it never creates the folder, the lock file or the marker, and a folder without a lock file
    /// is not in use. An unreadable lock counts as in use, so a caller never starts a second owner on a guess.
    /// </summary>
    internal static bool IsInUse(string profileDirectory)
    {
        string lockPath = Path.Combine(NormalizeDirectory(profileDirectory), LockName);
        try
        {
            using FileStream probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
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

    // Single coordinator per lease lifetime; a claim supplies no writer authority.
    // Keep failed claims owned so another Registry cannot resume partial startup.
    internal void ClaimStartupOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RunExclusive(() =>
        {
            if (_admission.Phase != ProfileAdmissionPhase.Starting)
                throw new InvalidOperationException("The profile is not starting.");
            if (_startupOwner != null && !ReferenceEquals(_startupOwner, owner))
                throw new InvalidOperationException("A different session owns profile startup.");
            _startupOwner = owner;
            return true;
        });
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

    // Stricter admission for the private journal. Ordinary store compatibility is
    // unchanged; this fence is in-process, not a persistent profile incarnation.
    internal void EnsureDirectoryIdentity() => RunExclusive(() =>
    {
        if (_directoryIdentity is null)
            throw new InvalidDataException("The leased profile directory identity is unavailable.");
        _directoryIdentity.EnsureMatches(ProfileDirectory);
        return true;
    });

    // Pin before waiting on the lease monitor, and release only after I/O returns.
    // The token authorizes the role; RunExclusive still verifies the live OS lease.
    internal T RunStoreWrite<T>(ProfileAdmissionToken admission, Func<T> operation) =>
        _admission.RunWithToken(admission, () => RunExclusive(operation),
            ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary);

    internal T RunPrivateStoreCommit<T>(ProfileAdmissionToken admission, Func<T> operation) =>
        _admission.RunWithToken(admission, () => RunExclusive(operation),
            ProfileAdmissionKind.Startup, ProfileAdmissionKind.Exclusive);

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
        // Seal new cooperative entries before releasing the OS lock. Existing
        // accepted entries are drained by their owners; Dispose stays synchronous
        // and never waits while holding the lease monitor.
        _admission.Close();

        lock (_sync)
        {
            _lockStream?.Dispose();
            _lockStream = null;
        }

        // Keep the lock file: deleting it can let another process lock a different inode.
    }
}
