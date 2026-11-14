using System;
using System.IO;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop.Booru;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Host-only adapters for the local Booru ↔ media cross-links. No provider action is issued here.</summary>
public partial class MainWindow
{
    private void InitializeBooruMedia()
    {
        _mediaPage?.AttachBooruOverflow(() => CanOpenBooru, ShowBooruWorkbench);
    }

    private void ConfigureBooruMediaLinks(BooruWorkbenchWindow window)
    {
        window.ShowInMedia = ShowBooruPostInMediaAsync;
        window.IsPostSavedInMedia = IsSavedBooruPost;
    }

    /// <summary>Exact local catalog admission for a saved Danbooru original; pending/other-source records do not qualify.</summary>
    internal static bool CatalogHasSavedBooruPost(BooruCatalogSnapshot? catalog, string postId)
    {
        ResourceId thread = BooruMediaIdentity.Thread(postId);
        return BooruMediaIdentity.IsBooruThread(thread)
            && catalog?.TryGetPost(BooruMediaIdentity.Source, postId) is { LocalAssetId: { Length: > 0 } };
    }

    private bool IsSavedBooruPost(string postId)
    {
        if (_isClosing || !CatalogHasSavedBooruPost(SafeReadBooruCatalog(), postId)) return false;
        // LocalAssetId is the catalog's durable saved-original link. The media page performs the second,
        // manifest-backed Presence check when the action is invoked, so a stale link is reported honestly.
        return true;
    }

    private BooruCatalogSnapshot? SafeReadBooruCatalog()
    {
        try { return _session.ReadBooruCatalog(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        { return null; }
    }

    private async Task<bool> ShowBooruPostInMediaAsync(string postId)
    {
        if (!CanOpenBooru || !IsSavedBooruPost(postId) || _mediaPage is not { } page) return false;
        if (!NavigateWorkspace(WorkspacePage.Media)) return false;
        // The click starts in the secondary Booru window. Transfer activation before the exact tile focus
        // so the main window, not the still-open workbench, owns keyboard focus after navigation.
        Activate();
        await page.ActivateAsync().ConfigureAwait(true);
        return await page.ShowBooruPostAsync(postId).ConfigureAwait(true);
    }
}
