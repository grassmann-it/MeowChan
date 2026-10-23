using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static class MediaTileIntegrationChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-tile-integration-" + Guid.NewGuid().ToString("N"));
        ResourceId id = new("offline", "media", "tiles");
        Directory.CreateDirectory(profile);
        ArchivePage? page = null;
        Window? fixture = null;
        bool holdMediaScan = false;
        TaskCompletionSource scanStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using SKBitmap bitmap = new(80, 160);
            bitmap.Erase(SKColors.CornflowerBlue);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = png.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                foreach (string folder in new[] { "a", "b" })
                {
                    string media = Path.Combine(profile, folder);
                    Directory.CreateDirectory(media);
                    await File.WriteAllBytesAsync(Path.Combine(media, "portrait.png"), bytes);
                    var entries = Enumerable.Range(0, 600).ToDictionary(index => $"asset-{index:D4}.png", index => (object)new
                    {
                        MediaIdentifier = "duplicate-" + index,
                        SourceUrl = "https://offline.invalid/" + index,
                        FileName = "portrait.png", OriginalFileName = $"Image {index:D4} with a complete long visible title.png",
                        Length = bytes.Length, Sha256 = hash
                    });
                    await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/tiles", "Tile integration", ThreadSourceState.Archived,
                        DateTimeOffset.UtcNow, media, Array.Empty<CapturedPost>()));
                    // Historical catalog paths can spell the same manifest directory differently.
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/tiles", "Tile integration", ThreadSourceState.Archived,
                        DateTimeOffset.UtcNow, media + Path.DirectorySeparatorChar + ".", Array.Empty<CapturedPost>()));
                }
            }
            page = new ArchivePage(ct => ArchiveReader.OpenAsync(profile, ct), (_, _) => Task.CompletedTask, _ => { }, async (directory, token) =>
            {
                if (Volatile.Read(ref holdMediaScan))
                {
                    scanStarted.TrySetResult();
                    // Emulate a disconnected filesystem operation that does not observe cancellation.
                    await releaseScan.Task.ConfigureAwait(false);
                    return await MediaAssetReader.OpenAsync(directory, CancellationToken.None).ConfigureAwait(false);
                }
                return await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
            }) { IsAvailable = true };
            fixture = new Window { Width = 1200, Height = 850, Content = page, Title = "Archive tile integration fixture" };
            fixture.Show(owner);
            await page.ActivateAsync();
            await page.LoadThreadAsync(id);
            await page.MediaLoadTask;
            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            await Settle(fixture);
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            DataGrid grid = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
            Require(tiles.ItemCount == 1200, "Saved-media integration truncated the manifest sequence or duplicated a canonical directory alias.");
            MediaTilePanel panel = Panel(tiles);
            ArchiveAssetRow[] original = Rows(tiles);
            await WaitForVisibleThumbnailsAsync(tiles);
            await CheckDelayedThumbnailRecyclingAsync(fixture, tiles);
            ArchiveAssetRow chosen = original[722];
            tiles.SelectedItem = chosen;
            panel.RestoreAnchor(720, -17);
            await Settle(fixture);
            var anchor = panel.CaptureAnchor()!.Value;
            string anchored = Key(original[anchor.Index]);
            Render(page); // Queues restoration of the old deep anchor.
            tiles.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.LeftShift });
            panel.RestoreAnchor(0, 0);
            await Settle(fixture);
            Require(panel.CaptureAnchor()!.Value.Index == 0, "A deferred anchor restore overrode newer user input.");
            panel.RestoreAnchor(720, 0);
            Render(page);
            tiles.SelectedItem = Rows(tiles)[100];
            panel.RestoreAnchor(0, 0);
            await Settle(fixture);
            Require(panel.CaptureAnchor()!.Value.Index == 0 && ReferenceEquals(tiles.SelectedItem, original[100]),
                "A deferred anchor restore overrode newer media selection.");
            tiles.SelectedItem = chosen;
            panel.RestoreAnchor(720, -17);
            await Settle(fixture);
            object source = tiles.ItemsSource!;
            fixture.Width = 1040;
            await Program.WaitUntilAsync(() => Math.Abs(fixture.ClientSize.Width - 1040) < 1);
            await Settle(fixture);
            await Program.WaitUntilAsync(() =>
            {
                fixture.UpdateLayout();
                return panel.CaptureAnchor() is { } current && Key(Rows(tiles)[current.Index]) == anchored
                    && Math.Abs(current.Offset + 17) < 1;
            });
            Require(ReferenceEquals(source, tiles.ItemsSource), "Resizing rebuilt or sorted the media ItemsSource.");
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == anchored, "Resize changed the first visible stable media identity.");
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Resize lost media selection.");
            fixture.Width = 1200;
            await Program.WaitUntilAsync(() => Math.Abs(fixture.ClientSize.Width - 1200) < 1);
            await Settle(fixture);
            await Program.WaitUntilAsync(() =>
            {
                fixture.UpdateLayout();
                return panel.CaptureAnchor() is { } current && Key(Rows(tiles)[current.Index]) == anchored
                    && Math.Abs(current.Offset + 17) < 1;
            });
            panel.RestoreAnchor(720, -17);
            await Settle(fixture);
            anchored = Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]);
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
            await Settle(fixture);
            Require(Key((ArchiveAssetRow)grid.SelectedItem!) == Key(chosen), "Tile-to-list toggle lost exact selected asset.");
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
            await Settle(fixture);
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "List-to-tile toggle lost exact selected asset.");
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == anchored, "List/tile roundtrip lost first visible identity.");
            Require(Math.Abs(panel.CaptureAnchor()!.Value.Offset + 17) <= 1 / fixture.RenderScaling,
                "List/tile roundtrip lost the fractional row offset.");
            Action galleryReturn = page.CaptureGalleryOrigin();
            panel.RestoreAnchor(0, 0);
            galleryReturn();
            await Settle(fixture);
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == anchored, "Gallery return lost media scroll identity.");
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Gallery return lost selection.");

            await page.LoadThreadAsync(id, resetPage: false);
            await page.MediaLoadTask;
            await Settle(fixture);
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Manifest reload lost exact selection across an empty loading projection.");
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == anchored, "Manifest reload lost the stable media anchor.");

            // Model refresh publishes new row instances, including historical flags.
            ArchiveAssetRow[] rebound = original.Select((row, index) => new ArchiveAssetRow(row.Manifest, row.Asset with { IsCurrent = index % 2 == 0 })).ToArray();
            SetAssets(page, rebound);
            Render(page);
            await Settle(fixture);
            Require(!ReferenceEquals(tiles.SelectedItem, chosen) && Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Refresh failed to rebind exact selected identity.");
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == anchored, "Row refresh lost the media anchor.");
            page.FindControl<ComboBox>("ArchiveMediaSort")!.SelectedIndex = 1;
            await Settle(fixture);
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Sort lost exact selected identity.");
            page.FindControl<ComboBox>("ArchiveVersionScope")!.SelectedIndex = 0;
            await Settle(fixture);
            Require(Rows(tiles).All(row => row.Asset.IsCurrent) && tiles.ItemCount == 600, "Latest saved version filter disagrees with the model.");
            Require(Key((ArchiveAssetRow)tiles.SelectedItem!) == Key(chosen), "Version scope lost a retained selected identity.");
            page.FindControl<ComboBox>("ArchiveVersionScope")!.SelectedIndex = 1;
            await Settle(fixture);
            panel.RestoreAnchor(720, 0);
            await Settle(fixture);
            ArchiveAssetRow[] before = Rows(tiles);
            int first = panel.CaptureAnchor()!.Value.Index;
            string next = Key(before[first + panel.Columns]);
            var favorites = before.Skip(first + panel.Columns).Select(Key).ToHashSet();
            page.IsArchiveFavorite = entry => favorites.Contains(entry.Manifest.Directory + "|" + entry.Asset.AssetId);
            Click(page.FindControl<Button>("ArchiveFavoritesFilter")!);
            await Settle(fixture);
            Require(Key(Rows(tiles)[panel.CaptureAnchor()!.Value.Index]) == next, "Removed favorite anchor did not choose the next remaining old entry.");
            favorites.Clear(); Render(page);
            await Settle(fixture);
            Require(tiles.ItemCount == 0 && tiles.SelectedItem == null, "Empty favorites did not clear the sequence and selection.");
            Click(page.FindControl<Button>("ArchiveFavoritesFilter")!);
            await Settle(fixture);
            Require(panel.CaptureAnchor()!.Value.Index == 0, "Leaving an empty result did not start at the beginning.");
            await Program.WaitUntilAsync(() => tiles.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap));
            ArchiveMediaTileView tile = tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>().First(view => view.DataContext is ArchiveAssetRow);
            ArchiveAssetRow tileRow = (ArchiveAssetRow)tile.DataContext!;
            Require(AutomationProperties.GetName(tile)?.Contains(tileRow.Name) == true, "Accessible media text omitted the complete filename.");
            await WaitForVisibleThumbnailsAsync(tiles);
            Program.CaptureWindow(fixture, "smoke-media-tiles-" + fixture.RenderScaling.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + "x-" + MeowChan.Desktop.Localization.Texts.Culture.TwoLetterISOLanguageName + "-" + fixture.ActualThemeVariant + ".png");
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
            await Settle(fixture);
            Require(tiles.GetVisualDescendants().OfType<Image>().All(image => image.Source == null), "Hidden tile view retained decoded preview bitmaps.");
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
            await Settle(fixture);
            await WaitForVisibleThumbnailsAsync(tiles);
            page.FindControl<ListBox>("ArchiveThreads")!.SelectedItems!.Clear();
            await Settle(fixture);
            RequireThreadDeselected(page, tiles, grid);
            Volatile.Write(ref holdMediaScan, true);
            await page.LoadThreadAsync(id, resetPage: false);
            await Program.WaitUntilAsync(() => scanStarted.Task.IsCompleted);
            Task staleScan = page.MediaLoadTask;
            page.FindControl<ListBox>("ArchiveThreads")!.SelectedItems!.Clear();
            RequireThreadDeselected(page, tiles, grid);
            releaseScan.TrySetResult();
            await staleScan;
            await Settle(fixture);
            RequireThreadDeselected(page, tiles, grid);
            Console.WriteLine("SMOKE media-tile thread deselection: decoded previews released and late uncancelable manifest scan rejected PASS");
            Console.WriteLine("SMOKE media-tile-integration 1200 assets: resize, mode, gallery, sort, version, favorites, rebound identity, accessibility and hide PASS");
        }
        finally
        {
            releaseScan.TrySetResult();
            page?.EndLifetime(); fixture?.Close();
            Directory.Delete(profile, true);
        }
    }

    private static void RequireThreadDeselected(ArchivePage page, ListBox tiles, DataGrid grid)
    {
        Require(page.CurrentThread == null && !page.IsBusy && !page.IsMediaLoading, "Thread deselection retained a reader or active load state.");
        Require(tiles.ItemCount == 0 && !grid.ItemsSource!.Cast<ArchiveAssetRow>().Any()
            && tiles.SelectedItem == null && grid.SelectedItem == null, "Thread deselection retained stale media rows or selection.");
        Require(tiles.GetVisualDescendants().OfType<Image>().All(image => image.Source == null), "Thread deselection retained decoded thumbnail pixels.");
        Require(page.FindControl<TextBlock>("ArchiveSelectHint")!.IsVisible, "Thread deselection did not show the selection hint.");
    }

    private static Task WaitForVisibleThumbnailsAsync(ListBox list) => Program.WaitUntilAsync(() =>
        list.GetVisualDescendants().OfType<ArchiveMediaTileView>()
            .Where(view => view.TranslatePoint(default, list) is { } point && point.Y < list.Bounds.Height && point.Y + view.Bounds.Height > 0)
            .All(view => view.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap)));

    private static async Task CheckDelayedThumbnailRecyclingAsync(Window fixture, ListBox list)
    {
        ArchiveMediaTileView[] views = list.GetVisualDescendants().OfType<ArchiveMediaTileView>()
            .Where(view => view.TranslatePoint(default, list) is { } point && point.Y >= 0 && point.Y + view.Bounds.Height <= list.Bounds.Height)
            .Where(view => view.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap)).Take(3).ToArray();
        Require(views.Length == 3, "Delayed thumbnail fixture needs three visible, decoded tiles.");
        ArchiveAssetRow[] originals = views.Select(view => (ArchiveAssetRow)view.DataContext!).ToArray();
        ArchiveAssetRow[] delayedRows = originals.Select(row => new ArchiveAssetRow(row.Manifest, row.Asset with { OriginalFileName = "Delayed " + row.Name })).ToArray();
        var decoders = views.Select(view => view.ThumbnailDecoder).ToArray();
        Image[] images = views.Select(view => view.GetVisualDescendants().OfType<Image>().Single()).ToArray();
        Bitmap?[] heldBitmaps = new Bitmap?[3];
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, started = 0, ready = 0, maximum = 0;
        bool wrongBitmapPublished = false;
        EventHandler<AvaloniaPropertyChangedEventArgs> observe = (_, e) =>
        {
            if (e.Property == Image.SourceProperty && e.Sender is Image changed
                && heldBitmaps.Any(bitmap => bitmap != null && ReferenceEquals(bitmap, changed.Source)))
                wrongBitmapPublished = true;
        };
        foreach (Image image in images) image.PropertyChanged += observe;
        try
        {
            for (int index = 0; index < views.Length; index++)
            {
                int slot = index;
                views[index].ThumbnailDecoder = async (row, token) =>
                {
                    if (!ReferenceEquals(row, delayedRows[slot])) return await decoders[slot](row, token);
                    int running = Interlocked.Increment(ref active);
                    Interlocked.Increment(ref started);
                    int observed;
                    do { observed = Volatile.Read(ref maximum); }
                    while (running > observed && Interlocked.CompareExchange(ref maximum, running, observed) != observed);
                    try
                    {
                        // Real verified local decode deliberately ignores cancellation while held.
                        heldBitmaps[slot] = await decoders[slot](row, CancellationToken.None);
                        Interlocked.Increment(ref ready);
                        await release.Task.ConfigureAwait(false);
                        return heldBitmaps[slot];
                    }
                    finally { Interlocked.Decrement(ref active); }
                };
                views[index].DataContext = delayedRows[index];
            }
            await Program.WaitUntilAsync(() => Volatile.Read(ref ready) >= 2);
            Require(Volatile.Read(ref started) == 2 && Volatile.Read(ref maximum) == 2,
                "More than two tile thumbnail jobs entered decoding while two jobs were held.");
            int[] held = Enumerable.Range(0, 3).Where(index => heldBitmaps[index] != null).ToArray();
            Require(held.Length == 2, "Held thumbnail jobs did not decode real bitmaps.");
            int rebound = held[0], hidden = held[1];
            int queued = Enumerable.Range(0, 3).Except(held).Single();
            ArchiveAssetRow replacement = Rows(list).Last(row => Key(row) != Key(delayedRows[rebound]));
            views[rebound].DataContext = replacement;
            views[hidden].IsVisible = false;
            views[queued].IsVisible = false;
            release.TrySetResult();
            await Program.WaitUntilAsync(() => images[rebound].Source is Bitmap && Volatile.Read(ref active) == 0);
            Require(!wrongBitmapPublished, "A late canceled thumbnail was published onto a rebound or hidden tile.");
            Require(ReferenceEquals(views[rebound].DataContext, replacement), "Recycling test lost the replacement row.");
            Require(images[hidden].Source == null && images[queued].Source == null, "A hidden tile accepted a late thumbnail.");
            Require(Volatile.Read(ref started) == 2 && Volatile.Read(ref maximum) == 2,
                "A canceled queued thumbnail entered decoding or exceeded the two-job bound.");
            Console.WriteLine("SMOKE media-tile late decode: rebound, hidden and canceled queued tiles rejected stale bitmaps; maximum two active decodes PASS");
        }
        finally
        {
            release.TrySetResult();
            for (int index = 0; index < views.Length; index++)
            {
                images[index].PropertyChanged -= observe;
                views[index].ThumbnailDecoder = decoders[index];
                views[index].ClearValue(StyledElement.DataContextProperty);
                views[index].IsVisible = true;
            }
            await Program.WaitUntilAsync(() => Volatile.Read(ref active) == 0);
            await Settle(fixture);
        }
    }

    private static string Key(ArchiveAssetRow row) => row.Manifest.Directory + "|" + row.Asset.AssetId;
    private static ArchiveAssetRow[] Rows(ListBox list) => list.ItemsSource!.Cast<ArchiveAssetRow>().ToArray();
    private static MediaTilePanel Panel(ListBox list) => list.GetVisualDescendants().OfType<MediaTilePanel>().Single();
    private static void SetAssets(ArchivePage page, ArchiveAssetRow[] rows) => typeof(ArchivePage).GetField("_assets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, rows);
    private static void Render(ArchivePage page) => typeof(ArchivePage).GetMethod("RenderMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
