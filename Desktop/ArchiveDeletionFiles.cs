using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>A frozen, reviewable inventory of media files belonging to selected archived threads.</summary>
internal sealed class ArchiveFilesPlan
{
    internal ArchiveFilesPlan(string profileDirectory, IReadOnlyList<ArchiveDirectoryPlan> directories)
        => (ProfileDirectory, Directories) = (profileDirectory, directories);
    public string ProfileDirectory { get; }
    public IReadOnlyList<ArchiveDirectoryPlan> Directories { get; }
    public int OwnedFileCount => Directories.Sum(directory => directory.OwnedFileCount);
    public long OwnedBytes => Directories.Sum(directory => directory.OwnedBytes);
    public bool HasBlockers => Directories.Any(directory => directory.Blocker != null);
}

internal sealed class ArchiveDirectoryPlan
{
    internal ArchiveDirectoryPlan(string directory, string? generation, IReadOnlyList<ArchiveOwnedFile> files,
        IReadOnlyList<string> leftovers, string? blocker, IReadOnlyList<string>? assetSha256 = null)
        => (Directory, Generation, Files, Leftovers, Blocker, AssetSha256) =
            (directory, generation, files, leftovers, blocker, assetSha256 ?? Array.Empty<string>());
    public string Directory { get; }
    internal string? Generation { get; }
    internal IReadOnlyList<ArchiveOwnedFile> Files { get; }
    public IReadOnlyList<string> Leftovers { get; }
    public string? Blocker { get; }
    public IReadOnlyList<string> AssetSha256 { get; }
    public int OwnedFileCount => Files.Count;
    public long OwnedBytes => Files.Sum(file => file.Length);
}

internal sealed record ArchiveOwnedFile(string Name, long Length, DateTime LastWriteUtc);

internal sealed record ArchiveDirectoryResult(string Directory, int DeletedFileCount, long DeletedBytes,
    IReadOnlyList<string> Leftovers, string? Error);

internal sealed class ArchiveFilesResult
{
    internal ArchiveFilesResult(IReadOnlyList<ArchiveDirectoryResult> directories) => Directories = directories;
    public IReadOnlyList<ArchiveDirectoryResult> Directories { get; }
    public int DeletedFileCount => Directories.Sum(directory => directory.DeletedFileCount);
    public long DeletedBytes => Directories.Sum(directory => directory.DeletedBytes);
    public bool HasErrors => Directories.Any(directory => directory.Error != null);
}

/// <summary>Deletes only manifest-owned media and exact MeowChan housekeeping names; never descends into a directory.</summary>
internal static class ArchiveDeletionFiles
{
    private const string CurrentManifest = ".meowchan-manifest.json";
    private const string LegacyManifest = ".ochan-manifest.json";
    private const string LeaseName = ".meowchan-store.lock";
    private const string LegacyLeaseName = ".ochan-store.lock";
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task<ArchiveFilesPlan> PrepareAsync(IEnumerable<string> mediaDirectories,
        string profileDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
        HashSet<string> seen = new(PathComparer);
        List<ArchiveDirectoryPlan> plans = new();
        foreach (string input in mediaDirectories.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory;
            try { directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                plans.Add(new ArchiveDirectoryPlan(input, null, Array.Empty<ArchiveOwnedFile>(),
                    Array.Empty<string>(), exception.Message));
                continue;
            }
            if (seen.Add(directory))
                plans.Add(await PrepareDirectoryAsync(directory, profile, cancellationToken).ConfigureAwait(false));
        }
        return new ArchiveFilesPlan(profile, new ReadOnlyCollection<ArchiveDirectoryPlan>(plans));
    }

