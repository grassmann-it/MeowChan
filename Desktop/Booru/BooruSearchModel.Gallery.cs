namespace MeowChan.Desktop.Booru;

// The inline gallery asks the same preview port for the sample of the open post, if that port can deliver samples (the production one does).
internal sealed partial class BooruSearchModel
{
    /// <summary>The sample source of the gallery; null when the preview port has none (the gallery then shows the preview only).</summary>
    internal IBooruSamplePort? Samples => BindingFor(View)?.Previews as IBooruSamplePort;
}
