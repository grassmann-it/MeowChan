using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class MediaLibraryIndexTests
{
    private static readonly byte[] Shared = { 1, 2, 3 };
    private static readonly byte[] Other = { 4, 5, 6, 7 };

    // A saved file; Post/Position bind it to a captured attachment, null leaves it unassigned.
    internal sealed record File_(string Name, string MediaId, byte[] Bytes, string? Post = null, int Position = 0);

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Url(string name) => "https://offline.invalid/" + name;

    internal static CapturedAttachment Attachment(int position, string mediaId, string name, byte[]? bytes = null) =>
        new(position, mediaId, "original", Url(name), name, ExpectedSize: bytes?.Length, ExpectedMd5: bytes == null ? null : Md5(bytes))
        { ExpectedSha256 = bytes == null ? null : Sha(bytes) };

    /// <summary>Writes a manifest like real captures do: files plus exact bindings to post attachment versions.</summary>
    internal static async Task WriteManifestAsync(string directory, ResourceId resource, params File_[] files)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new(), assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        foreach (File_ file in files)
        {
            object entry = new { MediaIdentifier = file.MediaId, Variant = "original", SourceUrl = Url(file.Name), FileName = file.Name,
                OriginalFileName = file.Name, Length = file.Bytes.Length, Md5 = Md5(file.Bytes), Sha256 = Sha(file.Bytes) };
            entries[file.Name] = entry;
        }
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot plain = await MediaAssetReader.OpenAsync(directory);
        foreach (File_ file in files)
        {
            MediaAssetReference asset = plain.GetPage(0, 500).Single(item => item.RequestedFileName == file.Name);
            assets[asset.AssetId] = new { RequestedFileName = file.Name, Entry = entries[file.Name] };
            if (file.Post == null) continue;
            CapturedAttachment observed = Attachment(file.Position, file.MediaId, file.Name, file.Bytes);
            MediaProviderMetadata provider = new(file.MediaId, "original", Url(file.Name), file.Name,
                observed.ExpectedSize, observed.ExpectedMd5, observed.ExpectedSha256);
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, file.Post, file.Position, provider);
            bindings[key] = new(new(resource, file.Post, file.Position, key), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
                asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256),
                new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
    }

    internal static async Task CaptureAsync(string profile, ResourceId id, string title, string directory, params CapturedPost[] posts)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + id.Thread, title,
            ThreadSourceState.Archived, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), directory, posts));
    }

    internal static Task<MediaLibrarySnapshot> BuildAsync(string profile, MediaLibrarySnapshot? previous = null,
        List<MediaLibraryProgress>? progress = null, CancellationToken token = default) =>
        MediaLibraryIndexBuilder.BuildAsync(t => ArchiveReader.OpenAsync(profile, t), MediaAssetReader.OpenAsync, previous,
            progress == null ? null : new SyncProgress(progress), token);

    private sealed class SyncProgress(List<MediaLibraryProgress> items) : IProgress<MediaLibraryProgress>
    {
        public void Report(MediaLibraryProgress value) => items.Add(value);
    }

    internal static readonly ResourceId A = new("src", "g", "100");
    internal static readonly ResourceId B = new("src", "g", "200");

    internal static async Task SeedAsync(string profile)
    {
        string a = Path.Combine(profile, "media", "a"), b = Path.Combine(profile, "media", "b");
        // A: one saved post file, one captured but never saved, one saved file without a post.
        await WriteManifestAsync(a, A, new File_("shared.png", "m1", Shared, "100"), new File_("loose.webm", "m9", Other));
        await CaptureAsync(profile, A, "Thread A", a,
            new CapturedPost("100", 0, PostFieldPresence.All, Html: "op", Attachments: [Attachment(0, "m1", "shared.png", Shared)]),
            new CapturedPost("101", 1, PostFieldPresence.All, Html: "reply", Attachments: [Attachment(0, "m2", "gone.gif")]));
        // B: the same bytes as A's post file, in another thread.
        await WriteManifestAsync(b, B, new File_("copy.png", "x1", Shared, "200"));
        await CaptureAsync(profile, B, "Thread B", b,
            new CapturedPost("200", 0, PostFieldPresence.All, Html: "op", Attachments: [Attachment(0, "x1", "copy.png", Shared)]));
    }

    [Fact]
    public async Task IndexListsSavedMissingAndUnassignedMediaOfEveryThread()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path);

        Assert.True(snapshot.Complete);
        Assert.Empty(snapshot.Warnings);
        Assert.Equal(new[] { A, B }, snapshot.Threads.Select(thread => thread.Thread).OrderBy(id => id.Thread));
        MediaLibraryEntry saved = Assert.Single(snapshot.Entries, entry => entry.Thread == A && entry.PostId == "100");
        Assert.Equal((MediaLibraryPresence.Present, SavedMediaKind.Image, "shared.png", 0), (saved.Presence, saved.Kind, saved.Name, saved.Position!.Value));
        Assert.Equal(Sha(Shared), saved.Sha256);
        Assert.Equal("Thread A", saved.ThreadTitle);
        MediaLibraryEntry missing = Assert.Single(snapshot.Entries, entry => entry.Thread == A && entry.PostId == "101");
        Assert.Equal((MediaLibraryPresence.Missing, SavedMediaKind.Image, "gone.gif"), (missing.Presence, missing.Kind, missing.Name));
        Assert.Null(missing.Sha256);
        Assert.Null(missing.AssetId);
        MediaLibraryEntry loose = Assert.Single(snapshot.Entries, entry => entry.Thread == A && entry.Unassigned);
        Assert.Equal((SavedMediaKind.Video, (string?)null, MediaLibraryPresence.Present), (loose.Kind, loose.PostId, loose.Presence));
        MediaLibraryEntry copy = Assert.Single(snapshot.Entries, entry => entry.Thread == B);
        // Same bytes, different occurrence: both are kept; grouping is the page's job.
        Assert.Equal(saved.Sha256, copy.Sha256);
        Assert.NotEqual(saved.Directory, copy.Directory);
        Assert.Equal(4, snapshot.Entries.Count);
    }

    [Fact]
    public async Task UnchangedThreadsAreReusedAndChangedOnesReadAgain()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        MediaLibrarySnapshot first = await BuildAsync(temp.Path);

        List<MediaLibraryProgress> progress = new();
        MediaLibrarySnapshot second = await BuildAsync(temp.Path, first, progress);
        Assert.Equal(new MediaLibraryProgress(2, 2, 2), progress.Last());
        Assert.Equal(first.Entries.OrderBy(Key), second.Entries.OrderBy(Key));

        // B gets a new post (catalog revision changes) and A's manifest gets a new file (generation changes).
        await WriteManifestAsync(Path.Combine(temp.Path, "media", "b"), B, new File_("copy.png", "x1", Shared, "200"), new File_("new.jpg", "x2", Other, "201"));
        await CaptureAsync(temp.Path, B, "Thread B", Path.Combine(temp.Path, "media", "b"),
            new CapturedPost("200", 0, PostFieldPresence.All, Html: "op", Attachments: [Attachment(0, "x1", "copy.png", Shared)]),
            new CapturedPost("201", 1, PostFieldPresence.All, Html: "more", Attachments: [Attachment(0, "x2", "new.jpg", Other)]));
        progress.Clear();
        MediaLibrarySnapshot third = await BuildAsync(temp.Path, second, progress);
        Assert.Equal(new MediaLibraryProgress(2, 2, 1), progress.Last());
        Assert.Contains(third.Entries, entry => entry.Thread == B && entry.PostId == "201" && entry.Presence == MediaLibraryPresence.Present);

        // A's manifest changes (a newly saved loose file) without any catalog change: A is read again.
        await WriteManifestAsync(Path.Combine(temp.Path, "media", "a"), A, new File_("shared.png", "m1", Shared, "100"),
            new File_("loose.webm", "m9", Other), new File_("later.jpg", "m8", Encoding.UTF8.GetBytes("later")));
        progress.Clear();
        MediaLibrarySnapshot fourth = await BuildAsync(temp.Path, third, progress);
        Assert.Equal(new MediaLibraryProgress(2, 2, 1), progress.Last());
        Assert.Contains(fourth.Entries, entry => entry.Thread == A && entry.Unassigned && entry.Name == "later.jpg");
    }

    private static string Key(MediaLibraryEntry entry) => entry.Thread + "|" + entry.PostId + "|" + entry.Position + "|" + entry.AssetId;

    internal static async Task SeedMetadataThreadsAsync(string profile, int count)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.WriteAsync((connection, transaction) =>
        {
            using SqliteCommand insert = ArchiveSql.Command(connection, transaction, """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<$count)
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                SELECT 'metadata','b',printf('%05d',n),'https://offline.invalid/'||n,'Metadata thread',$media,0,1,
                    '2026-11-01T00:00:00.0000000+00:00',0 FROM numbers
                """);
            insert.Parameters.AddWithValue("$count", count);
            insert.Parameters.AddWithValue("$media", Path.Combine(profile, "media"));
            return insert.ExecuteNonQuery();
        });
    }

    [Fact]
    public async Task BatchedFoldersPreservePageOrderAndReuseOnlyUnchangedManifestIdentities()
    {
        using TempDirectory temp = new();
        await SeedMetadataThreadsAsync(temp.Path, ArchiveReader.MediaIndexPageSize - 1);
        ResourceId otherBoard = A with { Board = "z" };
        string first = Path.Combine(temp.Path, "a-media");
        string last = Path.Combine(temp.Path, "z-media");
        string other = Path.Combine(temp.Path, "other-media");
        await WriteManifestAsync(last, A, new File_("z.jpg", "mz", Other));
        await WriteManifestAsync(first, A, new File_("a.jpg", "ma", Shared));
        await WriteManifestAsync(other, otherBoard, new File_("other.jpg", "mo", Shared));
        await CaptureAsync(temp.Path, A, "Thread A", last);
        await CaptureAsync(temp.Path, A, "Thread A", first);
        await CaptureAsync(temp.Path, otherBoard, "Other board", other);

        List<string> opened = new();
        MediaManifestCache manifests = new((directory, token) =>
        {
            opened.Add(directory);
            return MediaAssetReader.OpenAsync(directory, token);
        }, identity: true);
        List<MediaLibraryProgress> progress = new();
        Task<MediaLibrarySnapshot> Build(MediaLibrarySnapshot? previous = null) => MediaLibraryIndexBuilder.BuildAsync(
            token => ArchiveReader.OpenAsync(temp.Path, token), manifests, previous, new SyncProgress(progress));

        MediaLibrarySnapshot initial = await Build();
        int total = ArchiveReader.MediaIndexPageSize + 1;
        Assert.True(initial.Complete);
        Assert.Equal(total, initial.Threads.Count);
        Assert.All(initial.Threads.Take(total - 2), thread => Assert.Empty(thread.Manifests));
        Assert.Equal(new[] { A, otherBoard }, initial.Threads.TakeLast(2).Select(thread => thread.Thread));
        Assert.Equal(new[] { first, last, other }, opened);
        Assert.Equal(new[] { first, last }, initial.Threads[^2].Manifests.Select(manifest => manifest.Directory));
        Assert.All(initial.Threads.SelectMany(thread => thread.Manifests), manifest =>
        {
            Assert.NotNull(manifest.Generation);
            Assert.NotNull(manifest.Identity);
        });

        opened.Clear();
        MediaLibrarySnapshot warm = await Build(initial);
        Assert.Empty(opened);
        Assert.Equal(new MediaLibraryProgress(total, total, total), progress.Last());
        Assert.True(MediaLibrarySnapshot.SameContent(initial, warm));

        await WriteManifestAsync(last, A, new File_("changed.jpg", "changed", Shared));
        MediaLibrarySnapshot changed = await Build(warm);
        Assert.Equal(new[] { first, last }, opened);
        Assert.Equal(new MediaLibraryProgress(total, total, total - 1), progress.Last());
        Assert.True(MediaLibrarySnapshot.SameContent(await Build(), changed));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaptureOrDeletionBetweenManifestAndPostReadsDoesNotPublishMixedEntries(bool delete, bool recapture)
    {
        using TempDirectory temp = new();
        string oldDirectory = Path.Combine(temp.Path, "old");
        string newDirectory = Path.Combine(temp.Path, "new");
        await WriteManifestAsync(oldDirectory, A, new File_("old.jpg", "old", Shared));
        await WriteManifestAsync(newDirectory, A, new File_("new.jpg", "new", Other));
        await CaptureAsync(temp.Path, A, "Old thread", oldDirectory);

        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        MediaLibrarySnapshot raced = await MediaLibraryIndexBuilder.BuildAsync(token => ArchiveReader.OpenAsync(temp.Path, token),
            async (directory, token) =>
            {
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token);
                if (delete) await catalog.DeleteThreadsAsync([A], token);
                if (recapture)
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(A, Url(A.Thread!), "Updated thread",
                        ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, newDirectory, []), token);
                return manifest;
            });

        Assert.Empty(raced.Entries);
        if (recapture)
        {
            Assert.False(raced.Complete);
            Assert.False(Assert.Single(raced.Threads).Complete);
        }
        else Assert.Empty(raced.Threads);

        MediaLibrarySnapshot rebuilt = await BuildAsync(temp.Path, raced);
        MediaLibrarySnapshot cold = await BuildAsync(temp.Path);
        Assert.True(rebuilt.Complete);
        Assert.True(MediaLibrarySnapshot.SameContent(cold, rebuilt));
        if (recapture) Assert.Contains(rebuilt.Entries, entry => entry.Directory == newDirectory && entry.Name == "new.jpg");
    }

    [Fact]
    public async Task MissingAndDamagedManifestsNeverInventSavedFiles()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        Directory.Delete(Path.Combine(temp.Path, "media", "b"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "media", "a", ".meowchan-manifest.json"), "{damaged");

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path);

        Assert.All(snapshot.Entries, entry => Assert.Equal(MediaLibraryPresence.Missing, entry.Presence));
        Assert.Equal(3, snapshot.Entries.Count); // Two captured attachments in A, one in B; no loose file without a manifest.
        Assert.False(snapshot.Complete);
        Assert.Single(snapshot.Warnings, warning => warning.Contains("damaged", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Warnings, warning => warning.Contains(temp.Path, StringComparison.Ordinal));
        // A missing folder is a valid state (nothing saved), an unknown generation is never reused.
        MediaLibrarySnapshot again = await BuildAsync(temp.Path, snapshot);
        Assert.Equal(snapshot.Entries.OrderBy(Key), again.Entries.OrderBy(Key));
    }

    [Fact]
    public async Task CancellationStopsTheBuild()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(temp.Path, token: cancel.Token));
        int opened = 0;
        using CancellationTokenSource later = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryIndexBuilder.BuildAsync(
            t => ArchiveReader.OpenAsync(temp.Path, t),
            (directory, t) => { if (++opened == 1) later.Cancel(); return MediaAssetReader.OpenAsync(directory, t); },
            cancellationToken: later.Token));
        Assert.Equal(1, opened);
        MediaLibrarySnapshot rebuilt = await BuildAsync(temp.Path);
        Assert.True(rebuilt.Complete);
        Assert.Empty(rebuilt.Warnings);
    }

    [Fact]
    public async Task CacheRoundTripsAndAnyDamageMeansRebuild()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        MediaLibrarySnapshot built = await BuildAsync(temp.Path);
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);

        MediaLibraryIndexCacheSync.Save(path, built);
        MediaLibrarySnapshot? loaded = MediaLibraryIndexCacheSync.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(built.Entries.OrderBy(Key), loaded!.Entries.OrderBy(Key));
        Assert.Equal(built.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete)), loaded.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!).Where(file => file != path));
        List<MediaLibraryProgress> progress = new();
        await BuildAsync(temp.Path, loaded, progress);
        Assert.Equal(2, progress.Last().Reused);

        string valid = await File.ReadAllTextAsync(path);
        // Version 4 is current (manifest file identities); version 3 is still read; an older or a newer document is no cache and rebuilds.
        Assert.Contains("\"Version\":4", valid);
        Assert.NotNull(MediaLibraryIndexCacheSync.Load(path));
        foreach (string damaged in new[] { "{bad", valid.Replace("\"Version\":4", "\"Version\":5"), valid.Replace("\"Version\":4", "\"Version\":2"),
                     valid.Replace("\"Threads\":[", "\"Extra\":1,\"Threads\":["),
                     valid.Replace("\"Sha\":\"" + Sha(Shared) + "\"", "\"Sha\":\"nothex\"") })
        {
            await File.WriteAllTextAsync(path, damaged);
            Assert.Null(MediaLibraryIndexCacheSync.Load(path));
        }
        File.Delete(path);
        Assert.Null(MediaLibraryIndexCacheSync.Load(path));
        // Saving again replaces without leaving backups or preserved copies behind.
        MediaLibraryIndexCacheSync.Save(path, built);
        MediaLibraryIndexCacheSync.Save(path, built);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task TenThousandOccurrencesBuildAndReuseWithinBounds()
    {
        using TempDirectory temp = new();
        const int threads = 200, perThread = 50;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
            for (int index = 0; index < threads; index++)
            {
                ResourceId id = new("scale", "b", (1000 + index).ToString());
                string directory = Path.Combine(temp.Path, "media", id.Thread!);
                // Half of the attachments are saved, half are only captured.
                byte[] Bytes(int n) => Encoding.UTF8.GetBytes(id.Thread + "-" + n);
                File_[] files = Enumerable.Range(0, perThread / 2).Select(n => new File_("f" + n + ".jpg", "m" + n, Bytes(n), "p" + n)).ToArray();
                await WriteManifestAsync(directory, id, files);
                CapturedPost[] posts = Enumerable.Range(0, perThread).Select(n => new CapturedPost("p" + n, n, PostFieldPresence.All,
                    Html: "x", Attachments: [Attachment(0, "m" + n, "f" + n + ".jpg", n < perThread / 2 ? Bytes(n) : null)])).ToArray();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + id.Thread, "t" + index,
                    ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
            }

        Stopwatch watch = Stopwatch.StartNew();
        MediaLibrarySnapshot built = await BuildAsync(temp.Path);
        TimeSpan cold = watch.Elapsed;
        Assert.Equal(threads * perThread, built.Entries.Count);
        Assert.Equal(threads * perThread / 2, built.Entries.Count(entry => entry.Presence == MediaLibraryPresence.Present));
        Assert.True(cold < TimeSpan.FromSeconds(60), "Cold build took " + cold);

        List<MediaLibraryProgress> progress = new();
        MediaLibrarySnapshot warm = await BuildAsync(temp.Path, built, progress);
        Assert.Equal(threads, progress.Last().Reused);
        Assert.Equal(built.Entries.Count, warm.Entries.Count);
        // Reuse is proved by the progress counter above (every thread reused, none read again); comparing two wall-clock
        // samples of the same build says nothing more and flips on a loaded machine.
    }

    [Fact]
    public async Task IndexEntriesResolveBackToTheSameGalleryEntriesAndChangedFilesAreLeftOut()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path);
        MediaLibraryEntry[] shown = MediaLibraryView.Select(snapshot.Entries, MediaLibrarySort.ByThread, includeMissing: true).ToArray();

        IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(shown, t => ArchiveReader.OpenAsync(temp.Path, t), MediaAssetReader.OpenAsync);

        // Missing entries have no file; the three saved ones come back in page order with their post binding.
        Assert.Equal(3, entries.Count);
        MediaLibraryEntry[] present = shown.Where(entry => entry.Presence == MediaLibraryPresence.Present).ToArray();
        Assert.Equal(present.Select(entry => (entry.Thread, entry.AssetId, entry.PostId)),
            entries.Select(entry => (entry.Key.Thread, (string?)entry.Key.AssetId, entry.Post?.PostId)));
        GalleryEntry bound = entries.Single(entry => entry.Key.Thread == A && entry.Post?.PostId == "100");
        Assert.NotNull(bound.AttachmentResolution?.Binding);
        Assert.True(entries.Single(entry => entry.Key.Thread == A && entry.Post == null).Unassigned);

        // B's file is replaced on disk after indexing: its stale entry is not resolved to the new bytes.
        await WriteManifestAsync(Path.Combine(temp.Path, "media", "b"), B, new File_("copy.png", "x1", Other, "200"));
        IReadOnlyList<GalleryEntry> after = await MediaLibraryGallery.ResolveAsync(shown, t => ArchiveReader.OpenAsync(temp.Path, t), MediaAssetReader.OpenAsync);
        Assert.DoesNotContain(after, entry => entry.Key.Thread == B);
        Assert.Equal(2, after.Count);
    }
}
