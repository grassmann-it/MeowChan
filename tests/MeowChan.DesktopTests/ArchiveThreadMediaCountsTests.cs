using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ArchiveThreadMediaCountsTests
{
    [Fact]
    public async Task EmptyCurrentWorkGenerationCannotHideDurableMediaVersions()
    {
        using TempDirectory temp = new();
        await ManifestAsync(temp.Path, 2);
        ArchiveThreadRow row = new(Summary(temp.Path), new("https://counts.invalid/thread", WorkIntent.Suppressed,
            ThreadSourceStateValue.Active, null, false, []));
        Assert.Equal("—", row.CompactMedia);
        row.SetMediaCount(await ArchiveThreadMediaInventory.ReadAsync([temp.Path]), row.Work);
        Assert.Equal("2", row.CompactMedia);
        Assert.Equal(2, row.SavedMediaCount);
        Assert.DoesNotContain("0/0", row.MediaStatus);
        Assert.DoesNotContain("0 / 0", row.MediaStatus);
        Assert.DoesNotContain(Texts.T("archive.media.currentProgress", ""), row.MediaStatus);
    }

    [Fact]
    public async Task LegacyManifestAndMediaOnlyThreadCountWithoutWorkOrMutation()
    {
        using TempDirectory temp = new();
        await ManifestAsync(temp.Path, 1, legacy: true);
        var before = Files(temp.Path);
        ArchiveThreadRow row = new(Summary(temp.Path) with { LastCapturedAt = null, PostCount = 0 });
        row.SetMediaCount(await ArchiveThreadMediaInventory.ReadAsync([temp.Path]), null);
        Assert.Equal("1", row.CompactMedia);
        Assert.Equal(before, Files(temp.Path));
        Assert.False(File.Exists(Path.Combine(temp.Path, ".meowchan-manifest.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, ".meowchan-store.lock")));
    }

    [Fact]
    public async Task EmptyTextOnlyFolderIsZeroAndMissingParentIsUnknownWithoutCreatingAnything()
    {
        using TempDirectory temp = new();
        var empty = await ArchiveThreadMediaInventory.ReadAsync([temp.Path]);
        Assert.Equal(0, empty.SavedVersions);
        string missingParent = Path.Combine(temp.Path, "offline", "thread");
        var unknown = await ArchiveThreadMediaInventory.ReadAsync([missingParent]);
        Assert.Null(unknown.SavedVersions);
        Assert.NotNull(unknown.Error);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task WitnessedMissingDirectChildIsZeroButLooseFilesAreNeverInferred()
    {
        using TempDirectory temp = new();
        Assert.Equal(0, (await ArchiveThreadMediaInventory.ReadAsync([Path.Combine(temp.Path, "absent")])).SavedVersions);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "unowned.jpg"), "unowned");
        var unknown = await ArchiveThreadMediaInventory.ReadAsync([temp.Path]);
        Assert.Null(unknown.SavedVersions);
        Assert.NotNull(unknown.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task MultipleRegisteredFoldersAndPathAliasesDoNotDoubleCount()
    {
        using TempDirectory temp = new();
        string first = Path.Combine(temp.Path, "first"), second = Path.Combine(temp.Path, "second");
        await ManifestAsync(first, 2); await ManifestAsync(second, 1);
        var count = await ArchiveThreadMediaInventory.ReadAsync([first, Path.Combine(first, "."), first + Path.DirectorySeparatorChar, second]);
        Assert.Equal(3, count.SavedVersions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingAndChangedLengthFilesAreNotClaimedAsStored(bool missing)
    {
        using TempDirectory temp = new();
        await ManifestAsync(temp.Path, 2);
        string first = Path.Combine(temp.Path, "file-0.png");
        if (missing) File.Delete(first); else await File.AppendAllTextAsync(first, "changed");
        var count = await ArchiveThreadMediaInventory.ReadAsync([temp.Path]);
        Assert.Equal(1, count.SavedVersions);
        Assert.Equal(1, count.MissingOrChangedFiles);
        Assert.Null(count.Error);
    }

    [Fact]
    public async Task CorruptOrUnsupportedManifestIsUnknownAndCannotBecomeZero()
    {
        using TempDirectory temp = new();
        string manifest = Path.Combine(temp.Path, ".meowchan-manifest.json");
        foreach (string content in new[] { "{broken", "{\"Version\":999,\"Entries\":{}}" })
        {
            await File.WriteAllTextAsync(manifest, content);
            var count = await ArchiveThreadMediaInventory.ReadAsync([temp.Path]);
            Assert.Null(count.SavedVersions); Assert.NotNull(count.Error);
            Assert.Equal(content, await File.ReadAllTextAsync(manifest));
        }
    }

    [Fact]
    public async Task CancelledSlowReadCannotPublishItsLateCount()
    {
        using TempDirectory temp = new();
        await ManifestAsync(temp.Path, 1);
        var snapshot = await MeowChan.Storage.MediaAssetReader.OpenAsync(temp.Path);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ArchiveThreadMediaCount> pending = ArchiveThreadMediaInventory.ReadAsync([temp.Path], cancellation.Token, async (_, _) => {
            entered.SetResult(); await release.Task; return snapshot;
        });
        await entered.Task;
        cancellation.Cancel(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task FreshProjectionObservesNewVersionAndRaisesOnlyMediaProperties()
    {
        using TempDirectory temp = new();
        await ManifestAsync(temp.Path, 1);
        ArchiveThreadRow row = new(Summary(temp.Path));
        row.SetMediaCount(await ArchiveThreadMediaInventory.ReadAsync([temp.Path]), null);
        List<string?> changes = [];
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        await ManifestAsync(temp.Path, 2);
        row.SetMediaCount(await ArchiveThreadMediaInventory.ReadAsync([temp.Path]), null);
        Assert.Equal("2", row.CompactMedia);
        Assert.Equal(new[] { nameof(row.CompactMedia), nameof(row.SavedMediaCount), nameof(row.MediaStatus) }, changes);
        Assert.Equal(8, row.Posts);
    }

    private static ArchiveThreadSummary Summary(string directory) => new(new("fixture", "board", "123"), "https://counts.invalid/thread", "Captured text",
        directory, ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 8, 1);
    private static async Task ManifestAsync(string directory, int count, bool legacy = false)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = [(byte)i, 2, 3]; string file = $"file-{i}.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
            entries[file] = new { MediaIdentifier = "media-" + i, Variant = "original", SourceUrl = "https://counts.invalid/" + file,
                FileName = file, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, legacy ? ".ochan-manifest.json" : ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
    }
    private static string[] Files(string directory) => Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
