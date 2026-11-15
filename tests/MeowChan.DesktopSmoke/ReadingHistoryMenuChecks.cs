using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// RH1 (smoke ids reading-position, reader-ux, context-menus-header, post-bookmark-return): the book button's context menu lists the
/// saved threads as the one "continue reading" command with its one icon, ends with "Show full history…", and an entry opens that
/// thread's saved place. Runs after the history holds at least one place and the reader is closed.
/// </summary>
internal static class ReadingHistoryMenuChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await window.RefreshReadingHistorySnapshotAsync();
        Require(window.ReadingHistorySnapshot.Count > 0, "The reading history snapshot stayed empty after saving places.");
        ContextMenuModel model = window.ReadingHistoryMenuModel ?? throw new InvalidOperationException("The reading history menu did not compose.");
        ContextMenuEntry[] places = model.Entries.Where(entry => entry.CommandId == ReadingHistoryMenu.ContinueCommandId).ToArray();
        Require(places.Length > 0 && places.Length <= window.SessionHost.Config.ReadingHistoryMenuEntries, "The history menu lists no place or more than its setting.");
        Require(model.Entries.Last().CommandId == ReadingHistoryMenu.HistoryCommandId, "The history menu does not end with the full history entry.");

        // One icon: every menu item of the command carries the catalog mask of the command, as the header button does.
        ContextMenu built = ContextMenuBuilder.Build(model)!;
        foreach (MenuItem item in ((IEnumerable<Control>)built.ItemsSource!).OfType<MenuItem>()
            .Where(item => (item.Tag as string)?.StartsWith("reading.place:", StringComparison.Ordinal) == true))
            Require(MaskOf(item.Icon as Control) == CommandBindings.IconMask(ReadingHistoryMenu.ContinueIcon), "A history entry has another icon than the continue reading command.");
        Button button = window.FindControl<Button>("ContinueReadingButton")!;
        if (button.IsEffectivelyVisible)
        {
            Require(button.GetVisualDescendants().OfType<Border>().Any(border => MaskOf(border) == CommandBindings.IconMask(ReadingHistoryMenu.ContinueIcon)),
                "The header button does not draw the continue reading icon.");
            ContextMenuHost host = window.ReadingHistoryHost ?? throw new InvalidOperationException("The book button has no menu host.");
            host.Show(atPointer: false);
            Require(host.OpenMenu != null, "The menu key did not open the reading history menu.");
            host.Close();
        }

        // An entry opens that thread's saved place in the reader.
        LocalReadingPosition target = window.ReadingHistorySnapshot[^1].Position;
        await window.ResumeReadingPlaceAsync(target);
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => reader.IsVisible && window.ArchivePage.CurrentThread?.Summary.Id == target.ThreadId);
        Click(window.ArchivePage.FindControl<Button>("ArchiveCloseReader")!);
        Console.WriteLine("SMOKE reading history menu: one command and icon, full history entry, entry opens its place.");
    }

    private static object? MaskOf(Control? control) => (control as Border)?.OpacityMask is ImageBrush brush ? brush.Source : null;

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
