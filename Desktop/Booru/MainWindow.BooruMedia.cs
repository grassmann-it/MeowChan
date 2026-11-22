using System;
using System.IO;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Host-only adapters for the local Booru ↔ media cross-links. No provider action is issued here.</summary>
public partial class MainWindow
{
    private void InitializeBooruMedia()
    {
        if (_mediaPage is { } page) AttachBooruOverflow(page);
    }

    // Called at MediaPage's actual creation point and kept idempotent for the late Booru
    // composition hook. No MediaPage implementation or toolbar layout is changed here.
    private void AttachBooruOverflow(MediaPage page) =>
        page.AttachBooruOverflow(() => CanOpenBooru, ShowBooruWorkbench);

    private void ConfigureBooruMediaLinks(BooruPage page) => ConfigureBooruMediaLinks(page.View);

    private void ConfigureBooruMediaLinks(BooruWorkbenchView view)
    {
        view.ShowInMedia = postId => ShowBooruPostInMediaAsync(view, postId);
        view.IsPostSavedInMedia = IsSavedBooruPost;
        view.ResultTagsShown = () => _session.Config.BooruResultTagsShown;
        view.SearchTabsLimit = () => _session.Config.BooruSearchTabs;
        view.GallerySampleMiB = () => _session.Config.BooruGallerySampleMiB;
        view.GalleryCacheMiB = () => _session.Config.BooruGalleryCacheMiB;
    }

    // E1: the result view (list or tiles) and the tile size are view preferences of the profile, restored when the page is built and saved when
    // the person changes them (the same path as the tagging page's view).
    private void ConfigureBooruViewPreferences(BooruPage page)
    {
        BooruWorkbenchView view = page.View;
        view.SaveResultView = mode => OpenActions.Start(() => _session.SaveViewPreferencesAsync(config => config.BooruPageView = mode.ToString()), "booru.saveResultView");
        view.SaveTileSize = size => OpenActions.Start(() => _session.SaveViewPreferencesAsync(config => config.BooruTileSize = size.ToString()), "booru.saveTileSize");
        view.RestoreResultView(BooruResultViews.Parse(_session.Config.BooruPageView), MediaTileSizes.Parse(_session.Config.BooruTileSize));
    }

    /// <summary>Exact local catalog admission for a saved Danbooru original; pending/other-source records do not qualify.</summary>
    internal static bool CatalogHasSavedBooruPost(BooruCatalogSnapshot? catalog, string postId)
    {
        ResourceId thread = BooruMediaIdentity.Thread(postId);
        return BooruMediaIdentity.IsBooruThread(thread)
            && catalog?.TryGetPost(BooruMediaIdentity.Source, postId) is { LocalAssetId: { Length: > 0 } };
    }

    // The passive button-visibility probe may fail closed, but an invoked navigation must
    // distinguish a readable catalog with no saved post from an unavailable/failed catalog.
    internal static bool CatalogHasSavedBooruPostForNavigation(Func<BooruCatalogSnapshot?> readCatalog, string postId) =>
        CatalogHasSavedBooruPost(readCatalog() ?? throw new InvalidOperationException("The local Booru catalog is unavailable."), postId);

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

    private async Task<bool> ShowBooruPostInMediaAsync(BooruWorkbenchView origin, string postId)
    {
        Action cancelNotice = origin.CaptureLinkActionCancellation();
        bool Cancelled() { cancelNotice(); return false; }
        if (!CanOpenBooru || _mediaPage is not { } page || _booruPage is not { } sourcePage
            || !ReferenceEquals(sourcePage.View, origin)) return Cancelled();
        // Only an admitted, current click may report that its saved original is missing.
        if (!CatalogHasSavedBooruPostForNavigation(_session.ReadBooruCatalog, postId)) return false;
        BooruWorkbenchServices services = sourcePage.Services;
        if (!ReferenceEquals(_session.BooruServices, services) || !NavigateWorkspace(WorkspacePage.Media)) return Cancelled();
        Func<bool> navigationCurrent = CaptureMediaPageMenuGuard();
        Func<bool> interactionCurrent = page.CaptureBooruHandoffInteraction();
        bool Current() => navigationCurrent() && CanOpenBooru
            && ReferenceEquals(_booruPage, sourcePage) && ReferenceEquals(_session.BooruServices, services);
        // This is a local page-to-page handoff. If navigation or source composition changes
        // while Media activates, the old click must not reset the user's new filters or focus.
        try
        {
            Activate();
            await page.ActivateAsync().ConfigureAwait(true);
            // User edits win even when the old workbench remains alive behind the Media page.
            if (!Current() || !interactionCurrent()) return Cancelled();
            bool shown = await page.ShowBooruPostAsync(postId, Current, cancelNotice).ConfigureAwait(true);
            if (!Current() || !interactionCurrent()) return Cancelled();
            return shown;
        }
        catch (Exception error) when (error is not OutOfMemoryException && (!Current() || !interactionCurrent()))
        {
            // A retired navigation has no authority to publish either a missing-file or an error
            // notice. A current failure still propagates to the view's honest navigationFailed path.
            return Cancelled();
        }
    }
}
