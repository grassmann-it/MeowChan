using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

string profile = Path.GetFullPath(args.Single());
if (Directory.Exists(profile)) throw new InvalidOperationException("Generator requires a new profile path.");
Directory.CreateDirectory(profile);
new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
    SaveUrlsOnExit = true, MaxParallelDownloads = 2,
    RecheckTimer = 3600, CompactDensity = true,
    ArchiveThreadLayout = "Table", ArchiveSplitWidth = 431, WindowWidth = 1123,
    LastExportDirectory = Path.Combine(profile, "exports") }.SaveConfig(profile);
using Fixture transport = new();
using HttpClient client = new(transport);
List<object> threads = new();
List<FavoriteEntry> favorites = new();
await using (DesktopSessionHost host = new(profile, new IImageBoard[] { new FourChanImageBoard(client), new EightKunImageBoard(client) }))
{
    await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
    foreach (string url in Fixture.Urls)
        if ((await host.AddAsync(url)).Status != AdmissionStatus.Added) throw new Exception("Old source admission failed: " + url);
    await host.SetPausedAsync(false);
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
    while (host.GetThreadWorkSnapshots().Count != 2 || host.GetThreadWorkSnapshots().Any(thread => thread.Media.Count != 1 || thread.Media.Any(media => media.Status != MeowChan.Persistence.MediaWorkStatus.Succeeded)))
        await Task.Delay(25, deadline.Token);
    await host.SetPausedAsync(true);
    await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
    foreach (string source in new[] { "4chan", "8kun" })
    {
        ResourceId id = new(source, "g", "123");
        ArchivedThreadView view = (await reader.ReadThreadAsync(id))!;
        MediaAssetReference asset = (await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory)).GetPage().Single();
        FavoriteEntry favorite = new(FavoriteAssetKey.From(id, asset), asset.Length, asset.OriginalFileName);
        favorites.Add(favorite);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, id, Path.Combine(profile, "exports", source));
        await host.ExportHistory.RecordPublishedAsync(export);
        threads.Add(new { Id = id, view.Summary.Url, view.Summary.MediaDirectory, Posts = view.GetPosts(), Asset = asset,
            Favorite = favorite, Export = export.Metadata });
    }
}
using (ProfileLease lease = ProfileLease.Acquire(profile))
{
    FavoriteStore store = new(profile, lease);
    foreach (FavoriteEntry favorite in favorites) store.SetFavorite(favorite, true);
}
// Receipt is evidence from old public/internal contracts, never an input state document.
var owned = Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
    .Where(path => path.EndsWith(".png") || path.EndsWith(".html") || path.EndsWith(".meowchan-manifest.json") || path.Contains(Path.DirectorySeparatorChar + "exports" + Path.DirectorySeparatorChar))
    .ToDictionary(path => Path.GetRelativePath(profile, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
File.WriteAllText(Path.Combine(profile, "v09-receipt.json"), JsonSerializer.Serialize(new {
    SourceRevision = "8ec0cf50b270e0551a06d3ad61eff0f448bd70ff", Threads = threads, Files = owned,
    Requests = transport.Requests.ToArray(), LegacyProfileLock = File.Exists(Path.Combine(profile, ".ochan-profile.lock")),
    Protocol = File.ReadAllText(Path.Combine(profile, ".meowchan-profile.protocol")) }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(profile);

sealed class Fixture : HttpMessageHandler
{
    internal static readonly string[] Urls = { "https://boards.4chan.org/g/thread/123", "https://8kun.top/g/res/123.html" };
    internal static readonly byte[] Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nAAAAABJRU5ErkJggg==");
    internal System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string uri = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(uri);
        string? json = uri switch {
            "https://a.4cdn.org/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic\"}]}",
            "https://8kun.top/boards.json" => "[{\"uri\":\"g\",\"title\":\"Synthetic\"}]",
            "https://a.4cdn.org/g/thread/123.json" or "https://8kun.top/g/res/123.json" => JsonSerializer.Serialize(new { posts = new object[] {
                new { no = 123, resto = 0, sub = "Synthetic upgrade", com = "<p>Retained opening</p>", time = 1700000000,
                    tim = 456, ext = ".png", filename = "fixture", fsize = Bytes.Length, md5 = Convert.ToBase64String(MD5.HashData(Bytes)) },
                new { no = 124, resto = 123, com = "<p>Retained reply &gt;&gt;123</p>", time = 1700000001 } } }),
            _ => null
        };
        byte[] content = json != null ? Encoding.UTF8.GetBytes(json) : uri is "https://i.4cdn.org/g/456.png" or "https://nerv.8kun.top/file_store/456.png"
            ? Bytes : throw new InvalidOperationException("Blocked nonfixture network: " + uri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) });
    }
}
