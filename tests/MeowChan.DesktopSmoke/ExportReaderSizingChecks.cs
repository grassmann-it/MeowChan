using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

internal static class ExportReaderSizingChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        string previousMode = Config.LoadConfig().WorkspaceMode;
        double previousWidth = window.Width, previousHeight = window.Height;
        try
        {
            foreach (var size in new[] { (Mode: "Simple", Width: 760d, Height: 500d), (Mode: "Barebones", Width: 640d, Height: 480d) })
            {
                await PresentationChecks.SwitchModeAsync(window, size.Mode);
                window.WindowState = WindowState.Normal; window.Width = size.Width; window.Height = size.Height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, size.Width, size.Height));
                Console.WriteLine($"SMOKE download reader export geometry: mode={size.Mode}, requested={size.Width}x{size.Height}, actual={window.Bounds.Size}, frame={window.FrameSize}, minimum={window.MinWidth}x{window.MinHeight}, scale={window.RenderScaling}.");
                page.FindControl<Button>("ExportArchiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    Border overlay = page.FindControl<Border>("ArchiveExportDialog")!;
                    if (!overlay.IsEffectivelyVisible || overlay.Child is not Border card) return false;
                    ScrollViewer body = card.GetVisualDescendants().OfType<ScrollViewer>().First();
                    return PresentationChecks.MatchesRequestedSize(window, size.Width, size.Height)
                        && overlay.GetVisualAncestors().Contains(window.FindControl<ContentControl>("ReaderHost")!)
                        && card.Bounds.Height > 200 && card.MaxHeight > 200 && body.Viewport.Height > 80
                        && body.Extent.Width <= body.Viewport.Width + 1
                        && Fits(window, page.FindControl<Button>("ArchiveExportClose")!)
                        && Fits(window, page.FindControl<Button>("ArchiveExportOpen")!)
                        && Fits(window, page.FindControl<Button>("ArchiveExportStart")!);
                });
                await ReaderUxChecks.CheckOverlayChromeAsync(window, window.FindControl<ContentControl>("ReaderHost")!);
                Program.CaptureWindow(window, "smoke-download-reader-export-" + size.Mode.ToLowerInvariant() + ".png");
                page.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!window.FindControl<ContentControl>("ReaderHost")!.IsVisible)
                    throw new InvalidOperationException("Closing export lost the Downloads-origin reader.");
            }
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, previousMode);
            window.Width = previousWidth; window.Height = previousHeight; window.UpdateLayout();
        }
        Console.WriteLine("SMOKE download-first reader export: hidden archive page, visible usable dialog body/footer at Simple760x500 and Barebones640x480 passed.");
    }
    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && point.HasValue && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && point.Value.X >= -1 && point.Value.Y >= -1
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
}
