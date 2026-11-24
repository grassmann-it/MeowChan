using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests.Shared;

internal static class MediaTileScrollScenario
{
    private const int TileCount = 48;

    internal static async Task RunAsync(string profile, CancellationToken token, Window? owner = null, Action<Window>? capture = null)
    {
        await SeedAsync(profile, token);
        List<string?> opened = new();
        MediaPage page = new(cancel => ArchiveReader.OpenAsync(profile, cancel))
        {
            OpenGallery = (entries, start) => { opened.Add(entries[start].AssetId); return Task.CompletedTask; }
        };
        Window window = new() { Width = 1100, Height = 800, Content = page, ShowInTaskbar = false };
        try
        {
            page.InitializeSharedMediaMenus();
            if (owner is null) window.Show(); else window.Show(owner);
            window.Activate();
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(token);
            await page.PresentTask.WaitAsync(token);

            ListBox grid = page.Grid;
            Check(grid.ItemCount == TileCount, $"Expected {TileCount} saved tiles, found {grid.ItemCount}.");
            grid.SelectedIndex = 0;
            await WaitForTileAsync(grid, 0, page.ShownEntries[0].AssetId!, selected: true, window, token);

            const int targetIndex = TileCount - 2;
            MediaLibraryEntry target = page.ShownEntries[targetIndex];
            string targetId = target.AssetId ?? throw new InvalidOperationException("The target needs a saved asset ID.");
            void CheckActivation(int count, string gesture) =>
                Check(opened.Count == count && opened[^1] == targetId,
                    $"{gesture} must open {targetId} exactly once, got [{string.Join(", ", opened)}].");

            Check(grid.ContainerFromIndex(targetIndex) is null, "The deep target must start outside the realized range.");
            grid.ScrollIntoView(targetIndex);
            (ListBoxItem container, MediaLibraryTileView tile) =
                await WaitForTileAsync(grid, targetIndex, targetId, selected: false, window, token);
            Check(grid.ItemsPanelRoot is MediaTilePanel && grid.ContainerFromIndex(0) is null && grid.SelectedIndex == 0,
                "Scrolling must virtualize the first tile while keeping its selection.");

            await ClickAsync(tile, window, MouseButton.Left, count: 2);
            CheckActivation(1, "Double-click");
            (container, tile) = await WaitForTileAsync(grid, targetIndex, targetId, selected: true, window, token);
            capture?.Invoke(window);

            container.Focus();
            Check(container.IsKeyboardFocusWithin, "The scrolled tile must receive keyboard focus.");
            KeyEventArgs enter = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            container.RaiseEvent(enter);
            await SettleAsync(window);
            Check(enter.Handled, "Enter on the tile must be handled by the media page.");
            CheckActivation(2, "Enter");

            grid.SelectedIndex = targetIndex + 1;
            (_, tile) = await WaitForTileAsync(grid, targetIndex, targetId, selected: false, window, token);
            await ClickAsync(tile, window, MouseButton.Right, count: 1);
            await WaitForTileAsync(grid, targetIndex, targetId, selected: true, window, token);
            ContextMenu menu = page.MediaMenu is { IsOpen: true } shown
                ? shown : throw new InvalidOperationException("Right-click on the scrolled tile did not open its menu.");
            Check(grid.SelectedIndex == targetIndex && grid.Selection.Count == 1, "Right-click must select only the hit tile.");
            Check(page.MediaMenuSession is { IsCurrent: true } session && session.Target == MediaMenuTarget.Of(target),
                "The menu must capture the exact hit occurrence.");
            Check(opened.Count == 2, "Opening the context menu must not activate the tile.");
            CommandBindings.InvokeAction(menu.Items.OfType<MenuItem>().Single(item => CommandBindings.ActionId(item) == "gallery.open"));
            await SettleAsync(window);
            CheckActivation(3, "Open in gallery from the context menu");
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    private static async Task<(ListBoxItem, MediaLibraryTileView)> WaitForTileAsync(ListBox grid, int index, string assetId,
        bool selected, Window window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await SettleAsync(window);
            if (grid.ContainerFromIndex(index) is ListBoxItem { IsArrangeValid: true, IsEffectivelyVisible: true,
                    Bounds.Width: > 0, DataContext: MediaLibraryTileRow row } container
                && row.Entry.AssetId == assetId && container.IsSelected == selected
                && container.GetVisualDescendants().OfType<MediaLibraryTileView>().FirstOrDefault() is
                    { IsArrangeValid: true, IsEffectivelyVisible: true, Bounds.Width: > 0, DataContext: MediaLibraryTileRow bound } tile
                && ReferenceEquals(bound, row)
                && tile.FindAncestorOfType<ScrollContentPresenter>() is { } viewport
                && tile.Badges.Bounds is { Width: > 0, Height: > 0 }
                && tile.Badges.TranslatePoint(new Rect(tile.Badges.Bounds.Size).Center, viewport) is { } point
                && new Rect(viewport.Bounds.Size).Contains(point)
                && tile.GetVisualDescendants().OfType<Border>().SingleOrDefault(mark => mark.Name == "MediaTileSelected") is { } check
                && check.IsEffectivelyVisible == selected && (!selected || check is { IsArrangeValid: true, Bounds.Width: > 0 }))
                return (container, tile);
            await Task.Delay(15, CancellationToken.None);
        }
        throw new OperationCanceledException($"Waiting for arranged media tile {index} ({assetId}, selected={selected}).", token);
    }

    private static async Task ClickAsync(MediaLibraryTileView tile, Window window, MouseButton button, int count)
    {
        Point at = tile.Badges.TranslatePoint(new Rect(tile.Badges.Bounds.Size).Center, window)
            ?? throw new InvalidOperationException("The arranged tile detached before the pointer input.");
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        bool right = button == MouseButton.Right;
        for (int click = 1; click <= count; click++)
        {
            tile.RaiseEvent(new PointerPressedEventArgs(tile, pointer, window, at, (ulong)(click * 10),
                new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                    right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, click));
            tile.RaiseEvent(new PointerReleasedEventArgs(tile, pointer, window, at, (ulong)(click * 10 + 5),
                new PointerPointProperties(RawInputModifiers.None,
                    right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, button));
        }
        pointer.Capture(null);
        await SettleAsync(window);
    }

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static async Task SeedAsync(string profile, CancellationToken token)
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
        string media = Path.Combine(profile, "media");
        Directory.CreateDirectory(media);
        Dictionary<string, object> entries = new();
        for (int index = 0; index < TileCount; index++)
        {
            string name = $"tile-{index:D2}.png";
            byte[] bytes = [.. png, (byte)index];
            await File.WriteAllBytesAsync(Path.Combine(media, name), bytes, token);
            entries.Add(name, new
            {
                MediaIdentifier = name, Variant = "original", SourceUrl = "https://offline.invalid/" + name,
                FileName = name, OriginalFileName = name, Length = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            });
        }
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }), token);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease, token);
        await catalog.CaptureAsync(new(new ResourceId("offline", "media", "100"), "https://offline.invalid/100",
            "Media tile activation", ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, media,
            [new CapturedPost("100", 0, PostFieldPresence.All, Html: "Saved media")]), token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
