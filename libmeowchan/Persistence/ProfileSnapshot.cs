using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal sealed record ProfileSnapshotFile(string Name, long Length, string Sha256);
internal sealed record ProfileExternalMedia(string Root, ProfileSnapshotFile[] Files, string[]? Directories = null);

internal sealed record ProfileSnapshot(int Version, string SourceDirectory, DateTimeOffset CreatedUtc,
    ProfileManifest Profile, string[] Directories, ProfileSnapshotFile[] Files, ProfileExternalMedia? ExternalMedia = null,
    string? SourceIdentity = null, string? LegacyDownloads = null, ProfileExternalMedia[]? ManagedMedia = null)
{
    internal const string FileName = "snapshot.json";
    internal const string DataName = "data";
    internal const int UpgradeVersion = 2;
    private const int SnapshotVersion = 1;
    private const string MediaName = "media";
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false };

    internal static ProfileSnapshot Capture(ProfileLease source, string destination, ProfileManifest profile,
        Func<string, long>? availableBytes = null, CancellationToken token = default)
    {
        return source.RunExclusive(() =>
        {
            source.EnsureDirectoryIdentity();
            string root = source.ProfileDirectory;
            var (directories, copied) = CaptureTree(root, Path.Combine(destination, DataName), availableBytes, token);
            source.EnsureDirectoryIdentity();
            return new ProfileSnapshot(SnapshotVersion, root, DateTimeOffset.UtcNow, profile, directories, copied);
        });
    }

    private static (string[] Directories, ProfileSnapshotFile[] Files) CaptureTree(string root, string destination,
        Func<string, long>? availableBytes, CancellationToken token)
    {
        var (directories, files) = Inventory(root);
        long bytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        if ((availableBytes ?? FreeSpace)(Path.GetDirectoryName(destination)!) / 2 < bytes)
            throw new IOException("There is insufficient space for the profile snapshot and its trial restore.");

        Directory.CreateDirectory(destination);
        foreach (string directory in directories)
            Directory.CreateDirectory(Child(destination, directory));
        var copied = new ProfileSnapshotFile[files.Count];
        for (int index = 0; index < files.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[index];
            string hash = CopyFile(Child(root, file.Name), Child(destination, file.Name), file.Length);
            copied[index] = new(file.Name, file.Length, hash);
        }
        VerifyTree(root, directories, copied, token);
        return (directories, copied);
    }

    internal static ProfileExternalMedia[] CaptureMedia(string snapshotDirectory, string[] roots,
        Func<string, long>? availableBytes, CancellationToken token)
    {
        if (roots.Length == 0) return [];
        Directory.CreateDirectory(Path.Combine(snapshotDirectory, MediaName));
        var media = new ProfileExternalMedia[roots.Length];
        for (int index = 0; index < roots.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string root = roots[index];
            string destination = MediaTree(snapshotDirectory, index);
            ArchiveExportMetadataFile.RejectLinks(root);
            if (MediaRootExists(root))
            {
                var (directories, files) = CaptureTree(root, destination, availableBytes, token);
                media[index] = new(root, files, directories);
            }
            else
            {
                Directory.CreateDirectory(destination);
                media[index] = new(root, [], []);
            }
        }
        return media;
    }

    private static bool MediaRootExists(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("A managed media root is a file.");
        return true;
    }

    private static string MediaTree(string snapshotDirectory, int index) =>
        Path.Combine(snapshotDirectory, MediaName, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal void Write(string directory) => PublishJson(Path.Combine(directory, FileName), this, Json);

    internal static ProfileSnapshot Read(string directory)
    {
        ArchiveExportMetadataFile.RejectLinks(directory);
        using FileStream file = RegularProfileFile.OpenRead(Path.Combine(directory, FileName));
        ProfileSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<ProfileSnapshot>(file, Json); }
        catch (JsonException error) { throw new InvalidDataException("The profile snapshot index is damaged.", error); }
        if (snapshot is null || snapshot.Version is not (SnapshotVersion or UpgradeVersion) || string.IsNullOrWhiteSpace(snapshot.SourceDirectory)
            || !Path.IsPathFullyQualified(snapshot.SourceDirectory)
            || snapshot.Profile is null || snapshot.Profile.ProfileId == Guid.Empty || snapshot.Profile.Generation == Guid.Empty
            || snapshot.Directories is null || snapshot.Files is null
            || !ValidFiles(snapshot.Files)
            || snapshot.ExternalMedia is { } external && !ValidMedia(external)
            || snapshot.Version == UpgradeVersion && (snapshot.ExternalMedia is not null || string.IsNullOrWhiteSpace(snapshot.SourceIdentity)
                || snapshot.LegacyDownloads is null || !Path.IsPathFullyQualified(snapshot.LegacyDownloads)
                || snapshot.ManagedMedia is null || snapshot.ManagedMedia.Any(media => !ValidMedia(media) || media.Directories is null)))
            throw new InvalidDataException("The profile snapshot index is invalid.");
        return snapshot;
    }

    private static bool ValidFiles(ProfileSnapshotFile[]? files) =>
        files is not null && files.All(entry => entry is not null && entry.Length >= 0
            && entry.Sha256 is { Length: 64 } && entry.Sha256.All(Uri.IsHexDigit));

    private static bool ValidMedia(ProfileExternalMedia? media) => media is not null
        && !string.IsNullOrWhiteSpace(media.Root) && Path.IsPathFullyQualified(media.Root) && ValidFiles(media.Files);

    internal void Restore(string snapshotDirectory, string destination, CancellationToken token = default)
    {
        string target = ProfileLease.NormalizeDirectory(destination);
        ArchiveExportMetadataFile.RejectLinks(target);
        string? generation = Path.GetDirectoryName(ProfileLease.NormalizeDirectory(snapshotDirectory));
        if (Overlaps(SourceDirectory, target) || generation is not null && Overlaps(generation, target)
            || ManagedMedia?.Any(media => Overlaps(media.Root, target)) == true)
            throw new IOException("Restore the snapshot outside the original and working profile directories.");
        if (Version == SnapshotVersion) VerifyExternalMedia(snapshotDirectory, token);
        RestoreData(snapshotDirectory, target, token, rebindPaths: Version == UpgradeVersion);
    }

    internal void RestoreData(string snapshotDirectory, string target, CancellationToken token, bool rebindPaths = false)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The profile restore requires a fresh directory.");
        string staging = target + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(staging, snapshotDirectory);
            CopyTree(Path.Combine(snapshotDirectory, DataName), staging, Directories, Files, token);
            if (rebindPaths) ProfileMediaRelocation.Apply(this, snapshotDirectory, staging, target, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal void CopyMedia(string snapshotDirectory, int index, string destination, CancellationToken token)
    {
        ProfileExternalMedia media = ManagedMedia![index];
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidDataException("The staged profile already contains the designated media directory.");
        CopyTree(MediaTree(snapshotDirectory, index), destination, media.Directories!, media.Files, token);
    }

    private static void CopyTree(string source, string destination, string[] directories, ProfileSnapshotFile[] files, CancellationToken token)
    {
        VerifyInventory(source, directories, files);
        Directory.CreateDirectory(destination);
        if (FreeSpace(destination) < files.Aggregate(0L, (sum, file) => checked(sum + file.Length)))
            throw new IOException("There is insufficient space to restore the profile snapshot.");
        foreach (string directory in directories)
            Directory.CreateDirectory(Child(destination, directory));
        foreach (ProfileSnapshotFile file in files)
        {
            token.ThrowIfCancellationRequested();
            string hash = CopyFile(Child(source, file.Name), Child(destination, file.Name), file.Length);
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The profile snapshot checksum failed for '{file.Name}'.");
        }
        VerifyInventory(destination, directories, files);
    }

    private static bool Overlaps(string first, string second)
    {
        static bool Contains(string parent, string child)
        {
            string relative = Path.GetRelativePath(parent, child);
            return !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        return Contains(first, second) || Contains(second, first);
    }

    internal void VerifyData(string directory, CancellationToken token = default) =>
        VerifyTree(Path.Combine(directory, DataName), Directories, Files, token);

    private static void VerifyTree(string directory, string[] directories, ProfileSnapshotFile[] files, CancellationToken token)
    {
        VerifyInventory(directory, directories, files);
        VerifyFiles(directory, files, token);
    }

    private static void VerifyInventory(string directory, string[] directories, ProfileSnapshotFile[] files)
    {
        var inventory = Inventory(directory);
        if (!directories.SequenceEqual(inventory.Directories)
            || !files.Select(file => (file.Name, file.Length)).SequenceEqual(inventory.Files))
            throw new InvalidDataException("The profile snapshot inventory changed during verification.");
    }

    internal void VerifyExternalMedia(string snapshotDirectory, CancellationToken token = default)
    {
        if (ExternalMedia is { } old) VerifyFiles(old.Root, old.Files, token);
        if (ManagedMedia is not { } managed) return;
        for (int index = 0; index < managed.Length; index++)
        {
            ProfileExternalMedia media = managed[index];
            VerifyTree(MediaTree(snapshotDirectory, index), media.Directories!, media.Files, token);
        }
    }

    private static void VerifyFiles(string directory, ProfileSnapshotFile[] files, CancellationToken token)
    {
        foreach (ProfileSnapshotFile file in files)
        {
            token.ThrowIfCancellationRequested();
            using FileStream stream = RegularProfileFile.OpenRead(Child(directory, file.Name));
            if (stream.Length != file.Length ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The profile snapshot checksum failed for '{file.Name}'.");
        }
    }

    internal static (string[] Directories, List<(string Name, long Length)> Files) Inventory(string root)
    {
        ArchiveExportMetadataFile.RejectLinks(root);
        List<string> directories = new();
        List<(string Name, long Length)> files = new();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetRelativePath(root, entry);
                if (name is ".meowchan-profile.lock" or ".ochan-profile.lock") continue;
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The profile snapshot contains a linked entry '{name}'.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(name);
                    pending.Push(entry);
                    continue;
                }
                using FileStream stream = RegularProfileFile.OpenRead(entry);
                files.Add((name, stream.Length));
            }
        }
        directories.Sort(StringComparer.Ordinal);
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return (directories.ToArray(), files);
    }

    internal static string Child(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Split(Separators).Any(
            part => part is "" or "." or ".." || part.IndexOfAny(InvalidNameCharacters) >= 0
                || OperatingSystem.IsWindows() && part[^1] is '.' or ' '))
            throw new InvalidDataException("The profile snapshot index contains an unsafe path.");
        string path = Path.Combine(directory, name);
        ArchiveExportMetadataFile.RejectLinks(path);
        return path;
    }

    internal static string CopyFile(string source, string destination, long length)
    {
        using FileStream input = RegularProfileFile.OpenRead(source);
        if (input.Length != length) throw new InvalidDataException("A profile file changed length during the snapshot.");
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        input.CopyTo(output);
        if (output.Length != length) throw new InvalidDataException("A profile file changed length during the snapshot.");
        output.Flush(flushToDisk: true);
        output.Position = 0;
        return Convert.ToHexString(SHA256.HashData(output));
    }

    internal static long FreeSpace(string path) => new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;

    internal static void CreatePrivateDirectory(string path, string? source = null)
    {
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else if (source is null) WindowsProfileCopyAccess.Instance.CreateProtectedDirectory(path);
        else ProfileCopyAccess.CreateVerified(source, path, WindowsProfileCopyAccess.Instance);
    }

    internal static void PublishJson<T>(string path, T value, JsonSerializerOptions options)
    {
        string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, options);
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally { File.Delete(pending); }
    }
}
