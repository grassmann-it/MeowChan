using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

internal static class ExportReaderSizingChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        string previousMode = window.SessionHost.Config.WorkspaceMode;
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
                    // The body is the dialog frame's scrolling part; the error strip has a scroll viewer of its own above it (MD1/MD4).
                    ScrollViewer body = card.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == MeowChan.Desktop.Controls.DialogFrame.BodyName);
                    return PresentationChecks.MatchesRequestedSize(window, size.Width, size.Height)
                        && overlay.GetVisualAncestors().Contains(window.FindControl<ContentControl>("ReaderHost")!)
                        && card.Bounds.Height > 200 && card.MaxHeight > 200 && body.Viewport.Height > 80
                        && body.Extent.Width <= body.Viewport.Width + 1
                        && Fits(window, page.FindControl<Button>("ArchiveExportClose")!)
                        && Fits(window, page.FindControl<Button>("ArchiveExportOpen")!)
                        && Fits(window, page.FindControl<Button>("ArchiveExportStart")!);
                }, () => ExportState(window, page, size.Width, size.Height));
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
    private static string ExportState(Window window, ArchivePage page, double width, double height)
    {
        Border overlay = page.FindControl<Border>("ArchiveExportDialog")!;
        string Box(string name) => page.FindControl<Control>(name) is { } control
            ? name + "=" + control.TranslatePoint(default, window) + "/" + control.Bounds.Size + (control.IsEffectivelyVisible ? "" : " hidden") : name + "=missing";
        string card = overlay.Child is Border child
            ? "card " + child.Bounds.Size + " max " + child.MaxHeight + " body " + (child.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(viewer => viewer.Name == MeowChan.Desktop.Controls.DialogFrame.BodyName) is { } body
                ? body.Viewport + " extent " + body.Extent : "none") : "no card";
        return "window " + window.Bounds.Size + " requested " + width + "x" + height + " size ok " + PresentationChecks.MatchesRequestedSize(window, width, height)
            + "; overlay visible " + overlay.IsEffectivelyVisible + " in reader host " + overlay.GetVisualAncestors().Contains(window.FindControl<ContentControl>("ReaderHost")!)
            + "; " + card + "; " + Box("ArchiveExportClose") + "; " + Box("ArchiveExportOpen") + "; " + Box("ArchiveExportStart");
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
