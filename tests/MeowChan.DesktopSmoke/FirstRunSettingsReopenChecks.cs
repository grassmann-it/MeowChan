using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root owns Program dispatch for MEOWCHAN_FIRST_RUN_REOPEN_ONLY. Run only from
// the native application's UI thread, after its real resources are initialized.
// No fake asset loader, constructor bypass, profile redirection or global UI setup.
internal static class FirstRunSettingsReopenChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible, "Reopen checks need a live native owner.");
        string root = TemporaryWorkspacePaths.CreateDirectory("first-run-settings-reopen-");
        try
        {
            await JourneyAsync(owner, Path.Combine(root, "above-cap"), 48, 12, 12, 36);
            await JourneyAsync(owner, Path.Combine(root, "below-cap"), 7, 64, 7, 0);
            Console.WriteLine("SMOKE first-run Settings reopen: both saved-cap cases; real editor constructors/path controls, abandoned unsaved draft, fresh disk snapshot/editor, byte-preserving reads and joined empty-provider host restart checked. Managed UI only.");
        }
        finally
        {
            // Retain synthetic profiles as evidence; no fixture cleanup in this helper.
            Dispatcher.UIThread.VerifyAccess();
            owner.Activate();
        }
    }

    private static async Task JourneyAsync(MainWindow owner, string root,
        int requested, int cap, int effective, int deferredSlots)
    {
        string chosen = Path.Combine(root, "actual media ä", "nested destination");
        Directory.CreateDirectory(chosen);
        string profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(profile);
        new Config
        {
            FirstRunSetupPending = true,
            DownloadPath = Path.Combine(root, "before setup"),
            ParallelDownloadsSafetyLimit = cap,
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = false,
            MinimumFreeSpaceGiB = 0,
            MinimizeToTray = false,
            CloseToTray = false
        }.SaveConfig(profile);

        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await using (host)
        {
            await host.StartAsync().WaitAsync(Timeout);
            Require(host.State == DesktopSessionState.Ready, "Synthetic host failed to become Ready.");
            FirstRunSetupCommitResult result = await host.CompleteFirstRunSetupAsync(
                new(chosen, 2_621_440, requested)).WaitAsync(Timeout);
            Require(result.Persisted && result.ActivationWarning == null,
                "Empty-provider first-run commit was not durably confirmed without warning.");
        }
        Require(host.IsProfileReleased, "First host lifetime was not joined before disk reopen.");

        string settingsFile = Path.Combine(profile, "settings.json");
        byte[] committedBytes = File.ReadAllBytes(settingsFile);
        Config saved = Config.LoadConfig(profile);
        RequireSaved(saved, chosen, requested, cap);
        Dispatcher.UIThread.VerifyAccess();
        SettingsEditor abandoned = new(saved);
        Window first = EditorWindow(abandoned);
        try
        {
            first.Show(owner);
            await CheckHydratedAsync(first, abandoned, chosen, requested, cap);
            string draftPath = Path.Combine(root, "UNSAVED draft");
            abandoned.DownloadPath = draftPath;
            abandoned.MaxParallelDownloads = 99;
            abandoned.ParallelDownloadsSafetyLimit = 1;
            abandoned.BandwidthLimiterMB = 9m;
            await Dispatcher.UIThread.InvokeAsync(first.UpdateLayout, DispatcherPriority.Background);
            Require(abandoned.DownloadPath == draftPath && abandoned.MaxParallelDownloads == 99
                && abandoned.ParallelDownloadsSafetyLimit == 1 && abandoned.BandwidthLimiterMB == 9m
                && abandoned.FindControl<TextBox>("DownloadPathInput")!.Text == draftPath,
                "The first editor did not actually hold the unsaved draft.");
            RequireSaved(saved, chosen, requested, cap);
            Require(File.ReadAllBytes(settingsFile).SequenceEqual(committedBytes),
                "An unsaved editor draft rewrote durable settings.");
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            abandoned.EndLifetime();
            first.Close();
        }
        Require(!first.IsVisible, "Abandoned editor window did not close before reopen.");

        Config reopened = Config.LoadConfig(profile);
        Require(!ReferenceEquals(saved, reopened), "Reopen reused a config object instead of reloading disk.");
        RequireSaved(reopened, chosen, requested, cap);
        // No test-side property hydration or RefreshConfirmedFirstRunSetup call:
        // the real constructor must read the persisted values itself.
        SettingsEditor editor = new(reopened);
        Window second = EditorWindow(editor);
        try
        {
            Require(!ReferenceEquals(abandoned, editor), "Reopen reused the abandoned editor.");
            second.Show(owner);
            await CheckHydratedAsync(second, editor, chosen, requested, cap);
            Require(File.ReadAllBytes(settingsFile).SequenceEqual(committedBytes),
                "Opening a fresh editor changed committed settings bytes.");
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            editor.EndLifetime();
            second.Close();
        }
        Require(!second.IsVisible, "Reopened editor window did not close.");

        DesktopSessionHost restarted = new(profile, Array.Empty<IImageBoard>());
        await using (restarted)
        {
            await restarted.StartAsync().WaitAsync(Timeout);
            Require(restarted.State == DesktopSessionState.Ready, "Restarted synthetic host failed to become Ready.");
            await restarted.CaptureProjectionAsync().WaitAsync(Timeout);
            RequireSaved(restarted.Config, chosen, requested, cap);
            Require(restarted.Projection.MediaConcurrency == (requested, effective, 0, deferredSlots),
                "Restarted runtime did not honor the persisted requested/custom-cap pair.");
            Require(!FirstRunSetupCoordinator.CanOffer(restarted.Config, restarted.State),
                "Completed first-run setup was offered after restart.");
            Require(File.ReadAllBytes(settingsFile).SequenceEqual(committedBytes),
                "Restart changed the committed path/resource settings bytes.");
        }
        Require(restarted.IsProfileReleased, "Restarted host lifetime was not joined.");
        Console.WriteLine($"SMOKE first-run reopen case: requested={requested}; cap={cap}; effective={effective}; deferred={deferredSlots}; profile={profile}.");
    }

    private static Window EditorWindow(SettingsEditor editor) => new()
    {
        Title = "First-run Settings reopen boundary",
        Width = 1100,
        Height = 800,
        Content = editor
    };

    private static async Task CheckHydratedAsync(Window window, SettingsEditor editor,
        string path, int requested, int cap)
    {
        Dispatcher.UIThread.VerifyAccess();
        await Program.WaitUntilAsync(() => window.IsVisible && editor.IsEffectivelyVisible);
        await SettingsSectionChecks.OpenAsync(editor, "settings.downloads");
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(ReferenceEquals(editor, editor.DataContext), "Editor lost its own binding model.");
        Require(editor.DownloadPath == path && editor.MaxParallelDownloads == requested
            && editor.ParallelDownloadsSafetyLimit == cap && editor.BandwidthLimiterMB == 2.5m,
            "New Settings editor hydrated stale/default/draft path or resource values.");
        TextBox pathControl = editor.FindControl<TextBox>("DownloadPathInput")!;
        Require(pathControl.IsEffectivelyVisible && pathControl.Text == path,
            "Actual visible path TextBox did not retain the persisted real path.");
        Require(editor.FindControl<NumericUpDown>("ParallelDownloadsInput")!.Value == requested
            && editor.FindControl<NumericUpDown>("ParallelDownloadsSafetyInput")!.Value == cap
            && editor.FindControl<NumericUpDown>("BandwidthInput")!.Value == 2.5m,
            "Actual numeric controls disagree with the saved request, cap or bandwidth.");
    }

    private static void RequireSaved(Config config, string path, int requested, int cap) => Require(
        config.LoadWarning == null && !config.FirstRunSetupPending && config.FirstRunSetupVersion == 1
        && config.DownloadPath == path && config.MaxParallelDownloads == requested
        && config.ParallelDownloadsSafetyLimit == cap && config.BandwidthLimiter == 2_621_440
        && !config.AutomaticUpdateChecks && !config.SaveUrlsOnExit && config.MinimumFreeSpaceGiB == 0,
        "Durable first-run values/provenance or unrelated profile preferences were lost.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
