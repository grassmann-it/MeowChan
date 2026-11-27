using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal sealed record ProfileSnapshotFile(string Name, long Length, string Sha256, DateTime? LastWriteUtc = null);
internal sealed record ProfileExternalMedia(string Root, ProfileSnapshotFile[] Files, string[]? Directories = null,
    string[]? SelectedNames = null);

internal sealed record ProfileMediaSelection(string Root, string[]? Names = null);

internal readonly record struct ProfileCopyOperation(Action<string, long>? Progress, CancellationToken Token)
{
    internal void Report(string path, long bytes = 0)
    {
        Progress?.Invoke(path, bytes);
        Token.ThrowIfCancellationRequested();
    }

    internal void Verify(string path, ProfileSnapshotFile file)
    {
        FileStream? input = null;
        try
        {
            input = RegularProfileFile.OpenRead(path);
            if (input.Length != file.Length || !string.Equals(Hash(path, input), file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The profile snapshot checksum failed for '{file.Name}'.");
        }
        finally { input?.Dispose(); }
    }

    private string Hash(string path, FileStream input)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Transfer(path, input, hash, null);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal string Copy(string source, string destination, long length)
    {
        using FileStream input = RegularProfileFile.OpenRead(source);
        if (input.Length != length) throw new InvalidDataException("A profile file changed length during the snapshot.");
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Transfer(source, input, null, output);
        if (output.Length != length) throw new InvalidDataException("A profile file changed length during the snapshot.");
        output.Flush(flushToDisk: true);
        output.Position = 0;
        return Hash(destination, output);
    }

    private void Transfer(string path, FileStream input, IncrementalHash? hash, FileStream? output)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            Report(path);
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                hash?.AppendData(buffer, 0, count);
                output?.Write(buffer, 0, count);
                Report(path, input.Position);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

internal sealed record ProfileSnapshot(int Version, string SourceDirectory, DateTimeOffset CreatedUtc,
    ProfileManifest Profile, string[] Directories, ProfileSnapshotFile[] Files, ProfileExternalMedia? ExternalMedia = null,
    string? LegacyDownloads = null, ProfileExternalMedia[]? ManagedMedia = null)
{
    private readonly record struct InventoryFile(string Name, long Length, DateTime LastWriteUtc);
    internal const string FileName = "snapshot.json";
    internal const string DataName = "data";
    internal const int UpgradeVersion = 2;
    private const int SnapshotVersion = 1;
    private const string MediaName = "media";
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false };

    internal static ProfileSnapshot CaptureUpgrade(ProfileLease source, string destination, ProfileManifest profile,
        string legacyDownloads, Func<string, long>? availableBytes, ProfileCopyOperation operation)
    {
        return source.RunExclusive(() =>
        {
            source.EnsureDirectoryIdentity();
            string root = source.ProfileDirectory;
            var profileTree = Inventory(root, operation);
            ProfileMediaSelection[] selections = ProfileMediaRelocation.ManagedRoots(root, legacyDownloads, operation.Token);
            var mediaTrees = selections.Select(selection => InventoryMedia(selection, operation)).ToArray();
            long bytes = profileTree.Files.Sum(file => file.Length);
            foreach (var tree in mediaTrees) bytes = checked(bytes + tree.Files.Sum(file => file.Length));
            if ((availableBytes ?? FreeSpace)(destination) / 2 < bytes)
                throw new IOException("There is insufficient space for the profile snapshot and its working copy.");

            ProfileSnapshotFile[] copied = CaptureTree(root, Path.Combine(destination, DataName), profileTree, operation);
            var media = new ProfileExternalMedia[selections.Length];
            for (int index = 0; index < selections.Length; index++)
            {
                var tree = mediaTrees[index];
                var selection = selections[index];
                var files = CaptureTree(selection.Root, MediaTree(destination, index), tree, operation);
                media[index] = new(selection.Root, files, tree.Directories, selection.Names);
                var current = InventoryMedia(selection, operation);
                if (!tree.Directories.SequenceEqual(current.Directories) || !tree.Files.SequenceEqual(current.Files))
                    throw new InvalidDataException("The media inventory changed during the snapshot.");
            }
            VerifyInventory(root, profileTree.Directories, copied, operation);
            source.EnsureDirectoryIdentity();
            return new ProfileSnapshot(UpgradeVersion, root, DateTimeOffset.UtcNow, profile, profileTree.Directories,
                copied, LegacyDownloads: legacyDownloads, ManagedMedia: media);
        });
    }

    private static (string[] Directories, List<InventoryFile> Files) InventoryMedia(
        ProfileMediaSelection selection, ProfileCopyOperation operation)
    {
        ArchiveExportMetadataFile.RejectLinks(selection.Root);
        if (!MediaRootExists(selection.Root)) return ([], []);
        if (selection.Names is null) return Inventory(selection.Root, operation);
        List<InventoryFile> files = [];
        foreach (string name in selection.Names)
        {
            string path = Child(selection.Root, name);
            operation.Report(path);
            try
            {
                using FileStream stream = RegularProfileFile.OpenRead(path);
                files.Add(new(name, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle)));
            }
            catch (FileNotFoundException) { } // A planned download or optional manifest can be absent.
        }
        return ([], files);
    }

    private static ProfileSnapshotFile[] CaptureTree(string root, string destination,
        (string[] Directories, List<InventoryFile> Files) tree, ProfileCopyOperation operation)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in tree.Directories)
        {
            operation.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Child(destination, directory));
        }
        var copied = new ProfileSnapshotFile[tree.Files.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            var (name, length, lastWriteUtc) = tree.Files[index];
            string hash = operation.Copy(Child(root, name), Child(destination, name), length);
            copied[index] = new(name, length, hash, lastWriteUtc);
        }
        VerifyFiles(root, copied, operation);
        return copied;
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
            || snapshot.Version == UpgradeVersion && (snapshot.ExternalMedia is not null
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

    internal void Restore(string snapshotDirectory, string destination, Action<string, long>? progress = null, CancellationToken token = default)
    {
        string target = ProfileLease.NormalizeDirectory(destination);
        ArchiveExportMetadataFile.RejectLinks(target);
        string? generation = Path.GetDirectoryName(ProfileLease.NormalizeDirectory(snapshotDirectory));
        if (Overlaps(SourceDirectory, target) || generation is not null && Overlaps(generation, target)
            || ManagedMedia?.Any(media => media.SelectedNames is null ? Overlaps(media.Root, target)
                : media.SelectedNames.Any(name => Overlaps(Child(media.Root, name), target))) == true)
            throw new IOException("Restore the snapshot outside the original and working profile directories.");
        ProfileCopyOperation operation = new(progress, token);
        long bytes = Files.Sum(file => file.Length);
        foreach (var media in ManagedMedia ?? []) bytes = checked(bytes + media.Files.Sum(file => file.Length));
        if (FreeSpace(Path.GetDirectoryName(target)!) < bytes)
            throw new IOException("There is insufficient space to restore the profile snapshot.");
        if (Version == SnapshotVersion) VerifyExternalMedia(snapshotDirectory, operation);
        RestoreData(snapshotDirectory, target, operation, rebindPaths: Version == UpgradeVersion);
    }

    internal void RestoreData(string snapshotDirectory, string target, ProfileCopyOperation operation, bool rebindPaths = false)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The profile restore requires a fresh directory.");
        string staging = target + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(staging, snapshotDirectory);
            CopyTree(Path.Combine(snapshotDirectory, DataName), staging, Directories, Files, operation);
            if (rebindPaths) ProfileMediaRelocation.Apply(this, snapshotDirectory, staging, target, operation, restoring: true);
            operation.Token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal void CopyMedia(string snapshotDirectory, int index, string destination, ProfileCopyOperation operation)
    {
        ProfileExternalMedia media = ManagedMedia![index];
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidDataException("The staged profile already contains the designated media directory.");
        CopyTree(MediaTree(snapshotDirectory, index), destination, media.Directories!, media.Files, operation);
    }

    private static void CopyTree(string source, string destination, string[] directories, ProfileSnapshotFile[] files, ProfileCopyOperation operation)
    {
        VerifyInventory(source, directories, files, operation);
        Directory.CreateDirectory(destination);
        if (FreeSpace(destination) < files.Aggregate(0L, (sum, file) => checked(sum + file.Length)))
            throw new IOException("There is insufficient space to restore the profile snapshot.");
        foreach (string directory in directories)
        {
            operation.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Child(destination, directory));
        }
        foreach (ProfileSnapshotFile file in files)
        {
            operation.Token.ThrowIfCancellationRequested();
            string hash = operation.Copy(Child(source, file.Name), Child(destination, file.Name), file.Length);
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The profile snapshot checksum failed for '{file.Name}'.");
        }
        VerifyInventory(destination, directories, files, operation);
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

    internal bool LegacyInventoryMatches(string directory, CancellationToken token)
    {
        var inventory = Inventory(directory, new(null, token));
        // Older snapshots recorded only their completion time. For those, a newer timestamp is
        // evidence of a change. New snapshots can also detect a restored or backdated timestamp.
        return Directories.SequenceEqual(inventory.Directories) && Files.Length == inventory.Files.Count
            && Files.Zip(inventory.Files).All(pair => pair.First.Name == pair.Second.Name && pair.First.Length == pair.Second.Length
                && (pair.First.LastWriteUtc is { } saved ? saved == pair.Second.LastWriteUtc : pair.Second.LastWriteUtc <= CreatedUtc.UtcDateTime));
    }

    internal void VerifyData(string directory, ProfileCopyOperation operation = default) =>
        VerifyTree(Path.Combine(directory, DataName), Directories, Files, operation);

    private static void VerifyTree(string directory, string[] directories, ProfileSnapshotFile[] files, ProfileCopyOperation operation)
    {
        VerifyInventory(directory, directories, files, operation);
        VerifyFiles(directory, files, operation);
    }

    private static void VerifyInventory(string directory, string[] directories, ProfileSnapshotFile[] files, ProfileCopyOperation operation)
    {
        var inventory = Inventory(directory, operation);
        if (!directories.SequenceEqual(inventory.Directories)
            || !files.Select(file => (file.Name, file.Length)).SequenceEqual(inventory.Files.Select(file => (file.Name, file.Length))))
            throw new InvalidDataException("The profile snapshot inventory changed during verification.");
    }

    internal void VerifyExternalMedia(string snapshotDirectory, ProfileCopyOperation operation = default)
    {
        if (ExternalMedia is { } old) VerifyFiles(old.Root, old.Files, operation);
        if (ManagedMedia is not { } managed) return;
        for (int index = 0; index < managed.Length; index++)
        {
            ProfileExternalMedia media = managed[index];
            VerifyTree(MediaTree(snapshotDirectory, index), media.Directories!, media.Files, operation);
        }
    }

    private static void VerifyFiles(string directory, ProfileSnapshotFile[] files, ProfileCopyOperation operation)
    {
        foreach (ProfileSnapshotFile file in files)
        {
            operation.Token.ThrowIfCancellationRequested();
            operation.Verify(Child(directory, file.Name), file);
        }
    }

    private static (string[] Directories, List<InventoryFile> Files) Inventory(string root, ProfileCopyOperation operation)
    {
        ArchiveExportMetadataFile.RejectLinks(root);
        List<string> directories = new();
        List<InventoryFile> files = new();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                operation.Report(entry);
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
                files.Add(new(name, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle)));
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
