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
        Exception? failureBeforeCleanup = null;
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
            await UntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) is { IsVisible: true, Bounds.Width: > 0 });
            await LayoutAsync(window);
            DialogGallery.Record(MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window)!, "gallery-shortcut-help", "shortcuts window",
                "F1 shortcuts window on the Gallery group from inside the gallery");
            // F1 in the gallery toggles the same window shut; the shortcuts window is a window of its own, so the keyboard goes back to the viewer first.
            window.Activate();
            await LayoutAsync(window);
            viewer.Focus();
            PressKey(viewer, Key.F1);
            await UntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) == null);

            Grid viewport = Find<Grid>(viewer, "GalleryViewport");
            // The context request on the viewport (the way the other media-menu smokes raise it); a synthesized right-button press/release
            // opened the menu only in some cells of a live window run.
            viewport.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await LayoutAsync(window);
            await UntilAsync(() => viewer.MediaMenu?.IsOpen == true);
            await LayoutAsync(window);
            DialogGallery.RecordSurface(viewer.MediaMenu!, "gallery-media-menu", "context menu", "right-click on the shown picture");
            viewer.MediaMenu!.Close();
            await LayoutAsync(window);
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                viewer.SetOverflowOpen(false);
                if (viewer.MediaMenu?.IsOpen == true) viewer.MediaMenu.Close();
                Click(Find<Button>(viewer, "GalleryClose"));
                await window.GalleryCleanupTask.WaitAsync(Deadline);
                await UntilAsync(() => host.Content is not GalleryViewer);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in GalleryToolbarLayoutChecks.Gallery: " + exception));
        }
    }
}
