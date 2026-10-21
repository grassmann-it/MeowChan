using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ArchiveDeletionHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DeletesManifestMediaAndPosterAndKeepsSuppressionAfterRestart()
    {
        using TempDirectory temp = new();
        ResourceId id = new("4chan", "g", "123");
        string media = Path.Combine(temp.Path, "downloads", "g", "123");
        string posterCache = Path.Combine(temp.Path, "cache", "video-posters-v1");
        byte[] original = [1, 2, 3, 4];
        string selectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string unrelatedHash = new string('b', 64);
        new Config { DownloadsPaused = true, DownloadPath = Path.Combine(temp.Path, "downloads") }.SaveConfig(temp.Path);
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(posterCache);
        await WriteManifestAsync(media, original);
        await File.WriteAllBytesAsync(Path.Combine(posterCache, selectedHash + ".png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(posterCache, unrelatedHash + ".png"), [2]);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
            await catalog.RegisterThreadAsync(Registration(id, media));

        using CatalogTransport firstTransport = new();
        using HttpClient firstClient = new(firstTransport);
        await using (DesktopSessionHost first = new(temp.Path, [new ObservedCatalogProvider(firstClient)]))
        {
            await first.StartAsync().WaitAsync(Timeout);
            await first.AddAsync("https://boards.4chan.org/g/").WaitAsync(Timeout);
            await first.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
            Assert.Single(first.GetView().Threads);
            ArchivePermanentDeletionPlan plan = await first.PrepareArchiveDeletionAsync([id]);
            Assert.Equal(selectedHash, Assert.Single(plan.Files.Directories.SelectMany(directory => directory.AssetSha256)));
            ArchivePermanentDeletionResult result = await first.DeleteArchivePermanentlyAsync(plan).WaitAsync(Timeout);
            Assert.Equal(new[] { id }, result.DeletedThreads);
            Assert.Empty(result.Errors);
            Assert.True(first.IsPaused);
            Assert.Empty(first.GetView().Threads);
            Assert.Single(first.GetView().Boards);
        }
        Assert.False(File.Exists(Path.Combine(media, "clip.webm")));
        Assert.False(File.Exists(Path.Combine(media, ".meowchan-manifest.json")));
        Assert.False(File.Exists(Path.Combine(posterCache, selectedHash + ".png")));
        Assert.True(File.Exists(Path.Combine(posterCache, unrelatedHash + ".png")));

        using CatalogTransport restoredTransport = new();
        using HttpClient restoredClient = new(restoredTransport);
        ObservedCatalogProvider restoredProvider = new(restoredClient);
        await using DesktopSessionHost restored = new(temp.Path, [restoredProvider]);
        await restored.StartAsync().WaitAsync(Timeout);
        Assert.Empty(restored.GetView().Threads);
        await restored.SetPausedAsync(false).WaitAsync(Timeout);
        await UntilAsync(() => restoredProvider.Board.FinishedPolls > 0);
        Assert.Empty(restored.GetView().Threads);
        Assert.Equal(0, restoredTransport.ThreadRequests);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Null(await reader.ReadThreadAsync(id));
    }

    [Fact]
    public async Task DeletePreservesSharedThreadAndPreviousPauseState()
    {
        using TempDirectory temp = new();
        ResourceId shared = new("4chan", "g", "301");
        ResourceId kept = new("4chan", "g", "302");
        ResourceId exclusive = new("4chan", "g", "303");
        string sharedDirectory = Path.Combine(temp.Path, "shared");
        string ownDirectory = Path.Combine(temp.Path, "own");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            await catalog.RegisterThreadAsync(Registration(shared, sharedDirectory));
            await catalog.RegisterThreadAsync(Registration(kept, sharedDirectory));
            await catalog.RegisterThreadAsync(Registration(exclusive, ownDirectory));
            FavoriteStore favorites = new(temp.Path, lease);
            Assert.True(favorites.SetFavorite(Favorite(shared), true));
            Assert.True(favorites.SetFavorite(Favorite(exclusive), true));
        }

        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await host.SetPausedAsync(true);
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync(new[] { shared, exclusive });
        Assert.Contains(sharedDirectory, plan.Catalog.SharedMediaDirectories);
        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(plan);
        Assert.Equal(new[] { exclusive }, result.DeletedThreads);
        Assert.NotEmpty(result.Errors);
        Assert.True(host.IsPaused);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.NotNull(await reader.ReadThreadAsync(shared));
        Assert.NotNull(await reader.ReadThreadAsync(kept));
        Assert.Null(await reader.ReadThreadAsync(exclusive));
        Assert.Equal(new[] { shared }, host.GetFavorites().Select(entry => entry.Key.Thread));
    }

    [Fact]
    public async Task ChangedCatalogPlanIsRejectedBeforeDeletion()
    {
        using TempDirectory temp = new();
        ResourceId id = new("4chan", "g", "401");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
            await catalog.RegisterThreadAsync(Registration(id, Path.Combine(temp.Path, "initial")));

        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync(new[] { id });
        // A second writer cannot open the leased profile, but the host's catalog may change
        // through normal source capture. Simulate that by changing the plan presented to execution.
        ArchivePermanentDeletionPlan stale = plan with
        {
            Catalog = plan.Catalog with
            {
                Threads = new[] { plan.Catalog.Threads[0] with
                    { Summary = plan.Catalog.Threads[0].Summary with { Title = "stale" } } }
            }
        };
        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(stale);
        Assert.Empty(result.DeletedThreads);
        Assert.NotEmpty(result.Errors);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.NotNull(await reader.ReadThreadAsync(id));
    }

    private static ArchiveThreadRegistration Registration(ResourceId id, string directory) =>
        new(id, $"https://boards.4chan.org/{id.Board}/thread/{id.Thread}", id.Thread!, directory, true);

    private static FavoriteEntry Favorite(ResourceId id) =>
        new(new FavoriteAssetKey(id, "media", "original", new string('a', 64)), 12, "file.jpg");

    private static async Task WriteManifestAsync(string media, byte[] bytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(media, "clip.webm"), bytes);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object> { ["clip.webm"] = new
            {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/clip.webm",
                FileName = "clip.webm", Length = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            } }
        }));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(Timeout);
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class ObservedCatalogProvider : FourChanImageBoard
    {
        public ObservedCatalogProvider(HttpClient client) : base(client) => Board = new ObservedBoard(this);
        public ObservedBoard Board { get; }
        public override IBoard GetBoard(Uri uri) => Board;
    }

    private sealed class ObservedBoard(IImageBoard provider) : FourChanBoard(provider, new Uri("https://boards.4chan.org/g/"))
    {
        private int _finishedPolls;
        public int FinishedPolls => Volatile.Read(ref _finishedPolls);
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _finishedPolls);
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CatalogTransport : HttpMessageHandler
    {
        private int _threadRequests;
        public int ThreadRequests => Volatile.Read(ref _threadRequests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body = request.RequestUri!.AbsolutePath switch
            {
                "/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Fixture\"}]}",
                "/g/catalog.json" => "[{\"page\":1,\"threads\":[{\"no\":123}]}]",
                "/g/thread/123.json" => "{\"posts\":[{\"no\":123}]}" ,
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
            };
            if (request.RequestUri.AbsolutePath == "/g/thread/123.json") Interlocked.Increment(ref _threadRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
