using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

public sealed partial class MediaPage
{
    private async Task<string?> ReadMediaMenuMd5Async(MediaLibraryEntry entry)
    {
        MediaAssetReference? asset = await MediaSourceMenu.ReadAssetAsync(entry, OpenManifestAsync, _lifetime.Token);
        if (MediaSourceMenu.Md5(asset) is { } digest) return digest;
        return entry.Presence == MediaLibraryPresence.Missing
            ? MediaSourceMenu.Md5(null, await ReadMediaAttachmentAsync(entry, _lifetime.Token)) : null;
    }

    private static async Task<string?> ReadBatchMd5Async(MediaLibraryEntry entry, CancellationToken token)
    {
        if (entry is not { Directory: { } directory, AssetId: not null }) return null;
        try
        {
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
            if (MediaTileThumbnails.Matching(manifest, entry) is not { } asset) return null;
            await using Stream stream = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false);
            if (MediaSourceMenu.Md5(asset) is { } md5) return md5;
            // MD5 is the provider's exact-file search key. The manifest read above verifies the file with SHA-256.
            return Convert.ToBase64String(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void AddMd5BatchItems(List<Control> items, MediaMenuSession session, IReadOnlyList<MediaLibraryEntry> selection,
        Func<Func<Task>, Task> run)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow { CanOfferMd5Batch: true } window) return;
        void Add(string key, Func<IEnumerable<MediaLibraryEntry>> entries)
        {
            items.Add(MediaMenuBuilder.ContextItem(session, key, () => window.OpenMd5BatchAsync(entries()
                .Where(entry => entry.Presence == MediaLibraryPresence.Present && entry.Variant == "original")
                .DistinctBy(entry => (entry.Directory, entry.AssetId)).ToArray(), ReadBatchMd5Async, _lifetime.Token),
                () => window.CanOfferMd5Batch, run, icon: "search"));
        }

        if (selection.Any(entry => entry.Presence == MediaLibraryPresence.Present)) Add("booru.md5.batch.selected", () => selection);
        if (_snapshot?.Entries.Any(entry => entry.Presence == MediaLibraryPresence.Present) == true)
            Add("booru.md5.batch.all", () => _snapshot?.Entries ?? []);
    }
}
