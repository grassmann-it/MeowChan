using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// UX103 P7 group "collections": post bookmarks (overview, single-post editor, discard question),
// saved archive searches (list, editor, unreadable store), tag manager and media tags.
// Private fixtures only; the existing feature smokes keep their own behavior coverage.
internal static partial class DialogFrameChecks
{
    private static partial async Task RunCollectionsAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-frame-collections-" + Guid.NewGuid().ToString("N"));
        string tags = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-frame-tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); Directory.CreateDirectory(tags);
        DesktopSessionHost? host = null;
        try
        {
            var key = new PostBookmarkKey(new ResourceId("4chan", "g", "7400"), "7401");
            new Config { DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(key.Thread, "https://offline.invalid/g/thread/7400",
                    "Dialog frame fixture", ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "media"),
                    [new CapturedPost(key.Post, 0, PostFieldPresence.Html, Html: "<p>Exact local post.</p>")]));
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            await host.SavePostBookmarkAsync(key, "original note", 0, false);
            await PostBookmarksFrameAsync(owner, host, key);
            await SavedSearchesFrameAsync(owner, host, profile);
            await TagDialogsFrameAsync(owner, tags);
        }
        finally
        {
            if (host != null) await host.DisposeAsync();
            try { Directory.Delete(profile, true); } catch (IOException) { }
            try { Directory.Delete(tags, true); } catch (IOException) { }
        }
    }

    private static async Task PostBookmarksFrameAsync(MainWindow owner, DesktopSessionHost host, PostBookmarkKey key)
    {
        PostBookmarksDialog? dialog = null;
        try
        {
            dialog = new PostBookmarksDialog(host, (_, _) => Task.FromResult(false));
            dialog.Show(owner); await dialog.InitializeAsync();
            Button open = dialog.FindControl<Button>("OpenTarget")!;
            TextBox note = dialog.FindControl<TextBox>("Note")!;
            TextBlock status = dialog.FindControl<TextBlock>("Status")!;
            await Program.WaitUntilAsync(() => open.IsEnabled && status.Text == Texts.T("bookmarks.available", "Dialog frame fixture"));
            Require(Find<Control>(dialog, "DialogFrameErrorStrip") is { IsVisible: false }, "post-bookmarks: error strip shown without an error.");
            await VerifyFrameAsync(dialog, "post-bookmarks overview", "collections-post-bookmarks");
            PostBookmarksDialog overview = dialog;
            await CollectionsAtMinimumAsync(overview, () => VerifyFrameAsync(overview, "post-bookmarks overview minimum", "collections-post-bookmarks-min"));

            // Real validation failure: the draft stays, Status keeps its text, the strip shows it.
            note.Text = "invalid \u0001 draft";
            await Program.WaitUntilAsync(() => status.Text == Texts.T("bookmarks.invalidNote"));
            await VerifyErrorStripAsync(dialog, "post-bookmarks invalid note", Texts.T("bookmarks.invalidNote"));
            Require(Find<Control>(dialog, "StatusHost") is { IsVisible: false }, "post-bookmarks: error repeated in the footer status line.");
            Capture(dialog, "collections-post-bookmarks-error");

            // Discard question on Close: framed itself; Escape keeps editing as before.
            // The validation message stays until the next status, as before; the draft becomes valid again.
            note.Text = "dirty valid draft";
            await Settle();
            dialog.FindControl<Button>("Cancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => dialog.OwnedWindows.Any(window => window.IsVisible));
            Window confirm = dialog.OwnedWindows.Single(window => window.IsVisible);
            await VerifyFrameAsync(confirm, "post-bookmarks discard question", "collections-post-bookmarks-confirm");
            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            confirm.RaiseEvent(escape);
            await Program.WaitUntilAsync(() => !confirm.IsVisible);
            Require(escape.Handled && dialog.IsVisible && note.Text == "dirty valid draft", "post-bookmarks: Escape on the question lost the draft or closed the dialog.");

            note.Text = "original note";
            await Settle();
            var close = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.RaiseEvent(close);
            await Program.WaitUntilAsync(() => !dialog.IsVisible);
            dialog = null;

            // Single-post editor: no list, same frame.
            dialog = new PostBookmarksDialog(host, (_, _) => Task.FromResult(false));
            dialog.Show(owner); await dialog.InitializeAsync(key);
            Require(!dialog.FindControl<ListBox>("Bookmarks")!.IsVisible, "post-bookmarks editor: list visible.");
            await VerifyFrameAsync(dialog, "post-bookmarks editor", "collections-post-bookmark-editor");
            PostBookmarksDialog editor = dialog;
            await CollectionsAtMinimumAsync(editor, () => VerifyFrameAsync(editor, "post-bookmarks editor minimum"));
        }
        finally { dialog?.CloseForProfileEnd(); }
    }

    private static async Task SavedSearchesFrameAsync(MainWindow owner, DesktopSessionHost host, string profile)
    {
        await host.CreateSavedArchiveSearchAsync(Guid.NewGuid(), "Frame fixture search", new ArchiveSearchSpec("frameneedle"), 0);
        var page = new ArchivePage(async token => await ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
        var window = new Window { Width = 900, Height = 700, Content = page, Title = "Saved search frame fixture" };
        SavedArchiveSearchesDialog? dialog = null;
        window.Show(owner);
        try
        {
            (dialog, Task closed) = await OpenSavedSearchesAsync(window, page, host);
            ListBox list = dialog.FindControl<ListBox>("SavedSearchList")!;
            list.SelectedIndex = 0;
            await VerifyFrameAsync(dialog, "saved searches list", "collections-saved-searches");
            SavedArchiveSearchesDialog manager = dialog;
            await CollectionsAtMinimumAsync(manager, () => VerifyFrameAsync(manager, "saved searches list minimum", "collections-saved-searches-min"));
            // The long German (and the other longest) labels wrap the eight footer actions onto several lines at the minimum
            // size; the footer must still lie inside the window (the bug: footer clipped at the minimum in German).
            string frameLanguage = Texts.Language;
            try
            {
                foreach (string language in new[] { "de", "ru", "fr", "pl" })
                {
                    Texts.SetLanguage(language);
                    await Settle();
                    await CollectionsAtMinimumAsync(manager, () => VerifyFrameAsync(manager, "saved searches list minimum " + language,
                        language == "de" ? "collections-saved-searches-min-de" : null));
                }
            }
            finally { Texts.SetLanguage(frameLanguage); await Settle(); }

            Button rename = dialog.FindControl<Button>("SavedSearchRename")!;
            rename.Command!.Execute(null);
            await Program.WaitUntilAsync(() => dialog.FindControl<StackPanel>("SavedSearchEditor")!.IsVisible);
            await VerifyFrameAsync(dialog, "saved searches editor", "collections-saved-searches-editor");
            await CollectionsAtMinimumAsync(manager, () => VerifyFrameAsync(manager, "saved searches editor minimum"));
            var cancel = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.RaiseEvent(cancel);
            await Program.WaitUntilAsync(() => !dialog.FindControl<StackPanel>("SavedSearchEditor")!.IsVisible);
            Require(cancel.Handled && dialog.IsVisible, "saved searches: Escape did not just leave the editor.");
            var close = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.RaiseEvent(close);
            await closed; dialog = null;

            // An unreadable store is reported in the local strip; Status keeps the same text.
            string store = Path.Combine(profile, "archive-searches.json");
            await File.WriteAllTextAsync(store, "{ not json");
            File.Delete(store + ".bak");
            (dialog, closed) = await OpenSavedSearchesAsync(window, page, host);
            string statusText = dialog.FindControl<TextBlock>("SavedSearchStatus")!.Text ?? "";
            Require(statusText.Length > 0 && statusText != Texts.T("savedSearch.hint"), "saved searches: unreadable store fixture did not fail.");
            await VerifyErrorStripAsync(dialog, "saved searches unreadable store", statusText);
            Capture(dialog, "collections-saved-searches-error");
            SavedArchiveSearchesDialog failed = dialog;
            await CollectionsAtMinimumAsync(failed, () => VerifyErrorStripAsync(failed, "saved searches unreadable store minimum", statusText));
            dialog.FindControl<Button>("SavedSearchClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await closed; dialog = null;
        }
        finally
        {
            dialog?.CloseForLifetime();
            page.EndLifetime();
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static async Task<(SavedArchiveSearchesDialog Dialog, Task Closed)> OpenSavedSearchesAsync(Window window, ArchivePage page, DesktopSessionHost host)
    {
        var dialog = new SavedArchiveSearchesDialog(host, page);
        Task closed = dialog.ShowDialog(window);
        await Program.WaitUntilAsync(() => dialog.IsVisible && dialog.FindControl<Button>("SavedSearchReload")!.IsEnabled);
        return (dialog, closed);
    }

    private static async Task TagDialogsFrameAsync(MainWindow owner, string directory)
    {
        using ProfileLease lease = ProfileLease.Acquire(directory);
        PrivateTagStore store = new(directory, lease);
        TagId red = store.Define("red").Tag;
        store.Define("blue");
        Require(PrivateMediaTagTarget.TryCreate(new MeowChan.Storage.AttachmentRevisionReference(new ResourceId("4chan", "g", "7400"), "7401", 0,
            "att-v2:" + new string('a', 64)), "m", "original", MeowChan.Transfer.TransferMediaRole.Original, new string('c', 64),
            out PrivateMediaTagTarget? target, out _), "Media tag fixture target is invalid.");
        store.SetMedia(red, target, true);
        PrivateTagLoadSnapshot Load() => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null);

        TagManagerDialog? manager = null;
        try
        {
            manager = new TagManagerDialog(new TagManagerSession(Load,
                (id, name, revision, token) => Task.FromResult(store.Rename(id, name, revision, token)),
                (id, revision, token) => Task.FromResult(store.Delete(id, revision, token))));
            manager.Show(owner);
            await Program.WaitUntilAsync(() => manager.IsVisible && manager.List.ItemCount == 2);
            manager.List.SelectedItem = manager.List.ItemsSource!.Cast<TagManagerRow>().Single(row => row.Name == "red");
            await VerifyFrameAsync(manager, "tag manager", "collections-tag-manager");
            TagManagerDialog shown = manager;
            await CollectionsAtMinimumAsync(shown, () => VerifyFrameAsync(shown, "tag manager minimum", "collections-tag-manager-min"));

            // Name collision is a real refused change: the strip shows it, Status keeps the text.
            manager.NameInput.Text = "blue";
            manager.RenameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !manager.Session.Busy && manager.Status.Text == Texts.T("tags.manage.error.collision"));
            await VerifyErrorStripAsync(manager, "tag manager collision", Texts.T("tags.manage.error.collision"));
            Require(!manager.Status.IsVisible, "tag manager: error repeated in the footer status line.");
            Capture(manager, "collections-tag-manager-error");

            // Delete question stays in the body; Escape keeps the tag, a second Escape closes.
            manager.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(manager.ConfirmPanel.IsVisible, "tag manager: delete question missing.");
            await VerifyFrameAsync(manager, "tag manager delete question", "collections-tag-manager-confirm");
            var keep = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            manager.RaiseEvent(keep);
            Require(keep.Handled && !manager.ConfirmPanel.IsVisible && manager.IsVisible, "tag manager: Escape on the question did not keep the dialog.");
            manager.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Program.WaitUntilAsync(() => !manager.IsVisible);
            manager = null;
        }
        finally { manager?.CloseForLifetime(); }

        MediaTagDialog? media = null;
        try
        {
            media = new MediaTagDialog(new MediaTagSession(new MediaTagSubject.Bound(target!), Load,
                (name, on, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [on], null, token)),
                (tag, on, assigned, token) => Task.FromResult(store.SetMediaSubjects(tag, [on], assigned, null, token))), "frame-fixture.png");
            media.Show(owner);
            await Program.WaitUntilAsync(() => media.IsVisible);
            Require(Find<Control>(media, "DialogFrameErrorStrip") is { IsVisible: false }, "media tags: error strip shown without an error.");
            await VerifyFrameAsync(media, "media tags", "collections-media-tags");
            MediaTagDialog shown = media;
            await CollectionsAtMinimumAsync(shown, () => VerifyFrameAsync(shown, "media tags minimum", "collections-media-tags-min"));

            media.Input.Text = "bad\tname";
            media.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !media.Session.Busy && media.Status.Text == Texts.T("tags.error.invalidName"));
            await VerifyErrorStripAsync(media, "media tags invalid name", Texts.T("tags.error.invalidName"));
            Require(media.Input.Text == "bad\tname", "media tags: refused draft was not kept.");
            Capture(media, "collections-media-tags-error");
            await CollectionsAtMinimumAsync(shown, () => VerifyErrorStripAsync(shown, "media tags invalid name minimum", Texts.T("tags.error.invalidName")));

            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            media.Input.RaiseEvent(escape);
            await Program.WaitUntilAsync(() => !media.IsVisible);
            Require(escape.Handled, "media tags: Escape was not handled.");
            media = null;
        }
        finally { media?.CloseForLifetime(); }
    }

    /// <summary>Runs <paramref name="check"/> with the dialog at its minimum size, then restores the size.</summary>
    private static async Task CollectionsAtMinimumAsync(Window dialog, Func<Task> check)
    {
        double width = dialog.Width, height = dialog.Height;
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        await Program.WaitUntilAsync(() => Math.Abs(dialog.Bounds.Height - dialog.MinHeight) <= 1 && Math.Abs(dialog.Bounds.Width - dialog.MinWidth) <= 1);
        await check();
        dialog.Width = width; dialog.Height = height;
        await Settle();
    }
}
