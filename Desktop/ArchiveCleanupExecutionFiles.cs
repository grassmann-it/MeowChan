using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal sealed record ArchiveCleanupFile(string Path, long Length, string Sha256, bool Manifest);
internal sealed record ArchiveCleanupDirectoryFiles(ArchiveCleanupDirectoryEvidence Evidence,
    IReadOnlyList<ArchiveCleanupFile> Files);

// Cleanup deliberately does not use the manual deletion helper's missing-directory/temporary-file rules.
internal static class ArchiveCleanupExecutionFiles
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const string Manifest = ".meowchan-manifest.json";
    private const string LegacyManifest = ".ochan-manifest.json";
    private const string Lease = ".meowchan-store.lock";
    private const string LegacyLease = ".ochan-store.lock";

    internal static async Task<IReadOnlyList<ArchiveCleanupDirectoryFiles>> PrepareAsync(
        ArchiveCleanupPreviewRow row, string profile, CancellationToken token)
    {
        List<ArchiveCleanupDirectoryFiles> plans = new();
        foreach (ArchiveCleanupDirectoryEvidence evidence in row.Media.Directories)
        {
            token.ThrowIfCancellationRequested();
            EnsureSafeDirectory(evidence.Directory, profile);
            List<ArchiveCleanupFile> files = new();
            if (evidence.Missing)
            {
                if (Directory.Exists(evidence.Directory) || File.Exists(evidence.Directory) || evidence.StorageAnchor == null)
                    throw new InvalidDataException("The missing-directory storage evidence changed.");
            }
            else
            {
                if (!Directory.Exists(evidence.Directory)) throw new InvalidDataException("A media directory is unavailable.");
                foreach (MediaAssetReference asset in evidence.Assets)
                {
                    if (!PathComparer.Equals(Path.GetDirectoryName(Path.GetFullPath(asset.Path)), Path.GetFullPath(evidence.Directory)))
                        throw new InvalidDataException("An asset path is outside its owned media directory.");
                    ArchiveCleanupFile file = new(asset.Path, asset.Length, asset.Sha256, false);
                    ArchiveCleanupFile? previous = files.FirstOrDefault(item => PathComparer.Equals(item.Path, file.Path));
                    if (previous != null && previous != file) throw new InvalidDataException("Conflicting physical asset ownership.");
                    if (previous == null) files.Add(file);
                }
                if (evidence.ManifestGeneration != null)
                {
                    string[] manifests = new[] { Path.Combine(evidence.Directory, Manifest), Path.Combine(evidence.Directory, LegacyManifest) }
                        .Where(File.Exists).ToArray();
                    if (manifests.Length != 1) throw new InvalidDataException("The media manifest ownership changed.");
                    files.Add(new(manifests[0], new FileInfo(manifests[0]).Length, evidence.ManifestGeneration, true));
                }
                ValidateEntries(evidence.Directory, files);
                foreach (ArchiveCleanupFile file in files) await VerifyAsync(file, token).ConfigureAwait(false);
            }
            plans.Add(new(evidence, Array.AsReadOnly(files.ToArray())));
        }
        return Array.AsReadOnly(plans.ToArray());
    }

    internal static async Task<IAsyncDisposable> AcquireAsync(IEnumerable<ArchiveCleanupDirectoryEvidence> evidence,
        string profile, CancellationToken token)
    {
        List<FileStream> handles = new();
        try
        {
            string[] directories = evidence.SelectMany(item => item.StorageAnchor == null
                    ? new[] { item.Directory } : new[] { item.Directory, item.StorageAnchor.Directory })
                .Select(Path.GetFullPath).Distinct(PathComparer).Order(PathComparer).ToArray();
            foreach (string directory in directories)
            {
                EnsureSafeDirectory(directory, profile);
                if (!Directory.Exists(directory)) continue; // Missing is revalidated by the same-board anchor collector.
                // Take both protocols even for empty directories: a legacy writer may start for the first time.
                string[] leaseNames = [Lease, LegacyLease];
                foreach (string leaseName in leaseNames)
                {
                    string path = Path.Combine(directory, leaseName);
                    if ((File.Exists(path) || Directory.Exists(path))
                        && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new InvalidDataException("The media lease path is a link or directory.");
                    token.ThrowIfCancellationRequested();
                    // Current then legacy lease matches MediaStore's adoption lock order.
                    // A busy writer is a changed prerequisite, never stopped or waited out.
                    handles.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
            }
            await Task.CompletedTask;
            return new DirectoryLeases(handles);
        }
        catch { foreach (FileStream handle in handles) handle.Dispose(); throw; }
    }

    internal static async Task DeleteAsync(IReadOnlyList<ArchiveCleanupDirectoryFiles> directories,
        string profile, Func<Task> revalidate, Action<long> committed, CancellationToken token)
    {
        foreach (ArchiveCleanupDirectoryFiles directory in directories)
        {
            EnsureSafeDirectory(directory.Evidence.Directory, profile);
            if (directory.Evidence.Missing)
            {
                if (Directory.Exists(directory.Evidence.Directory) || File.Exists(directory.Evidence.Directory))
                    throw new InvalidDataException("The previously missing media directory changed.");
                continue;
            }
            List<ArchiveCleanupFile> remaining = directory.Files.OrderBy(file => file.Manifest).ToList();
            foreach (ArchiveCleanupFile file in directory.Files.OrderBy(file => file.Manifest))
            {
                token.ThrowIfCancellationRequested();
                await revalidate().ConfigureAwait(false);
                EnsureSafeDirectory(directory.Evidence.Directory, profile);
                ValidateEntries(directory.Evidence.Directory, remaining);
                if (!file.Manifest && directory.Evidence.ManifestGeneration != null)
                {
                    ArchiveCleanupFile manifest = directory.Files.Single(item => item.Manifest);
                    await VerifyAsync(manifest, token).ConfigureAwait(false);
                }
                await VerifyAsync(file, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Delete(file.Path);
                committed(file.Length);
                remaining.Remove(file);
            }
        }
    }

    internal static void ValidateCompleted(IReadOnlyList<ArchiveCleanupDirectoryFiles> directories, string profile)
    {
        foreach (ArchiveCleanupDirectoryFiles directory in directories)
        {
            EnsureSafeDirectory(directory.Evidence.Directory, profile);
            if (directory.Evidence.Missing)
            {
                if (Directory.Exists(directory.Evidence.Directory) || File.Exists(directory.Evidence.Directory))
                    throw new InvalidDataException("The missing media directory changed before the catalog commit.");
            }
            else ValidateEntries(directory.Evidence.Directory, Array.Empty<ArchiveCleanupFile>());
        }
    }

    private static void ValidateEntries(string directory, IReadOnlyList<ArchiveCleanupFile> owned)
    {
        string[] entries = Directory.GetFileSystemEntries(directory);
        foreach (string path in entries)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("A media directory contains a link or foreign subdirectory.");
            if (Path.GetFileName(path) is Lease or LegacyLease) continue;
            if (!owned.Any(file => PathComparer.Equals(file.Path, path)))
                throw new InvalidDataException("A media directory contains a foreign or staged file.");
        }
        if (owned.Any(file => !entries.Contains(file.Path, PathComparer)))
            throw new InvalidDataException("An owned media file is no longer available.");
    }

    private static async Task VerifyAsync(ArchiveCleanupFile file, CancellationToken token)
    {
        if ((File.GetAttributes(file.Path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("An owned media file is a link or directory.");
        await using FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != file.Length || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)
            .ConfigureAwait(false)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Owned media bytes changed after confirmation.");
    }

    private static void EnsureSafeDirectory(string directory, string profile)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
        string profilePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        if (string.Equals(full, root, PathComparison) || string.Equals(full, profilePath, PathComparison)
            || profilePath.StartsWith(full + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("A media directory cannot be a root, profile, or profile ancestor.");
        for (string? cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A media directory path contains a link.");
    }

    private sealed class DirectoryLeases(List<FileStream> handles) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (FileStream handle in handles) handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
