using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_MANUAL_IMPORT_ONLY. Manual import phase 1 in a real second shell over a private fixture
// profile: the import dialog opened prefilled (as by a drop) checks a fixture folder, imports the new files with a tag,
// links the equal-bytes twin, and "Show batch" lands on the Media page filtered to the batch with tagged loose tiles.
// Managed input only; no OS file picker, no native XDND/OLE/Cocoa drag.
internal static class ManualImportChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
    // Other bytes, still a decodable image: decoders ignore data after the end chunk.
    private static readonly byte[] OtherPng = Png.Concat(new byte[] { 0x42 }).ToArray();

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "manual-import-fixture");
        string downloads = Path.Combine(profile, "downloads");
        string source = Path.Combine(profile, "source folder");
        try
        {
            Texts.SetLanguage("en");
            new Config { DownloadPath = downloads, DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false,
                WorkspaceMode = "Simple" }.SaveConfig(profile);
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "first.png"), Png);
            await File.WriteAllBytesAsync(Path.Combine(source, "second.png"), OtherPng);
            await File.WriteAllBytesAsync(Path.Combine(source, "twin.png"), Png);
            await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "not media");
            await RunShellAsync(owner, profile, source);
            Console.WriteLine("SMOKE manual import [en]: prefilled dialog checked a fixture folder (2 new, 1 linked twin, 1 unsupported), copied 2 files with a tag; "
                + "Show batch opened the Media page filtered to the batch with 2 tagged loose tiles; sources unchanged; managed input only.");
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task RunShellAsync(MainWindow owner, string profile, string source)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        try
        {
            await using (MainWindow shell = new(host) { Width = 1300, Height = 900 })
            {
                shell.Show(owner);
                Exception? failure = null;
                try
                {
                    await UntilAsync(() => host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
                    shell.OpenManualImportDialog(new[] { source });
                    await UntilAsync(() => shell.ManualImportWindow is { IsVisible: true } dialog && dialog.Session.Stage == ManualImportStage.Ready);
                    ManualImportDialog import = shell.ManualImportWindow!;
                    Require((import.Session.NewCount, import.Session.LinkedCount, import.Session.UnsupportedCount) == (2, 1, 1),
                        "The check did not find 2 new, 1 linked and 1 unsupported file: " + import.Counts.Text);
                    Require(import.ImportButton.IsEnabled && Equals(import.ImportButton.Content, Texts.Count("import.start", 2)), "The import button does not name 2 files.");
                    import.TagInput.Text = "imported_fixture";
                    import.TagAddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(import.Session.Tags.SequenceEqual(new[] { "imported_fixture" }), "The batch tag was not taken.");
                    import.BatchNameBox.Text = "Fixture batch";
                    Program.CaptureWindow(import, "smoke-manual-import-preview.png");
                    import.ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => import.Session.Stage == ManualImportStage.Done);
                    ManualImportOutcome outcome = import.Session.Outcome!;
                    Require(outcome.TagsApplied && outcome.Batch.Count(ManualImportItemState.Committed) == 2 && outcome.Batch.Count(ManualImportItemState.Duplicate) == 1,
                        "The import did not copy 2, link 1 and apply the tag: " + import.Status.Text);
                    Require(import.ShowBatchButton.IsVisible, "The summary does not offer Show batch.");
                    Program.CaptureWindow(import, "smoke-manual-import-done.png");
                    import.ShowBatchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => shell.ManualImportWindow == null && shell.SelectedPage == MainWindow.WorkspacePage.Media && shell.MediaPageForTests != null);
                    MediaPage page = shell.MediaPageForTests!;
                    await UntilAsync(() => page.BuildTask.IsCompleted && page.ShownEntries.Count == 2);
                    Require(page.ShownEntries.All(entry => entry.ImportItemId != null && entry.Thread == outcome.Batch.Thread && entry.LocalSlot != null),
                        "The Media page does not show exactly the batch's imported files.");
                    PrivateTagStoreSnapshot tags = host.GetPrivateTagSnapshot().Snapshot!;
                    Require(page.ShownEntries.All(entry => MediaTagSubjects.Of(entry) is MediaTagSubject.Local local && tags.ResolveLocal(local.Observation).IsRegistered),
                        "The imported tiles are not tagged loose files.");
                    Require(File.ReadAllBytes(Path.Combine(source, "first.png")).SequenceEqual(Png) && File.Exists(Path.Combine(source, "twin.png")),
                        "A source file changed.");
                    Program.CaptureWindow(shell, "smoke-manual-import-media.png");
                }
                catch (Exception thrown) { failure = thrown; throw; }
                finally
                {
                    await SmokeCleanup.RunAsync(failure, () => CloseAsync(shell),
                        exception => Console.Error.WriteLine("SMOKE cleanup failed in ManualImportChecks: " + exception));
                }
            }
            Require(host.IsProfileReleased, "The fixture host did not release its private profile.");
        }
        finally
        {
            if (!host.IsProfileReleased) await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Deadline);
        await UntilAsync(() => !shell.IsVisible);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Manual import UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
