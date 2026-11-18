using System.Net;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Ratings;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Testing;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

/// <summary>
/// Investigation, 11.11.2026: what the production archive does when a source that used to report a provider MD5
/// stops reporting it. The attachment revision key hashes ExpectedMd5, so dropping the field gives every
/// attachment of an already archived thread a different key. This runs the real adapter, queue, media store,
/// archive and gallery selection against a profile that was filled by the "old" metadata (MD5 present, like the
/// 2ch plugin before) and then re-checked with the "new" metadata (no MD5, same URL, same bytes).
/// Result: each original is fetched once more (the new key has no binding yet), identical bytes land in the same
/// assets, favorites and ratings are untouched, and private media tags are carried over to the new key
/// (MediaTagKeyCarryOver) when the bytes are the same and not when they differ.
/// No network: the worker and the media transport are in-memory.
/// </summary>
public sealed class ProviderMd5DropArchiveTests(ITestOutputHelper output)
{
    private static readonly byte[][] Files = [[1, 2, 3, 4, 5], [9, 8, 7, 6, 5, 4, 3]];
    private const string ThreadUrl = "https://2ch.su/b/res/123.html";
    private static readonly ResourceId Id = new("2ch", "b", "123");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AReCheckWithoutTheProviderMd5RedownloadsIntoTheSameAssetsAndCarriesTagsOverOnlyForTheSameBytes(
        bool restartBeforeTheRecheck, bool theBytesChanged)
    {
        using TempProfile profile = new();
        MediaCounter media = new();
        FavoriteAssetKey favoriteKey;
        PrivateMediaTagTarget oldTarget;
        TagId tag;
        string mediaDirectory;

        // Phase 1: the profile as the old plugin left it (MD5 reported).
        Env first = await Env.StartAsync(profile.Path, md5: true, media);
        Env second = first;
        try
        {
            AdmissionResult admitted = await first.Registry.AddAsync(ThreadUrl);
            Assert.Equal(AdmissionStatus.Added, admitted.Status);
            await WaitAsync(() => admitted.Thread!.DownloadedMediaCount == 2);
            await first.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, media.Total);
            mediaDirectory = admitted.Thread!.DownloadDirectory;

            Observation before = await Observe(first, mediaDirectory);
            output.WriteLine("BEFORE\n" + before.Describe());
            Assert.Equal(2, before.AttachmentKeys.Count);
            Assert.Equal(2, before.BindingKeys.Count);
            Assert.All(before.Tiles, tile => Assert.False(tile.Unassigned));
            GalleryEntry entry = before.Gallery.Single(item => item.Attachment!.Position == 0);
            favoriteKey = FavoriteAssetKey.From(Id, entry.Asset);
            new FavoriteStore(profile.Path, first.Lease).SetFavorite(new FavoriteEntry(favoriteKey, entry.Asset.Length, entry.Asset.OriginalFileName), true);
            new MediaRatingStore(profile.Path, first.Lease).Set(favoriteKey, 4);
            PrivateTagStore tags = new(profile.Path, first.Lease);
            tag = tags.Define("keep me").Tag;
            oldTarget = Assert.IsType<PrivateMediaTagTarget>(GalleryMediaTagTarget.Resolve(entry, out _));
            tags.SetMedia(tag, oldTarget, true);
            if (restartBeforeTheRecheck)
            {
                // Simulates closing the app and starting it again after the plugin was updated.
                await first.DisposeAsync();
                second = await Env.StartAsync(profile.Path, md5: false, media);
            }
            else first.Worker.Md5 = false;
            media.ChangeFirstFile = theBytesChanged;

            // Phase 2: the same profile, the source now reports no MD5 (new plugin), same URL and bytes.
            int requestsBefore = media.Total;
            IThread thread = second.Registry.Threads.SingleOrDefault()
                ?? (await second.Registry.AddAsync(ThreadUrl)).Thread!;
            await thread.checkThreadAsync(second.Queue);
            await second.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(300);
            await second.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));

            Observation after = await Observe(second, mediaDirectory);
            output.WriteLine("AFTER\n" + after.Describe());

            PrivateTagStoreSnapshot tagSnapshot = new PrivateTagStore(profile.Path, second.Lease).ReadSnapshot();
            // Old assignments are never removed.
            Assert.Single(tagSnapshot.TagsOf(oldTarget));
            // Keyed by thread, media id, variant and bytes: favorite and rating are untouched by the key change.
            Assert.True(new FavoriteStore(profile.Path, second.Lease).Contains(favoriteKey));
            Assert.Equal(4, new MediaRatingStore(profile.Path, second.Lease).List().Single(item => item.Key == favoriteKey).Value);
            // Re-download: the new key has no binding yet, so the host fetches each original once more.
            Assert.Equal(requestsBefore + 2, media.Total);
            Assert.Equal(2, after.Gallery.Count);
            Assert.All(after.Gallery, shown => Assert.NotNull(shown.AttachmentResolution));
            // The archive row moves to the new keys (without MD5); the old bindings stay in the manifest.
            Assert.All(after.AttachmentKeys, key => Assert.EndsWith(":md5=null", key));
            Assert.Empty(after.AttachmentKeys.Select(key => key.Split(':')[1] + ":" + key.Split(':')[2]).Intersect(before.AttachmentKeys.Select(key => key.Split(':')[1] + ":" + key.Split(':')[2])));
            Assert.Subset(after.BindingKeys.ToHashSet(), before.BindingKeys.ToHashSet());

            GalleryEntry shownFirst = after.Gallery.Single(item => item.Attachment!.Position == 0);
            PrivateMediaTagTarget currentFirst = Assert.IsType<PrivateMediaTagTarget>(GalleryMediaTagTarget.Resolve(shownFirst, out _));
            Assert.NotEqual(oldTarget.Reference.RevisionKey, currentFirst.Reference.RevisionKey);
            // The second file has no tags and gets none.
            PrivateMediaTagTarget currentSecond = Assert.IsType<PrivateMediaTagTarget>(GalleryMediaTagTarget.Resolve(
                after.Gallery.Single(item => item.Attachment!.Position == 1), out _));
            Assert.Empty(tagSnapshot.TagsOf(currentSecond));

            if (!theBytesChanged)
            {
                // Same bytes: same two assets, both current, no duplicate tile, nothing superseded.
                Assert.Equal(before.Assets.Select(asset => asset.AssetId).Order(), after.Assets.Select(asset => asset.AssetId).Order());
                Assert.All(after.Assets, asset => Assert.True(asset.IsCurrent));
                Assert.All(after.Gallery, shown => { Assert.False(shown.Unassigned); Assert.False(shown.Historical); });
                Assert.Equal(2, after.Tiles.Count);
                Assert.All(after.Tiles, tile => { Assert.Equal(MediaLibraryPresence.Present, tile.Presence); Assert.False(tile.Unassigned); Assert.False(tile.Historical); });
                Assert.Equal(4, after.BindingKeys.Count);
                // The tag is on the entry that is shown now, and exactly once more than before.
                Assert.Equal(tag, Assert.Single(tagSnapshot.TagsOf(currentFirst)).Id);
                Assert.Equal(2, tagSnapshot.MediaAssignments.Count);

                // Idempotent: another re-check, and a fresh store instance after a restart, add nothing.
                long revision = tagSnapshot.Revision;
                await thread.checkThreadAsync(second.Queue);
                await second.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(300);
                PrivateTagStoreSnapshot again = new PrivateTagStore(profile.Path, second.Lease).ReadSnapshot();
                Assert.Equal(2, again.MediaAssignments.Count);
                Assert.Equal(revision, again.Revision);
            }
            else
            {
                // Different bytes are a different file: a new asset, and it does not inherit the tag.
                Assert.NotEqual(oldTarget.Sha256, currentFirst.Sha256);
                Assert.Empty(tagSnapshot.TagsOf(currentFirst));
                Assert.Single(tagSnapshot.MediaAssignments);
            }
        }
        finally { await second.DisposeAsync(); }
    }

    /// <summary>
    /// The profile after a stop between the media commit and the tag write: the new key is bound in the manifest and
    /// the archive, but the tag file is as it was before the re-check (tags only on the old key).
    /// </summary>
    private async Task<(PrivateMediaTagTarget Old, TagId Tag, MediaCounter Media, string MediaDirectory)> CrashedProfileAsync(string profile)
    {
        MediaCounter media = new();
        Env first = await Env.StartAsync(profile, md5: true, media);
        try
        {
            AdmissionResult admitted = await first.Registry.AddAsync(ThreadUrl);
            await WaitAsync(() => admitted.Thread!.DownloadedMediaCount == 2);
            await first.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            string mediaDirectory = admitted.Thread!.DownloadDirectory;
            Observation before = await Observe(first, mediaDirectory);
            PrivateTagStore tags = new(profile, first.Lease);
            TagId tag = tags.Define("keep me").Tag;
            PrivateMediaTagTarget old = Assert.IsType<PrivateMediaTagTarget>(GalleryMediaTagTarget.Resolve(
                before.Gallery.Single(item => item.Attachment!.Position == 0), out _));
            tags.SetMedia(tag, old, true);
            Dictionary<string, byte[]> saved = Directory.GetFiles(profile, PrivateTagStore.FileName + "*")
                .ToDictionary(path => path, File.ReadAllBytes);

            first.Worker.Md5 = false;
            await admitted.Thread.checkThreadAsync(first.Queue);
            await first.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(300);
            await first.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Observation after = await Observe(first, mediaDirectory);
            Assert.Equal(4, after.BindingKeys.Count);
            Assert.Equal(2, new PrivateTagStore(profile, first.Lease).ReadSnapshot().MediaAssignments.Count);
            await first.DisposeAsync();

            // The stop: the media commit survived, the tag write did not.
            foreach (string path in Directory.GetFiles(profile, PrivateTagStore.FileName + "*")) File.Delete(path);
            foreach ((string path, byte[] bytes) in saved) File.WriteAllBytes(path, bytes);
            return (old, tag, media, mediaDirectory);
        }
        catch { await first.DisposeAsync(); throw; }
    }

    [Fact]
    public async Task ATagHandOverLostToAStopIsCompletedWhenTheDesktopHostStartsAndNeverDelaysItsStart()
    {
        using TempProfile profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "media") }.SaveConfig(profile.Path);
        (PrivateMediaTagTarget old, TagId tag, _, _) = await CrashedProfileAsync(profile.Path);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
            Assert.Single(new PrivateTagStore(profile.Path, lease).ReadSnapshot().MediaAssignments);

        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await host.MediaTagReconcileForTests.WaitAsync(TimeSpan.FromSeconds(30));
            PrivateTagStoreSnapshot snapshot = host.GetPrivateTagSnapshot().Snapshot!;
            // The tag is on the key the archive shows now, the old assignment stays, and the file counts once.
            Assert.Equal(2, snapshot.MediaAssignments.Count);
            PrivateMediaTagTarget current = snapshot.MediaAssignments.Select(item => item.Target)
                .Single(target => target.Reference.RevisionKey != old.Reference.RevisionKey);
            Assert.Equal(old.Sha256, current.Sha256);
            Assert.Equal(tag, Assert.Single(snapshot.TagsOf(current)).Id);
            Assert.Equal(1, snapshot.DistinctMediaCount(tag));

            // Idempotent: a second pass finds nothing to write.
            long revision = snapshot.Revision;
            await host.ReconcileMediaTagKeysAsync(CancellationToken.None);
            Assert.Equal(revision, host.GetPrivateTagSnapshot().Snapshot!.Revision);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact]
    public async Task AQueueWriteToTheTagFileIsNeverOverwrittenByTheHostAndTheViewRefreshesWithoutARestart()
    {
        using TempProfile profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "media") }.SaveConfig(profile.Path);
        (PrivateMediaTagTarget old, TagId tag, _, _) = await CrashedProfileAsync(profile.Path);
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await host.MediaTagReconcileForTests.WaitAsync(TimeSpan.FromSeconds(30));
            PrivateTagStoreSnapshot viewed = host.GetPublishedPrivateTagSnapshot().Snapshot!;
            PrivateMediaTagTarget current = viewed.MediaAssignments.Select(item => item.Target)
                .Single(target => target.Reference.RevisionKey != old.Reference.RevisionKey);

            // A writer that does not go through the host (the download queue) changes the file: define and assign a tag.
            ProfileLease lease = host.ProfileLeaseForTests;
            PrivateTagStore direct = new(profile.Path, lease);
            TagId dog;
            using (ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter())
            {
                dog = direct.DefineAdmitted("dog", admission).Tag;
                direct.SetMediaAdmitted(dog, current, true, admission);
            }
            Assert.True(direct.ReadSnapshot().Revision > viewed.Revision);
            // The published view is older until it is refreshed, and a host write built on it is refused, not applied over the new state.
            Assert.Equal(viewed.Revision, host.GetPublishedPrivateTagSnapshot().Snapshot!.Revision);
            await Assert.ThrowsAsync<PrivateTagStoreException>(() => host.SetMediaTagsAsync(tag, [current], false, viewed.Revision));

            // A host write without an expected revision starts from the file, so the queue's write survives it.
            PrivateTagCommit bird = await host.DefinePrivateTagAsync("bird");
            Assert.Equal(dog, Assert.Single(bird.Snapshot.TagsOf(current), item => item.Id == dog).Id);
            Assert.NotNull(bird.Snapshot.Find(dog));
            Assert.Contains(tag, bird.Snapshot.TagsOf(current).Select(item => item.Id));

            // And the other way round: a write the host did not see is shown after the refresh the queue triggers.
            using (ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter())
                direct.SetMediaAdmitted(bird.Tag, current, true, admission);
            Assert.DoesNotContain(bird.Tag, host.GetPublishedPrivateTagSnapshot().Snapshot!.TagsOf(current).Select(item => item.Id));
            await host.RefreshPublishedPrivateTagsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(bird.Tag, host.GetPublishedPrivateTagSnapshot().Snapshot!.TagsOf(current).Select(item => item.Id));
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact]
    public async Task ATagHandOverLostToAStopIsCompletedByTheNextCaptureOfTheThread()
    {
        using TempProfile profile = new();
        (PrivateMediaTagTarget old, TagId tag, MediaCounter media, _) = await CrashedProfileAsync(profile.Path);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
            Assert.Single(new PrivateTagStore(profile.Path, lease).ReadSnapshot().MediaAssignments);
        Env restarted = await Env.StartAsync(profile.Path, md5: false, media);
        int announced = 0;
        restarted.Queue.MediaTagsCarriedOver += () => Interlocked.Increment(ref announced);
        try
        {
            IThread thread = restarted.Registry.Threads.SingleOrDefault() ?? (await restarted.Registry.AddAsync(ThreadUrl)).Thread!;
            await thread.checkThreadAsync(restarted.Queue);
            await restarted.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(300);
            await restarted.Queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            PrivateTagStoreSnapshot snapshot = new PrivateTagStore(profile.Path, restarted.Lease).ReadSnapshot();
            // The queue told its host that it changed the tags, so a host can refresh its view.
            Assert.True(Volatile.Read(ref announced) >= 1);
            output.WriteLine("media requests: " + media.Total + ", assignments: " + snapshot.MediaAssignments.Count);
            Assert.Equal(2, snapshot.MediaAssignments.Count);
            Assert.Equal(tag, Assert.Single(snapshot.TagsOf(snapshot.MediaAssignments.Select(item => item.Target)
                .Single(target => target.Reference.RevisionKey != old.Reference.RevisionKey))).Id);
        }
        finally { await restarted.DisposeAsync(); }
    }

    private static async Task<Observation> Observe(Env env, string mediaDirectory)
    {
        await using ArchiveReader reader = await env.Content.OpenReaderAsync();
        ArchivedThreadView view = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(mediaDirectory);
        List<string> attachmentKeys = new();
        foreach (CapturedPost post in view.GetPosts())
            foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
                attachmentKeys.Add(post.PostId + "/" + attachment.Position + ":" + attachment.AttachmentRevisionKey + ":md5=" + (attachment.ExpectedMd5 ?? "null"));
        GalleryThreadResult gallery = GallerySelection.Build(view, [assets], allSavedVersions: false, includeOtherMedia: true);
        MediaLibraryEntry[] tiles = MediaLibraryIndexBuilder.EntriesOf(view, [assets]).ToArray();
        return new Observation(attachmentKeys, assets.AttachmentBindings.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            assets.GetPage().ToArray(), gallery.Entries.ToArray(), tiles);
    }

    private sealed record Observation(IReadOnlyList<string> AttachmentKeys, IReadOnlyList<string> BindingKeys,
        IReadOnlyList<MediaAssetReference> Assets, IReadOnlyList<GalleryEntry> Gallery, IReadOnlyList<MediaLibraryEntry> Tiles)
    {
        internal string Describe()
        {
            StringBuilder text = new();
            text.AppendLine("archive attachments:");
            foreach (string key in AttachmentKeys) text.AppendLine("  " + key);
            text.AppendLine("manifest assets: " + Assets.Count + " (" + string.Join(", ", Assets.Select(asset => asset.AssetId[..8] + (asset.IsCurrent ? " current" : " retained"))) + ")");
            text.AppendLine("manifest bindings: " + BindingKeys.Count);
            foreach (string key in BindingKeys) text.AppendLine("  " + key);
            text.AppendLine("gallery entries: " + Gallery.Count);
            foreach (GalleryEntry entry in Gallery)
                text.AppendLine($"  post={entry.Post?.PostId} pos={entry.Attachment?.Position} unassigned={entry.Unassigned} historical={entry.Historical} resolved={(entry.AttachmentResolution != null)} asset={entry.Asset.AssetId[..8]}");
            text.AppendLine("media page tiles: " + Tiles.Count);
            foreach (MediaLibraryEntry tile in Tiles)
                text.AppendLine($"  post={tile.PostId} pos={tile.Position} presence={tile.Presence} unassigned={tile.Unassigned} historical={tile.Historical} asset={tile.AssetId?[..8]}");
            return text.ToString();
        }
    }

    private sealed class MediaCounter
    {
        private int _total;
        internal int Total => Volatile.Read(ref _total);
        internal volatile bool ChangeFirstFile;
        internal void Hit() => Interlocked.Increment(ref _total);
    }

    private sealed class Env : IAsyncDisposable
    {
        internal required ProfileLease Lease { get; init; }
        internal required ThreadContentRuntime Content { get; init; }
        internal required Registry Registry { get; init; }
        internal required DownloadQueue Queue { get; init; }
        internal required PluginSourceImageBoard Source { get; init; }
        internal required HttpClient Media { get; init; }
        internal required Worker Worker { get; init; }

        internal static async Task<Env> StartAsync(string profile, bool md5, MediaCounter counter)
        {
            ProfileLease lease = ProfileLease.Acquire(profile);
            ThreadContentRuntime content = new(profile, lease);
            await content.InitializeAsync();
            PluginDescription description = Description();
            Worker worker = new(description, md5);
            HttpClient media = new(new Images(counter));
            PluginSourceImageBoard source = new(description, "2ch", _ => Task.FromResult<IPluginWorkerClient>(worker), media);
            DownloadQueue queue = new(2, 1048576);
            Registry registry = new(new[] { source }, queue, Path.Combine(profile, "media"), TimeSpan.FromHours(1), 3600,
                new SessionStore(profile, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile, lease));
            await registry.StartAsync(lease);
            return new Env { Lease = lease, Content = content, Registry = registry, Queue = queue, Source = source, Media = media, Worker = worker };
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await Content.DisposeAsync();
            Source.Dispose();
            Media.Dispose();
            Lease.Dispose();
        }
    }

    private static PluginDescription Description() => new("meowchan.source.2ch", "2ch / Двач", "1.0.0", SdkContract.Version,
        "Synthetic 2ch-shaped source for the revision-key investigation.", [Source()], Array.Empty<FilterDescription>(),
        new SettingsSchema("1", Array.Empty<SettingDefinition>()),
        [new InformationEntry("support", "Accepted links", InformationKind.Text, "Synthetic.")]);

    private static SourceDescription Source() => new("2ch", "2ch / Двач", new Uri("https://2ch.su/"),
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, "2ch.su", "/{board}/", IsCanonical: true),
         new UrlRoute(SourceResourceKind.Board, "2ch.hk", "/{board}/"),
         new UrlRoute(SourceResourceKind.Thread, "2ch.su", "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false) { ThreadIdentifierKind = RouteIdentifierKind.Decimal },
         new UrlRoute(SourceResourceKind.Thread, "2ch.hk", "/{board}/res/{thread}.html", AllowTrailingSlash: false) { ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration("2ch.hk", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia),
         new HttpHostDeclaration("2ch.su", HttpHostPurpose.OriginalMedia)],
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), PostOrderKind.DecimalPostId)
    { Maturity = SourceMaturity.Experimental };

    private sealed class Worker(PluginDescription description, bool initialMd5) : IPluginWorkerClient
    {
        public bool Md5 { get; set; } = initialMd5;
        public PluginDescription Description { get; } = description;
        public PluginWorkerIdentity Identity { get; } = new("test", "build", "config");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, Array.Empty<string>());

        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        {
            SourceAttachment Attachment(int position) => new(position, "123-" + position, Url: new Uri("https://2ch.hk/b/src/123/f" + position + ".png"),
                OriginalFileName: "file" + position + ".png", Extension: ".png", ExpectedSize: null,
                ExpectedMd5: Md5 ? Convert.ToBase64String(MD5.HashData(Files[position])) : null)
            { ProviderSizeHint = new SourceSizeHint("24", "Кб", SourceSizePrecision.Unspecified) };
            return Task.FromResult<ThreadReadResult>(new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread, new Uri(ThreadUrl),
                "Synthetic", SourceThreadState.Active,
                [new SourcePost("123", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments, Text: "<p>op</p>", TextFormat: SourceTextFormat.Html,
                    Attachments: [Attachment(0), Attachment(1)])])));
        }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FieldValidationError>>(Array.Empty<FieldValidationError>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Images(MediaCounter counter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counter.Hit();
            int index = request.RequestUri!.AbsolutePath.EndsWith("f0.png", StringComparison.Ordinal) ? 0 : 1;
            byte[] bytes = index == 0 && counter.ChangeFirstFile ? [42, 42, 42, 42] : Files[index];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TempProfile : IDisposable
    {
        internal string Path { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-md5-drop-");
        public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { } }
    }
}
