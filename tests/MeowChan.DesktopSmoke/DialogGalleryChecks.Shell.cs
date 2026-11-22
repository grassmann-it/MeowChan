using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Ursa.Controls;
using NotificationCard = Avalonia.Controls.Notifications.NotificationCard;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Gallery scenes owned by the gallery itself: Settings, toasts and the shell banner, the shell overlays, the clipboard offer,
// the discard confirmation, manual import, the provider post details and the reader groups manager. Each is private
// fixture state, and each ends with Close/Escape/Cancel.
internal static partial class DialogGalleryChecks
{
    static partial void AddExtraScenes(List<(string Group, string Name, Func<MainWindow, Task> Run)> scenes)
    {
        scenes.Add(("Settings", "settings-window", SettingsAsync));
        scenes.Add(("Toasts, banners and overlays", "toasts-banner", ToastsAsync));
        scenes.Add(("Toasts, banners and overlays", "shell-overlays", ShellOverlaysAsync));
        scenes.Add(("Toasts, banners and overlays", "clipboard-offer", ClipboardOfferAsync));
        scenes.Add(("Confirmations", "discard-confirmation", DiscardConfirmationAsync));
        scenes.Add(("Import and media", "manual-import", ManualImportAsync));
        scenes.Add(("Import and media", "media-page-dialogs", MediaPageChecks.CaptureForDialogGalleryAsync));
        scenes.Add(("Import and media", "booru-details", BooruDetailsAsync));
        scenes.Add(("Import and media", "booru-workbench", BooruWorkbenchChecks.CaptureForDialogGalleryAsync));
        scenes.Add(("Reader", "reader-groups", ReaderGroupsAsync));
        scenes.Add(("Reader", "reader-overlays", ReaderPostFilterChecks.CaptureForDialogGalleryAsync));
        scenes.Add(("Gallery viewer", "gallery-viewer", GalleryToolbarLayoutChecks.CaptureForDialogGalleryAsync));
    }

