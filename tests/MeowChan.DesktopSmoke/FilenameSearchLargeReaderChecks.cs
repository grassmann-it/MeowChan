using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Separate from UI03. Root must explicitly await RunAsync after actual MainWindow Ready.
// Production DetachReader/AttachReader and real native windows, not reflected large-reader flags.
// Managed routed buttons / Text / SelectedIndex are fixture input, NOT OS keyboard/pointer input.
internal static class FilenameSearchLargeReaderChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId Origin = new("synthetic-large-A", "board-A", "31000");
    private static readonly ResourceId Target = new("synthetic-large-B", "board-B", "32000");
    private const string Post = "p250";
    private const string Needle = "wave24-large-filename";

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.SessionHost.State == DesktopSessionState.Ready && owner.IsVisible
            && owner.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
            "Large-reader filename checks require actual Ready/native owner.");
        foreach (string scenario in new[] { "select", "null", "tab-return", "stale-revision",
            "close-navigation", "reattach-navigation", "close-search" })
            await JourneyAsync(owner, scenario);
        // Every Journey includes its awaited finally/disposal before this marker.
        Console.WriteLine("SMOKE filename-large-reader complete: seven journeys and real host/session joins completed; source fixture, managed input, no shell/OS/WinMac claim.");
    }

    private static async Task JourneyAsync(MainWindow owner, string scenario)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        ArchivedThreadView before = f.Page.CurrentThread!;
        long revision = before.Summary.Revision;
        string[] rows = f.Rows();
        object? selected = f.Posts.SelectedItem;
        Require(before.Summary.Id == Origin && before.PostCount == 251
            && before.FindPostIndex(Post) == 250 && rows.SequenceEqual(new[] { Post })
            && selected is ArchivePostRow { Id: Post }, "Nontrivial original revision/page/selection missing.");
        if (scenario == "tab-return") f.DelayReturn = f.Hold();
        Click(f.Named<Button>("ReaderFilenameSearchOpen"));
        Task run = f.Page.FilenameSearchTask;
        FilenameSearchDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        Require(f.Page.IsFilenameSearchOpen && f.Page.IsReaderDialogOpen && !run.IsCompleted
            && ReferenceEquals(dialog.Owner, f.ReaderWindow), "Large surface did not reserve actual owner.");
        if (scenario == "null")
        {
            Click(Named<Button>(dialog, "FilenameSearchClose"));
            Require(await f.NativeShow!.WaitAsync(Deadline) == null, "Close fabricated a selected hit.");
            await run.WaitAsync(Deadline);
            Unchanged();
        }
        else if (scenario == "close-search")
        {
            Barrier held = f.Arm();
            Named<TextBox>(dialog, "FilenameSearchQuery").Text = Needle;
            await SmokeDispatcher.SettleAsync();
            Click(Named<Button>(dialog, "FilenameSearchSubmit"));
            CancellationToken token = await held.Entered.Task.WaitAsync(Deadline);
            Require(dialog.Session.Current.Busy && dialog.IsVisible && !run.IsCompleted,
                "Search barrier did not hold actual visible request.");
            f.ReaderWindow.Close(); // Actual owner Closing invalidates and closes native modal.
            await f.NativeShow!.WaitAsync(Deadline);
            Task drain = dialog.TerminateAsync();
            Require(token.IsCancellationRequested && !dialog.IsVisible && dialog.Session.Current.Closed
                && !drain.IsCompleted && !run.IsCompleted && f.Page.IsFilenameSearchOpen,
                "Owner close abandoned pending search or released reservation before drain.");
            held.Release.TrySetResult(true);
            await Task.WhenAll(run, drain).WaitAsync(Deadline);
            await DisposedAsync(await held.Returned.Task.WaitAsync(Deadline));
            Unchanged();
        }
        else
        {
            Named<TextBox>(dialog, "FilenameSearchQuery").Text = Needle;
            await SmokeDispatcher.SettleAsync();
            Click(Named<Button>(dialog, "FilenameSearchSubmit"));
            await UntilAsync(() => dialog.Session.Current is { Busy: false, StatusKey: "filename.search.ready" });
            var hits = dialog.Session.Current.Rows;
            Require(hits.Length == 4 && dialog.Session.Current.Total == 4,
                "Complete catalog search must include both claims in both distinct resources.");
            Require(hits.Count(hit => hit.Thread == Origin && hit.Post == "p000") == 1
                && !rows.Contains("p000", StringComparer.Ordinal)
                && hits.All(hit => (hit.Thread == Origin || hit.Thread == Target)
                    && (hit.Post == "p000" || hit.Post == Post)
                    && hit.OriginalName == Needle + ".png"),
                "Actual search omitted the current reader's off-page claim or returned an unexpected claim.");
            ListBox results = Named<ListBox>(dialog, "FilenameSearchResults");
            // The dialog renders session changes through a posted Refresh; give that turn a chance before judging.
            try { await UntilAsync(() => results.Items.Count == 4); }
            catch (TimeoutException) { Require(false, "Actual native result list did not bind all four session hits."); }
            int index = Enumerable.Range(0, hits.Length).Single(i => hits[i].Thread == Target && hits[i].Post == Post);
            ArchiveFilenameHit hit = hits[index];
            Require(hit.Post == Post && hit.Position == 3 && hit.Variant == "preview"
                && hit.OriginalName == Needle + ".png" && ReferenceEquals(f.Page.CurrentThread, before),
                "Actual filename search lost identity or navigated before selection.");
            if (scenario == "stale-revision")
            {
                await f.ReplaceTargetAsync();
                await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Profile);
                Require((await reader.ReadThreadAsync(Target))!.Summary.Revision != hit.CatalogThreadRevision,
                    "Stale-hit fixture failed to advance actual catalog revision.");
            }
            Barrier? navigation = scenario is "select" or "close-navigation" or "reattach-navigation" ? f.Arm() : null;
            Named<ListBox>(dialog, "FilenameSearchResults").SelectedIndex = index;
            await SmokeDispatcher.SettleAsync();
            Click(Named<Button>(dialog, "FilenameSearchSelect"));
            ArchiveFilenameHit? actual = await f.NativeShow!.WaitAsync(Deadline);
            Require(ReferenceEquals(actual, hit), "Real native modal did not return exact immutable selected hit.");
            if (scenario == "select")
            {
                CancellationToken positiveToken = await navigation!.Entered.Task.WaitAsync(Deadline);
                Require(!positiveToken.IsCancellationRequested && !run.IsCompleted && !dialog.IsVisible
                    && dialog.Session.Current.Closed && f.Page.IsFilenameSearchOpen
                    && ReferenceEquals(before, f.Page.CurrentThread),
                    "Positive return did not retain actual reservation while navigation reader was held.");
                navigation.Release.TrySetResult(true);
                await run.WaitAsync(Deadline);
                await DisposedAsync(await navigation.Returned.Task.WaitAsync(Deadline));
                navigation = null; // Continue to full positive resource/revision/selection assertions below.
            }
            if (navigation != null)
            {
                CancellationToken token = await navigation.Entered.Task.WaitAsync(Deadline);
                Require(!run.IsCompleted && !dialog.IsVisible && dialog.Session.Current.Closed
                    && f.Page.IsFilenameSearchOpen, "Navigation factory not held after actual selected modal return.");
                if (scenario == "close-navigation") f.ReaderWindow.Close();
                else
                {
                    f.Reattach(); // Actual AttachReader legitimately re-renders rows; not late filename publication.
                    Require(ReferenceEquals(before, f.Page.CurrentThread)
                        && f.Page.CurrentThread.Summary.Revision == revision && f.Rows().SequenceEqual(rows)
                        && f.Posts.SelectedItem is ArchivePostRow { Id: Post },
                        "Reattach itself lost old revision/page/selected post.");
                    selected = f.Posts.SelectedItem; // Exact post-boundary object must now survive late read return.
                }
                Require(token.IsCancellationRequested && !run.IsCompleted && f.Page.IsFilenameSearchOpen,
                    "Surface invalidation failed to retain/cancel held navigation owner.");
                await f.Page.OpenFilenameSearchAsync(f.Pane).WaitAsync(Deadline);
                Require(ReferenceEquals(run, f.Page.FilenameSearchTask) && f.Shows == 1,
                    "Invalidated operation was replaced before its real return.");
                navigation.Release.TrySetResult(true);
                await run.WaitAsync(Deadline);
                await DisposedAsync(await navigation.Returned.Task.WaitAsync(Deadline));
                Unchanged();
            }
            else
            {
                if (scenario == "tab-return")
                {
                    await f.Returned.Task.WaitAsync(Deadline);
                    await UntilAsync(() => f.ReaderWindow.IsEnabled);
                    Require(!run.IsCompleted && f.Page.IsFilenameSearchOpen && !dialog.IsVisible,
                        "Actual selected return was not held after native Close.");
                    // A reader content tab is NOT a workspace/surface invalidation. The pane survives.
                    f.Named<TabControl>("ArchiveContentTabs").SelectedIndex = 1;
                    await SmokeDispatcher.SettleAsync();
                    Require(f.Named<TabControl>("ArchiveContentTabs").SelectedIndex == 1
                        && f.Named<TabItem>("ArchiveMediaTab").IsSelected && f.Pane.IsEffectivelyVisible
                        && ReferenceEquals(TopLevel.GetTopLevel(f.Pane), f.ReaderWindow)
                        && ReferenceEquals(f.Page.CurrentThread, before) && !run.IsCompleted,
                        "Real tab switch replaced surface or prematurely applied held hit.");
                    f.DelayReturn!.TrySetResult(true);
                }
                await run.WaitAsync(Deadline);
                await f.Page.MediaLoadTask.WaitAsync(Deadline);
                await SmokeDispatcher.SettleAsync();
                if (scenario == "stale-revision") Unchanged();
                else
                {
                    ArchivedThreadView view = f.Page.CurrentThread!;
                    Require(view.Summary.Id == Target && view.Summary.Revision == hit.CatalogThreadRevision
                        && !ReferenceEquals(view, before) && view.FindPostIndex(Post) == 250
                        && f.Rows().SequenceEqual(new[] { Post }) && f.Posts.SelectedItem is ArchivePostRow { Id: Post },
                        "Positive large-reader return lost exact resource/revision/offscreen page/selection.");
                    CapturedAttachment attachment = view.GetPosts(250, 1).Single().Attachments!.Single();
                    Require(attachment.Position == hit.Position && attachment.Variant == hit.Variant
                        && attachment.OriginalFileName == hit.OriginalName
                        && attachment.AttachmentRevisionKey == hit.AttachmentRevisionKey,
                        "Large-reader return reconstructed a different attachment claim.");
                    Require(f.ReaderWindow.IsVisible && f.Pane.IsEffectivelyVisible
                        && ReferenceEquals(f.ReaderWindow.Content, f.Pane)
                        && ReferenceEquals(TopLevel.GetTopLevel(f.Posts), f.ReaderWindow)
                        && f.Named<TabControl>("ArchiveContentTabs").SelectedIndex == 0,
                        "Successful hit did not restore actual detached large-reader posts surface.");
                    if (scenario == "tab-return")
                    {
                        object? targetSelection = f.Posts.SelectedItem;
                        f.Named<TabControl>("ArchiveContentTabs").SelectedIndex = 1;
                        await SmokeDispatcher.SettleAsync();
                        f.Named<TabControl>("ArchiveContentTabs").SelectedIndex = 0;
                        await SmokeDispatcher.SettleAsync();
                        Require(ReferenceEquals(view, f.Page.CurrentThread)
                            && ReferenceEquals(targetSelection, f.Posts.SelectedItem) && f.Rows().SequenceEqual(new[] { Post }),
                            "Post-return tab roundtrip changed frozen revision or selected post.");
                    }
                    // Positive large-reader close is admitted only AFTER real filename return/drain.
                    Click(f.Named<Button>("ArchiveCloseReader"));
                    Require(!f.ReaderWindow.IsVisible && !f.Page.IsFilenameSearchOpen,
                        "Actual large-reader Close entry failed after successful filename return.");
                }
            }
        }
        Require(!f.Page.IsFilenameSearchOpen && !f.Page.IsReaderDialogOpen
            && f.Page.FilenameSearchWindow == null && !dialog.IsVisible && f.ExternalActions == 0,
            "Completed large-reader journey leaked ownership/window or external side effects.");
        Console.WriteLine("SMOKE filename-large-reader " + scenario + ": exact return/lifetime assertions reached; cleanup still required.");

        void Unchanged() => Require(ReferenceEquals(before, f.Page.CurrentThread)
            && f.Page.CurrentThread.Summary.Revision == revision && f.Rows().SequenceEqual(rows)
            && ReferenceEquals(selected, f.Posts.SelectedItem),
            "Rejected/null/invalidated return changed old snapshot revision/page/selection.");
    }

    private sealed class Barrier
    {
        internal readonly TaskCompletionSource<CancellationToken> Entered = Signal<CancellationToken>();
        internal readonly TaskCompletionSource<bool> Release = Signal<bool>();
        internal readonly TaskCompletionSource<ArchiveReader> Returned = Signal<ArchiveReader>();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Profile = TemporaryWorkspacePaths.CreateDirectory("filename-large-reader-");
        internal readonly ArchivePage Page;
        internal readonly Window ReaderWindow;
        internal readonly Window PageWindow;
        internal readonly Control Pane;
        internal readonly TaskCompletionSource<FilenameSearchDialog> Shown = Signal<FilenameSearchDialog>();
        internal readonly TaskCompletionSource<bool> Returned = Signal<bool>();
        internal TaskCompletionSource<bool>? DelayReturn;
        internal Task<ArchiveFilenameHit?>? NativeShow;
        internal int Shows, ExternalActions;
        private Barrier? _next;
        private readonly List<Barrier> _barriers = new();
        private bool _attached;
        internal ListBox Posts => Named<ListBox>("ArchivePosts");
        internal T Named<T>(string name) where T : Control => FilenameSearchLargeReaderChecks.Named<T>(_attached ? (Control)Page : Pane, name);
        internal string[] Rows() => Posts.ItemsSource!.Cast<ArchivePostRow>().Select(row => row.Id).ToArray();

        private Fixture()
        {
            Page = new ArchivePage(OpenAsync, (_, _) => { ExternalActions++; return Task.CompletedTask; }, _ => ExternalActions++,
                (directory, _) => Task.FromResult(new MediaAssetSnapshot(directory, MediaManifestStatus.Missing, null, null,
                    Array.Empty<MediaAssetReference>())));
            Page.ReadArchiveMediaInventoryAsync = (_, _) => Task.FromResult(new ArchiveThreadMediaCount(null));
            Page.OpenGallery = (_, _, _) => { ExternalActions++; return Task.CompletedTask; };
            Page.OpenMediaGallery = (_, _, _) => { ExternalActions++; return Task.CompletedTask; };
            Page.OpenResultsGallery = (_, _, _) => { ExternalActions++; return Task.CompletedTask; };
            Page.FullscreenRequested = () => ExternalActions++;
            Pane = Page.DetachReader();
            PageWindow = new Window { Content = Page, Width = 1120, Height = 820, ShowInTaskbar = false,
                Title = "Synthetic filename archive backing page" };
            ReaderWindow = new Window { Content = Pane, Width = 1120, Height = 820, ShowInTaskbar = false,
                Title = "Synthetic actual detached filename reader" };
            Page.CloseReaderRequested = () => ReaderWindow.Close();
            Page.FilenameSearchShowForTests = async (dialog, window) =>
            {
                Shows++;
                Require(ReferenceEquals(window, ReaderWindow) && Page.IsFilenameSearchOpen
                    && ReferenceEquals(TopLevel.GetTopLevel(Pane), ReaderWindow), "Filename host selected backing page/incorrect owner.");
                NativeShow = dialog.ShowDialog<ArchiveFilenameHit?>(window);
                Require(dialog.IsVisible && ReferenceEquals(dialog.Owner, ReaderWindow)
                    && ReaderWindow.OwnedWindows.Contains(dialog)
                    && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Show seam did not create actual native owned dialog.");
                Shown.TrySetResult(dialog);
                ArchiveFilenameHit? actual = await NativeShow;
                Returned.TrySetResult(true);
                if (DelayReturn != null) await DelayReturn.Task;
                return actual;
            };
        }
        internal static async Task<Fixture> CreateAsync(MainWindow owner)
        {
            Fixture f = new();
            try
            {
                using (ProfileLease lease = ProfileLease.Acquire(f.Profile))
                await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(f.Profile, lease))
                    foreach (ResourceId id in new[] { Origin, Target }) await catalog.CaptureAsync(f.Snapshot(id, "initial"));
                f.PageWindow.Show(owner);
                f.ReaderWindow.Show(owner);
                f.Page.IsAvailable = true;
                var counts = Signal<bool>();
                f.Page.BeforeInlineReplyCountsPublicationAsync = _ => { counts.TrySetResult(true); return Task.CompletedTask; };
                await f.Page.LoadThreadAsync(Origin).WaitAsync(Deadline);
                await counts.Task.WaitAsync(Deadline);
                Require(f.Page.CurrentThread is { PostCount: 251 }
                    && f.Rows().Length == 50 && f.Rows()[0] == "p000" && !f.Rows().Contains(Post),
                    "Initial production reader page must exclude the eventual p250 navigation target.");
                await f.Page.MediaLoadTask.WaitAsync(Deadline);
                await f.Page.ArchiveMediaCountTask.WaitAsync(Deadline);
                var finalCounts = Signal<bool>();
                f.Page.BeforeInlineReplyCountsPublicationAsync = _ => { finalCounts.TrySetResult(true); return Task.CompletedTask; };
                f.Page.ShowPost(Post);
                await finalCounts.Task.WaitAsync(Deadline);
                await SmokeDispatcher.SettleAsync();
                f.ReaderWindow.UpdateLayout();
                Require(f.ReaderWindow.IsVisible && f.ReaderWindow.TryGetPlatformHandle()?.Handle is nint handle && handle != 0
                    && f.Pane.IsEffectivelyVisible && ReferenceEquals(TopLevel.GetTopLevel(f.Pane), f.ReaderWindow)
                    && !ReferenceEquals(TopLevel.GetTopLevel(f.Page), f.ReaderWindow)
                    && f.Named<Button>("ArchiveCloseReader").IsVisible
                    && f.Named<Button>("ReaderFilenameSearchOpen").IsEffectivelyVisible,
                    "Production detached large-reader surface not actually attached/reachable.");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal TaskCompletionSource<bool> Hold() => Signal<bool>();
        internal Barrier Arm()
        {
            Barrier barrier = new();
            Require(Interlocked.CompareExchange(ref _next, barrier, null) == null, "Reader already armed.");
            _barriers.Add(barrier);
            return barrier;
        }
        private async Task<ArchiveReader> OpenAsync(CancellationToken token)
        {
            Barrier? barrier = Interlocked.Exchange(ref _next, null);
            if (barrier == null) return await ArchiveReader.OpenAsync(Profile, token).ConfigureAwait(false);
            barrier.Entered.TrySetResult(token);
            await barrier.Release.Task.ConfigureAwait(false);
            ArchiveReader reader = await ArchiveReader.OpenAsync(Profile, CancellationToken.None).ConfigureAwait(false);
            barrier.Returned.TrySetResult(reader);
            return reader; // Real consumer owns/disposes it; no fake reader or cancelled-task shortcut.
        }
        internal void Reattach()
        {
            // Same production snapshot/anchor restoration primitive used by MainWindow's close path.
            // AttachReader re-renders rows; restoring an actual captured origin is not assigning fake fields.
            Action restore = Page.CaptureGalleryOrigin(restoreReaderFocus: false);
            ReaderWindow.Content = null;
            Page.AttachReader(Pane);
            _attached = true;
            restore();
            Require(ReferenceEquals(TopLevel.GetTopLevel(Pane), PageWindow), "Actual pane not reattached to backing page.");
        }
        internal async Task ReplaceTargetAsync()
        {
            using ProfileLease lease = ProfileLease.Acquire(Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(Profile, lease);
            await catalog.CaptureAsync(Snapshot(Target, "changed revision after hit publication"));
        }
        private CapturedThreadSnapshot Snapshot(ResourceId id, string text) => new(id,
            "https://example.invalid/not-requested", "Synthetic large reader", ThreadSourceState.NotFound,
            DateTimeOffset.UnixEpoch, Path.Combine(Profile, "uncreated-media"),
            Enumerable.Range(0, 251).Select(i => new CapturedPost("p" + i.ToString("D3"), i,
                PostFieldPresence.All, Html: text, Attachments: i is 0 or 250
                    ? new[] { new CapturedAttachment(3, "synthetic-media", "preview", OriginalFileName: Needle + ".png") }
                    : Array.Empty<CapturedAttachment>())).ToArray());
        public async ValueTask DisposeAsync()
        {
            Page.EndLifetime();
            DelayReturn?.TrySetResult(true);
            foreach (Barrier barrier in _barriers) barrier.Release.TrySetResult(true);
            try
            {
                await Page.FilenameSearchTask.WaitAsync(Deadline);
                if (NativeShow != null) await NativeShow.WaitAsync(Deadline);
                await Page.MediaLoadTask.WaitAsync(Deadline);
                await Page.ArchiveMediaCountTask.WaitAsync(Deadline);
            }
            finally
            {
                try { ReaderWindow.Close(); }
                finally { PageWindow.Close(); }
            }
            Console.WriteLine("SMOKE filename-large-reader cleanup: actual task joins and native window closes reached.");
            // Synthetic evidence retained, never recursive cleanup or user profile/settings writes.
        }
    }
    private static async Task DisposedAsync(ArchiveReader reader)
    {
        bool disposed = false;
        try { await reader.ReadThreadSummaryAsync(Target).WaitAsync(Deadline); }
        catch (ObjectDisposedException) { disposed = true; }
        Require(disposed, "Late real reader was not disposed by actual filename consumer.");
    }
    private static T Named<T>(Control root, string name) where T : Control
    {
        Control[] matches = root.GetVisualDescendants().OfType<Control>().Prepend(root)
            .Where(control => string.Equals(control.Name, name, StringComparison.Ordinal)).ToArray();
        Require(matches.Length == 1, "Expected unique actual large-reader control: " + name + "; count=" + matches.Length);
        return matches[0] is T typed ? typed : throw new InvalidOperationException("Wrong control type: " + name);
    }
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Hidden/disabled large-reader action: " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Large-reader filename condition not reached.");
            await SmokeDispatcher.SettleAsync();
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
