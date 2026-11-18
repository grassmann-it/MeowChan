namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private GalleryFitPreferences _fitPreferences;

    /// <summary>Sets whether Fit may enlarge small images without limit; the default keeps today's behaviour.</summary>
    internal void ConfigureFit(GalleryFitPreferences preferences)
    {
        preferences = preferences with { MaxUpscalePercent = GalleryFitPreferences.ClampPercent(preferences.MaxUpscalePercent) };
        if (_fitPreferences == preferences) return;
        _fitPreferences = preferences;
        if (_info != null) FitIfAutomatic();
    }

    private double? FitUpscaleCap => _fitPreferences.MaxUpscale(DisplayScale);
}
