using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// Interaction audit 1.1.0, inside the registered "media-page" journey (MEOWCHAN_MEDIA_PAGE_ONLY): a real double click (two left presses,
// the second with click count 2, through Avalonia's gesture recognizer) on a tile opens the gallery at that tile, not at whatever was
// selected before; Enter on the same tile opens the same window at the same start (parity); a double click on the tile's star or on
// empty grid space opens nothing; Ctrl+wheel steps the tile size and Ctrl+Minus steps it back. Managed input only.
internal static partial class MediaPageChecks
{
    private static async Task TileActivationParityAsync(MediaPage page, Window window,
        List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened)
    {
        ListBox grid = page.Grid;
        Require(grid.ItemCount >= 2, "Tile activation needs two tiles, got " + grid.ItemCount);
        grid.SelectedIndex = 0; // an older selection elsewhere must not be what the double click opens
        grid.ScrollIntoView(1);
        Control? second = null;
        await UntilAsync(() => (second = grid.ContainerFromIndex(1)) is { IsEffectivelyVisible: true, Bounds.Width: > 0 });

        int before = opened.Count;
        await DoubleClickAsync(second!, window);
        await UntilAsync(() => opened.Count == before + 1);
        (IReadOnlyList<MediaLibraryEntry> clicked, int clickedStart) = opened[^1];
        Require(ReferenceEquals(clicked[clickedStart], page.ShownEntries[1]) || clicked[clickedStart] == page.ShownEntries[1],
            "A double click must open the gallery at the double-clicked tile.");
        Require(grid.SelectedIndex == 1 && grid.Selection.Count == 1, "The double-clicked tile must become the selection.");

        // Enter on the same tile: the same window and start.
        second!.Focus();
        second.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = second });
        await UntilAsync(() => opened.Count == before + 2);
        Require(opened[^1].Window.SequenceEqual(clicked) && opened[^1].Start == clickedStart,
            "Enter and a double click on one tile must open the same gallery window at the same place.");

        // The star keeps its own meaning; empty space opens nothing.
        if (second.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .FirstOrDefault(toggle => toggle.Name == "MediaTileFavoriteToggle" && toggle.IsEffectivelyVisible) is { } star)
        {
            int count = opened.Count;
            await DoubleClickAsync(star, window); // toggled twice: the star is back where it was
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(opened.Count == count, "A double click on the tile's star must not open the gallery.");
        }
        if (grid.GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault() is { } panel
            && panel.Bounds.Height < grid.Bounds.Height - 40)
        {
            int count = opened.Count;
            await DoubleClickAtAsync(grid, new Point(grid.Bounds.Width / 2, grid.Bounds.Height - 12), window);
            Require(opened.Count == count, "A double click on empty grid space must open nothing.");
        }

        // Ctrl+wheel up: one size larger; Ctrl+Minus: back. The three sizes of the Layout menu are the only ones.
        MediaTileSize size = page.PageLayout.TileSize;
        Wheel(second, window, new Vector(0, 1), KeyModifiers.Control);
        Require(page.PageLayout.TileSize == MediaPage.StepTileSize(size, 1), "Ctrl+wheel up must step the tile size up: " + page.PageLayout.TileSize);
        grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemMinus, KeyModifiers = KeyModifiers.Control, Source = second });
        Require(page.PageLayout.TileSize == MediaPage.StepTileSize(MediaPage.StepTileSize(size, 1), -1), "Ctrl+Minus must step the tile size down.");
        page.ApplyLayout(page.PageLayout with { TileSize = size }, save: false);
    }

    private static Task DoubleClickAsync(Control target, Window root) =>
        DoubleClickAtAsync(target, new Point(Math.Max(1, target.Bounds.Width / 2), Math.Max(1, target.Bounds.Height / 2)), root);

    private static async Task DoubleClickAtAsync(Control target, Point local, Window root)
    {
        Point at = target.TranslatePoint(local, root) ?? local;
        Control hit = root.InputHitTest(at) as Control ?? target;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        for (int click = 1; click <= 2; click++)
        {
            hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, root, at, (ulong)(click * 10),
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, click));
            hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, root, at, (ulong)(click * 10 + 5),
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        }
        pointer.Capture(null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Wheel(Control target, Window root, Vector delta, KeyModifiers modifiers)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? default;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerWheelEventArgs(target, pointer, root, at, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), modifiers, delta));
    }
}
