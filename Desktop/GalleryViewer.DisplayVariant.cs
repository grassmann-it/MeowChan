using Avalonia.Media.Imaging;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    /// <summary>Takes ownership of a bounded still-image variant after the source has decoded it.</summary>
    internal bool TryPresentDisplayVariant(GalleryEntry? preview, GalleryEntry entry, Bitmap bitmap)
    {
        if (_viewerInteractionStopped || _sessionCancellation.IsCancellationRequested
            || preview is not null && !ReferenceEquals(_entry, preview)) return false;

        CancelLoads();
        if (preview is null)
        {
            ResetViewForMedium(entry);
            _manualZoom = _actualZoom = false;
            _panX = _panY = 0;
        }
        Bitmap? previousFit = _fitBitmap;
        Bitmap? previousDetail = _highBitmap;
        GalleryAssetKey? previousKey = _entry?.Key;
        _entry = entry;
        _fitBitmap = null;
        _highBitmap = bitmap;
        _info = new(bitmap.PixelSize.Width, bitmap.PixelSize.Height, "Sample", false);
        _image.Source = bitmap;
        _image.IsVisible = true;
        DisposeBitmap(previousFit, previousKey, _navigationId, AssociationRevision);
        DisposeBitmap(previousDetail, previousKey, _navigationId, AssociationRevision);
        _mediaFailed = false;
        _status.Text = string.Empty;
        SetImageCommands(true);
        FitIfAutomatic();
        MarkVisibleImageReady();
        return true;
    }
}
