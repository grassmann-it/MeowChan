using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class ProfileUpgradeReturnTests
{
    [Fact]
    public void InsufficientSpaceLeavesTheSourceGenerationUntouched()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        byte[] settings = File.ReadAllBytes(Path.Combine(source, "settings.json"));
        IOException failure = Assert.Throws<IOException>(() => ProfileUpgrade.Open(source, root.Path, availableBytes: _ => 0));
        Assert.Contains("insufficient space", failure.Message, StringComparison.Ordinal);
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(source, "settings.json")));
        Assert.False(Directory.Exists(source + ".1.2.0"));
    }

    [Fact]
    public void UnknownWritablePartStopsBeforePublishingAnUpgrade()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string tabs = Path.Combine(source, BooruSearchTabStore.FileName);
        const string future = """{"version":21,"state":{}}""";
        File.WriteAllText(tabs, future);
        var failure = Assert.Throws<InvalidDataException>(() => ProfileUpgrade.Open(source, root.Path));
        Assert.Contains("unsupported format 21", failure.Message, StringComparison.Ordinal);
        Assert.Equal(future, File.ReadAllText(tabs));
        Assert.False(Directory.Exists(source + ".1.2.0"));
    }

    [Fact]
    public void DamagedSnapshotCannotBeRestoredOrAdmittedForTheFirstTime()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string target;
        using (ProfileLease lease = ProfileUpgrade.Open(source, root.Path))
            target = lease.ProfileDirectory;
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(target));
        string damaged = Path.Combine(backup, ProfileSnapshot.DataName, "settings.json");
        byte[] bytes = File.ReadAllBytes(damaged);
        bytes[^1] ^= 1;
        File.WriteAllBytes(damaged, bytes);
        string restore = Path.Combine(root.Path, "restored");
        var snapshot = ProfileSnapshot.Read(backup);
        Assert.Throws<InvalidDataException>(() => snapshot.Restore(backup, restore));
        Assert.Throws<InvalidDataException>(() => ProfileUpgrade.Open(source, root.Path));
        Assert.Throws<InvalidDataException>(() => ProfileUpgrade.Open(target, root.Path));
        Assert.False(Directory.Exists(restore));
        Assert.Equal(ProfileManifest.Preparing, ProfileManifest.Read(target)!.State);
        Assert.NotEqual(bytes, File.ReadAllBytes(Path.Combine(source, "settings.json")));
    }

    [Fact]
    public void CancellationAtPublicationLeavesOneRetryableGeneration()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(() => ProfileUpgrade.Open(source, root.Path,
            beforePublish: cancellation.Cancel, token: cancellation.Token));
        Assert.False(Directory.Exists(source + ".1.2.0"));
        Assert.Empty(Directory.EnumerateDirectories(root.Path, "*.pending-*"));
        Guid generation;
        using (ProfileLease first = ProfileUpgrade.Open(source, root.Path))
        {
            var manifest = ProfileManifest.Read(first.ProfileDirectory)!;
            Assert.Equal(ProfileManifest.Preparing, manifest.State);
            generation = manifest.Generation;
            Assert.Throws<IOException>(() => ProfileUpgrade.Open(source, root.Path));
        }
        using ProfileLease second = ProfileUpgrade.Open(source, root.Path);
        Assert.Equal(generation, ProfileManifest.Read(second.ProfileDirectory)!.Generation);
        ProfileUpgrade.Complete(second, []);
        Assert.Equal(ProfileManifest.Ready, ProfileManifest.Read(second.ProfileDirectory)!.State);
        Assert.Single(Directory.EnumerateDirectories(root.Path, "*.1.2.0"));
    }

    [Fact]
    public void PrivateV4ReviewReceiptIsPreservedAsItsOwnStoreFixture()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        var operation = Guid.NewGuid();
        var document = $$"""
            {"Version":4,"Revision":1,"Tags":[],"Threads":[],"Media":[],"LocalOccurrences":[],"LocalMedia":[],
             "Aliases":[],"LastReview":{"OperationId":"{{operation:D}}","Revision":1}}
            """;
        File.WriteAllText(Path.Combine(source, PrivateTagStore.FileName), document);
        using ProfileLease lease = ProfileUpgrade.Open(source, root.Path);
        Assert.Equal("1.2.0", ProfileManifest.Read(lease.ProfileDirectory)!.Parts[PrivateTagStore.FileName].MinimumReader);
        var store = new PrivateTagStore(lease.ProfileDirectory, lease);
        Assert.Equal(new PrivateTagReviewReceipt(operation, 1), store.LastReviewReceipt());
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(lease.ProfileDirectory));
        string restored = Path.Combine(root.Path, "review-restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        Assert.Equal(document, File.ReadAllText(Path.Combine(restored, PrivateTagStore.FileName)));
    }

    [Legacy113WriterFact]
    public async Task ReleasedWriterCanEditAndReopenTheRestoredGenerationAfterUpgradeUse()
    {
        using TempDirectory root = new();
        string source = Path.Combine(root.Path, "original");
        string downloads = Directory.CreateDirectory(Path.Combine(root.Path, "downloads")).FullName;
        const string originalName = "original.png";
        byte[] original = BooruUiFixtures.Png(5);
        await File.WriteAllBytesAsync(Path.Combine(downloads, originalName), original);
        using Legacy113ProfileWriter historical = new();
        await historical.WriteAsync(source, downloads, originalName);
        await historical.WriteTabsAsync(source, draft: false);

        string working;
        await using (DesktopSessionHost host = new(source, providers: [], admitProfileUpgrade: true))
        {
            await host.StartAsync();
            Assert.Equal(DesktopSessionState.Ready, host.State);
            working = host.ProfileDirectory;
            Assert.NotEqual(source, working);
            Assert.NotEqual(downloads, host.Config.DownloadPath);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(host.Config.DownloadPath, originalName)));
        }

        await BooruSearchTabPersistenceTests.ReopenLegacyPageWithKnownEndAsync(working, editDeferredTab: true);
        using (ProfileLease lease = ProfileLease.Acquire(working))
        {
            var catalog = new BooruCatalogStore(working, lease);
            var previous = Assert.Single(catalog.Read().Posts);
            Assert.Equal("fixture-asset", previous.LocalAssetId);
            Assert.NotNull(previous.LocalPath);
            Assert.Equal(original, await File.ReadAllBytesAsync(previous.LocalPath));
            Assert.NotEqual(Path.Combine(downloads, originalName), previous.LocalPath);
            catalog.UpsertPost(new BooruPostClaim("e621", "123", BooruPostClaimPresence.None), DateTimeOffset.UtcNow);
        }

        await using (DesktopSessionHost reopened = new(source, providers: [], admitProfileUpgrade: true))
        {
            await reopened.StartAsync();
            Assert.Equal(DesktopSessionState.Ready, reopened.State);
            Assert.Equal(working, reopened.ProfileDirectory);
            Assert.Equal(2, ProfileManifest.Read(working)!.Parts[BooruCatalogStore.FileName].Version);
        }
        await BooruSearchTabPersistenceTests.ReopenLegacyPageWithKnownEndAsync(working, editDeferredTab: false);

        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot snapshot = ProfileSnapshot.Read(backup);
        Assert.Equal(1, snapshot.Profile.Parts[BooruSearchTabStore.FileName].Version);
        Assert.Contains(downloads, snapshot.Profile.ExternalMediaRoots);
        snapshot.Restore(backup, restored);
        foreach (ProfileSnapshotFile file in snapshot.Files)
        {
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restored, file.Name)))));
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, file.Name)))));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(downloads, originalName)));
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(downloads, originalName))));
        historical.CheckAndEdit(restored, downloads, originalName, edit: true);
        historical.CheckAndEdit(restored, downloads, originalName, edit: false);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(downloads, originalName)));
        Assert.True(File.Exists(Path.Combine(working, BooruCatalogStore.FileName)));
        Assert.Equal(1, ProfileManifest.Inspect(restored)[BooruCatalogStore.FileName].Version);
    }

    private static string CreateSource(string parent)
    {
        string source = Path.Combine(parent, "legacy");
        using ProfileLease lease = ProfileLease.Acquire(source);
        Config initial = Config.LoadConfig(source, lease);
        initial.DownloadPath = Path.Combine(source, "downloads");
        initial.SaveConfig(source);
        File.WriteAllText(Path.Combine(source, "unfinished.journal"), "pending");
        return source;
    }
}
