using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;

namespace MeowChan.FolderSources;

/// <summary>
/// The virtual manifest of one folder source item: one asset, the user's file itself, once its SHA-256 is known. Opening the
/// manifest of an unhashed item hashes it first (at <see cref="FolderSourceHashPriority.View"/>, within the size limit), so a
/// thumbnail, the gallery or a menu action reads the file through the same verified path as any saved medium and a write
/// can only ever name hashed bytes. A missing, changed, too large or unreadable item has an empty manifest ("missing").
/// </summary>
internal sealed class FolderSourceManifests
{
    private readonly FolderSourceCatalog _catalog;
    private readonly FolderSourceHasher? _hasher;

    internal FolderSourceManifests(FolderSourceCatalog catalog, FolderSourceHasher? hasher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _hasher = hasher;
    }

    /// <summary>Registers this provider for the profile's virtual item folders; dispose to unregister.</summary>
    internal IDisposable Register() => VirtualMediaManifests.Register(_catalog.VirtualRoot, OpenAsync);

    internal async Task<MediaAssetSnapshot> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!FolderSourceIdentity.TryParseVirtualDirectory(_catalog.VirtualRoot, directory, out Guid root, out Guid itemId)) return Missing(directory);
        (FolderSourceItem Item, FolderSourceRoot Root)? found;
        try { found = await Task.Run(() => _catalog.Find(itemId), cancellationToken).ConfigureAwait(false); }
        catch (FolderSourceException) { throw new InvalidDataException("The folder source catalog cannot be read."); }
        if (found is not { } pair || pair.Root.Id != root || pair.Item.State == FolderSourceItemState.Missing) return Missing(directory);
        FolderSourceItem item = pair.Item;
        if (item.State != FolderSourceItemState.Hashed)
        {
            if (_hasher == null) return Missing(directory);
            FolderSourceHashResult hashed = await _hasher.HashAsync(itemId, FolderSourceHashPriority.View, cancellationToken).ConfigureAwait(false);
            if (hashed is not { Outcome: FolderSourceHashOutcome.Hashed or FolderSourceHashOutcome.AlreadyHashed, Item: { Sha256: not null } current })
                return Missing(directory);
            item = current;
        }
        return Of(directory, pair.Root, item);
    }

    /// <summary>The manifest view of a hashed item.</summary>
    internal static MediaAssetSnapshot Of(string directory, FolderSourceRoot root, FolderSourceItem item)
    {
        if (item.Sha256 == null) return Missing(directory);
        MediaAssetReference asset = new(item.AssetId, item.MediaIdentifier, FolderSourceIdentity.Variant, item.RequestedFileName,
            FolderSourceIdentity.AbsolutePath(root.Path, item.RelativePath), item.Length, item.Sha256, string.Empty, item.Name, IsCurrent: true);
        // The generation names exactly what the view says: this item with these bytes.
        return new MediaAssetSnapshot(directory, MediaManifestStatus.Available, null, "folder:" + FolderSourceIdentity.Format(item.Id) + ":" + item.Sha256,
            new[] { asset });
    }

    private static MediaAssetSnapshot Missing(string directory) =>
        new(directory, MediaManifestStatus.Missing, null, null, Array.Empty<MediaAssetReference>());
}
