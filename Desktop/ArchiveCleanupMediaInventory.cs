using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>Passive evidence only. Never creates a directory, lease file, manifest or recovery file.</summary>
internal static class ArchiveCleanupMediaInventory
{
    internal static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const string MissingMessage = "The media directory is missing and its storage location has not been witnessed.";
    private sealed record Entry(string Name, FileAttributes Attributes, long Length, long LastWriteTicks);

    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool IsLease(string name) => name is ".meowchan-store.lock" or ".ochan-store.lock";

    internal static string ExpectedThreadDirectory(string root, ResourceId id) => Path.GetDirectoryName(
        StorageLayout.GetPluginMediaPath(root, id.Source, id.Board, id.Thread!, "cleanup-proof", ".bin", "original"))!;

    internal static async Task<ArchiveCleanupDirectoryEvidence> ReadDirectoryAsync(string input, string profile,
        CancellationToken token = default)
    {
        string directory = input;
        try
        {
            directory = Canonical(input);
            string normalizedProfile = Canonical(profile);
            if (Paths.Equals(directory, normalizedProfile) || normalizedProfile.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison)
                || directory.StartsWith(normalizedProfile + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("Media inventory cannot include a profile directory or its ancestor.");
            EnsureSafeAncestors(directory);
            Entry[] before = ReadEntries(directory, token);
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
            bool current = before.Any(entry => entry.Name == ".meowchan-manifest.json");
            bool legacy = before.Any(entry => entry.Name == ".ochan-manifest.json");
            if (current && legacy) throw new InvalidDataException("Both media manifest formats are present.");
            if (manifest.Status == MediaManifestStatus.Corrupt)
                throw new InvalidDataException("The media manifest is corrupt.");
            if (manifest.Status == MediaManifestStatus.Missing && before.Length != 0)
                throw new InvalidDataException("Files exist without a supported media manifest.");
            List<MediaAssetReference> assets = new();
            for (int offset = 0; offset < manifest.Count; offset += 500)
            {
                token.ThrowIfCancellationRequested();
                foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                {
                    await using Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false);
                    assets.Add(asset);
                }
            }
            HashSet<string> owned = assets.Select(asset => Path.GetFileName(asset.Path)).ToHashSet(Paths);
            if (current) owned.Add(".meowchan-manifest.json");
            if (legacy) owned.Add(".ochan-manifest.json");
            if (before.Any(entry => (entry.Attributes & FileAttributes.Directory) != 0 || !owned.Contains(entry.Name)))
                throw new InvalidDataException("The directory contains unowned files, stages or subdirectories.");
            Entry[] after = ReadEntries(directory, token);
            if (!before.SequenceEqual(after)) throw new InvalidDataException("The media directory changed during inventory.");
            MediaAssetSnapshot final = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
            if (final.Generation != manifest.Generation || final.Status != manifest.Status)
                throw new InvalidDataException("The media manifest changed during inventory.");
            var frozen = Array.AsReadOnly(assets.ToArray());
            long bytes = assets.Sum(asset => asset.Length);
            return new(directory, manifest.Generation, frozen, bytes, false, null,
                ArchiveCleanupFingerprint.Of(new { directory, manifest.Generation, Entries = before, Assets = frozen }), null) { Manifest = manifest };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                string parent = ConfirmMissing(directory, token);
                return new(directory, null, Array.Empty<MediaAssetReference>(), 0, true, null, parent, MissingMessage);
            }
            catch (Exception unavailable) when (unavailable is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { return Unknown(directory, unavailable.Message); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        { return Unknown(directory, error.Message); }
    }

    internal static ArchiveCleanupMediaEvidence Combine(IReadOnlyList<ArchiveCleanupDirectoryEvidence> directories)
    {
        string? error = directories.Select(item => item.Error).FirstOrDefault(value => value != null);
        int? count = error == null ? directories.Sum(item => item.Assets.Count) : null;
        return new(count, directories.Sum(item => item.VerifiedBytes), directories,
            ArchiveCleanupFingerprint.Of(directories), error);
    }

    internal static ArchiveCleanupDirectoryEvidence WithMissingWitness(ArchiveCleanupDirectoryEvidence missing,
        ResourceId id, string outputRoot, IEnumerable<(ResourceId Thread, ArchiveCleanupDirectoryEvidence Directory)> witnesses,
        CancellationToken token)
    {
        if (!missing.Missing) return missing;
        try
        {
            if (!Paths.Equals(Canonical(ExpectedThreadDirectory(outputRoot, id)), missing.Directory)) return missing;
            string parent = Path.GetDirectoryName(missing.Directory)!;
            string parentFingerprint = ConfirmMissing(missing.Directory, token);
            if (parentFingerprint != missing.Fingerprint) return missing with { Error = "The media parent changed during inventory." };
            var candidates = witnesses.Where(witness => witness.Thread != id && witness.Directory.Error == null
                && witness.Directory.ManifestGeneration != null && witness.Directory.Assets.Count > 0
                && Paths.Equals(Path.GetDirectoryName(witness.Directory.Directory), parent)
                && Paths.Equals(Canonical(ExpectedThreadDirectory(outputRoot, witness.Thread)), witness.Directory.Directory))
                .OrderByDescending(witness => witness.Directory.Assets.Count).ThenBy(witness => witness.Directory.Directory, Paths).ToArray();
            if (candidates.Length == 0) return missing;
            var witness = candidates[0];
            ArchiveCleanupStorageAnchor anchor = new(witness.Thread, witness.Directory.Directory,
                witness.Directory.ManifestGeneration!, witness.Directory.Assets[0], parentFingerprint);
            return missing with { StorageAnchor = anchor, Error = null,
                Fingerprint = ArchiveCleanupFingerprint.Of(new { missing.Directory, Missing = true, anchor }) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return missing with { Error = error.Message }; }
    }

    private static ArchiveCleanupDirectoryEvidence Unknown(string directory, string error) =>
        new(directory, null, Array.Empty<MediaAssetReference>(), 0, false, null,
            ArchiveCleanupFingerprint.Of(new { directory, error }), error);

    private static string ConfirmMissing(string directory, CancellationToken token)
    {
        string parent = Path.GetDirectoryName(Canonical(directory)) ?? throw new InvalidDataException("A media directory has no parent.");
        EnsureSafeAncestors(parent);
        var entries = Directory.EnumerateFileSystemEntries(parent).Select(path =>
        {
            token.ThrowIfCancellationRequested();
            return new { Name = Path.GetFileName(path), Attributes = File.GetAttributes(path) };
        }).OrderBy(entry => entry.Name, Paths).ToArray();
        if (entries.Any(entry => Paths.Equals(entry.Name, Path.GetFileName(directory))))
            throw new InvalidDataException("The directory exists but could not be fully read.");
        return ArchiveCleanupFingerprint.Of(new { parent, entries });
    }

    private static Entry[] ReadEntries(string directory, CancellationToken token) => Directory.EnumerateFileSystemEntries(directory)
        .Select(path =>
        {
            token.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A media entry is a symbolic link or reparse point.");
            if (IsLease(Path.GetFileName(path)))
            {
                if ((attributes & FileAttributes.Directory) != 0) throw new InvalidDataException("A media lease must be a regular file.");
                return null;
            }
            FileInfo info = new(path);
            return new Entry(Path.GetFileName(path), attributes, (attributes & FileAttributes.Directory) == 0 ? info.Length : 0,
                info.LastWriteTimeUtc.Ticks);
        }).OfType<Entry>().OrderBy(entry => entry.Name, Paths).ToArray();

    private static void EnsureSafeAncestors(string path)
    {
        for (DirectoryInfo? current = new(path); current != null; current = current.Parent)
        {
            FileAttributes attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("A media ancestor must be a regular directory without links.");
        }
    }
}
