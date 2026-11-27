using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class ProfileUpgradeReturnTests
{
    [Fact]
    public void ReplacingAnUnidentifiedLegacyDirectoryDoesNotOpenThePreviousWorkingCopy()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        using (ProfileLease lease = ProfileUpgrade.Open(source, root.Path))
            ProfileUpgrade.Complete(lease, []);

        Directory.Move(source, Path.Combine(root.Path, "detached"));
        Assert.Equal(source, CreateSource(root.Path));
        Assert.Null(ProfileInstanceIdentity.TryRead(source));
        Assert.Throws<InvalidDataException>(() =>
        {
            using ProfileLease unexpected = ProfileUpgrade.Open(source, root.Path);
        });
    }

    [Fact]
    public void NativeUnixNamesRoundTripThroughUpgradeAndRestore()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string downloads = Directory.CreateDirectory(Path.Combine(source, "downloads")).FullName;
        const string name = "scan:01\\negative.png";
        byte[] bytes = BooruUiFixtures.Png(7);
        File.WriteAllBytes(Path.Combine(downloads, name), bytes);

        using ProfileLease lease = ProfileUpgrade.Open(source, root.Path);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(lease.ProfileDirectory, "downloads", name)));
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(lease.ProfileDirectory));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(restored, "downloads", name)));
    }

    [Fact]
    public async Task ResumedWatchUsesItsOwnCopiedDownloadTree()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string current = Directory.CreateDirectory(Path.Combine(root.Path, "current-downloads")).FullName;
        string previous = Directory.CreateDirectory(Path.Combine(root.Path, "previous-downloads")).FullName;
        const string thread = "https://boards.4chan.org/g/thread/123";
        await File.WriteAllTextAsync(Path.Combine(current, "current.txt"), "current marker");
        await File.WriteAllTextAsync(Path.Combine(previous, "previous.txt"), "previous marker");
        using (ProfileLease lease = ProfileLease.Acquire(source))
        {
            Config settings = Config.LoadConfig(source, lease);
            settings.DownloadPath = current;
            settings.AutomaticUpdateChecks = false;
            settings.SaveConfig(source);
            lease.Admission.CompleteStartup();
            new WorkStateStore(source, lease).CommitIntent(new WorkResource
            {
                Url = thread, OutputRoot = previous, Generation = 2, Intent = WorkIntent.Watching, IsThread = true
            });
        }

        byte[] bytes = BooruUiFixtures.Png(6);
        using HttpClient client = new(new WatchTransport(bytes));
        await using DesktopSessionHost host = new(source, providers: [new FourChanImageBoard(client)], admitProfileUpgrade: true);
        TaskCompletionSource<string> downloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReadSnapshot()
        {
            var watch = host.GetView().Threads.SingleOrDefault();
            if (watch?.Snapshot.CompletedMedia == 1) downloaded.TrySetResult(watch.Snapshot.DownloadDirectory);
        }
        host.StateChanged += ReadSnapshot;
        try
        {
            await host.StartAsync();
            ReadSnapshot();
            string directory = await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith(host.ProfileDirectory + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
            Assert.NotEqual(host.Config.DownloadPath, directory);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "777.png")));
            Assert.Equal("current marker", await File.ReadAllTextAsync(Path.Combine(host.Config.DownloadPath, "current.txt")));
            Assert.Equal("previous marker", await File.ReadAllTextAsync(Assert.Single(
                Directory.EnumerateFiles(host.ProfileDirectory, "previous.txt", SearchOption.AllDirectories))));
        }
        finally { host.StateChanged -= ReadSnapshot; }
        await host.StopAsync();
        Assert.Equal(Path.Combine(current, "current.txt"), Assert.Single(Directory.EnumerateFileSystemEntries(current)));
        Assert.Equal(Path.Combine(previous, "previous.txt"), Assert.Single(Directory.EnumerateFileSystemEntries(previous)));
    }

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
             "Aliases":[],"LastReview":{"OperationId":"{{operation:D}}","Revision":1}
            }
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
        var media = Assert.Single(Assert.IsType<ProfileExternalMedia[]>(snapshot.ManagedMedia));
        Assert.Equal(downloads, media.Root);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Assert.Single(media.Files).Sha256);
        string detached = Path.Combine(root.Path, "detached-downloads");
        Directory.Move(downloads, detached);
        snapshot.Restore(backup, restored);
        foreach (ProfileSnapshotFile file in snapshot.Files)
        {
            // The review requires absolute references to be rebound in the restored copy.
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(backup, ProfileSnapshot.DataName, file.Name)))));
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, file.Name)))));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(detached, originalName)));
        string restoredOriginal = Assert.Single(Directory.EnumerateFiles(restored, originalName, SearchOption.AllDirectories));
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(restoredOriginal)));
        string restoredDownloads = Path.GetDirectoryName(restoredOriginal)!;
        historical.CheckAndEdit(restored, restoredDownloads, originalName, edit: true);
        historical.CheckAndEdit(restored, restoredDownloads, originalName, edit: false);
        Assert.Equal(original, await File.ReadAllBytesAsync(restoredOriginal));
        Assert.True(File.Exists(Path.Combine(working, BooruCatalogStore.FileName)));
        Assert.Equal(1, ProfileManifest.Inspect(restored)[BooruCatalogStore.FileName].Version);
    }

    [Legacy113WriterFact]
    public async Task ReleasedWriterReturnsWithProfileAndExternalTreesDetached()
    {
        using TempDirectory root = new();
        string source = Path.Combine(root.Path, "original");
        string downloads = Path.Combine(source, "downloads");
        string previous = Directory.CreateDirectory(Path.Combine(root.Path, "older-downloads")).FullName;
        const string name = "original.png";
        byte[] internalBytes = BooruUiFixtures.Png(5);
        byte[] externalBytes = BooruUiFixtures.Png(6);
        await File.WriteAllBytesAsync(Path.Combine(previous, name), externalBytes);
        using Legacy113ProfileWriter historical = new();
        await historical.WriteAsync(source, downloads, name, previous);
        Directory.CreateDirectory(downloads);
        await File.WriteAllBytesAsync(Path.Combine(downloads, name), internalBytes);
        await historical.WriteTabsAsync(source, draft: false);

        string working;
        await using (DesktopSessionHost host = new(source, providers: [], admitProfileUpgrade: true))
        {
            await host.StartAsync();
            Assert.Equal(DesktopSessionState.Ready, host.State);
            working = host.ProfileDirectory;
        }
        string workingExternal = Assert.Single(Directory.EnumerateFiles(working, name, SearchOption.AllDirectories),
            path => Path.GetDirectoryName(path) != Path.Combine(working, "downloads"));
        await File.WriteAllBytesAsync(workingExternal, BooruUiFixtures.Png(9));
        Directory.Move(source, Path.Combine(root.Path, "detached-profile"));
        Directory.Move(previous, Path.Combine(root.Path, "detached-media"));
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);

        string restoredDownloads = Path.Combine(restored, "downloads");
        string restoredExternal = Assert.Single(Directory.EnumerateFiles(restored, name, SearchOption.AllDirectories),
            path => Path.GetDirectoryName(path) != restoredDownloads);
        Assert.Equal(internalBytes, await File.ReadAllBytesAsync(Path.Combine(restoredDownloads, name)));
        Assert.Equal(externalBytes, await File.ReadAllBytesAsync(restoredExternal));
        historical.CheckAndEdit(restored, restoredDownloads, name, edit: true, previous: Path.GetDirectoryName(restoredExternal));
        historical.CheckAndEdit(restored, restoredDownloads, name, edit: false, previous: Path.GetDirectoryName(restoredExternal));
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(previous));
    }

    private sealed class WatchTransport(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content = request.RequestUri!.AbsolutePath switch
            {
                "/g/777.png" => new ByteArrayContent(bytes),
                "/boards.json" => new StringContent("""{"boards":[{"board":"g","title":"Technology"}]}"""),
                "/g/thread/123.json" => new StringContent(JsonSerializer.Serialize(new
                {
                    posts = new[] { new { no = 123, sub = "Watch fixture", tim = 777, filename = "fixture", ext = ".png",
                        fsize = bytes.Length, md5 = Convert.ToBase64String(MD5.HashData(bytes)) } }
                })),
                _ => throw new InvalidOperationException("Unexpected watch request: " + request.RequestUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
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
