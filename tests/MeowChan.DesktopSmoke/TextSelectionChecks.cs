using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Live application controls and rendered client-window pixels; routed input, not OS keyboard injection.
internal static class TextSelectionChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        TextBox url = window.FindControl<TextBox>("UrlInput")!;
        TextBox search = window.ArchivePage.FindControl<TextBox>("ArchiveSearchText")!;
        string? originalUrl = url.Text, originalSearch = search.Text;
        List<string> failures = [];
        async Task CheckCase(TextBox input, string scene, ThemeVariant variant, string value, string replacement)
        {
            try { await CheckAsync(window, input, scene, variant, value, replacement); }
            catch (Exception error) { failures.Add(scene + "/" + variant + ": " + error.Message); Console.WriteLine("SMOKE text-selection failure: " + error); }
        }
        try
        {
            Require(window.SessionHost.IsPaused, "Text selection fixture requires paused network work.");
            Texts.SetLanguage("en");
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = variant;
                Click(window, "DownloadsNavigationButton");
                await CheckCase(url, "url", variant, "https://boards.4chan.org/g/thread/123456789", "replacement URL draft");
                Click(window, "ArchiveNavigationButton");
                await CheckCase(search, "search", variant, "archive selection visibility sample", "replacement archive query");
            }
        }
        finally
        {
            url.Text = originalUrl; search.Text = originalSearch;
            Application.Current.RequestedThemeVariant = theme;
            Texts.SetLanguage(language);
            Click(window, "DownloadsNavigationButton");
        }
        Require(failures.Count == 0, string.Join("; ", failures));
        Console.WriteLine("SMOKE text selection: real URL/archive search, EN, Light/Dark, routed Ctrl/Command+A, selection-range/pixel checks and routed replacement passed; no OS keyboard claim.");
    }

    private static async Task CheckAsync(MainWindow window, TextBox input, string scene, ThemeVariant theme, string value, string replacement)
    {
        await Program.WaitUntilAsync(() => input.IsEffectivelyVisible && input.Bounds.Width > 0);
        input.BringIntoView();
        input.Text = value;
        input.CaretIndex = value.Length;
        input.SelectionStart = input.SelectionEnd = value.Length;
        bool focusSucceeded = input.Focus(NavigationMethod.Tab);
        await Settle(window);
        ISolidColorBrush? brush = input.SelectionBrush as ISolidColorBrush;
        string stem = $"smoke-text-selection-{scene}-{theme}";
        Program.CaptureWindow(window, stem + "-before.png");
        KeyModifiers modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyEventArgs select = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = modifier };
        input.RaiseEvent(select);
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.A, KeyModifiers = modifier });
        await Settle(window);
        Program.CaptureWindow(window, stem + ".png");
        int selected = brush == null ? -1 : CountFill(window, input, stem + "-before.png", stem + ".png", brush);
        TextPresenter? presenter = input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        Console.WriteLine("SMOKE text-selection evidence: " + JsonSerializer.Serialize(new
        {
            scene, theme = theme.ToString(), language = Texts.Language,
            input = "routed Ctrl/Command+A, not OS keyboard", select.Handled,
            focusSucceeded, input.IsFocused, input.SelectionStart, input.SelectionEnd,
            selectionBrush = input.SelectionBrush?.ToString(), selectionBrushOpacity = brush?.Opacity,
            selectionForegroundBrush = input.SelectionForegroundBrush?.ToString(),
            input.Opacity, input.IsVisible, input.IsEffectivelyVisible, bounds = input.Bounds.ToString(),
            presenter = presenter == null ? null : new
            {
                presenter.Name, presenter.SelectionStart, presenter.SelectionEnd,
                selectionBrush = presenter.SelectionBrush?.ToString(), selectionForegroundBrush = presenter.SelectionForegroundBrush?.ToString(),
                background = presenter.Background?.ToString(),
                presenter.ShowSelectionHighlight, presenter.Opacity, presenter.IsVisible, presenter.IsEffectivelyVisible,
                bounds = presenter.Bounds.ToString()
            },
            matchingVisibleCompositePixels = selected, minimumRgbChange = 12, compositeChannelTolerance = 3,
            capture = stem + ".png", captureKind = "live Window RenderTargetBitmap client pixels"
        }));
        Require(focusSucceeded && input.IsFocused, scene + ": actual input is not focused.");
        Require(Math.Min(input.SelectionStart, input.SelectionEnd) == 0
            && Math.Max(input.SelectionStart, input.SelectionEnd) == value.Length,
            scene + ": routed Select All did not select the entire real input.");
        Require(presenter != null && presenter.ShowSelectionHighlight && presenter.IsEffectivelyVisible && presenter.Opacity > 0,
            scene + ": actual TextPresenter does not show selection highlight.");
        Require(brush != null && brush.Color.A > 0 && brush.Opacity > 0, scene + ": pixel check requires a visible solid selection fill.");
        Require(selected >= 100, scene + ": live selection did not produce at least 100 visibly changed pixels matching the theme brush composite.");
        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = replacement });
        await Settle(window);
        Require(input.Text == replacement, scene + ": routed typing did not replace selected text.");
        Require(input.SelectionStart == input.SelectionEnd, scene + ": replacement retained a nonempty selection.");
    }

    private static int CountFill(Window window, Control input, string beforeFile, string selectedFile, ISolidColorBrush brush)
    {
        using SKBitmap before = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, beforeFile))
            ?? throw new InvalidOperationException("Could not decode unselected live capture.");
        using SKBitmap bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, selectedFile))
            ?? throw new InvalidOperationException("Could not decode live selection capture.");
        Require(before.Width == bitmap.Width && before.Height == bitmap.Height, "Selection capture dimensions changed.");
        Color color = brush.Color;
        double alpha = Math.Clamp(brush.Opacity * color.A / 255d, 0, 1);
        Point origin = input.TranslatePoint(default, window)
            ?? throw new InvalidOperationException("Input has no window-relative coordinates.");
        double scale = window.RenderScaling;
        int left = Math.Clamp((int)Math.Ceiling(origin.X * scale), 0, bitmap.Width);
        int top = Math.Clamp((int)Math.Ceiling(origin.Y * scale), 0, bitmap.Height);
        int right = Math.Clamp((int)Math.Floor((origin.X + input.Bounds.Width) * scale), 0, bitmap.Width);
        int bottom = Math.Clamp((int)Math.Floor((origin.Y + input.Bounds.Height) * scale), 0, bitmap.Height);
        int count = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                SKColor baseline = before.GetPixel(x, y), pixel = bitmap.GetPixel(x, y);
                // Semi's selection fill is translucent. Composite over the corresponding
                // real unselected pixel, rather than expecting the raw brush RGB.
                double red = color.R * alpha + baseline.Red * (1 - alpha);
                double green = color.G * alpha + baseline.Green * (1 - alpha);
                double blue = color.B * alpha + baseline.Blue * (1 - alpha);
                int visibleChange = Math.Max(Math.Abs(pixel.Red - baseline.Red),
                    Math.Max(Math.Abs(pixel.Green - baseline.Green), Math.Abs(pixel.Blue - baseline.Blue)));
                if (visibleChange >= 12 && Math.Abs(pixel.Red - red) <= 3 && Math.Abs(pixel.Green - green) <= 3
                    && Math.Abs(pixel.Blue - blue) <= 3 && pixel.Alpha >= 250) count++;
            }
        return count;
    }

    private static void Click(Window window, string name) => window.FindControl<Button>(name)!
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Task.Delay(80);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
