using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MOTION_ONLY (D1). The policy and the transition styles on real windows: the class motion-off follows environment,
// setting and OS signal at run time; hover, overlay, toast and image fade exist only while motion is on; and the frame interval
// of a page-sized fade and an overlay reveal on a heavy tree is measured under the private Xvfb (software rendering). The
// harness runs every other smoke with MEOWCHAN_MOTION=off; this one removes the variable inside its own process.
internal static class MotionChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    // Documented limit: the mean frame interval during the fades under Xvfb software rendering. The design target is 16 ms per
    // frame on real hardware; a software rasteriser on a shared build machine gets twice that before a run counts as a regression.
    private const double MeanFrameLimitMs = 40;

    internal static async Task RunAsync(MainWindow owner)
    {
        string? savedEnvironment = Environment.GetEnvironmentVariable("MEOWCHAN_MOTION");
        string? savedReduced = Environment.GetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION");
        string savedSetting = MotionPolicy.Setting;
        bool savedOs = MotionPolicy.OsReduced;
        Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", null);
        MotionPolicy.SetSetting(MotionPolicy.System);
        MotionPolicy.SetOsReduced(false);
        Button button = new() { Content = "Hover probe", Width = 160, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Border overlay = new() { Width = 300, Height = 80, Background = Brushes.DimGray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Classes = { "motion-overlay" }, ZIndex = 10 };
        Image image = new() { Width = 64, Height = 64, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Panel heavy = new();
        Grid page = new() { Children = { heavy } };
        for (int index = 0; index < 1200; index++)
            heavy.Children.Add(new TextBlock { Text = "Row " + index, Margin = new Thickness(index % 40 * 34, index / 40 * 28, 0, 0), FontSize = 13 });
        Grid content = new() { Children = { page, button, image, overlay } };
        Window window = new() { Width = 1440, Height = 960, Content = content, Title = "Motion smoke — diagnostic", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            await UntilAsync(() => window.IsVisible && button.GetVisualDescendants().OfType<ContentPresenter>().Any(), "probe window");
            ContentPresenter Presenter() => button.GetVisualDescendants().OfType<ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");

            // Policy at run time: environment > setting > operating system, applied as a class on the open window.
            Require(!window.Classes.Contains("motion-off") && !MotionPolicy.IsOff, "Motion must start on with no environment, setting System and no OS signal.");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            await UntilAsync(() => window.Classes.Contains("motion-off"), "setting Off did not reach the window");
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "setting On did not clear the class");
            MotionPolicy.SetSetting(MotionPolicy.System);
            MotionPolicy.SetOsReduced(true);
            await UntilAsync(() => window.Classes.Contains("motion-off"), "the OS signal did not reach the window under System");
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "setting On must beat the OS signal");
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", "off");
            MotionPolicy.Refresh();
            await UntilAsync(() => window.Classes.Contains("motion-off"), "MEOWCHAN_MOTION=off must beat setting On");
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", null);
            Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", "1");
            MotionPolicy.Refresh();
            await UntilAsync(() => window.Classes.Contains("motion-off"), "MEOWCHAN_REDUCED_MOTION=1 must still mean off");
            Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", null);
            MotionPolicy.SetOsReduced(false);
            MotionPolicy.SetSetting(MotionPolicy.System);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion did not come back");

            // Hover: brush transitions of 80 ms on the button template while motion is on, none under motion-off.
            Require(Presenter().Transitions is { Count: 3 } hover && hover.OfType<BrushTransition>().All(item => item.Duration == TimeSpan.FromMilliseconds(80)),
                "The hover transitions are missing while motion is on.");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            await UntilAsync(() => Presenter().Transitions == null, "motion-off did not remove the hover transitions");
            MotionPolicy.SetSetting(MotionPolicy.System);
            await UntilAsync(() => Presenter().Transitions != null, "the hover transitions did not come back");

            // Overlay: hidden until .shown, then the 180 ms transition runs to full opacity; under motion-off the class does nothing.
            Require(overlay.Opacity == 0 && overlay.Transitions is { Count: 1 }, "The overlay must wait for .shown while motion is on: " + overlay.Opacity);
            overlay.Classes.Add("shown");
            await UntilAsync(() => Math.Abs(overlay.Opacity - 1) < 0.001, "the overlay never reached full opacity");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            await UntilAsync(() => window.Classes.Contains("motion-off") && overlay.Transitions == null && overlay.Opacity == 1, "motion-off must show the overlay at once");
            overlay.Classes.Remove("shown");
            await UntilAsync(() => overlay.Opacity == 1, "under motion-off the overlay class must have no effect");
            overlay.Classes.Add("shown");

            // Toast: FeedbackToastContent carries the class; without motion it is fully shown on its first frame, with motion it ends fully shown.
            FeedbackToastContent toast = new("Title", "Message", null, _ => { });
            Require(toast.Classes.Contains("toast-content"), "The toast content must carry the toast-content class.");
            content.Children.Add(toast);
            await UntilAsync(() => toast.IsEffectivelyVisible && toast.Bounds.Width > 0, "toast layout");
            Require(toast.Opacity == 1, "Under motion-off the toast must appear at once.");
            content.Children.Remove(toast);
            MotionPolicy.SetSetting(MotionPolicy.System);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on for the toast");
            FeedbackToastContent second = new("Title", "Message", null, _ => { });
            content.Children.Add(second);
            await UntilAsync(() => second.IsEffectivelyVisible && second.Bounds.Width > 0, "second toast layout");
            await UntilAsync(() => Math.Abs(second.Opacity - 1) < 0.001, "the toast never became fully visible");
            content.Children.Remove(second);

            // Image fade: only an asynchronous, visible arrival fades; the helper leaves Opacity at 1 once it has finished.
            Require(!ImageFade.ShouldFade(false, true, true, false) && !ImageFade.ShouldFade(true, false, true, false)
                && !ImageFade.ShouldFade(true, true, false, false) && !ImageFade.ShouldFade(true, true, true, true) && ImageFade.ShouldFade(true, true, true, false),
                "ImageFade.ShouldFade is wrong.");
            using (WriteableBitmap bitmap = new(new PixelSize(8, 8), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque))
            {
                ImageFade.Show(image, bitmap, arrivedAsync: true);
                Require(image.Opacity == 0, "An asynchronous arrival must start the fade at opacity 0: opacity=" + image.Opacity + " class=" + image.Classes.Contains(ImageFade.FadeClass) + " visible=" + image.IsEffectivelyVisible + " off=" + MotionPolicy.IsOff);
                await UntilAsync(() => image.Opacity == 1 && image.Classes.Contains(ImageFade.FadeClass), "the image never faded in");
                image.Source = null; image.Opacity = 1;
                ImageFade.Show(image, bitmap, arrivedAsync: false);
                Require(image.Opacity == 1 && ReferenceEquals(image.Source, bitmap), "A synchronous source (cache hit) must show directly: opacity=" + image.Opacity + " same=" + ReferenceEquals(image.Source, bitmap));
                image.Source = null;
            }

            // Frame interval during a page-sized fade (120 ms) and an overlay reveal (180 ms) on a 1200-label tree, motion on.
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on for the frame measurement");
            overlay.Classes.Remove("shown");
            await IdleAsync();
            page.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }];
            page.Opacity = 0.4;
            await IdleAsync();
            List<double> frames = new();
            TimeSpan? previous = null;
            bool running = true;
            void OnFrame(TimeSpan time)
            {
                if (previous is { } last) frames.Add((time - last).TotalMilliseconds);
                previous = time;
                if (running) window.RequestAnimationFrame(OnFrame);
            }
            window.RequestAnimationFrame(OnFrame);
            page.Opacity = 1;
            overlay.Classes.Add("shown");
            await Task.Delay(400);
            running = false;
            page.Transitions = null;
            Require(frames.Count >= 5, "Too few animation frames were delivered to measure: " + frames.Count);
            double mean = frames.Average(), max = frames.Max();
            double[] sorted = frames.OrderBy(value => value).ToArray();
            double p95 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))];
            Console.WriteLine($"SMOKE motion frames: n={frames.Count} mean={mean:0.0} ms p95={p95:0.0} ms max={max:0.0} ms (software rendering under Xvfb; limit on the mean {MeanFrameLimitMs} ms, design target 16 ms on real hardware)");
            Require(mean <= MeanFrameLimitMs, $"Mean frame interval {mean:0.0} ms exceeds {MeanFrameLimitMs} ms during the fades.");

            Console.WriteLine("SMOKE motion: policy (environment > setting > OS) toggled the class on an open window at run time; hover, overlay, toast and image fade exist only with motion on; frame interval measured.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", savedEnvironment);
            Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", savedReduced);
            MotionPolicy.SetSetting(savedSetting);
            MotionPolicy.SetOsReduced(savedOs);
            window.Close();
            owner.Activate();
        }
    }

    private static Task IdleAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render).GetTask();

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new InvalidOperationException("Motion smoke timed out: " + failure);
            await Task.Delay(15);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
