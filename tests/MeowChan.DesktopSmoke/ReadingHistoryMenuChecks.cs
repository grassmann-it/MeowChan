using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// RH1 and QW-A (smoke ids reading-position, reader-ux, context-menus-header, post-bookmark-return): the reading history list under the
/// header book button lists the saved threads as the one "continue reading" command with its one icon, ends with "Show full history…",
/// has a usable size (at least 320 dip wide, never wider than its maximum, as high as the setting says before it scrolls) and an entry
/// opens that thread's saved place. Runs after the history holds at least one place and the reader is closed.
/// </summary>
internal static class ReadingHistoryMenuChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await window.RefreshReadingHistorySnapshotAsync();
        Require(window.ReadingHistorySnapshot.Count > 0, "The reading history snapshot stayed empty after saving places.");
        ReadingHistoryFlyoutContent content = window.ReadingHistoryContent ?? throw new InvalidOperationException("The reading history list did not compose.");
        Require(content.Rows.Count > 0 && content.Rows.Count <= window.SessionHost.Config.ReadingHistoryMenuEntries, "The history list lists no place or more than its setting.");
        Require(content.HasHistory, "The history list offers no full history although places are saved.");

        Button button = window.FindControl<Button>("ContinueReadingButton")!;
        if (button.IsEffectivelyVisible)
        {
            // One icon: the header button, every row and the arrow's list carry the catalog mask of the command.
            Require(button.GetVisualDescendants().OfType<Border>().Any(border => MaskOf(border) == CommandBindings.IconMask(ReadingHistoryMenu.ContinueIcon)),
                "The header button does not draw the continue reading icon.");
            ReadingHistoryFlyout list = window.ReadingHistoryList ?? throw new InvalidOperationException("The book button has no history list.");
            Require(window.ShowReadingHistoryList(keyboard: true), "The history list did not open.");
            Require(list.IsOpen, "The history list reports closed after opening.");
            ReadingHistoryPanel panel = list.Panel;
            await Program.WaitUntilAsync(() => panel.Bounds.Width > 0 && panel.PlaceButtons.Count == content.Rows.Count);

            // Size (compile-only for the review smokes; the central run checks the numbers): not a tiny popup.
            Require(panel.Bounds.Width >= ReadingHistoryFlyoutLayout.MinWidth - 1, "The history list is narrower than its minimum width: " + panel.Bounds.Width);
            Require(panel.Bounds.Width <= ReadingHistoryFlyoutLayout.MaxWidthFor(window.Bounds.Width) + 1, "The history list is wider than its maximum width: " + panel.Bounds.Width);
            Require(Math.Abs(panel.Scroller.MaxHeight - ReadingHistoryFlyoutLayout.ListMaxHeight(content.Rows.Count, window.Bounds.Height)) < 0.5,
                "The history list's scroll height is not the setting's rows (or the window's room).");
            Rect box = new(panel.TranslatePoint(default, window) ?? default, panel.Bounds.Size);
            Require(box.Left >= -1 && box.Right <= window.Bounds.Width + 1 && box.Bottom <= window.Bounds.Height + 1, "The history list leaves the window: " + box);
            foreach (Button row in panel.PlaceButtons)
                Require(row.GetVisualDescendants().OfType<Border>().Any(border => MaskOf(border) == CommandBindings.IconMask(ReadingHistoryMenu.ContinueIcon)),
                    "A history row has another icon than the continue reading command.");
            Require(panel.ShowAllButton.IsEnabled, "Show full history is disabled although places are saved.");
            Program.CaptureWindow(window, "smoke-reading-history-list.png");
            list.Close();
            await Program.WaitUntilAsync(() => !list.IsOpen);
        }

        // An entry opens that thread's saved place in the reader.
        LocalReadingPosition target = window.ReadingHistorySnapshot[^1].Position;
        await window.ResumeReadingPlaceAsync(target);
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => reader.IsVisible && window.ArchivePage.CurrentThread?.Summary.Id == target.ThreadId);
        // With the thread open the header reading actions stay available.
        Require(button.IsEffectivelyVisible, "The book hides while a thread is open.");
        Click(window.ArchivePage.FindControl<Button>("ArchiveCloseReader")!);
        Console.WriteLine("SMOKE reading history list: one command and icon, usable size, full history entry, entry opens its place.");
    }

    private static object? MaskOf(Control? control) => (control as Border)?.OpacityMask is ImageBrush brush ? brush.Source : null;

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
