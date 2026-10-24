using System.Text;
using Microsoft.Data.Sqlite;
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

/// <summary>New transition coverage, separate from the completed restart/presentation comparison.</summary>
internal static class PostBookmarkTransitionChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-bookmark-transitions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        DesktopSessionHost? host = null;
        PostBookmarksDialog? dialog = null;
        var key = new PostBookmarkKey(new ResourceId("4chan", "g", "7300"), "7301");
        const string draft = "Own unsaved reason\n\nKeep this independent work 🙂";
        try
        {
            new Config { DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(key.Thread, "https://offline.invalid/g/thread/7300",
                    "Draft transition fixture", ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "media"),
                    [new CapturedPost(key.Post, 0, PostFieldPresence.Html, Html: "<p>Exact local post.</p>")]));
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            await host.SavePostBookmarkAsync(key, "original", 0, false);
            string path = Path.Combine(profile, "post-bookmarks.json");
            byte[] original = await File.ReadAllBytesAsync(path);
            dialog = new PostBookmarksDialog(host, (_, _) => Task.FromResult(false));
            dialog.Show(owner); await dialog.InitializeAsync();
            Button save = B(dialog, "Save"), open = B(dialog, "OpenTarget");
            TextBox note = dialog.FindControl<TextBox>("Note")!;
            TextBlock status = dialog.FindControl<TextBlock>("Status")!;
            await Program.WaitUntilAsync(() => open.IsEnabled);
            // A real local read failure must recover its own label when access becomes valid.
            // Only the private fixture metadata changes; user data and UI state are not injected.
            string database = Path.Combine(profile, "archive.db");
            long applicationId = ArchiveApplicationId(database);
            try
            {
                SetArchiveApplicationId(database, 0);
                await InvalidateAsync(dialog);
                Require(!open.IsEnabled && status.Text?.Contains(path, StringComparison.Ordinal) == true,
                    "The private unsupported archive fixture did not expose a real availability read error.");
            }
            finally { SetArchiveApplicationId(database, applicationId); }
            await InvalidateAsync(dialog);
            Require(open.IsEnabled && status.Text == Texts.T("bookmarks.available", "Draft transition fixture")
                && note.Text == "original",
                "Successful availability refresh retained its own obsolete read error.");
            await EditAsync(note, draft);

            // Real transient persistence failure, without replacing bytes or injecting private model state.
            using (FileStream blocked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                save.Command!.Execute(null);
                await Program.WaitUntilAsync(() => save.IsEnabled && status.Text?.Contains(path, StringComparison.Ordinal) == true);
            }
            string failure = status.Text!;
            await InvalidateAsync(dialog);
            Require(status.Text == failure && note.Text == draft && open.IsEnabled && save.Command!.CanExecute(null),
                "Availability refresh hid a persistence error or lost its recoverable draft.");
            byte[] afterFailure = await File.ReadAllBytesAsync(path);
            Require(original.SequenceEqual(afterFailure), "Failed note write changed stored bytes.");

            Task<bool> mayClose = dialog.PrepareToCloseAsync();
            await Program.WaitUntilAsync(() => dialog.OwnedWindows.Any(window => window.IsVisible));
            Window confirm = dialog.OwnedWindows.Single(window => window.IsVisible);
            confirm.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T("bookmarks.keepEditing")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!await mayClose && dialog.IsVisible && note.Text == draft,
                "Owner close preparation discarded a failed-write draft after Continue editing.");

            save.Command!.Execute(null);
            await Program.WaitUntilAsync(() => save.IsEnabled && open.IsEnabled && status.Text == Texts.T("bookmarks.available", "Draft transition fixture"));
            PostBookmarkSnapshot retried = await host.ReadPostBookmarksAsync();
            Require(retried.Revision == 2 && retried.Bookmarks.Single().Note == draft, "Explicit retry did not recover the preserved draft.");

            await host.SavePostBookmarkAsync(key, "winner from another editor", 2, true);
            string stale = draft + "\nstale independent addition";
            await EditAsync(note, stale);
            save.Command!.Execute(null);
            await Program.WaitUntilAsync(() => status.Text == Texts.T("bookmarks.conflict") && !save.IsEnabled);
            ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([key.Thread]);
            await host.DeleteArchivePermanentlyAsync(plan);
            await InvalidateAsync(dialog);
            Require(status.Text == Texts.T("bookmarks.conflict") && note.Text == stale
                && !save.Command!.CanExecute(null) && !open.Command!.CanExecute(null),
                "Archive deletion hid the conflict recovery instruction or changed its raw draft/guards.");
            PostBookmarkSnapshot winner = await host.ReadPostBookmarksAsync();
            Require(winner.Revision == 3 && winner.Bookmarks.Single().Note == "winner from another editor",
                "Conflict or archive deletion overwrote the other editor's committed note.");
            dialog.CloseForProfileEnd();

            dialog = new PostBookmarksDialog(host, (_, _) => Task.FromResult(false));
            dialog.Show(owner); await dialog.InitializeAsync();
            note = dialog.FindControl<TextBox>("Note")!;
            status = dialog.FindControl<TextBlock>("Status")!;
            string invalid = string.Concat(Enumerable.Repeat("🙂", 8001));
            await EditAsync(note, invalid);
            await InvalidateAsync(dialog);
            Require(status.Text == Texts.T("bookmarks.invalidNote") && note.Text == invalid
                && note.Text.EnumerateRunes().Count() == 8001 && !B(dialog, "Save").Command!.CanExecute(null)
                && !B(dialog, "OpenTarget").Command!.CanExecute(null),
                "Missing-target resolution hid invalid-note feedback or changed the 8001-scalar raw draft.");
            Console.WriteLine("SMOKE bookmark transitions: read error recovered; real failed write preserved raw draft/bytes and owner-close veto; explicit retry committed; later archive availability preserved conflict/invalid-note feedback and exact command guards.");
        }
        finally
        {
            dialog?.CloseForProfileEnd();
            if (host != null) await host.DisposeAsync();
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
    }

    private static long ArchiveApplicationId(string database)
    {
        using SqliteConnection connection = OpenFixtureDatabase(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void SetArchiveApplicationId(string database, long applicationId)
    {
        using SqliteConnection connection = OpenFixtureDatabase(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id=" + applicationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        command.ExecuteNonQuery();
    }
    private static SqliteConnection OpenFixtureDatabase(string database)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
    private static async Task InvalidateAsync(PostBookmarksDialog dialog)
    {
        // The real Save command is refreshed once on invalidation and again when its
        // asynchronous availability operation finishes. Observe its public notifications.
        var command = B(dialog, "Save").Command!;
        int refreshes = 0;
        void Changed(object? sender, EventArgs args) => refreshes++;
        command.CanExecuteChanged += Changed;
        try { dialog.InvalidateAvailability(); await Program.WaitUntilAsync(() => refreshes >= 2); }
        finally { command.CanExecuteChanged -= Changed; }
    }
    private static async Task EditAsync(TextBox note, string text)
    {
        bool changed = false;
        void Changed(object? sender, TextChangedEventArgs args) { if (note.Text == text) changed = true; }
        note.TextChanged += Changed;
        try { note.Text = text; await Program.WaitUntilAsync(() => changed); }
        finally { note.TextChanged -= Changed; }
    }
    private static Button B(PostBookmarksDialog dialog, string name) => dialog.FindControl<Button>(name)!;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
