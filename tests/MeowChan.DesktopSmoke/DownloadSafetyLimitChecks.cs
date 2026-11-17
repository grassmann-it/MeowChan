using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Desktop.Localization;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Native windows, managed routed input: real Settings validation/save/discard and host restart.
// No providers, watches or downloads; actual dispatch/drain are covered by queue tests.
internal static class DownloadSafetyLimitChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("download-safety-settings-");
        string media = Path.Combine(profile, "media");
        new Config { DownloadPath = media, MaxParallelDownloads = 90, ParallelDownloadsSafetyLimit = 32,
            AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Advanced" }.SaveConfig(profile);
        Directory.CreateDirectory(media);
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        try
        {
            await using DesktopSessionHost ownedHost = new(profile, Array.Empty<IImageBoard>());
            host = ownedHost;
            await using MainWindow ownedShell = new(host);
            shell = ownedShell;
            shell.Show(owner);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Click(shell.FindControl<Button>("SettingsNavigationButton")!);
            SettingsEditor editor = shell.SettingsEditor;
            await SettingsSectionChecks.OpenAsync(editor, "settings.downloads");
            NumericUpDown cap = editor.FindControl<NumericUpDown>("ParallelDownloadsSafetyInput")!;
            Require(cap.IsEffectivelyVisible && cap.Value == 32 && editor.ParallelDownloadsSafetyLimit == 32,
                "Safety limit is not visible beside requested parallelism/default32.");
            TextBlock hint = editor.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == editor.ParallelSafetyHint);
            Require(hint.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(hint.Text), "Safety explanation is hidden.");
            // Draft cannot mutate the live queue. Real discard must reset both Value and raw Text.
            cap.Value = 64;
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(host.Config.ParallelDownloadsSafetyLimit == 32 && host.Projection.MediaConcurrency.Effective == 32,
                "Unsaved cap affected live configuration/queue.");
            editor.CancelChanges();
            Require(cap.Value == 32 && cap.Text == "32" && editor.ParallelDownloadsSafetyLimit == 32,
                "Discard retained unsaved cap.");
            cap.Text = "257";
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy);
            Require(editor.FindControl<TextBlock>("ParallelDownloadsSafetyInputError")!.IsVisible
                && host.Config.ParallelDownloadsSafetyLimit == 32 && Config.LoadConfig(profile).ParallelDownloadsSafetyLimit == 32,
                "Out-of-range raw cap was accepted or silently clamped.");
            cap.Value = 64;
            cap.Text = "64";
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy && host.Config.ParallelDownloadsSafetyLimit == 64);
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(!editor.HasError && host.Projection.MediaConcurrency == (90, 64, 0, 26)
                && Config.LoadConfig(profile).ParallelDownloadsSafetyLimit == 64,
                "Real settings save did not persist/apply requested90/cap64.");
            Require(cap.Value == 64 && cap.Text == "64", "Post-save reset lost the cap.");
            TextBlock counts = shell.FindControl<TextBlock>("WorkspaceCounts")!;
            string details = Texts.T("downloads.protectionDetails", 90, 64);
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(counts)?.Contains(details, StringComparison.Ordinal) == true);
            Require((ToolTip.GetTip(counts)?.ToString() ?? "").Contains(details, StringComparison.Ordinal)
                && counts.Text!.Contains(Texts.T("downloads.protectionShort", 64), StringComparison.Ordinal),
                "Footer does not explain effective media transfers separately from watched threads.");
            Program.CaptureWindow(shell, "smoke-download-safety-limit.png");
            await CloseAsync(shell);
            await shell.DisposeAsync();
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            shell = null; host = null;
            await using DesktopSessionHost ownedHostSecond = new(profile, Array.Empty<IImageBoard>());
            host = ownedHostSecond;
            await using MainWindow ownedShellSecond = new(host);
            shell = ownedShellSecond;
            shell.Show(owner);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(host.Config.ParallelDownloadsSafetyLimit == 64 && host.Projection.MediaConcurrency == (90, 64, 0, 26),
                "Fresh host did not restore cap64 before downloads.");
            Click(shell.FindControl<Button>("SettingsNavigationButton")!);
            await SettingsSectionChecks.OpenAsync(shell.SettingsEditor, "settings.downloads");
            Require(shell.SettingsEditor.ParallelDownloadsSafetyLimit == 64
                && shell.SettingsEditor.FindControl<NumericUpDown>("ParallelDownloadsSafetyInput")!.Value == 64,
                "Fresh settings editor did not restore cap.");
            Console.WriteLine("SMOKE download safety: visible1..256/default32 + hint; unsaved draft/discard; rejected257; real save90/64 live+disk; fresh host+UI restored64. Managed routed input, no real HTTP transfers.");
        }
        finally
        {
            if (shell != null) await CloseAsync(shell);
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            owner.Activate();
        }
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(TimeSpan.FromSeconds(15));
        await Program.WaitUntilAsync(() => !shell.IsVisible);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
