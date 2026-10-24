using MeowChan.Desktop;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCleanupMediaInventoryTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CountsVerifiedStoredFassungenAndNotMetadata(int count)
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "media"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(profile);
        await AssetsAsync(directory, Enumerable.Range(0, count).Select(index =>
            new Asset("m" + index, "original", index + ".bin", index + ".bin", [(byte)index, 7])).ToArray());
        var evidence = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile);
        Assert.Null(evidence.Error); Assert.False(evidence.Missing); Assert.Equal(count, evidence.Assets.Count);
        Assert.Equal(count * 2, evidence.VerifiedBytes);
        var combined = ArchiveCleanupMediaInventory.Combine([evidence]);
        Assert.Equal(count, combined.SavedAssetCount);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-store.lock"), "");
        Assert.Equal(evidence.Fingerprint, (await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile)).Fingerprint);
    }

    [Fact]
    public async Task HistoryVariantsAndSeparateMediaIdentitiesRemainDistinct()
    {
        using TempDirectory temp = new(); string directory = Path.Combine(temp.Path, "media"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(profile);
        await AssetsAsync(directory, new Asset("same", "original", "same.bin", "old.bin", [1]),
            new Asset("same", "original", "same.bin", "current.bin", [2]),
            new Asset("same", "thumbnail", "thumb.bin", "thumb.bin", [2]),
            new Asset("other", "original", "other.bin", "other.bin", [2]));
        var evidence = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile);
        Assert.Null(evidence.Error); Assert.Equal(4, evidence.Assets.Count);
        Assert.Single(evidence.Assets, asset => !asset.IsCurrent);
        Assert.Equal(4, ArchiveCleanupMediaInventory.Combine([evidence]).SavedAssetCount);
    }

    [Fact]
    public async Task MissingCorruptUnknownStageAndChangedBytesNeverBecomeZero()
    {
        using TempDirectory temp = new(); string profile = Path.Combine(temp.Path, "profile"), directory = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(directory);
        Assert.Equal(0, ArchiveCleanupMediaInventory.Combine([await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile)]).SavedAssetCount);
        string missing = Path.Combine(temp.Path, "missing");
        var absent = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(missing, profile);
        Assert.True(absent.Missing); Assert.Null(ArchiveCleanupMediaInventory.Combine([absent]).SavedAssetCount);
        foreach (string unexpected in new[] { "notes.txt", ".meowchan-media-stage.part" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, unexpected), "keep");
            Assert.Null(ArchiveCleanupMediaInventory.Combine([await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile)]).SavedAssetCount);
            File.Delete(Path.Combine(directory, unexpected));
        }
        await AssetsAsync(directory, new Asset("m", "original", "m.bin", "m.bin", [1, 2]));
        await File.WriteAllBytesAsync(Path.Combine(directory, "m.bin"), [3, 4]);
        Assert.Null(ArchiveCleanupMediaInventory.Combine([await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile)]).SavedAssetCount);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), "{\"Version\":99}");
        Assert.Null(ArchiveCleanupMediaInventory.Combine([await ArchiveCleanupMediaInventory.ReadDirectoryAsync(directory, profile)]).SavedAssetCount);
    }

    [Fact]
    public async Task MissingNormalOrOpaqueThreadRequiresSameBoardVerifiedSiblingAndStableParent()
    {
        using TempDirectory temp = new(); string root = Path.Combine(temp.Path, "downloads"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(profile);
        ResourceId anchor = new("plugin/source", "board:opaque", "anchor"), target = anchor with { Thread = "missing/thread" };
        string anchorDir = DirectoryFor(root, anchor), targetDir = DirectoryFor(root, target);
        await AssetsAsync(anchorDir, new Asset("m", "original", "m.bin", "m.bin", [1, 2]));
        var witness = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(anchorDir, profile);
        var missing = await ArchiveCleanupMediaInventory.ReadDirectoryAsync(targetDir, profile);
        Assert.True(missing.Missing); Assert.NotNull(missing.Error);
        var known = ArchiveCleanupMediaInventory.WithMissingWitness(missing, target, root, [(anchor, witness)], default);
        Assert.Null(known.Error); Assert.NotNull(known.StorageAnchor); Assert.Equal(0, ArchiveCleanupMediaInventory.Combine([known]).SavedAssetCount);
        Assert.NotNull(ArchiveCleanupMediaInventory.WithMissingWitness(missing, target, root, [], default).Error);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(targetDir)!, "new-sibling"));
        Assert.NotNull(ArchiveCleanupMediaInventory.WithMissingWitness(missing, target, root, [(anchor, witness)], default).Error);
    }
}
