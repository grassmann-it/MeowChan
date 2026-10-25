using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

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
        ContentControl sourcesHost = C<ContentControl>(window, "SourcesPageHost"), pluginsHost = C<ContentControl>(window, "PluginsPageHost");
        object? retainedSources = sourcesHost.Content, retainedPlugins = pluginsHost.Content;
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
        PluginsPage plugins = new();
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
            navigatedSource = source; Navigate(window, "nav.sources"); sources.SelectSource(source);
        };
        sourcesHost.Content = sources; pluginsHost.Content = plugins;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            Texts.SetLanguage("en"); Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Navigate(window, "nav.sources"); sources.SelectSource(PluginUxFixture.SourceId);
            CheckBox permission = C<CheckBox>(sources, "SourceAllowProviderMismatch");
            await RevealAsync(window, permission);
            Require(permission.IsChecked == false && C<TextBlock>(sources, "SourceExperimental").IsVisible,
                "Experimental metadata unexpectedly enabled the source exception.");
            await ToggleAsync(window, sources, true);
            Require(first.GetSnapshot().AllowProviderSizeMd5Mismatch && !second.GetSnapshot().AllowProviderSizeMd5Mismatch,
                "Allowing the first source changed another source or did not publish immediately.");
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

            await SelectSourceCardAsync(window, sources, 0);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before = invocations.Count;
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && invocations.Count == before + 1 && invocations[^1].Source == PluginUxFixture.SourceId && invocations[^1].Allow,
                "The pending action did not freeze its original source and intent.");
            // Programmatic selection/refresh models an external source projection change while saving.
            sources.SelectSource(OtherSource); sources.Refresh(providers, session.GetView(), true);
            pending.SetResult(); pending = null;
            await Program.WaitUntilAsync(() => !sources.IsBusy);
            Require(first.GetSnapshot().AllowProviderSizeMd5Mismatch && second.GetSnapshot().AllowProviderSizeMd5Mismatch
                && C<TextBlock>(sources, "SourceHeading").Text == providers[1].DisplayName && permission.IsChecked == true,
                "Selection or refresh retargeted a pending save or rendered the wrong source's consent.");
            Observation(window, sources, "selection-switch-during-save", new { action = invocations[^1], first = first.GetView(), second = second.GetView() });

            sources.SelectSource(PluginUxFixture.SourceId); failSave = true;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && !invocations[^1].Allow, "The controlled late failure did not capture the revoke intent.");
            sources.SelectSource(OtherSource);
            pending.SetResult(); pending = null;
            await Program.WaitUntilAsync(() => !sources.IsBusy);
            Require(first.GetView().IsGranted && second.GetView().IsGranted && permission.IsChecked == true
                && !C<Ursa.Controls.Banner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible
                && !C<Ursa.Controls.Banner>(sources, "SourcesActionErrorBanner").IsVisible,
                "A late failure changed durable permission or moved an old source's error onto the new selection.");
            Observation(window, sources, "failure-after-selection-switch", new { action = invocations[^1], first = first.GetView(), second = second.GetView() });
            failSave = false;
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
                Require(badge.Text == Texts.T("sources.experimental") && scope.Text == Texts.T("sources.providerMismatchScope", providers[0].DisplayName)
                    && !scope.Text!.Contains("commit", StringComparison.OrdinalIgnoreCase), "The source scope or Experimental translation was stale or exposed an internal commit term.");
                await RevealAsync(window, badge); AssertTextFits(badge);
                Program.CaptureWindow(window, CaptureName("badge-" + cell));
                await RevealAsync(window, permission);
                Require(permission.Focus(NavigationMethod.Tab), "The source checkbox could not receive actual keyboard focus.");
                await Settle();
                Require(permission.IsFocused && permission.IsChecked == false && InViewport(window, permission),
                    "The strict source checkbox is not keyboard reachable at the actual minimum size.");
                AssertTextFits(C<TextBlock>(sources, "SourceAllowProviderMismatchLabel"));
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
                    Require(!C<Ursa.Controls.Banner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible,
                        "Switching sources retained an old source's permission error.");
                }

                Navigate(window, "nav.plugins"); plugins.Select(PluginUxFixture.SourceKey);
                await Settle();
                Require(C<Border>(plugins, "PluginExperimentalChip").IsVisible
                    && C<TextBlock>(plugins, "PluginExperimental").Text == Texts.T("plugins.experimental")
                    && C<TextBlock>(plugins, "PluginDescriptionOrigin").IsVisible,
                    "Typed Experimental or its description origin was missing from the real plugin details.");
                TextBlock pluginBadge = C<TextBlock>(plugins, "PluginExperimental");
                await RevealAsync(window, pluginBadge); AssertTextFits(pluginBadge);
                Program.CaptureWindow(window, CaptureName("plugin-badge-" + cell));
                Button manage = C<Button>(plugins, "PluginWatches");
                await RevealAsync(window, manage);
                Require(manage.IsEffectivelyEnabled && manage.Focus(NavigationMethod.Tab), "The real Manage in Sources link could not receive focus.");
                await Settle();
                Require(manage.IsFocused && InViewport(window, manage)
                    && Avalonia.Automation.AutomationProperties.GetName(manage) == Texts.T("plugins.watches"),
                    "The Manage in Sources link was not translated or keyboard reachable.");
                Program.CaptureWindow(window, CaptureName("plugin-link-" + cell));
                await NativeFeatureCheckpoint.HoldAsync("source-plugin-link", plugins, manage, manage);
                navigatedSource = null;
                await ActivateAsync(manage);
                await Program.WaitUntilAsync(() => navigatedSource == PluginUxFixture.SourceId && sources.IsEffectivelyVisible);
                Require(C<TextBlock>(sources, "SourceHeading").Text == providers[0].DisplayName,
                    "The frozen plugin source action navigated to a different source.");
                Observation(window, sources, "plugin-link-navigation", new { cell, source = navigatedSource, expectedScale });
            }
            Require(fixture.ForbiddenCalls == 0, "The passive GUI fixture unexpectedly compiled, described, started or probed a plugin.");
            Require(!first.GetView().IsGranted && !second.GetView().IsGranted, "A passive presentation cell changed saved consent.");
            Console.WriteLine("SOURCE_INTEGRITY_GUI PASS: real pages and Host persistence; independent allow/revoke, failure rollback, frozen pending targets; twelve EN/DE Dark/Light B/S/A cells at actual scale " + expectedScaleText
                + ". Routed keyboard events are synthetic; no OS keyboard, provider HTTP or media-commit proof is claimed.");
        }
        finally
        {
            pending?.TrySetResult(); first.SetActive(false); second.SetActive(false);
            sources.EndLifetime(); plugins.EndLifetime();
            sourcesHost.Content = retainedSources; pluginsHost.Content = retainedPlugins;
            Texts.SetLanguage(initial.Language); Application.Current.RequestedThemeVariant = originalTheme;
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = originalWidth; window.Height = originalHeight;
        }
    }

    private static async Task AssertLocalSaveFailureVisibleAsync(MainWindow window, SourcesPage page, bool expectedSaved)
    {
        TextBlock error = C<TextBlock>(page, "SourceMediaIntegrityError");
        // Only observe native layout/viewport state. Product code owns any BringIntoView.
        await Program.WaitUntilAsync(() => error.IsEffectivelyVisible && InViewport(window, error));
        Require(C<Ursa.Controls.Banner>(page, "SourceMediaIntegrityErrorBanner").IsVisible
            && error.Text == Texts.T("sources.providerMismatchSaveFailed", PersistenceFailure)
            && C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == expectedSaved
            && !C<Ursa.Controls.Banner>(page, "SourcesActionErrorBanner").IsVisible
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
    private static async Task SelectSourceCardAsync(MainWindow window, SourcesPage page, int index)
    {
        Button card = C<StackPanel>(page, "SourceCards").Children.OfType<Button>().ElementAt(index);
        if (!card.IsEffectivelyVisible) await ActivateAsync(C<Button>(page, "SourceBackButton"));
        await RevealAsync(window, card); await ActivateAsync(card);
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
    private static void Navigate(MainWindow window, string key) => window.GetLogicalDescendants().OfType<MenuItem>()
        .First(item => Equals(item.Tag, key)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
    private static void AssertTextFits(TextBlock text) => Require(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 1
        && text.TextLayout.Height <= text.Bounds.Height + 1, "Changed source text is clipped: " + text.Name);
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
