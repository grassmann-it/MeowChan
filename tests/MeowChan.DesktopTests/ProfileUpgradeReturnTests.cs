using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class ProfileUpgradeReturnTests
{
    [Fact]
    public void ReplacingAnUnidentifiedLegacyDirectoryKeepsThePublishedWorkingCopyAvailable()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string working;
        using (ProfileLease lease = ProfileUpgrade.Open(source, root.Path))
        {
            working = lease.ProfileDirectory;
            ProfileUpgrade.Complete(lease, []);
        }

        Directory.Move(source, Path.Combine(root.Path, "detached"));
        Assert.Equal(source, CreateSource(root.Path));
        Assert.Null(ProfileInstanceIdentity.TryRead(source));
        using ProfileLease reopened = ProfileUpgrade.Open(source, root.Path, out bool changed);
        Assert.Equal(working, reopened.ProfileDirectory);
        Assert.True(changed);
    }

    [Fact]
    public void ReplacingLegacyContentsInPlaceKeepsThePublishedWorkingCopyAvailable()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string working;
        using (ProfileLease lease = ProfileUpgrade.Open(source, root.Path))
        {
            working = lease.ProfileDirectory;
            ProfileUpgrade.Complete(lease, []);
        }
        // Keep the directory identity, filenames and lengths identical to the capture.
        string journal = Path.Combine(source, "unfinished.journal");
        DateTime written = File.GetLastWriteTimeUtc(journal);
        File.WriteAllText(journal, "another");
        File.SetLastWriteTimeUtc(journal, written.AddSeconds(2));
        Assert.Null(ProfileInstanceIdentity.TryRead(source));
        using ProfileLease reopened = ProfileUpgrade.Open(source, root.Path, out bool changed);
        Assert.Equal(working, reopened.ProfileDirectory);
        Assert.True(changed);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("moved")]
    [InlineData("device")]
    [InlineData("id")]
    public async Task PublishedUpgradeReopensAndRestoresWhenLegacySourceChanges(string change)
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path, ".oChan");
        Assert.Null(ProfileInstanceIdentity.TryRead(source));
        string working;
        string backup;
        await using (DesktopSessionHost first = new(source, providers: [], admitProfileUpgrade: true))
        {
            await first.StartAsync();
            Assert.Equal(DesktopSessionState.Ready, first.State);
            working = first.ProfileDirectory;
            backup = Assert.IsType<string>(first.PreUpgradeSnapshot);
        }

        switch (change)
        {
            case "deleted": Directory.Delete(source, recursive: true); break;
            case "moved": Directory.Move(source, Path.Combine(root.Path, "moved")); break;
            case "device":
                string index = Path.Combine(backup, ProfileSnapshot.FileName);
                JsonObject oldSnapshot = JsonNode.Parse(File.ReadAllText(index))!.AsObject();
                foreach (JsonObject file in oldSnapshot["files"]!.AsArray().OfType<JsonObject>()) file.Remove("lastWriteUtc");
                string identity = ProfileDirectoryIdentity.Capture(source).DiscardToken;
                // An older snapshot still records the inode and a different device number.
                oldSnapshot["sourceIdentity"] = (identity[0] == '0' ? '1' : '0') + identity[1..];
                File.WriteAllText(index, oldSnapshot.ToJsonString());
                break;
            case "id":
                using (ProfileLease legacy = ProfileLease.Acquire(source))
                    Assert.NotEqual(ProfileManifest.Read(working)!.ProfileId, ProfileInstanceIdentity.ReadOrCreate(legacy).Id);
                break;
        }

        string discovered = Config.ResolveDefaultProfileDirectory(root.Path);
        Assert.Equal(source, discovered);
        await using DesktopSessionHost reopened = new(discovered, providers: [], admitProfileUpgrade: true);
        await reopened.StartAsync();
        Assert.Equal(DesktopSessionState.Ready, reopened.State);
        Assert.Equal(working, reopened.ProfileDirectory);
        Assert.Equal(backup, reopened.PreUpgradeSnapshot);
        Assert.Equal(change != "device", reopened.LegacyProfileSourceChanged);
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        Assert.Equal("pending", File.ReadAllText(Path.Combine(restored, "unfinished.journal")));
        if (change is "deleted" or "moved") Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void SwappingTheSourceBeforePublicationStillRefusesTheUpgrade()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string detached = Path.Combine(root.Path, "detached");

        Assert.Throws<InvalidDataException>(() => ProfileUpgrade.Open(source, root.Path, beforePublish: () =>
        {
            Directory.Move(source, detached);
            Directory.CreateDirectory(source);
        }));
        Assert.False(Directory.Exists(source + ".1.2.0"));
        Assert.Empty(Directory.EnumerateDirectories(root.Path, "*.pending-*"));
        Assert.Equal("pending", File.ReadAllText(Path.Combine(detached, "unfinished.journal")));
    }

    [Fact]
    public async Task CancellationAfterTheCopyReleasesTheHostAndLeavesThePublishedCopyRetryable()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        using CancellationTokenSource cancellation = new();
        string? working = null;
        Assert.Throws<OperationCanceledException>(() => new DesktopSessionHost(source, providers: [], admitProfileUpgrade: true,
            upgradeCancellation: cancellation.Token, updateSessionFactory: (directory, config) =>
            {
                working = directory;
                cancellation.Cancel();
                return new UpdateSession(directory, config);
            }));

        Assert.Equal(ProfileManifest.Preparing, ProfileManifest.Read(Assert.IsType<string>(working))!.State);
        Directory.Delete(source, recursive: true);
        await using DesktopSessionHost reopened = new(source, providers: [], admitProfileUpgrade: true);
        await reopened.StartAsync();
        Assert.Equal(DesktopSessionState.Ready, reopened.State);
        Assert.Equal(working, reopened.ProfileDirectory);
        Assert.True(reopened.LegacyProfileSourceChanged);
    }

    [Fact]
    public void TagQueueAndItsBackupPointAtCopiedMediaInEachGeneration()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string media = Directory.CreateDirectory(Path.Combine(root.Path, "queued-originals")).FullName;
        const string name = "queued.png";
        byte[] bytes = BooruUiFixtures.Png(7);
        File.WriteAllBytes(Path.Combine(media, name), bytes);
        TagQueueItem item = new(new ResourceId("fixture", "g", "123"), media, "asset",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "media", "original", null, null, null, null,
            false, null, name, SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch);
        using (ProfileLease legacy = ProfileLease.Acquire(source))
        {
            legacy.Admission.CompleteStartup();
            TagQueueStore queue = new(source, legacy);
            Assert.Equal(1, queue.Add([item]).Added);
            queue.SetCursor(1);
        }
        string originalQueue = File.ReadAllText(Path.Combine(source, TagQueueStore.FileName));
        string originalBackup = File.ReadAllText(Path.Combine(source, TagQueueStore.FileName + ".bak"));

        using ProfileLease working = ProfileUpgrade.Open(source, root.Path);
        AssertRelocated(working);
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working.ProfileDirectory));
        Directory.Move(media, Path.Combine(root.Path, "detached-originals"));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        using ProfileLease restoredLease = ProfileLease.Acquire(restored);
        AssertRelocated(restoredLease);
        Assert.Equal(originalQueue, File.ReadAllText(Path.Combine(source, TagQueueStore.FileName)));
        Assert.Equal(originalBackup, File.ReadAllText(Path.Combine(source, TagQueueStore.FileName + ".bak")));
        Assert.Equal(originalQueue, File.ReadAllText(Path.Combine(backup, ProfileSnapshot.DataName, TagQueueStore.FileName)));

        void AssertRelocated(ProfileLease lease)
        {
            string directory = lease.ProfileDirectory;
            var entry = Assert.Single(new TagQueueStore(directory, lease).Read().Items);
            Assert.Equal(item with { Directory = entry.Directory }, entry);
            Assert.StartsWith(directory + Path.DirectorySeparatorChar, entry.Directory, StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(entry.Directory, name)));
            var document = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, TagQueueStore.FileName + ".bak")));
            Assert.Equal(entry.Directory, (string?)document!["Items"]![0]!["Directory"]);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedWatchUsesItsOwnCopiedDownloadTree(bool utf16Session)
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
            if (utf16Session)
            {
                var session = new SessionSnapshot { ThreadUrls = [thread], DownloadDirectories = new() { [thread] = previous } };
                await File.WriteAllTextAsync(Path.Combine(source, "saved_urls.json"), JsonSerializer.Serialize(session), Encoding.Unicode);
                string configFile = Path.Combine(source, "settings.json");
                await File.WriteAllTextAsync(configFile, await File.ReadAllTextAsync(configFile), Encoding.BigEndianUnicode);
            }
            else new WorkStateStore(source, lease).CommitIntent(new WorkResource
            {
                Url = thread, OutputRoot = previous, Generation = 2, Intent = WorkIntent.Watching, IsThread = true
            });
        }

        byte[] bytes = BooruUiFixtures.Png(6);
        using WatchTransport transport = new(bytes);
        using HttpClient client = new(transport, disposeHandler: false);
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
        Assert.Equal("current marker", await File.ReadAllTextAsync(Path.Combine(current, "current.txt")));
        Assert.Equal("previous marker", await File.ReadAllTextAsync(Path.Combine(previous, "previous.txt")));
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
    public void CombinedSpaceIsCheckedBeforeCopyingTheFirstFile()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        string current = Directory.CreateDirectory(Path.Combine(root.Path, "current")).FullName;
        string previous = Directory.CreateDirectory(Path.Combine(root.Path, "previous")).FullName;
        File.WriteAllBytes(Path.Combine(current, "first"), new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(previous, "second"), new byte[1024 * 1024]);
        new Config { DownloadPath = current }.SaveConfig(source);
        File.WriteAllText(Path.Combine(source, "saved_urls.json"), JsonSerializer.Serialize(new SessionSnapshot
        {
            DownloadDirectories = new() { ["https://boards.4chan.org/g/thread/123"] = previous }
        }));
        bool copied = false;
        Assert.Throws<IOException>(() => ProfileUpgrade.Open(source, root.Path,
            availableBytes: _ => 3 * 1024 * 1024, progress: (_, bytes) => copied |= bytes > 0));
        Assert.False(copied);
        Assert.False(Directory.Exists(source + ".1.2.0"));
    }

    [Fact]
    public void CancellationInsideOneLargeFileCleansUpThePartialCopy()
    {
        using TempDirectory root = new();
        string source = CreateSource(root.Path);
        const int length = 4 * 1024 * 1024;
        File.WriteAllBytes(Path.Combine(source, "large.bin"), new byte[length]);
        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(() => ProfileUpgrade.Open(source, root.Path,
            progress: (path, bytes) =>
            {
                if (Path.GetFileName(path) == "large.bin" && bytes is > 0 and < length) cancellation.Cancel();
            }, token: cancellation.Token));
        Assert.False(Directory.Exists(source + ".1.2.0"));
        Assert.Empty(Directory.EnumerateDirectories(root.Path, "*.pending-*"));
    }

    [Legacy113WriterFact]
    public async Task AbsentLinkedOriginalStopsUpgradeUntilTheDriveReturns()
    {
        using TempDirectory root = new();
        string source = Path.Combine(root.Path, "legacy");
        string drive = Directory.CreateDirectory(Path.Combine(root.Path, "drive")).FullName;
        string offline = Path.Combine(root.Path, "offline");
        File.WriteAllText(Path.Combine(drive, "original.png"), "original");
        using Legacy113ProfileWriter writer = new();
        await writer.WriteAsync(source, drive, "original.png");
        Directory.Move(drive, offline);
        Assert.Throws<InvalidDataException>(() => ProfileUpgrade.Open(source, root.Path));
        Assert.False(Directory.Exists(source + ".1.2.0"));
        Assert.False(Directory.Exists(drive));
        Directory.Move(offline, drive);
        using ProfileLease working = ProfileUpgrade.Open(source, root.Path);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(
            working.ProfileDirectory, "original.png", SearchOption.AllDirectories))));
    }

    [Legacy113WriterFact]
    public async Task SingleLinkedFileCopiesOnlyItsOriginalAndManifestIntoBothCopies()
    {
        using TempDirectory root = new();
        string source = Path.Combine(root.Path, "legacy");
        string shared = Directory.CreateDirectory(Path.Combine(root.Path, "shared")).FullName;
        string downloads = Path.Combine(source, "downloads");
        using Legacy113ProfileWriter writer = new();
        await writer.WriteAsync(source, downloads, "original.png", originalDirectory: shared);
        File.WriteAllText(Path.Combine(shared, "original.png"), "kept");
        File.WriteAllText(Path.Combine(shared, ".meowchan-manifest.json"), "{}");
        File.WriteAllText(Path.Combine(shared, "unrelated.png"), "private");
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(shared, "unrelated-link"), Path.Combine(shared, "unrelated.png"));

        using ProfileLease working = ProfileUpgrade.Open(source, root.Path);
        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working.ProfileDirectory));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        foreach (string copy in new[] { backup, working.ProfileDirectory, restored })
        {
            string original = Assert.Single(Directory.GetFiles(copy, "original.png", SearchOption.AllDirectories));
            Assert.Equal("kept", File.ReadAllText(original));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(Path.GetDirectoryName(original)!, ".meowchan-manifest.json")));
            Assert.Empty(Directory.GetFiles(copy, "unrelated*", SearchOption.AllDirectories));
        }
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

        string tags = JsonSerializer.Serialize(new { Version = 1, Revision = 1,
            Tags = new[] { new { Id = Guid.NewGuid(), Name = downloads } }, Threads = Array.Empty<object>(), Media = Array.Empty<object>() });
        await File.WriteAllTextAsync(Path.Combine(source, PrivateTagStore.FileName), tags);

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
        string restoredOriginal = Assert.Single(Directory.EnumerateFiles(restored, originalName, SearchOption.AllDirectories));
        string restoredDownloads = Path.GetDirectoryName(restoredOriginal)!;
        foreach (ProfileSnapshotFile file in snapshot.Files)
        {
            string destination = Path.Combine(restored, file.Name);
            string sourceFile = Path.Combine(source, file.Name);
            byte[] restoredBytes = await File.ReadAllBytesAsync(destination);
            string primary = file.Name.EndsWith(".bak", StringComparison.Ordinal) ? file.Name[..^4] : file.Name;
            if (primary is "settings.json" or "saved_urls.json" or "booru-catalog.json")
            {
                JsonObject expected = JsonNode.Parse(await File.ReadAllTextAsync(sourceFile))!.AsObject();
                bool rebound = false;
                void Rebind(JsonObject row, string field, string from, string to)
                {
                    if ((string?)row[field] != from) return;
                    row[field] = to;
                    rebound = true;
                }
                if (primary == "settings.json") Rebind(expected, "DownloadPath", downloads, restoredDownloads);
                if (expected["DownloadDirectories"] is JsonObject assignments)
                    foreach (string url in assignments.Select(pair => pair.Key).ToArray())
                        Rebind(assignments, url, downloads, restoredDownloads);
                if (expected["posts"] is JsonArray posts)
                    foreach (JsonObject post in posts.OfType<JsonObject>())
                        Rebind(post, "localPath", Path.Combine(downloads, originalName), restoredOriginal);
                Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(restoredBytes)), file.Name);
                if (!rebound) Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(restoredBytes)));
            }
            else Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(restoredBytes)));
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(backup, ProfileSnapshot.DataName, file.Name)))));
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, file.Name)))));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(detached, originalName)));
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(restoredOriginal)));
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

    private static string CreateSource(string parent, string name = "legacy")
    {
        string source = Path.Combine(parent, name);
        using ProfileLease lease = ProfileLease.Acquire(source);
        Config initial = Config.LoadConfig(source, lease);
        initial.DownloadPath = Path.Combine(source, "downloads");
        initial.AutomaticUpdateChecks = false;
        initial.SaveConfig(source);
        File.WriteAllText(Path.Combine(source, "unfinished.journal"), "pending");
        return source;
    }
}
