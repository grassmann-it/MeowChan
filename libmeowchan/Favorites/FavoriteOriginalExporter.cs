using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interop;
using MeowChan.Interop.Sidecars;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

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

internal sealed class OriginalExportSidecarOptions
{
    internal const int MinPairLimit = 1;
    internal const int MaxPairLimit = 1000;
    internal const int DefaultPairLimit = MaxPairLimit;

    internal OriginalExportSidecarOptions(int pairLimit = DefaultPairLimit, PrivateTagStoreSnapshot? privateTags = null,
        BooruCatalogSnapshot? sourceTags = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pairLimit, MinPairLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pairLimit, MaxPairLimit);
        PairLimit = pairLimit;
        PrivateTags = privateTags;
        SourceTags = sourceTags;
    }

    internal int PairLimit { get; }
    private PrivateTagStoreSnapshot? PrivateTags { get; }
    private BooruCatalogSnapshot? SourceTags { get; }

    internal SidecarExportResult Serialize(FavoriteExportAsset item) =>
        PassiveSidecarCodec.SerializeChosenTags(ChosenTags(item).Take(PassiveSidecarCodec.MaxTags + 1).ToArray());

    private IEnumerable<string> ChosenTags(FavoriteExportAsset item)
    {
        MediaAssetReference asset = item.Manifest.FindById(item.AssetId)!;
        if (PrivateTags is { } snapshot)
        {
            foreach (PrivateTagDefinition tag in snapshot.TagsOf(item.Key.Thread).Concat(MediaTags(snapshot, item, asset))
                .DistinctBy(tag => tag.Id))
                yield return tag.Name.Display;
        }
        if (SourceTags != null && BooruMediaIdentity.TryGetPostKey(item.Key.Thread, out var identity)
            && SourceTags.TryGetPost(identity) is { } record && record.LocalAssetId == asset.AssetId
            && record.LocalPath == asset.Path && record.OriginalIdentity.StorageId == asset.MediaIdentifier)
        {
            foreach (BooruPostClaimTag tag in record.Post.Tags)
                yield return tag.Category.ToString().ToLowerInvariant() + ":" + tag.Name;
        }
    }

    private static IEnumerable<PrivateTagDefinition> MediaTags(PrivateTagStoreSnapshot snapshot, FavoriteExportAsset item, MediaAssetReference asset)
    {
        MediaAttachmentBinding[] bindings = item.Manifest.AttachmentBindings.Values
            .Where(binding => binding.AssetId == item.AssetId).ToArray();
        if (bindings.Length == 0)
        {
            if (LocalMediaObservation.TryCreate(item.Key.Thread, item.Manifest.Directory, asset.RequestedFileName,
                asset.AssetId, asset.Length, asset.Sha256, out LocalMediaObservation? observation))
                return snapshot.TagsOf(new MediaTagSubject.Local(observation!, null));
            return Array.Empty<PrivateTagDefinition>();
        }
        return bindings.Where(binding => binding.Reference.Resource == item.Key.Thread
                && binding.Provider.MediaId == asset.MediaIdentifier && binding.Provider.Variant == asset.Variant
                && binding.Actual.Sha256 == asset.Sha256)
            .SelectMany(binding => PrivateMediaTagTarget.TryCreate(binding.Reference, asset.MediaIdentifier, asset.Variant,
                TransferMediaRole.Original, asset.Sha256, out PrivateMediaTagTarget? target, out _)
                ? snapshot.TagsOf(target!) : Array.Empty<PrivateTagDefinition>());
    }
}

/// <summary>Copies verified originals into stable source/board/thread folders without replacing existing files.</summary>
public static class FavoriteOriginalExporter
{
    /// <summary>Test-only seams for copy, pair publication and completed items.</summary>
    internal sealed class Hooks
    {
        internal Func<FavoriteExportAsset, Stream, Stream>? WrapOutput { get; init; }
        internal Action? BeforePairCompletion { get; init; }
        internal Action<FavoriteExportItem>? AfterItem { get; init; }
    }

    private const string SidecarExtension = ".json";
    private const int MaxFileNameBytes = 255;

