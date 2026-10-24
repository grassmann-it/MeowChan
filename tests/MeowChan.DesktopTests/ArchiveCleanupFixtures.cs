using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

internal static class ArchiveCleanupFixtures
{
    internal sealed record Asset(string Media, string Variant, string Requested, string File, byte[] Bytes);
    internal static ResourceId Id(string thread) => new("source", "board", thread);
    internal static string DirectoryFor(string root, ResourceId id) => ArchiveCleanupMediaInventory.ExpectedThreadDirectory(root, id);

    internal static async Task AssetsAsync(string directory, params Asset[] assets)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> current = new(), history = new();
        foreach (Asset asset in assets)
        {
            await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, asset.File), asset.Bytes);
            string sha = Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant();
            object entry = new { MediaIdentifier = asset.Media, asset.Variant, SourceUrl = "https://offline.invalid/" + asset.File,
                FileName = asset.File, Length = asset.Bytes.Length, Sha256 = sha, Md5 = Convert.ToBase64String(MD5.HashData(asset.Bytes)) };
            string[] identity = asset.Variant == "original" ? [asset.Requested, asset.Media, asset.File, sha]
                : [asset.Requested, asset.Media, asset.File, sha, asset.Variant];
            string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity)))).ToLowerInvariant();
            current[asset.Requested] = entry;
            history[assetId] = new { RequestedFileName = asset.Requested, Entry = entry };
        }
        await System.IO.File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 2, Entries = current, Assets = history }));
    }

    internal static async Task RegisterAsync(string profile, string root, params ResourceId[] ids)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in ids)
            await catalog.RegisterThreadAsync(new(id, "https://offline.invalid/" + Uri.EscapeDataString(id.Thread!),
                "Thread " + id.Thread, DirectoryFor(root, id), false));
    }

    internal static void Config(string profile, string root) => new global::MeowChan.Config
    { DownloadsPaused = true, DownloadPath = root, SaveUrlsOnExit = false }.SaveConfig(profile);
}
