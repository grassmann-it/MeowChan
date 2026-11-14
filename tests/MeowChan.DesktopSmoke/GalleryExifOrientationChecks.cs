using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Gallery;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Real host/catalog, verified local JPEGs and production loaders: no decoder/thumbnail delegates replaced.
internal static class GalleryExifOrientationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        window.Width = 1400; window.Height = 900;
        Click(window.FindControl<Button>("MediaNavigationButton")!);
        ContentControl mediaHost = window.FindControl<ContentControl>("MediaPageHost")!;
        await Program.WaitUntilAsync(() => mediaHost.IsVisible && mediaHost.Content is MediaPage);
        MediaPage media = (MediaPage)mediaHost.Content!;
        await media.BuildTask.WaitAsync(TimeSpan.FromSeconds(15));
        Require(media.Tiles.Count == 2, "Expected exactly two synthetic EXIF media tiles.");
        foreach (GalleryEntry entry in ExifOrientationFixture.Entries)
        {
            string file = entry.Asset.OriginalFileName!;
            await Program.WaitUntilAsync(() => media.GetVisualDescendants().OfType<MediaLibraryTileView>()
                .Any(tile => tile.DataContext is MediaLibraryTileRow row && row.Entry.Name == file && tile.HasThumbnail));
            MediaLibraryTileView tile = media.GetVisualDescendants().OfType<MediaLibraryTileView>()
                .Single(tile => tile.DataContext is MediaLibraryTileRow row && row.Entry.Name == file);
            Check(tile.GetVisualDescendants().OfType<Image>().Single(), file, "media tile");
        }
        Program.CaptureWindow(window, "smoke-gallery-exif-tiles.png");

        await window.OpenMediaGalleryLaidOutAsync(ExifOrientationFixture.Entries, ExifOrientationFixture.Entries[0].EntryKey);
        ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
        await Program.WaitUntilAsync(() => galleryHost.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
        try
        {
            viewer.ConfigureFilmstrip(true, 96, 2);
            await Program.WaitUntilAsync(() => viewer.IsFilmstripVisible && viewer.Filmstrip.Count == 2
                && viewer.Filmstrip.IsTileLoaded(0) && viewer.Filmstrip.IsTileLoaded(1),
                () => $"visible={viewer.IsFilmstripVisible}; count={viewer.Filmstrip.Count}; loaded0={viewer.Filmstrip.IsTileLoaded(0)}; loaded1={viewer.Filmstrip.IsTileLoaded(1)}");
            for (int i = 0; i < 2; i++)
                Check(viewer.Filmstrip.TileControl(i)!.GetVisualDescendants().OfType<Image>().Single(),
                    ExifOrientationFixture.Entries[i].Asset.OriginalFileName!, "filmstrip");
            Image image = viewer.GetVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
            for (int i = 0; i < 2; i++)
            {
                string file = ExifOrientationFixture.Entries[i].Asset.OriginalFileName!;
                Click(Find<Button>(viewer, "GalleryFit"));
                await Program.WaitUntilAsync(() => image.Source is Bitmap && !viewer.CaptureState().ManualZoom);
                await SmokeDispatcher.SettleAsync();
                Check(image, file, "gallery Fit");
                Require(((Bitmap)image.Source!).PixelSize.Height < ExifOrientationFixture.StoredWidth,
                    file + ": Fit did not exercise a reduced decode.");
                Program.CaptureWindow(window, $"smoke-gallery-exif-{Path.GetFileNameWithoutExtension(file)}-fit.png");
                Click(Find<Button>(viewer, "GalleryActual"));
                await Program.WaitUntilAsync(() => image.Source is Bitmap bitmap
                    && bitmap.PixelSize == new PixelSize(ExifOrientationFixture.StoredHeight, ExifOrientationFixture.StoredWidth)
                    && viewer.CaptureState().ManualZoom);
                await SmokeDispatcher.SettleAsync();
                Check(image, file, "gallery 100%");
                Require(Math.Abs(image.Bounds.Width * window.RenderScaling - ExifOrientationFixture.StoredHeight) < 1
                    && Math.Abs(image.Bounds.Height * window.RenderScaling - ExifOrientationFixture.StoredWidth) < 1,
                    file + ": 100% geometry is not one physical pixel per shown pixel.");
                Program.CaptureWindow(window, $"smoke-gallery-exif-{Path.GetFileNameWithoutExtension(file)}-actual.png");
                if (i == 0)
                {
                    object previous = image.Source!;
                    Click(Find<Button>(viewer, "GalleryNext"));
                    await Program.WaitUntilAsync(() => image.Source is Bitmap && !ReferenceEquals(previous, image.Source)
                        && viewer.CaptureState().Key == ExifOrientationFixture.Entries[1].Key);
                }
            }
        }
        finally
        {
            Click(Find<Button>(viewer, "GalleryClose"));
            await window.GalleryCleanupTask.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage archive = window.ArchivePage;
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Archive && !archive.IsBusy);
        await archive.LoadThreadAsync(ExifOrientationFixture.Resource).WaitAsync(TimeSpan.FromSeconds(15));
        await archive.MediaLoadTask.WaitAsync(TimeSpan.FromSeconds(15));
        ListBox posts = archive.FindControl<ListBox>("ArchivePosts")!;
        foreach (GalleryEntry entry in ExifOrientationFixture.Entries)
        {
            string file = entry.Asset.OriginalFileName!;
            await Program.WaitUntilAsync(() => posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .Any(view => view.DataContext is ArchiveAttachmentRow row && row.Attachment.OriginalFileName == file
                    && view.Thumbnail.Source is Bitmap && view.Thumbnail.IsEffectivelyVisible));
            ArchiveAttachmentView view = posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .Single(view => view.DataContext is ArchiveAttachmentRow row && row.Attachment.OriginalFileName == file);
            Check(view.Thumbnail, file, "reader attachment");
        }
        Program.CaptureWindow(window, "smoke-gallery-exif-reader.png");
        Console.WriteLine("SMOKE EXIF: orientations 6/8; source and rendered quadrant order BRYG/GYRB; real media tiles, filmstrip, reduced gallery Fit, full 100% with physical-pixel geometry, reader attachments; no replacement loaders.");
    }

    private static void Check(Image image, string file, string surface)
    {
        TopLevel.GetTopLevel(image)?.UpdateLayout();
        Require(image.IsEffectivelyVisible && image.Bounds.Width > 8 && image.Bounds.Height > 8, surface + ": image is not laid out/visible.");
        Require(image.Source is Bitmap, surface + ": no decoded bitmap.");
        Bitmap bitmap = (Bitmap)image.Source!;
        Require(Math.Abs((double)bitmap.PixelSize.Width / bitmap.PixelSize.Height - 0.5) < 0.02, surface + ": source is not portrait.");
        string expected = file == "orientation-6.jpg" ? "BRYG" : file == "orientation-8.jpg" ? "GYRB"
            : throw new InvalidOperationException("Unexpected EXIF file " + file);
        using MemoryStream png = new();
        bitmap.Save(png, PngBitmapEncoderOptions.Default); png.Position = 0;
        using SKBitmap source = SKBitmap.Decode(png);
        Require(Letters(source) == expected, $"{surface}/{file}: source quadrants {Letters(source)}, expected {expected}.");
        // Assert the actual Image control too (stretch, control rotation/mirroring), including the cropped filmstrip.
        using RenderTargetBitmap rendered = new(new PixelSize((int)Math.Ceiling(image.Bounds.Width), (int)Math.Ceiling(image.Bounds.Height)), new Vector(96, 96));
        rendered.Render(image);
        using MemoryStream drawn = new(); rendered.Save(drawn, PngBitmapEncoderOptions.Default); drawn.Position = 0;
        using SKBitmap pixels = SKBitmap.Decode(drawn);
        Rect visible = new(image.Bounds.Size);
        if (image.Stretch is Stretch.Uniform or Stretch.UniformToFill)
        {
            double scale = image.Stretch == Stretch.Uniform
                ? Math.Min(image.Bounds.Width / bitmap.Size.Width, image.Bounds.Height / bitmap.Size.Height)
                : Math.Max(image.Bounds.Width / bitmap.Size.Width, image.Bounds.Height / bitmap.Size.Height);
            Size drawnSize = new(bitmap.Size.Width * scale, bitmap.Size.Height * scale);
            visible = visible.Intersect(new Rect((image.Bounds.Width - drawnSize.Width) / 2,
                (image.Bounds.Height - drawnSize.Height) / 2, drawnSize.Width, drawnSize.Height));
        }
        string renderedLetters = Letters(pixels, visible);
        Require(renderedLetters == expected, $"{surface}/{file}: rendered quadrants {renderedLetters}, expected {expected}.");
        Console.WriteLine($"SMOKE EXIF {surface}/{file}: {bitmap.PixelSize}, rendered {pixels.Width}x{pixels.Height}, {expected}.");
    }

    private static string Letters(SKBitmap bitmap) => Letters(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height));
    private static string Letters(SKBitmap bitmap, Rect area) => string.Concat(new[] { (1, 1), (3, 1), (1, 3), (3, 3) }
        .Select(point => Letter(bitmap.GetPixel((int)(area.X + point.Item1 * area.Width / 4), (int)(area.Y + point.Item2 * area.Height / 4)))));
    private static char Letter(SKColor c) => c.Red > 160 && c.Green < 96 && c.Blue < 96 ? 'R'
        : c.Green > 160 && c.Red < 96 && c.Blue < 96 ? 'G' : c.Blue > 160 && c.Red < 96 && c.Green < 96 ? 'B'
        : c.Red > 160 && c.Green > 160 && c.Blue < 96 ? 'Y' : '?';
    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
