using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveDeletionChecks
{
    internal static async Task RunAsync(MainWindow window, DataGrid table)
    {
        await Task.Delay(120);
        await Program.WaitUntilAsync(() => !window.ArchivePage.IsBusy);
        ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
        Require(rows.Length == 2, "Permanent deletion needs its two isolated fixture threads.");
        ResourceId groupedThread = rows[0].Summary.Id;
        ReaderGroup group = new(Guid.NewGuid(), "Deletion fixture", [groupedThread]);
        ReaderGroupsSnapshot groups = (await window.SessionHost.LoadReaderGroupsAsync()).Snapshot!;
        await window.SessionHost.SaveReaderGroupsAsync(groups with { Groups = [.. groups.Groups, group] }, groups.Revision);
        table.SelectedItems.Clear();
        foreach (ArchiveThreadRow row in rows) table.SelectedItems.Add(row);
        await Program.WaitUntilAsync(() => !window.ArchivePage.IsBusy);
        string[] directories = rows.Select(row => Path.Combine(window.SessionHost.ProfileDirectory, "Delete media " + row.Summary.Id.Thread)).ToArray();
        Require(directories.All(path => File.Exists(Path.Combine(path, "reader.png"))), "Missing owned deletion fixtures.");
        string export = Path.Combine(window.SessionHost.ProfileDirectory, "Existing export");
        Directory.CreateDirectory(export);
        await File.WriteAllTextAsync(Path.Combine(export, "index.html"), "preserve exported copy");
        Window? Dialog() => window.OwnedWindows.FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "archive.delete.dialog");
        void Open()
        {
            Require(table.SelectedItems.Count == 2, "Deletion shortcut lost its selected threads.");
            table.Focus();
            KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, KeyModifiers = KeyModifiers.Shift };
            table.RaiseEvent(key);
            Require(key.Handled, "Archive did not handle Shift+Delete.");
        }
        string[] owned = directories.SelectMany(path => new[] { Path.Combine(path, "reader.png"), Path.Combine(path, ".meowchan-manifest.json") }).ToArray();
        string preview = CountPhrases.ArchiveDeletionSummary(rows.Length, owned.Length, owned.Sum(path => new FileInfo(path).Length));
        const string effects = "Active watches for these threads will stop. The archive entries, their own files, media favorites and tag assignments will be deleted. Watched boards will not add these threads again. Thread favorite marks, reader group memberships, exports and unrelated files are kept.";
        foreach (Key key in new[] { Key.Escape, Key.Enter })
        {
            Open();
            await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true);
            Window question = Dialog()!;
            question.UpdateLayout();
            Button cancel = Find<Button>(question, "archive.delete.cancel");
            Button confirm = Find<Button>(question, "archive.delete.confirm");
            Require(Equals(cancel.Content, "Cancel"), "Confirmation cancel label is not localized.");
            Require(Equals(confirm.Content, "Delete archive entries and files") && confirm.IsEnabled,
                "The enabled confirmation does not name the entries and files.");
            Require(cancel.TranslatePoint(default, confirm) is { X: < 0 }, "Cancel is not left of the destructive action.");
            Require(Find<TextBlock>(question, "archive.delete.summary").Text == preview,
                "Confirmation omitted the preview's counts or size.");
            string details = Find<TextBox>(question, "archive.delete.details").Text!;
            Require(details.Contains("Retained: unrelated.txt", StringComparison.Ordinal) &&
                details.Split(Environment.NewLine).Contains(effects, StringComparer.Ordinal) &&
                details.Contains(Texts.T("bookmarks.retained"), StringComparison.Ordinal),
                "Confirmation omitted deletion effects, retained files or bookmarks.");
            await Program.WaitUntilAsync(() => cancel.IsFocused);
            if (key == Key.Escape) Program.CaptureWindow(question, "smoke-archive-delete-confirmation.png");
            RawKeyboardInput.Press(question, key);
            await Program.WaitUntilAsync(() => Dialog() == null);
            Require(owned.All(File.Exists), key + " deleted local files.");
            await using ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None);
            foreach (ArchiveThreadRow row in rows)
                Require(await reader.ReadThreadAsync(row.Summary.Id) != null, key + " deleted archive records.");
        }
        Open();
        await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true);
        Window dialog = Dialog()!;
        Click(Find<Button>(dialog, "archive.delete.confirm"));
        await Program.WaitUntilAsync(() => Find<Button>(dialog, "archive.delete.cancel").IsEnabled);
        Require(!owned.Any(File.Exists), "Confirmed deletion retained owned files.");
        Require(directories.All(path => File.Exists(Path.Combine(path, "unrelated.txt"))) && File.Exists(Path.Combine(export, "index.html")), "Deletion removed unrelated files or exports.");
        await using (ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None))
        {
            foreach (ArchiveThreadRow row in rows) Require(await reader.ReadThreadAsync(row.Summary.Id) == null, "Confirmed deletion retained archive records.");
            Require((await reader.SelectThreadIdsAsync(new ArchiveThreadQuery(), CancellationToken.None)).Count == 50, "Deletion affected unselected archive threads.");
        }
        Require(File.Exists(Path.Combine(window.SessionHost.ProfileDirectory, "Delete media 1025", "reader.png")), "Deletion affected sibling media.");
        ReaderGroupsSnapshot after = (await window.SessionHost.LoadReaderGroupsAsync()).Snapshot!;
        Require(after.Groups.Single(saved => saved.Id == group.Id).Members.SequenceEqual([groupedThread]), "Deletion changed the saved reader group membership.");
        Require(Find<TextBox>(dialog, "archive.delete.details").Text!.Contains("unrelated.txt"), "Deletion result omitted retained files.");
        Program.CaptureWindow(dialog, "smoke-archive-delete-result.png");
        Click(Find<Button>(dialog, "archive.delete.cancel"));
        await Program.WaitUntilAsync(() => Dialog() == null);
        Console.WriteLine("SMOKE archive permanent deletion: Shift+Delete, native batch confirmation, cancel unchanged, owned media/catalog removal, retained siblings, unrelated files, exports and reader group membership passed.");
    }

    private static T Find<T>(Window dialog, string id) where T : Control => dialog.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
