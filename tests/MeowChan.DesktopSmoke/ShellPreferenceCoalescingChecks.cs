using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>Held-write regression at actual shell flush/event boundaries, not just the host coalescer.</summary>
internal static class ShellPreferenceCoalescingChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(MainWindow owner)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("shell-preference-coalescing-");
        string language = Texts.Language;
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        SemaphoreSlim? gate = null;
        bool gateHeld = false;
        Task? close = null;
        PerformanceDiagnostics.DiagnosticRun? diagnostics = null;
        try
        {
            new Config { DownloadPath = Path.Combine(directory, "media"), SaveUrlsOnExit = false,
                AutomaticUpdateChecks = false, RememberReadingPosition = true, WorkspaceMode = "Advanced",
                CloseToTray = false, MinimizeToTray = false, ShowErrorNotifications = false }.SaveConfig(directory);
            host = new DesktopSessionHost(directory, Array.Empty<IImageBoard>());
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && Get<bool>(shell, "_windowPreferencesReady") && host.AcceptedCommandCount == 0);
            StopTimers(shell);
            await FlushWindow(shell).WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => host.PreferenceWriteCounts.Active == 0 && host.PreferenceWriteCounts.Pending == 0);
            if (!PerformanceDiagnostics.Enabled)
                diagnostics = PerformanceDiagnostics.BeginRun("pf01-shell-preferences", "integration-root", "held-128-burst");
            gate = Get<SemaphoreSlim>(host, "_commands");
            await gate.WaitAsync().WaitAsync(Deadline); gateHeld = true;
            Set(shell, "_lastNormalClient", new Size(1000, 700));
            Task firstWindow = FlushWindow(shell);
            LocalReadingPosition Position(int value) => new("fixture", "b", "1", value.ToString(), -value, value.ToString());
            Call(shell, "SaveConsciousReadingPosition", Position(1000));
            Task firstReading = Get<Task>(shell, "_readingPositionSave");
            await Program.WaitUntilAsync(() => host.PreferenceWriteCounts.Active == 2);
            var windowTasks = new HashSet<Task> { firstWindow };
            var galleryTasks = new HashSet<Task>();
            var readingTasks = new HashSet<Task> { firstReading };
            var expectedWidths = new Dictionary<string, double>(StringComparer.Ordinal);
            var expectedOrders = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] modes = ["Barebones", "Simple", "Advanced"];
            DataGrid grid = shell.FindControl<DataGrid>("ThreadsDataGrid")!;
            DataGridColumn column = grid.Columns.First(candidate => candidate.SortMemberPath == nameof(ThreadRow.Title));
            const string columnKey = "ThreadsDataGrid.Title";
            for (int index = 0; index < 128; index++)
            {
                string mode = modes[index % modes.Length];
                host.Config.WorkspaceMode = mode;
                // Real column notification captures each mode's independent delta before the actual flush.
                column.Width = new DataGridLength(300 + index);
                expectedWidths[mode] = 300 + index;
                int order = index % grid.Columns.Count;
                Get<Dictionary<string, Dictionary<string, int>>>(shell, "_pendingColumnOrder")[mode] =
                    new(StringComparer.Ordinal) { [columnKey] = order };
                expectedOrders[mode] = order;
                Set(shell, "_lastNormalClient", new Size(1000 + index, 700));
                Set(shell, "_lastNormalPosition", new PixelPoint(index, index + 10));
                windowTasks.Add(FlushWindow(shell));
                Call(shell, "QueueGalleryPreference", index == 0 ? (bool?)true : null, (int?)(index % 101));
                galleryTasks.Add(FlushGallery(shell));
                Call(shell, "SaveConsciousReadingPosition", Position(2000 + index));
                readingTasks.Add(Get<Task>(shell, "_readingPositionSave"));
            }
            Require(windowTasks.Count <= 2 && galleryTasks.Count <= 2 && readingTasks.Count <= 2,
                $"Shell flushes retained unbounded tasks before host admission: window={windowTasks.Count}, gallery={galleryTasks.Count}, reading={readingTasks.Count}.");
            Require(host.PreferenceWriteCounts == (2, 2, 0)
                && windowTasks.Concat(galleryTasks).Concat(readingTasks).All(task => !task.IsCompleted),
                "The held shell burst did not reach exactly one active + one pending value for each host save unit.");
            // Prevent unrelated native geometry notifications from adding new automatic values during release.
            Set(shell, "_windowPreferencesReady", false); StopTimers(shell);
            gate.Release(); gateHeld = false;
            await Task.WhenAll(windowTasks.Concat(galleryTasks).Concat(readingTasks)).WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => host.PreferenceWriteCounts.Active == 0 && host.PreferenceWriteCounts.Pending == 0);
            if (diagnostics != null)
            {
                var events = diagnostics.Snapshot().Events;
                Require(events.Count(item => item.Phase == "host.preferences.view" && item.Outcome == "begin") == 2
                    && events.Count(item => item.Phase == "host.preferences.reading" && item.Outcome == "begin") == 2,
                    "Releasing the held burst replayed stale shell snapshots instead of two writes per save unit.");
            }
            Config saved = Config.LoadConfig(directory);
            // Last input is index 127: 127 % 101 is 26.
            Require(saved.WindowWidth == 1127 && saved.WindowPositionX == 127 && saved.WindowPositionY == 137
                && saved.GalleryContextOpen && saved.VideoVolume == 26,
                "A mixed shell burst lost its final geometry/context/volume values. "
                + "Expected width=1127, x=127, y=137, context=True, volume=26; actual " + SavedValues(saved)
                + $"; captured client={Get<Size>(shell, "_lastNormalClient")}, captured position={Get<PixelPoint>(shell, "_lastNormalPosition")}, native client={shell.ClientSize}, native position={shell.Position}.");
            foreach (string mode in modes)
            {
                PresentationProfile profile = saved.GetLayoutProfile(mode);
                Require(profile.ColumnWidths.TryGetValue(columnKey, out double width) && width == expectedWidths[mode]
                    && profile.ColumnOrder.TryGetValue(columnKey, out int order) && order == expectedOrders[mode],
                    $"Coalescing lost {mode}/{columnKey}: expected width={expectedWidths[mode]}, order={expectedOrders[mode]}; "
                    + $"actual width={(profile.ColumnWidths.TryGetValue(columnKey, out double actualWidth) ? actualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing")}, "
                    + $"order={(profile.ColumnOrder.TryGetValue(columnKey, out int actualOrder) ? actualOrder.ToString() : "missing")}.");
            }
            LocalReadingPosition reading = await host.ReadReadingPositionAsync().WaitAsync(Deadline)
                ?? throw new InvalidOperationException("The conscious reading burst did not persist an anchor.");
            Require(reading.PostId == "2127" && reading.Top == -2127
                && Get<object>(shell, "_readingResumeState").ToString() == "Present",
                $"The shared shell reading completion retained an older position/revision: expected post=2127, top=-2127, state=Present; actual post={reading.PostId}, top={reading.Top}, state={Get<object>(shell, "_readingResumeState")}.");

            // Failed automatic observations must remain visible, retain host drafts, and keep explicit final
            // flush failures observable. Remove the IO faults and retry the retained drafts without replaying events.
            string settingsPath = Path.Combine(directory, "settings.json");
            string readingPath = Path.Combine(directory, "reading-position.json");
            File.Delete(settingsPath); Directory.CreateDirectory(settingsPath);
            File.Delete(readingPath); Directory.CreateDirectory(readingPath);
            Set(shell, "_windowPreferencesReady", true);
            Set(shell, "_lastNormalClient", new Size(1160, 720));
            Task failedWindow = FlushWindow(shell);
            Call(shell, "QueueGalleryPreference", (bool?)false, (int?)63);
            Task failedGallery = FlushGallery(shell);
            Call(shell, "SaveConsciousReadingPosition", Position(3001));
            Task failedReading = Get<Task>(shell, "_readingPositionSave");
            Set(shell, "_windowPreferencesReady", false); StopTimers(shell);
            await Task.WhenAll(failedWindow, failedGallery, failedReading).WaitAsync(Deadline);
            Require(host.PreferenceWriteCounts.Failed == 2
                && shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible
                && Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(entry => entry.Operation == "preferences.reading-position")
                && Get<SessionFeedbackStore>(shell, "_feedback").Entries.Any(entry => entry.Operation is "preferences.window" or "preferences.gallery"),
                "Automatic IO faults disappeared or their unsaved drafts were dropped.");
            bool finalFailed = false;
            try { await FlushFinal(shell, failedWindow, failedGallery, failedReading).WaitAsync(Deadline); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { finalFailed = true; }
            Require(finalFailed, "Explicit final preference flush hid a persistent save failure.");
            Directory.Delete(settingsPath); Directory.Delete(readingPath);
            await FlushFinal(shell, failedWindow, failedGallery, failedReading).WaitAsync(Deadline);
            saved = Config.LoadConfig(directory);
            Require(saved.WindowWidth == 1160 && !saved.GalleryContextOpen && saved.VideoVolume == 63
                && (await host.ReadReadingPositionAsync().WaitAsync(Deadline))?.PostId == "3001",
                "Retrying failed final writers did not retain every latest shell draft. "
                + "Expected width=1160, context=False, volume=63, reading post=3001; actual " + SavedValues(saved)
                + $"; reading payload={File.ReadAllText(readingPath)}.");

            // Actual CloseApplicationAsync must include the final shell values while admission is quiescing.
            Set(shell, "_windowPreferencesReady", true);
            await gate.WaitAsync().WaitAsync(Deadline); gateHeld = true;
            Set(shell, "_lastNormalClient", new Size(1180, 740));
            Call(shell, "QueueGalleryPreference", (bool?)true, (int?)99);
            Call(shell, "SaveConsciousReadingPosition", Position(4001));
            close = (Task)Call(shell, "CloseApplicationAsync")!;
            Require(!close.IsCompleted && host.State == DesktopSessionState.Stopping,
                "Close did not wait for the real held final writers after early engine cancellation.");
            bool leaseHeld = false;
            try { using ProfileLease unexpected = ProfileLease.Acquire(directory); }
            catch (IOException) { leaseHeld = true; }
            Require(leaseHeld, "Final shell writers released ownership before their actual completion.");
            gate.Release(); gateHeld = false;
            await close.WaitAsync(Deadline);
            saved = Config.LoadConfig(directory);
            Require(!shell.IsVisible && host.State == DesktopSessionState.Stopped && saved.WindowWidth == 1180
                && saved.GalleryContextOpen && saved.VideoVolume == 99
                && File.ReadAllText(readingPath).Contains("4001", StringComparison.Ordinal),
                "Actual shutdown lost its last shell preference/reading values. "
                + "Expected closed=True, state=Stopped, width=1180, context=True, volume=99, reading post=4001; actual "
                + SavedValues(saved) + $"; visible={shell.IsVisible}, state={host.State}, reading payload={File.ReadAllText(readingPath)}.");
            using (ProfileLease released = ProfileLease.Acquire(directory)) { }
            Console.WriteLine($"SMOKE shell preference coalescing: PASS; 128 mixed flushes, distinct held shell tasks window/gallery/reading={windowTasks.Count}/{galleryTasks.Count}/{readingTasks.Count}; all profile-column/geometry/context/volume/reading deltas survive; failures visible and retained/retryable; actual shutdown drains final values. Offline fixture, no native input claim.");
        }
        finally
        {
            if (gateHeld) gate!.Release();
            // Remove fixture-owned IO failure directories even if an assertion failed before retry.
            foreach (string name in new[] { "settings.json", "reading-position.json" })
                if (Directory.Exists(Path.Combine(directory, name))) Directory.Delete(Path.Combine(directory, name));
            if (shell != null)
            {
                StopTimers(shell);
                close ??= (Task)Call(shell, "CloseApplicationAsync")!;
                await close.WaitAsync(Deadline);
            }
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            diagnostics?.Dispose();
            Texts.SetLanguage(language);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string SavedValues(Config saved) => FormattableString.Invariant(
        $"width={saved.WindowWidth}, height={saved.WindowHeight}, x={saved.WindowPositionX}, y={saved.WindowPositionY}, maximized={saved.WindowMaximized}, context={saved.GalleryContextOpen}, volume={saved.VideoVolume}");

    private static Task FlushWindow(MainWindow shell) => (Task)Call(shell, "FlushWindowPreferencesAsync")!;
    private static Task FlushGallery(MainWindow shell) => (Task)Call(shell, "FlushGalleryPreferencesAsync")!;
    private static Task FlushFinal(MainWindow shell, params Task[] tasks) => (Task)Call(shell, "FlushFinalPreferencesAsync", (object)tasks)!;
    private static void StopTimers(MainWindow shell)
    {
        foreach (string field in new[] { "_windowPreferenceTimer", "_galleryPreferenceTimer" })
            (typeof(MainWindow).GetField(field, PrivateInstance)!.GetValue(shell) as DispatcherTimer)?.Stop();
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, arguments);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