    /// <summary>Default size and, where the window has a minimum, the minimum size; the window is restored afterwards.</summary>
    internal static async Task RecordWindowAsync(Window window, string name, string note = "")
    {
        await DialogFrameChecks.Settle();
        DialogGallery.Record(window, name, "default size", note);
        if (window.MinWidth <= 0 || window.MinHeight <= 0 || double.IsNaN(window.Width) || double.IsNaN(window.Height)) return;
        double width = window.Width, height = window.Height;
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && (Math.Abs(window.ClientSize.Width - window.MinWidth) > 1 || Math.Abs(window.ClientSize.Height - window.MinHeight) > 1))
            await DialogFrameChecks.Settle();
        await DialogFrameChecks.Settle();
        DialogGallery.Record(window, name, "minimum size", note);
        window.Width = width; window.Height = height;
        await DialogFrameChecks.Settle();
    }

    /// <summary>Opens a context menu at <paramref name="origin"/>, renders it as its own surface and closes it again.</summary>
    internal static async Task RecordMenuAsync(ContextMenu menu, Control origin, string name, string note = "")
    {
        menu.Open(origin);
        await Program.WaitUntilAsync(() => menu.IsOpen && menu.Bounds.Width > 0 && menu.Bounds.Height > 0, () => name + " did not open");
        await DialogFrameChecks.Settle();
        DialogGallery.RecordSurface(menu, name, "context menu", note);
        menu.Close();
        await DialogFrameChecks.Settle();
    }

    private static async Task SettingsAsync(MainWindow owner)
    {
        SettingsWindow window = new(new Config());
        Task shown = window.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            await RecordWindowAsync(window, "settings-window", "first sections; nothing is saved");
            window.Editor.OpenSectionNavigation();
            await DialogFrameChecks.Settle();
            DialogGallery.Record(window, "settings-window-section-contents", "default size", "the section contents menu opened");
            window.Editor.GoToSection("settings.downloads");
            await DialogFrameChecks.Settle();
            DialogGallery.Record(window, "settings-window-downloads-section", "default size", "jumped to the Downloads section");
        }
        finally { if (window.IsVisible) window.Close(); await shown; }
    }

    private static async Task ToastsAsync(MainWindow owner)
    {
        Config config = owner.SessionHost.Config;
        bool information = config.ShowInformationNotifications, warning = config.ShowWarningNotifications, error = config.ShowErrorNotifications;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo publish = typeof(MainWindow).GetMethod("ShowNotification", instance)!;
        MethodInfo publishAction = typeof(MainWindow).GetMethod("ShowActionNotification", instance)!;
        string cell = DialogGallery.Theme + "-" + DialogGallery.Language;
        try
        {
            config.ShowInformationNotifications = config.ShowWarningNotifications = config.ShowErrorNotifications = true;
            // The session feedback store records only warnings and errors (SessionFeedbackStore.Record): information, success and
            // action toasts carry no Entry. Each toast is therefore found by its own message text, and the recorded kinds
            // are additionally pinned to their gallery operation.
            FeedbackToastContent[] Toasts() => owner.GetVisualDescendants().OfType<NotificationCard>()
                .Where(card => !card.IsClosing && !card.IsClosed).Select(card => card.Content).OfType<FeedbackToastContent>().ToArray();
            // The window keeps at most three cards, so each kind is published, captured and dismissed on its own.
            (string Kind, string Message, bool Recorded, Action Show)[] kinds =
            [
                ("info", "Added 3 threads to the watch list.", false,
                    () => publish.Invoke(owner, [Texts.T("notification.session"), "Added 3 threads to the watch list.", NotificationType.Information, "info-" + cell, "gallery.toast.info"])),
                ("success", "Saved 12 files to the archive.", false,
                    () => publish.Invoke(owner, [Texts.T("notification.session"), "Saved 12 files to the archive.", NotificationType.Success, "success-" + cell, "gallery.toast.success"])),
                ("warning", "The board is rate limited; the next check is delayed.", true,
                    () => publish.Invoke(owner, [Texts.T("notification.session"), "The board is rate limited; the next check is delayed.", NotificationType.Warning, "warning-" + cell, "gallery.toast.warning"])),
                ("error", "The destination folder is not writable.", true,
                    () => publish.Invoke(owner, [Texts.T("notification.error"), "The destination folder is not writable.", NotificationType.Error, "error-" + cell, "gallery.toast.error"])),
                ("action", "Removed the watch for /g/ thread 7400.", false,
                    () => publishAction.Invoke(owner, [Texts.T("notification.session"), "Removed the watch for /g/ thread 7400.", NotificationType.Information,
                        new[] { new FeedbackToastAction(Texts.T("action.cancel"), () => Task.CompletedTask) }, "action-" + cell, "gallery.toast.action"])),
            ];
            foreach ((string kind, string message, bool recorded, Action show) in kinds)
            {
                string? violation = null; // a contract violation fails the scene after the toast is closed; it is not a mere gap
                show();
                try
                {
                    await Program.WaitUntilAsync(() => Toasts().Any(toast => toast.Message == message && toast.IsEffectivelyVisible && toast.Bounds.Width > 0),
                        () => "toast " + kind + " count " + Toasts().Length);
                    await DialogFrameChecks.Settle();
                    FeedbackToastContent toast = Toasts().First(candidate => candidate.Message == message);
                    if (recorded ? toast.Entry?.Operation != "gallery.toast." + kind : toast.Entry != null)
                        violation = "toast " + kind + " has the wrong feedback record: " + (toast.Entry?.Operation ?? "none");
                    else if ((kind == "action") != (toast.ActionButton != null))
                        violation = "toast " + kind + (kind == "action" ? " lacks its action button." : " shows an action button.");
                    NotificationCard card = toast.GetVisualAncestors().OfType<NotificationCard>().First();
                    DialogGallery.RecordSurface(card, "toast-" + kind, "toast", toast.Title);
                    if (kind == "error")
                    {
                        DialogGallery.Record(owner, "toasts-in-main-window", "main window", "error toast at the corner, banner below the menu");
                        MeowChan.Desktop.Controls.WorkspaceBanner banner = owner.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("ShellActionErrorBanner")!;
                        if (banner.IsEffectivelyVisible && banner.Bounds.Width > 0) DialogGallery.RecordSurface(banner, "shell-error-banner", "banner", "the last failure stays visible below the menu until closed");
                        else DialogGallery.Skip("shell-error-banner", "the shell banner was not visible");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DialogGallery.Skip("toast-" + kind, ex.Message);
                }
                // A recorded kind is dismissed through the shell (it also acknowledges its banner); the others only close their card.
                foreach (FeedbackToastContent toast in Toasts().Where(candidate => candidate.Message == message))
                {
                    if (toast.Entry != null) owner.DismissShellFeedback(toast.Entry);
                    else toast.GetVisualAncestors().OfType<NotificationCard>().FirstOrDefault()?.Close();
                }
                await DialogFrameChecks.Settle();
                if (violation != null) throw new InvalidOperationException(violation);
            }
        }
        finally
        {
            config.ShowInformationNotifications = information; config.ShowWarningNotifications = warning; config.ShowErrorNotifications = error;
        }
    }

    private static async Task ShellOverlaysAsync(MainWindow owner)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo showHelp = typeof(MainWindow).GetMethod("ShowShellHelp", instance)!;
        showHelp.Invoke(owner, [null]); // the optional ShortcutHelpContext: null means the current area
        try
        {
            // F1 opens the one shortcuts window (KS2), no longer an overlay of the main window.
            await Program.WaitUntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(owner) is { IsVisible: true, Bounds.Width: > 0 });
            await DialogFrameChecks.Settle();
            DialogGallery.Record(MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(owner)!, "shell-shortcut-help", "shortcuts window", "F1 keyboard shortcuts window");
        }
        finally { MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(owner)?.CloseWithoutAsking(); }

        MethodInfo drop = typeof(MainWindow).GetMethod("ShowDropOverlay", instance)!;
        Grid overlay = owner.FindControl<Grid>("DropOverlay")!;
        foreach ((DroppedContentKind kind, int files, string name) in new[] { (DroppedContentKind.Links, 0, "links"), (DroppedContentKind.Files, 3, "files"), (DroppedContentKind.Empty, 0, "nothing-usable") })
        {
            drop.Invoke(owner, [kind, files]);
            try
            {
                await Program.WaitUntilAsync(() => overlay.IsVisible && overlay.Bounds.Width > 0);
                await DialogFrameChecks.Settle();
                DialogGallery.Record(owner, "drop-overlay-" + name, "main window", "drag-and-drop target overlay");
            }
            finally { drop.Invoke(owner, [null, 0]); }
        }
    }

    private static async Task ClipboardOfferAsync(MainWindow owner)
    {
        // The offer panel filled with its production strings; the clipboard is never read.
        Border panel = owner.FindControl<Border>("ClipboardSuggestionPanel")!;
        TextBlock title = owner.FindControl<TextBlock>("ClipboardSuggestionTitle")!;
        TextBox urls = owner.FindControl<TextBox>("ClipboardSuggestionUrls")!;
        Button accept = owner.FindControl<Button>("ClipboardSuggestionAccept")!, dismiss = owner.FindControl<Button>("ClipboardSuggestionDismiss")!;
        (bool visible, string? titleText, string? urlText, bool urlVisible, object? acceptContent, object? dismissContent, bool acceptVisible) =
            (panel.IsVisible, title.Text, urls.Text, urls.IsVisible, accept.Content, dismiss.Content, accept.IsVisible);
        try
        {
            title.Text = Texts.T("clipboard.monitor.found", 2);
            urls.Text = "https://boards.4chan.org/g/thread/7400" + Environment.NewLine + "https://boards.4chan.org/g/thread/7401";
            urls.IsVisible = true; accept.IsVisible = true;
            accept.Content = Texts.T("clipboard.monitor.accept"); dismiss.Content = Texts.T("clipboard.monitor.dismiss");
            panel.IsVisible = true;
            await DialogFrameChecks.Settle();
            DialogGallery.RecordSurface(panel, "clipboard-offer-panel", "panel", "offer after a copy of two thread links; the panel is filled directly, the clipboard is never read");
        }
        finally
        {
            panel.IsVisible = visible; title.Text = titleText; urls.Text = urlText; urls.IsVisible = urlVisible;
            accept.Content = acceptContent; dismiss.Content = dismissContent; accept.IsVisible = acceptVisible;
            await DialogFrameChecks.Settle();
        }
    }

    private static async Task DiscardConfirmationAsync(MainWindow owner)
    {
        Button trigger = new() { Content = Texts.T("rules.discard"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40) };
        Grid content = new();
        content.Children.Add(trigger);
        Window host = new() { Width = 720, Height = 360, Title = "Discard confirmation fixture", Content = content, ShowInTaskbar = false };
        host.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => host.IsVisible && trigger.Bounds.Width > 0);
            DraftDiscardConfirmation confirmation = new(trigger);
            bool confirmed = false;
            confirmation.Open("Example plugin", () => confirmed = true);
            await Program.WaitUntilAsync(() => confirmation.IsOpen);
            Popup? popup = null;
            await Program.WaitUntilAsync(() =>
            {
                host.UpdateLayout();
                popup = host.GetVisualDescendants().OfType<PopConfirm>().FirstOrDefault()?.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == PopConfirm.PART_Popup);
                return popup is { IsOpen: true, Child: Control { Bounds.Width: > 0 } };
            }, () => "discard confirmation did not open");
            await DialogFrameChecks.Settle();
            DialogGallery.RecordSurface((Control)popup!.Child!, "discard-popconfirm", "popup", "destructive-action confirmation anchored to its button; Keep editing is the default");
            DialogGallery.Record(host, "discard-popconfirm-in-context", "default size", "same confirmation over its host window");
            confirmation.Close(true);
            Require(!confirmed, "The discard confirmation fixture was confirmed.");
        }
        finally { host.Close(); }
    }

    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    private static async Task ManualImportAsync(MainWindow owner)
    {
        string source = TemporaryWorkspacePaths.CreateDirectory("meowchan-gallery-import-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "holiday-01.png"), SmallPng);
            await File.WriteAllBytesAsync(Path.Combine(source, "holiday-02.png"), [.. SmallPng, 0x42]);
            await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "not media");
            owner.OpenManualImportDialog([source]);
            await Program.WaitUntilAsync(() => owner.ManualImportWindow is { IsVisible: true } dialog && dialog.Session.Stage == ManualImportStage.Ready,
                () => "import dialog not ready: " + owner.ManualImportWindow?.Session.Stage);
            ManualImportDialog import = owner.ManualImportWindow!;
            await RecordWindowAsync(import, "manual-import-dialog", "checked fixture folder, nothing copied");
            import.Close();
            await Program.WaitUntilAsync(() => owner.ManualImportWindow == null);
        }
        finally
        {
            if (owner.ManualImportWindow is { } open) open.Close();
            try { Directory.Delete(source, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task BooruDetailsAsync(MainWindow owner)
    {
        DateTimeOffset seen = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        BooruMediaDetails details = new("12345", new Uri("https://danbooru.donmai.us/posts/12345"), "asset-12345", true, BooruPostClaimRating.General, "g",
            42, 17, seen.AddDays(-30), false, false, "https://example.org/artwork/12345",
            [new("sky", BooruPostClaimTagCategory.Artist), new("blue_eyes", BooruPostClaimTagCategory.General), new("cat", BooruPostClaimTagCategory.General),
                new("original", BooruPostClaimTagCategory.Copyright), new("highres", BooruPostClaimTagCategory.Meta)],
            [new(BooruPostClaimMediaRole.Original, BooruMediaVariantState.Stored, "png", 245760, 1600, 1000),
                new(BooruPostClaimMediaRole.Sample, BooruMediaVariantState.ProviderClaimOnly, "jpg", 90112, 850, 531),
                new(BooruPostClaimMediaRole.Preview, BooruMediaVariantState.ProviderClaimOnly, "jpg", 8192, 180, 112)],
            [new(BooruFindingKind.Watcher, "w-active", "cat blue_eyes", seen, BooruMediaWatcherStatus.Observing, null),
                new(BooruFindingKind.Search, null, "cat", seen.AddDays(-1), BooruMediaWatcherStatus.NotWatcher, null)],
            seen, seen.AddDays(2));
        BooruMediaDetailsDialog dialog = new("12345", ["first.png", "240 KiB", "SHA-256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"],
            details, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => (true, null));
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => dialog.IsVisible);
            await RecordWindowAsync(dialog, "booru-post-details-dialog", "synthetic Danbooru post claims; open/copy actions are no-ops");
        }
        finally { if (dialog.IsVisible) dialog.Close(); await shown; }
    }

    private static async Task ReaderGroupsAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-gallery-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId a = new("local", "b", "1"), b = a with { Thread = "2" }, c = a with { Thread = "3" };
        try
        {
            new Config { SaveUrlsOnExit = false }.SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                foreach (ResourceId id in new[] { a, b, c })
                    await catalog.CaptureAsync(new(id, "https://example.test/b/thread/" + id.Thread, "Group fixture " + id.Thread,
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                        [new CapturedPost(id.Thread!, 0, PostFieldPresence.Html, Html: "Saved local post " + id.Thread)]));
            await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            ReaderGroupsDialog dialog = new(host);
            Task<ReaderGroupReadRequest?> completion = dialog.ShowDialog<ReaderGroupReadRequest?>(owner);
            try
            {
                await Program.WaitUntilAsync(() => dialog.CanExecute("reader-groups.create"));
                await RecordWindowAsync(dialog, "reader-groups-dialog-empty", "no group saved yet");
                await dialog.ExecuteCommandAsync("reader-groups.create");
                dialog.FindControl<TextBox>("GroupName")!.Text = "Weekend reading";
                dialog.FindControl<TextBox>("GroupSearch")!.Text = "Group fixture";
                ListBox candidates = dialog.FindControl<ListBox>("GroupCandidates")!;
                await Program.WaitUntilAsync(() => candidates.ItemCount == 3);
                candidates.SelectedItems!.Add(candidates.Items[2]);
                candidates.SelectedItems!.Add(candidates.Items[0]);
                await dialog.ExecuteCommandAsync("reader-groups.add-members");
                // Closing with the unsaved draft opens the discard prompt; capture it and keep editing.
                dialog.Close();
                try
                {
                    await Program.WaitUntilAsync(() => dialog.OwnedWindows.Any(window => window.IsVisible && window.Bounds.Width > 0), () => "discard prompt not shown");
                    await DialogFrameChecks.Settle();
                    Window prompt = dialog.OwnedWindows.First(window => window.IsVisible);
                    await RecordWindowAsync(prompt, "reader-groups-discard-prompt", "unsaved group draft; Keep editing is the default");
                    prompt.Close(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { DialogGallery.Skip("reader-groups-discard-prompt", ex.Message); }
                await Program.WaitUntilAsync(() => dialog.IsVisible && dialog.OwnedWindows.Count == 0, () => "prompt did not close");
                await dialog.ExecuteCommandAsync("reader-groups.save");
                await Program.WaitUntilAsync(() => !dialog.CanExecute("reader-groups.save"), () => "group was not saved");
                await DialogFrameChecks.Settle();
                await RecordWindowAsync(dialog, "reader-groups-dialog", "one saved group with two ordered members");
            }
            finally
            {
                // The saved draft closes plainly; a dirty one would open the prompt again, so bypass it.
                typeof(ReaderGroupsDialog).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(dialog, true);
                foreach (Window stray in dialog.OwnedWindows.ToArray()) stray.Close(false);
                if (dialog.IsVisible) dialog.Close();
                await completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { try { Directory.Delete(profile, recursive: true); } catch (IOException) { } }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
