using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Opt-in standalone entry, no edits to shared selectors, projects or existing assertions.
// Build normally, then rebuild ONLY this project with --no-restore -p:BuildProjectReferences=false
// -p:DefineConstants=CX02_GALLERY_EXPORT_CLOSURE
// -p:StartupObject=MeowChan.DesktopSmoke.Cx02GalleryExportClosureChecks .
// Run via scripts/run-desktop-smoke.py --mode normal on its disposable private Xvfb.
// The same entry supports --source-contracts <source-root> and --managed without starting a GUI.
internal static class Cx02GalleryExportClosureChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);
    private static readonly ResourceId First = new("closure", "local", "1"), Second = new("closure", "local", "2");

#if CX02_GALLERY_EXPORT_CLOSURE
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--source-contracts", var root]) { VerifySourceContracts(root); return 0; }
        if (args is ["--managed"]) { VerifyManagedContracts(); return 0; }
        if (args.Length != 0 || !OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            || Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE") is not { Length: > 0 } profile)
            throw new InvalidOperationException("CX02 requires the disposable private-Xvfb runner.");
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        VerifyManagedContracts();
        Directory.CreateDirectory(profile);
        new Config { DownloadPath = Path.Combine(profile, "Downloads"), DownloadsPaused = true,
            SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false, MonitorClipboardUrls = false,
            AutomaticUpdateChecks = false, FirstRunSetupPending = false, FirstRunSetupVersion = 1,
            Language = "en", WorkspaceMode = "Advanced" }.SaveConfig(profile);
        // Existing owned local fixture: verified PNGs + exact post bindings, no remote fetches.
        ApphostWorkflowGalleryFixture.SeedAsync(Path.Combine(profile, "fixtures"), profile).GetAwaiter().GetResult();
        int result = 1;
        Console.WriteLine("SMOKE SCENARIO: normal");
        int exit = AppBuilder.Configure<StartupPreviewSmokeApp>().UsePlatformDetect().WithInterFont()
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                var app = (StartupPreviewSmokeApp)Application.Current!;
                var window = (MainWindow)desktop.MainWindow!;
                try
                {
                    Console.WriteLine($"SMOKE render scaling: actual={window.RenderScaling}; requested=unset;");
                    await RunAsync(window, app);
                    result = 0;
                    Console.WriteLine("SMOKE PASS: CX02 gallery/export closure; offline, private Xvfb, in-process input only.");
                }
                catch (Exception error) { Console.Error.WriteLine("SMOKE FAIL: CX02 " + error); }
                finally
                {
                    app.Barrier.Release.TrySetResult(); app.ReadableBarrier.Release.TrySetResult();
                    window.Close();
                    await Until(() => window.SessionHost.IsProfileReleased && !Read<bool>(window, "_shutdownRunning"));
                    if (window.IsVisible) window.Close();
                    desktop.Shutdown(result);
                }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return result == 0 ? exit : result;
    }
