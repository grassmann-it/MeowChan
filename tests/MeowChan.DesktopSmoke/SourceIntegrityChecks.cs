using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Narrow UI checks over real pages and the real Host consent command. The two
/// source authorities and plugin descriptions are controlled fixture data; no
/// provider, compiler, worker, HTTP request or media acceptance is exercised here.
/// </summary>
internal static class SourceIntegrityChecks
{
    private const string OtherSource = "integrity-other-source";
    private const string PersistenceFailure = "Controlled fixture persistence failure";
    private sealed record SaveInvocation(string Owner, string Source, bool Allow, long Revision);

    internal static async Task RunAsync(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_EXPERIMENTAL_ACTIVATION_GUI_ONLY") == "1")
        {
            await ExperimentalActivationChecks.RunAsync(window);
            return;
        }
        string expectedScaleText = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_INTEGRITY_EXPECT_SCALE")
            ?? throw new InvalidOperationException("Specify MEOWCHAN_SOURCE_INTEGRITY_EXPECT_SCALE as 1, 1.5 or 2 for this GUI slice.");
        double expectedScale = double.Parse(expectedScaleText, CultureInfo.InvariantCulture);
        Require(new[] { 1d, 1.5d, 2d }.Contains(expectedScale) && Math.Abs(window.RenderScaling - expectedScale) < 0.025,
            "The actual native window scale does not match the requested source integrity cell.");
        string scaleKey = expectedScale.ToString("0.0##", CultureInfo.InvariantCulture).Replace('.', '_');
        string CaptureName(string scene) => "smoke-source-integrity-" + scaleKey + "-" + scene + ".png";
        DesktopSessionHost session = window.SessionHost;
        Config initial = session.Config.Clone();
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        double originalWidth = window.Width, originalHeight = window.Height;
        ContentControl sourcesHost = C<ContentControl>(window, "SourcesPageHost");
        object? retainedSources = sourcesHost.Content;
        SourceMediaIntegrityPolicyService first = session.GetOrCreateSourceMediaIntegrityPolicy(PluginUxFixture.SourcePluginId, PluginUxFixture.SourceId);
        SourceMediaIntegrityPolicyService second = session.GetOrCreateSourceMediaIntegrityPolicy(PluginUxFixture.SourcePluginId, OtherSource);
        // Fixture activation is explicit. The separate live-host chain verifies production adapter ownership.
        first.SetActive(true); second.SetActive(true);
        Require(!first.GetView().IsGranted && !second.GetView().IsGranted, "The isolated fixture did not start strict for both sources.");
        List<SaveInvocation> invocations = new();
        TaskCompletionSource? pending = null;
        bool failSave = false;
        IReadOnlyList<SourceMediaIntegrityPolicyView> Policies() =>
            new[] { first.GetView(SourceMaturity.Experimental), second.GetView(SourceMaturity.Unspecified) };
        async Task SaveAsync(string owner, string source, bool allow, long revision)
        {
            invocations.Add(new(owner, source, allow, revision));
            Task? wait = pending?.Task;
            if (wait != null) await wait;
            if (failSave) throw new IOException(PersistenceFailure);
            await session.SetSourceMediaIntegrityPermissionAsync(owner, source, allow, revision);
        }
        ProviderDescriptor[] providers =
        [
            new(PluginUxFixture.SourceId, "Experimental discussion source", new("https://pux.example.test/"),
                ProviderCapabilities.ThreadWatch | ProviderCapabilities.OriginalMedia, ["pux.example.test"], "fixture", true),
            new(OtherSource, "Independent discussion source", new("https://other.example.test/"),
                ProviderCapabilities.ThreadWatch | ProviderCapabilities.OriginalMedia, ["other.example.test"], "fixture", true)
        ];
        SourcesPage sources = new() { GetSourceMediaIntegrityPolicies = Policies, SetSourceMediaIntegrityPermission = SaveAsync };
        sources.Refresh(providers, session.GetView(), true);
        PluginsPage plugins = sources.PluginManagement;
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(session.ProfileDirectory, "source-integrity-ui-fixture"));
        var description = fixture.Source.Description!;
        var observation = fixture.Source with { Description = description with
        {
            Sources = description.Sources.Select(source => source with { Maturity = SourceMaturity.Experimental }).ToArray()
        } };
        var profile = await fixture.Store.ReadAsync();
        await fixture.Store.ObserveCandidateAsync(observation, profile.Revision);
        plugins.Attach(fixture.Services);
        string? navigatedSource = null;
        plugins.OpenSourceWatches = source =>
        {
            navigatedSource = source; sources.SelectSource(source);
        };
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            Texts.SetLanguage("en"); Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            double wideWidth = window.Screens.ScreenFromWindow(window) is { } screen
                ? Math.Min(1200, Math.Floor(screen.WorkingArea.Width / window.RenderScaling
                    - Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width)))
                : 0;
            if (wideWidth >= 1024)
            {
                window.WindowState = WindowState.Normal;
                window.Width = wideWidth;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - wideWidth) <= 1);
            }
            sourcesHost.Content = sources;
            Navigate(window, "nav.sources"); sources.SelectSource(PluginUxFixture.SourceId);
            CheckBox permission = C<CheckBox>(sources, "SourceAllowProviderMismatch");
            await RevealAsync(window, permission);
            if (wideWidth >= 1024) await CheckSourceResizeAsync(window, sources, permission, wideWidth);
            Require(permission.IsChecked == false && C<TextBlock>(sources, "SourceExperimental").IsVisible,
                "Experimental metadata unexpectedly enabled the source exception.");
            AssertImmediatePermissionHint(sources, permission);
            await ToggleAsync(window, sources, true);
            Require(first.GetSnapshot().AllowProviderSizeMd5Mismatch && !second.GetSnapshot().AllowProviderSizeMd5Mismatch,
                "Allowing the first source changed another source or did not publish immediately.");
            first.SetActive(false);
            sources.Refresh(providers, session.GetView(), true);
            Require(!first.GetSnapshot().AllowProviderSizeMd5Mismatch && permission.IsChecked == true
                && !C<TextBlock>(sources, "SourceMediaIntegrityTiming").IsEffectivelyVisible
                && string.IsNullOrEmpty(AutomationProperties.GetHelpText(permission))
                && C<TextBlock>(sources, "SourceMediaIntegrityStatus").Text == Texts.T("sources.providerMismatchInactive"),
                "An inactive source presented a saved permission as taking effect immediately.");
            first.SetActive(true);
            sources.Refresh(providers, session.GetView(), true);
            sources.SelectSource(OtherSource); await ToggleAsync(window, sources, true);
            sources.SelectSource(PluginUxFixture.SourceId); await ToggleAsync(window, sources, false);
            Require(!first.GetSnapshot().AllowProviderSizeMd5Mismatch && second.GetSnapshot().AllowProviderSizeMd5Mismatch,
                "Revoking the first source changed the independent second source.");
            Config durable = Config.LoadConfig(session.ProfileDirectory);
            Require(!Saved(durable, PluginUxFixture.SourceId) && Saved(durable, OtherSource),
                "The actual host command did not persist independent consent values.");
            Observation(window, sources, "independent-allow-revoke", new { invocations = invocations.ToArray(), first = first.GetView(), second = second.GetView() });

            sources.SelectSource(OtherSource); failSave = true;
            await ToggleAsync(window, sources, false, expectedAfter: true);
            Require(second.GetSnapshot().AllowProviderSizeMd5Mismatch,
                "The failed save changed the actual source consent.");
            await AssertLocalSaveFailureVisibleAsync(window, sources, expectedSaved: true);
            Program.CaptureWindow(window, CaptureName("save-failure"));
            failSave = false;

            await SelectSourceCardAsync(window, sources, providers[0].DisplayName);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before = invocations.Count;
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && invocations.Count == before + 1 && invocations[^1].Source == PluginUxFixture.SourceId && invocations[^1].Allow,
                "The pending action did not freeze its original source and intent.");
            // Programmatic selection/refresh models an external source projection change while saving.
            sources.SelectSource(OtherSource);
            Require(C<TextBlock>(sources, "SourceHeading").Text == providers[0].DisplayName,
                "A pending consent save allowed an interactive selection change.");
            sources.Refresh(providers.Where(provider => provider.SourceId != PluginUxFixture.SourceId).ToArray(), session.GetView(), true);
            pending.SetResult(); pending = null;
            await Program.WaitUntilAsync(() => !sources.IsBusy);
            Require(first.GetSnapshot().AllowProviderSizeMd5Mismatch && second.GetSnapshot().AllowProviderSizeMd5Mismatch
                && C<TextBlock>(sources, "SourceHeading").Text == providers[1].DisplayName && permission.IsChecked == true,
                "Selection or refresh retargeted a pending save or rendered the wrong source's consent.");
            Observation(window, sources, "selection-switch-during-save", new { action = invocations[^1], first = first.GetView(), second = second.GetView() });

            sources.Refresh(providers, session.GetView(), true);
            sources.SelectSource(PluginUxFixture.SourceId); failSave = true;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && !invocations[^1].Allow, "The controlled late failure did not capture the revoke intent.");
            sources.Refresh(providers.Where(provider => provider.SourceId != PluginUxFixture.SourceId).ToArray(), session.GetView(), true);
            pending.SetResult(); pending = null;
            await Program.WaitUntilAsync(() => !sources.IsBusy);
            Require(first.GetView().IsGranted && second.GetView().IsGranted && permission.IsChecked == true
                && !C<MeowChan.Desktop.Controls.WorkspaceBanner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible
                && !C<MeowChan.Desktop.Controls.WorkspaceBanner>(sources, "SourcesActionErrorBanner").IsVisible,
                "A late failure changed durable permission or moved an old source's error onto the new selection.");
            Observation(window, sources, "failure-after-selection-switch", new { action = invocations[^1], first = first.GetView(), second = second.GetView() });
            failSave = false;
            sources.Refresh(providers, session.GetView(), true);
            // Leave both controls strict before the passive presentation matrix.
            sources.SelectSource(PluginUxFixture.SourceId); await ToggleAsync(window, sources, false);
            sources.SelectSource(OtherSource); await ToggleAsync(window, sources, false);
            await plugins.ActivateAsync();
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                Texts.SetLanguage(language); Application.Current.RequestedThemeVariant = theme;
                double width = mode == "Barebones" ? 640 : 760, height = mode == "Barebones" ? 480 : 500;
                window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height)
                    && window.ActualThemeVariant == theme && Texts.Language == language);
                Require(Math.Abs(window.RenderScaling - expectedScale) < 0.025, "Native render scale changed during the matrix.");
                string cell = mode.ToLowerInvariant() + "-" + language + "-" + theme.Key!.ToString()!.ToLowerInvariant();
                Navigate(window, "nav.sources");
                sources.Refresh(providers, session.GetView(), true); sources.SelectSource(PluginUxFixture.SourceId);
                TextBlock badge = C<TextBlock>(sources, "SourceExperimental"), scope = C<TextBlock>(sources, "SourceMediaIntegrityScope");
                TextBlock meta = C<TextBlock>(sources, "SourceMeta");
                // The meta line is always complete: unverified maintenance (no trusted evidence in this fixture) plus the declared
                // Experimental maturity. The sentence below it explains "Experimental" and is shown for that maturity only.
                string expectedMeta = SourceWording.DescribeMeta(SourceWording.Badges(new SourceWordingClassification(PluginUxFixture.SourceId,
                    providers[0].DisplayName, SourceWordingMaintenance.Unknown, SourceWordingMaturity.Experimental), isActive: true, isBuiltIn: false));
                Require(meta.Text == expectedMeta && meta.Text!.Contains(Texts.T("sources.maturity.experimental"), StringComparison.Ordinal)
                    && badge.IsVisible && badge.Text == Texts.T("sources.experimentalNote")
                    && scope.Text == Texts.T("sources.providerMismatchScope", providers[0].DisplayName)
                    && !scope.Text!.Contains("commit", StringComparison.OrdinalIgnoreCase), "The source scope, meta line or Experimental note was stale or exposed an internal commit term.");
                AssertTextFits(meta);
                await RevealAsync(window, badge); AssertTextFits(badge);
                Program.CaptureWindow(window, CaptureName("badge-" + cell));
                await RevealAsync(window, permission);
                Require(permission.Focus(NavigationMethod.Tab), "The source checkbox could not receive actual keyboard focus.");
                await Settle();
                Require(permission.IsFocused && permission.IsChecked == false && InViewport(window, permission),
                    "The strict source checkbox is not keyboard reachable at the actual minimum size.");
                AssertTextFits(C<TextBlock>(sources, "SourceAllowProviderMismatchLabel"));
                AssertImmediatePermissionHint(sources, permission);
                Program.CaptureWindow(window, CaptureName("policy-focus-" + cell));
                await NativeFeatureCheckpoint.HoldAsync("source-policy", sources, permission, permission);
                if (!InViewport(window, scope))
                {
                    await RevealAsync(window, scope);
                    Program.CaptureWindow(window, CaptureName("scope-" + cell));
                }
                AssertTextFits(scope);
                Observation(window, sources, "source-presentation", new { cell, expectedScale, badge = State(window, badge), checkbox = State(window, permission), scope = State(window, scope) });
                if (mode == "Barebones")
                {
                    failSave = true;
                    await ToggleAsync(window, sources, true, expectedAfter: false);
                    // No test reveal, scroll or layout nudge after the action: the controller must expose the error.
                    await AssertLocalSaveFailureVisibleAsync(window, sources, expectedSaved: false);
                    Require(!first.GetView().IsGranted, "The minimum-size save failure changed actual consent.");
                    Program.CaptureWindow(window, CaptureName("save-failure-" + cell));
                    Observation(window, sources, "minimum-save-failure", new { cell, expectedScale,
                        error = State(window, C<TextBlock>(sources, "SourceMediaIntegrityError")),
                        checkbox = State(window, permission), offset = C<ScrollViewer>(sources, "SourcesScroll").Offset.ToString() });
                    failSave = false;
                    sources.SelectSource(OtherSource); sources.SelectSource(PluginUxFixture.SourceId);
                    Require(!C<MeowChan.Desktop.Controls.WorkspaceBanner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible,
                        "Switching sources retained an old source's permission error.");
                }

                sources.SelectSource(PluginUxFixture.SourceId);
                Button pluginDetails = C<Button>(sources, "SourcePluginButton");
                await RevealAsync(window, pluginDetails);
                await ActivateAsync(pluginDetails);
                Require(plugins.ShowingSourcePluginDetails && plugins.Filter == SourcesPluginsFilter.Sources,
                    "Plugin details did not open as the second layer of the selected source.");
                await Settle();
                // The chip carries one compact classification per described source (unverified maintenance plus
                // the typed Experimental maturity), not the bare maturity word.
                string pluginClassification = SourceWording.DescribeCompact(observation.Description!.Sources
                    .Select(source => new SourceWordingClassification(source.SourceId, source.Name,
                        SourceWordingMaintenance.Unknown, SourceWordingMaturity.Experimental)).ToArray());
                Require(C<Border>(plugins, "PluginExperimentalChip").IsVisible
                    && C<TextBlock>(plugins, "PluginExperimental").Text == pluginClassification
                    && pluginClassification.Contains(Texts.T("sources.maturity.experimental"), StringComparison.Ordinal)
                    && C<TextBlock>(plugins, "PluginDescriptionOrigin").IsVisible,
                    "Typed Experimental or its description origin was missing from the real plugin details.");
                TextBlock pluginBadge = C<TextBlock>(plugins, "PluginExperimental");
                await RevealAsync(window, pluginBadge); AssertTextFits(pluginBadge);
                Program.CaptureWindow(window, CaptureName("plugin-badge-" + cell));
                Button manage = C<Button>(plugins, "PluginWatches");
                await RevealAsync(window, manage);
                Require(manage.IsEffectivelyEnabled && manage.Focus(NavigationMethod.Tab), "The real Show source link could not receive focus.");
                await Settle();
                Require(manage.IsFocused && InViewport(window, manage)
                    && Avalonia.Automation.AutomationProperties.GetName(manage) == Texts.T("plugins.watches"),
                    "The Show source link was not translated or keyboard reachable.");
                Program.CaptureWindow(window, CaptureName("plugin-link-" + cell));
                await NativeFeatureCheckpoint.HoldAsync("source-plugin-link", plugins, manage, manage);
                navigatedSource = null;
                await ActivateAsync(manage);
                await Program.WaitUntilAsync(() => navigatedSource == PluginUxFixture.SourceId && sources.IsEffectivelyVisible);
                Require(window.SelectedPage == MainWindow.WorkspacePage.Sources && ReferenceEquals(sourcesHost.Content, sources)
                    && !plugins.ShowingSourcePluginDetails && C<TextBlock>(sources, "SourceHeading").IsEffectivelyVisible
                    && C<TextBlock>(sources, "SourceHeading").Text == providers[0].DisplayName,
                    "The frozen plugin source action navigated to a different source.");
                Observation(window, sources, "plugin-link-navigation", new { cell, source = navigatedSource, expectedScale });
            }
            await CheckPluginTimingAsync(sources, plugins, fixture);
            Require(fixture.ForbiddenCalls == 0, "The passive GUI fixture unexpectedly compiled, described, started or probed a plugin.");
            Require(!first.GetView().IsGranted && !second.GetView().IsGranted, "A passive presentation cell changed saved consent.");
            Console.WriteLine("SOURCE_INTEGRITY_GUI PASS: real pages and Host persistence; independent allow/revoke, failure rollback, frozen pending targets; twelve EN/DE Dark/Light B/S/A cells at actual scale " + expectedScaleText
                + ". Routed keyboard events are synthetic; no OS keyboard, provider HTTP or media-commit proof is claimed.");
        }
        finally
        {
            pending?.TrySetResult(); first.SetActive(false); second.SetActive(false);
            sources.EndLifetime();
            sourcesHost.Content = retainedSources;
            Texts.SetLanguage(initial.Language); Application.Current.RequestedThemeVariant = originalTheme;
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = originalWidth; window.Height = originalHeight;
        }
    }

    private static async Task CheckSourceResizeAsync(MainWindow window, SourcesPage page, CheckBox permission, double wideWidth)
    {
        Grid split = C<Grid>(page, "SourcesSplit");
        Control list = C<StackPanel>(page, "SourcesListPane");
        Control detail = C<Border>(page, "SourceDetailsPanel");
        double Gap() => detail.TranslatePoint(default, split)!.Value.X
            - list.TranslatePoint(default, split)!.Value.X - list.Bounds.Width;

        await Program.WaitUntilAsync(() => list.IsEffectivelyVisible && detail.IsEffectivelyVisible && Gap() > 0);
        double initialGap = Gap();
        Require(permission.Focus(NavigationMethod.Tab), "The permission checkbox could not take focus in the wide layout.");
        window.Width = 760;
        await Program.WaitUntilAsync(() => !list.IsEffectivelyVisible && detail.IsEffectivelyVisible);
        await Settle();
        Require(permission.IsFocused && Math.Abs(detail.TranslatePoint(default, split)!.Value.X) <= 1,
            "The narrow source pane lost focus or retained an empty column.");

        window.Width = wideWidth;
        await Program.WaitUntilAsync(() => list.IsEffectivelyVisible && detail.IsEffectivelyVisible && Gap() > 0);
        await Settle();
        Require(permission.IsFocused && Math.Abs(Gap() - initialGap) <= 1,
            $"The restored layout lost focus or changed the gap from {initialGap:0.##} DIP to {Gap():0.##} DIP.");
    }

    private static void AssertImmediatePermissionHint(SourcesPage sources, CheckBox permission)
    {
        TextBlock timing = C<TextBlock>(sources, "SourceMediaIntegrityTiming");
        Require(permission.IsLogicalAncestorOf(timing) && timing.IsEffectivelyVisible
            && timing.Text == Texts.T("sources.providerMismatchImmediate")
            && AutomationProperties.GetName(permission) == Texts.T("sources.allowProviderMismatch")
            && AutomationProperties.GetHelpText(permission) == timing.Text,
            "The source permission did not present its immediate effect in current-language visible and accessible text.");
        AssertTextFits(timing);
    }

    private static async Task CheckPluginTimingAsync(SourcesPage sources, PluginsPage plugins, PluginUxFixture fixture)
    {
        await sources.ShowPlugin(PluginUxFixture.SourceId);
        var before = await fixture.Store.ReadAsync();
        var active = before.Plugins.Single(plugin => plugin.PluginId == PluginUxFixture.SourcePluginId).Active!;
        var savedProfile = await fixture.StageSettingsAsync();
        await plugins.RefreshAsync();
        var saved = savedProfile.Plugins.Single(plugin => plugin.PluginId == PluginUxFixture.SourcePluginId);
        Require(PluginAddChecks.NextLine(plugins) == Texts.T("plugins.step.next.saved")
            && saved.Active?.Configuration.Revision == active.Configuration.Revision && saved.Pending is { } settings
            && settings.ExecutionRevision == active.ExecutionRevision && settings.Configuration.Revision != active.Configuration.Revision,
            "Saved plugin settings did not show a restart hint and keep the running configuration.");

        await fixture.AddSecondSourceFileAsync();
        var profile = await fixture.Store.ReadAsync();
        await fixture.Store.SelectCandidateAsync(PluginUxFixture.SourcePluginId, PluginUxFixture.SecondSourceKey, profile.Revision);
        await plugins.RefreshAsync();
        foreach (string key in new[] { PluginUxFixture.SourceKey, PluginUxFixture.SecondSourceKey })
        {
            plugins.Select(key);
            await Settle();
            Require(PluginAddChecks.NextLine(plugins) == Texts.T("plugins.step.next.restartSaved"),
                "The running or scheduled candidate did not identify the saved build as waiting for restart.");
        }
        var scheduledProfile = await fixture.Store.ReadAsync();
        var scheduled = scheduledProfile.Plugins.Single(plugin => plugin.PluginId == PluginUxFixture.SourcePluginId);
        Require(scheduled.Active?.CandidateId == PluginUxFixture.SourceKey
            && scheduled.Pending?.CandidateId == PluginUxFixture.SecondSourceKey,
            "Scheduling a candidate changed the active build before restart.");

        plugins.CurrentDraft!.Plugin.Set("label", "Unsaved label");
        Require(C<TextBlock>(plugins, "PluginDraftStatus").Text == Texts.T("plugins.draft")
            && C<TextBlock>(plugins, "PluginDraftSummary").IsEffectivelyVisible
            && PluginAddChecks.NextLine(plugins) == Texts.T("plugins.step.next.restartSaved")
            && (await fixture.Store.ReadAsync()).Revision == scheduledProfile.Revision,
            "The unsaved plugin draft did not keep its own timing while leaving the scheduled build intact.");
    }

    private static async Task AssertLocalSaveFailureVisibleAsync(MainWindow window, SourcesPage page, bool expectedSaved)
    {
        TextBlock error = C<TextBlock>(page, "SourceMediaIntegrityError");
        // Only observe native layout/viewport state. Product code owns any BringIntoView.
        await Program.WaitUntilAsync(() => error.IsEffectivelyVisible && InViewport(window, error));
        Require(C<MeowChan.Desktop.Controls.WorkspaceBanner>(page, "SourceMediaIntegrityErrorBanner").IsVisible
            && error.Text == Texts.T("sources.providerMismatchSaveFailed", PersistenceFailure)
            && C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == expectedSaved
            && !C<MeowChan.Desktop.Controls.WorkspaceBanner>(page, "SourcesActionErrorBanner").IsVisible
            && C<TextBlock>(page, "SourcesActionError").Text?.Contains(PersistenceFailure, StringComparison.Ordinal) != true,
            "The save failure is not local, translated, visible with actual saved state, or is duplicated under Watches.");
        AssertTextFits(error);
    }

    private static bool Saved(Config config, string source) => config.SourceMediaIntegrityPermissions[
        SourceMediaIntegrityPermission.CreateKey(PluginUxFixture.SourcePluginId, source)].AllowProviderSizeMd5Mismatch;
    private static async Task ToggleAsync(MainWindow window, SourcesPage page, bool intended, bool? expectedAfter = null)
    {
        Require(C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked != intended, "The test did not start from the opposite checkbox state.");
        await ActivatePermissionAsync(window, page);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == (expectedAfter ?? intended), "The permission control did not render the saved state after completion.");
    }
    // The list is sorted by maturity, so a card is found by its name, never by its position.
    private static async Task SelectSourceCardAsync(MainWindow window, SourcesPage page, string name)
    {
        SelectionListItem card = C<SelectionList>(page, "SourceCards").Items.OfType<SelectionListItem>().Single(candidate =>
            candidate.Content is StackPanel labels && labels.Children.OfType<TextBlock>().First().Text == name);
        if (!card.IsEffectivelyVisible) await ActivateAsync(C<Button>(page, "SourceBackButton"));
        await RevealAsync(window, card); MergedSourcesSmoke.OpenSourceRow(page, card); await Settle();
    }
    private static async Task ActivatePermissionAsync(MainWindow window, SourcesPage page)
    {
        CheckBox checkbox = C<CheckBox>(page, "SourceAllowProviderMismatch");
        await RevealAsync(window, checkbox);
        Require(checkbox.IsEffectivelyEnabled, "The fixture's permission checkbox is disabled.");
        await ActivateAsync(checkbox);
    }
    private static async Task ActivateAsync(Control control)
    {
        Require(control.Focus(NavigationMethod.Tab), "The actual control refused focus: " + control.Name);
        Key key = control is CheckBox ? Key.Space : Key.Enter;
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = control, Key = key });
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = control, Key = key });
        await Settle();
    }
    // F1: the pages are footer buttons or entries of the footer's More menu, no longer entries of the ⋯ menu.
    private static void Navigate(MainWindow window, string key) => SmokeNavigation.GoTo(window, key switch
    {
        "nav.sources" => MainWindow.WorkspacePage.Sources,
        "nav.rules" => MainWindow.WorkspacePage.Rules,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a page this fixture opens.")
    });
    private static async Task RevealAsync(MainWindow window, Control control)
    {
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); control.BringIntoView(); window.UpdateLayout(); return InViewport(window, control); });
        await Settle();
    }
    private static bool InViewport(Window window, Control target)
    {
        if (!target.IsAttachedToVisualTree() || !target.IsEffectivelyVisible || !ReferenceEquals(TopLevel.GetTopLevel(target), window)
            || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return false;
        Point? position = target.TranslatePoint(default, window);
        if (position is not { } p || p.X < -1 || p.Y < -1 || p.X + target.Bounds.Width > window.ClientSize.Width + 1
            || p.Y + target.Bounds.Height > window.ClientSize.Height + 1) return false;
        return target.GetVisualAncestors().OfType<ScrollViewer>().All(scroll => target.TranslatePoint(default, scroll) is { } point
            && point.X >= -1 && point.Y >= -1 && point.X + target.Bounds.Width <= scroll.Viewport.Width + 1
            && point.Y + target.Bounds.Height <= scroll.Viewport.Height + 1);
    }
    // The widest line's glyph extent: the break space hanging after a wrapped line is neither drawn nor clipped.
    private static void AssertTextFits(TextBlock text) => Require(text.TextLayout.TextLines.Max(line => line.Width) <= text.Bounds.Width + 1
        && text.TextLayout.Height <= text.Bounds.Height + 1, "Changed source text is clipped: " + text.Name
        + $" bounds={text.Bounds.Size} layout={text.TextLayout.WidthIncludingTrailingWhitespace}x{text.TextLayout.Height}"
        + $" lines={string.Join("/", text.TextLayout.TextLines.Select(line => line.Width.ToString("0.#")))} text='{text.Text}'");
    private static object State(Window window, Control control) => new { control.Name, bounds = control.Bounds.ToString(),
        origin = control.TranslatePoint(default, window)?.ToString(), control.IsEffectivelyVisible, control.IsEffectivelyEnabled,
        control.IsFocused, inViewport = InViewport(window, control), text = (control as TextBlock)?.Text };
    private static void Observation(MainWindow window, SourcesPage page, string task, object data) => Console.WriteLine("SOURCE_INTEGRITY_GUI "
        + JsonSerializer.Serialize(new { task, sourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_DIGEST"),
            language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode, theme = window.ActualThemeVariant.Key?.ToString(),
            window.RenderScaling, client = window.ClientSize.ToString(), source = C<TextBlock>(page, "SourceHeading").Text,
            actualPolicies = window.SessionHost.Config.SourceMediaIntegrityPermissions.Values.ToArray(), data }));
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static T C<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing fixture control: " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
