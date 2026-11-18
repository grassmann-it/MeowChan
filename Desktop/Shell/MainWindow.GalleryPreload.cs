using Avalonia.Media.Imaging;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    // Decodes the neighbors of the shown gallery image in the background (settings: cache memory, images ahead and behind).
    private readonly GalleryPreloader<Bitmap> _galleryPreloader = new(GalleryImageCache.Shared, GalleryImageDecoder.DecodeBackgroundAsync);

    /// <summary>Stops the background decoding around the previous image. Called when the gallery moves on or closes.</summary>
    private void CancelGalleryPreload() => _galleryPreloader.Cancel();

    /// <summary>Starts decoding the neighbors of the image the viewer has just shown; images that are already kept are skipped.</summary>
    private void StartGalleryPreload(GalleryViewer viewer)
    {
        if (!ReferenceEquals(_gallery, viewer) || _isClosing || _gallerySuspended) return;
        int ahead = GalleryImageCache.PreloadAhead;
        int behind = GalleryImageCache.PreloadBehind;
        GalleryPreloadWindow window = GalleryPreloadWindow.Capture(_galleryEntries, _galleryIndex, ahead, behind);
        _ = _galleryPreloader.Schedule(window, viewer.PreloadFitRequest);
    }
}
