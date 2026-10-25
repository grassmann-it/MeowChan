using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Narrow continuation of SourceIntegrityChecks: real URL controls and delivered
/// Host/worker with the existing terminal HTTP fixture, plus explicitly synthetic
/// partial-save presentation. No live provider or physical keyboard proof.
/// </summary>
internal static class ExperimentalActivationChecks
{
    private const string ThreadUrl = ApphostIntegrityProcessSmoke.ThreadUrl;
    private const string Owner = ApphostIntegrityProcessSmoke.Owner;
    private const string Source = ApphostIntegrityProcessSmoke.Source;

    internal static async Task RunAsync(MainWindow parent)
    {
        double expectedScale = double.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_INTEGRITY_EXPECT_SCALE")
            ?? throw new InvalidOperationException("An explicit actual scale is required."), CultureInfo.InvariantCulture);
        Require(new[] { 1d, 1.5d, 2d }.Contains(expectedScale) && Math.Abs(parent.RenderScaling - expectedScale) < .025,
            "Experimental activation requested/actual render scale differs.");
        string scale = expectedScale.ToString("0.0##", CultureInfo.InvariantCulture).Replace('.', '_');
        string profile = Path.Combine(parent.SessionHost.ProfileDirectory, "experimental-activation-ui");
        Config original = parent.SessionHost.Config.Clone();
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
            SaveUrlsOnExit = true, AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0,
            WorkspaceMode = "Barebones", Language = "en", Theme = "Dark" }.SaveConfig(profile);
        using var http = new ApphostIntegrityProcessSmoke.FixtureHttp();
        await using DesktopSessionHost host = new(profile, providers: null,
            updateSessionFactory: (path, settings) => new UpdateSession(path, settings, http.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: http.Client);
        MainWindow window = new(host);
        window.Show(parent);
        TaskCompletionSource<ExperimentalSourceActivationResult>? pending = null;
        string settings = Path.Combine(profile, "settings.json"), retainedSettings = settings + ".activation-retained";

        async Task CheckStatusPresentationAsync(ExperimentalSourceActivationTarget target, SourceMediaIntegrityPolicyView strict)
        {
            // This section only supplies result DTOs to the actual dialog. It does
            // not claim a real persistence failure; EA02 tests own those failures.
            var actual = new ExperimentalSourceActivationActualState(true, true, false, false,
                target.ProfileRevision, target.LifecycleRevision, strict.Snapshot.Revision);
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Texts.SetLanguage(language); Application.Current.RequestedThemeVariant = theme;
                string cell = language + "-" + theme.Key!.ToString()!.ToLowerInvariant();
                int calls = 0;
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                ExperimentalActivationDialog modal = new(target, _ =>
                {
                    calls++;
                    return calls == 1 ? Task.FromResult(new ExperimentalSourceActivationResult(
                        ExperimentalSourceActivationStatus.GrantFailed, actual, target, "Controlled fixture grant persistence failure")) : pending!.Task;
                });
                Task<ExperimentalActivationDecision?> result = modal.ShowDialog<ExperimentalActivationDecision?>(window);
                modal.Width = modal.MinWidth; modal.Height = modal.MinHeight;
                TextBlock warningStatus = C<TextBlock>(modal, "ActivationStatus");
                void ObserveMinimumLayout(string stage)
                {
                    ScrollViewer? scroll = warningStatus.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                    var presenter = warningStatus.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
                    Console.WriteLine("EXPERIMENTAL_WARNING_MINIMUM_LAYOUT " + JsonSerializer.Serialize(new
                    {
                        stage, cell, requestedWidth = modal.MinWidth, requestedHeight = modal.MinHeight,
                        actualClient = modal.ClientSize.ToString(), modalBounds = modal.Bounds.ToString(),
                        modal.IsMeasureValid, modal.IsArrangeValid, actualScale = modal.RenderScaling,
                        scrollBounds = scroll?.Bounds.ToString(), viewport = scroll?.Viewport.ToString(),
                        extent = scroll?.Extent.ToString(), offset = scroll?.Offset.ToString(),
                        scrollMeasureValid = scroll?.IsMeasureValid, scrollArrangeValid = scroll?.IsArrangeValid,
                        presenterBounds = presenter?.Bounds.ToString(), presenterMeasureValid = presenter?.IsMeasureValid,
                        presenterArrangeValid = presenter?.IsArrangeValid
                    }));
                }
                ObserveMinimumLayout("before-native-minimum-readiness");
                try
                {
                    // Width/Height setters are requests. A real minimum-size warning
                    // and its Click must wait for native client size and matching layout.
                    await Program.WaitUntilAsync(() =>
                    {
                        var warningScroll = warningStatus.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                        var warningPresenter = warningStatus.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
                        return modal.IsVisible && C<Button>(modal, "ActivationConfirm").IsEnabled
                        && warningScroll != null && warningPresenter != null
                        && Math.Abs(modal.ClientSize.Width - modal.MinWidth) <= 1
                        && Math.Abs(modal.ClientSize.Height - modal.MinHeight) <= 1
                        && modal.IsMeasureValid && modal.IsArrangeValid
                        && warningScroll.IsMeasureValid && warningScroll.IsArrangeValid
                        && warningPresenter.IsMeasureValid && warningPresenter.IsArrangeValid
                        && warningScroll.Bounds.Width > 0 && warningScroll.Bounds.Height > 0
                        && warningScroll.Bounds.Width <= modal.ClientSize.Width + 1
                        && warningScroll.Bounds.Height <= modal.ClientSize.Height + 1
                        && warningScroll.Viewport.Width > 0 && warningScroll.Viewport.Height > 0
                        && warningScroll.Viewport.Width <= warningScroll.Bounds.Width + 1
                        && warningScroll.Viewport.Height <= warningScroll.Bounds.Height + 1
                        && Math.Abs(warningPresenter.Bounds.Width - warningScroll.Viewport.Width) <= 1
                        && Math.Abs(warningPresenter.Bounds.Height - warningScroll.Viewport.Height) <= 1;
                    });
                }
                finally { ObserveMinimumLayout("after-native-minimum-readiness"); }
                Require(Math.Abs(modal.RenderScaling - expectedScale) < .025, "Modal actual scale changed.");
                foreach (string name in new[] { "ActivationHeading", "ActivationExplanation", "ActivationScope", "ActivationConfirmLabel" })
                    AssertTextFits(C<TextBlock>(modal, name));
                Require(InWindow(modal, C<Button>(modal, "ActivationConfirm")) && InWindow(modal, C<Button>(modal, "ActivationCancel")),
                    "Minimum warning dialog clipped either action.");
                Program.CaptureWindow(modal, $"smoke-experimental-{scale}-{cell}-warning.png");
                Click(C<Button>(modal, "ActivationConfirm"));
                await Program.WaitUntilAsync(() => C<TextBlock>(modal, "ActivationStatus").IsEffectivelyVisible
                    && C<Button>(modal, "ActivationConfirm").IsEnabled);
                TextBlock partialStatus = C<TextBlock>(modal, "ActivationStatus");
                ScrollViewer statusViewport = partialStatus.GetVisualAncestors().OfType<ScrollViewer>().First();
                void ObservePartialLayout(string stage) => Console.WriteLine("EXPERIMENTAL_PARTIAL_LAYOUT " + JsonSerializer.Serialize(new
                {
                    stage, cell, statusText = partialStatus.Text, statusBounds = partialStatus.Bounds.ToString(),
                    statusDesiredSize = partialStatus.DesiredSize.ToString(), partialStatus.IsEffectivelyVisible,
                    partialStatus.IsMeasureValid, partialStatus.IsArrangeValid,
                    textLayoutWidth = partialStatus.TextLayout.Width, textLayoutHeight = partialStatus.TextLayout.Height,
                    viewportBounds = statusViewport.Bounds.ToString(), viewport = statusViewport.Viewport.ToString(),
                    extent = statusViewport.Extent.ToString(), offset = statusViewport.Offset.ToString(),
                    modalBounds = modal.Bounds.ToString(), confirmBounds = C<Button>(modal, "ActivationConfirm").Bounds.ToString(),
                    actualScale = modal.RenderScaling
                }));
                // Result DTO delivery can finish synchronously in the routed Click.
                // Visibility is not proof that the formerly hidden panel has had
                // its first natural measure/arrange. Keep the existing 5s oracle.
                ObservePartialLayout("before-natural-layout");
                try
                {
                    await Program.WaitUntilAsync(() => partialStatus.IsMeasureValid && partialStatus.IsArrangeValid
                        && partialStatus.Bounds.Width > 0 && partialStatus.Bounds.Height > 0);
                }
                finally
                {
                    ObservePartialLayout("after-natural-layout-barrier");
                    // The shared capture calls UpdateLayout; perform it only after
                    // the passive readiness oracle (or after its hard timeout).
                    Program.CaptureWindow(modal, $"smoke-experimental-{scale}-{cell}-partial-before-fit-guard.png");
                }
                AssertTextFits(C<TextBlock>(modal, "ActivationStatus"));
                AssertTextFits(C<TextBlock>(modal, "ActivationConfirmLabel"));
                var statusPresenter = partialStatus.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().First();
                Rect StatusRect()
                {
                    Matrix? transform = partialStatus.TransformToVisual(statusPresenter);
                    return transform is { } matrix ? new Rect(partialStatus.Bounds.Size).TransformToAABB(matrix) : default;
                }
                bool FullStatusVisible()
                {
                    Rect rect = StatusRect();
                    return modal.IsVisible && statusPresenter.IsEffectivelyVisible && partialStatus.IsEffectivelyVisible
                        && InWindow(modal, statusPresenter) && statusPresenter.IsMeasureValid && statusPresenter.IsArrangeValid
                        && partialStatus.IsMeasureValid && partialStatus.IsArrangeValid
                        && rect.Width > 0 && rect.Height > 0 && rect.X >= -1 && rect.Y >= -1
                        && rect.Right <= statusPresenter.Bounds.Width + 1 && rect.Bottom <= statusPresenter.Bounds.Height + 1;
                }
                try { await Program.WaitUntilAsync(FullStatusVisible); }
                finally
                {
                    Rect rect = StatusRect();
                    Rect visible = rect.Intersect(new Rect(statusPresenter.Bounds.Size));
                    Console.WriteLine("EXPERIMENTAL_PARTIAL_STATUS_VIEWPORT " + JsonSerializer.Serialize(new
                    { cell, statusRect = rect.ToString(), visibleIntersection = visible.ToString(),
                        presenterBounds = statusPresenter.Bounds.ToString(), offset = statusViewport.Offset.ToString(),
                        fullStatusVisible = FullStatusVisible(), statusText = partialStatus.Text }));
                    Program.CaptureWindow(modal, $"smoke-experimental-{scale}-{cell}-partial-status-viewport.png");
                }
                Require(FullStatusVisible() && partialStatus.Text?.Contains("Controlled fixture grant persistence failure", StringComparison.Ordinal) == true
                    && partialStatus.Text.Contains(Texts.T("activation.actualState", Texts.T("activation.yes"), Texts.T("activation.yes"),
                        Texts.T("activation.no"), Texts.T("activation.no")), StringComparison.Ordinal),
                    "The minimum partial-save dialog did not show the complete failure and actual state in its real scroll viewport.");
                Require(calls == 1 && !result.IsCompleted, "Partial result closed the modal or retried without a new action.");
                Require(InWindow(modal, C<Button>(modal, "ActivationConfirm")) && InWindow(modal, C<Button>(modal, "ActivationCancel")),
                    "Partial-save presentation clipped the fixed retry/close actions.");
                Program.CaptureWindow(modal, $"smoke-experimental-{scale}-{cell}-partial-error.png");
                Click(C<Button>(modal, "ActivationConfirm"));
                await Program.WaitUntilAsync(() => calls == 2 && !C<Button>(modal, "ActivationConfirm").IsEnabled);
                modal.Close();
                modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(modal.IsVisible && !result.IsCompleted, "Close/Escape pretended to roll back an in-flight save.");
                pending.SetResult(new(ExperimentalSourceActivationStatus.Succeeded, actual with { IsGranted = true, IsEffective = true }, null, null));
                pending = null;
                ExperimentalActivationDecision? decision = await result;
                Require(decision?.ContinueAdmission == true && calls == 2, "Explicit retry did not finish the dialog exactly once.");
                Console.WriteLine("EXPERIMENTAL_ACTIVATION_OBSERVATION " + JsonSerializer.Serialize(new
                { cell, actualScale = modal.RenderScaling, warningMinimum = modal.Bounds.ToString(), syntheticPartialSave = true, explicitRetryCalls = calls }));
            }
        }
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready && C<Button>(window, "AddUrlButton").IsEnabled);
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            var initial = await host.ProjectExperimentalSourceActivationAsync(ThreadUrl);
            Require(initial.Status == ExperimentalSourceActivationProjectionStatus.ConfirmationRequired && initial.Target != null,
                "Real delivered disabled source did not project a confirmation target.");
            var target = initial.Target!;
            Require(http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0, "Passive local routing performed work.");

            // Explicit presentation-only opt-in. The normal path still executes
            // every real admission edge before this exact shared matrix.
            if (Environment.GetEnvironmentVariable("MEOWCHAN_EXPERIMENTAL_ACTIVATION_STATUS_ONLY") == "1")
            {
                var statusPolicyBefore = Policy(host);
                Require(!statusPolicyBefore.IsActive && !statusPolicyBefore.IsGranted,
                    "Status-only presentation requires the real initial disabled, strict source.");
                await CheckStatusPresentationAsync(target, statusPolicyBefore);
                Require(Policy(host) == statusPolicyBefore && http.Requests.IsEmpty && http.Unexpected == 0
                    && host.GetThreadWorkSnapshots().Count == 0 && host.Plugins!.Runtime.GetSnapshots().Count == 0,
                    "Status-only result DTO presentation changed real consent, activation or work.");
                Console.WriteLine("EXPERIMENTAL_ACTIVATION_STATUS_ONLY PASS: complete unchanged EN/DE Dark/Light minimum warning, partial-save viewport, explicit retry and busy close/Escape matrix at actual scale " + expectedScale
                    + ". Presentation-only synthetic DTOs; real URL/source focus and admission edges were not run.");
                return;
            }

            TextBox input = C<TextBox>(window, "UrlInput");
            Button add = C<Button>(window, "AddUrlButton");
            foreach (bool escape in new[] { false, true })
            {
                string canceledUrl = escape ? "https://wizchan.org/demo/index.html" : ThreadUrl;
                List<string> cancelTransitions = new();
                void ObserveOwnerActivated(object? sender, EventArgs args) => cancelTransitions.Add("owner-activated");
                void ObserveOwnerDeactivated(object? sender, EventArgs args) => cancelTransitions.Add("owner-deactivated");
                window.Activated += ObserveOwnerActivated;
                window.Deactivated += ObserveOwnerDeactivated;
                input.Text = canceledUrl; input.Focus(); Click(add);
                ExperimentalActivationDialog modal = await WaitDialogAsync();
                Require(http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0
                    && !Policy(host).IsActive && !Policy(host).IsGranted, "Pre-consent dialog activated a source or performed HTTP/watch work.");
                cancelTransitions.Add($"dialog-open:visible={modal.IsVisible};owner-active={window.IsActive};owner-enabled={window.IsEnabled};input-enabled={input.IsEnabled};input-focused={input.IsFocused}");
                modal.Closing += (_, args) => cancelTransitions.Add($"dialog-closing:cancel={args.Cancel};reason={args.CloseReason}");
                modal.Closed += (_, _) => cancelTransitions.Add($"dialog-closed:visible={modal.IsVisible};owner-active={window.IsActive};input-focused={input.IsFocused}");
                modal.Activate();
                await Program.WaitUntilAsync(() => modal.IsActive && !window.IsActive
                    && cancelTransitions.Contains("owner-deactivated"));
                if (escape) modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                else Click(C<Button>(modal, "ActivationCancel"));
                await Program.WaitUntilAsync(() => !modal.IsVisible && add.IsEnabled);
                window.Activated -= ObserveOwnerActivated;
                window.Deactivated -= ObserveOwnerDeactivated;
                var canceledPolicy = Policy(host);
                var canceledFocus = window.FocusManager?.GetFocusedElement();
                Console.WriteLine("EXPERIMENTAL_CANCEL_DIAGNOSIS " + JsonSerializer.Serialize(new
                {
                    escape, canceledUrl, actualText = input.Text, textEqual = input.Text == canceledUrl,
                    inputFocused = input.IsFocused, inputEnabled = input.IsEnabled, inputVisible = input.IsEffectivelyVisible,
                    ownerActive = window.IsActive, ownerVisible = window.IsVisible, ownerEnabled = window.IsEnabled,
                    focusedType = canceledFocus?.GetType().FullName, focusedName = (canceledFocus as Control)?.Name,
                    focusedIsInput = ReferenceEquals(canceledFocus, input), focusedIsAdd = ReferenceEquals(canceledFocus, add),
                    sourceEnabled = host.Plugins!.Sources.Single(source => source.Name == Source).Enabled,
                    policyActive = canceledPolicy.IsActive, policyGranted = canceledPolicy.IsGranted,
                    policyEffective = canceledPolicy.Snapshot.AllowProviderSizeMd5Mismatch,
                    policyRevision = canceledPolicy.Snapshot.Revision,
                    dialogVisible = modal.IsVisible, dialogEnabled = modal.IsEnabled, addEnabled = add.IsEnabled,
                    requests = http.Requests.Count, watches = host.GetThreadWorkSnapshots().Count,
                    transitions = cancelTransitions.ToArray()
                }));
                Program.CaptureWindow(window, $"smoke-experimental-{scale}-{(escape ? "escape" : "cancel")}-before-focus-guard.png");
                Require(input.Text == canceledUrl && input.IsFocused && !Policy(host).IsActive && !Policy(host).IsGranted,
                    "Cancel/Escape changed policy or lost the original URL/focus.");
                Program.CaptureWindow(window, $"smoke-experimental-{scale}-{(escape ? "escape" : "cancel")}-focus.png");
            }

            await CheckTopbarCanceledModalFocusGuardsAsync(window, host, http);

            input.Text = ThreadUrl; Click(add);
            ExperimentalActivationDialog confirm = await WaitDialogAsync();
            Require(http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0, "HTTP/watch started before explicit confirmation.");
            // The same real Config persistence seam as EA02: Enable can save its
            // separate store, then the grant write encounters a directory.
            File.Move(settings, retainedSettings); Directory.CreateDirectory(settings);
            Click(C<Button>(confirm, "ActivationConfirm"));
            await Program.WaitUntilAsync(() => C<TextBlock>(confirm, "ActivationStatus").IsEffectivelyVisible
                && C<Button>(confirm, "ActivationConfirm").IsEnabled && Policy(host).IsActive);
            Require(!Policy(host).IsGranted && http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0
                && input.Text == ThreadUrl && confirm.IsVisible,
                "Real grant-save failure admitted the current URL or hid its actual active/strict state.");
            Program.CaptureWindow(confirm, $"smoke-experimental-{scale}-real-partial-error.png");
            Directory.Delete(settings); File.Move(retainedSettings, settings);
            Click(C<Button>(confirm, "ActivationConfirm"));
            await Program.WaitUntilAsync(() => !confirm.IsVisible && add.IsEnabled && host.GetThreadWorkSnapshots().Count == 1);
            Require(Policy(host).IsActive && Policy(host).IsGranted && http.Requests.IsEmpty,
                "Confirmed admission missed actual Host consent or ignored paused transport.");
            var durable = Config.LoadConfig(profile).SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(Owner, Source)];
            Require(durable.AllowProviderSizeMd5Mismatch && durable.Revision == Policy(host).Snapshot.Revision,
                "Real URL confirmation did not persist the actual Host grant.");
            await host.SetPausedAsync(false);
            using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45)))
                while (!host.GetThreadWorkSnapshots().Single().Media.Any(media => media.Status == MediaWorkStatus.Succeeded))
                    await Task.Delay(50, deadline.Token);
            await host.SetPausedAsync(true);
            Require(http.Unexpected == 0 && http.Requests.Count == 2
                && http.Requests.Contains("https://wizchan.org/demo/res/123.json")
                && http.Requests.Contains(ApphostIntegrityProcessSmoke.OriginalUrl), "Confirmed UI path did not use the exact deterministic HostHttp thread/media fixture.");

            const string mixed = "https://kohlchan.net/demo/res/123.html\nhttps://endchan.org/demo/res/123.html\nhttps://boards.4chan.org/g/thread/124\nhttps://invalid.example.test/no-source\n" + ThreadUrl + "#p124";
            var endchanBefore = host.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.SourceId == "endchan");
            bool endchanEnabledBefore = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins
                .Single(row => row.PluginId == "meowchan.source.endchan").Enabled;
            Require(!endchanEnabledBefore && !endchanBefore.IsActive && !endchanBefore.IsGranted,
                "Mixed-batch cancel target was not initially disabled and strict.");
            input.Text = mixed; Click(add);
            ExperimentalActivationDialog confirmedSource = await WaitDialogAsync();
            Require(C<TextBlock>(confirmedSource, "ActivationHeading").Text?.Contains("Kohlchan", StringComparison.OrdinalIgnoreCase) == true,
                "Mixed batch did not prompt its first disabled source first.");
            Click(C<Button>(confirmedSource, "ActivationConfirm"));
            await Program.WaitUntilAsync(() => !confirmedSource.IsVisible);
            ExperimentalActivationDialog canceledSource = await WaitDialogAsync();
            Require(C<TextBlock>(canceledSource, "ActivationHeading").Text?.Contains("Endchan", StringComparison.OrdinalIgnoreCase) == true,
                "A duplicate active source prompted again or batch source order changed.");
            Click(C<Button>(canceledSource, "ActivationCancel"));
            await Program.WaitUntilAsync(() => !canceledSource.IsVisible && add.IsEnabled && host.GetThreadWorkSnapshots().Count == 3);
            Require(input.Text == mixed && host.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.SourceId == "kohlchan").IsGranted
                && !host.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.SourceId == "endchan").IsGranted
                && http.Requests.Count == 2 && http.Unexpected == 0,
                "Mixed canceled/duplicate/normal/invalid batch lost text, granted the skipped source or performed paused HTTP.");
            var mixedView = host.GetView();
            ResourceId[] expectedMixed = { new("wizchan", "demo", "123"), new("kohlchan", "demo", "123"), new("4chan", "g", "124") };
            Require(mixedView.Threads.Count == expectedMixed.Length && mixedView.Boards.Count == 0
                && mixedView.Threads.Select(row => row.Snapshot.Id).ToHashSet().SetEquals(expectedMixed),
                "Mixed batch admitted a wrong canonical watch, lost a normal URL or duplicated a watch.");
            var endchanAfter = host.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.SourceId == "endchan");
            bool endchanEnabledAfter = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins
                .Single(row => row.PluginId == "meowchan.source.endchan").Enabled;
            Require(endchanEnabledAfter == endchanEnabledBefore && endchanAfter.IsActive == endchanBefore.IsActive
                && endchanAfter.IsGranted == endchanBefore.IsGranted,
                "Cancel changed the declined source's enabled/active/granted state.");
            Program.CaptureWindow(window, $"smoke-experimental-{scale}-mixed-batch.png");

            // Active strict sources must admit later URLs without restoring a revoked grant.
            await host.SetSourceMediaIntegrityPermissionAsync(Owner, Source, false, Policy(host).Snapshot.Revision);
            var strict = Policy(host);
            input.Text = "https://wizchan.org/demo/res/124.html"; Click(add);
            await Program.WaitUntilAsync(() => add.IsEnabled && host.GetThreadWorkSnapshots().Count == 4);
            Require(!Dialogs().Any() && Policy(host) == strict && !Policy(host).IsGranted && http.Requests.Count == 2,
                "An active strict URL reopened consent, regranted or attempted HTTP while paused.");

            await CheckOpenDialogInterleavingsAsync(window, host, scale);

            await CheckStatusPresentationAsync(target, strict);
            Console.WriteLine("EXPERIMENTAL_ACTIVATION_GUI PASS: real URL controls, disabled no-work warning/cancel/Escape, explicit Host consent, deterministic worker metadata/media, mixed batch and strict later URL; EN/DE Dark/Light warning and synthetic partial/retry presentation at actual scale " + expectedScale
                + ". Routed actions are synthetic; no physical/native input or live-source acceptance is claimed.");
        }
        finally
        {
            if (File.Exists(retainedSettings))
            {
                if (Directory.Exists(settings)) Directory.Delete(settings);
                File.Move(retainedSettings, settings);
            }
            pending?.TrySetResult(new(ExperimentalSourceActivationStatus.GrantFailed, null, null, "Fixture cleanup"));
            foreach (var modal in Dialogs().ToArray()) modal.Close();
            window.Close();
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Stopped);
            Texts.SetLanguage(original.Language); Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    private static async Task CheckTopbarCanceledModalFocusGuardsAsync(MainWindow window, DesktopSessionHost host,
        ApphostIntegrityProcessSmoke.FixtureHttp http)
    {
        TextBox input = C<TextBox>(window, "UrlInput");
        Button add = C<Button>(window, "AddUrlButton"), laterChoice = C<Button>(window, "PauseButton");
        foreach (string scenario in new[] { "add-button", "later-focus", "later-null", "owner-hide" })
        {
            input.Text = ThreadUrl;
            Require(input.Focus(), "The guarded cancel fixture could not focus its original URL editor.");
            if (scenario == "add-button")
                Require(add.Focus() && add.IsFocused, "The native-like Add-button origin did not actually receive focus before submit.");
            Click(add);
            ExperimentalActivationDialog modal = await WaitDialogAsync();
            bool laterChoiceFocused = false;
            modal.Closed += (_, _) =>
            {
                if (scenario is "later-focus" or "later-null")
                {
                    laterChoiceFocused = laterChoice.Focus() && laterChoice.IsFocused;
                    if (scenario == "later-null") window.FocusManager!.Focus(null);
                }
                else if (scenario == "owner-hide") window.Hide();
            };
            Click(C<Button>(modal, "ActivationCancel"));
            await Program.WaitUntilAsync(() => !modal.IsVisible && add.IsEnabled);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Require(input.Text == ThreadUrl && http.Requests.IsEmpty && http.Unexpected == 0
                && host.GetThreadWorkSnapshots().Count == 0 && host.Plugins!.Runtime.GetSnapshots().Count == 0
                && !Policy(host).IsActive && !Policy(host).IsGranted,
                "A guarded canceled modal changed text, admitted a Watch or activated its source.");
            if (scenario == "add-button")
                Require(input.IsFocused, "Cancel did not return an initially focused Add button to its retained URL editor.");
            else if (scenario == "later-focus")
                Require(laterChoiceFocused && laterChoice.IsFocused && !input.IsFocused,
                    "Canceled admission stole focus from a later deliberate control choice.");
            else if (scenario == "later-null")
                Require(laterChoiceFocused && window.FocusManager?.GetFocusedElement() == null && !input.IsFocused,
                    "Canceled admission reclaimed focus after a later choice subsequently moved to null.");
            else
            {
                Require(!window.IsVisible && !input.IsFocused, "Canceled admission restored its input after the owner was hidden.");
                window.Show(); window.Activate();
                await Program.WaitUntilAsync(() => window.IsVisible && window.IsActive);
            }
            Console.WriteLine("EXPERIMENTAL_CANCEL_FOCUS_GUARD " + JsonSerializer.Serialize(new
            { scenario, laterChoiceFocused, inputFocused = input.IsFocused, ownerVisible = window.IsVisible,
                focusedName = (window.FocusManager?.GetFocusedElement() as Control)?.Name }));
        }
    }

    private static async Task CheckSourcesModalFocusGuardsAsync(MainWindow window, SourcesPage sources,
        DesktopSessionHost host, ApphostIntegrityProcessSmoke.FixtureHttp http)
    {
        TextBox editor = C<TextBox>(sources, "SourceUrlInput");
        Button add = C<Button>(sources, "SourceAddButton"), laterChoice = C<Button>(sources, "SourcePluginButton");
        ContentControl pageHost = C<ContentControl>(window, "SourcesPageHost");
        bool RetainsExactControl(Control control) => window.IsActive && control.IsFocused
            && ReferenceEquals(window.FocusManager?.GetFocusedElement(), control)
            && control.IsEffectivelyEnabled && control.IsEffectivelyVisible && control.IsMeasureValid && control.IsArrangeValid
            && ReferenceEquals(TopLevel.GetTopLevel(control), window) && InWindow(window, control);
        foreach (string scenario in new[] { "add-button", "later-focus", "later-null", "owner-hide", "source-change", "detach", "text-away-back" })
        {
            sources.SelectSource(Source);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            editor.Text = ThreadUrl; editor.BringIntoView();
            Require(editor.Focus(), "The Sources modal guard fixture could not focus its actual editor.");
            if (scenario == "add-button")
                Require(add.Focus() && add.IsFocused, "The real Sources Add-button focus origin was not established before busy disable.");
            bool ownerDeactivated = false, laterChoiceFocused = false, nullChoiceEstablished = false;
            List<string> focusTransitions = new();
            List<object> textClaimTransitions = new();
            object? textFocusClaim = null;
            bool textClaimInitiallyLive = false, textAwayStarted = false, textAwayCancelled = false, textBackCancelled = false;
            bool textClaimStayedCancelled = true, nativeHistoricalEditorFocus = false, rejectedTextIntervalFocus = false;
            const System.Reflection.BindingFlags claimFields = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Delegate? CurrentTextCancel() => typeof(SourcesPage).GetField("_cancelSubmitFocusRestore", claimFields)?.GetValue(sources) as Delegate;
            object? ReadTextClaim(string name) => textFocusClaim?.GetType().GetField(name, claimFields)?.GetValue(textFocusClaim);
            bool TextClaimCancelled() => textFocusClaim != null && CurrentTextCancel() == null
                && ReadTextClaim("observing") is false && ReadTextClaim("newerInput") is true;
            void ObserveTextClaim(string stage, object? eventSource = null, bool focusStack = false)
            {
                if (scenario != "text-away-back") return;
                Delegate? cancel = CurrentTextCancel();
                textFocusClaim ??= cancel?.Target;
                var focused = window.FocusManager?.GetFocusedElement();
                if (textAwayCancelled && !TextClaimCancelled()) textClaimStayedCancelled = false;
                string? synchronousFocusStack = focusStack ? new System.Diagnostics.StackTrace(true).ToString() : null;
                if (focusStack && textAwayStarted)
                {
                    int scopeFrame = synchronousFocusStack!.IndexOf("Avalonia.Input.FocusManager.SetFocusScope(", StringComparison.Ordinal);
                    int activationFrame = synchronousFocusStack.IndexOf("Avalonia.Controls.WindowBase.HandleActivated(", StringComparison.Ordinal);
                    bool nativeActivation = scopeFrame >= 0 && activationFrame > scopeFrame
                        && !synchronousFocusStack.Contains("MeowChan.SourcesPage", StringComparison.Ordinal)
                        && !synchronousFocusStack.Contains("RestoreInputOrAwaitOwnerActivation", StringComparison.Ordinal);
                    bool exactNativeEditor = TextClaimCancelled() && nativeActivation && ReferenceEquals(eventSource, editor)
                        && ReferenceEquals(focused, editor) && editor.IsFocused;
                    nativeHistoricalEditorFocus |= exactNativeEditor;
                    // Even a later null final state must not hide a transient
                    // product restore or any unrelated focus after cancellation.
                    rejectedTextIntervalFocus |= !exactNativeEditor;
                }
                textClaimTransitions.Add(new
                {
                    sequence = textClaimTransitions.Count, stage, editor.Text,
                    ownerActive = window.IsActive, sources.IsBusy, editor.IsEnabled, editor.IsFocused,
                    focusedType = focused?.GetType().FullName, focusedName = (focused as Control)?.Name,
                    focusedIsEditor = ReferenceEquals(focused, editor), eventSourceType = eventSource?.GetType().FullName,
                    eventSourceName = (eventSource as Control)?.Name, cancelDelegatePresent = cancel != null,
                    claimType = textFocusClaim?.GetType().FullName,
                    availableClaimFields = textFocusClaim?.GetType().GetFields(claimFields).Select(field => field.Name).ToArray(),
                    observing = ReadTextClaim("observing"), newerInput = ReadTextClaim("newerInput"),
                    admissionModalPresent = ReadTextClaim("admissionModal") != null, sawAdmissionModal = ReadTextClaim("sawAdmissionModal"),
                    modalOwnerDeactivated = ReadTextClaim("modalOwnerDeactivated"),
                    awaitingAdmissionOwnerActivation = ReadTextClaim("awaitingAdmissionOwnerActivation"),
                    ownerActivationPending = ReadTextClaim("ownerActivation") != null,
                    // The synchronous focus stack distinguishes native activation
                    // focus from a product callback's explicit editor.Focus().
                    synchronousFocusStack,
                    passiveReflectionOnly = true
                });
            }
            void ObserveTextBeforeProduct(object? sender, AvaloniaPropertyChangedEventArgs args)
            { if (args.Property == TextBox.TextProperty) ObserveTextClaim("text-property-before-product"); }
            void ObserveTextAfterProduct(object? sender, AvaloniaPropertyChangedEventArgs args)
            { if (args.Property == TextBox.TextProperty) ObserveTextClaim("text-property-after-product"); }
            string FocusState() => $"editor={editor.IsFocused};add={add.IsFocused};current={(window.FocusManager?.GetFocusedElement() as Control)?.Name};busy={sources.IsBusy};owner-active={window.IsActive}";
            void ObserveDeactivated(object? sender, EventArgs args)
            { ownerDeactivated = true; focusTransitions.Add("owner-deactivated:" + FocusState()); ObserveTextClaim("owner-deactivated-before-product"); }
            void ObserveActivated(object? sender, EventArgs args)
            { focusTransitions.Add("owner-activated:" + FocusState()); ObserveTextClaim("owner-activated-before-product"); }
            void ObserveFocus(object? sender, Avalonia.Input.FocusChangedEventArgs args)
            { focusTransitions.Add("got-focus:" + (args.Source as Control)?.Name + ";" + FocusState()); ObserveTextClaim("owner-got-focus", args.Source, focusStack: true); }
            void ObserveBusy(object? sender, EventArgs args)
            { focusTransitions.Add("busy-changed:" + FocusState()); ObserveTextClaim("busy-changed"); }
            void ObserveEnabled(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == InputElement.IsEnabledProperty)
                    focusTransitions.Add($"enabled-changed:{(sender as Control)?.Name};editor-enabled={editor.IsEnabled};add-enabled={add.IsEnabled};" + FocusState());
            }
            focusTransitions.Add("before-submit:" + FocusState());
            window.Activated += ObserveActivated;
            window.AddHandler(InputElement.GotFocusEvent, ObserveFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            sources.BusyChanged += ObserveBusy;
            editor.PropertyChanged += ObserveEnabled; add.PropertyChanged += ObserveEnabled;
            if (scenario == "text-away-back") editor.PropertyChanged += ObserveTextBeforeProduct;
            window.Deactivated += ObserveDeactivated;
            try
            {
                if (scenario == "add-button") Click(add);
                else editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                ExperimentalActivationDialog modal = await WaitDialogAsync();
                if (scenario == "text-away-back") editor.PropertyChanged += ObserveTextAfterProduct;
                ObserveTextClaim("modal-open");
                modal.Activate();
                await Program.WaitUntilAsync(() => modal.IsActive && !window.IsActive && ownerDeactivated);
                modal.Closed += (_, _) =>
                {
                    focusTransitions.Add("modal-closed:" + FocusState());
                    ObserveTextClaim("modal-closed-after-product");
                    if (scenario is "later-focus" or "later-null")
                    {
                        laterChoiceFocused = laterChoice.Focus() && laterChoice.IsFocused;
                        if (scenario == "later-null")
                        {
                            window.FocusManager!.Focus(null);
                            nullChoiceEstablished = window.FocusManager.GetFocusedElement() == null;
                            focusTransitions.Add("null-choice-established=" + nullChoiceEstablished + ";" + FocusState());
                            Console.WriteLine("EXPERIMENTAL_SOURCES_NULL_PREMISE " + JsonSerializer.Serialize(new
                            { scenario, nullChoiceEstablished, laterChoiceFocused, ownerActive = window.IsActive,
                                focusedName = (window.FocusManager.GetFocusedElement() as Control)?.Name }));
                        }
                    }
                    else if (scenario == "owner-hide") window.Hide();
                    else if (scenario == "source-change") { sources.SelectSource("kohlchan"); sources.SelectSource(Source); }
                    else if (scenario == "detach") { pageHost.Content = null; pageHost.Content = sources; }
                    else if (scenario == "text-away-back")
                    {
                        ObserveTextClaim("before-text-away");
                        textClaimInitiallyLive = textFocusClaim != null && ReferenceEquals(CurrentTextCancel()?.Target, textFocusClaim)
                            && ReadTextClaim("observing") is true && ReadTextClaim("newerInput") is false;
                        textAwayStarted = true;
                        editor.Text = ThreadUrl + "#later";
                        textAwayCancelled = TextClaimCancelled();
                        ObserveTextClaim("after-text-away-before-text-back");
                        editor.Text = ThreadUrl;
                        textBackCancelled = TextClaimCancelled();
                        ObserveTextClaim("after-text-back");
                    }
                };
                Click(C<Button>(modal, "ActivationCancel"));
                await Program.WaitUntilAsync(() => !modal.IsVisible && !sources.IsBusy && editor.IsEnabled);
                if (scenario != "owner-hide") await Program.WaitUntilAsync(() => window.IsActive
                    && editor.IsMeasureValid && editor.IsArrangeValid && add.IsMeasureValid && add.IsArrangeValid
                    && (scenario != "add-button" || RetainsExactControl(editor) || RetainsExactControl(add)));
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Require(editor.Text == ThreadUrl && http.Requests.IsEmpty && http.Unexpected == 0
                    && host.GetThreadWorkSnapshots().Count == 0 && !Policy(host).IsActive && !Policy(host).IsGranted,
                    "A canceled Sources modal changed policy, original input or work.");
                Console.WriteLine("EXPERIMENTAL_SOURCES_FOCUS_DIAGNOSIS " + JsonSerializer.Serialize(new
                {
                    scenario, ownerDeactivated, laterChoiceFocused, nullChoiceEstablished, editorFocused = editor.IsFocused, addFocused = add.IsFocused,
                    editorKeyboardFocusWithin = editor.IsKeyboardFocusWithin, addKeyboardFocusWithin = add.IsKeyboardFocusWithin,
                    focusedType = window.FocusManager?.GetFocusedElement()?.GetType().FullName,
                    focusedName = (window.FocusManager?.GetFocusedElement() as Control)?.Name,
                    ownerActive = window.IsActive, ownerVisible = window.IsVisible, ownerEnabled = window.IsEnabled,
                    editorEnabled = editor.IsEnabled, addEnabled = add.IsEnabled, sourcesBusy = sources.IsBusy,
                    originalText = ThreadUrl, currentText = editor.Text,
                    sourceHeading = C<TextBlock>(sources, "SourceHeading").Text,
                    requests = http.Requests.Count, watches = host.GetThreadWorkSnapshots().Count,
                    policyActive = Policy(host).IsActive, policyGranted = Policy(host).IsGranted,
                    transitions = focusTransitions.ToArray(), textClaimTransitions = textClaimTransitions.ToArray()
                }));
                Program.CaptureWindow(window, $"smoke-experimental-sources-focus-{scenario}-before-guard.png");
                if (scenario == "add-button")
                    Require(RetainsExactControl(editor) || RetainsExactControl(add),
                        "Sources Add-button cancel lost both its retained URL editor and its actual original active trigger.");
                else if (scenario == "later-focus")
                    Require(laterChoiceFocused && laterChoice.IsFocused && !editor.IsFocused,
                        "Sources modal cancel stole a later deliberate control focus.");
                else if (scenario == "later-null")
                    Require(laterChoiceFocused && nullChoiceEstablished && !editor.IsFocused
                        && !ReferenceEquals(window.FocusManager?.GetFocusedElement(), editor)
                        && (window.FocusManager?.GetFocusedElement() == null || RetainsExactControl(laterChoice)),
                        "Sources modal cancel overrode a later null choice with its editor or an unrelated/inactive focus target.");
                else if (scenario == "text-away-back")
                {
                    Require(textClaimInitiallyLive && textAwayCancelled && textBackCancelled
                        && textClaimStayedCancelled && TextClaimCancelled(),
                        "Text-away/back did not synchronously and permanently cancel its exact original live submit claim.");
                    Require(!rejectedTextIntervalFocus && window.IsActive
                        && (window.FocusManager?.GetFocusedElement() == null && !editor.IsFocused
                            || RetainsExactControl(editor) && nativeHistoricalEditorFocus),
                        "Text-away/back allowed an unproved, unrelated or product-driven focus after its claim was canceled.");
                }
                else
                    Require(!editor.IsFocused, "Sources restored focus after a later " + scenario + " invalidated the submit scope.");
                if (scenario == "owner-hide")
                {
                    Require(!window.IsVisible, "Sources modal cancellation reopened a deliberately hidden owner.");
                    window.Show(); window.Activate();
                    await Program.WaitUntilAsync(() => window.IsVisible && window.IsActive);
                }
                Console.WriteLine("EXPERIMENTAL_SOURCES_FOCUS_GUARD " + JsonSerializer.Serialize(new
                { scenario, ownerDeactivated, laterChoiceFocused, editorFocused = editor.IsFocused,
                    ownerActive = window.IsActive, focusedName = (window.FocusManager?.GetFocusedElement() as Control)?.Name }));
            }
            finally
            {
                if (scenario == "text-away-back")
                {
                    ObserveTextClaim("probe-finally-before-observer-cleanup");
                    Console.WriteLine("EXPERIMENTAL_SOURCES_TEXT_CLAIM_DIAGNOSIS " + JsonSerializer.Serialize(new
                    { scenario, textClaimTransitions = textClaimTransitions.ToArray() }));
                }
                editor.PropertyChanged -= ObserveTextBeforeProduct;
                editor.PropertyChanged -= ObserveTextAfterProduct;
                window.Deactivated -= ObserveDeactivated;
                window.Activated -= ObserveActivated;
                window.RemoveHandler(InputElement.GotFocusEvent, ObserveFocus);
                sources.BusyChanged -= ObserveBusy;
                editor.PropertyChanged -= ObserveEnabled; add.PropertyChanged -= ObserveEnabled;
            }
        }
    }

    private static async Task CheckOpenDialogInterleavingsAsync(MainWindow owner, DesktopSessionHost otherSession, string scale)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "open-dialog-edges");
        new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
            SaveUrlsOnExit = true, AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0,
            WorkspaceMode = "Barebones", Language = "en", Theme = "Dark" }.SaveConfig(profile);
        using var http = new ApphostIntegrityProcessSmoke.FixtureHttp();
        await using DesktopSessionHost host = new(profile, providers: null,
            updateSessionFactory: (path, settings) => new UpdateSession(path, settings, http.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: http.Client);
        MainWindow window = new(host);
        window.Show(owner);
        string settings = Path.Combine(profile, "settings.json"), retainedSettings = settings + ".edge-retained";
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready && C<Button>(window, "AddUrlButton").IsEnabled);
            TextBox input = C<TextBox>(window, "UrlInput");
            Button add = C<Button>(window, "AddUrlButton");
            async Task<ExperimentalActivationDialog> OpenAsync(string url)
            {
                input.Text = url; input.Focus(); Click(add);
                return await WaitDialogAsync();
            }
            async Task RejectStaleAsync(ExperimentalActivationDialog modal)
            {
                Click(C<Button>(modal, "ActivationConfirm"));
                await Program.WaitUntilAsync(() => C<TextBlock>(modal, "ActivationStatus").IsEffectivelyVisible
                    && C<Button>(modal, "ActivationCancel").IsEnabled && !C<Button>(modal, "ActivationConfirm").IsEnabled);
                Require(modal.IsVisible, "A stale open-dialog operation silently continued admission.");
                Click(C<Button>(modal, "ActivationCancel"));
                await Program.WaitUntilAsync(() => !modal.IsVisible);
            }

            // Submit through the real Sources page, including its owner-deactivation
            // guard. Record actual focus separately: native modal restoration and
            // physical keyboard/IME acceptance are not inferred from routed Enter.
            window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            SourcesPage sources = (SourcesPage)C<ContentControl>(window, "SourcesPageHost").Content!;
            sources.SelectSource(Source);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            TextBox sourceInput = C<TextBox>(sources, "SourceUrlInput");
            sourceInput.Text = ThreadUrl; sourceInput.BringIntoView();
            Require(sourceInput.Focus(), "The productive Sources editor could not receive focus before its modal.");
            bool sourceOwnerDeactivated = false;
            bool sourceModalClosed = false;
            object? focusClaim = null;
            List<object> sourceTransitions = new();
            const System.Reflection.BindingFlags privateFields = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object? PageField(string name) => typeof(SourcesPage).GetField(name, privateFields)?.GetValue(sources);
            object? ClaimField(string name) => focusClaim?.GetType().GetField(name, privateFields)?.GetValue(focusClaim);
            void ObserveSource(string stage, object? eventSource = null, object? originalSource = null)
            {
                // Read only the current exact submit closure. Retain it after the
                // product clears its delegate so cancellation remains observable.
                Delegate? cancel = PageField("_cancelSubmitFocusRestore") as Delegate;
                focusClaim ??= cancel?.Target;
                var focused = window.FocusManager?.GetFocusedElement();
                Window? claimedModal = ClaimField("admissionModal") as Window;
                bool? observing = ClaimField("observing") as bool?;
                bool? newerInput = ClaimField("newerInput") as bool?;
                string guardPrediction = stage.StartsWith("owner-got-focus", StringComparison.Ordinal)
                    ? "OnNewFocus unconditionally cancels while handler is attached"
                    : stage.StartsWith("owner-deactivated", StringComparison.Ordinal) && claimedModal == null
                        ? "OnOwnerDeactivated cancels if admissionModal is null"
                        : "no event cancellation inferred";
                sourceTransitions.Add(new
                {
                    sequence = sourceTransitions.Count, stage, sourceModalClosed,
                    focusedType = focused?.GetType().FullName, focusedName = (focused as Control)?.Name,
                    focusedIsEditor = ReferenceEquals(focused, sourceInput), sourceInput.IsFocused,
                    eventSourceType = eventSource?.GetType().FullName, eventSourceName = (eventSource as Control)?.Name,
                    originalSourceType = originalSource?.GetType().FullName, originalSourceName = (originalSource as Control)?.Name,
                    ownerActive = window.IsActive, ownerVisible = window.IsVisible, ownerEnabled = window.IsEnabled,
                    editorEnabled = sourceInput.IsEnabled, editorEffectiveEnabled = sourceInput.IsEffectivelyEnabled,
                    editorVisible = sourceInput.IsEffectivelyVisible, sourceInput.IsMeasureValid, sourceInput.IsArrangeValid,
                    editorBounds = sourceInput.Bounds.ToString(), sources.IsBusy, sourceInput.Text,
                    selectedId = PageField("_selectedId") as string, selectionRevision = PageField("_sourceSelectionRevision"),
                    claimType = focusClaim?.GetType().FullName, cancelDelegatePresent = cancel != null,
                    claimFields = focusClaim?.GetType().GetFields(privateFields).Select(field => field.Name).ToArray(),
                    observing, newerInput, sawAdmissionModal = ClaimField("sawAdmissionModal"),
                    admissionModalPresent = claimedModal != null, admissionModalVisible = claimedModal?.IsVisible,
                    admissionModalActive = claimedModal?.IsActive, ownerActivationPending = ClaimField("ownerActivation") != null,
                    guardPrediction, diagnosticInferenceOnly = true
                });
            }
            void ObserveDeactivation(object? sender, EventArgs args)
            { sourceOwnerDeactivated = true; ObserveSource("owner-deactivated-before-product"); }
            void ObserveActivation(object? sender, EventArgs args) => ObserveSource("owner-activated-before-product");
            void ObserveFocus(object? sender, FocusChangedEventArgs args) => ObserveSource("owner-got-focus-before-product", args.Source,
                args.GetType().GetProperty("OriginalSource")?.GetValue(args));
            void ObservePostFocus(object? sender, FocusChangedEventArgs args) => ObserveSource("owner-got-focus-after-product", args.Source,
                args.GetType().GetProperty("OriginalSource")?.GetValue(args));
            void ObservePostDeactivation(object? sender, EventArgs args) => ObserveSource("owner-deactivated-after-product");
            void ObserveBusy(object? sender, EventArgs args) => ObserveSource("busy-changed");
            void ObserveEditorProperty(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == InputElement.IsEnabledProperty || args.Property == TextBox.TextProperty)
                    ObserveSource("editor-property:" + args.Property.Name);
            }
            window.Deactivated += ObserveDeactivation;
            window.Activated += ObserveActivation;
            window.AddHandler(InputElement.GotFocusEvent, ObserveFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            sources.BusyChanged += ObserveBusy;
            sourceInput.PropertyChanged += ObserveEditorProperty;
            ObserveSource("before-submit");
            try
            {
                sourceInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                ExperimentalActivationDialog sourceModal = await WaitDialogAsync();
                window.AddHandler(InputElement.GotFocusEvent, ObservePostFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                window.Deactivated += ObservePostDeactivation;
                sourceModal.Closing += (_, args) => ObserveSource("modal-closing:cancel=" + args.Cancel);
                sourceModal.Closed += (_, _) => { sourceModalClosed = true; ObserveSource("modal-closed-after-product"); };
                sourceModal.Deactivated += (_, _) => ObserveSource("modal-deactivated-after-product");
                ObserveSource("modal-open");
                Require(sources.IsBusy && !sourceInput.IsEnabled, "The productive Sources submit did not keep its busy input guard during the modal.");
                // This follow-up must traverse the actual native Window events;
                // a rapid routed Cancel before WM activation cannot prove them.
                sourceModal.Activate();
                try
                {
                    await Program.WaitUntilAsync(() => sourceModal.IsActive && !window.IsActive && sourceOwnerDeactivated);
                }
                finally
                {
                    Console.WriteLine("EXPERIMENTAL_SOURCES_ACTIVE_MODAL_TRANSITION " + JsonSerializer.Serialize(new
                    { modalActive = sourceModal.IsActive, ownerActive = window.IsActive, sourceOwnerDeactivated,
                        modalVisible = sourceModal.IsVisible, editorFocused = sourceInput.IsFocused,
                        actualScale = sourceModal.RenderScaling, nativeWindowEventsOnly = true }));
                    Program.CaptureWindow(sourceModal, $"smoke-experimental-{scale}-sources-active-modal-before-cancel.png");
                }
                Click(C<Button>(sourceModal, "ActivationCancel"));
                await Program.WaitUntilAsync(() => !sourceModal.IsVisible && !sources.IsBusy && sourceInput.IsEnabled);
                ObserveSource("before-owner-editor-readiness");
                try
                {
                    // Strengthen the same existing 5s owner-ready phase; an
                    // active owner alone does not prove the Enter focus contract.
                    await Program.WaitUntilAsync(() => window.IsActive && sourceInput.IsFocused
                        && ReferenceEquals(window.FocusManager?.GetFocusedElement(), sourceInput)
                        && sourceInput.IsEffectivelyEnabled && sourceInput.IsEffectivelyVisible
                        && sourceInput.IsMeasureValid && sourceInput.IsArrangeValid
                        && ReferenceEquals(TopLevel.GetTopLevel(sourceInput), window) && InWindow(window, sourceInput));
                }
                finally
                {
                    ObserveSource("after-owner-editor-readiness-before-capture");
                    Console.WriteLine("EXPERIMENTAL_SOURCES_ENTER_DIAGNOSIS " + JsonSerializer.Serialize(new
                    { sourceOwnerDeactivated, transitions = sourceTransitions.ToArray(),
                        requests = http.Requests.Count, unexpected = http.Unexpected, watches = host.GetThreadWorkSnapshots().Count,
                        policyActive = Policy(host).IsActive, policyGranted = Policy(host).IsGranted,
                        originalSourcePropertyAvailable = typeof(FocusChangedEventArgs).GetProperty("OriginalSource") != null,
                        passiveReflectionOnly = true }));
                    // Capture updates layout, so it follows the passive oracle or
                    // its hard timeout and cannot satisfy that oracle retroactively.
                    Program.CaptureWindow(window, $"smoke-experimental-{scale}-sources-enter-before-focus-guard.png");
                }
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Require(sourceInput.Text == ThreadUrl && !Policy(host).IsActive && !Policy(host).IsGranted
                    && http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0,
                    "The Sources modal cancel changed its retained text, policy or work.");
                Require(sourceOwnerDeactivated && window.IsActive && sourceInput.IsFocused
                    && ReferenceEquals(window.FocusManager?.GetFocusedElement(), sourceInput),
                    "Sources did not restore its retained editor after the actual owned-modal deactivation/cancel transition.");
                Program.CaptureWindow(window, $"smoke-experimental-{scale}-sources-modal-focus.png");
                Console.WriteLine("EXPERIMENTAL_SOURCES_MODAL_OBSERVATION " + JsonSerializer.Serialize(new
                { sourceOwnerDeactivated, editorFocused = sourceInput.IsFocused, ownerActive = window.IsActive,
                    focusedControl = (window.FocusManager?.GetFocusedElement() as Control)?.Name,
                    focusRequirementClosed = sourceInput.IsFocused, routedInputOnly = true }));
            }
            finally
            {
                ObserveSource("probe-finally-before-detach");
                Console.WriteLine("EXPERIMENTAL_SOURCES_ENTER_FINAL_DIAGNOSIS " + JsonSerializer.Serialize(new
                { sourceOwnerDeactivated, transitions = sourceTransitions.ToArray(), passiveReflectionOnly = true }));
                window.Deactivated -= ObserveDeactivation;
                window.Deactivated -= ObservePostDeactivation;
                window.Activated -= ObserveActivation;
                window.RemoveHandler(InputElement.GotFocusEvent, ObserveFocus);
                window.RemoveHandler(InputElement.GotFocusEvent, ObservePostFocus);
                sources.BusyChanged -= ObserveBusy;
                sourceInput.PropertyChanged -= ObserveEditorProperty;
            }
            await CheckSourcesModalFocusGuardsAsync(window, sources, host, http);
            Click(C<Button>(window, "DownloadsNavigationButton"));

            // Invalidate the selected delivered build while its actual URL modal
            // remains open. No target DTO or equality helper is mutated.
            const string endchanUrl = "https://endchan.net/demo/res/123.html";
            var endchan = (await host.ProjectExperimentalSourceActivationAsync(endchanUrl)).Target
                ?? throw new InvalidOperationException("Missing delivered Endchan target.");
            ExperimentalActivationDialog unavailable = await OpenAsync(endchanUrl);
            var saved = await host.Plugins!.ProfileStore.ReadAsync();
            await host.Plugins.ProfileStore.ReportActivationFailureAsync(endchan.PluginOwnerId, endchan.ExecutionRevision,
                "Open-dialog fixture selected build unavailable", saved.Revision);
            Require(unavailable.IsVisible && (await host.ProjectExperimentalSourceActivationAsync(endchanUrl)).Status
                == ExperimentalSourceActivationProjectionStatus.Unavailable, "Selected-build invalidation did not affect the open operation.");
            await RejectStaleAsync(unavailable);
            await Program.WaitUntilAsync(() => add.IsEnabled);
            Require(input.Text == endchanUrl && input.IsFocused && http.Requests.IsEmpty
                && host.GetThreadWorkSnapshots().Count == 0 && !Policy(host).IsGranted
                && !host.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.SourceId == "endchan").IsGranted,
                "An invalidated open-dialog target changed consent/work or lost its retained URL focus.");

            // A genuine second Host rejects this first Host's captured authority.
            // MainWindow has no in-place session/owner replacement API; this proves
            // the real command boundary, not an invented UI session switch.
            var target = (await host.ProjectExperimentalSourceActivationAsync(ThreadUrl)).Target!;
            var otherPolicy = Policy(otherSession);
            ExperimentalSourceActivationResult? foreignResult = null;
            ExperimentalActivationDialog foreign = new(target, async captured =>
                foreignResult = await otherSession.ActivateAndAllowExperimentalSourceAsync(captured));
            Task<ExperimentalActivationDecision?> foreignDecision = foreign.ShowDialog<ExperimentalActivationDecision?>(window);
            await Program.WaitUntilAsync(() => foreign.IsVisible);
            await RejectStaleAsync(foreign);
            Require((await foreignDecision)?.ContinueAdmission != true && foreignResult?.Status == ExperimentalSourceActivationStatus.StaleTarget
                && Policy(otherSession) == otherPolicy && !Policy(host).IsActive && !Policy(host).IsGranted
                && http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 0,
                "An open modal accepted another real Host session's target or mutated either session.");

            // Seed one real paused Watch, disable its owner, then submit a different
            // URL. A real grant-save failure exposes the durable Enable/Grant gap.
            saved = await host.Plugins.ProfileStore.ReadAsync();
            saved = await host.Plugins.ProfileStore.SetEnabledAsync(Owner, true, saved.Revision);
            Require((await host.AddAsync(ThreadUrl)).Status == MeowChan.AdmissionStatus.Added, "Existing-watch fixture admission failed.");
            await host.Plugins.ProfileStore.SetEnabledAsync(Owner, false, saved.Revision);
            const string deferredUrl = "https://wizchan.org/demo/res/124.html";
            ExperimentalActivationDialog partial = await OpenAsync(deferredUrl);
            Require(http.Requests.IsEmpty && host.GetThreadWorkSnapshots().Count == 1 && !Policy(host).IsActive,
                "The disabled existing Watch or unconfirmed new URL started work.");
            File.Move(settings, retainedSettings); Directory.CreateDirectory(settings);
            Click(C<Button>(partial, "ActivationConfirm"));
            await Program.WaitUntilAsync(() => C<TextBlock>(partial, "ActivationStatus").IsEffectivelyVisible
                && C<Button>(partial, "ActivationConfirm").IsEnabled && Policy(host).IsActive);
            Require(!Policy(host).IsGranted && partial.IsVisible && host.GetThreadWorkSnapshots().Count == 1,
                "The new URL escaped the partial Enable/Grant boundary.");
            // The real failed grant is now observed. Remove only the fixture's
            // write barrier: SetPausedAsync also persists Config. Do not retry
            // consent or admit the deferred URL while repairing this setup.
            var partialPolicy = Policy(host);
            Directory.Delete(settings); File.Move(retainedSettings, settings);
            Require(File.Exists(settings) && !Directory.Exists(settings) && !File.Exists(retainedSettings)
                && Policy(host) == partialPolicy && partial.IsVisible
                && C<Button>(partial, "ActivationConfirm").IsEnabled && http.Requests.IsEmpty
                && host.GetThreadWorkSnapshots().Single().Url == ThreadUrl,
                "Removing the fixture write barrier changed partial consent, admitted the new URL or started paused work.");
            await host.SetPausedAsync(false);
            using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45)))
                while (!host.GetThreadWorkSnapshots().Single().Media.Any(media => media.Status == MediaWorkStatus.NeedsIntervention
                    && media.Failure == "Provider size or MD5 differs without current source permission and exact references."))
                    await Task.Delay(50, deadline.Token);
            await host.SetPausedAsync(true);
            Require(partial.IsVisible && !Policy(host).IsGranted && host.GetThreadWorkSnapshots().Single().Url == ThreadUrl
                && http.Unexpected == 0 && http.Requests.Count == 2
                && http.Requests.Count(url => url == "https://wizchan.org/demo/res/123.json") == 1
                && http.Requests.Count(url => url == ApphostIntegrityProcessSmoke.OriginalUrl) == 1
                && host.Plugins.Runtime.GetSnapshots().Any(worker => worker.ProcessId is > 0),
                "The existing Watch did not work strictly after Enable, or the deferred URL started before success.");
            Program.CaptureWindow(partial, $"smoke-experimental-{scale}-existing-watch-before-grant.png");
            Click(C<Button>(partial, "ActivationCancel"));
            await Program.WaitUntilAsync(() => !partial.IsVisible && add.IsEnabled);
            Require(input.Text == deferredUrl && host.GetThreadWorkSnapshots().Count == 1 && !Policy(host).IsGranted,
                "Closing partial consent admitted the new URL or implicitly granted its source.");

            // Stop the real session while another productive URL dialog is open;
            // a late confirm must not save or redirect into another live session.
            const string shutdownUrl = "https://kohlchan.net/demo/res/123.html";
            ExperimentalActivationDialog stopping = await OpenAsync(shutdownUrl);
            saved = await host.Plugins.ProfileStore.ReadAsync();
            string grantsBeforeStop = JsonSerializer.Serialize(Config.LoadConfig(profile).SourceMediaIntegrityPermissions);
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Require(stopping.IsVisible && host.State == DesktopSessionState.Stopped, "Shutdown did not leave the captured modal testable.");
            stopping.Closed += (_, _) => window.Close();
            await RejectStaleAsync(stopping);
            await Program.WaitUntilAsync(() => !window.IsVisible);
            Require(!input.IsFocused, "A late canceled modal restored focus while its owner was closing.");
            Require((await host.Plugins.ProfileStore.ReadAsync()).Revision == saved.Revision
                && JsonSerializer.Serialize(Config.LoadConfig(profile).SourceMediaIntegrityPermissions) == grantsBeforeStop
                && http.Requests.Count == 2 && http.Unexpected == 0
                && Policy(otherSession) == otherPolicy,
                "Late confirmation after real shutdown changed durable consent or redirected into the other session.");
            Console.WriteLine("EXPERIMENTAL_OPEN_DIALOG_EDGES PASS: real selected-build invalidation, foreign-Host target rejection, existing strict Watch during partial consent and real StopAsync before late confirm; no in-place owner/session replacement or native-focus acceptance is claimed.");
        }
        finally
        {
            if (File.Exists(retainedSettings))
            {
                if (Directory.Exists(settings)) Directory.Delete(settings);
                File.Move(retainedSettings, settings);
            }
            foreach (var modal in Dialogs().ToArray()) modal.Close();
            window.Close();
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await Program.WaitUntilAsync(() => !window.IsVisible);
        }
    }

    private static SourceMediaIntegrityPolicyView Policy(DesktopSessionHost host) => host.GetSourceMediaIntegrityPolicies()
        .Single(row => row.Snapshot.PluginOwnerId == Owner && row.Snapshot.SourceId == Source);
    private static IEnumerable<ExperimentalActivationDialog> Dialogs() =>
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.OfType<ExperimentalActivationDialog>().Where(window => window.IsVisible);
    private static async Task<ExperimentalActivationDialog> WaitDialogAsync()
    {
        await Program.WaitUntilAsync(() => Dialogs().Any());
        return Dialogs().Single();
    }
    private static T C<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Control control) => SmokeActionClick.Execute(control);
    private static bool InWindow(Window window, Control control)
    {
        Rect value = new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window) ?? Matrix.Identity);
        return value.Width > 0 && value.Height > 0 && value.X >= -1 && value.Y >= -1
            && value.Right <= window.Bounds.Width + 1 && value.Bottom <= window.Bounds.Height + 1;
    }
    private static void AssertTextFits(TextBlock block)
    {
        Require(block.IsEffectivelyVisible && block.Bounds.Width > 0 && block.Bounds.Height > 0,
            "Expected activation text is not visible: " + block.Name);
        Require(block.TextLayout.Height <= block.Bounds.Height + 1 && block.TextLayout.Width <= block.Bounds.Width + 1,
            "Activation text is clipped at the minimum size: " + block.Name);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
