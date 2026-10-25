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
            if (!video) await CheckLiveThemeAsync(window, viewer);
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = variant;
                foreach (double testedWidth in new[] { 849d, 850d, 851d })
                {
                    window.Width = testedWidth; window.Height = 530;
                    // Adjacent widths straddle the context breakpoint. A one-DIP
                    // general resize tolerance would accept the previous width
                    // before the native resize/layout has even happened.
                    await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - testedWidth) < .5 / window.RenderScaling
                        && Math.Abs(window.Bounds.Height - 530) < .5 / window.RenderScaling
                        && Math.Abs(viewer.Bounds.Width - window.Bounds.Width) < .5 / window.RenderScaling);
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
            await Task.Delay(4200); // Tools must remain available beyond the former auto-hide timeout.
            if (!chrome.IsEffectivelyVisible)
                throw new InvalidOperationException("Gallery tools disappeared while the pointer was idle.");
            Check(window, viewer, video, "Dark", 851, false, "tools-idle-visible");
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
        Console.WriteLine($"SMOKE gallery caption: {(video ? "real video" : "decoded image")},849/850/851,D/L,context open/closed,wrapped status,persistent tools below media passed.");
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
        Border tools = Find<Border>(viewer, "GalleryOverlayControls");
        Rect toolsBounds = Bounds(tools, viewer);
        Console.WriteLine("SMOKE gallery tools geometry: " + JsonSerializer.Serialize(new { video, theme, width, contextOpen, state,
            viewer = viewer.Bounds.ToString(), tools = toolsBounds.ToString(), info = infoBounds.ToString(),
            viewport = viewportBounds.ToString(), viewport.IsVisible, tools.IsEffectivelyVisible }));
        if (!tools.IsEffectivelyVisible || toolsBounds.Height <= 0 || toolsBounds.Left < 0
            || toolsBounds.Right > viewer.Bounds.Width || toolsBounds.Bottom > infoBounds.Top + 1
            || viewport.IsVisible && (toolsBounds.Top < viewportBounds.Bottom - 1 || viewportBounds.Height < 100))
            throw new InvalidOperationException("Gallery tools cover media, leave the gallery, or consume its usable height.");
        foreach (Button tool in tools.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
        {
            Rect bounds = Bounds(tool, viewer), visible = VisibleBounds(tool, viewer);
            if (bounds.Width <= 0 || bounds.Height <= 0 || visible.Width < bounds.Width - 1 || visible.Height < bounds.Height - 1)
                throw new InvalidOperationException("Gallery tool is clipped: " + tool.Name);
        }
        if (video && viewport.IsVisible)
        {
            Border transport = Find<Border>(viewer, "GalleryVideoTransport");
            Color background = Solid(transport.Background);
            Rect transportBounds = Bounds(transport, viewer);
            Control surface = viewer.GetGalleryVisualDescendants().OfType<Control>()
                .Single(control => control.Name is "GalleryNativeVideo" or "GallerySoftwareVideo");
            Rect surfaceBounds = Bounds(surface, viewer);
            if (surfaceBounds.Height < 100 || transportBounds.Top < surfaceBounds.Bottom - 1
                || transportBounds.Bottom > viewportBounds.Bottom + 1)
                throw new InvalidOperationException("Video transport covers its picture or leaves the media viewport.");
            foreach (Control action in transport.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible && control.Name is "GalleryVideoSeek" or "GalleryVideoVolume"
                    or "GalleryVideoPlay" or "GalleryVideoPause" or "GalleryVideoStop"))
            {
                Rect bounds = Bounds(action, viewer), visible = VisibleBounds(action, viewer);
                if (bounds.Width <= 0 || bounds.Height <= 0 || visible.Width < bounds.Width - 1 || visible.Height < bounds.Height - 1)
                    throw new InvalidOperationException("Video transport action is clipped: " + action.Name);
            }
            foreach (string name in new[] { "GalleryVideoTime", "GalleryVideoStatus" })
            {
                TextBlock text = Find<TextBlock>(viewer, name);
                if (text.IsEffectivelyVisible && Contrast(text.Foreground, background) < 4.5)
                    throw new InvalidOperationException(name + " lost readable contrast on its themed transport.");
            }
        }
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

    internal static async Task CheckLiveThemeAsync(MainWindow window, GalleryViewer viewer)
    {
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        bool originalContext = viewer.CaptureState().ContextOpen;
        Border help = Find<Border>(viewer, "GalleryShortcutHelp");
        bool originalHelp = help.IsVisible;
        Dictionary<string, Color>? firstLight = null, previous = null;
        try
        {
            SetContext(viewer, true);
            if (!help.IsVisible) Find<Button>(viewer, "GalleryHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            GalleryViewerState held = viewer.CaptureState();
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = variant;
                await Program.WaitUntilAsync(() => window.ActualThemeVariant == variant && viewer.ActualThemeVariant == variant);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Dictionary<string, Color> colors = new();
                foreach (string name in new[] { "GalleryContext", "GalleryShortcutHelp", "GalleryOverlayControls", "GalleryOverlayInfo" })
                {
                    Border panel = Find<Border>(viewer, name);
                    if (!panel.IsEffectivelyVisible || panel.ActualThemeVariant != variant)
                        throw new InvalidOperationException(name + " lost its visible host theme during live theme change.");
                    Color background = Solid(panel.Background);
                    colors.Add(name + ".background", background);
                    // Native button/selector text has its own state surfaces; its contrast
                    // is checked separately against the actual rendered template brushes.
                    int index = 0;
                    foreach (TextBlock text in panel.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => !text.GetVisualAncestors().Any(parent => parent is Button or ComboBox)))
                    {
                        colors.Add(name + ".text" + index++, Solid(text.Foreground));
                        if (Contrast(text.Foreground, background) < 4.5)
                            throw new InvalidOperationException(name + " metadata/help text lost 4.5:1 contrast.");
                    }
                }
                TextBlock status = Find<TextBlock>(viewer, "GalleryStatus");
                Border statusPlate = (Border)status.Parent!;
                colors.Add("status.background", Solid(statusPlate.Background));
                colors.Add("status.foreground", Solid(status.Foreground));
                if (Contrast(status.Foreground, Solid(statusPlate.Background)) < 4.5)
                    throw new InvalidOperationException("Gallery status lost 4.5:1 contrast.");
                if (previous != null && (colors.Count != previous.Count || colors.Any(pair => !previous.TryGetValue(pair.Key, out Color old) || old == pair.Value)))
                    throw new InvalidOperationException("Gallery live theme change left a panel or metadata color unchanged.");
                if (variant == ThemeVariant.Light)
                {
                    if (firstLight != null && (colors.Count != firstLight.Count || colors.Any(pair => !firstLight.TryGetValue(pair.Key, out Color first) || first != pair.Value)))
                        throw new InvalidOperationException("Gallery did not restore its original Light palette.");
                    firstLight ??= colors;
                }
                GalleryViewerState current = viewer.CaptureState();
                if (current.Key != held.Key || current.PostId != held.PostId || current.AttachmentPosition != held.AttachmentPosition
                    || current.ManualZoom != held.ManualZoom || !current.ContextOpen || !help.IsVisible)
                    throw new InvalidOperationException("Live theme change lost the gallery asset, zoom mode, context or help.");
                if (viewer.GetGalleryVisualDescendants().OfType<ComboBox>().Any(control => control.Name == "GalleryPostAssociation"))
                    await CheckContextSelectorAsync(window, viewer, variant);
                Console.WriteLine("SMOKE gallery live theme: " + JsonSerializer.Serialize(new
                { theme = variant.ToString(), colors = colors.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()), asset = current.Key?.ToString() }));
                Program.CaptureWindow(window, "smoke-gallery-live-theme-" + (previous == null ? "first-" : "return-") + variant.ToString().ToLowerInvariant() + ".png");
                previous = colors;
            }
        }
        finally
        {
            if (!originalHelp && help.IsVisible) Find<Button>(viewer, "GalleryHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SetContext(viewer, originalContext);
            Application.Current.RequestedThemeVariant = originalTheme;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
    }

    private static Color Solid(IBrush? brush) => brush is ISolidColorBrush solid
        ? solid.Color : throw new InvalidOperationException("Gallery theme did not resolve a solid brush.");

    private static async Task CheckContextSelectorAsync(MainWindow window, GalleryViewer viewer, ThemeVariant workspaceTheme)
    {
        ComboBox selector = Find<ComboBox>(viewer, "GalleryPostAssociation");
        int selected = selector.SelectedIndex;
        foreach (bool enabled in new[] { true, false })
        {
            selector.IsEnabled = enabled;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(220); // Let native Semi state transitions settle.
            if (selector.ActualThemeVariant != workspaceTheme || window.ActualThemeVariant != workspaceTheme
                || viewer.ActualThemeVariant != workspaceTheme)
                throw new InvalidOperationException("Post selector does not follow the workspace theme.");
            TextBlock label = selector.GetVisualDescendants().OfType<TextBlock>().First(text => text.IsEffectivelyVisible && text.Text?.StartsWith("#") == true);
            PathIcon arrow = selector.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph");
            // Compare actual rendered template brushes to the current themed post surface.
            Color contextBackground = ((ISolidColorBrush)Find<Border>(viewer, "GalleryContext").Background!).Color;
            Border selectorBackground = selector.GetTemplateDescendants().OfType<Border>().Single(border => border.Name == "Background");
            Color background = Composite(selectorBackground.Background, contextBackground);
            double textContrast = Contrast(label.Foreground, background), arrowContrast = Contrast(arrow.Foreground, background);
            Console.WriteLine($"SMOKE gallery context selector: workspace={workspaceTheme}, enabled={enabled}, text={textContrast:0.###}, arrow={arrowContrast:0.###}");
            if (enabled && (textContrast < 4.5 || arrowContrast < 3))
                throw new InvalidOperationException("Post selector label or chevron is unreadable against its themed context.");
            Program.CaptureWindow(window, $"smoke-gallery-context-selector-{workspaceTheme.ToString().ToLowerInvariant()}-{(enabled ? "enabled" : "disabled")}.png");
        }
        selector.IsEnabled = true;
        selector.IsDropDownOpen = true;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Popup popup = selector.GetTemplateDescendants().OfType<Popup>().Single();
        await Program.WaitUntilAsync(() => popup.IsOpen && popup.Child is { Bounds.Width: > 0, Bounds.Height: > 0 });
        Control child = popup.Child!;
        if (child.ActualThemeVariant != workspaceTheme || selector.SelectedIndex != selected)
            throw new InvalidOperationException("Post selector popup lost its host theme resources or changed the association.");
        ComboBoxItem[] items = child.GetVisualDescendants().OfType<ComboBoxItem>().ToArray();
        // The 55-association fixture is virtualized; inspect the visible popup rows.
        if (items.Length == 0 || items.Any(item => item.ActualThemeVariant != workspaceTheme))
            throw new InvalidOperationException("Post selector popup rows lost the host theme.");
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
