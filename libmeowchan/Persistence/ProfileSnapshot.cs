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

internal sealed record ProfileSnapshot(int Version, string SourceDirectory, DateTimeOffset CreatedUtc,
    ProfileManifest Profile, string[] Directories, ProfileSnapshotFile[] Files)
{
    internal const string FileName = "snapshot.json";
    internal const string DataName = "data";
    private const int SnapshotVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false };

    internal static ProfileSnapshot Capture(ProfileLease source, string destination, ProfileManifest profile,
        Func<string, long>? availableBytes = null, CancellationToken token = default)
    {
        return source.RunExclusive(() =>
        {
            source.EnsureDirectoryIdentity();
            string root = source.ProfileDirectory;
            (string[] directories, List<(string Name, long Length)> files) = Inventory(root);
            long bytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Length));
            if ((availableBytes ?? FreeSpace)(destination) / 2 < bytes)
                throw new IOException("There is insufficient space for the profile snapshot and its trial restore.");

            string data = Path.Combine(destination, DataName);
            Directory.CreateDirectory(data);
            foreach (string directory in directories)
                Directory.CreateDirectory(Child(data, directory));
            List<ProfileSnapshotFile> copied = new(files.Count);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string hash = CopyFile(Child(root, file.Name), Child(data, file.Name), file.Length);
                copied.Add(new(file.Name, file.Length, hash));
            }
            source.EnsureDirectoryIdentity();
            var snapshot = new ProfileSnapshot(SnapshotVersion, root, DateTimeOffset.UtcNow, profile,
                directories, copied.ToArray());
            snapshot.Verify(root, token);
            return snapshot;
        });
    }

    internal void Write(string directory) => PublishJson(Path.Combine(directory, FileName), this, Json);

    internal static ProfileSnapshot Read(string directory)
    {
        ArchiveExportMetadataFile.RejectLinks(directory);
        using FileStream file = RegularProfileFile.OpenRead(Path.Combine(directory, FileName));
        ProfileSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<ProfileSnapshot>(file, Json); }
        catch (JsonException error) { throw new InvalidDataException("The profile snapshot index is damaged.", error); }
        if (snapshot is null || snapshot.Version != SnapshotVersion || !Path.IsPathFullyQualified(snapshot.SourceDirectory)
            || snapshot.Profile is null || snapshot.Profile.ProfileId == Guid.Empty || snapshot.Profile.Generation == Guid.Empty
            || snapshot.Directories is null || snapshot.Files is null
            || snapshot.Files.Any(entry => entry is null || entry.Length < 0
                || entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The profile snapshot index is invalid.");
        return snapshot;
    }

    internal void Restore(string snapshotDirectory, string destination, CancellationToken token = default)
    {
        string target = ProfileLease.NormalizeDirectory(destination);
        ArchiveExportMetadataFile.RejectLinks(target);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The profile restore requires a fresh directory.");
        string staging = target + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            CreatePrivateDirectory(staging);
            string source = Path.Combine(snapshotDirectory, DataName);
            if (FreeSpace(staging) < Files.Aggregate(0L, (sum, file) => checked(sum + file.Length)))
                throw new IOException("There is insufficient space to restore the profile snapshot.");
            foreach (string directory in Directories)
                Directory.CreateDirectory(Child(staging, directory));
            foreach (ProfileSnapshotFile file in Files)
            {
                token.ThrowIfCancellationRequested();
                string hash = CopyFile(Child(source, file.Name), Child(staging, file.Name), file.Length);
                if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"The profile snapshot checksum failed for '{file.Name}'.");
            }
            Verify(staging, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal void VerifyData(string directory, CancellationToken token = default) => Verify(Path.Combine(directory, DataName), token);

    private void Verify(string directory, CancellationToken token)
    {
        (string[] directories, List<(string Name, long Length)> files) = Inventory(directory);
        if (!Directories.SequenceEqual(directories)
            || !Files.Select(file => (file.Name, file.Length)).SequenceEqual(files))
            throw new InvalidDataException("The profile snapshot inventory changed during verification.");
        foreach (ProfileSnapshotFile file in Files)
        {
            token.ThrowIfCancellationRequested();
            using FileStream stream = RegularProfileFile.OpenRead(Child(directory, file.Name));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.Ordinal))
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
        if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Split('/', '\\').Any(
            part => part is "" or "." or ".." || part.Contains(':')))
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

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
