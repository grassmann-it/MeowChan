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
        if (_mediaPage is { } page) AttachBooruOverflow(page);
    }

    // Called at MediaPage's actual creation point and kept idempotent for the late Booru
    // composition hook. No MediaPage implementation or toolbar layout is changed here.
    private void AttachBooruOverflow(MediaPage page) =>
        page.AttachBooruOverflow(() => CanOpenBooru, ShowBooruWorkbench);

    private void ConfigureBooruMediaLinks(BooruPage page) => ConfigureBooruMediaLinks(page.View);

    private void ConfigureBooruMediaLinks(BooruWorkbenchView view)
    {
        view.ShowInMedia = ShowBooruPostInMediaAsync;
        view.IsPostSavedInMedia = IsSavedBooruPost;
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
        if (!CanOpenBooru || !IsSavedBooruPost(postId) || _mediaPage is not { } page
            || _booruPage is not { } sourcePage) return false;
        BooruWorkbenchServices services = sourcePage.Services;
        if (!ReferenceEquals(_session.BooruServices, services) || !NavigateWorkspace(WorkspacePage.Media)) return false;
        Func<bool> navigationCurrent = CaptureMediaPageMenuGuard();
        bool Current() => navigationCurrent() && CanOpenBooru
            && ReferenceEquals(_booruPage, sourcePage) && ReferenceEquals(_session.BooruServices, services);
        // This is a local page-to-page handoff. If navigation or source composition changes
        // while Media activates, the old click must not reset the user's new filters or focus.
        Activate();
        await page.ActivateAsync().ConfigureAwait(true);
        if (!Current()) return false;
        bool shown = await page.ShowBooruPostAsync(postId, Current).ConfigureAwait(true);
        return Current() && shown;
    }
}
