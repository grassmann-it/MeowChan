using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_FOLDER_SOURCE_ONLY. PR2/PR3 (read-only folder sources) in a real second shell over a private
// fixture profile: Settings › Storage and limits › Media folders › "Add folder…" (moved from the Plugins page 14.11.2026) refuses the download folder, adds a fixture folder and scans it; the Media
// page shows its three files under "Folders" (its source label) without hashing them; the gallery opens an unhashed file (hashed on view) and
// favorites it; "Remove…" asks inline and removes the source. The fixture folder is byte- and timestamp-identical afterwards.
// Managed input only, no OS folder picker.
internal static class FolderSourceChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string root = Path.Combine(owner.SessionHost.ProfileDirectory, "folder-source-fixture");
        string profile = Path.Combine(root, "profile"), downloads = Path.Combine(root, "downloads"), media = Path.Combine(root, "my photos");
        try
        {
            Texts.SetLanguage("en");
            new Config { DownloadPath = downloads, DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false,
                WorkspaceMode = "Simple", FolderSourceBackgroundHashing = false }.SaveConfig(profile);
            Directory.CreateDirectory(Path.Combine(media, "trip"));
            Directory.CreateDirectory(downloads);
            await File.WriteAllBytesAsync(Path.Combine(media, "first.png"), Png);
            await File.WriteAllBytesAsync(Path.Combine(media, "second.png"), Png.Concat(new byte[] { 0x42 }).ToArray());
            await File.WriteAllBytesAsync(Path.Combine(media, "trip", "third.png"), Png.Concat(new byte[] { 0x43 }).ToArray());
            await File.WriteAllTextAsync(Path.Combine(media, "notes.txt"), "not media");
            IReadOnlyList<string> before = Fingerprint(media);
            await RunShellAsync(owner, profile, downloads, media);
            Require(Fingerprint(media).SequenceEqual(before), "The fixture folder changed.");
            Console.WriteLine("SMOKE folder source [en]: Add folder refused the download folder, added and scanned a fixture folder (3 media, 1 text file skipped); "
                + "the Media page showed 3 unhashed folder tiles; the gallery hashed one on view and favorited it; Remove asked inline; folder unchanged; managed input only.");
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task RunShellAsync(MainWindow owner, string profile, string downloads, string media)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(host) { Width = 1300, Height = 900 };
        shell.Show(owner);
        Exception? failure = null;
        try
        {
            await UntilAsync(() => host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            await OpenMediaFoldersAsync(shell);
            FolderSourcesPanel panel = shell.SettingsEditor.FolderSourcesSection!;
            await UntilAsync(() => panel.IsVisible && panel.AddButton.IsEnabled);
            Require(shell.SettingsEditor.FindControl<TextBlock>("MediaFoldersHeading")!.Text == Texts.T("folderSource.section.title"), "The Media folders heading is missing.");

            // "Add folder…" with the download folder is refused with its own sentence; the fixture folder is added.
            string? refusal = null;
            panel.ShowDialogAsync = async dialog =>
            {
                dialog.Show(shell);
                await UntilAsync(() => dialog.IsVisible);
                dialog.FolderBox.Text = downloads;
                await dialog.ConfirmAsync();
                refusal = dialog.ErrorText.Text;
                dialog.FolderBox.Text = media;
                Require(dialog.NameBox.Text == "my photos", "The name does not follow the folder: " + dialog.NameBox.Text);
                Program.CaptureWindow(dialog, "smoke-folder-source-dialog.png");
                await dialog.ConfirmAsync();
                await UntilAsync(() => !dialog.IsVisible);
            };
            panel.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => host.ListFolderSources() is [{ Scanning: false, Counts.Files: 3 }]);
            Require(refusal == Texts.T("folderSource.error.downloads"), "The download folder was not refused: " + refusal);
            FolderSourceRoot root = host.ListFolderSources().Single().Root;
            string id = FolderSourceIdentity.Format(root.Id);
            await UntilAsync(() => panel.Rows.Children.Count == 1 && Find<TextBlock>(panel, "FolderSourceCounts_" + id)?.Text == Texts.T("folderSource.row.counts", "3", "0", "0"));
            Program.CaptureWindow(shell, "smoke-folder-source-settings.png");

            // The Media page lists the three files as unhashed folder tiles of this source.
            shell.FindControl<MenuItem>("MediaMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Media && shell.MediaPageForTests != null);
            MediaPage page = shell.MediaPageForTests!;
            await UntilAsync(() => page.BuildTask.IsCompleted && page.ShownEntries.Count(entry => entry.FolderItemId != null) == 3);
            Require(page.ShownEntries.All(entry => entry.Thread == root.Thread && entry.Sha256 == null && entry.LocalSlot != null),
                "The Media page does not show exactly the folder's unhashed loose files.");
            Program.CaptureWindow(shell, "smoke-folder-source-media.png");

            // The gallery opens an unhashed file (it is hashed on view) and favorites it.
            Gallery.MediaLibraryEntry[] shown = page.ShownEntries.ToArray();
            await page.OpenGallery!(shown, 0);
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            Require(host.ListFolderSources().Single().Counts.Hashed >= 1, "Opening the gallery did not hash the opened file.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryFavorite");
            await UntilAsync(() => host.GetFavorites().Count == 1);
            Program.CaptureWindow(shell, "smoke-folder-source-gallery.png");
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await UntilAsync(() => !galleryHost.IsVisible);

            // "Remove…" asks inline; confirming removes the source and keeps the favorite.
            await OpenMediaFoldersAsync(shell);
            await UntilAsync(() => Find<Button>(panel, "FolderSourceRemove_" + id) != null);
            Find<Button>(panel, "FolderSourceRemove_" + id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Find<Button>(panel, "FolderSourceRemoveConfirm") != null);
            Require(Find<TextBlock>(panel, "FolderSourceRemoveAsk")?.Text == Texts.T("folderSource.remove.ask", "my photos"), "Remove does not ask with the folder's name.");
            Find<Button>(panel, "FolderSourceRemoveConfirm")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => host.ListFolderSources().Count == 0 && panel.Rows.Children.Count == 0);
            Require(host.GetFavorites().Count == 1, "Removing the source dropped the favorite.");
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, () => CloseAsync(host, shell),
                exception => Console.Error.WriteLine("SMOKE cleanup failed in FolderSourceChecks: " + exception));
        }
    }

    // The Settings page on its Storage and limits category, where the Media folders group lives.
    private static async Task OpenMediaFoldersAsync(MainWindow shell)
    {
        shell.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Settings);
        await SettingsSectionChecks.OpenAsync(shell.SettingsEditor, "settings.worker-budgets");
        await UntilAsync(() => shell.SettingsEditor.FolderSourcesSection is { IsEffectivelyVisible: true });
    }

    private static T? Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private static IReadOnlyList<string> Fingerprint(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(entry => File.Exists(entry)
                ? Path.GetRelativePath(folder, entry) + " " + new FileInfo(entry).Length + " " + File.GetLastWriteTimeUtc(entry).Ticks + " "
                  + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))
                : Path.GetRelativePath(folder, entry) + "/ " + Directory.GetLastWriteTimeUtc(entry).Ticks).ToArray();

    private static async Task CloseAsync(DesktopSessionHost host, MainWindow shell)
    {
        foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Deadline);
        await UntilAsync(() => !shell.IsVisible);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Require(host.IsProfileReleased, "The fixture host did not release its private profile.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Folder source UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
