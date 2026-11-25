using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root hook: await SettingsLanguageRefreshChecks.RunAsync(owner), on the native UI thread.
// Native visible windows; managed routed clicks/global Texts notifications, not OS input.
// No DesktopSessionHost, providers, network, environment redirects or production profile.
internal static class SettingsLanguageRefreshChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly string[] RefreshLanguages = ["fr", "de", "es", "ru", "en"];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string language = Texts.Language;
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-settings-language-refresh-");
        try
        {
            foreach (string required in RefreshLanguages)
                Require(Texts.Languages.Any(item => item.Code == required), "Bundled language unavailable: " + required);
            await SettingsInvalidDraftAsync(owner, Path.Combine(root, "numeric-format"), "numeric-format");
            await SettingsInvalidDraftAsync(owner, Path.Combine(root, "numeric-range"), "numeric-range");
            await SettingsInvalidDraftAsync(owner, Path.Combine(root, "path-syntax"), "path-syntax");
            await PendingDestinationAsync(owner, Path.Combine(root, "pending-check"));
            await FirstRunPathAsync(owner, Path.Combine(root, "first-run-syntax"), syntax: true);
            await FirstRunPathAsync(owner, Path.Combine(root, "first-run-missing"), syntax: false);
            Console.WriteLine("SMOKE settings language refresh: Settings numeric format/range and path errors, pending destination check, FirstRun syntax/Missing errors; FR/DE/ES/RU/EN labels, raw drafts, selections/caret/focus, unchanged config/files and no accept/apply passed. Managed input only; no translation-quality/native-speech claim.");
        }
        finally
        {
            Texts.SetLanguage(language);
            await SettleAsync();
            owner.Activate();
            Directory.Delete(root, recursive: true); // Only the helper's newly generated fixture tree.
        }
    }

    private static async Task SettingsInvalidDraftAsync(MainWindow owner, string profile, string scenario)
    {
        Texts.SetLanguage("en");
        Config config = Seed(profile);
        string baselineConfig = ConfigState(config);
        string[] baselineFiles = Files(profile);
        int applies = 0;
        SettingsWindow window = new(config, _ =>
        {
            applies++;
            throw new InvalidOperationException("Invalid fixture draft unexpectedly reached Apply.");
        });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            SettingsEditor editor = window.Editor;
            await SettingsSectionChecks.OpenAsync(editor, "settings.downloads");
            editor.ShowWarningNotifications = !config.ShowWarningNotifications; // Unrelated unsaved draft.
            editor.SelectedTheme = editor.ThemeChoices.Single(item => item.Value == "Light");
            editor.SelectedReadingFontSize = editor.ReadingFontSizeChoices.Single(item => item.Value == "Large");
            TextBox path = Need<TextBox>(editor, "DownloadPathInput");
            NumericUpDown number = Need<NumericUpDown>(editor, "RecheckTimerInput");
            number.ApplyTemplate();
            TextBox numericText = number.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "PART_TextBox");
            TextBox input = scenario == "path-syntax" ? path : numericText;
            // A fractional value inside the native range isolates our whole-number
            // validation from Avalonia's own out-of-range DataValidationErrors.
            string raw = scenario switch
            {
                "path-syntax" => "    ",
                "numeric-range" => 60.5m.ToString(number.NumberFormat),
                _ => "invalid raw 17"
            };
            Require(input.Focus(), scenario + ": could not focus the visible input.");
            input.Text = raw;
            await Program.WaitUntilAsync(() => scenario == "path-syntax" ? editor.DownloadPath == raw : number.Text == raw);
            Click(Need<Button>(editor, "SaveSettingsButton"));
            TextBlock error = Need<TextBlock>(editor, scenario == "path-syntax" ? "DownloadPathInputError" : "RecheckTimerInputError");
            await Program.WaitUntilAsync(() => !editor.IsBusy && error.IsEffectivelyVisible && input.IsFocused);
            await SettleAsync(window);
            SelectRaw(input);
            string draft = DraftState(editor);
            InputState selection = InputState.Capture(input);
            string? details = editor.ErrorDetails;
            Expander expander = Need<Expander>(editor, "SettingsErrorDetails");
            bool expanded = expander.IsExpanded;
            foreach (string current in RefreshLanguages)
            {
                // A passive global-language change, not a user click which would intentionally move focus.
                Texts.SetLanguage(current);
                await SettleAsync(window);
                string expected = scenario == "path-syntax" ? Texts.T("settings.pathInvalid")
                    : NumericError(editor, scenario == "numeric-range" ? "settings.fieldInteger" : "settings.fieldNumber");
                Require(Texts.Language == current && error.IsEffectivelyVisible && error.Text == expected,
                    scenario + "/" + current + ": field error is missing or contains stale field label/unit text.");
                Require(editor.ErrorMessage == Texts.T("settings.notSaved") && editor.StatusMessage == Texts.T("settings.unsaved")
                    && Need<TextBlock>(editor, "SettingsError").Text == editor.ErrorMessage,
                    scenario + "/" + current + ": visible summary/status retained the previous language.");
                Require(AutomationProperties.GetHelpText(scenario == "path-syntax" ? (Control)path : number) == expected
                    && AutomationProperties.GetHelpText(input) == expected,
                    scenario + "/" + current + ": focused-input accessible error help is stale.");
                Require(input.Text == raw && InputState.Capture(input) == selection
                    && ReferenceEquals(window.FocusManager?.GetFocusedElement(), input),
                    scenario + "/" + current + ": raw input, selection, caret or focus changed.");
                Require(ReferenceEquals(editor, window.Editor) && DraftState(editor) == draft
                    && editor.ErrorDetails == details && expander.IsExpanded == expanded,
                    scenario + "/" + current + ": editor/draft/details/expander changed during rendering-only refresh.");
                Require(applies == 0 && ConfigState(config) == baselineConfig && Files(profile).SequenceEqual(baselineFiles),
                    scenario + "/" + current + ": rejected draft or language refresh applied/persisted settings.");
            }
            Console.WriteLine("SMOKE settings language " + scenario + ": current error/help/summary, raw numeric/path and semantic draft, selection/caret/focus retained across five locales.");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(Deadline);
        }
    }

    private static async Task PendingDestinationAsync(MainWindow owner, string profile)
    {
        Texts.SetLanguage("en");
        Config config = Seed(profile);
        string baselineConfig = ConfigState(config);
        string[] baselineFiles = Files(profile);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0, applies = 0;
        DownloadDestinationValidator validator = new(1, (path, _) =>
        {
            Interlocked.Increment(ref probes);
            entered.TrySetResult();
            try
            {
                // Cleanup failsafe only; the pending assertions below have a separate
                // four-second budget, below the product's five-second caller timeout.
                if (!release.Wait(Deadline)) throw new TimeoutException("Pending-check fixture was not released.");
                return new(DownloadDestinationStatus.Missing, path);
            }
            finally { finished.TrySetResult(); }
        });
        SettingsWindow window = new(config, _ => { applies++; return Task.CompletedTask; });
        window.Editor.DestinationValidator = validator;
        Task dialog = window.ShowDialog(owner);
        Exception? failureBeforeCleanup = null;
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            SettingsEditor editor = window.Editor;
            await SettingsSectionChecks.OpenAsync(editor, "settings.downloads");
            TextBox path = Need<TextBox>(editor, "DownloadPathInput");
            string raw = Path.Combine(profile, "missing destination ü 日本");
            path.Text = raw;
            await Program.WaitUntilAsync(() => editor.DownloadPath == raw);
            Stopwatch pendingBudget = Stopwatch.StartNew();
            void RequirePendingBudget() => Require(pendingBudget.Elapsed < TimeSpan.FromSeconds(4),
                "Pending-check fixture exceeded its four-second budget; this run cannot distinguish UI scheduling delay from the product's five-second timeout.");
            Click(Need<Button>(editor, "CheckDestinationButton"));
            await entered.Task.WaitAsync(Deadline);
            RequirePendingBudget();
            Require(editor.IsBusy && editor.IsCheckingDestination, "Fixture check was not in flight.");
            string draft = DraftState(editor);
            foreach (string current in new[] { "fr", "de", "ru" })
            {
                Texts.SetLanguage(current);
                await SettleAsync(window);
                RequirePendingBudget();
                Require(editor.IsBusy && editor.IsCheckingDestination && !finished.Task.IsCompleted
                    && Volatile.Read(ref probes) == 1,
                    current + ": language refresh cancelled/restarted the pending destination check.");
                Require(DraftState(editor) == draft && path.Text == raw && !editor.HasError && applies == 0,
                    current + ": pending-check refresh changed the draft or invented a result.");
                Require(ConfigState(config) == baselineConfig && Files(profile).SequenceEqual(baselineFiles),
                    current + ": pending-check refresh changed active or persisted settings.");
            }
            RequirePendingBudget();
            release.Set();
            await finished.Task.WaitAsync(Deadline);
            TextBlock error = Need<TextBlock>(editor, "DownloadPathInputError");
            await Program.WaitUntilAsync(() => !editor.IsBusy && !editor.IsCheckingDestination && error.IsEffectivelyVisible && path.IsFocused);
            await SettleAsync(window);
            Require(error.Text == Texts.T("destination.Missing") && editor.DestinationStatusText == Texts.T("destination.Missing"),
                "The accepted pending result was dropped as stale or lost its localized error.");
            SelectRaw(path);
            InputState selection = InputState.Capture(path);
            string completedDraft = DraftState(editor);
            foreach (string current in new[] { "es", "en" })
            {
                Texts.SetLanguage(current);
                await SettleAsync(window);
                Require(error.IsEffectivelyVisible && error.Text == Texts.T("destination.Missing")
                    && Need<TextBlock>(editor, "DestinationCheckStatus").Text == Texts.T("destination.Missing")
                    && AutomationProperties.GetHelpText(path) == error.Text,
                    current + ": completed destination result/error/help was cleared or remained stale.");
                Require(path.IsFocused && InputState.Capture(path) == selection && DraftState(editor) == completedDraft
                    && probes == 1 && applies == 0 && !Directory.Exists(raw)
                    && ConfigState(config) == baselineConfig && Files(profile).SequenceEqual(baselineFiles),
                    current + ": result relocalization changed focus/draft, re-probed or persisted settings.");
            }
            Console.WriteLine($"SMOKE settings pending fixture released within budget; observed completion after {pendingBudget.Elapsed.TotalMilliseconds:F0} ms (five-second product timeout; twenty-second cleanup failsafe).");
            Console.WriteLine("SMOKE settings language pending check: one gated fixture probe survives three language changes; released result is applied to UI and remains localized on two more changes, never committed.");
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                release.Set();
                if (entered.Task.IsCompleted) await finished.Task.WaitAsync(Deadline);
                await Program.WaitUntilAsync(() => !window.Editor.IsBusy);
                if (window.IsVisible) window.Close();
                await dialog.WaitAsync(Deadline);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in SettingsLanguageRefreshChecks: " + exception));
        }
    }

    private static async Task FirstRunPathAsync(MainWindow owner, string profile, bool syntax)
    {
        Texts.SetLanguage("en");
        Config config = Seed(profile);
        string baselineConfig = ConfigState(config);
        string[] baselineFiles = Files(profile);
        int accepts = 0;
        // A real FirstRun window with a private baseline/validator. No automatic Host journey
        // or accepted commit: these are covered by FirstRunSettingsChecks, not this helper.
        FirstRunSetupWindow window = new(config, _ =>
        {
            accepts++;
            return Task.FromResult(new FirstRunSetupCommitResult(false));
        }, new DownloadDestinationValidator());
        Task<FirstRunSetupRunResult> dialog = window.ShowAsync(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            TextBox path = Need<TextBox>(window, "SetupFolderInput");
            TextBox bandwidth = Need<TextBox>(window, "SetupBandwidthInput");
            TextBox parallel = Need<TextBox>(window, "SetupParallelInput");
            string raw = syntax ? "    " : Path.Combine(profile, "missing first-run folder ü 日本");
            path.Text = raw;
            bandwidth.Text = 2.5m.ToString(CultureInfo.CurrentCulture);
            parallel.Text = "3";
            await SettleAsync(window); // Drain deferred TextChanged before taking the revision witness.
            Click(Need<Button>(window, syntax ? "SetupAcceptButton" : "SetupCheckButton"));
            TextBlock error = Need<TextBlock>(window, "SetupPathError");
            await Program.WaitUntilAsync(() => !window.IsWorking && error.IsEffectivelyVisible && path.IsFocused);
            await SettleAsync(window);
            SelectRaw(path);
            InputState selection = InputState.Capture(path);
            long revision = window.DraftRevision;
            string? rawBandwidth = bandwidth.Text, rawParallel = parallel.Text;
            string key = syntax ? "settings.pathInvalid" : "destination.Missing";
            foreach (string current in RefreshLanguages)
            {
                Texts.SetLanguage(current);
                await SettleAsync(window);
                Require(error.IsEffectivelyVisible && error.Text == Texts.T(key),
                    "FirstRun/" + key + "/" + current + ": retained path error was stale or replaced with check status.");
                Require(!Need<TextBlock>(window, "SetupFolderStatus").IsEffectivelyVisible
                    && string.IsNullOrEmpty(Need<TextBlock>(window, "SetupStatus").Text)
                    && Need<TextBlock>(window, "SetupFolderLabel").Text == Texts.T("settings.downloadPath")
                    && AutomationProperties.GetName(path) == Texts.T("settings.downloadPath"),
                    "FirstRun/" + current + ": status or visible/accessibility label failed to refresh.");
                Require(path.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), path)
                    && InputState.Capture(path) == selection && path.Text == raw
                    && bandwidth.Text == rawBandwidth && parallel.Text == rawParallel && window.DraftRevision == revision,
                    "FirstRun/" + current + ": refresh altered focus/selection/raw limits/path or advanced draft revision.");
                Require(window.IsVisible && !window.IsWorking && !window.IsCommitting && accepts == 0
                    && !Need<TextBlock>(window, "SetupLimitsError").IsVisible && !Need<TextBlock>(window, "SetupError").IsVisible
                    && ConfigState(config) == baselineConfig && Files(profile).SequenceEqual(baselineFiles)
                    && (syntax || !Directory.Exists(raw)),
                    "FirstRun/" + current + ": refresh accepted/persisted setup, hid the dialog, created a target or invented another error.");
            }
            Click(Need<Button>(window, "SetupLaterButton"));
            FirstRunSetupRunResult result = await dialog.WaitAsync(Deadline);
            Require(result.Outcome == FirstRunSetupOutcome.Deferred && config.FirstRunSetupPending
                && config.FirstRunSetupVersion == 0 && Files(profile).SequenceEqual(baselineFiles),
                "FirstRun Later after language changes completed or persisted setup.");
            Console.WriteLine("SMOKE first-run language " + key + ": retained error identity, status, raw fields, exact revision and selection/caret/focus passed in five locales; Later stays pending.");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(Deadline);
        }
    }

    private static Config Seed(string profile)
    {
        Config config = Config.LoadConfig(profile); // Bind while empty, before adding fixture data.
        config.DownloadPath = Path.Combine(profile, "initial downloads");
        config.WorkspaceMode = "Advanced";
        config.Language = "en";
        config.AutomaticUpdateChecks = config.MonitorClipboardUrls = config.CloseToTray = config.MinimizeToTray = false;
        config.MinimumFreeSpaceGiB = 0;
        config.BandwidthLimiter = 5 * 1024 * 1024;
        config.MaxParallelDownloads = 2;
        config.FirstRunSetupPending = true;
        config.FirstRunSetupVersion = 0;
        config.SaveConfig(profile); // No host and no subsequent successful Save in these checks.
        return config;
    }

    private static string NumericError(SettingsEditor editor, string key)
    {
        string label = editor.RecheckLabel;
        string suffix = "(" + editor.SecondsHelp + ")";
        if (!label.Contains(suffix, StringComparison.OrdinalIgnoreCase)) label += " " + suffix;
        return key == "settings.fieldInteger" ? Texts.T(key, label, 10m, 4294967m) : Texts.T(key, label);
    }

    // Compare semantic user choices/values, not relocalized choice-object identities or display strings.
    private static string DraftState(SettingsEditor editor) => JsonSerializer.Serialize(new
    {
        editor.DownloadPath, editor.RecheckTimer, editor.BandwidthLimiterMB, editor.MaxParallelDownloads,
        editor.ParallelDownloadsSafetyLimit, editor.MinimumFreeSpaceGiB, editor.ProductionWorkers,
        editor.WorkerIdleMinutes, editor.SourceHeapMiB, editor.FilterHeapMiB, editor.ShowWarningNotifications,
        editor.ShowInformationNotifications, editor.ShowErrorNotifications, editor.MonitorClipboardUrls,
        editor.CompactDensity, editor.SaveUrlsOnExit, editor.VideoAutoplay, editor.VideoStartMuted, editor.VideoVolume,
        editor.GallerySlideshowSeconds, editor.GalleryMediaFocus, editor.GalleryOverlayControls, editor.GalleryOverlayPostContext,
        Theme = editor.SelectedTheme?.Value, Mode = editor.SelectedWorkspaceMode?.Value,
        Language = editor.SelectedLanguage?.Code, ReadingSize = editor.SelectedReadingFontSize?.Value,
        GalleryMode = editor.SelectedGalleryPlaybackMode?.Value,
        Numbers = new[] { "RecheckTimerInput", "BandwidthInput", "ParallelDownloadsInput", "ParallelDownloadsSafetyInput",
            "ReserveInput", "ProductionWorkersInput", "WorkerIdleMinutesInput", "SourceHeapMiBInput", "FilterHeapMiBInput", "GallerySlideshowSecondsInput" }
            .Select(name => new { Name = name, Text = Need<NumericUpDown>(editor, name).Text, Value = Need<NumericUpDown>(editor, name).Value }).ToArray()
    });

    private readonly record struct InputState(string? Text, int Start, int End, int Caret)
    {
        internal static InputState Capture(TextBox input) => new(input.Text, input.SelectionStart, input.SelectionEnd, input.CaretIndex);
    }

    private static void SelectRaw(TextBox input)
    {
        Require(input.Focus(), "Rejected field could not retain focus.");
        int length = input.Text?.Length ?? 0;
        Require(length > 0, "Fixture requires nonempty raw input to witness selection preservation.");
        input.CaretIndex = Math.Min(length, 3);
        input.SelectionStart = length > 1 ? 1 : 0;
        input.SelectionEnd = Math.Min(length, 3);
        Require(input.SelectionEnd > input.SelectionStart, "Fixture did not establish a nonempty selection.");
    }

    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static T Need<T>(Control parent, string name) where T : Control => parent.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing fixture control: " + name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string ConfigState(Config config) => JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config);
    private static string[] Files(string profile) => Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(profile, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
