using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ArchiveDeletionFilesTests
{
    [Fact]
    public async Task DeletesManifestOwnedMediaAndRetainsUnrelatedFiles()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        string media = Path.Combine(temp.Path, "downloads", "board", "thread");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(media);
        await WriteManifestAsync(media, "image.jpg", new byte[] { 1, 2, 3 });
        string unknown = Path.Combine(media, "notes.txt");
        await File.WriteAllTextAsync(unknown, "keep");
        string part = Path.Combine(media, ".meowchan-media-" + Guid.NewGuid().ToString("N") + ".part");
        await File.WriteAllBytesAsync(part, new byte[] { 4, 5 });

        ArchiveFilesPlan plan = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        Assert.False(plan.HasBlockers);
        Assert.Equal(3, plan.OwnedFileCount);
        Assert.Equal(3 + 2 + new FileInfo(Path.Combine(media, ".meowchan-manifest.json")).Length, plan.OwnedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(),
            Assert.Single(plan.Directories.Single().AssetSha256));
        Assert.Contains("notes.txt", plan.Directories.Single().Leftovers);
        ArchiveFilesResult result = await ArchiveDeletionFiles.DeleteAsync(plan);
        Assert.False(result.HasErrors);
        Assert.Equal(plan.OwnedBytes, result.DeletedBytes);
        Assert.False(File.Exists(Path.Combine(media, "image.jpg")));
        Assert.False(File.Exists(Path.Combine(media, ".meowchan-manifest.json")));
        Assert.False(File.Exists(part));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown));
    }

    [Fact]
    public async Task CanonicalAliasesAreInventoriedAndDeletedOnlyOnce()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        string media = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(media);
        await WriteManifestAsync(media, "image.jpg", new byte[] { 1, 2, 3 });
        ArchiveFilesPlan plan = await ArchiveDeletionFiles.PrepareAsync(
            [media, media + Path.DirectorySeparatorChar, Path.Combine(media, "..", "media")], profile);
        Assert.Single(plan.Directories);
        ArchiveFilesResult result = await ArchiveDeletionFiles.DeleteAsync(plan);
        Assert.False(result.HasErrors);
        Assert.Equal(plan.OwnedFileCount, result.DeletedFileCount);
    }

    [Fact]
    public async Task ChangedManifestRefusesDeletion()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        string media = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(media);
        await WriteManifestAsync(media, "image.jpg", new byte[] { 1, 2, 3 });
        ArchiveFilesPlan plan = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        string manifest = Path.Combine(media, ".meowchan-manifest.json");
        await File.AppendAllTextAsync(manifest, " ");

        ArchiveFilesResult result = await ArchiveDeletionFiles.DeleteAsync(plan);
        Assert.True(result.HasErrors);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.True(File.Exists(Path.Combine(media, "image.jpg")));
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public async Task AlreadyMissingManifestAssetDoesNotBlockCleanup()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        string media = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(media);
        await WriteManifestAsync(media, "missing.jpg", new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(media, "missing.jpg"));
        ArchiveFilesPlan plan = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        Assert.False(plan.HasBlockers);
        Assert.Single(plan.Directories.Single().Files);

        ArchiveFilesResult result = await ArchiveDeletionFiles.DeleteAsync(plan);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.False(File.Exists(Path.Combine(media, ".meowchan-manifest.json")));
    }

    [Fact]
    public async Task CorruptOrMissingManifestBlocksNonemptyDirectory()
    {
        using TempDirectory temp = new();
        string media = Path.Combine(temp.Path, "media");
        string profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(media); Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(media, "unknown.jpg"), "keep");
        ArchiveFilesPlan missing = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        Assert.True(missing.HasBlockers);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), "bad json");
        ArchiveFilesPlan corrupt = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        Assert.True(corrupt.HasBlockers);
        Assert.True((await ArchiveDeletionFiles.DeleteAsync(corrupt)).HasErrors);
        Assert.True(File.Exists(Path.Combine(media, "unknown.jpg")));
    }

    [Fact]
    public async Task RefusesLinkedDirectoryAndLinkedOwnedFile()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        string media = Path.Combine(temp.Path, "media");
        string target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(media); Directory.CreateDirectory(target);
        await WriteManifestAsync(media, "image.jpg", new byte[] { 1, 2, 3 });
        string linked = Path.Combine(temp.Path, "linked");
        Directory.CreateSymbolicLink(linked, media);
        Assert.True((await ArchiveDeletionFiles.PrepareAsync([linked], profile)).HasBlockers);
        File.Delete(Path.Combine(media, "image.jpg"));
        string outside = Path.Combine(target, "outside.jpg");
        await File.WriteAllBytesAsync(outside, new byte[] { 1, 2, 3 });
        File.CreateSymbolicLink(Path.Combine(media, "image.jpg"), outside);
        ArchiveFilesPlan plan = await ArchiveDeletionFiles.PrepareAsync([media], profile);
        Assert.True(plan.HasBlockers);
        Assert.True((await ArchiveDeletionFiles.DeleteAsync(plan)).HasErrors);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(outside));
    }

    private static async Task WriteManifestAsync(string directory, string name, byte[] bytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
        string manifest = JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object> { [name] = new
            {
                MediaIdentifier = "image", SourceUrl = "https://offline.invalid/image.jpg",
                FileName = name, Length = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            } }
        });
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), manifest);
    }
}
