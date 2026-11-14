using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "gallery": the real main-window gallery over two generated pictures. Captures the viewer, its post
// panel, the overflow menu, the shortcut help, the media menu and the toolbar tooltip-free states, then closes the gallery.
internal static partial class GalleryToolbarLayoutChecks
{
    internal static async Task CaptureForDialogGalleryAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "dialog-gallery-fixture");
        GalleryEntry first = await EntryAsync(Path.Combine(root, "one"), "first.png", "1", "81", 0);
        GalleryEntry second = await EntryAsync(Path.Combine(root, "two"), "second.png", "2", "82", 1);
        await window.OpenMediaGalleryLaidOutAsync([first, second], first.EntryKey);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        await UntilAsync(() => host.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        try
        {
            await UntilAsync(() => Find<Image>(viewer, "GalleryImage").Source is Avalonia.Media.Imaging.Bitmap);
            await LayoutAsync(window);
            if (Find<Border>(viewer, "GalleryContext").IsVisible) Click(Find<Button>(viewer, "GalleryToggleContext"));
            viewer.SetOverflowOpen(false);
            await LayoutAsync(window);
            DialogGallery.Record(window, "gallery-viewer", "main window", "viewer with toolbar, post panel closed");

            Click(Find<Button>(viewer, "GalleryToggleContext"));
            await UntilAsync(() => Find<Border>(viewer, "GalleryContext").IsVisible);
            await LayoutAsync(window);
            DialogGallery.Record(window, "gallery-viewer-post-panel", "main window", "post panel open beside the picture");
            Click(Find<Button>(viewer, "GalleryToggleContext"));
            await LayoutAsync(window);

            await OpenOverflowByKeyAsync(window, viewer);
            if (TopLevel.GetTopLevel(viewer.OverflowMenuContent) is Control popup && popup.Bounds.Width > 0)
                DialogGallery.RecordSurface(popup, "gallery-overflow-menu", "popup", "the more (...) menu with the rare commands");
            else DialogGallery.Skip("gallery-overflow-menu", "popup surface had no size");
            viewer.SetOverflowOpen(false);
            await LayoutAsync(window);

            viewer.Focus();
            PressKey(viewer, Key.F1);
            await UntilAsync(() => Find<Border>(viewer, "GalleryShortcutHelp").IsVisible);
            await LayoutAsync(window);
            DialogGallery.Record(window, "gallery-shortcut-help", "main window", "F1 shortcut overlay inside the gallery");
            PressKey(viewer, Key.F1);
            await UntilAsync(() => !Find<Border>(viewer, "GalleryShortcutHelp").IsVisible);

            Grid viewport = Find<Grid>(viewer, "GalleryViewport");
            await RightClickAsync(viewport, window);
            await UntilAsync(() => viewer.MediaMenu?.IsOpen == true);
            await LayoutAsync(window);
            DialogGallery.RecordSurface(viewer.MediaMenu!, "gallery-media-menu", "context menu", "right-click on the shown picture");
            viewer.MediaMenu!.Close();
            await LayoutAsync(window);
        }
        finally
        {
            viewer.SetOverflowOpen(false);
            if (viewer.MediaMenu?.IsOpen == true) viewer.MediaMenu.Close();
            Click(Find<Button>(viewer, "GalleryClose"));
            await window.GalleryCleanupTask.WaitAsync(Deadline);
            await UntilAsync(() => host.Content is not GalleryViewer);
        }
    }

    private static async Task RightClickAsync(Control target, TopLevel root)
    {
        Point at = target.TranslatePoint(new Point(Math.Max(1, target.Bounds.Width / 2), Math.Max(1, target.Bounds.Height / 2)), root) ?? new Point(40, 40);
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, at, 0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, at, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
        pointer.Capture(null);
        await LayoutAsync((Window)root);
    }
}