    public static Task<FavoriteExportResult> ExportAsync(IReadOnlyList<FavoriteExportAsset> assets,
        string destinationDirectory, CancellationToken cancellationToken = default)
        => ExportCoreAsync(assets, destinationDirectory, null, cancellationToken, null, null);

    /// <param name="filenameTemplate">The filename template setting (<see cref="ExportFilenameTemplate"/>); null or empty keeps the current naming, and so does any file the template cannot name safely.</param>
    public static Task<FavoriteExportResult> ExportAsync(IReadOnlyList<FavoriteExportAsset> assets,
        string destinationDirectory, string? filenameTemplate, CancellationToken cancellationToken = default)
        => ExportCoreAsync(assets, destinationDirectory, null, cancellationToken, filenameTemplate, null);

    internal static Task<FavoriteExportResult> ExportAsync(IReadOnlyList<FavoriteExportAsset> assets, string destinationDirectory,
        string? filenameTemplate, OriginalExportSidecarOptions? sidecar, CancellationToken cancellationToken)
        => ExportCoreAsync(assets, destinationDirectory, null, cancellationToken, filenameTemplate, sidecar);

    internal static Task<FavoriteExportResult> ExportWithHooksAsync(IReadOnlyList<FavoriteExportAsset> assets,
        string destinationDirectory, Hooks hooks, CancellationToken cancellationToken = default, string? filenameTemplate = null,
        OriginalExportSidecarOptions? sidecar = null)
        => ExportCoreAsync(assets, destinationDirectory, hooks, cancellationToken, filenameTemplate, sidecar);

