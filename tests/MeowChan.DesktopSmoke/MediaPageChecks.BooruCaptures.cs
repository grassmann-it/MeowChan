using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaPageChecks
{
    // A small standalone registry run, also using exactly the capture assertions called by media-page's full journey.
    internal static async Task RunBooruCapturesAsync(MainWindow owner)
    {
        DesktopSessionHost host = owner.SessionHost;
        string language = Texts.Language;
        Texts.SetLanguage("en");
        foreach (string name in new[] { "synthetic", "orientation", "portrait" })
        {
            var tag = await host.DefinePrivateTagAsync(name).WaitAsync(Deadline);
            await host.SetThreadTagAsync(tag.Tag, ExifOrientationFixture.Resource, true).WaitAsync(Deadline);
        }
        MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host) };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media — Booru mode", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == 2, "Booru capture requires exactly two real synthetic tiles.");
            await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Count(tile => tile.HasThumbnail) == 2);
            page.BooruModeButton.IsChecked = true;
            await UntilAsync(() => page.IsBooruMode && page.TagCountTask.IsCompleted && page.TagCounts?.Media == 2);
            Require(page.TagSidebar.Items.Count(row => row.Kind == MediaTagListKind.Tag && row.Count == 2) == 3,
                "Booru capture requires all three synthetic tags and exact counts.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                await CaptureBooruModesAsync(window, page, theme);
            Console.WriteLine("SMOKE media Booru captures: 1400x900 and 760x900 DIP, Light/Dark, real verified local thumbnails, three tag rows each counting both tiles; sidebar left of nonempty grid, all within client.");
        }
        finally { page.EndLifetime(); window.Close(); Texts.SetLanguage(language); }
    }

    private static async Task CaptureBooruModesAsync(Window window, MediaPage page, ThemeVariant theme)
    {
        window.RequestedThemeVariant = theme;
        Require(page.IsBooruMode, "Booru capture must not silently capture the normal media mode.");
        foreach (int width in new[] { 1400, 760 })
        {
            window.Width = width; window.Height = 900;
            await UntilAsync(() => Math.Abs(window.ClientSize.Width - width) < 1 && window.ActualThemeVariant == theme
                && page.TagSidebar.IsEffectivelyVisible && page.TagCountTask.IsCompleted
                && page.TagCounts?.Media == page.Tiles.Count && page.Tiles.Count > 0
                && page.GetVisualDescendants().OfType<MediaLibraryTileView>().Any(tile => tile.HasThumbnail));
            await SmokeDispatcher.SettleAsync();
            window.UpdateLayout();
            MediaTagSidebar sidebar = page.TagSidebar;
            Control surface = C<Control>(page, "MediaGridSurface");
            Rect list = new(sidebar.TranslatePoint(default, window)!.Value, sidebar.Bounds.Size);
            Rect grid = new(surface.TranslatePoint(default, window)!.Value, surface.Bounds.Size);
            Require(Math.Abs(sidebar.Bounds.Width - MediaTagSidebar.ColumnWidth) < 1 && list.Height > 100
                && grid.Width > 100 && grid.Height > 100 && list.Right <= grid.Left + 1
                && list.Left >= 0 && grid.Right <= window.ClientSize.Width + 1
                && grid.Bottom <= window.ClientSize.Height + 1 && list.Bottom <= window.ClientSize.Height + 1,
                $"Booru {theme}/{width}: sidebar or grid clipped/overlapping: {list} / {grid}; client {window.ClientSize}.");
            Program.CaptureWindow(window, $"smoke-media-page-booru-{width}-{theme.ToString().ToLowerInvariant()}.png");
        }
        window.Width = 1400;
        await UntilAsync(() => Math.Abs(window.ClientSize.Width - 1400) < 1);
    }
}
