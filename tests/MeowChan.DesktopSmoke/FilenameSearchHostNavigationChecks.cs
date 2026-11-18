using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Microsoft.Data.Sqlite;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

using ArchivePostRow = MeowChan.Desktop.ArchivePostRow;

namespace MeowChan.DesktopSmoke;

// ROOT dispatch only, after actual MainWindow Ready/resources on the native UI thread.
// Real native owned Window/Page/Dialog; actions are managed routed clicks/keys, NOT OS input.
// Bernoulli owns concurrent modal/lifetime races; those are deliberately not repeated here.
internal static class FilenameSearchHostNavigationChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId Origin = new("synthetic-A", "board-A", "1000");
    private static readonly ResourceId Target = new("synthetic-B", "board-B", "2000");
    private const string TargetPost = "p250";
    private const string Needle = "w21-host-name";

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
            "Filename host navigation requires actual visible native MainWindow after Ready.");
        foreach (string scenario in new[] { "close-null", "escape-null", "select", "missing-post", "missing-thread" })
            await JourneyAsync(owner, scenario);
        Console.WriteLine("SMOKE filename-host-navigation complete: all navigation assertions reached; no lifetime/modal or OS-keyboard claim.");
    }

    private static async Task JourneyAsync(MainWindow owner, string scenario)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("filename-host-navigation-");
        await SeedAsync(profile);
        int factories = 0, captures = 0, folders = 0, galleries = 0, fullscreen = 0;
        var page = new ArchivePage(token =>
        {
            Interlocked.Increment(ref factories);
            return ArchiveReader.OpenAsync(profile, token);
        }, (_, _) => { captures++; throw new InvalidOperationException("Unexpected capture mutation."); },
            _ => { folders++; throw new InvalidOperationException("Unexpected external launch."); });
        page.OpenGallery = (_, _, _) => { galleries++; throw new InvalidOperationException("Unexpected gallery."); };
        page.OpenMediaGallery = (_, _, _) => { galleries++; throw new InvalidOperationException("Unexpected media gallery."); };
        page.OpenResultsGallery = (_, _, _) => { galleries++; throw new InvalidOperationException("Unexpected results gallery."); };
        page.FullscreenRequested = () => { fullscreen++; throw new InvalidOperationException("Unexpected fullscreen action."); };
        Window window = new() { Width = 1120, Height = 820, Content = page,
            Title = "Synthetic filename host navigation", ShowInTaskbar = false };
        Exception? journeyFailure = null;
        try
        {
            window.Show(owner);
            await UntilAsync(() => window.IsVisible && window.TryGetPlatformHandle()?.Handle is nint h && h != 0);
            page.IsAvailable = true;
            await page.LoadThreadAsync(Origin).WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline); // existing passive projection is allowed
            await page.ArchiveMediaCountTask.WaitAsync(Deadline);
            page.ShowPost(TargetPost); // origin deliberately contains the SAME post string
            await SmokeDispatcher.SettleAsync();
            ArchivedThreadView origin = page.CurrentThread!;
            string[] beforeRows = Rows(page);
            string? beforeSelected = (C<ListBox>(page, "ArchivePosts").SelectedItem as ArchivePostRow)?.Id;
            Require(origin.Summary.Id == Origin && origin.PostCount == 251
                && origin.FindPostIndex(TargetPost) == 250
                && origin.GetPosts(0, 1).Single().PostId == "p000"
                && beforeRows.SequenceEqual(new[] { TargetPost }) && beforeSelected == TargetPost,
                "Origin must select final-page collision post, distinct from firstpost fallback.");
            int beforeOpen = Volatile.Read(ref factories);
            // Actual product entry (AP2): reader find bar, File name scope, Search; not injected Show/Navigate.
            Button entry = UnifiedSearchEntry.ReaderScope(page, MeowChan.Desktop.Controls.SearchScope.FileName);
            Click(page, entry.Name!);
            await UntilAsync(() => page.FilenameSearchWindow is { IsVisible: true });
            FilenameSearchDialog dialog = page.FilenameSearchWindow!;
            Require(ReferenceEquals(dialog.Owner, window), "Search dialog has wrong real owner.");
            Require(Volatile.Read(ref factories) == beforeOpen, "Opening host implicitly searched.");
            Task operation = page.FilenameSearchTask;
            Require(!operation.IsCompleted, "Navigation operation observer is vacuous.");
            if (scenario is "close-null" or "escape-null")
            {
                if (scenario == "close-null") Click(dialog, "FilenameSearchClose");
                else dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await operation.WaitAsync(Deadline);
                Unchanged();
                Require(Volatile.Read(ref factories) == beforeOpen, "Null/Cancel acquired navigation reader.");
                Console.WriteLine("SMOKE filename-host-navigation " + scenario + ": exact origin/page/selection retained, zero reader acquisition.");
            }
            else
            {
                C<TextBox>(dialog, "FilenameSearchQuery").Text = Needle;
                await SmokeDispatcher.SettleAsync();
                Click(dialog, "FilenameSearchSubmit");
                await UntilAsync(() => dialog.Session.Current is { Busy: false, StatusKey: "filename.search.ready" });
                var rows = dialog.Session.Current.Rows;
                Require(rows.Length == 2, "Search must contain same-name claims in two different resources.");
                int selectedIndex = Enumerable.Range(0, rows.Length).Single(i => rows[i].Thread == Target);
                ArchiveFilenameHit hit = rows[selectedIndex];
                Require(hit.Thread == Target && hit.Post == TargetPost && hit.Position == 3 && hit.Variant == "preview"
                    && hit.OriginalName == Needle + ".png", "Actual catalog search lost full selected claim.");
                Require(ReferenceEquals(page.CurrentThread, origin), "Search itself navigated.");
                C<ListBox>(dialog, "FilenameSearchResults").SelectedIndex = selectedIndex;
                await SmokeDispatcher.SettleAsync();
                Require(C<Button>(dialog, "FilenameSearchSelect").IsEnabled, "Explicit result choice is not actionable.");
                if (scenario == "missing-post") await RemovePostAsync(profile);
                if (scenario == "missing-thread")
                {
                    using ProfileLease lease = ProfileLease.Acquire(profile);
                    await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
                    await catalog.DeleteThreadsAsync(new[] { Target });
                }
                Click(dialog, "FilenameSearchSelect"); // old immutable result remains selectable; host MUST revalidate
                await operation.WaitAsync(Deadline);
                await page.MediaLoadTask.WaitAsync(Deadline);
                await SmokeDispatcher.SettleAsync();
                if (scenario == "select")
                {
                    Require(page.CurrentThread?.Summary.Id == Target && page.CurrentThread.FindPostIndex(TargetPost) == 250,
                        "Result choice did not load exact other stored resource/offscreen post.");
                    Require(Rows(page).SequenceEqual(new[] { TargetPost })
                        && C<ListBox>(page, "ArchivePosts").SelectedItem is ArchivePostRow { Id: TargetPost },
                        "Wrong post page, no explicit selection, or first-post fallback.");
                    Require(page.CurrentThread.Summary.Revision == hit.CatalogThreadRevision,
                        "Fresh snapshot revision differs from the selected captured claim.");
                    CapturedAttachment attachment = page.CurrentThread.GetPosts(250, 1).Single().Attachments!.Single();
                    Require(attachment.Position == hit.Position && attachment.Variant == hit.Variant
                        && attachment.OriginalFileName == hit.OriginalName
                        && attachment.AttachmentRevisionKey == hit.AttachmentRevisionKey,
                        "Navigation reconstructed attachment identity.");
                    Console.WriteLine("SMOKE filename-host-navigation select: exact other Source/Board/Thread, collision Post, offscreen index250/page, full attachment identity.");
                }
                else
                {
                    Unchanged();
                    Console.WriteLine("SMOKE filename-host-navigation " + scenario + ": target removed after search; fresh read rejects without firstpost fallback.");
                }
            }
            Require(!page.IsFilenameSearchOpen && page.FilenameSearchWindow == null, "Navigation operation did not finish.");
            Require(captures == 0 && folders == 0 && galleries == 0 && fullscreen == 0,
                "Navigation triggered capture/folder/gallery/fullscreen side effect.");

            void Unchanged()
            {
                Require(ReferenceEquals(page.CurrentThread, origin) && page.CurrentThread.Summary.Id == Origin,
                    "Null/missing target replaced current resource.");
                Require(Rows(page).SequenceEqual(beforeRows)
                    && (C<ListBox>(page, "ArchivePosts").SelectedItem as ArchivePostRow)?.Id == beforeSelected,
                    "Null/missing target changed page/selected post or fell back to first post.");
            }
        }
        catch (Exception error)
        {
            journeyFailure = error;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                try
                {
                    page.EndLifetime();
                    await page.FilenameSearchTask.WaitAsync(Deadline);
                    await page.MediaLoadTask.WaitAsync(Deadline);
                }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            finally
            {
                // Native close is attempted even when termination/join fails or times out.
                try { window.Close(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                finally
                {
                    try { if (owner.IsVisible) owner.Activate(); }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
            }
            if (cleanupFailures.Count != 0)
            {
                if (journeyFailure != null) cleanupFailures.Insert(0, journeyFailure);
                throw new AggregateException("Filename navigation journey/cleanup failed; no drain PASS.", cleanupFailures);
            }
            // Original journey failure propagates unchanged when cleanup succeeds.
            // Keep synthetic evidence only; no recursive cleanup or user settings writes.
        }
    }

    private static async Task SeedAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in new[] { Origin, Target })
        {
            const int count = 251;
            var posts = Enumerable.Range(0, count).Select(i => new CapturedPost(
                "p" + i.ToString("D3"), i, PostFieldPresence.All,
                Html: "plain synthetic offline post", Attachments: i == count - 1
                    ? new[] { new CapturedAttachment(3, "synthetic-media", "preview",
                        Url: "https://example.invalid/not-acquired", OriginalFileName: Needle + ".png",
                        PlannedPath: Path.Combine(profile, "uncreated-media", "not-opened.png")) } : Array.Empty<CapturedAttachment>())).ToArray();
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", "Synthetic filename navigation",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(profile, "uncreated-media"), posts));
        }
    }

    // Fixture-only mutation AFTER result publication. Keep thread revision unchanged so
    // missing-post rejection cannot pass merely because a revision counter changed.
    private static Task RemovePostAsync(string profile)
    {
        using var connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        foreach (string table in new[] { "archive_attachments", "archive_posts" })
        {
            using var command = ArchiveSql.Command(connection, transaction,
                "DELETE FROM " + table + " WHERE source=$source AND board=$board AND thread=$thread AND post=$post");
            ArchiveSql.AddId(command, Target); command.Parameters.AddWithValue("$post", TargetPost); command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.CompletedTask;
    }

    private static string[] Rows(ArchivePage page) => C<ListBox>(page, "ArchivePosts").ItemsSource!
        .Cast<ArchivePostRow>().Select(row => row.Id).ToArray();
    private static T C<T>(Control root, string name) where T : Control
    {
        // Code-built dialogs have no parent XAML NameScope. Resolve actual attached controls,
        // requiring unique ordinal Name before checking type (never mask duplicate names).
        Control[] matches = root.GetVisualDescendants().OfType<Control>().Prepend(root)
            .Where(control => string.Equals(control.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Expected exactly one actual filename host control: " + name
                + "; found " + matches.Length);
        return matches[0] is T typed ? typed
            : throw new InvalidOperationException("Wrong actual filename host control type: " + name);
    }
    private static void Click(Control root, string name)
    {
        Button button = C<Button>(root, name);
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Hidden/disabled filename host action: " + name);
        SmokeActionClick.Execute(button);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!predicate())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Filename host navigation did not reach expected state.");
            await Task.Delay(20);
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
