using Avalonia.Input;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>Geometry and decoder observations only; never fabricates input or playback state.</summary>
internal static class MediaInteractionSmokeHelpers
{
    internal static bool EdgeHintVisible(Button target) => target.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
        .Any(path => path.IsEffectivelyVisible && path.Opacity > .01);

    internal static void RequireBorderlessEdgeTarget(Button target)
    {
        target.ApplyTemplate();
        if (target.BorderThickness != new Thickness(0)
            || target.Background is not ISolidColorBrush background || background.Color.A != 0
            || target.GetVisualDescendants().OfType<Border>().Any(border => border.BorderThickness != new Thickness(0)
                || border.Background is ISolidColorBrush fill && fill.Color.A != 0))
            throw new InvalidOperationException("Edge hint has a persistent box/fill/border: " + target.Name);
    }

    internal static Rect ScreenBoundsIn(Control target, Control owner)
    {
        if (!target.IsAttachedToVisualTree() || !owner.IsAttachedToVisualTree())
            throw new InvalidOperationException("Edge geometry requires two attached visual trees.");
        // Ownership is not visual ancestry. Native video chrome lives in a separate
        // window; TranslatePoint returns null across those roots. Convert both
        // corners through screen pixels so root scaling is respected as well.
        Point first = owner.PointToClient(target.PointToScreen(default));
        Point last = owner.PointToClient(target.PointToScreen(new Point(target.Bounds.Width, target.Bounds.Height)));
        return new Rect(first, last);
    }

    internal static void RequireEdgeTargetWithinViewer(Control target, Control viewer)
    {
        Rect mapped = ScreenBoundsIn(target, viewer);
        bool withinViewer = mapped.Left >= 0 && mapped.Top >= 0
            && mapped.Right <= viewer.Bounds.Width + 1 && mapped.Bottom <= viewer.Bounds.Height + 1;
        Control? host = target.Parent as Control;
        Rect? local = host == null ? null : ScreenBoundsIn(target, host);
        bool withinHost = local is { } bounds && bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= host!.Bounds.Width + 1 && bounds.Bottom <= host.Bounds.Height + 1;
        Console.WriteLine("SMOKE edge bounds: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            target.Name, mapped = mapped.ToString(), viewer = viewer.Bounds.ToString(),
            host = host?.GetType().Name, hostBounds = host?.Bounds.ToString(), local = local?.ToString(),
            separateRoots = !ReferenceEquals(TopLevel.GetTopLevel(target), TopLevel.GetTopLevel(viewer)),
            withinViewer, withinHost
        }));
        if (!withinViewer || !withinHost)
            throw new InvalidOperationException("Edge indicator is clipped outside the usable gallery/host: " + target.Name);
    }

    internal static bool IsPointerTargetReady(Control target)
    {
        try
        {
            if (!IsNativeGeometryReady(target) || TopLevel.GetTopLevel(target) is not { } root) return false;
            PixelPoint pixel = target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
            return root.InputHitTest(root.PointToClient(pixel)) is Visual hit
                && hit.GetSelfAndVisualAncestors().Contains(target);
        }
        catch (ArgumentException)
        {
            // A popup/control transfer can invalidate the target between the geometry
            // probe and the cross-root hit test. Let the caller reacquire the current
            // instance; never turn that transient native-root race into a smoke failure.
            return false;
        }
    }

    /// <summary>Checks the current native popup-root geometry without trusting a stale cross-root hit-test cache.</summary>
    internal static bool IsNativeGeometryReady(Control target)
    {
        if (!target.IsAttachedToVisualTree() || !target.IsArrangeValid || !target.IsEffectivelyVisible
            || !target.IsEffectivelyEnabled || target.Bounds.Width <= 0 || target.Bounds.Height <= 0
            || TopLevel.GetTopLevel(target) is not { IsVisible: true } root) return false;
        try
        {
            PixelPoint pixel = target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
            Point local = root.PointToClient(pixel);
            return new Rect(root.ClientSize).Contains(local);
        }
        catch (ArgumentException)
        {
            // Avalonia can retain a popup visual for one layout turn while its native
            // root is being replaced. It is not a native target until the conversion
            // succeeds; never synthesize a routed event for that interval.
            return false;
        }
    }

    /// <summary>Reveal an auto-hidden gallery transport, then wait for its current native hit geometry.</summary>
    internal static async Task WaitForNativeTargetAsync(MediaNativeInput native, GalleryViewer viewer, Control target, string message)
    {
        if (!IsPointerTargetReady(target))
            await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .4, viewer.Bounds.Height - 2));
        await Program.WaitUntilAsync(() => IsPointerTargetReady(target), () => message);
    }

    internal static Point TrackPoint(Slider slider, double fraction)
    {
        if (fraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(fraction));
        slider.ApplyTemplate();
        Track[] tracks = slider.GetVisualDescendants().OfType<Track>().ToArray();
        if (tracks.Length != 1)
            throw new InvalidOperationException($"Expected exactly one native slider track; found {tracks.Length} for {slider.Name}.");
        Track track = tracks[0];
        double thumb = track.Thumb?.Bounds.Width ?? 12;
        return track.TranslatePoint(new Point(thumb / 2 + fraction * (track.Bounds.Width - thumb), track.Bounds.Height / 2), slider)
            ?? throw new InvalidOperationException("Native slider track has no transform.");
    }

    internal static async Task<Point> TrackPointAsync(Slider slider, double fraction)
    {
        const int maxAttempts = 24;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // TrackPoint and its slider must remain one attached presentation. A
                // transfer can detach the retained PART_Track between TranslatePoint
                // and native input; let the caller's current-target transaction resolve
                // a new slider instead of pairing this point with another instance.
                if (!slider.IsAttachedToVisualTree() || !slider.IsArrangeValid)
                    throw new ArgumentException("Slider presentation changed during native coordinate conversion.", nameof(slider));
                return TrackPoint(slider, fraction);
            }
            catch (ArgumentException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(25);
            }
        }
    }

    internal static bool HasDecodedFrames(GalleryVideoPlayer.PlaybackEvidence evidence) =>
        OperatingSystem.IsWindows() ? evidence.DisplayedFrames >= 3 : evidence.SurfaceFrames >= 3 && evidence.SurfaceNonUniform;

    internal static async Task HoldStateAsync(GalleryVideoPlayer player, string expected, string interaction,
        int milliseconds = 650)
    {
        GalleryVideoPlayer.PlaybackEvidence first = await player.InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(4));
        GalleryVideoPlayer.PlaybackEvidence last = first;
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            last = await player.InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(4));
            if (!last.HasPlayback || last.State != expected)
                throw new InvalidOperationException($"{interaction} changed native {expected} intent to {last.State}.");
            await Task.Delay(65);
        } while (clock.ElapsedMilliseconds < milliseconds);
        if (expected == "Playing" && last.Time <= first.Time + 150)
            throw new InvalidOperationException(interaction + " left a Playing label but froze the actual decoder clock.");
        if (expected == "Paused" && Math.Abs(last.Time - first.Time) > 120)
            throw new InvalidOperationException(interaction + " advanced the paused decoder clock.");
    }
}
