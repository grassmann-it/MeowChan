using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_LOCAL_MEDIA_TAGS_ONLY. AP-01b in the real window: a loose legacy file (old manifest, no
// post binding) is tagged through the archive's saved-media menu, the real dialog, host and store; a second shell over
// the same private profile shows the same tag in the archive dialog and on the gallery toolbar. Managed input only.
internal static class LocalMediaTagChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId Loose = new("local-media-tag-fixture", "g", "77");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "local-media-tag-fixture");
        string media = Path.Combine(profile, "media", "loose");
        try
        {
            Texts.SetLanguage("en");
            await SeedAsync(profile, media);
            LocalMediaTagTarget target = await FirstShellAsync(owner, profile);
            await SecondShellAsync(owner, profile, target);
            Console.WriteLine("SMOKE local media tags [en]: loose legacy file tagged via archive menu -> dialog -> host/store; equal-bytes twin and thread untouched; "
                + "reopened shell shows the same occurrence, revision and tag in the archive dialog and the gallery toolbar.");
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task SeedAsync(string profile, string directory)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Simple" }
            .SaveConfig(profile);
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        foreach (string file in new[] { "loose.png", "twin.png" })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, file), Png);
            entries[file] = new { MediaIdentifier = "m1", Variant = "original", SourceUrl = "https://offline.invalid/" + file, FileName = file,
                OriginalFileName = file, Length = Png.Length, Md5 = Convert.ToBase64String(MD5.HashData(Png)),
                Sha256 = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant() };
        }
        // An old version 1 manifest: files only, no attachment bindings.
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Loose, "https://offline.invalid/77", "Loose legacy media",
            ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, [new CapturedPost("77", 0, PostFieldPresence.All, Html: "No attachments.")]));
    }

    private static async Task<(DesktopSessionHost Host, MainWindow Shell, ArchivePage Page)> OpenAsync(MainWindow owner, string profile)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(host) { Width = 1300, Height = 900 };
        shell.Show(owner);
        await UntilAsync(() => host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
        SmokeNavigation.GoTo(shell, MainWindow.WorkspacePage.Archive);
        ArchivePage page = shell.ArchivePage;
        await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Archive && !page.IsBusy);
        await page.LoadThreadAsync(Loose).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        page.MediaTilesPreference = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return (host, shell, page);
    }

    private static async Task CloseAsync(DesktopSessionHost host, MainWindow shell)
    {
        foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Deadline);
        await UntilAsync(() => !shell.IsVisible);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Require(host.IsProfileReleased, "The fixture host did not release its private profile.");
    }

    private static async Task<MenuItem> TagMenuAsync(ArchivePage page, string file)
    {
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        object row = tiles.Items.Cast<object>().Single(item => item is ArchiveAssetRow asset && asset.Asset.RequestedFileName == file);
        tiles.SelectedItem = row;
        tiles.Focus();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        tiles.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await UntilAsync(() => page.ArchiveMediaMenu?.IsOpen == true);
        return page.ArchiveMediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.rating.edit");
    }

    private static async Task<MediaTagDialog> OpenTagsAsync(MainWindow shell, ArchivePage page, string file)
    {
        MenuItem edit = await TagMenuAsync(page, file);
        Require(edit.IsEnabled && edit.Command!.CanExecute(null), "A loose legacy file must offer an enabled media-tag action.");
        edit.Command!.Execute(null);
        page.ArchiveMediaMenu!.Close();
        // TP3 (E5): the entry opens the tag popover; the dialog is "Advanced…" inside it.
        await UntilAsync(() => shell.ArchiveTagPopover is { IsOpen: true, Surface: not null });
        shell.ArchiveTagPopover!.Surface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => shell.GalleryMediaTagWindow?.IsVisible == true);
        MediaTagDialog dialog = shell.GalleryMediaTagWindow!;
        Require(dialog.Session.IsLocal && shell.OwnedWindows.Contains(dialog), "The dialog is not a local-file tag dialog owned by the shell.");
        return dialog;
    }

    private static async Task CloseTagsAsync(MainWindow shell, MediaTagDialog dialog)
    {
        // An open suggestion list takes the first Escape and only the next one closes the dialog (MediaTagDialog.OnKey, DialogEscape).
        if (dialog.Input.IsDropDownOpen)
        {
            dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => !dialog.Input.IsDropDownOpen);
            Require(shell.GalleryMediaTagWindow != null, "The first Escape closed the dialog instead of the suggestion list.");
        }
        dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await UntilAsync(() => shell.GalleryMediaTagWindow == null);
    }

    private static async Task<LocalMediaTagTarget> FirstShellAsync(MainWindow owner, string profile)
    {
        (DesktopSessionHost host, MainWindow shell, ArchivePage page) = await OpenAsync(owner, profile);
        Exception? failure = null;
        try
        {
            MediaTagDialog dialog = await OpenTagsAsync(shell, page, "loose.png");
            Require(dialog.Session.Assigned.Count == 0, "A never tagged loose file starts with tags.");
            dialog.Input.Text = "loose_tag";
            dialog.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !dialog.Session.Busy);
            Require(dialog.Session.MessageKey == null && dialog.Session.Assigned.Select(tag => tag.Name.Display).SequenceEqual(new[] { "loose_tag" }),
                "The loose file was not tagged: " + dialog.Session.MessageKey);
            LocalMediaTagTarget target = ((MediaTagSubject.Local)dialog.Session.Subject).Expected
                ?? throw new InvalidOperationException("The dialog did not freeze the new registration.");
            Program.CaptureWindow(dialog, "smoke-local-media-tags.png");
            await CloseTagsAsync(shell, dialog);

            PrivateTagStoreSnapshot stored = host.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.FormatVersion == 2 && stored.LocalAssignments.Count == 1 && stored.TagsOf(target).Single().Name.Display == "loose_tag"
                && stored.TagsOf(Loose).Count == 0 && stored.MediaAssignments.Count == 0,
                "The store must hold exactly one local media assignment and no thread or bound tag.");
            MediaTagDialog twin = await OpenTagsAsync(shell, page, "twin.png");
            Require(twin.Session.Assigned.Count == 0, "The equal-bytes twin received the tag (hash fan-out).");
            await CloseTagsAsync(shell, twin);
            return target;
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, () => CloseAsync(host, shell),
                exception => Console.Error.WriteLine("SMOKE cleanup failed in LocalMediaTagChecks (first shell): " + exception));
        }
    }

    private static async Task SecondShellAsync(MainWindow owner, string profile, LocalMediaTagTarget target)
    {
        (DesktopSessionHost host, MainWindow shell, ArchivePage page) = await OpenAsync(owner, profile);
        Exception? failure = null;
        try
        {
            MediaTagDialog dialog = await OpenTagsAsync(shell, page, "loose.png");
            Require(((MediaTagSubject.Local)dialog.Session.Subject).Expected == target, "The reopened file resolved to another occurrence or revision.");
            Require(dialog.Session.Assigned.Select(tag => tag.Name.Display).SequenceEqual(new[] { "loose_tag" }), "The tag did not survive reopening.");
            await CloseTagsAsync(shell, dialog);

            await TagMenuAsync(page, "loose.png");
            MenuItem gallery = page.ArchiveMediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.open");
            Require(gallery.IsEnabled, "The loose file cannot be opened in the gallery.");
            gallery.Command!.Execute(null);
            page.ArchiveMediaMenu.Close();
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => galleryHost.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
            // On the toolbar the live count is the button's accessible name and tooltip; its visible text stays "Tags" (GalleryViewer.UpdateTagsButton).
            try { await UntilAsync(() => viewer.TagsButton.IsEffectivelyEnabled && Equals(Avalonia.Automation.AutomationProperties.GetName(viewer.TagsButton), Texts.T("tags.media.buttonCount", 1))); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("Gallery tags button: enabled=" + viewer.TagsButton.IsEffectivelyEnabled + " content="
                    + Avalonia.Automation.AutomationProperties.GetName(viewer.TagsButton) + " tip=" + ToolTip.GetTip(viewer.TagsButton));
            }
            // The shell's own shutdown below closes the gallery and drains its cleanup.
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, () => CloseAsync(host, shell),
                exception => Console.Error.WriteLine("SMOKE cleanup failed in LocalMediaTagChecks (second shell): " + exception));
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Local media tag UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
