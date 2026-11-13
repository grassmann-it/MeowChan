using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MeowChan.Desktop.Motion;

/// <summary>
/// D1: shows an image that arrived asynchronously with a 150 ms fade (class <c>motion-fade-in</c>). Only for one-off loads such as the
/// archive preview: never for tiles, cache hits or recycled containers, where a fade would run on every scroll. Under motion-off,
/// for a hidden image or for a synchronous source the image is set directly.
/// </summary>
internal static class ImageFade
{
    internal const string FadeClass = "motion-fade-in";

    /// <summary>True when a fade should run: the image arrived asynchronously, is shown, and motion is on.</summary>
    internal static bool ShouldFade(bool arrivedAsync, bool hasSource, bool visible, bool motionOff) => arrivedAsync && hasSource && visible && !motionOff;

    internal static void Show(Image image, IImage? source, bool arrivedAsync)
    {
        if (!ShouldFade(arrivedAsync, source != null, image.IsEffectivelyVisible, MotionPolicy.IsOff))
        {
            image.Opacity = 1;
            image.Source = source;
            return;
        }
        // The start value must be set without a transition (else Opacity 1 -> 0 would animate), so the class is off while it is set
        // and goes on, one rendered frame later, together with the final value.
        image.Classes.Remove(FadeClass);
        image.Opacity = 0;
        image.Source = source;
        Dispatcher.UIThread.Post(() =>
        {
            image.Classes.Add(FadeClass);
            image.Opacity = 1;
        }, DispatcherPriority.Render);
    }
}
