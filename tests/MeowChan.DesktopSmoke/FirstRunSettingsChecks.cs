using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Real shell/host/modal journey. Managed routed input on native windows, not OS input.
// Program hook (after its owner is Ready): await FirstRunSettingsChecks.RunAsync(owner).
internal static class FirstRunSettingsChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string root = TemporaryWorkspacePaths.CreateDirectory("first-run-settings-");
        try
        {
            // Run each locale in a separate process via LANG, so dispatcher,
            // binding and async continuations all share the real startup culture.
            // Flipping only this async flow's CurrentCulture mid-run fabricates
            // mismatched cultures across otherwise unchanged UI callbacks.
            string locale = CultureInfo.CurrentCulture.Name;
            if (Environment.GetEnvironmentVariable("MEOWCHAN_EXPECTED_TEST_CULTURE") is { } expectedLocale)
                Require(locale == expectedLocale, $"Process locale was {locale}, expected {expectedLocale}.");
            Texts.SetLanguage(locale.StartsWith("de", StringComparison.Ordinal) ? "de" : "en");
            Console.WriteLine("SMOKE first-run process culture: " + locale);
            await JourneyAsync(owner, Path.Combine(root, "accept"), "accept");
            await JourneyAsync(owner, Path.Combine(root, "later"), "later");
            await JourneyAsync(owner, Path.Combine(root, "close"), "close");
            Console.WriteLine("SMOKE first-run Settings: real automatic new-profile modal, rejected zero, Unicode/2.5 MiB/s/3 commit, retained editor model and numeric text/value, unrelated save and fresh host reopen; Later/X preserve pending. Managed input only.");
        }
        finally
        {
            // Retain disposable evidence; no fixture-directory cleanup in this sidecar.
            Texts.SetLanguage(language);
            owner.Activate();
        }
    }

    private static async Task JourneyAsync(MainWindow owner, string profile, string action)
    {
        Directory.CreateDirectory(profile);
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        bool? savedUnrelated = null;
        try
        {
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            Require(host.Config.Origin == ProfileOrigin.ConfirmedNew, "Fixture must start from a genuinely empty new profile.");
            // No environment/profile redirection: only this explicit private host is used.
            // No providers, watches, media, update checks, tray or audio in these journeys.
            host.Config.DownloadPath = Path.Combine(profile, "initial downloads");
            host.Config.AutomaticUpdateChecks = false;
            host.Config.MinimumFreeSpaceGiB = 0;
            host.Config.CloseToTray = false;
            host.Config.MinimizeToTray = false;
            host.Config.WorkspaceMode = "Advanced";
            Directory.CreateDirectory(host.Config.DownloadPath);
            shell = new MainWindow(host);
            SettingsEditor eager = shell.SettingsEditor;
            bool unrelatedDraft = !eager.ShowWarningNotifications;
            if (action == "accept")
            {
                eager.ShowWarningNotifications = unrelatedDraft;
                eager.ParallelDownloadsSafetyLimit = 64; // setup does not own this unrelated draft
            }
            shell.Show(owner);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Program.WaitUntilAsync(() => shell.OwnedWindows.OfType<FirstRunSetupWindow>().Any(w => w.IsVisible));
            FirstRunSetupWindow dialog = shell.OwnedWindows.OfType<FirstRunSetupWindow>().Single(w => w.IsVisible);
            // ShowDialog disables the native owner, not Avalonia IsEffectivelyEnabled.
            Require(dialog.IsVisible && ReferenceEquals(dialog.Owner, shell)
                && shell.OwnedWindows.Contains(dialog), "Automatic setup must be a visible shell-owned dialog.");
            Config pending = Config.LoadConfig(profile);
            Require(pending.FirstRunSetupPending && pending.FirstRunSetupVersion == 0, "Automatic setup did not durably mark pending.");
            string folder = Path.Combine(profile, "chosen ü 日本");
            Directory.CreateDirectory(folder);
            dialog.FindControl<TextBox>("SetupFolderInput")!.Text = folder;
            dialog.FindControl<TextBox>("SetupBandwidthInput")!.Text = 2.5m.ToString(CultureInfo.CurrentCulture);
            dialog.FindControl<TextBox>("SetupParallelInput")!.Text = "3";
            if (action == "accept")
            {
                // Real dialog validation failure, not a substituted coordinator/commit delegate.
                dialog.FindControl<TextBox>("SetupBandwidthInput")!.Text = "0";
                Click(dialog.FindControl<Button>("SetupAcceptButton")!);
                await Program.WaitUntilAsync(() => dialog.FindControl<TextBlock>("SetupLimitsError")!.IsVisible);
                Require(dialog.IsVisible && host.Config.FirstRunSetupVersion == 0
                    && Config.LoadConfig(profile).FirstRunSetupPending, "Rejected setup completed or closed.");
                dialog.FindControl<TextBox>("SetupBandwidthInput")!.Text = 2.5m.ToString(CultureInfo.CurrentCulture);
                Click(dialog.FindControl<Button>("SetupAcceptButton")!);
                await Program.WaitUntilAsync(() => !dialog.IsWorking && host.Config.FirstRunSetupVersion == 1);
                // Activation warnings may keep the completed dialog open with an explicit Close.
                if (dialog.IsVisible) Click(dialog.FindControl<Button>("SetupLaterButton")!);
            }
            else if (action == "later") Click(dialog.FindControl<Button>("SetupLaterButton")!);
            else dialog.Close(); // framework window-X lifetime, not native pointer input
            await Program.WaitUntilAsync(() => !dialog.IsVisible);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ReferenceEquals(eager, shell.SettingsEditor), "Setup replaced the eager Settings editor instead of refreshing it.");
            Config expected = action == "accept" ? Config.LoadConfig(profile) : pending;
            if (action == "accept")
            {
                RequireValues(expected, folder);
                Require(eager.ShowWarningNotifications == unrelatedDraft,
                    "Setup refresh discarded an unrelated pre-existing settings draft.");
                Require(eager.ParallelDownloadsSafetyLimit == 64 && eager.FindControl<NumericUpDown>("ParallelDownloadsSafetyInput")!.Value == 64
                    && host.Config.ParallelDownloadsSafetyLimit == 32,
                    "Setup reset a foreign cap draft or applied it without a Settings save.");
            }
            else Require(host.Config.FirstRunSetupPending && host.Config.FirstRunSetupVersion == 0,
                "Later/X falsely confirmed setup.");
            await CheckEditorAsync(shell, expected);
            if (action == "accept")
            {
                await SettingsSectionChecks.OpenAsync(eager, "settings.behavior");
                CheckBox unrelated = eager.FindControl<CheckBox>("BoardAdmissionWizardInput")!;
                bool changed = !(unrelated.IsChecked ?? false);
                unrelated.IsChecked = changed;
                savedUnrelated = changed;
                Click(eager.FindControl<Button>("SaveSettingsButton")!);
                await Program.WaitUntilAsync(() => !eager.IsBusy && eager.IsAvailable
                    && Config.LoadConfig(profile).ShowBoardAdmissionWizard == changed);
                Require(!eager.HasError, "Unrelated real Settings save failed.");
                RequireValues(host.Config, folder);
                RequireValues(Config.LoadConfig(profile), folder);
                Require(host.Config.ParallelDownloadsSafetyLimit == 64 && Config.LoadConfig(profile).ParallelDownloadsSafetyLimit == 64,
                    "Later unrelated Settings save lost the cap draft that setup preserved.");
                await CheckEditorAsync(shell, expected);
            }
            await CloseAsync(shell);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Require(!shell.IsVisible && host.IsProfileReleased, "Shell/host did not release the private profile.");
            shell = null;
            host = null;
            if (action != "accept") return;
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            RequireValues(host.Config, folder);
            Require(host.Config.ShowBoardAdmissionWizard == savedUnrelated, "Fresh host lost unrelated saved preference.");
            Require(!shell.OwnedWindows.OfType<FirstRunSetupWindow>().Any(), "Completed profile offered setup again.");
            await CheckEditorAsync(shell, host.Config);
        }
        finally
        {
            if (shell != null)
            {
                foreach (FirstRunSetupWindow modal in shell.OwnedWindows.OfType<FirstRunSetupWindow>().ToArray())
                {
                    await Program.WaitUntilAsync(() => !modal.IsWorking);
                    modal.Close();
                }
                await CloseAsync(shell);
            }
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task CheckEditorAsync(MainWindow shell, Config expected)
    {
        Click(shell.FindControl<Button>("SettingsNavigationButton")!);
        SettingsEditor editor = shell.SettingsEditor;
        await Program.WaitUntilAsync(() => shell.SettingsPageVisible && editor.IsEffectivelyVisible);
        await SettingsSectionChecks.OpenAsync(editor, "settings.downloads");
        await Dispatcher.UIThread.InvokeAsync(shell.UpdateLayout, DispatcherPriority.Background);
        decimal rate = expected.BandwidthLimiter / (1024m * 1024m);
        Require(editor.DownloadPath == expected.DownloadPath
            && editor.FindControl<TextBox>("DownloadPathInput")!.Text == expected.DownloadPath
            && editor.BandwidthLimiterMB == rate && editor.MaxParallelDownloads == expected.MaxParallelDownloads,
            $"Settings model/path TextBox retained stale pre-setup values: path={editor.DownloadPath}; "
            + $"text={editor.FindControl<TextBox>("DownloadPathInput")!.Text}; rate={editor.BandwidthLimiterMB}/{rate}; "
            + $"parallel={editor.MaxParallelDownloads}/{expected.MaxParallelDownloads}; culture={CultureInfo.CurrentCulture.Name}.");
        CheckNumber(editor.FindControl<NumericUpDown>("BandwidthInput")!, rate);
        CheckNumber(editor.FindControl<NumericUpDown>("ParallelDownloadsInput")!, expected.MaxParallelDownloads);
        Console.WriteLine($"SMOKE first-run retained Settings confirmed: culture={CultureInfo.CurrentCulture.Name}; path={editor.DownloadPath}; rate={editor.BandwidthLimiterMB}; parallel={editor.MaxParallelDownloads}.");
        Program.CaptureWindow(shell, "smoke-first-run-settings-" + (++_captureIndex) + ".png");
    }

    private static int _captureIndex;

    private static void CheckNumber(NumericUpDown input, decimal expected)
    {
        input.ApplyTemplate();
        TextBox text = input.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
        Require(input.IsEffectivelyVisible && input.Value == expected
            && decimal.TryParse(input.Text, input.ParsingNumberStyle, input.NumberFormat, out decimal outer) && outer == expected
            && decimal.TryParse(text.Text, input.ParsingNumberStyle, input.NumberFormat, out decimal inner) && inner == expected,
            $"{input.Name}: numeric Value, Text and rendered PART_TextBox disagree with confirmed setup ({expected}); "
            + $"Value={input.Value}; Text={input.Text}; PART={text.Text}; visible={input.IsEffectivelyVisible}; "
            + $"controlDecimal={input.NumberFormat?.NumberDecimalSeparator ?? CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}; culture={CultureInfo.CurrentCulture.Name}.");
    }

    private static void RequireValues(Config config, string folder) => Require(config.DownloadPath == folder
        && config.BandwidthLimiter == 2621440 && config.MaxParallelDownloads == 3
        && !config.FirstRunSetupPending && config.FirstRunSetupVersion == 1,
        "Confirmed setup values/provenance were lost or reverted.");

    private static async Task CloseAsync(MainWindow shell)
    {
        // Use the shell's real asynchronous shutdown/drain, not Window.Close bypass.
        var method = typeof(MainWindow).GetMethod("CloseApplicationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(shell, null)!).WaitAsync(TimeSpan.FromSeconds(15));
        await Program.WaitUntilAsync(() => !shell.IsVisible);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
