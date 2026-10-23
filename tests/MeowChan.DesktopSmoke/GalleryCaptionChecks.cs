using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class GalleryCaptionChecks
{
    internal static async Task RunAsync(MainWindow window, GalleryViewer viewer, bool video)
    {
        double width = window.Width, height = window.Height;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        bool contextOpen = viewer.CaptureState().ContextOpen;
        string warnings = Find<TextBlock>(viewer, "GalleryWarnings").Text ?? string.Empty;
        try
        {
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = variant;
                foreach (double testedWidth in new[] { 849d, 850d, 851d })
                {
                    window.Width = testedWidth; window.Height = 530;
                    await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, testedWidth, 530));
                    foreach (bool open in new[] { false, true })
                    {
                        SetContext(viewer, open);
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                        if (open && testedWidth == 850 && !video)
                            await CheckContextSelectorAsync(window, viewer, variant);
                        Check(window, viewer, video, variant.ToString(), testedWidth, open, "normal");
                        if (open)
                        {
                            viewer.SetWarnings("Saved local fixture media. This deliberately long explanatory status wraps onto multiple lines beside the visible post context; all text and the Show in thread action must remain reachable.");
                            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                            Check(window, viewer, video, variant.ToString(), testedWidth, open, "wrapped");
                            viewer.SetWarnings(warnings);
                        }
                    }
                }
            }
            SetContext(viewer, false);
            viewer.Focus();
            Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
            await Program.WaitUntilAsync(() => !chrome.IsVisible);
            Check(window, viewer, video, "Dark", 851, false, "chrome-hidden");
        }
        finally
        {
            viewer.SetWarnings(warnings);
            SetContext(viewer, contextOpen);
            Application.Current.RequestedThemeVariant = theme;
            window.Width = width; window.Height = height;
            viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftShift });
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
        Console.WriteLine($"SMOKE gallery caption: {(video ? "real video" : "decoded image")},849/850/851,D/L,context open/closed,wrapped status,hidden toolbar passed.");
    }

    private static void Check(MainWindow window, GalleryViewer viewer, bool video, string theme,
        double width, bool contextOpen, string state)
    {
        window.UpdateLayout();
        Border info = Find<Border>(viewer, "GalleryOverlayInfo");
        Border context = Find<Border>(viewer, "GalleryContext");
        Grid viewport = Find<Grid>(viewer, "GalleryViewport");
        Rect infoBounds = Bounds(info, viewer);
        Rect contextBounds = Bounds(context, viewer);
        Rect viewportBounds = Bounds(viewport, viewer);
        bool contextOverlap = contextOpen && infoBounds.Intersects(contextBounds);
        bool outsideMediaWidth = contextOpen && viewport.IsVisible && infoBounds.Intersects(viewportBounds)
            && (infoBounds.Left < viewportBounds.Left || infoBounds.Right > viewportBounds.Right);
        Button? readPost = viewer.GetGalleryVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "GalleryReadPost");
        Rect actionBounds = readPost == null ? default : Bounds(readPost, viewer);
        Rect visibleActionBounds = readPost == null ? default : VisibleBounds(readPost, viewer);
        string[] actionClips = readPost == null ? Array.Empty<string>() : readPost.GetVisualAncestors()
            .OfType<Control>().Where(control => control.ClipToBounds)
            .Select(control => control.GetType().Name + ": " + Bounds(control, viewer)).ToArray();
        bool rawActionOverlap = contextOpen && readPost != null && infoBounds.Intersects(actionBounds);
        // A scrolled-out action can have layout bounds underneath the caption,
        // while the ScrollContentPresenter clips every one of those pixels.
        bool actionOverlap = contextOpen && visibleActionBounds.Width > 0 && visibleActionBounds.Height > 0
            && infoBounds.Intersects(visibleActionBounds);
        Console.WriteLine("SMOKE gallery caption geometry: " + JsonSerializer.Serialize(new { video, theme, width, state,
            contextOpen, contextOverlap, actionOverlap, rawActionOverlap,
            action = actionBounds.ToString(), visibleAction = visibleActionBounds.ToString(), actionClips,
            outsideMediaWidth, info = infoBounds.ToString(), context = contextBounds.ToString(),
            viewport = viewportBounds.ToString(), info.Margin, window.RenderScaling,
            transportInset = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().FirstOrDefault()?.TransportBottomInset }));
        Program.CaptureWindow(window, $"smoke-gallery-caption-{(video ? "video" : "image")}-{theme.ToLowerInvariant()}-{width}-{contextOpen}-{state}.png");
        if (contextOverlap || actionOverlap || outsideMediaWidth)
            throw new InvalidOperationException("Gallery caption overlaps the post context or extends beyond its media viewport.");
        foreach (Control transport in viewer.GetGalleryVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control.Name is "GalleryVideoSeek" or "GalleryVideoPlay" or "GalleryVideoPause" or "GalleryVideoStop"))
            if (infoBounds.Intersects(Bounds(transport, viewer)))
                throw new InvalidOperationException("Gallery caption covers video transport: " + transport.Name);
        if (!info.IsEffectivelyVisible || infoBounds.Height <= 0 || infoBounds.Left < 0 || infoBounds.Right > viewer.Bounds.Width
            || infoBounds.Bottom > viewer.Bounds.Height)
            throw new InvalidOperationException("Gallery caption is outside the visible gallery.");
        if (contextOpen && context.Child is ScrollViewer scroll)
        {
            scroll.ScrollToEnd();
            window.UpdateLayout();
            Rect reachedAction = readPost == null ? default : Bounds(readPost, viewer);
            Rect reachedVisibleAction = readPost == null ? default : VisibleBounds(readPost, viewer);
            Console.WriteLine("SMOKE gallery caption action after scroll: " + JsonSerializer.Serialize(new
            {
                video, theme, width, state, action = reachedAction.ToString(),
                visibleAction = reachedVisibleAction.ToString(), context = contextBounds.ToString(),
                offset = scroll.Offset.ToString(), viewport = scroll.Viewport.ToString(), extent = scroll.Extent.ToString()
            }));
            if (readPost == null || !readPost.IsEffectivelyVisible || reachedAction.Bottom > contextBounds.Bottom
                || infoBounds.Intersects(reachedAction)
                || reachedVisibleAction.Width <= 0 || reachedVisibleAction.Height <= 0
                || reachedVisibleAction.Left > reachedAction.Left || reachedVisibleAction.Top > reachedAction.Top
                || reachedVisibleAction.Right < reachedAction.Right || reachedVisibleAction.Bottom < reachedAction.Bottom)
                throw new InvalidOperationException("Show in thread cannot be reached clear of the gallery caption.");
            scroll.ScrollToHome();
        }
    }

    private static async Task CheckContextSelectorAsync(MainWindow window, GalleryViewer viewer, ThemeVariant workspaceTheme)
    {
        ComboBox selector = Find<ComboBox>(viewer, "GalleryPostAssociation");
        int selected = selector.SelectedIndex;
        foreach (bool enabled in new[] { true, false })
        {
            selector.IsEnabled = enabled;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(220); // Let native Semi state transitions settle.
            if (selector.ActualThemeVariant != ThemeVariant.Dark || window.ActualThemeVariant != workspaceTheme
                || viewer.ActualThemeVariant != workspaceTheme)
                throw new InvalidOperationException("Post selector lost its local dark theme or changed the workspace theme.");
            TextBlock label = selector.GetVisualDescendants().OfType<TextBlock>().First(text => text.IsEffectivelyVisible && text.Text?.StartsWith("#") == true);
            PathIcon arrow = selector.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph");
            // Compare actual rendered template brushes to the permanent post surface.
            Color contextBackground = ((ISolidColorBrush)Find<Border>(viewer, "GalleryContext").Background!).Color;
            Border selectorBackground = selector.GetTemplateDescendants().OfType<Border>().Single(border => border.Name == "Background");
            Color background = Composite(selectorBackground.Background, contextBackground);
            double textContrast = Contrast(label.Foreground, background), arrowContrast = Contrast(arrow.Foreground, background);
            Console.WriteLine($"SMOKE gallery context selector: workspace={workspaceTheme}, enabled={enabled}, text={textContrast:0.###}, arrow={arrowContrast:0.###}");
            if (enabled && (textContrast < 4.5 || arrowContrast < 3))
                throw new InvalidOperationException("Post selector label or chevron is unreadable against its dark context.");
            Program.CaptureWindow(window, $"smoke-gallery-context-selector-{workspaceTheme.ToString().ToLowerInvariant()}-{(enabled ? "enabled" : "disabled")}.png");
        }
        selector.IsEnabled = true;
        selector.IsDropDownOpen = true;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Popup popup = selector.GetTemplateDescendants().OfType<Popup>().Single();
        await Program.WaitUntilAsync(() => popup.IsOpen && popup.Child is { Bounds.Width: > 0, Bounds.Height: > 0 });
        Control child = popup.Child!;
        if (child.ActualThemeVariant != ThemeVariant.Dark || selector.SelectedIndex != selected)
            throw new InvalidOperationException("Post selector popup lost its local dark resources or changed the association.");
        ComboBoxItem[] items = child.GetVisualDescendants().OfType<ComboBoxItem>().ToArray();
        // The 55-association fixture is virtualized; inspect the visible popup rows.
        if (items.Length == 0 || items.Any(item => item.ActualThemeVariant != ThemeVariant.Dark))
            throw new InvalidOperationException("Post selector popup rows lost the dark theme.");
        Program.CaptureWindow(window, $"smoke-gallery-context-selector-{workspaceTheme.ToString().ToLowerInvariant()}-open.png");
        CaptureContextPopup(window, selector, child, workspaceTheme);
        selector.IsDropDownOpen = false;
    }

    private static void CaptureContextPopup(MainWindow owner, ComboBox selector, Control child, ThemeVariant workspaceTheme)
    {
        // Native popups have their own top level and are absent from owner captures.
        TopLevel popupRoot = TopLevel.GetTopLevel(child)
            ?? throw new InvalidOperationException("Post selector popup has no render root.");
        child.UpdateLayout();
        double scale = popupRoot.RenderScaling;
        PixelSize pixels = PixelSize.FromSize(child.Bounds.Size, scale);
        string stem = "smoke-gallery-context-selector-" + workspaceTheme.ToString().ToLowerInvariant() + "-popup";
        using RenderTargetBitmap image = new(pixels, new Vector(96 * scale, 96 * scale));
        image.Render(child);
        image.Save(Path.Combine(AppContext.BaseDirectory, stem + ".png"), PngBitmapEncoderOptions.Default);
        string metadata = JsonSerializer.Serialize(new
        {
            capture = "popup-child", workspaceTheme = workspaceTheme.ToString(),
            popupTheme = child.ActualThemeVariant.ToString(), popupRootType = popupRoot.GetType().FullName,
            popupRootBounds = popupRoot.Bounds.ToString(), childBounds = child.Bounds.ToString(),
            popupScale = scale, ownerScale = owner.RenderScaling,
            pixelWidth = pixels.Width, pixelHeight = pixels.Height,
            selectedIndex = selector.SelectedIndex, totalAssociations = selector.ItemCount,
            realizedItems = child.GetVisualDescendants().OfType<ComboBoxItem>()
                .Select(item => new { label = item.Content?.ToString(), bounds = item.Bounds.ToString(), item.IsEffectivelyVisible })
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, stem + ".json"), metadata);
        Console.WriteLine("SMOKE gallery context popup capture: " + stem + ", scale=" + scale + ", pixels=" + pixels);
    }

    private static double Contrast(IBrush? foreground, Color background)
    {
        if (foreground is not ISolidColorBrush brush) throw new InvalidOperationException("Post selector did not resolve a solid template brush.");
        Color color = Composite(brush, background);
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color value) => .2126 * Linear(value.R) + .7152 * Linear(value.G) + .0722 * Linear(value.B);
        double first = Luminance(color), second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static Color Composite(IBrush? foreground, Color background)
    {
        if (foreground == null) return background;
        if (foreground is not ISolidColorBrush brush) throw new InvalidOperationException("Post selector did not resolve a solid background brush.");
        double alpha = brush.Color.A / 255d * brush.Opacity;
        return Color.FromRgb((byte)Math.Round(brush.Color.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(brush.Color.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(brush.Color.B * alpha + background.B * (1 - alpha)));
    }

    private static void SetContext(GalleryViewer viewer, bool open)
    {
        if (viewer.CaptureState().ContextOpen != open)
            Find<Button>(viewer, "GalleryToggleContext").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Rect VisibleBounds(Control control, Control root)
    {
        if (!control.IsEffectivelyVisible) return default;
        Rect visible = Bounds(control, root);
        foreach (Control ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.ClipToBounds) visible = visible.Intersect(Bounds(ancestor, root));
            if (ReferenceEquals(ancestor, root)) break;
        }
        return visible;
    }

    private static Rect Bounds(Control control, Control root)
    {
        if (control.TranslatePoint(default, root) is Point translated) return new Rect(translated, control.Bounds.Size);
        // Windows video chrome can belong to an owned native overlay window.
        PixelPoint point = control.PointToScreen(default), origin = root.PointToScreen(default);
        double scale = TopLevel.GetTopLevel(root)!.RenderScaling;
        double controlScale = TopLevel.GetTopLevel(control)!.RenderScaling;
        return new Rect((point.X - origin.X) / scale, (point.Y - origin.Y) / scale,
            control.Bounds.Width * controlScale / scale, control.Bounds.Height * controlScale / scale);
    }
}
