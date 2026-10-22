using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class GalleryToolbarContrastChecks
{
    internal static async Task RunAsync(Window window, GalleryViewer viewer, ThemeVariant theme, List<string> failures)
    {
        ToggleButton favorite = viewer.GetGalleryVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "GalleryFavorite");
        ToggleButton context = viewer.GetGalleryVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "GalleryToggleContext");
        foreach (bool contextOpen in new[] { false, true })
        foreach (bool marked in new[] { false, true })
        {
            if (context.IsChecked != contextOpen) context.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (favorite.IsChecked != marked) favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => context.IsChecked == contextOpen && favorite.IsChecked == marked);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(220); // Let the native Semi state brush transition settle before measuring it.
            window.UpdateLayout();
            if (window.ActualThemeVariant != theme || viewer.ActualThemeVariant != theme)
                throw new InvalidOperationException("The toolbar theme changed its surrounding gallery or workspace.");
            Console.WriteLine($"SMOKE gallery toolbar scope: workspace={window.ActualThemeVariant}, gallery={viewer.ActualThemeVariant}, toolbar={favorite.ActualThemeVariant}");
            foreach (ToggleButton toggle in new[] { context, favorite })
                Inspect(toggle, theme, contextOpen, marked, failures);
            Program.CaptureWindow(window, $"smoke-gallery-toolbar-{theme.ToString().ToLowerInvariant()}-context-{contextOpen}-favorite-{marked}.png");
        }
        // Leave the existing playback checks in their original closed/unmarked state.
        if (favorite.IsChecked == true) favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (context.IsChecked == true) context.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => favorite.IsChecked == false && context.IsChecked == false);
    }

    private static void Inspect(ToggleButton toggle, ThemeVariant theme, bool contextOpen, bool marked, List<string> failures)
    {
        TextBlock label = toggle.GetVisualDescendants().OfType<TextBlock>().Single();
        Border icon = toggle.GetVisualDescendants().OfType<Border>().Single(border => border.OpacityMask != null);
        Color textColor = Solid(label.Foreground);
        Color iconColor = Solid(icon.Background);
        List<(string Element, ISolidColorBrush Brush)> layers = new();
        foreach (Visual parent in label.GetVisualAncestors())
        {
            IBrush? background = parent switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                _ => null
            };
            if (background is ISolidColorBrush solid && solid.Color.A > 0)
                layers.Add((parent.GetType().Name + ":" + (parent as Control)?.Name, solid));
            if (parent is Control { Name: "GalleryOverlayControls" }) break;
        }
        // Video pixels can be white or black. Composite the actual template/chrome
        // brush layers over both extremes instead of assuming an opaque backdrop.
        Color[] backgrounds = new[] { Colors.Black, Colors.White }.Select(backdrop =>
        {
            foreach ((string _, ISolidColorBrush layer) in layers.AsEnumerable().Reverse()) backdrop = Over(Solid(layer), backdrop);
            return backdrop;
        }).ToArray();
        double textRatio = backgrounds.Min(backdrop => Contrast(Over(textColor, backdrop), backdrop));
        double iconRatio = backgrounds.Min(backdrop => Contrast(Over(iconColor, backdrop), backdrop));
        Console.WriteLine("SMOKE gallery toolbar contrast: " + JsonSerializer.Serialize(new
        {
            theme = theme.ToString(), contextOpen, marked, toggle.Name, toggle.IsChecked,
            actualTheme = toggle.ActualThemeVariant.ToString(), text = textColor.ToString(), icon = iconColor.ToString(),
            layers = layers.Select(layer => new { layer.Element, color = layer.Brush.Color.ToString(), layer.Brush.Opacity,
                effectiveColor = Solid(layer.Brush).ToString() }),
            backgrounds = backgrounds.Select(color => color.ToString()), textRatio, iconRatio,
            label.IsEffectivelyVisible, label.Bounds
        }));
        if (!label.IsEffectivelyVisible || label.Bounds.Width <= 0 || textRatio < 4.5 || iconRatio < 3)
            failures.Add($"toolbar/{theme}/{toggle.Name}/context={contextOpen}/favorite={marked}: text={textRatio:0.###}, icon={iconRatio:0.###}");
    }

    private static Color Solid(IBrush? brush)
    {
        ISolidColorBrush solid = brush as ISolidColorBrush
            ?? throw new InvalidOperationException("Gallery toolbar did not resolve a solid brush.");
        Color color = solid.Color;
        return Color.FromArgb((byte)Math.Round(color.A * solid.Opacity), color.R, color.G, color.B);
    }
    private static Color Over(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        byte Blend(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Blend(foreground.R, background.R), Blend(foreground.G, background.G), Blend(foreground.B, background.B));
    }
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
