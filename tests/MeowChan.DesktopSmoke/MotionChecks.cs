using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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

            // Popups: a menu or flyout lives in its own root, which must follow the policy too (open while on, switched, opened while off).
            Button inPopup = new() { Content = "In popup", Width = 120, Height = 32 };
            Flyout flyout = new() { Content = inPopup, ShowMode = FlyoutShowMode.Standard };
            ContentPresenter PopupPresenter() => inPopup.GetVisualDescendants().OfType<ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on for the popup");
            flyout.ShowAt(button);
            await UntilAsync(() => inPopup.IsEffectivelyVisible && inPopup.Bounds.Width > 0 && inPopup.GetVisualDescendants().OfType<ContentPresenter>().Any(), "flyout content");
            TopLevel popupRoot = TopLevel.GetTopLevel(inPopup)!;
            Console.WriteLine("SMOKE motion popup host: " + popupRoot.GetType().Name);
            Require(!popupRoot.Classes.Contains("motion-off") && PopupPresenter().Transitions is { Count: 3 }, "A popup opened while motion is on must animate.");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            await UntilAsync(() => popupRoot.Classes.Contains("motion-off") && PopupPresenter().Transitions == null, "switching motion off must reach an open popup");
            flyout.Hide();
            await UntilAsync(() => !flyout.IsOpen, "flyout hide"); await Task.Delay(150);
            flyout.ShowAt(button);
            await UntilAsync(() => inPopup.IsEffectivelyVisible && inPopup.GetVisualDescendants().OfType<ContentPresenter>().Any(), "flyout content again");
            Require(TopLevel.GetTopLevel(inPopup)!.Classes.Contains("motion-off") && PopupPresenter().Transitions == null, "A popup opened while motion is off must not animate.");
            flyout.Hide();
            await UntilAsync(() => !flyout.IsOpen, "flyout hide again"); await Task.Delay(150);
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on after the popups");

            // Buttons in virtualised containers never transition (E6); the same button outside a list does.
            Button inRow = new() { Content = "In row", Width = 100, Height = 28 };
            ListBox rows = new() { Items = { new ListBoxItem { Content = inRow } }, Width = 200, Height = 60, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            content.Children.Add(rows);
            await UntilAsync(() => inRow.GetVisualDescendants().OfType<ContentPresenter>().Any(), "row button template");
            ContentPresenter RowPresenter() => inRow.GetVisualDescendants().OfType<ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
            Require(RowPresenter().Transitions is not { Count: > 0 } && Presenter().Transitions is { Count: 3 }, "A button in a list row must not transition; one outside must: row=" + (RowPresenter().Transitions?.Count.ToString() ?? "null") + " outside=" + (Presenter().Transitions?.Count.ToString() ?? "null") + " logical=" + string.Join(">", Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(inRow).Select(node => node.GetType().Name).Take(5)));
            content.Children.Remove(rows);

            // A plain Popup (the reader's quote preview is one) gets the class as well.
            Popup popup = new() { PlacementTarget = button, Child = new Button { Content = "In Popup" }, IsLightDismissEnabled = false };
            content.Children.Add(popup);
            MotionPolicy.SetSetting(MotionPolicy.Off);
            popup.IsOpen = true;
            await UntilAsync(() => popup.Child!.IsEffectivelyVisible && popup.Child is Visual, "popup content");
            Require(TopLevel.GetTopLevel(popup.Child as Visual)!.Classes.Contains("motion-off"), "A Popup root must carry motion-off while motion is off.");
            popup.IsOpen = false;
            content.Children.Remove(popup);
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on after the popup");

            // Handlers do not pile up: hiding and showing a window again attaches it to the policy once (WindowOpened fires per Show).
            int opened = 0;
            Window cycle = new() { Width = 200, Height = 120, Title = "Motion cycle", ShowInTaskbar = false };
            cycle.Opened += (_, _) => opened++;
            int subscribersBefore = MotionPolicy.Subscribers;
            cycle.Show(window);
            await UntilAsync(() => opened >= 1, "cycle window opened");
            int afterFirst = MotionPolicy.Subscribers;
            for (int index = 0; index < 4; index++)
            {
                cycle.Hide();
                await IdleAsync();
                cycle.Show(window);
                await Task.Delay(150); // Whether Avalonia raises Opened again is reported below; the subscription count is what matters.
            }
            Console.WriteLine($"SMOKE motion handlers: Opened fired {opened} times for 5 shows; subscribers before={subscribersBefore} after first={afterFirst} after cycle={MotionPolicy.Subscribers}");
            Require(afterFirst == subscribersBefore + 1 && MotionPolicy.Subscribers == afterFirst, "Showing a window again must not add another policy subscription.");
            cycle.Close();

            // Theme switch: the brushes change at once, with no 80 ms cross-fade, and motion is back afterwards.
            Application app = Application.Current!;
            ThemeVariant originalTheme = app.RequestedThemeVariant ?? ThemeVariant.Default;
            ThemeVariant otherTheme = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
            Color Ink() => (Presenter().Background as ISolidColorBrush)?.Color ?? default;
            Color before = Ink(), immediately = default;
            MotionPolicy.WhileThemeChanges(() =>
            {
                app.RequestedThemeVariant = otherTheme;
                immediately = Ink();
                Require(window.Classes.Contains("motion-off"), "Motion must be off while the theme changes.");
            });
            await Task.Delay(40);
            Color midway = Ink();
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion must come back after the theme change");
            await Task.Delay(200);
            Color settled = Ink();
            Console.WriteLine($"SMOKE motion theme ink: before={before} immediately={immediately} midway={midway} settled={settled}");
            Require(before != settled, "The probe button must change its ink with the theme.");
            Require(immediately == settled && midway == settled, "The theme switch must not cross-fade the button ink.");
            // Control: the same switch without the wrapper does cross-fade (informational; shows the check can fail).
            Color control = settled;
            app.RequestedThemeVariant = originalTheme == ThemeVariant.Default ? (otherTheme == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark) : originalTheme;
            await Task.Delay(30);
            control = Ink();
            Console.WriteLine($"SMOKE motion theme control (no wrapper): ink after 30 ms={control} (settled before={settled})");
            await Task.Delay(250);
            MotionPolicy.WhileThemeChanges(() => app.RequestedThemeVariant = originalTheme);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion back after restoring the theme");

            // OverlayReveal (filter surfaces): the class follows visibility; entering fades in, leaving is immediate, motion-off is instant.
            Border surface = new() { Width = 220, Height = 40, Background = Brushes.DimGray, IsVisible = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, ZIndex = 10 };
            OverlayReveal.Follow(surface);
            content.Children.Add(surface);
            Require(surface.Classes.Contains(OverlayReveal.StyleClass) && !surface.Classes.Contains(OverlayReveal.ShownClass), "A hidden followed overlay must not carry 'shown'.");
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on for the overlay reveal");
            surface.IsVisible = true;
            await UntilAsync(() => surface.Classes.Contains(OverlayReveal.ShownClass) && Math.Abs(surface.Opacity - 1) < 0.001, "the followed overlay never faded in");
            surface.IsVisible = false;
            Require(!surface.Classes.Contains(OverlayReveal.ShownClass), "Leaving must drop 'shown' at once.");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            await UntilAsync(() => window.Classes.Contains("motion-off"), "motion off for the overlay reveal");
            surface.IsVisible = true;
            Require(surface.Classes.Contains(OverlayReveal.ShownClass) && surface.Opacity == 1, "Under motion-off a followed overlay must be fully shown at once.");
            surface.IsVisible = false;
            content.Children.Remove(surface);
            MotionPolicy.SetSetting(MotionPolicy.On);
            await UntilAsync(() => !window.Classes.Contains("motion-off"), "motion on after the overlay reveal");

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

            await FocusRingGeometryAsync(window);

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

    // The double focus ring sits 4 dip outside the control on every side (a negative Margin once shifted it up-left instead).
    internal static async Task FocusRingGeometryAsync(Window owner)
    {
        Button button = new() { Content = "Focus probe", Width = 160, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Window window = new() { Width = 400, Height = 200, Content = new Grid { Children = { button } }, Title = "Focus ring probe", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            await UntilAsync(() => window.IsVisible && button.IsEffectivelyVisible && button.Bounds.Width > 0, "focus probe window");
            button.Focus(Avalonia.Input.NavigationMethod.Tab, Avalonia.Input.KeyModifiers.None);
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(button) ?? throw new InvalidOperationException("No adorner layer for the probe button.");
            await UntilAsync(() => layer.Children.OfType<Control>().Any(item => ReferenceEquals(AdornerLayer.GetAdornedElement(item), button)), "the focus adorner never appeared");
            await IdleAsync();
            Control adorner = layer.Children.OfType<Control>().First(item => ReferenceEquals(AdornerLayer.GetAdornedElement(item), button));
            Point topLeft = adorner.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException("Adorner not in the window.");
            Point bottomRight = adorner.TranslatePoint(new Point(adorner.Bounds.Width, adorner.Bounds.Height), window)!.Value;
            Point buttonTopLeft = button.TranslatePoint(new Point(0, 0), window)!.Value;
            Rect ring = new(topLeft, bottomRight), target = new(buttonTopLeft, button.Bounds.Size);
            Console.WriteLine($"SMOKE focus ring: ring={ring} button={target} bounds={adorner.Bounds} clip={adorner.Clip != null}");
            Require(adorner.Clip == null, "The focus ring must not be clipped (AdornerLayer.IsClipEnabled=False).");
            Require(Math.Abs(ring.Left - (target.Left - 4)) < 0.6 && Math.Abs(ring.Top - (target.Top - 4)) < 0.6
                && Math.Abs(ring.Right - (target.Right + 4)) < 0.6 && Math.Abs(ring.Bottom - (target.Bottom + 4)) < 0.6,
                $"The focus ring must surround the button by 4 dip on every side: ring {ring}, button {target}.");
        }
        finally { window.Close(); owner.Activate(); }
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
