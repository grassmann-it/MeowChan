using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// MEOWCHAN_MEDIA_SLOT_ONLY: the visible frame of a preview is the drawn image, and what sits on it (video play button, favourite
/// star, type badge, hover/focus frame) is placed on that image, not on the reserved slot. Three generated pictures (portrait,
/// landscape, video poster) on three surfaces: the reader's attachment beside a post text, the archive's saved-media tile and
/// the media page tile; Light and Dark screenshots. No file, host or network is involved.
/// MEOWCHAN_MEDIA_SLOT_REPORT=1 reports the defects instead of failing (used to capture the state before a fix);
/// MEOWCHAN_MEDIA_SLOT_CAPTURE_DIR names the screenshot directory (default: the assembly folder).
/// </summary>
internal static class MediaSlotChecks
{
    private const double Tolerance = 1.5;
    private static readonly List<string> Defects = new();

    internal static async Task RunAsync(MainWindow owner)
    {
        string directory = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SLOT_CAPTURE_DIR") is { Length: > 0 } custom ? custom : AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        bool reportOnly = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SLOT_REPORT") == "1";
        Texts.SetLanguage("en");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            string name = theme == ThemeVariant.Dark ? "dark" : "light";
            await CheckReaderAsync(theme, name, directory);
            await CheckListRowsAsync(theme, name, directory);
            await CheckTilesAsync(theme, name, directory);
        }
        if (Defects.Count > 0)
        {
            foreach (string defect in Defects) Console.WriteLine("SMOKE media-slot defect: " + defect);
            if (!reportOnly) throw new InvalidOperationException("Media slot geometry: " + string.Join(" | ", Defects));
        }
        Console.WriteLine("SMOKE media slot: " + (Defects.Count == 0 ? "reader attachment, archive tile and media page tile hug the drawn image in Light and Dark; play button centered, star and badges on the image corners, hover/focus frame around the image."
            : Defects.Count + " defects reported (report-only run)."));
    }

    private static void Expect(bool condition, string message) { if (!condition) Defects.Add(message); }

    private static Bitmap Picture(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(color);
            using SKPaint line = new() { Color = SKColors.White, StrokeWidth = Math.Max(2, width / 60f), IsStroke = true, IsAntialias = true };
            canvas.DrawRect(line.StrokeWidth / 2, line.StrokeWidth / 2, width - line.StrokeWidth, height - line.StrokeWidth, line);
            canvas.DrawLine(0, 0, width, height, line);
            canvas.DrawLine(width, 0, 0, height, line);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(new MemoryStream(data.ToArray()));
    }

    private static async Task WaitDrawnAsync(Window host, Func<bool> condition)
    {
        await Program.WaitUntilAsync(() => { host.UpdateLayout(); return condition(); });
        await SmokeDispatcher.SettleAsync();
        host.UpdateLayout();
    }

    private static Rect RectIn(Control control, Visual root) =>
        new(control.TranslatePoint(default, root) ?? throw new InvalidOperationException("Detached control."), control.Bounds.Size);

    private static bool Inside(Rect outer, Rect inner) => inner.Left >= outer.Left - Tolerance && inner.Top >= outer.Top - Tolerance
        && inner.Right <= outer.Right + Tolerance && inner.Bottom <= outer.Bottom + Tolerance;

    // The play mark of a badge view, in the host's coordinates; null when the plan carries none.
    private static Rect? PlayMark(MeowChan.Desktop.Controls.MediaBadgeView badges, Visual host)
    {
        foreach (MeowChan.Desktop.Controls.PlacedBadge placed in badges.Plan.Badges)
            if (placed.Badge.Id == MeowChan.Desktop.Controls.MediaBadgeId.PlayMark)
                return new Rect(RectIn(badges, host).Position + placed.Bounds.Position, placed.Bounds.Size);
        return null;
    }

    private static bool Same(Rect a, Rect b) => Math.Abs(a.Left - b.Left) <= Tolerance && Math.Abs(a.Top - b.Top) <= Tolerance
        && Math.Abs(a.Width - b.Width) <= Tolerance && Math.Abs(a.Height - b.Height) <= Tolerance;

    // ---- the reader: attachment beside the post text ---------------------------------------------------------------------

    private static async Task CheckReaderAsync(ThemeVariant theme, string name, string directory)
    {
        (string File, int Width, int Height, SKColor Color, bool Favorite, bool Large)[] cases =
        [
            ("portrait-photo-2x3.jpg", 400, 600, SKColors.SteelBlue, true, false),
            ("a-very-long-original-file-name-of-a-portrait-photo-taken-in-the-garden.jpg", 300, 600, SKColors.SeaGreen, false, false),
            ("landscape-16x9.png", 640, 360, SKColors.IndianRed, false, false),
            ("clip.webm", 640, 360, SKColors.DarkOrchid, false, false),
            ("large-portrait.png", 400, 600, SKColors.Chocolate, false, true)
        ];
        StackPanel rows = new() { Spacing = 6, Margin = new Thickness(8) };
        List<(ArchiveAttachmentView View, TextBlock Body, ReaderPostLayout Layout, string File, bool Video, bool Large, int Width, int Height, bool Favorite)> built = new();
        foreach (var item in cases)
        {
            ArchiveAttachmentView view = new() { Margin = new Thickness(0, 2, 0, 8) };
            bool video = item.File.EndsWith(".webm", StringComparison.Ordinal);
            view.DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "m-" + item.File, OriginalFileName: item.File, ExpectedSize: 183_000))
            {
                LargePreview = item.Large,
                FavoriteAvailable = () => true, IsFavorite = () => item.Favorite, ToggleFavoriteAsync = () => Task.CompletedTask,
                LoadThumbnail = _ => Task.FromResult<Bitmap?>(Picture(item.Width, item.Height, item.Color))
            };
            StackPanel mediaHost = new() { HorizontalAlignment = HorizontalAlignment.Left, Children = { view } };
            TextBlock body = new() { TextWrapping = TextWrapping.Wrap, Text = "Post text starts right after the picture block, at the file line. " + new string('x', 5).Replace("x", "Lorem ipsum dolor sit amet. ") };
            ReaderPostLayout layout = new() { Children = { mediaHost, body } };
            if (item.Large) layout.SetValue(ReaderPostLayout.LargeReaderProperty, true);
            rows.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brushes.Gray, Child = layout });
            built.Add((view, body, layout, item.File, video, item.Large, item.Width, item.Height, item.Favorite));
        }
        Window host = new() { Width = 800, Height = 1150, RequestedThemeVariant = theme, Content = new ScrollViewer { Content = rows }, Title = "media-slot reader" };
        host.Show();
        try
        {
            await WaitDrawnAsync(host, () => built.All(entry => entry.View.Thumbnail.Source is Bitmap && entry.View.Thumbnail.Bounds.Width > 0));
            foreach (var entry in built)
            {
                ArchiveAttachmentView view = entry.View;
                Rect viewRect = RectIn(view, host), image = RectIn(view.Thumbnail, host), button = RectIn(view.PreviewButton, host);
                string label = (entry.Large ? "large " : "") + entry.File + " " + name;
                Rect bodyRect = RectIn(entry.Body, host);
                Console.WriteLine("SMOKE media-slot reader: " + JsonSerializer.Serialize(new
                {
                    label, view = viewRect.ToString(), image = image.ToString(), button = button.ToString(), body = bodyRect.ToString()
                }));
                // The hover/focus frame is the button: it must be the drawn image, not a box around slot and padding.
                Expect(Same(button, image), $"{label}: the clickable/focus frame {button} is not the drawn image {image}.");
                // The block is as wide as the image (a file line keeps a modest minimum); no grey slot to the right of it.
                double allowed = Math.Max(image.Width, 140);
                Expect(viewRect.Width <= allowed + Tolerance, $"{label}: the attachment block is {viewRect.Width:0} wide for a {image.Width:0} wide picture (allowed {allowed:0}).");
                Expect(bodyRect.Left - viewRect.Right <= 16, $"{label}: the text column starts {bodyRect.Left - viewRect.Right:0} after the block.");
                if (view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "ArchiveMediaFavorite") is { } star)
                {
                    Rect starRect = RectIn(star, host);
                    Expect(starRect.Right <= viewRect.Right + Tolerance, $"{label}: the star {starRect} is outside its block {viewRect}.");
                }
                // MB1b: the shared badge view lies exactly on the shown picture, never takes a click and is not in the accessibility tree.
                MeowChan.Desktop.Controls.MediaBadgeView badges = view.Badges;
                Expect(Same(RectIn(badges, host), image) || !badges.IsVisible, $"{label}: the badge view {RectIn(badges, host)} is not on the shown picture {image}.");
                Expect(!badges.IsHitTestVisible, $"{label}: the badge view takes clicks.");
                Expect(badges.Plan.Badges.All(placed => placed.Bounds.Left >= -Tolerance && placed.Bounds.Top >= -Tolerance
                    && placed.Bounds.Right <= image.Width + Tolerance && placed.Bounds.Bottom <= image.Height + Tolerance), $"{label}: a badge leaves the picture {image}.");
                // A favourite shows its star in the top left corner of the picture (the interactive toggle in the file line stays as it was).
                Expect(entry.Favorite == badges.Plan.Badges.Any(placed => placed.Badge.Id == MeowChan.Desktop.Controls.MediaBadgeId.Favorite), $"{label}: the favourite badge does not follow the row (favourite {entry.Favorite}).");
                Expect(entry.Video == badges.State.Kind.Equals(MeowChan.Desktop.Controls.MediaBadgeKind.Video), $"{label}: the badge kind {badges.State.Kind} does not follow the file ({entry.File}).");
                if (entry.Video)
                {
                    Rect playRect = PlayMark(badges, host) ?? throw new InvalidOperationException($"{label}: no play mark in the badge plan.");
                    Expect(Math.Abs(playRect.Center.X - image.Center.X) <= Tolerance && Math.Abs(playRect.Center.Y - image.Center.Y) <= Tolerance,
                        $"{label}: the play button {playRect.Center} is not centered on the image {image.Center}.");
                    // It scales with the picture (45 % of its shorter side, 20 to 44 dip) and never leaves it.
                    Expect(playRect.Left >= image.Left - Tolerance && playRect.Right <= image.Right + Tolerance && playRect.Top >= image.Top - Tolerance && playRect.Bottom <= image.Bottom + Tolerance,
                        $"{label}: the play button {playRect} leaves the image {image}.");
                    Expect(Math.Abs(playRect.Width - VideoPlayMark.Diameter(image.Size)) <= 1.5, $"{label}: the play button is {playRect.Width:0.#} wide, the rule gives {VideoPlayMark.Diameter(image.Size):0.#}.");
                }
            }
            Program.CaptureWindow(host, "media-slot-reader-" + name + ".png", directory);
        }
        finally { host.Content = null; host.Close(); await SmokeDispatcher.SettleAsync(); }
    }

    // ---- the thread list: compact rows in the 76 dip slot ------------------------------------------------------------------

    private static async Task CheckListRowsAsync(ThemeVariant theme, string name, string directory)
    {
        (string File, int Width, int Height, SKColor Color)[] cases =
        [
            ("17892507123456789.webm", 640, 360, SKColors.DarkOrchid),
            ("17892507123456790.webm", 360, 640, SKColors.Teal),
            ("landscape-16x9.png", 640, 360, SKColors.IndianRed),
            ("portrait-photo-2x3.jpg", 400, 600, SKColors.SteelBlue),
            ("thin-strip.webm", 30, 600, SKColors.SeaGreen)
        ];
        StackPanel rows = new() { Spacing = 8, Margin = new Thickness(8) };
        List<(ArchiveAttachmentView View, string File, bool Video, double Aspect)> built = new();
        foreach (var item in cases)
        {
            ArchiveAttachmentView view = new() { Width = 76 };
            bool video = item.File.EndsWith(".webm", StringComparison.Ordinal);
            view.DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "m-" + item.File, OriginalFileName: item.File, ExpectedSize: 183_000))
            {
                Compact = true,
                FavoriteAvailable = () => false, IsFavorite = () => false, ToggleFavoriteAsync = () => Task.CompletedTask,
                LoadThumbnail = _ => Task.FromResult<Bitmap?>(Picture(item.Width, item.Height, item.Color))
            };
            rows.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brushes.Gray, Child = view });
            built.Add((view, item.File, video, (double)item.Width / item.Height));
        }
        Window host = new() { Width = 300, Height = 700, RequestedThemeVariant = theme, Content = new ScrollViewer { Content = rows }, Title = "media-slot list" };
        host.Show();
        try
        {
            await WaitDrawnAsync(host, () => built.All(entry => entry.View.Thumbnail.Source is Bitmap && entry.View.Thumbnail.Bounds.Width > 0));
            foreach (var entry in built)
            {
                ArchiveAttachmentView view = entry.View;
                Rect viewRect = RectIn(view, host), image = RectIn(view.Thumbnail, host);
                string label = "list " + entry.File + " " + name;
                Console.WriteLine("SMOKE media-slot list: " + JsonSerializer.Serialize(new { label, view = viewRect.ToString(), image = image.ToString() }));
                // One uniform slot for portrait and landscape: the block is 76 wide, the picture centred in it.
                Expect(Math.Abs(viewRect.Width - 76) <= Tolerance, $"{label}: the slot is {viewRect.Width:0.#} wide, not 76.");
                Expect(Math.Abs(image.Center.X - viewRect.Center.X) <= Tolerance, $"{label}: the picture {image} is not centred in its slot {viewRect}.");
                // The file line is one trimmed line with the full text in its tooltip.
                TextBlock line = view.GetVisualDescendants().OfType<TextBlock>().Single(c => c.MaxLines == 1 && ToolTip.GetTip(c) is string tip && tip.StartsWith(entry.File, StringComparison.Ordinal));
                Expect(line.TextWrapping == TextWrapping.NoWrap && line.Bounds.Height < line.FontSize * 2, $"{label}: the file line is {line.Bounds.Height:0.#} high, more than one line.");
                // The file favourite is never wired in the list (the thread's star is on the right): hidden, so the line has the whole slot.
                Expect(view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ArchiveMediaFavorite").IsVisible == false, $"{label}: the dead file star is still shown in the list row.");
                Expect(!view.Badges.State.Favorite, $"{label}: the list row carries a favourite badge for a file favourite that is never wired there.");
                Expect(RectIn(line, host).Right <= viewRect.Right + Tolerance, $"{label}: the file line {RectIn(line, host)} leaves the slot {viewRect}.");
                if (entry.Video)
                {
                    // The shown picture is the letterboxed rectangle in the frame (a very thin strip is a wider frame with the picture centred in it).
                    Rect letterbox = MediaSlotGeometry.Letterbox(image.Size, entry.Aspect), shown = new(image.Position + letterbox.Position, letterbox.Size);
                    MeowChan.Desktop.Controls.MediaBadgeView badges = view.Badges;
                    Expect(!badges.IsHitTestVisible, $"{label}: the badge view takes clicks, so the whole picture is no longer the click target.");
                    // A picture shorter than the hide limit (a thin strip) carries no badge at all (MB1a); every other picture carries the mark as before.
                    if (Math.Min(shown.Width, shown.Height) < MeowChan.Desktop.Controls.MediaBadgeLimits.Current.HideBelow)
                    {
                        Expect(PlayMark(badges, host) == null, $"{label}: a picture of {shown.Width:0.#} x {shown.Height:0.#} still carries a play mark below the hide limit.");
                        continue;
                    }
                    Expect(Same(RectIn(badges, host), shown), $"{label}: the badge view {RectIn(badges, host)} is not on the shown picture {shown}.");
                    Rect playRect = PlayMark(badges, host) ?? throw new InvalidOperationException($"{label}: no play mark in the badge plan.");
                    Expect(Inside(shown, playRect), $"{label}: the play button {playRect} leaves the picture {shown}.");
                    Expect(Math.Abs(playRect.Center.X - shown.Center.X) <= Tolerance && Math.Abs(playRect.Center.Y - shown.Center.Y) <= Tolerance,
                        $"{label}: the play button {playRect.Center} is not centred on the picture {shown.Center}.");
                    Expect(Math.Abs(playRect.Width - VideoPlayMark.Diameter(shown.Size)) <= Tolerance, $"{label}: the play button is {playRect.Width:0.#} wide, the rule gives {VideoPlayMark.Diameter(shown.Size):0.#}.");
                }
            }
            Program.CaptureWindow(host, "media-slot-list-" + name + ".png", directory);
        }
        finally { host.Content = null; host.Close(); await SmokeDispatcher.SettleAsync(); }
    }

    // ---- the tiles: a uniform cell, the image letterboxed inside it --------------------------------------------------------

    private static MediaLibraryEntry LibraryEntry(string thread, char hash, string file, SavedMediaKind kind)
    {
        DateTimeOffset day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        return new(new ResourceId("s", "b", thread), "Thread " + thread, 1, day, day, "1", 1, null, 0, "att-v1:" + new string(hash, 64),
            "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m" + thread, "original", file, kind,
            MediaLibraryPresence.Present, false, false);
    }

    private static ArchiveAssetRow AssetRow(string file, char hash)
    {
        MediaAssetReference asset = new("asset-" + file, "media-" + file, "original", file, "/synthetic-never-opened/" + file, 183_000,
            new string(hash, 64), "https://example.invalid/" + file, file, true);
        MediaAssetSnapshot snapshot = new("/synthetic-never-opened", MediaManifestStatus.Available, 1, "generation", [asset], "synthetic");
        return new ArchiveAssetRow(snapshot, asset) { FavoriteAvailable = () => true, IsFavorite = () => true, ToggleFavoriteAsync = () => Task.CompletedTask };
    }

    private static async Task CheckTilesAsync(ThemeVariant theme, string name, string directory)
    {
        (string File, int Width, int Height, SKColor Color, SavedMediaKind Kind)[] pictures =
        [
            ("portrait.jpg", 400, 600, SKColors.SteelBlue, SavedMediaKind.Image),
            ("landscape.png", 640, 360, SKColors.IndianRed, SavedMediaKind.Image),
            ("clip.webm", 640, 360, SKColors.DarkOrchid, SavedMediaKind.Video),
            ("wide.gif", 800, 200, SKColors.SeaGreen, SavedMediaKind.Image)
        ];
        WrapPanel archive = new(), library = new();
        List<ArchiveMediaTileView> archiveTiles = new();
        List<MediaLibraryTileView> libraryTiles = new();
        for (int index = 0; index < pictures.Length; index++)
        {
            var picture = pictures[index];
            char hash = (char)('a' + index);
            ArchiveMediaTileView archiveTile = new();
            archiveTile.ThumbnailDecoder = (_, _) => Task.FromResult<Bitmap?>(Picture(picture.Width, picture.Height, picture.Color));
            archiveTile.DataContext = AssetRow(picture.File, hash);
            archive.Children.Add(archiveTile); archiveTiles.Add(archiveTile);

            MediaLibraryEntry entry = LibraryEntry((index + 1).ToString(), hash, picture.File, picture.Kind);
            MediaLibraryTileView libraryTile = new()
            {
                ThumbnailLoader = (_, _) => Task.FromResult<Bitmap?>(Picture(picture.Width, picture.Height, picture.Color)),
                FavoriteToggle = _ => Task.CompletedTask, CanToggleFavorite = () => true
            };
            libraryTile.DataContext = new MediaLibraryTileRow(new MediaLibraryTile(entry, [entry]), index, _ =>
                new MediaLibraryEntryState(MediaLibraryFlag.Yes, true, 4, true, Array.Empty<string>(), Array.Empty<string>(), null));
            library.Children.Add(libraryTile); libraryTiles.Add(libraryTile);
        }
        StackPanel content = new() { Margin = new Thickness(8), Spacing = 8, Children = { archive, library } };
        Window host = new() { Width = 860, Height = 560, RequestedThemeVariant = theme, Content = content, Title = "media-slot tiles" };
        host.Show();
        try
        {
            await WaitDrawnAsync(host, () => archiveTiles.All(tile => tile.HasThumbnail) && libraryTiles.All(tile => tile.HasThumbnail));
            foreach (ArchiveMediaTileView tile in archiveTiles)
            {
                string file = (tile.DataContext as ArchiveAssetRow)!.Name, label = "archive tile " + file + " " + name;
                Image image = tile.GetVisualDescendants().OfType<Image>().First();
                Rect picture = RectIn(image, host), cell = RectIn(image.GetVisualParent<Control>()!, host);
                double aspect = image.Source!.Size.Width / image.Source.Size.Height;
                // Where the picture is really drawn inside the (uniform) image control.
                Rect drawn = Marks(Drawn(picture, aspect));
                Console.WriteLine("SMOKE media-slot archive-tile: " + JsonSerializer.Serialize(new { label, cell = cell.ToString(), control = picture.ToString(), drawn = drawn.ToString() }));
                foreach (string overlayName in new[] { "ArchiveMediaFavorite" })
                    if (tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == overlayName && c.IsVisible && c.Bounds.Width > 0) is { } overlay)
                        Expect(Inside(drawn, RectIn(overlay, host)), $"{label}: {overlayName} {RectIn(overlay, host)} is not on the drawn image {drawn}.");
                // MB1b: one badge view lies exactly on the drawn picture (not on the marks band) and every badge is inside it.
                MeowChan.Desktop.Controls.MediaBadgeView badges = tile.Badges;
                Rect picture2 = Drawn(picture, aspect), badgeRect = RectIn(badges, host);
                Expect(Same(badgeRect, picture2), $"{label}: the badge view {badgeRect} is not on the drawn image {picture2}.");
                Expect(!badges.IsHitTestVisible, $"{label}: the badge view takes clicks.");
                Expect(badges.State.Favorite, $"{label}: the favourite row shows no favourite badge.");
                Expect((file == "clip.webm") == (badges.State.Kind == MeowChan.Desktop.Controls.MediaBadgeKind.Video) && (file == "wide.gif") == (badges.State.Kind == MeowChan.Desktop.Controls.MediaBadgeKind.Gif),
                    $"{label}: the badge kind {badges.State.Kind} does not follow the file name.");
                Expect(AutomationProperties.GetName(tile)!.Contains(tile.BadgeDescription, StringComparison.Ordinal) && tile.BadgeDescription.Length > 0, $"{label}: the tile's name lacks the badge sentence.");
                foreach (MeowChan.Desktop.Controls.PlacedBadge placed in badges.Plan.Badges)
                    Expect(Inside(picture2, new Rect(badgeRect.Position + placed.Bounds.Position, placed.Bounds.Size)), $"{label}: badge {placed.Badge.Id} {placed.Bounds} leaves the drawn image.");
                Console.WriteLine("SMOKE media-slot archive-badges: " + JsonSerializer.Serialize(new { label, mode = badges.Plan.Mode.ToString(), ids = badges.Plan.Badges.Select(placed => placed.Badge.Id.ToString()).ToArray(), text = tile.BadgeDescription }));
            }
            // MB1b: the badges of a media tile sit on the corners of the tile's slot, so every tile of the row carries the same badge at the same place
            // (a portrait, a landscape, a wide and a video tile alike); the star and the check share those corners; only the play mark follows the picture.
            Dictionary<MediaBadgeId, (Point Place, string Label)> firstPlace = new();
            foreach (MediaLibraryTileView tile in libraryTiles)
            {
                string file = (tile.DataContext as MediaLibraryTileRow)!.Entry.Name ?? "?", label = "media tile #" + ((tile.DataContext as MediaLibraryTileRow)!.Index + 1) + " " + name;
                Image image = tile.GetVisualDescendants().OfType<Image>().First();
                Rect picture = RectIn(image, host), slot = RectIn(image.GetVisualParent<Control>()!, host);
                Rect drawn = Drawn(picture, image.Source!.Size.Width / image.Source.Size.Height);
                Console.WriteLine("SMOKE media-slot media-tile: " + JsonSerializer.Serialize(new { label, control = picture.ToString(), drawn = drawn.ToString(), slot = slot.ToString() }));
                Rect badgeView = RectIn(tile.Badges, host);
                Expect(tile.Badges.Options.Anchor == MediaBadgeAnchor.Slot, $"{label}: the media page's badges must be anchored to the slot.");
                Expect(!tile.Badges.IsVisible || Math.Abs(badgeView.X - slot.X) < 1.5 && Math.Abs(badgeView.Y - slot.Y) < 1.5
                    && Math.Abs(badgeView.Width - slot.Width) < 1.5 && Math.Abs(badgeView.Height - slot.Height) < 1.5,
                    $"{label}: the badge view {badgeView} is not on the tile's slot {slot}.");
                foreach (PlacedBadge placed in tile.Badges.Plan.Badges)
                {
                    Expect(Inside(new Rect(0, 0, badgeView.Width, badgeView.Height), placed.Bounds), $"{label}: badge {placed.Badge.Id} {placed.Bounds} is outside the slot {badgeView}.");
                    if (placed.Badge.Id == MediaBadgeId.PlayMark)
                    {
                        Point centre = placed.Bounds.Center + badgeView.TopLeft;
                        Expect(Math.Abs(centre.X - drawn.Center.X) < 1.5 && Math.Abs(centre.Y - drawn.Center.Y) < 1.5, $"{label}: the play mark centre {centre} is not the drawn picture's centre {drawn.Center}.");
                    }
                    else if (firstPlace.TryGetValue(placed.Badge.Id, out var first))
                        Expect(Math.Abs(first.Place.X - placed.Bounds.X) < 0.01 && Math.Abs(first.Place.Y - placed.Bounds.Y) < 0.01,
                            $"{label}: badge {placed.Badge.Id} sits at {placed.Bounds.TopLeft} but at {first.Place} on {first.Label}: a row's badges must share one height.");
                    else firstPlace[placed.Badge.Id] = (placed.Bounds.TopLeft, label);
                }
                if (tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "MediaTileFavoriteToggle" && c.IsVisible && c.Bounds.Width > 0) is { } star)
                    Expect(Inside(slot, RectIn(star, host)), $"{label}: MediaTileFavoriteToggle {RectIn(star, host)} is not in the tile's slot {slot}.");
                if (tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "MediaTileSelected" && c.IsVisible && c.Bounds.Width > 0) is { } check)
                    Expect(Inside(slot, RectIn(check, host)), $"{label}: MediaTileSelected {RectIn(check, host)} is not in the tile's slot {slot}.");
            }
            foreach (ArchiveMediaTileView tile in archiveTiles)
            {
                // The interactive toggle (bottom right) and the badges of the picture's bottom left corner (type) never overlap.
                Control? star = tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "ArchiveMediaFavorite" && c.IsVisible && c.Bounds.Width > 0);
                Rect badgeRect = RectIn(tile.Badges, host);
                if (star != null)
                    foreach (MeowChan.Desktop.Controls.PlacedBadge placed in tile.Badges.Plan.Badges.Where(placed => placed.Badge.Corner == MeowChan.Desktop.Controls.MediaBadgeCorner.BottomLeft))
                        Expect(!new Rect(badgeRect.Position + placed.Bounds.Position, placed.Bounds.Size).Intersects(RectIn(star, host)), $"archive tile {(tile.DataContext as ArchiveAssetRow)!.Name} {name}: type badge and star overlap.");
            }
            foreach (MediaLibraryTileView tile in libraryTiles)
            {
                // The star takes the favourite badge's place (top left) and never covers a badge of another corner.
                Control? star = tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "MediaTileFavoriteToggle" && c.IsVisible && c.Bounds.Width > 0);
                if (star == null) continue;
                Rect badgeView = RectIn(tile.Badges, host), starRect = RectIn(star, host);
                foreach (PlacedBadge placed in tile.Badges.Plan.Badges)
                    if (placed.Badge.Corner != MediaBadgeCorner.TopLeft && placed.Badge.Corner != MediaBadgeCorner.Center)
                        Expect(!starRect.Intersects(placed.Bounds.Translate(new Vector(badgeView.X, badgeView.Y))), $"media tile #{(tile.DataContext as MediaLibraryTileRow)!.Index + 1} {name}: badge {placed.Badge.Id} and star overlap.");
                if (tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "MediaTileSelected" && c.IsVisible && c.Bounds.Width > 0) is { } selected)
                    foreach (PlacedBadge placed in tile.Badges.Plan.Badges)
                        if (placed.Badge.Id != MediaBadgeId.PlayMark)
                            Expect(!RectIn(selected, host).Intersects(placed.Bounds.Translate(new Vector(badgeView.X, badgeView.Y))), $"media tile #{(tile.DataContext as MediaLibraryTileRow)!.Index + 1} {name}: badge {placed.Badge.Id} and the selection check overlap.");
            }
            Program.CaptureWindow(host, "media-slot-tiles-" + name + ".png", directory);
        }
        finally { host.Content = null; host.Close(); await SmokeDispatcher.SettleAsync(); }
    }

    // Marks sit on the picture's corners; a picture lower than the minimum band gets that band, centred, so badge and star never stack.
    private static Rect Marks(Rect picture)
    {
        double width = Math.Max(picture.Width, MediaSlotGeometry.MinimumMarkExtent), height = Math.Max(picture.Height, MediaSlotGeometry.MinimumMarkExtent);
        return new Rect(picture.Center.X - width / 2, picture.Center.Y - height / 2, width, height);
    }

    // The rectangle a Stretch.Uniform picture of this aspect covers inside its control (centred).
    private static Rect Drawn(Rect control, double aspect)
    {
        double width = control.Width, height = width / aspect;
        if (height > control.Height) { height = control.Height; width = height * aspect; }
        return new Rect(control.X + (control.Width - width) / 2, control.Y + (control.Height - height) / 2, width, height);
    }
}