    public static async Task<ArchiveFilesResult> DeleteAsync(ArchiveFilesPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<ArchiveDirectoryResult> results = new();
        foreach (ArchiveDirectoryPlan directoryPlan in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = directoryPlan.Directory;
            if (directoryPlan.Blocker != null)
            {
                results.Add(new(directory, 0, 0, directoryPlan.Leftovers, directoryPlan.Blocker));
                continue;
            }
            if (!Directory.Exists(directory))
            {
                results.Add(new(directory, 0, 0, Array.Empty<string>(), null));
                continue;
            }
            try
            {
                EnsureSafeDirectory(directory, plan.ProfileDirectory);
                using FileStream lease = await AcquireLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
                ArchiveDirectoryPlan fresh = await PrepareDirectoryAsync(directory, plan.ProfileDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (fresh.Blocker != null || fresh.Generation != directoryPlan.Generation ||
                    !fresh.Files.SequenceEqual(directoryPlan.Files))
                {
                    results.Add(new(directory, 0, 0, fresh.Leftovers,
                        fresh.Blocker ?? "The media manifest or owned files changed after confirmation."));
                    continue;
                }
                int count = 0;
                long bytes = 0;
                string? error = null;
                // Publishable media goes first. Keep the manifest if any of those deletions fails.
                foreach (ArchiveOwnedFile file in directoryPlan.Files.Where(file => !IsHousekeeping(file.Name)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        DeleteChecked(directory, file);
                        count++; bytes += file.Length;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    { error = exception.Message; break; }
                }
                if (error == null)
                {
                    foreach (ArchiveOwnedFile file in directoryPlan.Files.Where(file => IsHousekeeping(file.Name)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try { DeleteChecked(directory, file); count++; bytes += file.Length; }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                        { error = exception.Message; break; }
                    }
                }
                // The live lease file must remain until its handle closes. It may leave a tiny
                // directory behind; it is never removed while another process may be waiting.
                results.Add(new(directory, count, bytes, fresh.Leftovers, error));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                results.Add(new(directory, 0, 0, directoryPlan.Leftovers, exception.Message));
            }
        }
        return new ArchiveFilesResult(new ReadOnlyCollection<ArchiveDirectoryResult>(results));
    }

    private static async Task<ArchiveDirectoryPlan> PrepareDirectoryAsync(string directory, string profile,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureSafeDirectory(directory, profile);
            if (!Directory.Exists(directory))
                return new(directory, null, Array.Empty<ArchiveOwnedFile>(), Array.Empty<string>(), null);
            string[] paths = Directory.EnumerateFileSystemEntries(directory).ToArray();
            foreach (string path in paths)
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return new(directory, null, Array.Empty<ArchiveOwnedFile>(), Array.Empty<string>(),
                        "A media directory entry is a symbolic link or reparse point.");
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
            string[] names = paths.Select(Path.GetFileName).ToArray()!;
            bool hasCurrent = names.Contains(CurrentManifest, PathComparer);
            bool hasLegacy = names.Contains(LegacyManifest, PathComparer);
            if (hasCurrent && hasLegacy)
                return new(directory, null, Array.Empty<ArchiveOwnedFile>(), names, "Both media manifest formats are present.");
            if (snapshot.Status != MediaManifestStatus.Available)
            {
                string? blocker = paths.Any(path => !IsLease(Path.GetFileName(path)))
                    ? "The media manifest is missing or invalid; directory ownership cannot be verified." : null;
                return new(directory, null, Array.Empty<ArchiveOwnedFile>(), names, blocker);
            }
            HashSet<string> owned = new(PathComparer);
            HashSet<string> hashes = new(StringComparer.Ordinal);
            for (int offset = 0; offset < snapshot.Count; offset += 500)
                foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                {
                    owned.Add(Path.GetFileName(asset.Path));
                    if (asset.Sha256.Length == 64 && asset.Sha256.All(char.IsAsciiHexDigit))
                        hashes.Add(asset.Sha256.ToLowerInvariant());
                }
            owned.Add(hasCurrent ? CurrentManifest : LegacyManifest);
            foreach (string name in names)
                if (IsKnownTemporary(name)) owned.Add(name);
            List<ArchiveOwnedFile> files = new();
            List<string> leftovers = new();
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (IsLease(name)) continue;
                if (!owned.Contains(name) || Directory.Exists(path))
                { leftovers.Add(name); continue; }
                FileInfo info = new(path);
                files.Add(new(name, info.Length, info.LastWriteTimeUtc));
            }
            return new(directory, snapshot.Generation, new ReadOnlyCollection<ArchiveOwnedFile>(files),
                new ReadOnlyCollection<string>(leftovers), null,
                new ReadOnlyCollection<string>(hashes.OrderBy(hash => hash, StringComparer.Ordinal).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        { return new(directory, null, Array.Empty<ArchiveOwnedFile>(), Array.Empty<string>(), exception.Message); }
    }

    private static void DeleteChecked(string directory, ArchiveOwnedFile file)
    {
        string path = Path.Combine(directory, file.Name);
        FileInfo current = new(path);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
            current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteUtc)
            throw new InvalidDataException($"The media file '{file.Name}' changed after confirmation.");
        File.Delete(path);
    }

    private static void EnsureSafeDirectory(string directory, string profile)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full)!;
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            || string.Equals(full, profile, PathComparison)
            || profile.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("A media directory cannot be a filesystem root, profile, or profile ancestor.");
        string? cursor = full;
        while (cursor != null)
        {
            if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A media directory path contains a symbolic link or reparse point.");
            string? parent = Path.GetDirectoryName(cursor);
            if (parent == cursor) break;
            cursor = parent;
        }
    }

    private static bool IsLease(string name) => name == LeaseName || name == LegacyLeaseName;
    private static bool IsHousekeeping(string name) => name is CurrentManifest or LegacyManifest || IsKnownTemporary(name);
    private static bool IsKnownTemporary(string name)
        => HasGuidTemporary(name, ".meowchan-media-") || HasGuidTemporary(name, ".meowchan-manifest-");
    private static bool HasGuidTemporary(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(prefix.Length, name.Length - prefix.Length - 5), "N", out _);

    private static async Task<FileStream> AcquireLeaseAsync(string directory, CancellationToken token)
    {
        string path = Path.Combine(directory, LeaseName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The media lease path is a symbolic link.");
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (elapsed.Elapsed < TimeSpan.FromSeconds(30) &&
                (exception.HResult == unchecked((int)0x80070020) ||
                 OperatingSystem.IsLinux() && exception.HResult == 11 ||
                 OperatingSystem.IsMacOS() && exception.HResult == 35))
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
}
