using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// R0 registers RunAsync in the shared Program dispatch. These are real windows with
// injected bounded probes/commit failures, NOT native picker/ACL/package acceptance.
internal static class FirstRunSetupChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-first-setup-smoke-");
        string language = Texts.Language;
        try
        {
            foreach (string locale in new[] { "en", "de" })
            foreach (bool dark in new[] { false, true })
            {
                Texts.SetLanguage(locale);
                string profile = Path.Combine(root, locale + (dark ? "-dark" : "-light"));
                await CheckLaterAndClose(owner, Path.Combine(profile, "later"), dark, closeWithX: false);
                await CheckLaterAndClose(owner, Path.Combine(profile, "x"), dark, closeWithX: true);
                await CheckAcceptAndFailure(owner, Path.Combine(profile, "accept"), dark);
            }
            await CheckDeferredDraftNotifications(owner, Path.Combine(root, "deferred-check"), accept: false, changeDuringProbe: false);
            await CheckDeferredDraftNotifications(owner, Path.Combine(root, "deferred-accept"), accept: true, changeDuringProbe: false);
            await CheckDeferredDraftNotifications(owner, Path.Combine(root, "changed-probe"), accept: true, changeDuringProbe: true);
            await CheckRetiredProbe(owner, Path.Combine(root, "retired"));
            await CheckOwnerCloseDuringCommit(owner, Path.Combine(root, "owner-close-commit"));
            Console.WriteLine("SMOKE setup: EN/DE Light/Dark Later/X, displayed positive defaults, field focus, explicit accept/failure/retry, durable version/value snapshot and activation-warning no-retry checks completed; injected probes, no native dialog/input claim.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Config Seed(string profile)
    {
        Config config = new() { FirstRunSetupPending = true, MinimumFreeSpaceGiB = 0, VideoVolume = 37 };
        config.SaveConfig(profile);
        return Config.LoadConfig(profile);
    }
    private static DownloadDestinationValidator ReadyProbe() => new(1, (path, _) => new(DownloadDestinationStatus.Ready, path));

    private static async Task CheckLaterAndClose(MainWindow owner, string profile, bool dark, bool closeWithX)
    {
        Config config = Seed(profile);
        byte[] original = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        int commits = 0;
        var window = new FirstRunSetupWindow(config, _ => { commits++; return Task.FromResult(new FirstRunSetupCommitResult(true)); }, ReadyProbe())
        { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Task<FirstRunSetupRunResult> showing = window.ShowAsync(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            Require(window.FindControl<TextBox>("SetupBandwidthInput")!.Text == 1m.ToString(System.Globalization.CultureInfo.CurrentCulture)
                && window.FindControl<TextBox>("SetupParallelInput")!.Text == "5", "Setup changed the existing 1 MiB/s / 5 defaults.");
            Require(window.FindControl<Button>("SetupAcceptButton")!.Content?.ToString() == Texts.T("setup.accept"), "Explicit acceptance action is not localized.");
            if (closeWithX) window.Close(); // managed Window-X path, not native input
            else Click(window.FindControl<Button>("SetupLaterButton")!);
            FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Outcome == FirstRunSetupOutcome.Deferred && commits == 0
                && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original), "Later/X completed setup or wrote settings.");
            Config reopened = Config.LoadConfig(profile);
            Require(reopened.FirstRunSetupPending && reopened.FirstRunSetupVersion == 0, "Later/X lost durable pending provenance.");
        }
        finally { if (window.IsVisible) window.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CheckAcceptAndFailure(MainWindow owner, string profile, bool dark)
    {
        Config config = Seed(profile);
        string folder = Path.Combine(profile, "chosen ü 日本");
        Directory.CreateDirectory(folder);
        byte[] original = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        int attempts = 0;
        var window = new FirstRunSetupWindow(config, values =>
        {
            if (++attempts == 1) return Task.FromException<FirstRunSetupCommitResult>(new IOException("fixture save failure"));
            Config candidate = config.Clone();
            candidate.DownloadPath = values.DownloadPath;
            candidate.BandwidthLimiter = values.BandwidthBytesPerSecond;
            candidate.MaxParallelDownloads = values.ParallelDownloads;
            candidate.FirstRunSetupPending = false;
            candidate.FirstRunSetupVersion = 1;
            candidate.SaveConfig(profile); // existing atomic config writer, fixture only
            config.CopyFrom(candidate);
            return Task.FromResult(new FirstRunSetupCommitResult(true, "fixture activation warning"));
        }, ReadyProbe()) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 480, Height = 520 };
        Task<FirstRunSetupRunResult> showing = window.ShowAsync(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            TextBox path = window.FindControl<TextBox>("SetupFolderInput")!;
            TextBox rate = window.FindControl<TextBox>("SetupBandwidthInput")!;
            TextBox count = window.FindControl<TextBox>("SetupParallelInput")!;
            Button accept = window.FindControl<Button>("SetupAcceptButton")!;
            path.Text = folder;
            rate.Text = "0";
            // Intentionally no dispatcher drain/sleep: validation must survive the
            // queued TextChanged events produced by these immediate assignments.
            Click(accept);
            await Program.WaitUntilAsync(() => rate.IsFocused && window.FindControl<TextBlock>("SetupLimitsError")!.IsVisible);
            Require(rate.Text == "0" && attempts == 0, "Invalid zero rate was corrected or accepted.");
            rate.Text = "2";
            string fractionalCount = 1.5m.ToString(System.Globalization.CultureInfo.CurrentCulture);
            count.Text = fractionalCount;
            Click(accept);
            await Program.WaitUntilAsync(() => count.IsFocused);
            Require(count.Text == fractionalCount && attempts == 0, "Fractional parallelism was accepted or wrong field focused.");
            count.Text = "3";
            Require(window.FindControl<TextBlock>("SetupNormalizedPath")!.Text!.Contains(folder)
                && AutomationProperties.GetName(path) == Texts.T("settings.downloadPath"), "Normalized preview or accessible path name missing.");
            Click(accept);
            await Program.WaitUntilAsync(() => !window.IsWorking && window.FindControl<TextBlock>("SetupError")!.IsVisible);
            Require(attempts == 1 && window.IsVisible && rate.Text == "2" && count.Text == "3" && path.Text == folder
                && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original), "Save failure lost raw draft, closed dialog, or claimed completion.");
            Click(accept); // explicit retry only; the previous result did not confirm persistence
            await Program.WaitUntilAsync(() => !window.IsWorking && !accept.IsVisible);
            Config reopened = Config.LoadConfig(profile);
            Require(attempts == 2 && !reopened.FirstRunSetupPending && reopened.FirstRunSetupVersion == 1
                && reopened.DownloadPath == folder && reopened.BandwidthLimiter == 2097152 && reopened.MaxParallelDownloads == 3
                && reopened.VideoVolume == 37, "Accepted values/version were not one persisted snapshot or changed a non-setup preference.");
            Click(accept); // even a routed event on the hidden button cannot duplicate completion
            Require(attempts == 2 && window.FindControl<TextBlock>("SetupStatus")!.Text!.Contains("fixture activation warning"),
                "Activation warning allowed duplicate acceptance or falsely claimed rollback.");
            Click(window.FindControl<Button>("SetupLaterButton")!); // now labelled Close
            FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Outcome == FirstRunSetupOutcome.Completed && result.Message == "fixture activation warning", "Completed/warning result was lost.");
        }
        finally { if (window.IsVisible) window.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CheckDeferredDraftNotifications(MainWindow owner, string profile, bool accept, bool changeDuringProbe)
    {
        Config config = Seed(profile);
        string folder = Path.Combine(profile, "immediate chosen ü 日本");
        Directory.CreateDirectory(folder);
        byte[] original = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new DownloadDestinationValidator(1, (path, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return new(DownloadDestinationStatus.Ready, path);
        });
        int attempts = 0;
        var window = new FirstRunSetupWindow(config, values =>
        {
            attempts++;
            Config candidate = config.Clone();
            candidate.DownloadPath = values.DownloadPath;
            candidate.BandwidthLimiter = values.BandwidthBytesPerSecond;
            candidate.MaxParallelDownloads = values.ParallelDownloads;
            candidate.FirstRunSetupPending = false;
            candidate.FirstRunSetupVersion = 1;
            candidate.SaveConfig(profile);
            return Task.FromResult(new FirstRunSetupCommitResult(true));
        }, probe);
        Task<FirstRunSetupRunResult> showing = window.ShowAsync(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            TextBox path = window.FindControl<TextBox>("SetupFolderInput")!;
            TextBox rate = window.FindControl<TextBox>("SetupBandwidthInput")!;
            TextBox count = window.FindControl<TextBox>("SetupParallelInput")!;
            path.Text = folder;
            rate.Text = "2";
            count.Text = "3";
            // Action capture must precede (and subsume) deferred notifications.
            Click(window.FindControl<Button>(accept ? "SetupAcceptButton" : "SetupCheckButton")!);
            long validatedRevision = window.DraftRevision;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Force late routed notifications while physical work is held.
                // Their contents are already represented by the action snapshot.
                foreach (TextBox input in new[] { path, rate, count })
                    input.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
            }, DispatcherPriority.Background);
            Require(window.IsWorking && !window.IsCommitting && attempts == 0 && window.DraftRevision == validatedRevision
                && !window.FindControl<TextBlock>("SetupLimitsError")!.IsVisible
                && !window.FindControl<TextBlock>("SetupPathError")!.IsVisible,
                "Deferred notifications cancelled a current validated probe, changed revision or introduced an error.");
            if (changeDuringProbe)
            {
                rate.Text = "4"; // genuine new raw draft must invalidate, even if notification is still queued
                release.Set();
                await Program.WaitUntilAsync(() => !window.IsWorking);
                Require(window.DraftRevision > validatedRevision && attempts == 0 && rate.Text == "4"
                    && path.Text == folder && count.Text == "3"
                    && window.FindControl<TextBlock>("SetupStatus")!.Text == Texts.T("destination.NotChecked")
                    && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original),
                    "Actual draft edit was not invalidated or an obsolete successful probe admitted completion.");
            }
            else
            {
                release.Set();
                await Program.WaitUntilAsync(() => !window.IsWorking);
                if (accept)
                {
                    FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(5));
                    Config saved = Config.LoadConfig(profile);
                    Require(result.Outcome == FirstRunSetupOutcome.Completed && attempts == 1
                        && !saved.FirstRunSetupPending && saved.FirstRunSetupVersion == 1
                        && saved.DownloadPath == folder && saved.BandwidthLimiter == 2097152 && saved.MaxParallelDownloads == 3,
                        "Immediate draft/Accept plus late unchanged events lost or duplicated the exact accepted snapshot.");
                }
                else Require(attempts == 0 && window.FindControl<TextBlock>("SetupStatus")!.Text == Texts.T("destination.Ready")
                    && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original),
                    "Explicit Check failed after duplicate events or falsely completed setup.");
            }
            Console.WriteLine("SMOKE setup raw draft: immediate " + (accept ? "Accept" : "Check")
                + "; held probe + queued unchanged TextChanged; actual change=" + changeDuringProbe + "; snapshot/revision/persistence assertions held.");
        }
        finally
        {
            release.Set();
            await Program.WaitUntilAsync(() => !window.IsWorking);
            if (window.IsVisible) window.Close();
            await showing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (!accept || changeDuringProbe)
        {
            Config reopened = Config.LoadConfig(profile);
            Require(reopened.FirstRunSetupPending && reopened.FirstRunSetupVersion == 0,
                "Check or cancelled obsolete acceptance changed pending into completion.");
        }
    }

    private static async Task CheckRetiredProbe(MainWindow owner, string profile)
    {
        Config config = Seed(profile);
        byte[] original = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new DownloadDestinationValidator(1, (path, _) =>
        {
            entered.TrySetResult(); release.Wait();
            return new(DownloadDestinationStatus.Ready, path);
        });
        int attempts = 0;
        var window = new FirstRunSetupWindow(config, _ => { attempts++; return Task.FromResult(new FirstRunSetupCommitResult(true)); }, probe);
        Task<FirstRunSetupRunResult> showing = window.ShowAsync(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            Click(window.FindControl<Button>("SetupAcceptButton")!);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(window.FindControl<Button>("SetupLaterButton")!);
            FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Outcome == FirstRunSetupOutcome.Deferred && attempts == 0
                && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original),
                "Closing during a probe admitted a late completion or changed pending state.");
        }
        finally { release.Set(); if (window.IsVisible) window.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CheckOwnerCloseDuringCommit(MainWindow owner, string profile)
    {
        Config config = Seed(profile);
        var completion = new TaskCompletionSource<FirstRunSetupCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var parent = new Window { Width = 600, Height = 500, Title = "Setup owner-close fixture" };
        parent.Show(owner);
        var window = new FirstRunSetupWindow(config, values =>
        {
            attempts++;
            // Private atomic fixture save occurs before held activation completes.
            Config candidate = config.Clone();
            candidate.DownloadPath = values.DownloadPath;
            candidate.BandwidthLimiter = values.BandwidthBytesPerSecond;
            candidate.MaxParallelDownloads = values.ParallelDownloads;
            candidate.FirstRunSetupPending = false;
            candidate.FirstRunSetupVersion = 1;
            candidate.SaveConfig(profile);
            return completion.Task;
        }, ReadyProbe());
        Task<FirstRunSetupRunResult> showing = window.ShowAsync(parent);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            Click(window.FindControl<Button>("SetupAcceptButton")!);
            await Program.WaitUntilAsync(() => window.IsCommitting && attempts == 1);
            parent.Close(); // real framework OwnerWindowClosing path, not a direct child Close
            await Program.WaitUntilAsync(() => !parent.IsVisible && !window.IsVisible);
            Require(!showing.IsCompleted, "Owner-close guessed an outcome before the admitted commit completed.");
            string? statusAtClose = window.FindControl<TextBlock>("SetupStatus")!.Text;
            completion.SetResult(new(true, "fixture late activation warning"));
            FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Outcome == FirstRunSetupOutcome.Completed && result.Message == "fixture late activation warning"
                && attempts == 1 && window.FindControl<TextBlock>("SetupStatus")!.Text == statusAtClose,
                "Owner-close lost the confirmed durable outcome, duplicated acceptance, or mutated the retired dialog.");
            Config reopened = Config.LoadConfig(profile);
            Require(!reopened.FirstRunSetupPending && reopened.FirstRunSetupVersion == 1,
                "Owner-close rewrote or lost an already durable completion snapshot.");
            Console.WriteLine("SMOKE setup owner close: child closes without a cycle; admitted commit remains observed; late confirmed activation warning reconciles as completed, no retired UI write.");
        }
        finally
        {
            completion.TrySetResult(new(false));
            await Program.WaitUntilAsync(() => !window.IsCommitting);
            if (window.IsVisible) window.Close();
            if (parent.IsVisible) parent.Close();
            await showing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