#endif

    // Pure source assertions: no application, catalog, native media or platform initialization.
    internal static void VerifySourceContracts(string root)
    {
        string Section(string path, string first, string last)
        {
            string source = File.ReadAllText(Path.Combine(root, path));
            int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            Require(firstIndex >= 0, "Missing contract section: " + first);
            int lastIndex = source.IndexOf(last, firstIndex + first.Length, StringComparison.Ordinal);
            Require(lastIndex > firstIndex, "Missing contract boundary: " + last);
            return source[firstIndex..lastIndex];
        }
        const string shell = "Desktop/Shell/MainWindow.Gallery.cs";
        string guard = Section(shell, "private Func<bool>? ArchiveReaderMediaActivationGuard()", "internal async Task OpenGalleryAsync");
        Require(guard.Contains("_selectedPage == WorkspacePage.Archive || _largeReaderOpen", StringComparison.Ordinal)
            && guard.Contains("? _archivePage?.ReaderMediaActivationGuard : null", StringComparison.Ordinal),
            "Archive activation guard leaked into independent Downloads requests.");
        string open = Section(shell, "internal async Task OpenGalleryAsync", "internal async Task OpenMediaGalleryAsync");
        Require(open.Contains("activationRequest = ArchiveReaderMediaActivationGuard()", StringComparison.Ordinal)
            && open.Contains("activationRequest ?? ArchiveGalleryHandoffGuard()", StringComparison.Ordinal)
            && open.Contains("if (readerRequest?.Invoke() == false) return;", StringComparison.Ordinal)
            && open.Contains("stillRequested: readerRequest", StringComparison.Ordinal),
            "Archive activation/fallback guard must still survive the async shell handoff.");
        string start = Section(shell, "await _galleryCleanupTask;", "_galleryOrigin = origin;");
        Require(start.Contains("request != _galleryRequest", StringComparison.Ordinal)
            && start.Contains("stillRequested?.Invoke() == false", StringComparison.Ordinal),
            "Native cleanup must not revive a revoked Archive request.");
        string available = Section("Desktop/Views/Archive/ArchivePage.axaml.cs", "internal bool IsAvailable", "// Catalog reads may begin");
        Require(available.IndexOf("UpdateExportActions();", StringComparison.Ordinal)
                > available.IndexOf("_available = value;", StringComparison.Ordinal)
            && available.Contains("if (!value) _exportCancellation?.Cancel();", StringComparison.Ordinal),
            "Availability must refresh export presentation after assignment, retaining revocation.");
        string export = Section("Desktop/Views/Archive/ArchivePage.Export.cs", "private void UpdateExportActions()", "private void OnExportClose");
        Require(export.Contains("ExportRequestIsCurrent(_exportRequest)", StringComparison.Ordinal)
            && export.Contains("!_exportRunning && !_exportPicking && !_exportLoading && !_exportResolving", StringComparison.Ordinal)
            && export.Contains("IsEnabled = ready && !UpdatingExport", StringComparison.Ordinal),
            "Readiness recovery must preserve request/lifetime, operation and update-mode guards.");
        Console.WriteLine("CX02 source PASS: Archive-only activation scope; retained cleanup guards; export readiness refresh.");
    }

    // Real compiled callback/guard checks without platform/window/catalog initialization.
    internal static void VerifyManagedContracts()
    {
        var page = (ArchivePage)RuntimeHelpers.GetUninitializedObject(typeof(ArchivePage));
        using var lifetime = new CancellationTokenSource();
        Set(page, "_lifetime", lifetime); Set(page, "_readable", true);
        Set(page, "ArchiveExportDialog", new Border { IsVisible = false });
        int calls = 0;
        page.OpenGallery = (_, _, _) => { calls++; return Task.CompletedTask; };
        var summary = new ArchiveThreadSummary(First, "https://offline.invalid/1", "closure", "/not-read",
            ThreadSourceState.Archived, false, DateTimeOffset.UnixEpoch, null, 1, 1);
        var attachment = new CapturedAttachment(0, "asset", OriginalFileName: "fixture.png");
        var row = (ArchiveAttachmentRow)Call(page, "CreateListAttachmentRow", summary, "1", attachment)!;
        foreach (string state in new[] { "unavailable", "busy", "ended", "lifetime" })
        {
            Set(page, "_available", state != "unavailable"); Set(page, "_busy", state == "busy");
            Set(page, "_ended", state == "ended");
            if (state == "lifetime") lifetime.Cancel();
            row.OpenPreview!();
            Require(calls == 0, "Inline list preview bypassed " + state);
        }
        Set(page, "_ended", false); Set(page, "_busy", false);
        Require(!(bool)Call(page, "ExportRequestIsCurrent", 0)!, "Cancelled lifetime authorized export.");
        using var active = new CancellationTokenSource(); Set(page, "_lifetime", active);
        Set(page, "_exportRequest", 7);
        Require((bool)Call(page, "ExportRequestIsCurrent", 7)! && !(bool)Call(page, "ExportRequestIsCurrent", 6)!,
            "Export request identity not enforced.");
        Console.WriteLine("CX02 managed PASS: real inline callback unavailable/busy/ended/lifetime; export identity/lifetime.");

        // Keep a real captured guard alive after navigation's existing invalidation hook.
        // The shell must drop it only for independent workspace requests, not revive it in Archive/a reader.
        Func<bool> guard = page.CaptureGalleryHandoffGuard();
        Set(page, "<ReaderMediaActivationGuard>k__BackingField", guard);
        var shell = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Set(shell, "_archivePage", page); Set(shell, "_selectedPage", MainWindow.WorkspacePage.Archive);
        Require(guard() && ReferenceEquals(Call(shell, "ArchiveReaderMediaActivationGuard"), guard),
            "Current Archive handoff lost its activation guard.");
        page.CancelPendingGalleryOpen(); Set(shell, "_selectedPage", MainWindow.WorkspacePage.Downloads);
        Require(!guard() && Call(shell, "ArchiveReaderMediaActivationGuard") == null,
            "Revoked Archive activation guard rejected an independent Downloads request.");
        Set(shell, "_selectedPage", MainWindow.WorkspacePage.Archive);
        Require(ReferenceEquals(Call(shell, "ArchiveReaderMediaActivationGuard"), guard) && !guard(),
            "Returning to Archive revived a revoked handoff.");
        Set(shell, "_selectedPage", MainWindow.WorkspacePage.Downloads); Set(shell, "_largeReaderOpen", true);
        Require(ReferenceEquals(Call(shell, "ArchiveReaderMediaActivationGuard"), guard) && !guard(),
            "Detached reader bypassed its revoked activation guard.");
        Console.WriteLine("CX02 managed PASS: navigation revokes Archive guard without contaminating Downloads.");
    }

    private static async Task RunAsync(MainWindow window, StartupPreviewSmokeApp app)
    {
        await app.Barrier.Entered.Task.WaitAsync(Deadline); app.Barrier.Release.TrySetResult();
        await app.ReadableBarrier.Entered.Task.WaitAsync(Deadline);
        C<Button>(window, "ArchiveNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ArchivePage page = window.ArchivePage;
        await Until(() => !page.IsBusy && C<ListBox>(page, "ArchiveThreads").ItemCount > 0);
        Require(window.SessionHost.State == DesktopSessionState.Starting && window.SessionHost.CatalogReadable,
            "Early archive is not held at catalog-only readiness.");
        var original = page.OpenGallery;
        int opened = 0; page.OpenGallery = (_, _, _) => { opened++; return Task.CompletedTask; };
        await page.LoadThreadAsync(ApphostWorkflowGalleryFixture.Id); await page.MediaLoadTask.WaitAsync(Deadline);
        var thread = page.CurrentThread!;
        var post = thread.GetPosts(0, 1).Single(); var attachment = post.Attachments!.Single();
        var assets = Read<IReadOnlyList<ArchiveAssetRow>>(page, "_assets");
        var inline = (ArchiveAttachmentRow)Call(page, "CreateAttachmentRow", thread.Summary.Id, attachment, post.PostId,
            Task.FromResult(assets))!;
        inline.OpenPreview!();
        await (Task)Call(page, "OpenThreadGalleryAsync", thread.Summary.Id, null, page)!;
        await (Task)Call(page, "OpenAssetGalleryAsync", assets[0], page)!;
        await (Task)Call(page, "OpenResultsGalleryAsync", page)!;
        await page.OpenExportForThreadsAsync([thread.Summary.Id, Second], page);
        // Direct menu/list delegates must not bypass the specialized page wrappers.
        GalleryEntry[] entries = GallerySelection.Build(thread, assets.Select(asset => asset.Manifest).Distinct(),
            true, includeOtherMedia: true).Entries.ToArray();
        Require(entries.Length > 0, "Inline fixture has no verified gallery target.");
        await window.OpenGalleryAsync(thread.Summary.Id, origin: page);
        await window.OpenMediaGalleryLaidOutAsync(entries, entries[0].EntryKey, page);
        await window.OpenSearchGalleryAsync(ArchiveSearchRequest.Create(new ArchiveSearchSpec()), origin: page);
        Require(Read<GalleryViewer?>(window, "_gallery") == null, "Direct archive delegate bypassed the early gate.");
        Require(opened == 0 && !C<Border>(page, "ArchiveExportDialog").IsVisible,
            "Catalog-only reader opened a gallery/export.");
        page.OpenGallery = original;
        app.ReadableBarrier.Release.TrySetResult();
        await Until(() => C<Button>(window, "AddUrlButton").IsEnabled && !page.IsBusy);
        await page.LoadThreadAsync(ApphostWorkflowGalleryFixture.Id); await page.MediaLoadTask.WaitAsync(Deadline);
        inline = (ArchiveAttachmentRow)Call(page, "CreateAttachmentRow", page.CurrentThread!.Summary.Id, attachment, post.PostId,
            Task.FromResult(Read<IReadOnlyList<ArchiveAssetRow>>(page, "_assets")))!;
        inline.OpenPreview!();
        await Until(() => Read<GalleryViewer?>(window, "_gallery") != null);
        Call(window, "CloseGallery", true); await window.GalleryCleanupTask.WaitAsync(Deadline);
        Console.WriteLine("CX02 early/ready PASS: inline reader media, thread/asset/results and collection dialog gates.");

        foreach (string kind in new[] { "asset", "results", "thread" })
        foreach (string change in new[] { "valid", "readiness", "readiness-cycle", "content", "away-back", "modal", "cancel" })
            await HeldGalleryAsync(window, page, kind, change);
        await ArchiveThenDownloadsAsync(window, page);
        await MenuAndFocusAsync(window, page);
        await SuspendedInlineAsync(window, page);
        foreach (bool collection in new[] { false, true })
        foreach (string change in new[] { "valid", "readiness", "readiness-cycle", "request", "lifetime" })
            await HeldExportAsync(window, collection, change);
        await HeldCollectionHistoryAsync(window);
        await HeldPublishedHistoryAsync(window);
        Require(app.Transport.Requests == 0, "Offline host attempted HTTP.");
    }

    private static async Task HeldGalleryAsync(MainWindow window, ArchivePage page, string kind, string change)
    {
        C<Button>(window, "ArchiveNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !page.IsBusy);
        await page.LoadThreadAsync(ApphostWorkflowGalleryFixture.Id); await page.MediaLoadTask.WaitAsync(Deadline);
        var asset = Read<IReadOnlyList<ArchiveAssetRow>>(page, "_assets")[0];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_galleryCleanupTask", release.Task);
        Control origin = C<Button>(page, "ArchiveOpenGallery");
        Task pending = kind switch
        {
            "asset" => (Task)Call(page, "OpenAssetGalleryAsync", asset, origin)!,
            "results" => (Task)Call(page, "OpenResultsGalleryAsync", origin)!,
            _ => (Task)Call(page, "OpenThreadGalleryAsync", page.CurrentThread!.Summary.Id, null, origin)!
        };
        try
        {
            await Until(() => page.GallerySelectionGuard != null || page.ReaderMediaActivationGuard != null);
            Require(!pending.IsCompleted && Read<GalleryViewer?>(window, "_gallery") == null,
                kind + ": native cleanup did not hold coverage.");
            Require((page.GallerySelectionGuard ?? page.ReaderMediaActivationGuard)!(), kind + ": internal close invalidated valid handoff.");
            switch (change)
            {
                case "readiness": page.IsAvailable = false; break;
                case "readiness-cycle": page.IsAvailable = false; page.IsAvailable = true; break;
                case "content": Set(page, "_contentRequest", Read<int>(page, "_contentRequest") + 1); break;
                case "away-back":
                    C<Button>(window, "DownloadsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    // Do not reload: this proves away/back cannot revive the same content/request.
                    Call(window, "NavigateWorkspace", Enum.Parse(typeof(MainWindow.WorkspacePage), "Archive"), false); break;
                case "modal": C<Border>(page, "ArchiveExportDialog").IsVisible = true; break;
                case "cancel": page.CancelPendingGalleryOpen(); break;
            }
            release.TrySetResult(); await pending.WaitAsync(Deadline);
            Require((Read<GalleryViewer?>(window, "_gallery") != null) == (change == "valid"), kind + ": stale coverage after " + change);
            Console.WriteLine("CX02 held-cleanup PASS: " + kind + "/" + change);
        }
        finally
        {
            release.TrySetResult(); await pending.WaitAsync(Deadline);
            C<Border>(page, "ArchiveExportDialog").IsVisible = false;
            page.IsAvailable = true;
            Call(window, "CloseGallery", true); await window.GalleryCleanupTask.WaitAsync(Deadline);
        }
    }

    private static async Task ArchiveThenDownloadsAsync(MainWindow window, ArchivePage page)
    {
        C<Button>(window, "ArchiveNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !page.IsBusy);
        await page.LoadThreadAsync(ApphostWorkflowGalleryFixture.Id); await page.MediaLoadTask.WaitAsync(Deadline);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_galleryCleanupTask", release.Task);
        Task archive = (Task)Call(page, "OpenThreadGalleryAsync", ApphostWorkflowGalleryFixture.Id, null, page)!;
        Task downloads = Task.CompletedTask;
        try
        {
            Func<bool>? revoked = page.ReaderMediaActivationGuard;
            Require(revoked != null && revoked() && !archive.IsCompleted, "Archive handoff did not reach cleanup.");
            C<Button>(window, "DownloadsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads && !revoked!(),
                "Navigation did not revoke the held Archive handoff.");
            downloads = window.OpenGalleryAsync(ApphostWorkflowGalleryFixture.Id,
                origin: C<Button>(window, "DownloadsNavigationButton"));
            Require(!downloads.IsCompleted && Read<GalleryViewer?>(window, "_gallery") == null,
                "Downloads request was rejected by the retained Archive guard instead of waiting for cleanup.");
            release.TrySetResult(); await Task.WhenAll(archive, downloads).WaitAsync(Deadline);
            Require(Read<GalleryViewer?>(window, "_gallery") != null && !revoked!()
                && Read<MainWindow.WorkspacePage>(window, "_galleryOriginPage") == MainWindow.WorkspacePage.Downloads
                && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                "Only the fresh Downloads request may cover the shell after navigation.");
            Console.WriteLine("CX02 held-cleanup PASS: revoked Archive handoff does not veto a fresh Downloads gallery.");
        }
        finally
        {
            release.TrySetResult(); await Task.WhenAll(archive, downloads).WaitAsync(Deadline);
            Call(window, "CloseGallery", false); await window.GalleryCleanupTask.WaitAsync(Deadline);
            Call(window, "NavigateWorkspace", MainWindow.WorkspacePage.Archive, false);
        }
    }

    private static async Task MenuAndFocusAsync(MainWindow window, ArchivePage page)
    {
        var origin = C<Button>(page, "ArchiveOpenGallery"); origin.Focus();
        var row = C<ListBox>(page, "ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().First();
        Call(page, "OnThreadGalleryMenu", new MenuItem { DataContext = row }, new RoutedEventArgs());
        await Until(() => Read<GalleryViewer?>(window, "_gallery") != null && page.ReaderMediaActivationGuard == null);
        Call(window, "CloseGallery", true); await window.GalleryCleanupTask.WaitAsync(Deadline);
        await Until(() => !page.IsBusy && origin.IsEffectivelyVisible && origin.IsEnabled);
        Require(origin.Focus(), "Gallery toolbar origin cannot receive focus."); // Toolbar entry retains its own natural return target.
        origin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Read<GalleryViewer?>(window, "_gallery") != null && page.ReaderMediaActivationGuard == null);
        Call(window, "CloseGallery", true); await window.GalleryCleanupTask.WaitAsync(Deadline);
        await Until(() => origin.IsFocused);
        Console.WriteLine("CX02 menu/focus PASS: valid row menu and toolbar close restores origin.");
    }

    private static async Task SuspendedInlineAsync(MainWindow window, ArchivePage page)
    {
        await window.OpenGalleryAsync(ApphostWorkflowGalleryFixture.Id, origin: page);
        var viewer = Read<GalleryViewer>(window, "_gallery");
        GalleryEntry entry = Read<List<GalleryEntry>>(window, "_galleryEntries")[0];
        await ((Task)Call(window, "ReadGalleryPostAsync", entry)!).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        Require(Read<bool>(window, "_gallerySuspended") && Read<bool>(window, "_largeReaderOpen"), "Reader return did not suspend gallery.");
        var post = page.CurrentThread!.GetPosts(0, 1).Single();
        var inline = (ArchiveAttachmentRow)Call(page, "CreateAttachmentRow", page.CurrentThread.Summary.Id,
            post.Attachments!.Single(), post.PostId, Task.FromResult(Read<IReadOnlyList<ArchiveAssetRow>>(page, "_assets")))!;
        inline.OpenPreview!();
        await Until(() => !Read<bool>(window, "_gallerySuspended"));
        Require(ReferenceEquals(viewer, Read<GalleryViewer>(window, "_gallery")), "Inline reader media rebuilt an existing suspended viewer.");
        Call(window, "CloseGallery", true); await window.GalleryCleanupTask.WaitAsync(Deadline);
        Call(window, "CloseLargeReader", false);
        Console.WriteLine("CX02 suspended-inline PASS: existing gallery reused, reader return semantics retained.");
    }

    private static async Task HeldExportAsync(MainWindow owner, bool collection, string change)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "export-" + collection + "-" + change);
        Directory.CreateDirectory(profile);
        using var lease = ProfileLease.Acquire(profile);
        await SeedTextAsync(profile, lease);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        async Task<ArchiveReader> Reader(CancellationToken token)
        {
            // Open a real reader before the hold; deliberately return it even after revocation.
            ArchiveReader reader = await ArchiveReader.OpenAsync(profile, CancellationToken.None);
            if (hold) { entered.TrySetResult(); await release.Task; }
            return reader;
        }
        var page = new ArchivePage(Reader, (_, _) => Task.CompletedTask, _ => { })
        {
            IsAvailable = true, ExportHistory = new ExportHistoryStore(profile, lease),
            CollectionExportHistory = new CollectionExportHistoryStore(profile, lease), LastExportDirectoryPreference = profile
        };
        var window = new Window { Width = 1000, Height = 800, Content = page }; window.Show(owner);
        string destination = Path.Combine(profile, "output");
        try
        {
            await page.OpenExportForThreadsAsync(collection ? [First, Second] : [First], page);
            C<TextBox>(page, "ArchiveExportDestination").Text = destination;
            C<CheckBox>(page, "ArchiveExportMedia").IsChecked = false;
            if (change == "valid") VerifyExportReadinessRecovery(page);
            hold = true;
            C<Button>(page, "ArchiveExportStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(Deadline);
            Require(page.IsExportRunning && !Directory.Exists(destination), "Export did not stop at real reader boundary.");
            switch (change)
            {
                case "readiness": page.IsAvailable = false; break;
                case "readiness-cycle": page.IsAvailable = false; page.IsAvailable = true; break;
                case "request": Set(page, "_exportRequest", Read<int>(page, "_exportRequest") + 1); break;
                case "lifetime": Read<CancellationTokenSource>(page, "_lifetime").Cancel(); break;
            }
            release.TrySetResult(); await Until(() => !page.IsExportRunning);
            if (change == "valid")
            {
                Require(File.Exists(Path.Combine(destination, "index.html")), "Valid export was overblocked.");
                Require(collection ? page.LastCollectionExport != null && page.CollectionExportHistory.List([First, Second]).Count == 1
                    : page.LastExport != null && page.ExportHistory.List(First).Count == 1, "Valid result/history missing.");
            }
            else
            {
                Require(!Directory.Exists(destination) && page.LastExport == null && page.LastCollectionExport == null,
                    "Revoked post-reader request started/published export: " + change);
                Require(page.CollectionExportHistory.List([First, Second]).Count == 0 && page.ExportHistory.List(First).Count == 0,
                    "Revoked export recorded history.");
            }
            Console.WriteLine("CX02 held-reader PASS: " + (collection ? "collection/" : "single/") + change);
        }
        finally { release.TrySetResult(); await Until(() => !page.IsExportRunning); page.EndLifetime(); window.Close(); }
    }

    private static void VerifyExportReadinessRecovery(ArchivePage page)
    {
        Border dialog = C<Border>(page, "ArchiveExportDialog"); object? card = dialog.Child;
        string? destination = C<TextBox>(page, "ArchiveExportDestination").Text;
        int request = Read<int>(page, "_exportRequest");
        ResourceId? thread = Read<ResourceId?>(page, "_exportThread");
        ResourceId[]? members = Read<ResourceId[]?>(page, "_exportMembers");
        void Actions(bool enabled)
        {
            foreach (string name in new[] { "ArchiveExportStart", "ArchiveExportBrowse", "ArchiveExportDestination", "ArchiveExportMedia", "ArchiveExportBackup" })
                Require(C<Control>(page, name).IsEnabled == enabled, name + ": stale export readiness presentation.");
        }
        Actions(true);
        page.IsAvailable = false; Actions(false);
        page.IsAvailable = true; Actions(true);
        // Recovery must not bypass any of the dialog's existing operation guards.
        foreach (string field in new[] { "_exportRunning", "_exportPicking", "_exportLoading", "_exportResolving" })
        {
            Set(page, field, true);
            try
            {
                page.IsAvailable = false; Actions(false);
                page.IsAvailable = true; Actions(false);
            }
            finally { Set(page, field, false); page.IsAvailable = true; }
            Actions(true);
        }
        Require(dialog.IsVisible && ReferenceEquals(dialog.Child, card)
            && C<TextBox>(page, "ArchiveExportDestination").Text == destination
            && C<CheckBox>(page, "ArchiveExportMedia").IsChecked == false
            && Read<int>(page, "_exportRequest") == request && Read<ResourceId?>(page, "_exportThread") == thread
            && ReferenceEquals(Read<ResourceId[]?>(page, "_exportMembers"), members),
            "Readiness recovery reopened/reset the dialog or changed its frozen export request.");
        Console.WriteLine("CX02 readiness-recovery PASS: same single/collection dialog, restored fields/start, retained operation guards.");
    }

    private static async Task HeldCollectionHistoryAsync(MainWindow owner)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "held-history"); Directory.CreateDirectory(profile);
        using var lease = ProfileLease.Acquire(profile); await SeedTextAsync(profile, lease);
        var page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
        { IsAvailable = true, CollectionExportHistory = new CollectionExportHistoryStore(profile, lease), LastExportDirectoryPreference = profile };
        var window = new Window { Width = 1000, Height = 800, Content = page }; window.Show(owner);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = Task.CompletedTask;
        try
        {
            await page.OpenExportForThreadsAsync([First, Second], page);
            ComboBox history = C<ComboBox>(page, "ArchiveExportHistory"); object? original = history.ItemsSource;
            // Hold the real history store's shared lease monitor, not a replacement ListVerified implementation.
            object sync = Read<object>(lease, "_sync");
            blocker = Task.Run(() => { lock (sync) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } });
            await entered.Task.WaitAsync(Deadline);
            Task lookup = (Task)Call(page, "ReloadCollectionExportHistoryAsync", Read<int>(page, "_exportRequest"), null)!;
            Require(!lookup.IsCompleted, "History boundary was not held.");
            page.IsAvailable = false; release.TrySetResult(); await lookup.WaitAsync(Deadline); await blocker.WaitAsync(Deadline);
            Require(ReferenceEquals(original, history.ItemsSource), "Late collection history published after readiness loss.");
            Console.WriteLine("CX02 held-history PASS: real collection lookup cannot publish after readiness loss.");
        }
        finally { release.TrySetResult(); await blocker.WaitAsync(Deadline); page.EndLifetime(); window.Close(); }
    }

    private static async Task HeldPublishedHistoryAsync(MainWindow owner)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "held-published-history"); Directory.CreateDirectory(profile);
        using var lease = ProfileLease.Acquire(profile); await SeedTextAsync(profile, lease);
        var page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
        { IsAvailable = true, CollectionExportHistory = new CollectionExportHistoryStore(profile, lease), LastExportDirectoryPreference = profile };
        var window = new Window { Width = 1000, Height = 800, Content = page }; window.Show(owner);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = Task.CompletedTask;
        try
        {
            await page.OpenExportForThreadsAsync([First, Second], page);
            C<TextBox>(page, "ArchiveExportDestination").Text = Path.Combine(profile, "output");
            C<CheckBox>(page, "ArchiveExportMedia").IsChecked = false;
            ComboBox history = C<ComboBox>(page, "ArchiveExportHistory"); object? original = history.ItemsSource;
            object sync = Read<object>(lease, "_sync");
            blocker = Task.Run(() => { lock (sync) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } });
            await entered.Task.WaitAsync(Deadline);
            C<Button>(page, "ArchiveExportStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => page.LastCollectionExport != null);
            Require(page.IsExportRunning, "Published-history operation was not held.");
            // Publication was valid before revocation: do not undo its files or durable history.
            // Its delayed history result must nevertheless not publish new UI after the gate closes.
            page.IsAvailable = false; release.TrySetResult(); await Until(() => !page.IsExportRunning);
            await blocker.WaitAsync(Deadline);
            Require(ReferenceEquals(original, history.ItemsSource), "Late RecordPublished result repopulated revoked history UI.");
            Console.WriteLine("CX02 held-published-history PASS: valid publication retained; delayed history UI rejected.");
        }
        finally
        {
            release.TrySetResult(); await blocker.WaitAsync(Deadline); await Until(() => !page.IsExportRunning);
            page.EndLifetime(); window.Close();
        }
    }

    private static async Task SeedTextAsync(string profile, ProfileLease lease)
    {
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in new[] { First, Second })
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + id.Thread, "closure " + id.Thread,
                ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, profile,
                [new CapturedPost("1", 0, PostFieldPresence.All, Html: "<p>Offline closure fixture.</p>")]));
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!predicate()) { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(20, deadline.Token); }
    }
    private static T C<T>(Control owner, string name) where T : Control =>
        (owner is ArchivePage page ? page.FindControl<T>(name) : owner.FindControl<T>(name))
        ?? throw new InvalidOperationException("Missing " + name);
    private static T Read<T>(object owner, string field) => (T)owner.GetType().GetField(field, Private)!.GetValue(owner)!;
    private static void Set(object owner, string field, object? value) => owner.GetType().GetField(field, Private)!.SetValue(owner, value);
    private static object? Call(object owner, string method, params object?[] args) => owner.GetType().GetMethods(Private)
        .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(owner, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
