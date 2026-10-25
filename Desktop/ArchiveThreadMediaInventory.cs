using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal sealed record ArchiveThreadMediaCount(int? SavedVersions, int MissingOrChangedFiles = 0, string? Error = null);

/// <summary>
/// Passive count of manifest-published versions whose regular file still has its recorded length.
/// This does not hash, open image data, infer loose-file ownership, repair storage or create locks.
/// </summary>
internal static class ArchiveThreadMediaInventory
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static async Task<ArchiveThreadMediaCount> ReadAsync(IEnumerable<string> directories,
        CancellationToken token = default, Func<string, CancellationToken, Task<MediaAssetSnapshot>>? open = null)
    {
        open ??= MediaAssetReader.OpenAsync;
        try
        {
            string[] paths = directories.Select(Canonical).Distinct(Paths).ToArray();
            if (paths.Length == 0) return new(null, Error: "No media storage location is recorded.");
            int count = 0, missing = 0;
            HashSet<string> counted = new(Paths);
            foreach (string directory in paths)
            {
                token.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = await open(directory, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (manifest.Status == MediaManifestStatus.Corrupt)
                    return new(null, Error: manifest.Warning ?? "The media manifest is invalid.");
                if (manifest.Status == MediaManifestStatus.Missing)
                {
                    // A readable empty directory, or a witnessed missing direct child of a readable
                    // parent, proves zero here. Missing parent/NAS, loose legacy files or access failure
                    // remain unknown; an empty work journal is never evidence of an empty archive.
                    if (!Directory.Exists(directory))
                    {
                        string parent = Path.GetDirectoryName(directory) ?? throw new IOException("The media directory has no readable parent.");
                        string name = Path.GetFileName(directory);
                        if (Directory.EnumerateFileSystemEntries(parent).Any(path => Paths.Equals(Path.GetFileName(path), name)))
                            return new(null, Error: "The media directory could not be read.");
                    }
                    else
                    {
                        EnsureDirectory(directory);
                        if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) is not (".meowchan-store.lock" or ".ochan-store.lock")))
                            return new(null, Error: "Files exist without a supported media manifest.");
                    }
                    continue;
                }
                EnsureDirectory(directory);
                // Ensure the directory itself is readable even when individual references are missing.
                _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToArray();
                for (int offset = 0; offset < manifest.Count; offset += 500)
                    foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!counted.Add(Canonical(asset.Path))) continue;
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(asset.Path);
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                                || new FileInfo(asset.Path).Length != asset.Length) { missing++; continue; }
                            count = checked(count + 1);
                        }
                        catch (FileNotFoundException) { missing++; }
                    }
            }
            return new(count, missing);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return new(null, Error: error.Message); }
    }

    private static void EnsureDirectory(string directory)
    {
        FileAttributes attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The media directory is unavailable or a symbolic link.");
    }
}
