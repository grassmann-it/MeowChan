using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_GALLERY_TOOLBAR_ONLY. A real main-window gallery with a decoded picture and a post is
// captured at the two reference sizes in both themes, with the post panel closed and open.
internal static partial class GalleryToolbarLayoutChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId Resource = new("toolbar-fixture", "g", "77");

    internal static async Task RunAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "gallery-toolbar-fixture");
        string language = Texts.Language;
        ThemeVariant theme = Application.Current!.RequestedThemeVariant ?? ThemeVariant.Default;
        Size originalSize = window.ClientSize;
        try
        {
            Texts.SetLanguage("en");
            GalleryEntry first = await EntryAsync(Path.Combine(root, "one"), "first.png", "1", "81", 0);
            GalleryEntry second = await EntryAsync(Path.Combine(root, "two"), "second.png", "2", "82", 1);
            await window.OpenMediaGalleryLaidOutAsync([first, second], first.EntryKey);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => host.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            await UntilAsync(() => Find<Image>(viewer, "GalleryImage").Source is Bitmap);
            string output = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_TOOLBAR_OUTPUT") ?? AppContext.BaseDirectory;
            GalleryViewerState start = viewer.CaptureState();
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach ((int width, int height) in new[] { (760, 500), (1440, 920) })
            foreach (string lang in width == 760 ? new[] { "en", "de" } : new[] { "en" })
            {
                Application.Current.RequestedThemeVariant = variant;
                Texts.SetLanguage(lang);
                window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
                await UntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                await LayoutAsync(window);
                string tag = $"{width}x{height}-{variant}-{lang}".ToLowerInvariant();
                if (Find<Border>(viewer, "GalleryContext").IsVisible) Click(Find<Button>(viewer, "GalleryToggleContext"));
                viewer.SetOverflowOpen(false);
                await LayoutAsync(window);
                RequireToolbar(viewer, window, tag);
                Report(viewer, window, tag + "-closed");
                Program.CaptureWindow(window, $"smoke-gallery-toolbar-{tag}-closed.png", output);

                // The post panel never replaces the picture; below 900 dip it lies over it and leaves a strip.
                Click(Find<Button>(viewer, "GalleryToggleContext"));
                await UntilAsync(() => Find<Border>(viewer, "GalleryContext").IsVisible);
                await LayoutAsync(window);
                Grid viewport = Find<Grid>(viewer, "GalleryViewport");
                Border post = Find<Border>(viewer, "GalleryContext");
                Require(viewport.IsVisible && viewport.Bounds.Width >= 300 && viewport.Bounds.Height >= 200,
                    $"{tag}: the post panel left the picture too little room: {viewport.Bounds.Size}.");
                bool narrow = viewer.Bounds.Width < GalleryViewer.CompactPostWidth;
                Require(viewer.IsContextOverPicture == narrow, $"{tag}: post panel placement does not follow the width.");
                double strip = narrow ? viewport.Bounds.Width - post.Bounds.Width - 24 : viewport.Bounds.Width;
                Require(strip >= 280, $"{tag}: only {strip} dip of the picture stay free beside the post panel.");
                Report(viewer, window, tag + "-post");
                Program.CaptureWindow(window, $"smoke-gallery-toolbar-{tag}-post.png", output);
                Click(Find<Button>(viewer, "GalleryToggleContext"));
                await LayoutAsync(window);

                // The "⋯" menu: all rare commands, same controls, labelled, and the menu keeps within the window.
                RequireOverflow(viewer);
                await OpenOverflowByKeyAsync(window, viewer);
                CapturePopup(viewer, Path.Combine(output, $"smoke-gallery-toolbar-{tag}-menu.png"));
                Program.CaptureWindow(window, $"smoke-gallery-toolbar-{tag}-menu-window.png", output);
                // Down moves through the rows (even over the playback choice), Escape closes and returns to "⋯".
                IInputElement? firstRow = TopLevel.GetTopLevel(viewer.OverflowMenuContent)!.FocusManager!.GetFocusedElement();
                PressKey((Control)firstRow!, Key.Down);
                Require(!ReferenceEquals(firstRow, TopLevel.GetTopLevel(viewer.OverflowMenuContent)!.FocusManager!.GetFocusedElement()),
                    "Down did not move through the menu rows.");
                for (int step = 0; step < 12; step++)
                {
                    Control current = (Control)TopLevel.GetTopLevel(viewer.OverflowMenuContent)!.FocusManager!.GetFocusedElement()!;
                    PressKey(current, Key.Down);
                }
                Require(Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").SelectedIndex == 0, "Down changed the playback mode instead of moving on.");
                PressKey((Control)TopLevel.GetTopLevel(viewer.OverflowMenuContent)!.FocusManager!.GetFocusedElement()!, Key.Escape);
                await UntilAsync(() => !viewer.IsOverflowOpen);
                await UntilAsync(() => window.FocusManager?.GetFocusedElement() == viewer.OverflowButton);
                await OpenOverflowByKeyAsync(window, viewer);
                if (tag.StartsWith("760x500-dark-en"))
                {
                    // A zoom step keeps the menu open, then Escape closes it and returns the focus to "⋯".
                    Click(Find<Button>(viewer, "GalleryActual"));
                    double actualZoom = viewer.CaptureState().PhysicalZoom;
                    Click(Find<Button>(viewer, "GalleryPlus"));
                    Require(viewer.IsOverflowOpen && viewer.CaptureState().PhysicalZoom > actualZoom,
                        "Zoom in from the menu did not zoom or closed the menu.");
                    Click(Find<Button>(viewer, "GalleryMinus"));
                    Click(Find<Button>(viewer, "GalleryFit"));
                    viewer.SetOverflowOpen(false);
                    // Opening a command row (full screen is not safe here; the overlay choice is) closes the menu.
                    await OpenOverflowByKeyAsync(window, viewer);
                    ToggleButton overlayChoice = Find<ToggleButton>(viewer, "GalleryOverlayToggle");
                    bool before = overlayChoice.IsChecked == true;
                    Click(overlayChoice);
                    await UntilAsync(() => !viewer.IsOverflowOpen);
                    Require((Find<ToggleButton>(viewer, "GalleryOverlayToggle").IsChecked == true) != before,
                        "The overlay choice in the menu did not change.");
                    Click(overlayChoice);
                    await LayoutAsync(window);
                    // Choosing a slideshow moves its control onto the toolbar; manual returns it to the menu.
                    ComboBox mode = Find<ComboBox>(viewer, "GalleryPlaybackModeSelector");
                    Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
                    Button slideshow = Find<Button>(viewer, "GallerySlideshowToggle");
                    Require(!chrome.IsVisualAncestorOf(slideshow), "The slideshow control sits on the toolbar in manual mode.");
                    mode.SelectedIndex = (int)GalleryPlaybackMode.Slideshow;
                    await LayoutAsync(window);
                    Require(chrome.IsVisualAncestorOf(slideshow) && slideshow.IsEffectivelyVisible,
                        "The slideshow control did not move onto the toolbar for a chosen slideshow.");
                    mode.SelectedIndex = 0;
                    await LayoutAsync(window);
                    Require(!chrome.IsVisualAncestorOf(slideshow), "The slideshow control did not return to the menu.");
                    // A shortcut of a menu command works with the menu closed: F1 opens the shortcut help.
                    viewer.Focus();
                    PressKey(viewer, Key.F1);
                    await UntilAsync(() => Find<Border>(viewer, "GalleryShortcutHelp").IsVisible);
                    PressKey(viewer, Key.F1);
                    await UntilAsync(() => !Find<Border>(viewer, "GalleryShortcutHelp").IsVisible);
                }
                viewer.SetOverflowOpen(false);
                await LayoutAsync(window);
            }
            Require(viewer.CaptureState().Key == start.Key, "The gallery selection changed during the layout checks.");
            Console.WriteLine("SMOKE gallery toolbar: captures at 760x500 and 1440x920, Light and Dark, post panel closed and open.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = theme;
            window.Width = originalSize.Width; window.Height = originalSize.Height;
        }
    }

    private static readonly string[] Primary =
    {
        "GalleryPrevious", "GalleryCompactPosition", "GalleryNext", "GalleryFit", "GalleryActual",
        "GalleryToggleContext", "GalleryFavorite", "GalleryMore", "GalleryToolsToggle"
    };
    private static readonly string[] Rare =
    {
        "GalleryPlus", "GalleryMinus", "GalleryRating", "GalleryTags", "GalleryExportOriginal", "GalleryPlaybackModeSelector",
        "GallerySlideshowToggle", "GalleryOverlayToggle", "GalleryPostOverlayToggle", "GalleryFullscreen", "GalleryHelp",
        "GalleryVideoRepeatToggle", "GallerySaveFrame"
    };

    // Frequent controls stay visible on one row inside the viewer; none of the rare ones is on the toolbar.
    private static void RequireToolbar(GalleryViewer viewer, MainWindow window, string tag)
    {
        Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
        foreach (string name in Primary)
        {
            Control control = Find<Control>(viewer, name);
            Point? point = control.TranslatePoint(default, viewer);
            Require(control.IsEffectivelyVisible && point.HasValue && point.Value.X >= 0
                && point.Value.X + control.Bounds.Width <= viewer.Bounds.Width + 1
                && point.Value.Y + control.Bounds.Height <= viewer.Bounds.Height + 1, $"{tag}: primary control is missing or clipped: {name}");
            Require(chrome.IsVisualAncestorOf(control), $"{tag}: primary control left the toolbar: {name}");
        }
        foreach (string name in Rare)
            Require(!chrome.IsVisualAncestorOf(Find<Control>(viewer, name)), $"{tag}: rare control still on the toolbar: {name}");
        // The way back is not a toolbar button: it floats alone at the top right edge of the picture (Tom's gallery rule), labelled with its target.
        Button back = Find<Button>(viewer, "GalleryClose");
        Point? backPoint = back.TranslatePoint(default, viewer);
        Require(back.IsEffectivelyVisible && !chrome.IsVisualAncestorOf(back) && backPoint is { } origin
            && origin.X + back.Bounds.Width >= viewer.Bounds.Width - 24 && origin.X + back.Bounds.Width <= viewer.Bounds.Width + 1
            && origin.Y <= 24 && origin.X >= 0,
            $"{tag}: the way back is not at the top right of the picture.");
        Require(chrome.Bounds.Height <= 64, $"{tag}: the toolbar needs more than one row ({chrome.Bounds.Height} dip).");
        Button more = Find<Button>(viewer, "GalleryMore");
        Require(!string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(more))
            && ToolTip.GetTip(more) is string tip && tip.Length > 0, $"{tag}: the menu button has no name or tooltip.");
        // Labels follow the width: labelled at 1440, icon only below 1100.
        Require(viewer.IsCompactToolbar == (viewer.Bounds.Width < GalleryViewer.CompactToolbarWidth), $"{tag}: toolbar density does not follow the width.");
    }

    // Every rare command is a labelled row of the menu: same instance, name, label text and (where bound) shortcut.
    private static void RequireOverflow(GalleryViewer viewer)
    {
        string[] shortcuts = { "GalleryPlus", "GalleryMinus", "GalleryRating", "GalleryTags", "GalleryExportOriginal", "GalleryFullscreen", "GalleryHelp" };
        foreach (string name in Rare.Where(name => name != "GalleryPlaybackModeSelector"))
        {
            Button row = Find<Button>(viewer, name);
            Require(viewer.OverflowItems.Contains(row), "Not a row of the menu: " + name);
            Require(!string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(row)), "Menu row without a name: " + name);
            Grid content = row.Content as Grid ?? throw new InvalidOperationException("Menu row lost its content: " + name);
            Require(content.Children.OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text)), "Menu row without a visible label: " + name);
        }
        Require(shortcuts.All(name => ToolTip.GetTip(Find<Button>(viewer, name)) is string { Length: > 0 }), "A menu row lost its tooltip.");
    }

    private static async Task OpenOverflowByKeyAsync(MainWindow window, GalleryViewer viewer)
    {
        Button more = viewer.OverflowButton;
        more.Focus(Avalonia.Input.NavigationMethod.Tab);
        PressKey(more, Key.Enter);
        await UntilAsync(() => viewer.IsOverflowOpen && viewer.OverflowMenuContent.IsEffectivelyVisible && viewer.OverflowMenuContent.Bounds.Height > 0);
        await LayoutAsync(window);
        Control content = viewer.OverflowMenuContent;
        TopLevel? popup = TopLevel.GetTopLevel(content);
        Require(popup != null && !ReferenceEquals(popup, window), "The menu did not open in its own popup surface.");
        // The keyboard continues inside the menu, on the first usable row.
        await UntilAsync(() => popup!.FocusManager?.GetFocusedElement() is Visual focused && content.IsVisualAncestorOf(focused));
        PixelPoint origin = popup!.PointToScreen(default);
        PixelRect host = new(window.PointToScreen(default), new PixelSize((int)Math.Ceiling(window.Bounds.Width * window.RenderScaling),
            (int)Math.Ceiling(window.Bounds.Height * window.RenderScaling)));
        Require(host.Contains(origin) && origin.Y >= host.Y, $"The menu opened away from the window: {origin} outside {host}.");
        foreach (Control row in viewer.OverflowItems)
            Require(row.IsEffectivelyVisible && row.Bounds.Height >= 28 && row.Bounds.Width >= 200, "Menu row not shown: " + row.Name);
        Require(Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").IsEffectivelyVisible, "The playback choice is not shown in the menu.");
    }

    private static void CapturePopup(GalleryViewer viewer, string path)
    {
        if (TopLevel.GetTopLevel(viewer.OverflowMenuContent) is not { } popup) return;
        double scale = popup.RenderScaling;
        Size size = popup.ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        using RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(popup);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE popup render: " + path);
    }

    private static void PressKey(Control target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Report(GalleryViewer viewer, MainWindow window, string tag)
    {
        Grid viewport = Find<Grid>(viewer, "GalleryViewport");
        Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
        Console.WriteLine("SMOKE gallery toolbar metrics: " + JsonSerializer.Serialize(new
        {
            tag, window = window.Bounds.Size.ToString(), viewer = viewer.Bounds.Size.ToString(),
            viewport = viewport.IsVisible ? viewport.Bounds.Size.ToString() : "hidden",
            toolbar = chrome.Bounds.Size.ToString(),
            visibleButtons = viewer.GetGalleryVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && chrome.IsVisualAncestorOf(b))
        }));
    }

    private static async Task<GalleryEntry> EntryAsync(string directory, string file, string mediaId, string postId, int hue)
    {
        Directory.CreateDirectory(directory);
        using SKBitmap bitmap = new(1600, 1000);
        using (SKCanvas canvas = new(bitmap))
        {
            using SKPaint paint = new() { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1600, 1000),
                hue == 0 ? new[] { new SKColor(0x2E, 0x6F, 0xD6), new SKColor(0xF2, 0xA1, 0x3B) }
                         : new[] { new SKColor(0x2F, 0x9E, 0x6B), new SKColor(0xD6, 0x4F, 0x7A) }, SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, 1600, 1000, paint);
            using SKPaint edge = new() { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
            canvas.DrawRect(3, 3, 1594, 994, edge);
            canvas.DrawCircle(800, 500, 280, edge);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        byte[] png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory, file), png);
        object entry = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file,
            FileName = file, OriginalFileName = file, Length = png.Length,
            Md5 = Convert.ToBase64String(MD5.HashData(png)), Sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { [file] = entry } }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = snapshot.GetPage().Single();
        return new(new(Resource, directory, asset.AssetId), snapshot, asset,
            new CapturedPost(postId, 0, PostFieldPresence.All, Author: "Anonymous", Subject: "Toolbar fixture",
                Html: "<p>A short post that belongs to the picture, long enough to wrap in the narrow panel and show how much room the panel takes from the image.</p>"),
            null, false, false);
    }

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task LayoutAsync(Window window)
    {
        for (int pass = 0; pass < 3; pass++)
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Gallery toolbar smoke deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
