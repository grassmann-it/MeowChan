using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Root dispatch after Ready: await FilenameSearchHostLifetimeChecks.RunAsync(owner).
// Actual ArchivePage constructors, catalog readers, native owners and ShowDialog tasks.
// Only lifetime-negative selection cases belong here; normal identity navigation is Hypatia's.
// No OS keyboard/clipboard claim: button events below are deliberately managed/routed.
internal static class FilenameSearchHostLifetimeChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    // Implementation basis, not a substitute for Root's immutable runtime source digest.
    private const string FullscreenAdmissionBasisSha256 = "b03e5567fa9feb55a6bb3674afda6d907af4c5b8a0902cffc0c8624684a14845";
    private static readonly ResourceId Baseline = new("4chan", "g", "21000");
    private static readonly ResourceId Target = new("4chan", "g", "22000");
    private const string Needle = "w21-host-lifetime-only";
    private enum Boundary { OwnerClose, Unavailable, EndLifetime }
    private enum Pending { VisibleRead, ClosedDrain, DelayedShowReturn, NavigationRead }

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.SessionHost.State == DesktopSessionState.Ready
            && owner.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
            "Filename host lifetime requires a visible native MainWindow after Ready.");
        string profile = TemporaryWorkspacePaths.CreateDirectory("filename-host-lifetime-");
        await SeedAsync(profile);
        await ModalReservationAsync(owner, profile);
        foreach (Boundary boundary in Enum.GetValues<Boundary>())
        foreach (Pending pending in Enum.GetValues<Pending>())
            await InvalidateAsync(owner, profile, boundary, pending);
        await ShowFailureDrainsAsync(owner, profile);
        await RootShellReservationAsync(owner);
        Console.WriteLine("SMOKE filename-host-lifetime complete: synchronous reservation, competing modal entries, "
            + "12 owner/availability/end lifetime barriers, real show-failure drain and actual root-shell reservation reached; "
            + "synthetic catalog metadata, managed events, no current-byte/OS-keyboard/WinMac acceptance.");
        // Retain the isolated synthetic catalog as evidence. Never sweep a profile/media directory.
    }

    private static async Task ModalReservationAsync(MainWindow owner, string profile)
    {
        await using Fixture f = await Fixture.CreateAsync(owner, profile);
        Require(f.C<Button>("ArchiveCleanupOpen").IsEnabled && f.C<Button>("ArchiveTransferPreview").IsEnabled
            && f.C<Button>("ArchiveResultsExport").IsEnabled
            && f.C<Button>("ArchiveResultsExport").Command?.CanExecute(null) == true,
            "Competing-command baseline was already disabled.");
        // FNH-P2-02: real host-backed manager admission must work before filename owns it.
        // Open/close both actual managers without saving, applying, or substituting a store/Show.
        await CheckSavedSearchBaselineAsync(saveCurrent: false);
        await CheckSavedSearchBaselineAsync(saveCurrent: true);
        Require(f.C<Button>("ArchiveReaderGroups").IsEnabled
            && f.BookmarksEntry.Command?.CanExecute(null) == true,
            "Reader-group/bookmark manager baseline was already ineligible.");
        Dispatch(f.C<Button>("ArchiveReaderGroups"));
        f.BookmarksEntry.Command!.Execute(null); // Command-only button: RaiseEvent alone cannot dispatch ICommand.
        Require(f.ReaderGroups == 1 && f.PostBookmarkManagers == 1,
            "Actual reader-group/bookmark entries did not reach their instance callbacks before reservation.");
        // AP2: the product entry is the reader find bar in the File name scope; its Search button is the origin.
        UnifiedSearchEntry.ReaderScope(f.Page, SearchScope.FileName);
        await f.Page.ThreadFindTask.WaitAsync(Deadline);
        Require(f.C<StackPanel>("ReaderFindBar").IsVisible && f.Origin.IsEffectivelyVisible, "Find baseline never opened.");

        // Click handler enters the real host synchronously. No UI turn or await precedes this oracle.
        Click(f.Origin);
        Task run = f.Page.FilenameSearchTask;
        Require(f.Shows == 1 && f.ReservedAtShow && f.Page.IsFilenameSearchOpen
            && f.Page.IsReaderDialogOpen && !run.IsCompleted
            && f.C<SearchScopeChip>("ReaderFindScope").Scope == SearchScope.FileName,
            "Filename host did not own/refresh the reservation before its first Show await.");
        FilenameSearchDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        await CheckCompetitorsAsync("visible-show");

        ReaderBarrier read = f.HoldNextReader();
        dialog.Session.Edit(Needle);
        Task request = dialog.Session.SearchAsync();
        CancellationToken token = await read.Entered.Task.WaitAsync(Deadline);
        Require(!request.IsCompleted && dialog.Session.Current.Busy, "Reader barrier did not hold the actual request.");
        await CheckCompetitorsAsync("pending-read");
        Require(f.Page.TryDismissOverlay(), "Parent dismissal did not consume the filename overlay.");
        await f.NativeShow!.WaitAsync(Deadline);
        Task drain = dialog.TerminateAsync();
        Require(token.IsCancellationRequested && !drain.IsCompleted && !run.IsCompleted
            && !dialog.IsVisible && dialog.Session.Current.Closed && f.Page.IsFilenameSearchOpen,
            "Escape/overlay release abandoned real pending session work or released ownership early.");
        Require(f.Page.TryDismissOverlay(), "Dismissal during drain leaked to reader/fullscreen/HTML.");
        await CheckCompetitorsAsync("closed-drain");
        read.Release.TrySetResult(true);
        await Task.WhenAll(request, drain, run).WaitAsync(Deadline);
        await RequireDisposedAsync(await read.Returned.Task.WaitAsync(Deadline));
        Require(!f.Page.IsFilenameSearchOpen && !f.Page.IsReaderDialogOpen
            && !f.C<Border>("ArchiveExportDialog").IsVisible
            && f.C<Button>("ArchiveCleanupOpen").IsEnabled && f.C<Button>("ArchiveTransferPreview").IsEnabled
            && f.C<Button>("ArchiveResultsExport").Command?.CanExecute(null) == true
            && f.SavedSearches.IsEnabled && f.SaveSearch.IsEnabled
            && f.Page.CanSaveCurrentSearch && f.BookmarksEntry.Command?.CanExecute(null) == true,
            "Real drain did not release exactly its own reservation and restore action eligibility.");

        // Re-entry is a fresh real native dialog, not just a boolean checked after close.
        f.InstallShow();
        Task retry = f.Page.OpenFilenameSearchAsync(f.Origin);
        FilenameSearchDialog fresh = await f.Shown.Task.WaitAsync(Deadline);
        Require(!ReferenceEquals(dialog, fresh) && f.Shows == 2, "Drained operation did not admit one fresh dialog.");
        fresh.Close();
        await retry.WaitAsync(Deadline);
        Console.WriteLine("SMOKE filename-host-lifetime modal: sync owner before await; direct repeat/URLtext/HTML(single,batch,all)/cleanup/preview/find/reader/gallery/fullscreen blocked through real drain; fresh retry checked.");

        // AP2: the saved-search entries are the split button (manager) and its "Save applied search…" menu entry.
        bool EntryEnabled(bool saveCurrent) => saveCurrent ? f.SaveSearch.IsEnabled : f.SavedSearches.IsEnabled;
        async Task CheckSavedSearchBaselineAsync(bool saveCurrent)
        {
            Require(f.Page.CanSaveCurrentSearch && EntryEnabled(saveCurrent)
                && !f.Window.OwnedWindows.Any(window => window.IsVisible),
                "Saved-search baseline was not eligible without a filename reservation.");
            SavedArchiveSearchesDialog? manager = null;
            try
            {
                f.DispatchSavedSearch(saveCurrent); // Actual split Click / menu entry command, not OpenSavedSearchesAsync or a Show seam.
                await UntilAsync(() => f.Window.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Any(window => window.IsVisible));
                manager = f.Window.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Single(window => window.IsVisible);
                Require(ReferenceEquals(manager.Owner, f.Window)
                    && manager.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Saved-search baseline did not create the actual native owned manager.");
                SavedArchiveSearchesDialog shown = manager;
                await UntilAsync(() => saveCurrent
                    ? D<TextBox>(shown, "SavedSearchName").IsEffectivelyVisible
                        && D<TextBox>(shown, "SavedSearchName").IsEnabled
                        && D<Button>(shown, "SavedSearchConfirm").IsEnabled
                    : D<Button>(shown, "SavedSearchReload").IsEnabled);
                Require(!EntryEnabled(saveCurrent) && !f.Origin.IsEnabled,
                    "Actual saved-search manager did not hold page/reverse-filename admission.");
                Click(D<Button>(shown, "SavedSearchClose"));
                await UntilAsync(() => !shown.IsVisible && f.Window.IsEnabled
                    && f.SavedSearches.IsEnabled && f.SaveSearch.IsEnabled);
                Require(!f.Window.OwnedWindows.Contains(shown), "Baseline saved-search window remained owned after Close.");
            }
            finally
            {
                // No name edit/save/apply is performed. Cleanup also covers a failed baseline assertion.
                if (manager is { IsVisible: true }) manager.CloseForLifetime();
            }
        }

        async Task CheckCompetitorsAsync(string phase)
        {
            int reads = Volatile.Read(ref f.Readers), shows = f.Shows;
            int groups = f.ReaderGroups, bookmarks = f.PostBookmarkManagers;
            string? error = f.C<TextBlock>("ArchiveErrorText").Text;
            Require(!f.Origin.IsEnabled && !f.C<Button>("ReaderFindSearch").IsEnabled
                && !f.C<Button>("ArchiveCleanupOpen").IsEnabled
                && !f.C<Button>("ArchiveTransferPreview").IsEnabled
                && !f.C<Button>("ArchiveResultsExport").IsEnabled
                && f.C<Button>("ArchiveResultsExport").Command?.CanExecute(null) == false
                && !f.SavedSearches.IsEnabled && !f.SaveSearch.IsEnabled
                && !f.Page.CanSaveCurrentSearch && f.BookmarksEntry.Command?.CanExecute(null) == false,
                "Actual competing action eligibility ignores the filename owner.");
            await f.Page.OpenFilenameSearchAsync(f.Origin).WaitAsync(Deadline);
            await f.Page.OpenUrlTextExportForThreadAsync(Target, f.Origin).WaitAsync(Deadline);
            await f.Page.OpenExportForThreadAsync(Target, f.Origin).WaitAsync(Deadline);
            await f.Page.OpenExportForThreadsAsync(new[] { Baseline, Target }, f.Origin).WaitAsync(Deadline);
            await f.Page.OpenExportForAllResultsAsync(f.Origin).WaitAsync(Deadline);
            await f.Page.OpenArchiveCleanupAsync().WaitAsync(Deadline);
            await f.Page.OpenTransferPreviewAsync().WaitAsync(Deadline);
            await f.Page.ApplySearchAsync().WaitAsync(Deadline);
            await f.Page.NavigateReaderAsync(1, 0).WaitAsync(Deadline);
            // Forced routed commands intentionally bypass disabled-control input admission:
            // the real handler must guard, not merely the button's enabled appearance.
            foreach (string name in new[] { "ReaderFindOpen", "ArchiveReadPosts", "ArchiveCloseReader",
                "ArchiveReaderFullscreen", "ArchiveOpenGallery" })
                Dispatch(f.C<Button>(name));
            f.C<Button>("ArchiveResultsGallery").Command?.Execute(null);
            f.DispatchSavedSearch(saveCurrent: false);
            f.DispatchSavedSearch(saveCurrent: true);
            Dispatch(f.C<Button>("ArchiveReaderGroups"));
            f.BookmarksEntry.Command!.Execute(null); // Force real DelegateCommand admission despite disabled appearance.
            Require(!f.Window.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Any(window => window.IsVisible)
                && f.ReaderGroups == groups && f.PostBookmarkManagers == bookmarks,
                "A saved-search/reader-group/bookmark manager crossed filename admission synchronously.");
            await TurnAsync();
            Require(ReferenceEquals(run, f.Page.FilenameSearchTask) && f.Shows == shows
                && Volatile.Read(ref f.Readers) == reads && f.UrlReads == 0 && f.UrlShows == 0
                && f.PreviewRoots == 0 && f.PreviewShows == 0 && f.ExternalActions == 0
                && !f.Page.IsArchiveCleanupOpen && !f.C<Border>("ArchiveExportDialog").IsVisible
                && f.C<SearchScopeChip>("ReaderFindScope").Scope == SearchScope.FileName // forced find entry refused
                && !f.Window.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Any()
                && f.Window.OwnedWindows.All(window => ReferenceEquals(window, dialog))
                && f.ReaderGroups == groups && f.PostBookmarkManagers == bookmarks
                && f.C<TextBlock>("ArchiveErrorText").Text == error,
                "Competing direct entry crossed/replaced the active filename operation or published a failed-manager error.");
            Console.WriteLine($"SMOKE filename-host-lifetime FNH-P2-02 [{phase}]: real host-backed saved-search/save-current dispatch blocked; both reader-group handlers and actual bookmark ICommand preserve callback counts; no second manager/error.");
        }
    }

    private static async Task InvalidateAsync(MainWindow owner, string profile, Boundary boundary, Pending pending)
    {
        await using Fixture f = await Fixture.CreateAsync(owner, profile);
        TaskCompletionSource<bool>? showRelease = pending == Pending.DelayedShowReturn ? f.HoldShowReturn() : null;
        Task run = f.Page.OpenFilenameSearchAsync(f.Origin);
        FilenameSearchDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        ArchivedThreadView baseline = f.Page.CurrentThread!;
        string[] rows = f.PostIds();
        object? selected = f.C<ListBox>("ArchivePosts").SelectedItem;
        ReaderBarrier? read = null;
        Task? request = null, drain = null;
        CancellationToken token = default;
        if (pending is Pending.VisibleRead or Pending.ClosedDrain)
        {
            read = f.HoldNextReader();
            dialog.Session.Edit(Needle);
            request = dialog.Session.SearchAsync();
            token = await read.Entered.Task.WaitAsync(Deadline);
            Require(!request.IsCompleted && dialog.Session.Current.Busy && dialog.IsVisible,
                "Visible-read barrier was not reached in the real shown dialog.");
            if (pending == Pending.ClosedDrain)
            {
                dialog.Close();
                await f.NativeShow!.WaitAsync(Deadline);
                drain = dialog.TerminateAsync();
                Require(!drain.IsCompleted && !run.IsCompleted && f.Page.IsFilenameSearchOpen,
                    "Already-closed dialog did not retain its actual pending termination.");
            }
        }
        else
        {
            // Get a real original row, then actual ListBox selection + real Select.Click.
            // No synthetic hit, replaced navigation, or fabricated Show result is permitted.
            await f.SearchReadyAsync(dialog);
            if (pending == Pending.NavigationRead) read = f.HoldNextReader();
            f.SelectFirst(dialog);
            ArchiveFilenameHit? actual = await f.NativeShow!.WaitAsync(Deadline);
            Require(actual != null, "Actual selection did not return a metadata hit.");
            if (pending == Pending.DelayedShowReturn)
            {
                await f.ShowReturned.Task.WaitAsync(Deadline);
                Require(showRelease is { Task.IsCompleted: false } && !run.IsCompleted,
                    "Real selected Show result was not delayed at the host boundary.");
            }
            else
            {
                token = await read!.Entered.Task.WaitAsync(Deadline);
                Require(dialog.Session.Current.Closed && !dialog.IsVisible && !run.IsCompleted,
                    "Navigation reader was not pending after real show/termination.");
            }
        }
        int mediaAtBoundary = Volatile.Read(ref f.MediaReads);
        f.End(boundary);
        // Reuse the same page before old I/O returns. A fresh availability value must not
        // resurrect the cancelled owner or let another entry steal its drain reservation.
        if (boundary == Boundary.Unavailable) f.Page.IsAvailable = true;
        await f.NativeShow!.WaitAsync(Deadline);
        Require(!dialog.IsVisible && dialog.Session.Current.Closed && !run.IsCompleted
            && f.Page.IsFilenameSearchOpen && f.Page.IsReaderDialogOpen,
            "Invalidation was mistaken for completion or left a late native window visible.");
        if (read != null) Require(token.IsCancellationRequested, "Pending real factory token was not cancelled.");
        drain ??= dialog.TerminateAsync();
        if (pending is Pending.VisibleRead or Pending.ClosedDrain)
            Require(!drain.IsCompleted, "Real Terminate returned before the uncooperative request.");
        string? error = f.C<TextBlock>("ArchiveErrorText").Text;
        string? status = D<TextBlock>(dialog, "FilenameSearchStatus").Text;
        await f.FocusAwayAsync();
        await f.Page.OpenFilenameSearchAsync(f.Origin).WaitAsync(Deadline);
        Require(ReferenceEquals(run, f.Page.FilenameSearchTask) && f.Shows == 1,
            "Invalidated-but-pending owner was replaced before its actual drain/return.");
        showRelease?.TrySetResult(true);
        read?.Release.TrySetResult(true);
        await run.WaitAsync(Deadline);
        if (request != null) await request.WaitAsync(Deadline);
        await drain.WaitAsync(Deadline);
        if (read != null) await RequireDisposedAsync(await read.Returned.Task.WaitAsync(Deadline));
        await TurnAsync();
        Require(!f.Page.IsFilenameSearchOpen && !f.Page.IsReaderDialogOpen && f.Page.FilenameSearchWindow == null
            && !dialog.IsVisible && dialog.Session.Current.Rows.IsEmpty
            && ReferenceEquals(baseline, f.Page.CurrentThread) && f.PostIds().SequenceEqual(rows)
            && ReferenceEquals(selected, f.C<ListBox>("ArchivePosts").SelectedItem)
            && Volatile.Read(ref f.MediaReads) == mediaAtBoundary
            && f.C<TextBlock>("ArchiveErrorText").Text == error
            && D<TextBlock>(dialog, "FilenameSearchStatus").Text == status && f.ExternalActions == 0,
            "Late read/show return navigated, reprojected media, reopened UI, changed selection, or published stale diagnostics.");
        f.RequireNoLateFocus();
        Console.WriteLine($"SMOKE filename-host-lifetime barrier [{boundary}/{pending}]: real native close, cancellation, retained owner until actual completion, reader disposal where held, no late navigation/focus/publication checked.");
    }

    private static async Task ShowFailureDrainsAsync(MainWindow owner, string profile)
    {
        await using Fixture f = await Fixture.CreateAsync(owner, profile);
        var fail = f.Hold();
        f.ShowFailure = fail.Task;
        Task run = f.Page.OpenFilenameSearchAsync(f.Origin);
        FilenameSearchDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        ReaderBarrier read = f.HoldNextReader();
        dialog.Session.Edit(Needle);
        Task request = dialog.Session.SearchAsync();
        CancellationToken token = await read.Entered.Task.WaitAsync(Deadline);
        fail.TrySetResult(true); // Throws ONLY after actual ShowDialog has created the real native window.
        await UntilAsync(() => dialog.Session.Current.Closed);
        Task drain = dialog.TerminateAsync();
        Require(!run.IsCompleted && !drain.IsCompleted && token.IsCancellationRequested
            && f.Page.IsFilenameSearchOpen, "Show exception bypassed the real session drain.");
        f.Page.IsAvailable = false;
        await f.NativeShow!.WaitAsync(Deadline);
        string? error = f.C<TextBlock>("ArchiveErrorText").Text;
        await f.FocusAwayAsync();
        read.Release.TrySetResult(true);
        await Task.WhenAll(request, drain, run).WaitAsync(Deadline);
        await RequireDisposedAsync(await read.Returned.Task.WaitAsync(Deadline));
        await TurnAsync();
        Require(!f.Page.IsFilenameSearchOpen && !dialog.IsVisible && f.ExternalActions == 0
            && f.C<TextBlock>("ArchiveErrorText").Text == error,
            "Failed show leaked a native window/owner or published after invalidation.");
        f.RequireNoLateFocus();
        Console.WriteLine("SMOKE filename-host-lifetime show-failure: actual window + pending real factory, throw -> real Terminate join, availability invalidation before release, no stale focus/diagnostic checked.");
    }

    private static async Task RootShellReservationAsync(MainWindow owner)
    {
        // FNH-P2-01: use MainWindow's existing real page; never replace _archivePage or a reader pane.
        ArchivePage page = owner.ArchivePage;
        var previousShow = page.FilenameSearchShowForTests;
        var shown = Signal<FilenameSearchDialog>();
        var nativeReturned = Signal<bool>();
        var release = Signal<bool>();
        Task<ArchiveFilenameHit?>? nativeShow = null;
        Task? run = null;
        FilenameSearchDialog? dialog = null;
        WindowState before = owner.WindowState;
        Exception? failureBeforeCleanup = null;
        try
        {
            Require(owner.IsEnabled && !owner.OwnedWindows.Any(window => window.IsVisible)
                && !page.IsFilenameSearchOpen && !page.IsReaderDialogOpen
                && before is WindowState.Normal or WindowState.Maximized,
                "Actual root-shell regression requires an enabled, dialog-free windowed MainWindow.");
            Button archive = D<Button>(owner, "ArchiveNavigationButton");
            Require(archive.IsEnabled, "Actual Archive navigation handler was not eligible.");
            Dispatch(archive); // Required actual MainWindow handler, even if the nav action is in overflow.
            await UntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Archive
                && D<ContentControl>(owner, "ArchivePageHost").IsVisible && page.IsEffectivelyVisible
                && !page.IsBusy && page.AppliedSearchSpec != null
                && page.FilenameSearchEligibleForTests);
            Require(ReferenceEquals(owner.ArchivePage, page)
                && ReferenceEquals(D<ContentControl>(owner, "ArchivePageHost").Content, page)
                && ReferenceEquals(TopLevel.GetTopLevel(page), owner),
                "Archive navigation did not activate the actual root-owned archive page.");
            page.FilenameSearchShowForTests = async (search, window) =>
            {
                Require(ReferenceEquals(window, owner) && page.IsFilenameSearchOpen && page.IsReaderDialogOpen,
                    "Root filename host did not reserve its actual MainWindow before Show.");
                dialog = search;
                nativeShow = search.ShowDialog<ArchiveFilenameHit?>(window);
                Require(search.IsVisible && ReferenceEquals(search.Owner, owner) && owner.OwnedWindows.Contains(search)
                    && search.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Root show boundary did not create an actual native owned filename dialog.");
                shown.TrySetResult(search);
                ArchiveFilenameHit? actual = await nativeShow;
                Require(actual == null, "Null-close shell regression unexpectedly returned a selected hit.");
                nativeReturned.TrySetResult(true);
                await release.Task; // Hold ONLY the actual Show return, not search, selection, navigation or termination.
                return actual;
            };
            run = page.OpenFilenameSearchAsync(page);
            Require(page.IsFilenameSearchOpen && page.IsReaderDialogOpen && !run.IsCompleted,
                "Actual root filename owner was not synchronously pending.");
            dialog = await shown.Task.WaitAsync(Deadline);
            dialog.Close(); // Actual native close with no query/hit: old native-modal guards must no longer mask admission.
            await nativeShow!.WaitAsync(Deadline);
            await nativeReturned.Task.WaitAsync(Deadline);
            await UntilAsync(() => owner.IsEnabled && !owner.OwnedWindows.Any(window => window.IsVisible));
            RequirePendingRoot();
            Button fullscreen = D<Button>(owner, "FullscreenButton");
            Click(fullscreen); // Real shell Click -> ToggleFullscreen, not the child-page callback.
            Require(owner.WindowState == before, "Actual FullscreenButton bypassed the closed-but-pending filename owner.");
            await TurnAsync();
            RequirePendingRoot();
            owner.ToggleFullscreen(); // Independent assertion: two successful toggles must never cancel each other out.
            Require(owner.WindowState == before, "Direct central ToggleFullscreen bypassed pending filename admission.");
            await TurnAsync();
            RequirePendingRoot();
            Console.WriteLine("SMOKE filename-host-lifetime FNH-P2-01 root-shell: actual ArchiveNavigationButton -> existing root ArchivePage; native Close + held actual Show return; enabled owner/no visible OwnedWindow; actual FullscreenButton and direct ToggleFullscreen preserve WindowState; fullscreen-source="
                + FullscreenAdmissionBasisSha256);
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                release.TrySetResult(true);
                Exception? innerFailureBeforeCleanup = null;
                try
                {
                    if (dialog is { IsVisible: true }) dialog.Close();
                    if (run != null) await run.WaitAsync(Deadline); // Includes the REAL host finally/Terminate join.
                    if (nativeShow != null) await nativeShow.WaitAsync(Deadline);
                }
                catch (Exception thrown) { innerFailureBeforeCleanup = thrown; throw; }
                finally
                {
                    await SmokeCleanup.RunAsync(innerFailureBeforeCleanup, async () =>
                    {
                        page.FilenameSearchShowForTests = previousShow;
                        // Restore only AFTER the negative assertions and real host join; never use state assignment as their oracle.
                        if (owner.WindowState != before) owner.WindowState = before;
                        Dispatch(D<Button>(owner, "DownloadsNavigationButton"));
                        await UntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Downloads
                            && D<Grid>(owner, "WorkspaceBody").IsVisible && !page.IsEffectivelyVisible);
                    }, exception => Console.Error.WriteLine("SMOKE cleanup failed in FilenameSearchHostLifetimeChecks: " + exception));
                }
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in FilenameSearchHostLifetimeChecks: " + exception));
        }
        Require(!page.IsFilenameSearchOpen && !page.IsReaderDialogOpen && page.FilenameSearchWindow == null,
            "Root shell cleanup did not join and release the actual filename reservation.");

        void RequirePendingRoot() => Require(!dialog!.IsVisible && dialog.Session.Current.Closed
            && owner.IsEnabled && !owner.OwnedWindows.Any(window => window.IsVisible)
            && page.IsFilenameSearchOpen && page.IsReaderDialogOpen && !run!.IsCompleted
            && owner.SelectedPage == MainWindow.WorkspacePage.Archive && page.IsEffectivelyVisible
            && owner.WindowState == before,
            "Actual root owner/closed dialog/held Show return changed before release (including WindowState).");
    }

    private static async Task SeedAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in new[] { Baseline, Target })
        {
            CapturedPost[] posts = Enumerable.Range(0, 3).Select(i => new CapturedPost(
                (int.Parse(id.Thread!) + i).ToString(), i, PostFieldPresence.All,
                Html: "<p>Offline inert lifetime fixture</p>", Attachments: id == Target && i == 2
                    ? new[] { new CapturedAttachment(0, "synthetic-original", OriginalFileName: Needle + ".png") }
                    : Array.Empty<CapturedAttachment>())).ToArray();
            await catalog.CaptureAsync(new(id, "https://boards.4chan.org/g/thread/" + id.Thread,
                "Synthetic lifetime metadata", ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch,
                Path.Combine(profile, "media-not-created"), posts));
        }
    }

    private static async Task RequireDisposedAsync(ArchiveReader reader)
    {
        bool disposed = false;
        try { await reader.ReadThreadSummaryAsync(Target).WaitAsync(Deadline); }
        catch (ObjectDisposedException) { disposed = true; }
        Require(disposed, "Real reader returned after cancellation was not disposed by its actual owner.");
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T D<T>(Control root, string name) where T : Control
    {
        // These are XAML roots: registered controls can be hidden in an overflow
        // or not yet attached during Show. Find the actual NameScope object before
        // testing its type; never substitute another visually similar control.
        if (root is ArchivePage or MainWindow or SavedArchiveSearchesDialog)
        {
            Control? named = root.FindControl<Control>(name);
            return named is T xamlControl ? xamlControl : throw new InvalidOperationException(
                "Missing or wrong-type named filename lifetime XAML control: " + name);
        }
        // Code-built dialogs have no XAML NameScope. Check ordinal name uniqueness BEFORE expected T.
        Control[] matches = root.GetVisualDescendants().OfType<Control>().Prepend(root)
            .Where(control => string.Equals(control.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Expected exactly one actual filename lifetime control: " + name
                + "; found " + matches.Length);
        return matches[0] is T typed ? typed
            : throw new InvalidOperationException("Wrong actual filename lifetime control type: " + name);
    }
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyEnabled && button.IsEffectivelyVisible, "Cannot click actual disabled/hidden action: " + button.Name);
        Dispatch(button);
    }
    private static void Dispatch(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task TurnAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.VerifyAccess();
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Filename host lifetime UI condition timed out.");
            await TurnAsync(); // Dispatcher progress only; no sleep, delay or elapsed-time correctness oracle.
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ReaderBarrier
    {
        internal readonly TaskCompletionSource<CancellationToken> Entered = Signal<CancellationToken>();
        internal readonly TaskCompletionSource<bool> Release = Signal<bool>();
        internal readonly TaskCompletionSource<ArchiveReader> Returned = Signal<ArchiveReader>();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ArchivePage Page;
        internal readonly Window Window;
        private readonly Window _focusWindow;
        private readonly TextBox _focusTarget = new() { Text = "Independent focus owner" };
        private readonly string _profile;
        private readonly List<Action> _release = new();
        private ReaderBarrier? _nextReader;
        private TaskCompletionSource<bool>? _showRelease;
        private bool _observeFocus;
        private int _lateFocus;
        internal int Readers, MediaReads, Shows, UrlReads, UrlShows, PreviewRoots, PreviewShows, ExternalActions;
        internal int ReaderGroups, PostBookmarkManagers;
        internal bool ReservedAtShow;
        internal TaskCompletionSource<FilenameSearchDialog> Shown { get; private set; } = Signal<FilenameSearchDialog>();
        internal TaskCompletionSource<bool> ShowReturned { get; private set; } = Signal<bool>();
        internal Task<ArchiveFilenameHit?>? NativeShow { get; private set; }
        internal Task? ShowFailure;
        // AP2: the reader find bar in the File name scope is the product entry; its Search button is the origin.
        internal Button Origin => C<Button>("ReaderFindSearch");
        internal SplitButton SavedSearches => C<SplitButton>("ArchiveSavedSearches");
        internal MenuItem SaveSearch => Page.SaveCurrentSearchMenuItem;
        internal void DispatchSavedSearch(bool saveCurrent)
        {
            // Forced like a routed click: the real handler must guard, not the enabled appearance.
            if (saveCurrent) SaveSearch.Command!.Execute(null);
            else SavedSearches.RaiseEvent(new RoutedEventArgs(SplitButton.ClickEvent));
        }
        internal Button BookmarksEntry => C<ActionToolBar>("ArchiveResultActions").Items.OfType<Button>()
            .Single(button => button.Name == "ArchivePostBookmarks");

        private Fixture(MainWindow owner, string profile)
        {
            _profile = profile;
            Page = new ArchivePage(OpenReaderAsync,
                (_, _) => { ExternalActions++; return Task.CompletedTask; }, _ => ExternalActions++,
                (directory, _) =>
                {
                    Interlocked.Increment(ref MediaReads);
                    // Existing manifest seam isolates unrelated reader projection. Never open media files.
                    return Task.FromResult(new MediaAssetSnapshot(directory, MediaManifestStatus.Missing, null, null,
                        Array.Empty<MediaAssetReference>()));
                }) { ArchiveLayoutPreference = "Table", ArchiveColumnModePreference = "Advanced" };
            Page.ReadArchiveMediaInventoryAsync = (_, _) => Task.FromResult(new ArchiveThreadMediaCount(null));
            Page.AttachArchiveCleanup(owner.SessionHost);
            Page.AttachSavedSearches(owner.SessionHost);
            Page.OpenReaderGroupsRequested = _ => { ReaderGroups++; return Task.CompletedTask; };
            Page.OpenPostBookmarks = () => PostBookmarkManagers++;
            Page.OpenGallery = (_, _, _) => { ExternalActions++; return Task.CompletedTask; };
            Page.OpenResultsGallery = (_, _, _) => { ExternalActions++; return Task.CompletedTask; };
            Page.OpenReaderRequested = _ => ExternalActions++;
            Page.CloseReaderRequested = () => ExternalActions++;
            Page.FullscreenRequested = () => ExternalActions++;
            Page.UrlTextSummaryForTests = (_, _) => { UrlReads++; return Task.FromResult<ArchiveThreadSummary?>(null); };
            Page.UrlTextShowForTests = (dialog, window) =>
            {
                UrlShows++;
                Task shown = dialog.ShowDialog(window);
                dialog.Close();
                return shown;
            };
            Page.TransferPreviewWorkDirectoryCreatedForTests = _ => PreviewRoots++;
            Page.TransferPreviewShowForTests = (dialog, window) =>
            {
                PreviewShows++;
                Task shown = dialog.ShowDialog(window);
                dialog.Close();
                return shown;
            };
            Page.AddHandler(InputElement.GotFocusEvent, (_, _) => { if (_observeFocus) _lateFocus++; },
                RoutingStrategies.Bubble, handledEventsToo: true);
            Window = new Window { Content = Page, Width = 1240, Height = 820,
                ShowInTaskbar = false, Title = "Synthetic filename lifetime owner" };
            _focusWindow = new Window { Content = _focusTarget, Width = 420, Height = 180,
                ShowInTaskbar = false, Title = "Synthetic independent focus witness" };
            InstallShow();
        }

        internal static async Task<Fixture> CreateAsync(MainWindow owner, string profile)
        {
            Fixture f = new(owner, profile);
            try
            {
                f.Window.Show(owner);
                f._focusWindow.Show(owner); // Sibling of the page owner: remains alive when that owner closes.
                f.Page.IsAvailable = true;
                await f.Page.ActivateAsync().WaitAsync(Deadline);
                await f.Page.ArchiveMediaCountTask.WaitAsync(Deadline);
                var counts = Signal<bool>();
                f.Page.BeforeInlineReplyCountsPublicationAsync = _ => { counts.TrySetResult(true); return Task.CompletedTask; };
                await f.Page.LoadThreadAsync(Baseline).WaitAsync(Deadline);
                await f.Page.MediaLoadTask.WaitAsync(Deadline);
                await counts.Task.WaitAsync(Deadline);
                await TurnAsync();
                UnifiedSearchEntry.ReaderScope(f.Page, SearchScope.FileName);
                await f.Page.ThreadFindTask.WaitAsync(Deadline);
                f.Window.UpdateLayout();
                Require(f.Window.IsVisible && f.Page.IsEffectivelyVisible && f.Page.CurrentThread?.Summary.Id == Baseline
                    && f.Window.TryGetPlatformHandle()?.Handle is nint handle && handle != 0
                    && f.Origin.IsEffectivelyVisible && f.Origin.IsEffectivelyEnabled,
                    "Native page/reader/entry baseline was not actually reachable.");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        private async Task<ArchiveReader> OpenReaderAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Readers);
            ReaderBarrier? barrier = Interlocked.Exchange(ref _nextReader, null);
            if (barrier == null) return await ArchiveReader.OpenAsync(_profile, token).ConfigureAwait(false);
            barrier.Entered.TrySetResult(token);
            await barrier.Release.Task.ConfigureAwait(false); // Deliberately uncooperative until explicitly released.
            ArchiveReader reader = await ArchiveReader.OpenAsync(_profile, CancellationToken.None).ConfigureAwait(false);
            barrier.Returned.TrySetResult(reader);
            return reader; // Ownership transfers to the REAL session/host, whose disposal is asserted.
        }
        internal ReaderBarrier HoldNextReader()
        {
            ReaderBarrier barrier = new();
            Require(Interlocked.CompareExchange(ref _nextReader, barrier, null) == null, "Reader barrier already armed.");
            _release.Add(() => barrier.Release.TrySetResult(true));
            return barrier;
        }
        internal TaskCompletionSource<bool> Hold()
        {
            var signal = Signal<bool>();
            _release.Add(() => signal.TrySetResult(true));
            return signal;
        }
        internal TaskCompletionSource<bool> HoldShowReturn() => _showRelease = Hold();
        internal void InstallShow()
        {
            Shown = Signal<FilenameSearchDialog>();
            ShowReturned = Signal<bool>();
            Page.FilenameSearchShowForTests = async (dialog, window) =>
            {
                Dispatcher.UIThread.VerifyAccess();
                Shows++;
                ReservedAtShow = Page.IsFilenameSearchOpen && Page.IsReaderDialogOpen
                    && !C<Button>("SearchArchiveButton").IsEnabled
                    && !C<Button>("ReaderFindSearch").IsEnabled;
                Require(ReferenceEquals(window, Window), "Host used another or fabricated modal owner.");
                NativeShow = dialog.ShowDialog<ArchiveFilenameHit?>(window);
                Require(dialog.IsVisible && ReferenceEquals(dialog.Owner, Window)
                    && Window.OwnedWindows.Contains(dialog)
                    && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Show seam did not create the real native owned filename window.");
                Shown.TrySetResult(dialog);
                if (ShowFailure is { } fail)
                {
                    await fail;
                    throw new InvalidOperationException("PRIVATE SYNTHETIC SHOW FAILURE");
                }
                ArchiveFilenameHit? actual = await NativeShow;
                ShowReturned.TrySetResult(true);
                if (_showRelease != null) await _showRelease.Task;
                return actual; // Never fabricate a successful Show result.
            };
        }
        internal async Task SearchReadyAsync(FilenameSearchDialog dialog)
        {
            dialog.Session.Edit(Needle);
            await dialog.Session.SearchAsync().WaitAsync(Deadline);
            await TurnAsync();
            Require(dialog.Session.Current.StatusKey == "filename.search.ready" && dialog.Session.Current.Rows.Length == 1,
                "Real synthetic catalog search did not supply one selectable original row.");
        }
        internal void SelectFirst(FilenameSearchDialog dialog)
        {
            D<ListBox>(dialog, "FilenameSearchResults").SelectedIndex = 0;
            Click(D<Button>(dialog, "FilenameSearchSelect"));
        }
        internal string[] PostIds() => C<ListBox>("ArchivePosts").ItemsSource!.Cast<ArchivePostRow>().Select(row => row.Id).ToArray();
        internal T C<T>(string name) where T : Control => D<T>(Page, name);
        internal void End(Boundary boundary)
        {
            switch (boundary)
            {
                case Boundary.OwnerClose: Window.Close(); break;
                case Boundary.Unavailable: Page.IsAvailable = false; break;
                case Boundary.EndLifetime: Page.EndLifetime(); Page.EndLifetime(); break;
            }
        }
        internal async Task FocusAwayAsync()
        {
            var active = Signal<bool>();
            void Activated(object? sender, EventArgs e) => active.TrySetResult(true);
            _focusWindow.Activated += Activated;
            try
            {
                _focusWindow.Activate();
                if (!_focusWindow.IsActive) await active.Task.WaitAsync(Deadline);
                Require(_focusTarget.Focus(), "Independent native focus witness could not focus.");
                await TurnAsync();
                Require(_focusWindow.IsActive && ReferenceEquals(_focusWindow.FocusManager?.GetFocusedElement(), _focusTarget),
                    "Independent focus witness was not established before late work release.");
                _observeFocus = true;
            }
            finally { _focusWindow.Activated -= Activated; }
        }
        internal void RequireNoLateFocus() => Require(_lateFocus == 0 && _focusWindow.IsActive
            && ReferenceEquals(_focusWindow.FocusManager?.GetFocusedElement(), _focusTarget),
            "Late filename completion refocused/reactivated its old page instead of preserving newer focus.");
        public async ValueTask DisposeAsync()
        {
            Dispatcher.UIThread.VerifyAccess();
            _observeFocus = false;
            Page.EndLifetime();
            foreach (Action release in _release) release();
            try
            {
                await Page.FilenameSearchTask.WaitAsync(Deadline);
                if (NativeShow != null) await NativeShow.WaitAsync(Deadline);
                await Page.MediaLoadTask.WaitAsync(Deadline);
                await Page.ArchiveMediaCountTask.WaitAsync(Deadline);
                await Page.ThreadFindTask.WaitAsync(Deadline);
            }
            finally { _focusWindow.Close(); Window.Close(); }
        }
    }
}
