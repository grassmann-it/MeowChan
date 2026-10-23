using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>Exercises real dialog commands and reader navigation; core-only tests cannot detect a stale disabled button.</summary>
internal static class PostBookmarkChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        await CheckSaveThenOpenAsync(owner, deleteTargetBeforeSave: false);
        await CheckSaveThenOpenAsync(owner, deleteTargetBeforeSave: true);
        await PostBookmarkGalleryReturnChecks.RunAsync(owner);
    }

    private static async Task CheckSaveThenOpenAsync(MainWindow owner, bool deleteTargetBeforeSave)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-bookmark-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        PostBookmarksDialog? dialog = null;
        DesktopSessionHost? host = null;
        ArchivePage? page = null;
        Window? readerWindow = null;
        var key = new PostBookmarkKey(new ResourceId("4chan", "g", "7100"), "7101");
        PostBookmarkKey expectedTarget = key;
        int expectedIndex = 1, opened = 0;
        try
        {
            new Config { DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(key.Thread,
                    "https://boards.4chan.org/g/thread/7100", "Bookmark Save regression",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "media"),
                    [new CapturedPost("7100", 0, PostFieldPresence.Html, Html: "first post"),
                     new CapturedPost("7101", 1, PostFieldPresence.Html, Html: "exact saved target")]));
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            await host.SavePostBookmarkAsync(key, "original note", 0, false);
            DesktopSessionHost activeHost = host;
            ArchivePage readerPage = new(token => activeHost.OpenArchiveReaderAsync(token),
                (_, _) => Task.CompletedTask, _ => { });
            page = readerPage;
            readerWindow = new Window { Width = 900, Height = 650, Content = readerPage.DetachReader(), Title = "Bookmark exact target" };
            Window activeReader = readerWindow;
            readerPage.IsAvailable = true;
            dialog = new PostBookmarksDialog(host, async (requested, token) =>
            {
                Require(requested == expectedTarget, "The bookmark command retargeted a different identity.");
                bool shown = await readerPage.OpenPostBookmarkAsync(requested, () => activeReader.Show(owner), token);
                ListBox posts = readerPage.FindControl<ListBox>("ArchivePosts")!;
                ArchivedThreadView? rendered = readerPage.CurrentThread;
                Require(shown && rendered != null && rendered.Summary.Id == requested.Thread
                    && rendered.FindPostIndex(requested.Post) == expectedIndex
                    && (posts.SelectedItem as ArchivePostRow)?.Id == requested.Post,
                    "Bookmark Open did not render and select the exact local post.");
                opened++;
                return true;
            });
            dialog.Show(owner);
            await dialog.InitializeAsync();
            Button open = dialog.FindControl<Button>("OpenTarget")!;
            Button save = dialog.FindControl<Button>("Save")!;
            TextBox note = dialog.FindControl<TextBox>("Note")!;
            TextBlock status = dialog.FindControl<TextBlock>("Status")!;
            await Program.WaitUntilAsync(() => open.IsEnabled);
            Require(open.Command?.CanExecute(null) == true, "Available target did not initially enable Open.");
            await NativeFeatureCheckpoint.HoldAsync("bookmarks", dialog, null, note, open, save, dialog.FindControl<ListBox>("Bookmarks")!);

            if (deleteTargetBeforeSave)
            {
                ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([key.Thread]);
                ArchivePermanentDeletionResult deleted = await host.DeleteArchivePermanentlyAsync(plan);
                Require(deleted.DeletedThreads.Contains(key.Thread), "Missing-target fixture did not remove its archive thread.");
            }
            note.Text = "note committed before availability is refreshed";
            Require(save.Command?.CanExecute(null) == true, "Edited note could not be saved.");
            save.Command!.Execute(null);
            await Program.WaitUntilAsync(() => save.IsEnabled
                && status.Text == Texts.T(deleteTargetBeforeSave ? "bookmarks.missing" : "bookmarks.available", "Bookmark Save regression"));
            PostBookmarkSnapshot saved = await host.ReadPostBookmarksAsync();
            Require(saved.Revision == 2 && saved.Bookmarks.Single().Note == note.Text, "The editor did not commit its edited note.");
            Require(open.IsEnabled == !deleteTargetBeforeSave
                && open.Command!.CanExecute(null) == !deleteTargetBeforeSave,
                "Save did not refresh Open from the current exact target availability.");
            open.Command!.Execute(null);
            if (deleteTargetBeforeSave)
                Require(opened == 0, "A missing target invoked the open executor after Save.");
            else
            {
                await Program.WaitUntilAsync(() => opened == 1 && !dialog.IsVisible);
                // Set up the separate Remove → neighbour regression. Returning/reloading happens
                // before Remove; there must be no reload or selection change after its commit.
                var neighbour = new PostBookmarkKey(key.Thread, "7100");
                await host.SavePostBookmarkAsync(neighbour, "neighbour's independent note", 2, false);
                readerWindow.Hide();
                dialog.ReturnFromReader();
                ListBox bookmarks = dialog.FindControl<ListBox>("Bookmarks")!;
                Button remove = dialog.FindControl<Button>("Remove")!;
                await Program.WaitUntilAsync(() => bookmarks.Items.Count == 2 && remove.IsEnabled && open.IsEnabled);
                await ConfirmRemovalAsync(dialog);
                await Program.WaitUntilAsync(() => remove.IsEnabled && open.IsEnabled
                    && (bookmarks.SelectedItem as PostBookmark)?.Key == neighbour);
                PostBookmarkSnapshot remaining = await host.ReadPostBookmarksAsync();
                Require(remaining.Revision == 4 && remaining.Bookmarks.Single().Key == neighbour
                    && remaining.Bookmarks.Single().Note == "neighbour's independent note",
                    "Remove did not preserve and select the neighbouring bookmark.");
                expectedTarget = neighbour; expectedIndex = 0;
                open.Command!.Execute(null);
                await Program.WaitUntilAsync(() => opened == 2 && !dialog.IsVisible);

                // The overview is still hidden on neighbour. Remove it in a separate real
                // post editor, then use the overview's actual ReturnFromReader/reload path.
                await host.SavePostBookmarkAsync(key, "return neighbour note", 4, false);
                PostBookmarksDialog postEditor = new(host, (_, _) => Task.FromResult(false));
                try
                {
                    Task editorClosed = postEditor.ShowDialog(owner);
                    await postEditor.InitializeAsync(neighbour);
                    await ConfirmRemovalAsync(postEditor);
                    await Program.WaitUntilAsync(() => postEditor.FindControl<Button>("Remove")!.IsEnabled
                        && postEditor.FindControl<TextBlock>("Target")!.Text == key.ToString());
                    PostBookmarkSnapshot afterEditor = await host.ReadPostBookmarksAsync();
                    Require(afterEditor.Revision == 6 && afterEditor.Bookmarks.Single().Key == key,
                        "The separate post editor did not remove the overview's previously selected bookmark.");
                    postEditor.CloseForProfileEnd();
                    await editorClosed;
                }
                finally { postEditor.CloseForProfileEnd(); }
                readerWindow.Hide();
                dialog.ReturnFromReader();
                await Program.WaitUntilAsync(() => open.IsEnabled
                    && (bookmarks.SelectedItem as PostBookmark)?.Key == key);
                expectedTarget = key; expectedIndex = 1;
                open.Command!.Execute(null);
                await Program.WaitUntilAsync(() => opened == 3 && !dialog.IsVisible);
            }
            Console.WriteLine("SMOKE post bookmarks real commands/reader: "
                + (deleteTargetBeforeSave ? "Save after target deletion retains note and keeps Open disabled."
                    : "Save, confirmed Remove → neighbour, and ReturnFromReader after removal in another editor all enable exact Open."));
        }
        finally
        {
            dialog?.CloseForProfileEnd();
            page?.EndLifetime(); readerWindow?.Close();
            if (host != null) await host.DisposeAsync();
            try { Directory.Delete(profile, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ConfirmRemovalAsync(PostBookmarksDialog dialog)
    {
        Button remove = dialog.FindControl<Button>("Remove")!;
        Require(remove.Command?.CanExecute(null) == true, "The selected bookmark cannot enter the Remove confirmation.");
        remove.Command!.Execute(null);
        await Program.WaitUntilAsync(() => dialog.OwnedWindows.Any(window => window.IsVisible));
        Window confirmation = dialog.OwnedWindows.Single(window => window.IsVisible);
        Button confirm = confirmation.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, Texts.T("bookmarks.remove")));
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
