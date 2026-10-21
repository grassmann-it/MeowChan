using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.Favorites;

/// <summary>A caller-frozen asset reference and immutable manifest view. No post association is required.</summary>
public sealed record FavoriteExportAsset(FavoriteAssetKey Key, long Length, string? OriginalFileName,
    MediaAssetSnapshot Manifest, string AssetId)
{
    public static FavoriteExportAsset From(ResourceId thread, MediaAssetSnapshot manifest, MediaAssetReference asset)
        => new(FavoriteAssetKey.From(thread, asset), asset.Length,
            asset.OriginalFileName ?? asset.RequestedFileName, manifest, asset.AssetId);
}

public enum FavoriteExportStatus { Copied, AlreadyExists, Missing, Failed, Cancelled }
public sealed record FavoriteExportItem(FavoriteAssetKey Key, FavoriteExportStatus Status, string? Path, string? Error);
public sealed record FavoriteExportResult(IReadOnlyList<FavoriteExportItem> Items)
{
    public int Copied => Items.Count(item => item.Status == FavoriteExportStatus.Copied);
    public int AlreadyExists => Items.Count(item => item.Status == FavoriteExportStatus.AlreadyExists);
    public int Missing => Items.Count(item => item.Status == FavoriteExportStatus.Missing);
    public int Failed => Items.Count(item => item.Status == FavoriteExportStatus.Failed);
    public int Cancelled => Items.Count(item => item.Status == FavoriteExportStatus.Cancelled);
}

/// <summary>Copies verified originals into stable source/board/thread folders without replacing existing files.</summary>
public static class FavoriteOriginalExporter
{
    public static async Task<FavoriteExportResult> ExportAsync(IReadOnlyList<FavoriteExportAsset> assets,
        string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        FavoriteExportAsset[] frozen = assets.DistinctBy(asset => asset.Key).ToArray();
        foreach (FavoriteExportAsset item in frozen) Validate(item);
        string root = Path.GetFullPath(destinationDirectory);
        if (frozen.Length == 0) return new FavoriteExportResult(Array.Empty<FavoriteExportItem>());
        List<FavoriteExportItem> results = new(frozen.Length);
        foreach (FavoriteExportAsset item in frozen)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Cancelled, null, null));
                continue;
            }
            try
            {
                string? path = await CopyOneAsync(item, root, cancellationToken).ConfigureAwait(false);
                results.Add(new FavoriteExportItem(item.Key,
                    path != null && path.StartsWith("!", StringComparison.Ordinal)
                        ? FavoriteExportStatus.AlreadyExists : FavoriteExportStatus.Copied,
                    path?.TrimStart('!'), null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Cancelled, null, null)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Missing, null, ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Failed, null, ex.Message)); }
        }
        return new FavoriteExportResult(results);
    }

    private static void Validate(FavoriteExportAsset item)
    {
        ArgumentNullException.ThrowIfNull(item);
        FavoriteStore.Validate(item.Key);
        ArgumentNullException.ThrowIfNull(item.Manifest);
        if (item.Length < 0 || string.IsNullOrWhiteSpace(item.AssetId) || item.Key.Variant != "original")
            throw new ArgumentException("The export asset is incomplete.", nameof(item));
        MediaAssetReference asset = item.Manifest.FindById(item.AssetId)
            ?? throw new ArgumentException("The asset is absent from its frozen manifest.", nameof(item));
        if (asset.Length != item.Length || asset.MediaIdentifier != item.Key.MediaIdentifier
            || asset.Variant != item.Key.Variant
            || !string.Equals(asset.Sha256, item.Key.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The export identity does not match its frozen manifest.", nameof(item));
    }

    private static async Task<string> CopyOneAsync(FavoriteExportAsset item, string root, CancellationToken token)
    {
        string directory = EnsureSafeDirectory(root, item.Key.Thread.Source, item.Key.Thread.Board,
            item.Key.Thread.Thread!);
        string name = SafeFileName(item.OriginalFileName);
        string temporary = Path.Combine(directory, ".favorite-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (Stream source = await item.Manifest.OpenVerifiedReadAsync(item.AssetId, token).ConfigureAwait(false))
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            await using (FileStream check = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (check.Length != item.Length ||
                    !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, token).ConfigureAwait(false)),
                        item.Key.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied bytes no longer match the original manifest.");
            }
            // Include the content identity from the start: choosing A before B must not decide
            // which of two equally named originals gets the unsuffixed name.
            string identityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                item.Key.Thread.Source + "\0" + item.Key.Thread.Board + "\0" + item.Key.Thread.Thread
                + "\0" + item.Key.MediaIdentifier + "\0" + item.Key.Variant))).ToLowerInvariant();
            for (int extraLength = 0; extraLength <= 64; extraLength = extraLength == 0 ? 16 : Math.Min(64, extraLength + 8))
            {
                token.ThrowIfCancellationRequested();
                string suffix = item.Key.Sha256.ToLowerInvariant()
                    + (extraLength == 0 ? "" : "-" + identityDigest[..extraLength]);
                string candidate = Path.Combine(directory, WithSuffix(name, suffix));
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    if (await SameBytesAsync(candidate, item.Length, item.Key.Sha256, token).ConfigureAwait(false))
                        return "!" + candidate;
                    if (extraLength == 64) break;
                    continue;
                }
                try { File.Move(temporary, candidate); return candidate; }
                catch (IOException) when (File.Exists(candidate))
                {
                    if (await SameBytesAsync(candidate, item.Length, item.Key.Sha256, token).ConfigureAwait(false))
                        return "!" + candidate;
                }
                if (extraLength == 64) break;
            }
            throw new IOException("No collision-free destination name is available.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> SameBytesAsync(string path, long length, string sha256, CancellationToken token)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget != null || info.Length != length) return false;
        await using FileStream existing = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(existing, token).ConfigureAwait(false)),
            sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureSafeDirectory(string root, params string[] identity)
    {
        string current = Path.GetPathRoot(root)!;
        foreach (string part in Path.GetRelativePath(current, root).Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            EnsureRealDirectory(current);
        }
        foreach (string part in identity)
        {
            current = Path.Combine(current, SafeComponent(part));
            EnsureRealDirectory(current);
        }
        return current;
    }

    private static void EnsureRealDirectory(string path)
    {
        if (File.Exists(path) || (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("An export path is a file or symbolic link.");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An export path is a symbolic link.");
    }

    private static string SafeComponent(string original)
    {
        string clean = Clean(original, 32);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
        return clean + "-" + digest;
    }

    private static string SafeFileName(string? original)
    {
        string raw = original ?? "original";
        string leaf = raw.Replace('\\', '/').Split('/').LastOrDefault() ?? "original";
        string extension = Path.GetExtension(leaf);
        string stem = extension.Length == 0 ? leaf : leaf[..^extension.Length];
        string safeExtension = Clean(extension.TrimStart('.'), 12);
        return Clean(stem, 80) + (extension.Length == 0 || safeExtension == "item" ? "" : "." + safeExtension);
    }

    private static string Clean(string value, int limit)
    {
        string clean = new(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(limit).ToArray());
        clean = clean.Trim('_', '.', ' ');
        if (clean.Length == 0) clean = "item";
        string upper = clean.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" ||
            (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
             && upper[3] is >= '1' and <= '9')) clean = "_" + clean;
        return clean;
    }

    private static string WithSuffix(string name, string suffix)
    {
        string extension = Path.GetExtension(name);
        return (extension.Length == 0 ? name : name[..^extension.Length]) + "-" + suffix + extension;
    }
}