    private static async Task<FavoriteExportResult> ExportCoreAsync(IReadOnlyList<FavoriteExportAsset> assets,
        string destinationDirectory, Hooks? hooks, CancellationToken cancellationToken, string? filenameTemplate, OriginalExportSidecarOptions? sidecar)
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
                SidecarExportResult? sidecarData = PrepareSidecar(item, sidecar, results.Count);
                string path = await CopyOneAsync(item, root, hooks, filenameTemplate, sidecarData, cancellationToken).ConfigureAwait(false);
                results.Add(new FavoriteExportItem(item.Key,
                    path.StartsWith("!", StringComparison.Ordinal)
                        ? FavoriteExportStatus.AlreadyExists : FavoriteExportStatus.Copied,
                    path.TrimStart('!'), null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Cancelled, null, null)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Missing, null, ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { results.Add(new FavoriteExportItem(item.Key, FavoriteExportStatus.Failed, null, ex.Message)); }
            hooks?.AfterItem?.Invoke(results[^1]);
        }
        return new FavoriteExportResult(results);
    }

    private static SidecarExportResult? PrepareSidecar(FavoriteExportAsset item, OriginalExportSidecarOptions? sidecar, int index)
    {
        if (sidecar == null) return null;
        if (index >= sidecar.PairLimit) throw new InvalidDataException("The original is beyond the sidecar pair limit.");
        SidecarExportResult data = sidecar.Serialize(item);
        if (data.Error != SidecarError.None)
            throw new InvalidDataException("The selected tags cannot be written as a Hydrus sidecar: " + data.Error);
        return data;
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

    private static async Task<string> CopyOneAsync(FavoriteExportAsset item, string root, Hooks? hooks, string? filenameTemplate,
        SidecarExportResult? sidecar, CancellationToken token)
    {
        string directory = EnsureSafeDirectory(root, item.Key.Thread.Source, item.Key.Thread.Board,
            item.Key.Thread.Thread!);
        string name = TemplatedFileName(item, filenameTemplate) ?? SafeFileName(item.OriginalFileName);
        if (sidecar != null && !FitsSidecar(ExportFilenameTemplate.WithContentSuffix(name, item.Key.Sha256)))
            name = SafeFileName(item.OriginalFileName);
        string temporary = Path.Combine(directory, ".favorite-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (Stream source = await item.Manifest.OpenVerifiedReadAsync(item.AssetId, token).ConfigureAwait(false))
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                Stream output = hooks?.WrapOutput?.Invoke(item, file) ?? file;
                await source.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
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
            string identityDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TemplateIdentity(item))));
            for (int extraLength = 0; extraLength <= 64; extraLength = extraLength == 0 ? 16 : Math.Min(64, extraLength + 8))
            {
                token.ThrowIfCancellationRequested();
                string suffix = item.Key.Sha256.ToLowerInvariant()
                    + (extraLength == 0 ? "" : "-" + identityDigest[..extraLength]);
                string candidate = Path.Combine(directory, ExportFilenameTemplate.WithContentSuffix(name, suffix));
                if (sidecar != null)
                {
                    if (!FitsSidecar(candidate)) break;
                    string? pair = await TryPublishPairAsync(item, temporary, candidate, sidecar, hooks, token).ConfigureAwait(false);
                    if (pair != null) return pair;
                    if (extraLength == 64) break;
                    continue;
                }
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

    private static bool FitsSidecar(string original) =>
        Encoding.UTF8.GetByteCount(Path.GetFileName(original)) <= MaxFileNameBytes - SidecarExtension.Length;

    private static async Task<string?> TryPublishPairAsync(FavoriteExportAsset item, string temporary, string original,
        SidecarExportResult data, Hooks? hooks, CancellationToken token)
    {
        string sidecar = original + SidecarExtension;
        bool originalExists = Path.Exists(original);
        if (originalExists && !await SameBytesAsync(original, item.Length, item.Key.Sha256, token).ConfigureAwait(false))
            return null;
        if (Path.Exists(sidecar))
        {
            if (originalExists && await SameBytesAsync(sidecar, data.Utf8.Length,
                Convert.ToHexStringLower(SHA256.HashData(data.Utf8.AsSpan())), token).ConfigureAwait(false))
                return "!" + original;
            return null;
        }

        string staged = temporary + SidecarExtension;
        bool published = false;
        bool complete = false;
        try
        {
            await using (FileStream file = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.WriteAsync(data.Utf8.AsMemory(), token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staged, sidecar);
            published = true;
            hooks?.BeforePairCompletion?.Invoke();
            token.ThrowIfCancellationRequested();
            if (!originalExists) File.Move(temporary, original);
            complete = true;
            return originalExists ? "!" + original : original;
        }
        catch (IOException) when (published ? !originalExists && Path.Exists(original) : Path.Exists(sidecar))
        {
            return null;
        }
        finally
        {
            if (published && !complete) File.Delete(sidecar);
            if (File.Exists(staged)) File.Delete(staged);
        }
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

    /// <summary>
    /// The name the filename template gives this file, or null to keep <see cref="SafeFileName"/>: no template, no usable extension, or a
    /// name the template cannot produce safely. The identity is the same string that decides collisions below, so a name never depends on the batch.
    /// </summary>
    private static string? TemplatedFileName(FavoriteExportAsset item, string? template)
    {
        if (!ExportFilenameTemplate.IsActive(template)) return null;
        string extension = Path.GetExtension((item.OriginalFileName ?? string.Empty).Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty).TrimStart('.');
        string safeExtension = Clean(extension, 12);
        if (extension.Length == 0 || safeExtension == "item" || safeExtension.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return null;
        return ExportFilenameTemplate.TryRender(template, TemplateIdentity(item), Clean(item.Key.Thread.Source, 40),
            Clean(item.Key.Thread.Thread!, 40), "original", item.Key.Sha256, safeExtension, FilenameMd5(item));
    }

    private static string TemplateIdentity(FavoriteExportAsset item) => ExportFilenameTemplate.ItemIdentity(
        item.Key.Thread.Source, item.Key.Thread.Board, item.Key.Thread.Thread!, item.Key.MediaIdentifier, item.Key.Variant);

    private static string? FilenameMd5(FavoriteExportAsset item)
    {
        string? value = item.Manifest.FindById(item.AssetId)?.Md5;
        Span<byte> bytes = stackalloc byte[16];
        return value is not null && Convert.TryFromBase64String(value, bytes, out int written) && written == bytes.Length
            ? Convert.ToHexStringLower(bytes) : null;
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
}
