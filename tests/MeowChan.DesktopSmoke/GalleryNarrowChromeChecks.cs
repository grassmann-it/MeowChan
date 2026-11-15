using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_GALLERY_NARROW_ONLY. The gallery chrome at half-screen and smaller windows (Tom, 16.11.: "Back to thread"
// was clipped to its first letter at 945 dip): a real main-window gallery with the controls shown, a verified real picture and (with
// MEOWCHAN_VIDEO_FIXTURE) a real video, at 1920, 1280, 960, 800 and 640 dip in English and German. Every width/language cell is measured
// (clipped, truncated, overlapping or out-of-window controls are findings and fail the run) and captured: the full window plus crops of
// the top left (back + favourite), the top right, the bottom rows (transport + toolbar) and the info line, written to
// MEOWCHAN_GALLERY_NARROW_OUTPUT. Private Xvfb only (the runner's isolated display).
internal static class GalleryNarrowChromeChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly int[] Widths = [1920, 1280, 960, 800, 640];
    private static readonly string[] Languages = ["en", "de"];
    private static readonly ResourceId Resource = new("narrow-fixture", "g", "78");
    private const string ProvenanceText = "Thread 8kun /adults/ · 71 · a thread title as long as real titles are, with every word of it";
    private const string ScopeText = "Scope: all media of this thread and its quoted posts";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(MainWindow window)
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("The narrow-chrome smoke requires the parent's private Xvfb display on Linux, never a personal display.");
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "gallery-narrow-fixture");
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_NARROW_OUTPUT") ?? AppContext.BaseDirectory;
        string prefix = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_NARROW_PREFIX") ?? "narrow";
        bool report = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_NARROW_REPORT") == "1";
        Directory.CreateDirectory(output);
        string language = Texts.Language;
        Size originalSize = window.ClientSize;
        List<string> findings = [];
        try
        {
            GalleryEntry filler = await ImageEntryAsync(Path.Combine(root, "filler"));
            List<(string Kind, GalleryEntry Entry)> entries = [("image", await ImageEntryAsync(Path.Combine(root, "image")))];
            if (Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE") is { Length: > 0 } video && File.Exists(video))
                entries.Add(("video", await VideoEntryAsync(Path.Combine(root, "video"), video)));
            else Console.WriteLine("SMOKE gallery narrow: no MEOWCHAN_VIDEO_FIXTURE, the transport is not covered.");
            foreach ((string kind, GalleryEntry entry) in entries)
            {
                await window.OpenMediaGalleryLaidOutAsync([entry, filler], entry.EntryKey);
                ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
                await UntilAsync(() => host.Content is GalleryViewer);
                GalleryViewer viewer = (GalleryViewer)host.Content!;
                if (kind == "image") await UntilAsync(() => Find<Image>(viewer, "GalleryImage").Source is Bitmap);
                else await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any(player => player.IsVisible));
                viewer.ControlsIdleHideMs = Config.ControlsIdleHideMsMaximum; // the controls stay shown for the whole capture
                // The busiest bar: a running slideshow (its control and status), a scope text as long as a real thread title, a rating.
                viewer.ConfigurePlayback(new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, GalleryPlaybackPreferences.ImageSecondsMaximum,
                    VideoRule: SlideshowVideoRule.LoopAtLeastSeconds, VideoSeconds: Config.GallerySlideshowVideoSecondsMaximum));
                await LayoutAsync(window);
                Find<Button>(viewer, "GallerySlideshowToggle").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                viewer.SetScope(ScopeText);
                viewer.SetRating(null, null);
                foreach (string lang in Languages)
                foreach (int width in Widths)
                {
                    // The window's own minimum (760 dip, 640 in the compact Barebones view) is a real floor: a narrower request is not a state the app can be in.
                    if (width < window.MinWidth) { Console.WriteLine($"SMOKE gallery narrow: {width} dip is below the window minimum {window.MinWidth}; skipped."); continue; }
                    Texts.SetLanguage(lang);
                    window.WindowState = WindowState.Normal; window.Width = width; window.Height = 760;
                    await UntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, 760));
                    foreach (GalleryReturnTarget target in new[] { GalleryReturnTarget.Thread, GalleryReturnTarget.Media })
                    {
                        viewer.ReturnTarget = target;
                        // The provenance segment ("Thread 8kun /adults/ · 71" in real use) comes from a resolved attachment; a long one stands in
                        // for it (a language or medium change rewrites it, so it is set per cell).
                        TextBlock provenance = (TextBlock)typeof(GalleryViewer).GetField("_provenance", Private)!.GetValue(viewer)!;
                        provenance.Text = ProvenanceText; provenance.IsVisible = true;
                        await LayoutAsync(window);
                        Reveal(viewer);
                        await LayoutAsync(window);
                        string tag = $"{prefix}-{kind}-{width}-{lang}-{target.ToString().ToLowerInvariant()}";
                        List<string> cell = Scan(viewer, window, tag);
                        foreach (string finding in cell) Console.WriteLine("SMOKE gallery narrow finding: " + finding);
                        findings.AddRange(cell);
                        // The full set of crops for the case Tom reported; the other target is measured only.
                        if (target == GalleryReturnTarget.Thread) Capture(viewer, window, output, tag);
                    }
                }
                viewer.ReturnTarget = GalleryReturnTarget.Thread;
                // Narrower viewers than the window allows (a narrow dock, a future side layout): the arrow alone, decided by the measured label.
                if (kind == "image") await CheckBackCollapseAsync(window, viewer, output, prefix, findings);
            }
            if (findings.Count > 0 && !report)
                throw new InvalidOperationException($"The gallery chrome has {findings.Count} narrow-window finding(s):\n" + string.Join('\n', findings));
            Console.WriteLine($"SMOKE gallery narrow: {findings.Count} finding(s) over {Widths.Length} widths x {Languages.Length} languages x {entries.Count} media kinds; crops in {output}.");
        }
        finally
        {
            Texts.SetLanguage(language);
            window.Width = originalSize.Width; window.Height = originalSize.Height;
        }
    }

    // The way back collapses from the measured label: at 440 dip "Back to thread" still fits (its pair is 200 dip, half of 400), "Zurück zum Thread"
    // (230) does not; at 360 neither does. Collapsed it is the arrow in a 32 dip square with the same name and tooltip, the star right beside it.
    private static async Task CheckBackCollapseAsync(MainWindow window, GalleryViewer viewer, string output, string prefix, List<string> findings)
    {
        window.WindowState = WindowState.Normal; window.Width = 1280;
        await UntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 1280, 760));
        try
        {
            foreach (string lang in Languages)
            foreach (int width in new[] { 440, 360 })
            {
                Texts.SetLanguage(lang);
                viewer.ReturnTarget = GalleryReturnTarget.Thread;
                viewer.Width = width; viewer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                await LayoutAsync(window);
                Reveal(viewer);
                await LayoutAsync(window);
                string tag = $"{prefix}-collapse-{width}-{lang}";
                Button back = Find<Button>(viewer, "GalleryClose");
                double wanted = viewer.BackLabelledWidth;
                bool expectLabel = GalleryBackLabel.ShowsLabel(width, wanted, GalleryViewer.StarGap);
                if (viewer.IsBackCompact == expectLabel) findings.Add($"{tag} | GalleryClose | rule | labelled width {wanted:0.#}, viewer {width}: compact={viewer.IsBackCompact}, rule says label={expectLabel}");
                bool arrowOnly = Math.Abs(back.Bounds.Width - 32) < 0.5;
                if (arrowOnly != viewer.IsBackCompact) findings.Add($"{tag} | GalleryClose | form | width {back.Bounds.Width:0.#} does not match compact={viewer.IsBackCompact}");
                if (string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(back)) || ToolTip.GetTip(back) is not string { Length: > 0 })
                    findings.Add($"{tag} | GalleryClose | name | the way back lost its accessible name or tooltip");
                if (Avalonia.Automation.AutomationProperties.GetName(back) != viewer.CloseLabel) findings.Add($"{tag} | GalleryClose | name | not the label of its target");
                foreach (string cell in Scan(viewer, window, tag).Where(cell => cell.Contains("GalleryClose") || cell.Contains("FavoriteIndicator"))) findings.Add(cell);
                Console.WriteLine($"SMOKE gallery narrow collapse: {tag}: label {wanted:0.#} dip, viewer {width}, compact={viewer.IsBackCompact}, back {back.Bounds.Width:0.#} dip.");
                Capture(viewer, window, output, tag);
            }
            // The two languages must differ at 440: that is the measured threshold, not a fixed one.
        }
        finally
        {
            viewer.Width = double.NaN; viewer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            Texts.SetLanguage("en");
        }
    }

    private static void Reveal(GalleryViewer viewer) =>
        typeof(GalleryViewer).GetMethod("RevealOverlayControls", Private)!.Invoke(viewer, null);

    // ---- measurement -----------------------------------------------------------------------------------------------------

    private static readonly string[] Named =
    [
        "GalleryClose", "GalleryFavoriteIndicator", "GalleryNativeFavoriteIndicator", "GalleryPrevious", "GalleryCompactPosition", "GalleryNext",
        "GalleryFit", "GalleryActual", "GalleryToggleContext", "GalleryFavorite", "GalleryMore", "GalleryToolsToggle", "GalleryLayoutButton",
        "GalleryModeView", "GalleryModeTag"
    ];

    /// <summary>Clipped (desired wider than arranged), truncated (a text wider than its box), outside the viewer or overlapping controls.</summary>
    private static List<string> Scan(GalleryViewer viewer, MainWindow window, string tag)
    {
        List<string> found = [];
        int trimmedWithEllipsis = 0;
        double viewerWidth = viewer.Bounds.Width;
        Control[] visible = viewer.GetGalleryVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && control.Opacity > 0).ToArray();
        foreach (Control control in visible)
        {
            string name = control.Name ?? string.Empty;
            // A scroller's or a stretch host's desired size is not a clip; only controls that are buttons, their texts and the chrome rows count.
            bool inChrome = control is TextBlock or Button or Panel or Border && IsChromePart(viewer, control);
            if (!inChrome) continue;
            if (control is Button or ToggleButton or Border && name.Length > 0 && control.DesiredSize.Width - control.Margin.Left - control.Margin.Right > control.Bounds.Width + 0.75)
                found.Add($"{tag} | {name} | clipped | wants {control.DesiredSize.Width - control.Margin.Left - control.Margin.Right:0.#} dip, has {control.Bounds.Width:0.#}");
            if (control is Button button)
            {
                // The content of a button is clipped by the button: a text or icon past its right edge is a finding.
                foreach (Control inner in button.GetVisualDescendants().OfType<Control>().Where(inner => inner is TextBlock or Border && inner.IsEffectivelyVisible
                    && inner.Bounds.Width > 0 && inner.Bounds.Height > 0))
                    if (inner.TranslatePoint(new Point(inner.Bounds.Width, 0), button) is { } right && right.X > button.Bounds.Width + 0.75
                        && inner.GetVisualAncestors().OfType<Button>().First() == button)
                    { found.Add($"{tag} | {(string.IsNullOrEmpty(name) ? "button" : name)} | content clipped | {(inner as TextBlock)?.Text ?? inner.GetType().Name} ends at {right.X:0.#} of {button.Bounds.Width:0.#}"); break; }
            }
            // A text wider than its box ends in an ellipsis where the row allows it (info segments); without trimming it is cut off.
            if (control is TextBlock text && text.TextWrapping == TextWrapping.NoWrap && !string.IsNullOrEmpty(text.Text)
                && TextWidth(text) > text.Bounds.Width + 0.75)
            {
                if (text.TextTrimming == TextTrimming.None)
                    found.Add($"{tag} | {Describe(text)} | cut off | \"{text.Text}\" needs {TextWidth(text):0.#} dip, has {text.Bounds.Width:0.#}, no ellipsis");
                else trimmedWithEllipsis++;
            }
            if (control.TranslatePoint(default, viewer) is { } origin && name.Length > 0 && !IsOverlayBackdrop(name)
                && (origin.X < -0.5 || origin.X + control.Bounds.Width > viewerWidth + 0.75))
                found.Add($"{tag} | {name} | outside | x {origin.X:0.#}..{origin.X + control.Bounds.Width:0.#} of {viewerWidth:0.#}");
        }
        // The info line is one row: a wrapped line (more than one text row high) or a second status row is a finding.
        if (viewer.InfoLine is { IsVisible: true } info && info.Bounds.Height > 26)
            found.Add($"{tag} | GalleryInfoLine | wraps | {info.Bounds.Height:0.#} dip high (one row is about 20)");
        // Overlapping neighbours: named toolbar controls and the corner pair must not cover each other.
        Control[] rects = Named.Select(name => visible.FirstOrDefault(control => control.Name == name)).OfType<Control>().Distinct().ToArray();
        for (int a = 0; a < rects.Length; a++)
        for (int b = a + 1; b < rects.Length; b++)
        {
            if (rects[a].TranslatePoint(default, viewer) is not { } pa || rects[b].TranslatePoint(default, viewer) is not { } pb) continue;
            Rect ra = new(pa, rects[a].Bounds.Size), rb = new(pb, rects[b].Bounds.Size);
            Rect overlap = ra.Intersect(rb);
            if (overlap.Width > 0.75 && overlap.Height > 0.75 && !rects[a].IsVisualAncestorOf(rects[b]) && !rects[b].IsVisualAncestorOf(rects[a]))
                found.Add($"{tag} | {rects[a].Name} x {rects[b].Name} | overlap | {overlap.Width:0.#} x {overlap.Height:0.#} dip");
        }
        if (trimmedWithEllipsis > 0) Console.WriteLine($"SMOKE gallery narrow note: {tag}: {trimmedWithEllipsis} text(s) end in an ellipsis.");
        return found;
    }

    private static bool IsOverlayBackdrop(string name) => name is "GalleryViewport" or "GalleryImage" or "GalleryNativeVideoOverlay";

    private static bool IsChromePart(GalleryViewer viewer, Control control)
    {
        foreach (Visual ancestor in control.GetSelfAndVisualAncestors())
        {
            if (ancestor is not Control named) continue;
            if (named.Name is "GalleryOverlayControls" or "GalleryClose" or "GalleryFavoriteIndicator" or "GalleryNativeFavoriteIndicator"
                or "GalleryInfoLine" or "GalleryOverlayInfo" or "GalleryOverlayFooter" or "GalleryOverlayTransportHost" or "GalleryVideoTransport"
                or "GalleryDockTransportHost" or "GalleryDock" or "GalleryToolbar")
                return true;
            if (ReferenceEquals(ancestor, viewer)) break;
        }
        return false;
    }

    private static string Describe(TextBlock text)
    {
        for (Visual? at = text; at != null; at = at.GetVisualParent())
            if (at is Control { Name.Length: > 0 } named) return named.Name + (ReferenceEquals(at, text) ? string.Empty : "/text");
        return "text";
    }

    private static double TextWidth(TextBlock text)
    {
        FormattedText formatted = new(text.Text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize, null);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    // ---- captures --------------------------------------------------------------------------------------------------------

    private static void Capture(GalleryViewer viewer, MainWindow window, string output, string tag)
    {
        window.UpdateLayout();
        double scale = window.RenderScaling;
        using RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using MemoryStream stream = new();
        bitmap.Save(stream);
        using SKBitmap full = SKBitmap.Decode(stream.ToArray());
        Point origin = viewer.TranslatePoint(default, window) ?? default;
        int vx = (int)Math.Round(origin.X * scale), vy = (int)Math.Round(origin.Y * scale);
        int vw = (int)Math.Round(viewer.Bounds.Width * scale), vh = (int)Math.Round(viewer.Bounds.Height * scale);
        Crop(full, output, tag + "-topleft", new SKRectI(vx, vy, vx + Math.Min(vw, (int)(520 * scale)), vy + (int)(76 * scale)));
        Crop(full, output, tag + "-topright", new SKRectI(Math.Max(0, full.Width - (int)(520 * scale)), 0, full.Width, vy + (int)(76 * scale)));
        Crop(full, output, tag + "-bottom", new SKRectI(vx, vy + vh - (int)(190 * scale), vx + vw, vy + vh));
        Crop(full, output, tag + "-info", new SKRectI(vx, vy + vh - (int)(40 * scale), vx + vw, vy + vh));
        Console.WriteLine("SMOKE gallery narrow capture: " + tag);
    }

    private static void Crop(SKBitmap full, string output, string name, SKRectI rect)
    {
        rect = new SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top), Math.Min(full.Width, rect.Right), Math.Min(full.Height, rect.Bottom));
        if (rect.Width < 1 || rect.Height < 1) return;
        using SKBitmap part = new(rect.Width, rect.Height);
        full.ExtractSubset(part, rect);
        using SKImage image = SKImage.FromBitmap(part);
        File.WriteAllBytes(Path.Combine(output, name + ".png"), image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    // ---- fixtures --------------------------------------------------------------------------------------------------------

    private static async Task<GalleryEntry> ImageEntryAsync(string directory)
    {
        using SKBitmap bitmap = new(1600, 1000);
        using (SKCanvas canvas = new(bitmap))
        {
            using SKPaint paint = new() { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1600, 1000),
                [new SKColor(0x2E, 0x6F, 0xD6), new SKColor(0x20, 0x20, 0x28)], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, 1600, 1000, paint);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        return await AssetAsync(directory, "narrow.png", image.Encode(SKEncodedImageFormat.Png, 100).ToArray(), "1", "71");
    }

    private static async Task<GalleryEntry> VideoEntryAsync(string directory, string path) =>
        await AssetAsync(directory, "narrow" + Path.GetExtension(path), await File.ReadAllBytesAsync(path), "2", "72");

    private static async Task<GalleryEntry> AssetAsync(string directory, string file, byte[] bytes, string mediaId, string postId)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
        object entry = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file, FileName = file,
            OriginalFileName = file, Length = bytes.Length, Md5 = Convert.ToBase64String(MD5.HashData(bytes)),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { [file] = entry } }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = snapshot.GetPage().Single();
        return new(new(Resource, directory, asset.AssetId), snapshot, asset,
            new CapturedPost(postId, 0, PostFieldPresence.All, Author: "Anonymous", Subject: "Narrow fixture",
                Html: "<p>A short post that belongs to the picture.</p>"), null, false, false);
    }

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);

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
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Gallery narrow-chrome smoke deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
