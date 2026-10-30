using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>Real startup layout/Ready persistence plus actual shell Close failure and ownership boundaries.</summary>
internal static class PreferenceStartupChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ColumnKey = "ThreadsDataGrid.SourceBoard";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    // R0 owns Program: call from its opt-in normal-mode branch, never bypass this with a routed-key fallback.
    internal static async Task RunAsync(MainWindow owner, CancellationToken cancellationToken = default)
    {
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (string code in new[] { "en", "de" })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(90));
                await RunCellAsync(owner, code, variant, budget.Token);
                await RunCloseFailureCellAsync(owner, code, variant, "shutdown-wins", budget.Token);
                await RunCloseFailureCellAsync(owner, code, variant, "restore-fails", budget.Token);
                await RunCloseFailureCellAsync(owner, code, variant, "ready-io", budget.Token);
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            owner.Activate();
        }
        Console.WriteLine("SMOKE preference startup: PASS; all four EN/DE Light/Dark cells; real held restore + native header drag; bounded pending, Ready commit/reopen, and 12 real Close failure cells (held startup success/failure, Ready IO); safe error window, second close and ownership cleanup. No setup/recovery-consent or package acceptance claim.");
    }

    private static async Task RunCellAsync(MainWindow owner, string language, ThemeVariant variant, CancellationToken token)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("preference-startup-");
        var restore = new HeldStore();
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        Exception? failure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            // Valid existing-profile fixture, not setup consent and not a personal/user profile.
            new Config
            {
                DownloadPath = Path.Combine(directory, "media"), SaveUrlsOnExit = true,
                AutomaticUpdateChecks = false, WorkspaceMode = "Advanced", Language = language,
                Theme = variant == ThemeVariant.Light ? "Light" : "Dark", WindowWidth = 1000, WindowHeight = 700,
                CloseToTray = false, MinimizeToTray = false, ShowErrorNotifications = true,
                FirstRunSetupPending = false, FirstRunSetupVersion = 0
            }.SaveConfig(directory);
            string settings = Path.Combine(directory, "settings.json");
            byte[] before = await File.ReadAllBytesAsync(settings, token);
            Config initial = Config.LoadConfig(directory);
            host = new DesktopSessionHost(directory, Array.Empty<IImageBoard>(), sessionStoreFactory: (_, _) => restore);
            Require(host.Config.Origin == ProfileOrigin.ExistingValid, "Fixture did not bind a valid existing origin.");
            shell = new MainWindow(host); // Real shell owns StartAsync, event wiring and debounced saves.
            shell.Show(owner);
            await restore.Entered.Task.WaitAsync(Deadline, token);
            await UntilAsync(() => shell.IsVisible && Get<bool>(shell, "_windowPreferencesReady")
                && shell.ClientSize.Width > 0, "native layout readiness while restore held", token);
            await Dispatcher.UIThread.InvokeAsync(shell.UpdateLayout, DispatcherPriority.Background);
            Require(host.State == DesktopSessionState.Starting && !host.StartAsync().IsCompleted
                && !shell.FindControl<Button>("AddUrlButton")!.IsEnabled,
                "Held restoration was mistaken for a ready session.");
            Require(Texts.Language == language && shell.ActualThemeVariant == variant,
                "The startup cell lost its requested culture/theme.");
            double scale = shell.RenderScaling;
            Require(double.IsFinite(scale) && scale > 0, "No measured render scale.");
            if (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is string requested)
                Require(double.TryParse(requested, NumberStyles.Float, CultureInfo.InvariantCulture, out double expectedScale)
                    && Math.Abs(scale - expectedScale) < 0.01, "Measured startup scale differs from the requested cell.");

            DataGrid grid = shell.FindControl<DataGrid>("ThreadsDataGrid")!;
            DataGridColumn column = grid.Columns.Single(c => c.SortMemberPath == nameof(ThreadRow.SourceBoard));
            shell.Activate();
            await UntilAsync(() => shell.IsActive, "owned shell activation", token);
            using (var native = new MediaNativeInput(shell))
            {
                await ResizeColumnAsync(native, shell, grid, column, token);
                Task first = FlushWindow(shell);
                Task firstWrite = Get<Task>(shell, "_windowPreferenceWrite");
                // Actual column PropertyChanged/user-edit boundary, not injected pending dictionaries or host-only edits.
                // Deterministic property burst supplements (does not claim to be) native pointer delivery.
                for (int index = 0; index < 128; index++)
                {
                    column.Width = new DataGridLength(180 + index / 2d);
                    Require(ReferenceEquals(first, FlushWindow(shell))
                        && ReferenceEquals(firstWrite, Get<Task>(shell, "_windowPreferenceWrite")),
                        "Starting shell flushes accumulated distinct observers/writes.");
                    Require(host.PreferenceWriteCounts == (0, 1, 0) && !firstWrite.IsCompleted && !first.IsCompleted,
                        "Starting did not retain exactly one pending, nonfailed batch.");
                }
                await ResizeColumnAsync(native, shell, grid, column, token);
                Require(ReferenceEquals(first, FlushWindow(shell)), "Final native edit escaped the shared pending completion.");
                double expectedWidth = column.Width.Value;
                Require(Get<Task>(shell, "_windowPreferenceWrite") == firstWrite && host.PreferenceWriteCounts == (0, 1, 0),
                    "Final native edit did not remain bounded while Starting.");
                byte[] heldSettings = await File.ReadAllBytesAsync(settings, token);
                Require(before.AsSpan().SequenceEqual(heldSettings),
                    "A Starting shell layout was published before permitted Ready.");
                RequireNoPreferenceFailure(shell);
                Capture(shell, CaptureName("held", language, variant, scale));

                restore.Release.TrySetResult(new(new SessionSnapshot(), null, false));
                await host.StartAsync().WaitAsync(Deadline, token);
                await firstWrite.WaitAsync(Deadline, token); // Raw completion: the shell observer intentionally catches failures.
                await first.WaitAsync(Deadline, token);
                await host.FlushPreferencesAsync().WaitAsync(Deadline, token);
                await UntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                    && host.PreferenceWriteCounts == (0, 0, 0), "Ready and drained startup batch", token);
                Require(host.State == DesktopSessionState.Ready, "Startup did not reach permitted Ready.");
                RequireNoPreferenceFailure(shell);
                Config saved = Config.LoadConfig(directory);
                RequireSavedColumn(saved, expectedWidth);
                Require(saved.FirstRunSetupPending == initial.FirstRunSetupPending
                    && saved.FirstRunSetupVersion == initial.FirstRunSetupVersion,
                    "An ordinary startup layout save changed setup intent/completion.");
                Capture(shell, CaptureName("ready", language, variant, scale));
                Console.WriteLine(FormattableString.Invariant($"SMOKE preference startup cell: {language}/{variant}, actual scale={scale:0.###}, backend={native.Backend}, pending=(0,1,0), 128 shared flushes, final native width={expectedWidth:0.###}, Ready drained=(0,0,0)."));
            }

            double persistedWidth = column.Width.Value;
            await CloseAsync(shell).WaitAsync(Deadline, token);
            await host.DisposeAsync().AsTask().WaitAsync(Deadline, token);
            Require(!shell.IsVisible && host.State == DesktopSessionState.Stopped, "First shell did not close cleanly.");
            using (ProfileLease released = ProfileLease.Acquire(directory)) { }
            host = new DesktopSessionHost(directory, Array.Empty<IImageBoard>());
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Deadline, token);
            await UntilAsync(() => shell.IsVisible && Get<bool>(shell, "_windowPreferencesReady")
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled, "reopened shell layout/Ready", token);
            Require(host.State == DesktopSessionState.Ready && host.Config.Origin == ProfileOrigin.ExistingValid,
                "Reopen lost valid origin or readiness.");
            RequireSavedColumn(host.Config, persistedWidth);
            DataGridColumn reopened = shell.FindControl<DataGrid>("ThreadsDataGrid")!.Columns
                .Single(c => c.SortMemberPath == nameof(ThreadRow.SourceBoard));
            Require(reopened.Width.IsAbsolute && Math.Abs(reopened.Width.Value - persistedWidth) < 0.01,
                "Reopening stored bytes did not restore the real shell column.");
            Require(host.Config.FirstRunSetupPending == initial.FirstRunSetupPending
                && host.Config.FirstRunSetupVersion == initial.FirstRunSetupVersion, "Reopen changed setup markers.");
            RequireNoPreferenceFailure(shell);
            Capture(shell, CaptureName("reopened", language, variant, shell.RenderScaling));
            Console.WriteLine($"SMOKE preference startup reopen: {language}/{variant}; real column restored; origin ExistingValid; setup markers unchanged.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Always release the fixture hold, even on timeout/cancellation, before asking the shell to drain.
            restore.Release.TrySetResult(new(new SessionSnapshot(), null, false));
            if (shell != null)
                try { await CloseAsync(shell).WaitAsync(Deadline); } catch (Exception error) { cleanupFailures.Add(error); }
            if (host != null)
                try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (Exception error) { cleanupFailures.Add(error); }
            if ((host == null || host.IsProfileReleased) && (shell == null || !shell.IsVisible))
            {
                try
                {
                    using (ProfileLease released = ProfileLease.Acquire(directory)) { }
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            else cleanupFailures.Add(new InvalidOperationException("Startup fixture not quiescent; private directory retained: " + directory));
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Preference startup cleanup failed; not a PASS.",
                failure == null ? cleanupFailures : cleanupFailures.Prepend(failure));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// Controlled task-boundary probe ONLY for a dedicated disposable process. Intentionally retains the
    /// profile/window after assertions; the parent must observe this receipt, terminate/exit the process,
    /// then prove lease reacquisition. Never call from RunAsync/the reusable normal-suite process.
    /// No decoder is created or mocked; real StopAndDetach/media lifetime tests remain mandatory.
    /// </summary>
    internal static async Task RunRetainedNativeDrainBoundaryAsync(MainWindow shell, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        CancellationToken token = budget.Token;
        DesktopSessionHost host = shell.SessionHost;
        await host.StartAsync().WaitAsync(Deadline, token);
        await UntilAsync(() => shell.IsVisible && Get<bool>(shell, "_windowPreferencesReady")
            && shell.FindControl<Button>("AddUrlButton")!.IsEnabled, "native boundary Ready", token);
        Require(host.State == DesktopSessionState.Ready && !host.IsProfileReleased
            && Get<object?>(shell, "_gallery") == null
            && Get<Task>(shell, "_galleryNativeCleanupTask").IsCompletedSuccessfully,
            "Controlled native boundary requires a clean Ready shell with no real decoder/gallery owner.");
        using var disposableDisplayGuard = new MediaNativeInput(shell); // Session/foreground guard only, no input/decoder claim.
        await FlushWindow(shell).WaitAsync(Deadline, token);
        await host.FlushPreferencesAsync().WaitAsync(Deadline, token);
        ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
        Require(galleryHost.Content == null, "Controlled native boundary refuses to replace existing gallery content.");
        Border attachedSentinel = new() { Name = "F03ControlledDrawableBoundary", Width = 16, Height = 16 };
        galleryHost.Content = attachedSentinel; // Non-native sentinel; proves no detach, not decoder safety.
        var fault = Task.FromException(new IOException("Controlled F03 native drain boundary failure; no decoder instantiated"));
        (typeof(MainWindow).GetMethod("TrackNativeGalleryCleanup", PrivateInstance)
            ?? throw new MissingMethodException(nameof(MainWindow), "TrackNativeGalleryCleanup")).Invoke(shell, new object[] { fault });
        object refresh = Get<object>(shell, "_sessionRefresh");
        Require(!Get<bool>(refresh, "_disposed") && Get<bool>(shell, "_workspaceInitialized"),
            "Workspace was already disposed before the boundary probe.");
        shell.Close();
        await UntilAsync(() => !Get<bool>(shell, "_shutdownRunning") && Get<bool>(shell, "_isClosing"),
            "native failure close handler termination", token);
        foreach (string field in new[] { "_engineStopTask", "_stopTask", "_disposeTask" })
            await ObserveTerminatedAsync(Get<Task>(host, field), "retained-native " + field, token);
        Require(Get<bool>(host, "_ownersQuiescent") && Get<bool>(host, "_updatesQuiescent")
            && Get<bool>(host, "_externalOwnershipBlocked") && !host.IsProfileReleased,
            "Faulted external native drain either skipped internal cleanup or released exclusive profile ownership.");
        Require(Get<Task>(shell, "_galleryNativeCleanupTask").IsFaulted
            && ReferenceEquals(galleryHost.Content, attachedSentinel) && !Get<bool>(refresh, "_disposed")
            && Get<bool>(shell, "_workspaceInitialized")
            && shell.IsVisible && !Get<bool>(shell, "_allowClose")
            && Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(e => e.Operation == "session.shutdown"),
            "Faulted native drain detached/disposed workspace or offered an unsafe close.");
        RequireLeaseHeld(host.ProfileDirectory);
        shell.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(shell.IsVisible && !Get<bool>(shell, "_allowClose") && !host.IsProfileReleased,
            "Second Close bypassed retained native ownership.");
        RequireLeaseHeld(host.ProfileDirectory);
        Console.WriteLine("SMOKE retained native boundary: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "observed-retained-requires-parent-exit-proof", profile = host.ProfileDirectory,
            internalCleanupAttempted = true, updatesQuiescent = true, profileReleased = false,
            workspaceDetached = false, allowClose = false, decoderInstantiated = false,
            limitation = "Controlled shell task-boundary injection only; parent must prove process exit/lease reacquisition; not real decoder failure acceptance."
        }));
        // NO fake flag reset, manual lease Dispose, UI detach, directory deletion or in-process recovery.
    }

    // A2 F01-A2-03: exercise Window.Close/OnClosing, not just a direct host StopAsync.
    private static async Task RunCloseFailureCellAsync(MainWindow owner, string language, ThemeVariant variant,
        string scenario, CancellationToken token)
    {
        bool readyIo = scenario == "ready-io";
        string directory = TemporaryWorkspacePaths.CreateDirectory("preference-close-" + scenario + "-");
        string settings = Path.Combine(directory, "settings.json");
        string displaced = Path.Combine(directory, "settings.fixture-original");
        // Deliberately retain actual restore ownership after cancellation, until this test releases it.
        // This deterministic, externally bounded hold proves the lease is not released under live owners.
        var restore = new HeldStore(honorCancellation: false);
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        Exception? failure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            Config FixtureConfig() => new()
            {
                DownloadPath = Path.Combine(directory, "media"), AutomaticUpdateChecks = false,
                SaveUrlsOnExit = !readyIo, CloseToTray = false, MinimizeToTray = false,
                WorkspaceMode = "Advanced", Language = language,
                Theme = variant == ThemeVariant.Light ? "Light" : "Dark", WindowWidth = 1000, WindowHeight = 700,
                ShowErrorNotifications = true
            };
            if (readyIo) FixtureConfig().SaveConfig(directory);
            host = new DesktopSessionHost(directory, Array.Empty<IImageBoard>(), sessionStoreFactory: (_, _) => restore);
            if (!readyIo)
            {
                Require(host.Config.Origin == ProfileOrigin.ConfirmedNew, "Failed-start fixture was not genuinely empty at acquisition.");
                // In-memory presentation only: never seed settings or claim successful restore/first-run intent.
                host.Config.CopyFrom(FixtureConfig());
            }
            shell = new MainWindow(host);
            shell.Show(owner);
            Task startup = host.StartAsync();
            if (!readyIo) await restore.Entered.Task.WaitAsync(Deadline, token);
            await UntilAsync(() => shell.IsVisible && Get<bool>(shell, "_windowPreferencesReady")
                && shell.ClientSize.Width > 0, scenario + " shell layout", token);
            Require(Texts.Language == language && shell.ActualThemeVariant == variant,
                "Negative Close cell lost its culture/theme.");
            if (readyIo)
            {
                restore.Release.TrySetResult(new(new SessionSnapshot(), null, false));
                await startup.WaitAsync(Deadline, token);
                await UntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled, "Ready before IO injection", token);
                await FlushWindow(shell).WaitAsync(Deadline, token);
                await host.FlushPreferencesAsync().WaitAsync(Deadline, token);
                Require(host.State == DesktopSessionState.Ready && host.PreferenceWriteCounts == (0, 0, 0),
                    "IO fixture had not drained its successful Ready writes.");
                // A real owned filesystem failure; no fake writer/task or product guard weakening.
                File.Move(settings, displaced);
                Directory.CreateDirectory(settings);
            }
            else
            {
                Require(host.State == DesktopSessionState.Starting && !startup.IsCompleted,
                    "Startup Close fixture reached Ready before shutdown could win.");
                RequireNoSettings(directory);
            }
            shell.Activate();
            await UntilAsync(() => shell.IsActive, scenario + " owned activation", token);
            DataGrid grid = shell.FindControl<DataGrid>("ThreadsDataGrid")!;
            DataGridColumn column = grid.Columns.Single(c => c.SortMemberPath == nameof(ThreadRow.SourceBoard));
            using (var native = new MediaNativeInput(shell))
                await ResizeColumnAsync(native, shell, grid, column, token);
            Task observedWrite = FlushWindow(shell);
            Task rawWrite = Get<Task>(shell, "_windowPreferenceWrite");
            if (!readyIo)
                Require(!rawWrite.IsCompleted && host.PreferenceWriteCounts == (0, 1, 0),
                    "Held startup Close did not contain one genuine pending shell preference.");
            else
            {
                Exception io = await RequireWriteFailureAsync(rawWrite, scenario, token);
                Require(IsIoFailure(io), "The Ready fixture did not produce a genuine filesystem save failure: " + io);
                await observedWrite.WaitAsync(Deadline, token);
                Require(host.PreferenceWriteCounts.Failed == 1
                    && Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(e => e.Operation == "preferences.window"),
                    "Ready IO failure was reported as persisted or lost its unsaved draft/feedback.");
            }

            shell.Close(); // Actual public close event -> async OnClosing -> CloseApplicationAsync.
            Task closeRawWrite = Get<Task>(shell, "_windowPreferenceWrite");
            if (!readyIo)
            {
                await UntilAsync(() => host.State == DesktopSessionState.Stopping && Get<bool>(shell, "_shutdownRunning"),
                    scenario + " close waiting for restore", token);
                Require(shell.IsVisible && !Get<bool>(shell, "_allowClose") && !startup.IsCompleted,
                    "A live restore owner was mistaken for a safe error/close outcome.");
                RequireLeaseHeld(directory);
                RequireNoSettings(directory);
                if (scenario == "restore-fails") restore.Release.TrySetException(new IOException("Owned F03 restore failure"));
                else restore.Release.TrySetResult(new(new SessionSnapshot(), null, false));
            }
            await startup.WaitAsync(Deadline, token);
            Exception rawFailure = await RequireWriteFailureAsync(rawWrite, scenario, token);
            Require(readyIo || rawFailure is InvalidOperationException, "Starting write was not rejected by readiness authority.");
            await RequireWriteFailureAsync(closeRawWrite, scenario + " final Close write", token);
            await observedWrite.WaitAsync(Deadline, token); // Observer catches; raw faults above must remain faults.
            await UntilAsync(() => Get<bool>(shell, "_allowClose") && !Get<bool>(shell, "_shutdownRunning"),
                scenario + " safe error window", token);
            Require(host.State == DesktopSessionState.Stopped && host.IsProfileReleased
                && Get<bool>(host, "_ownersQuiescent") && Get<bool>(host, "_updatesQuiescent")
                && Get<Task>(shell, "_galleryNativeCleanupTask").IsCompletedSuccessfully, "Shell Close bypassed complete host disposal/quiescence.");
            foreach (string field in new[] { "_engineStopTask", "_stopTask", "_disposeTask" })
                await ObserveTerminatedAsync(Get<Task>(host, field), scenario + " " + field, token);
            foreach (string field in new[] { "_restoreTask", "_windowPreferenceSave", "_galleryNativeCleanupTask", "_galleryCleanupTask", "_readingPositionSave" })
                await ObserveTerminatedAsync(Get<Task>(shell, field), scenario + " " + field, token);
            Require(host.PreferenceWriteCounts.Active == 0 && host.PreferenceWriteCounts.Pending == 0
                && Get<System.Collections.IDictionary>(host, "_finalWriteLoops").Count == 0,
                "Shutdown left a writer/pending loop running after profile ownership was released.");
            Require(shell.IsVisible && Get<bool>(shell, "_isClosing") && !shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(e => e.Operation == "session.shutdown")
                && shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible,
                "Failed preferences did not leave the defined inert visible shutdown-error window.");
            if (scenario == "restore-fails")
                Require(host.StartupError?.Contains("Owned F03 restore failure", StringComparison.Ordinal) == true,
                    "The controlled restore failure disappeared from the startup outcome.");
            using (ProfileLease released = ProfileLease.Acquire(directory)) { } // Only AFTER actual shell/host cleanup.
            Capture(shell, CaptureName(scenario + "-safe-error", language, variant, shell.RenderScaling));
            if (!readyIo) RequireNoSettings(directory);
            else
            {
                Require(Directory.Exists(settings) && !Directory.EnumerateFileSystemEntries(settings).Any(),
                    "The failed writer mutated/removed its injected directory before teardown finished.");
                // Remove the IO fault only AFTER writers and owners terminate. Any late retry would now corrupt this check.
                byte[] original = await File.ReadAllBytesAsync(displaced, token);
                Directory.Delete(settings);
                File.Move(displaced, settings);
                await RequireWriteFailureAsync(host.SaveViewPreferencesAsync(config => config.WindowWidth = 1191),
                    "sealed host rejects late preference", token);
                byte[] restored = await File.ReadAllBytesAsync(settings, token);
                Require(original.AsSpan().SequenceEqual(restored),
                    "A disposed host published a late write after the IO fault was removed.");
            }
            Task lastRaw = Get<Task>(shell, "_windowPreferenceWrite");
            var counts = host.PreferenceWriteCounts;
            byte[]? restoredBytes = readyIo ? await File.ReadAllBytesAsync(settings, token) : null;
            shell.Close(); // _allowClose permits the SECOND request directly; no persistence retry against a released owner.
            await UntilAsync(() => !shell.IsVisible, scenario + " second close exits", token);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(600, token); // Past the shell's 500ms debounce; supplement terminal-task/sealed-writer proof.
            Require(host.IsProfileReleased && host.PreferenceWriteCounts == counts
                && ReferenceEquals(lastRaw, Get<Task>(shell, "_windowPreferenceWrite"))
                && Get<System.Collections.IDictionary>(host, "_finalWriteLoops").Count == 0,
                "Second Close retried side effects or restarted a retired preference writer.");
            if (!readyIo) RequireNoSettings(directory);
            else
            {
                byte[] after = await File.ReadAllBytesAsync(settings, token);
                Require(restoredBytes!.AsSpan().SequenceEqual(after), "A late write replaced the restored IO fixture settings.");
            }
            using (ProfileLease released = ProfileLease.Acquire(directory)) { }
            Console.WriteLine($"SMOKE preference close negative: PASS; {scenario}/{language}/{variant}; actual scale={shell.RenderScaling}; raw write faulted; ownership quiescent/disposed; safe error visible then second Close exits; no late write.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            restore.Release.TrySetResult(new(new SessionSnapshot(), null, false));
            if (shell != null)
            {
                try
                {
                    if (Get<bool>(shell, "_shutdownRunning"))
                        await UntilAsync(() => !Get<bool>(shell, "_shutdownRunning"), "negative fixture cleanup close", CancellationToken.None);
                    else if (!Get<bool>(shell, "_allowClose")) await CloseAsync(shell).WaitAsync(Deadline);
                    if (Get<bool>(shell, "_allowClose")) shell.Close();
                }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            if (host != null && !host.IsProfileReleased)
            {
                try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
                catch (Exception error)
                {
                    if (host.State != DesktopSessionState.Stopped || !host.IsProfileReleased) cleanupFailures.Add(error);
                    else Console.WriteLine("SMOKE expected failed-write disposal during negative fixture cleanup: " + error.GetType().Name);
                }
            }
            if ((host == null || host.IsProfileReleased) && (shell == null || !shell.IsVisible))
            {
                try
                {
                    using (ProfileLease released = ProfileLease.Acquire(directory)) { }
                    Directory.Delete(directory, recursive: true); // Includes only this fixture's directory IO fault/original.
                }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            else cleanupFailures.Add(new InvalidOperationException("Negative Close fixture not safely disposed/closed; retained: " + directory));
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Negative Close cleanup failed; not a PASS.",
                failure == null ? cleanupFailures : cleanupFailures.Prepend(failure));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RequireNoSettings(string directory) => Require(
        !Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetFileName(path).StartsWith("settings", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith(".settings", StringComparison.Ordinal)),
        "Failed/overtaken startup published settings, backup or temporary settings evidence.");
    private static void RequireLeaseHeld(string directory)
    {
        bool held = false;
        try { using ProfileLease unexpected = ProfileLease.Acquire(directory); }
        catch (IOException) { held = true; }
        Require(held, "Shell released the profile lease while its restore owner was still live.");
    }
    private static bool IsIoFailure(Exception error) => error is IOException or UnauthorizedAccessException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsIoFailure);
    private static async Task<Exception?> ObserveTerminatedAsync(Task task, string stage, CancellationToken token)
    {
        try { await task.WaitAsync(Deadline, token); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException && !token.IsCancellationRequested)
        { Console.WriteLine($"SMOKE expected negative task termination: {stage}: {error.GetType().Name}"); return error; }
    }
    private static async Task<Exception> RequireWriteFailureAsync(Task task, string stage, CancellationToken token)
    {
        Exception? error = await ObserveTerminatedAsync(task, stage, token);
        Require(task.IsFaulted && error != null, "A failed preference write reported persistence success/cancellation: " + stage);
        return error!;
    }

    private static async Task ResizeColumnAsync(MediaNativeInput native, MainWindow shell, DataGrid grid,
        DataGridColumn column, CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(shell.UpdateLayout, DispatcherPriority.Background);
        DataGridColumnHeader header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => h.IsEffectivelyVisible && Equals(h.Content, column.Header));
        Require(header.IsEffectivelyEnabled && header.Bounds.Width > 16 && header.Bounds.Height > 0,
            "Native column resize has no enabled, laid-out header.");
        double before = column.ActualWidth;
        // DataGrid's native right-edge resize hit area. No routed PointerPressed/DragDelta or synthetic fallback.
        // Fractional widths can put the last DIP in the adjacent header's left
        // gripper. Select a real hit-tested point just inside THIS right-edge band;
        // still require actual native dragging to resize this exact column below.
        Point? ownEdge = null;
        Visual? lastHit = null;
        // Layout bounds can advance ahead of the native hit-test/render scene after a
        // synchronous property burst. Await the actual hit geometry, not a fixed sleep.
        await UntilAsync(() =>
        {
            shell.UpdateLayout();
            // A column-profile refresh may replace realized headers. Always inspect
            // the current visual header, never a detached predecessor's bounds.
            header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Single(h => h.IsEffectivelyVisible && Equals(h.Content, column.Header));
            foreach (double inset in new[] { 2d, 3d, 4d, 5d, 6d })
            {
                Point? candidate = header.TranslatePoint(new Point(header.Bounds.Width - inset, header.Bounds.Height / 2), shell);
                if (candidate is not Point point) continue;
                lastHit = shell.InputHitTest(point) as Visual;
                if (ReferenceEquals(lastHit, header) || lastHit?.GetVisualAncestors().Contains(header) == true)
                { ownEdge = point; return true; }
            }
            return false;
        }, "native header scene/hit materialization; column=" + column.Header, token);
        Point edge = ownEdge ?? throw new InvalidOperationException("Native column right-edge band does not hit intended header: "
            + column.Header + "; bounds=" + header.Bounds + "; actual=" + column.ActualWidth + "; hit=" + lastHit?.GetType().Name);
        token.ThrowIfCancellationRequested();
        await native.MoveAsync(shell, edge);
        try
        {
            await native.ButtonAsync(true);
            await native.MoveAsync(shell, edge + new Point(12, 0));
            await native.MoveAsync(shell, edge + new Point(24, 0));
        }
        finally { await native.ButtonAsync(false); }
        await UntilAsync(() => column.Width.IsAbsolute && column.ActualWidth > before + 8,
            "native right-edge column resize", token);
    }

    private static void RequireSavedColumn(Config config, double width) => Require(
        config.GetLayoutProfile("Advanced").ColumnWidths.TryGetValue(ColumnKey, out double saved)
            && Math.Abs(saved - width) < 0.01, "The latest startup column edit was not durably committed.");

    private static void RequireNoPreferenceFailure(MainWindow shell) => Require(
        !Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(entry =>
            entry.Operation.StartsWith("preferences.", StringComparison.Ordinal)),
        "A startup layout produced preference failure feedback (including false not-ready save errors).");

    private static void Capture(MainWindow shell, string name)
    {
        shell.UpdateLayout();
        double scale = shell.RenderScaling;
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(shell.Bounds.Width * scale),
            (int)Math.Ceiling(shell.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        image.Render(shell);
        string path = Path.Combine(AppContext.BaseDirectory, name);
        image.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE window render: " + path);
        // Program.CaptureWindow reads the outer process profile's mode; this child has its own explicit profile.
        Console.WriteLine("SMOKE capture metadata: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            fileName = name, mode = shell.SessionHost.Config.WorkspaceMode, theme = shell.ActualThemeVariant.ToString(),
            language = Texts.Language, renderScaling = scale, clientDip = shell.ClientSize.ToString(),
            boundsDip = shell.Bounds.ToString(), imagePixels = image.PixelSize.ToString(), window = nameof(MainWindow)
        }));
    }

    private static string CaptureName(string stage, string language, ThemeVariant variant, double scale) =>
        FormattableString.Invariant($"smoke-preference-startup-{stage}-{language}-{variant}-{scale:0.###}x.png");
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target)!;
    private static Task CallTask(MainWindow shell, string name) => (Task)(typeof(MainWindow).GetMethod(name, PrivateInstance)
        ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(shell, null)!;
    private static Task FlushWindow(MainWindow shell) => CallTask(shell, "FlushWindowPreferencesAsync");
    private static Task CloseAsync(MainWindow shell) => CallTask(shell, "CloseApplicationAsync");
    private static async Task UntilAsync(Func<bool> predicate, string stage, CancellationToken token)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= Deadline) throw new TimeoutException("Preference startup timed out: " + stage);
            await Task.Delay(40, token);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class HeldStore(bool honorCancellation = true) : ISessionStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SessionLoadResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return honorCancellation ? Release.Task.WaitAsync(cancellationToken) : Release.Task;
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
