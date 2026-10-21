using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveDeletionChecks
{
    internal static async Task RunAsync(MainWindow window, DataGrid table)
    {
        await Task.Delay(120);
        await Program.WaitUntilAsync(() => !window.ArchivePage.IsBusy);
        ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
        Require(rows.Length == 2, "Permanent deletion needs its two isolated fixture threads.");
        table.SelectedItems.Clear();
        foreach (ArchiveThreadRow row in rows) table.SelectedItems.Add(row);
        await Program.WaitUntilAsync(() => !window.ArchivePage.IsBusy);
        string[] directories = rows.Select(row => Path.Combine(window.SessionHost.ProfileDirectory, "Delete media " + row.Summary.Id.Thread)).ToArray();
        Require(directories.All(path => File.Exists(Path.Combine(path, "reader.png"))), "Missing owned deletion fixtures.");
        string export = Path.Combine(window.SessionHost.ProfileDirectory, "Existing export");
        Directory.CreateDirectory(export);
        await File.WriteAllTextAsync(Path.Combine(export, "index.html"), "preserve exported copy");
        Window? Dialog() => window.OwnedWindows.FirstOrDefault(item => item.Name == "ArchivePermanentDeleteDialog");
        void Open()
        {
            Require(table.SelectedItems.Count == 2, "Deletion shortcut lost its selected threads.");
            table.Focus();
            KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, KeyModifiers = KeyModifiers.Shift };
            table.RaiseEvent(key);
            Require(key.Handled, "Archive did not handle Shift+Delete.");
        }
        Open();
        await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true);
        Window dialog = Dialog()!;
        Require(Equals(Find<Button>(dialog, "ArchivePermanentDeleteCancel").Content, "Cancel"), "Confirmation cancel label is not localized.");
        Require(Find<Button>(dialog, "ArchivePermanentDeleteConfirm").IsEnabled, "Valid multi-selection cannot be confirmed.");
        Program.CaptureWindow(dialog, "smoke-archive-delete-confirmation.png");
        Click(Find<Button>(dialog, "ArchivePermanentDeleteCancel"));
        await Program.WaitUntilAsync(() => Dialog() == null);
        Require(directories.All(path => File.Exists(Path.Combine(path, "reader.png"))), "Cancel deleted local media.");
        await using (ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None))
            foreach (ArchiveThreadRow row in rows) Require(await reader.ReadThreadAsync(row.Summary.Id) != null, "Cancel deleted archive records.");
        Open();
        await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true);
        dialog = Dialog()!;
        Click(Find<Button>(dialog, "ArchivePermanentDeleteConfirm"));
        await Program.WaitUntilAsync(() => Find<Button>(dialog, "ArchivePermanentDeleteCancel").IsEnabled);
        Require(directories.All(path => !File.Exists(Path.Combine(path, "reader.png")) && !File.Exists(Path.Combine(path, ".meowchan-manifest.json"))), "Confirmed deletion retained owned media.");
        Require(directories.All(path => File.Exists(Path.Combine(path, "unrelated.txt"))) && File.Exists(Path.Combine(export, "index.html")), "Deletion removed unrelated files or exports.");
        await using (ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None))
        {
            foreach (ArchiveThreadRow row in rows) Require(await reader.ReadThreadAsync(row.Summary.Id) == null, "Confirmed deletion retained archive records.");
            Require((await reader.SelectThreadIdsAsync(new ArchiveThreadQuery(), CancellationToken.None)).Count == 50, "Deletion affected unselected archive threads.");
        }
        Require(File.Exists(Path.Combine(window.SessionHost.ProfileDirectory, "Delete media 1025", "reader.png")), "Deletion affected sibling media.");
        Require(Find<TextBox>(dialog, "ArchivePermanentDeleteDetails").Text!.Contains("unrelated.txt"), "Deletion result omitted retained files.");
        Program.CaptureWindow(dialog, "smoke-archive-delete-result.png");
        Click(Find<Button>(dialog, "ArchivePermanentDeleteCancel"));
        await Program.WaitUntilAsync(() => Dialog() == null);
        Console.WriteLine("SMOKE archive permanent deletion: Shift+Delete, native batch confirmation, cancel unchanged, owned media/catalog removal, retained siblings/unrelated files/exports passed.");
    }

    private static T Find<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
