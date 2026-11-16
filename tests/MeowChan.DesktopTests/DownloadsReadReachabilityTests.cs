using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

/// <summary>
/// Regression coverage for the Downloads list's primary Read action. The row handler runs in
/// Avalonia's tunnel phase, before DataGrid has committed a pointer selection; composing the
/// menu synchronously therefore used to make Read look unavailable on a right-click.
/// </summary>
public sealed class DownloadsReadReachabilityTests
{
    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void PointerContextMenuIsComposedAfterTheDataGridSelectionRoute()
    {
        string source = Source("Desktop/Shell/MainWindow.Selection.cs");
        int handler = source.IndexOf("private void SelectDownloadContextRow(", StringComparison.Ordinal);
        int helper = source.IndexOf("private void OpenDownloadContextMenu(", handler, StringComparison.Ordinal);
        Assert.True(handler >= 0 && helper > handler);
        string pointerHandler = source[handler..helper];

        int post = pointerHandler.IndexOf("Dispatcher.UIThread.Post(() => OpenDownloadContextMenu(grid, contextItem)", StringComparison.Ordinal);
        Assert.True(post >= 0, "the menu must wait until DataGrid's pointer selection handling has completed");
        Assert.DoesNotContain("BuildDownloadMenu(grid, menu)", pointerHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadUsesTheValidatedThreadSelectionInsteadOfAStaleRawDataGridCount()
    {
        string menu = Source("Desktop/Shell/MainWindow.Menus.cs");
        int read = menu.IndexOf("Add(\"thread.read\"", StringComparison.Ordinal);
        Assert.True(read >= 0);
        string gate = menu[read..menu.IndexOf("Add(\"gallery.open\"", read, StringComparison.Ordinal)];
        Assert.Contains("frozen.Threads.Length == 1", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.SelectedItems.Count == 1", gate, StringComparison.Ordinal);

        string selection = Source("Desktop/Shell/MainWindow.Selection.cs");
        int key = selection.IndexOf("else if (SelectedThreads().Length == 1)", StringComparison.Ordinal);
        Assert.True(key >= 0, "Enter must use the same validated selection as the menu");
    }
}
