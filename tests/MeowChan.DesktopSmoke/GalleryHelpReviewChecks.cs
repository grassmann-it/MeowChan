using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Opt-in regression probe for the independent shortcut-help/toolbar layout.
internal static class GalleryHelpReviewChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        bool strict = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_HELP_REVIEW") == "1";
        bool trace = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_HELP_TRACE") == "1";
        List<string> failures = new();
        List<string> geometryObservations = new();
        foreach (string mode in trace ? new[] { "Barebones" } : new[] { "Barebones", "Simple", "Advanced" })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            foreach (string language in trace ? new[] { "de" } : new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                await window.OpenGalleryAsync(new ResourceId("4chan", "g", "1000"));
                ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
                await Program.WaitUntilAsync(() => host.Content is GalleryViewer current && current.GetGalleryVisualDescendants()
                    .OfType<Image>().Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
                GalleryViewer viewer = (GalleryViewer)host.Content!;
                if (viewer.CaptureState().ContextOpen) Click(Find<Button>(viewer, "GalleryToggleContext"));
                Button helpAction = Find<Button>(viewer, "GalleryHelp");
                helpAction.Focus(); Click(helpAction);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
                Border help = Find<Border>(viewer, "GalleryShortcutHelp");
                Border info = Find<Border>(viewer, "GalleryOverlayInfo");
                Rect chromeBounds = Bounds(chrome, viewer), helpBounds = Bounds(help, viewer);
                Rect overlap = chromeBounds.Intersect(helpBounds);
                string scene = mode + "-" + language + "-" + width + "x" + height;
                if (overlap.Width > 0.01 && overlap.Height > 0.01) geometryObservations.Add(scene + ": help/chrome border overlap " + overlap);
                if (helpBounds.Left < -1 || helpBounds.Top < -1 || helpBounds.Right > viewer.Bounds.Width + 1
                    || helpBounds.Bottom > viewer.Bounds.Height + 1) failures.Add(scene + ": help leaves gallery bounds");
                Console.WriteLine("SMOKE gallery-help geometry: " + JsonSerializer.Serialize(new {
                    scene, scale = window.RenderScaling, gallery = viewer.Bounds.ToString(), chrome = chromeBounds.ToString(),
                    help = helpBounds.ToString(), info = Bounds(info, viewer).ToString(), overlap = overlap.ToString(),
                    chromeHelpAction = Bounds(helpAction, viewer).ToString(), helpIsVisible = help.IsEffectivelyVisible }));
                Console.WriteLine("SMOKE gallery-help toolbar-actions: " + JsonSerializer.Serialize(new {
                    scene, actions = chrome.GetVisualDescendants().OfType<Button>()
                        .Where(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled)
                        .Select(button => new { button.Name, bounds = Bounds(button, viewer).ToString(),
                            helpIntersection = Bounds(button, viewer).Intersect(helpBounds).ToString() }).ToArray() }));
                Program.CaptureWindow(window, "gallery-help-" + scene + "-open.png");
                HashSet<string> visited = new();
                bool capturedClose = false;
                bool traced = false;
                foreach (NavigationDirection direction in new[] { NavigationDirection.Next, NavigationDirection.Previous })
                {
                    helpAction.Focus();
                    for (int tab = 0; tab < 36; tab++)
                    {
                        window.FocusManager!.TryMoveFocus(direction);
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                        if (window.FocusManager.GetFocusedElement() is not Control focus ||
                            !(ReferenceEquals(focus, viewer) || focus.GetVisualAncestors().Contains(viewer)))
                        { failures.Add(scene + ": tab escaped gallery"); break; }
                        if (focus is not Button button || string.IsNullOrEmpty(button.Name)) continue;
                        visited.Add(button.Name);
                        Rect area = Bounds(button, viewer);
                        Point center = new(area.Center.X, area.Center.Y);
                        Point? inWindow = viewer.TranslatePoint(center, window);
                        Control? hit = inWindow is { } point ? window.InputHitTest(point) as Control : null;
                        bool reachable = hit != null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button));
                        Console.WriteLine("SMOKE gallery-help focus: " + JsonSerializer.Serialize(new {
                            scene, direction = direction.ToString(), step = tab, name = button.Name,
                            bounds = area.ToString(), hit = hit?.GetType().Name, reachable }));
                        if (!reachable) failures.Add(scene + ": focused action is obscured " + button.Name);
                        if (trace && !traced && !reachable && inWindow is { } fixedPoint)
                        {
                            traced = true;
                            await TraceHitAsync(window, viewer, button, fixedPoint, scene);
                            Program.CaptureWindow(window, "gallery-help-" + scene + "-after-hittrace.png");
                        }
                        if (button.Name is "GalleryHelpMore" or "GalleryHelpClose")
                        {
                            Rect visible = VisibleBounds(button, viewer);
                            if (area.Width <= 0 || area.Height <= 0 || visible.Width < area.Width - 1 || visible.Height < area.Height - 1)
                                failures.Add(scene + ": focused help action is clipped " + button.Name);
                        }
                        if (!capturedClose && button.Name == "GalleryHelpClose" && direction == NavigationDirection.Next)
                        {
                            Program.CaptureWindow(window, "gallery-help-" + scene + "-close-focused.png");
                            capturedClose = true;
                        }
                    }
                }
                if (!visited.Contains("GalleryHelpClose") || !visited.Contains("GalleryHelpMore"))
                    failures.Add(scene + ": tab missed shortcut-help actions");
                Click(Find<Button>(viewer, "GalleryHelpClose"));
                if (help.IsVisible || !ReferenceEquals(window.FocusManager!.GetFocusedElement(), viewer))
                    failures.Add(scene + ": closing help did not restore viewer focus");
                Click(Find<Button>(viewer, "GalleryClose"));
                await window.GalleryCleanupTask;
            }
        }
        Console.WriteLine("SMOKE gallery-help result: " + JsonSerializer.Serialize(new { strict, failures, geometryObservations }));
        if (strict && failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures.Distinct()));
        Console.WriteLine("SMOKE gallery-help: bounded shortcut-help geometry and in-process Tab/ShiftTab review completed; no OS input or AT claim.");
    }

    // Observe the same focus reference and fixed window point without navigation or layout mutation.
    // An animation callback is an animation tick, not proof that the OS painted a frame.
    private static async Task TraceHitAsync(MainWindow window, GalleryViewer viewer, Button target, Point point, string scene)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        object[] PathOf(Visual? visual) => visual == null ? Array.Empty<object>() : visual.GetVisualAncestors().Prepend(visual)
            .Select(item => (object)new {
                type = item.GetType().Name, name = (item as Control)?.Name,
                identity = RuntimeHelpers.GetHashCode(item), item.ZIndex, item.IsVisible,
                enabled = (item as InputElement)?.IsEffectivelyEnabled,
                hitTestVisible = (item as InputElement)?.IsHitTestVisible,
                bounds = item.Bounds.ToString(), originInWindow = item.TranslatePoint(default, window)?.ToString()
            }).ToArray();
        void Record(string stage, TimeSpan? animationTime = null)
        {
            Visual? hit = window.InputHitTest(point) as Visual;
            Visual? focus = window.FocusManager!.GetFocusedElement() as Visual;
            Console.WriteLine("SMOKE gallery-help hittrace: " + JsonSerializer.Serialize(new {
                scene, stage, elapsedMs = elapsed.Elapsed.TotalMilliseconds, animationTime,
                fixedPoint = point.ToString(), targetBounds = target.TranslatePoint(default, viewer) is { } origin ? new Rect(origin, target.Bounds.Size).ToString() : null,
                sameFocus = ReferenceEquals(focus, target), targetAttached = target.GetVisualAncestors().Contains(viewer),
                reachable = hit != null && (ReferenceEquals(hit, target) || hit.GetVisualAncestors().Contains(target)),
                hitPath = PathOf(hit), focusPath = PathOf(focus), targetPath = PathOf(target)
            }));
        }
        Record("immediate");
        for (int tick = 1; tick <= 3; tick++)
        {
            TaskCompletionSource<TimeSpan> animation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(time => animation.TrySetResult(time));
            TimeSpan time = await animation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Record("animation-tick-" + tick, time);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Record("dispatcher-background-" + tick, time);
        }
    }

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Rect Bounds(Control control, Visual relative) =>
        new(control.TranslatePoint(default, relative) ?? throw new InvalidOperationException("Detached control: " + control.Name), control.Bounds.Size);
    private static Rect VisibleBounds(Control control, GalleryViewer viewer)
    {
        Rect visible = Bounds(control, viewer).Intersect(new Rect(viewer.Bounds.Size));
        foreach (Control ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.ClipToBounds) visible = visible.Intersect(Bounds(ancestor, viewer));
            if (ReferenceEquals(ancestor, viewer)) break;
        }
        return visible;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
