namespace MeowChan.Desktop;

/// <summary>Small host seam for the profile-owned seek-hover cache; transport layout stays in the existing viewer.</summary>
internal sealed partial class GalleryViewer
{
    private VideoScrubPreviewCache? _scrubPreviewCache;

    /// <summary>
    /// Connects or disconnects the viewer's video scrub previews. The parent owns and configures the cache;
    /// <c>null</c> is a deliberate disabled state.
    /// </summary>
    internal void ConfigureScrubPreview(VideoScrubPreviewCache? cache)
    {
        _scrubPreviewCache = cache;
        _video?.ConfigureScrubPreview(cache);
    }
}
