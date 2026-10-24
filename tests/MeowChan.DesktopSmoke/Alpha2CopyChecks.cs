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
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Real Sources/Rules controls in the isolated smoke window. Consent changes use
/// the Host command; no adapter, media transfer or HTTP is started. These checks
/// exercise visibility, source identity, viewport geometry and retained focus,
/// rather than fixing translated sentences into assertions.
/// </summary>
internal static class Alpha2CopyChecks
{
    private const string Owner = "alpha2-copy-fixture";
    private const string First = "alpha2-copy-first";
    private const string Second = "alpha2-copy-second";
    private const string NoPolicy = "alpha2-copy-no-policy";
    private const string Inactive = "alpha2-copy-inactive";
    private const string Failure = "Controlled alpha2 copy persistence failure";
    private sealed record SaveInvocation(string Owner, string Source, bool Allow, long Revision);

    internal static async Task RunAsync(MainWindow window)
    {
        string scaleText = Environment.GetEnvironmentVariable("MEOWCHAN_ALPHA2_COPY_EXPECT_SCALE")
            ?? throw new InvalidOperationException("Specify MEOWCHAN_ALPHA2_COPY_EXPECT_SCALE as 1, 1.5 or 2.");
        double scale = double.Parse(scaleText, CultureInfo.InvariantCulture);
        Require(new[] { 1d, 1.5d, 2d }.Contains(scale) && Math.Abs(window.RenderScaling - scale) < 0.025,
            "Actual native scale does not match the requested alpha2 copy cell.");
        string scaleKey = scale.ToString("0.0##", CultureInfo.InvariantCulture).Replace('.', '_');
        string CaptureName(string scene) => "smoke-alpha2-copy-" + scaleKey + "-" + scene + ".png";
        DesktopSessionHost session = window.SessionHost;
        Config initial = session.Config.Clone();
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        WindowState originalState = window.WindowState;
        double originalWidth = window.Width, originalHeight = window.Height;
        ContentControl sourcesHost = C<ContentControl>(window, "SourcesPageHost");
        ContentControl rulesHost = C<ContentControl>(window, "RulesPageHost");
        object? retainedSources = sourcesHost.Content, retainedRules = rulesHost.Content;
        SourceMediaIntegrityPolicyService first = session.GetOrCreateSourceMediaIntegrityPolicy(Owner, First);
        SourceMediaIntegrityPolicyService second = session.GetOrCreateSourceMediaIntegrityPolicy(Owner, Second);
        SourceMediaIntegrityPolicyService inactive = session.GetOrCreateSourceMediaIntegrityPolicy(Owner, Inactive);
        first.SetActive(true); second.SetActive(true); inactive.SetActive(true);
        Require(!first.GetView().IsGranted && !second.GetView().IsGranted && !inactive.GetView().IsGranted,
            "Copy fixture must start with fresh strict source authorities.");
        await session.SetSourceMediaIntegrityPermissionAsync(Owner, Inactive, true, inactive.GetSnapshot().Revision);
        inactive.SetActive(false);
        IReadOnlyList<SourceMediaIntegrityPolicyView> Policies() =>
            [first.GetView(SourceMaturity.Experimental), second.GetView(), inactive.GetView(SourceMaturity.Experimental)];
        List<SaveInvocation> saves = new();
        TaskCompletionSource? pending = null;
        bool failSave = false;
        int navigationCalls = 0, submitCalls = 0, ruleWrites = 0;
        async Task SaveAsync(string owner, string source, bool allow, long revision)
        {
            saves.Add(new(owner, source, allow, revision));
            Task? gate = pending?.Task;
            if (gate != null) await gate;
            if (failSave) throw new IOException(Failure);
            await session.SetSourceMediaIntegrityPermissionAsync(owner, source, allow, revision);
        }
        ProviderDescriptor[] providers =
        [
            Provider(First, "First discussion source"), Provider(Second, "Second discussion source"),
            Provider(NoPolicy, "Source without policy"), Provider(Inactive, "Inactive discussion source")
        ];
        SourcesPage sources = new(_ => { navigationCalls++; return false; }, _ => navigationCalls++,
            () => navigationCalls++, () => navigationCalls++, _ => { submitCalls++; return Task.FromResult(false); })
        {
            GetSourceMediaIntegrityPolicies = Policies,
            SetSourceMediaIntegrityPermission = SaveAsync,
            OpenPluginSettings = _ => navigationCalls++
        };
        MediaRuleSet ruleSet = new("alpha2-copy", Array.Empty<MediaRule>());
        RulesPage rules = new(() => ruleSet, (_, _) =>
        {
            ruleWrites++; throw new InvalidOperationException("Copy presentation must not save rules.");
        }) { IsAvailable = true };
        sourcesHost.Content = sources; rulesHost.Content = rules;
        sources.Refresh(providers, session.GetView(), true);
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            Texts.SetLanguage("en"); Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Navigate(window, "nav.sources"); sources.SelectSource(First);
            AssertSource(sources, providers[0], granted: false, active: true, hasPolicy: true);
            await ToggleAsync(window, sources, true);
            AssertSource(sources, providers[0], granted: true, active: true, hasPolicy: true);
            Require(first.GetSnapshot().AllowProviderSizeMd5Mismatch && !second.GetView().IsGranted,
                "Granting the selected source changed an independent source or failed to publish.");
            Require(saves.Count == 1 && navigationCalls == 0 && submitCalls == 0
                && window.SelectedPage == MainWindow.WorkspacePage.Sources,
                "Granting consent automatically submitted work or navigated away from Sources.");

            // A failed revoke must retain its granted hint and local saved state.
            failSave = true;
            await ToggleAsync(window, sources, false, expectedSaved: true);
            await AssertFailureAsync(window, sources, expectedSaved: true);
            AssertSource(sources, providers[0], granted: true, active: true, hasPolicy: true);
            Program.CaptureWindow(window, CaptureName("failed-revoke"));
            failSave = false;
            sources.SelectSource(Second);
            AssertSource(sources, providers[1], granted: false, active: true, hasPolicy: true);
            Require(!C<Ursa.Controls.Banner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible,
                "An old source's save error followed selection.");

            // A failed grant must never display a successful grant's retry guidance.
            failSave = true;
            await ToggleAsync(window, sources, true, expectedSaved: false);
            await AssertFailureAsync(window, sources, expectedSaved: false);
            AssertSource(sources, providers[1], granted: false, active: true, hasPolicy: true);
            Program.CaptureWindow(window, CaptureName("failed-grant"));
            failSave = false;

            // A delayed successful grant remains bound to its captured source;
            // the new strict selection must not inherit its hint or focus command.
            sources.SelectSource(First); await ToggleAsync(window, sources, false);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && saves[^1] is { Source: First, Allow: true }, "Pending grant lost its captured target.");
            sources.SelectSource(Second);
            await FinishPendingOnSelectionAsync(window, sources, pending);
            pending = null;
            AssertSource(sources, providers[1], granted: false, active: true, hasPolicy: true);
            Require(first.GetView().IsGranted && !second.GetView().IsGranted,
                "A delayed grant changed the current source rather than its original authority.");
            Observation(window, "late-success-strict-selection", new { action = saves[^1], first = first.GetView(), second = second.GetView() });

            // A delayed failure on the old source may not place an error or hint
            // onto the currently selected strict source or steal its focus.
            sources.SelectSource(First); failSave = true;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await ActivatePermissionAsync(window, sources);
            Require(sources.IsBusy && saves[^1] is { Source: First, Allow: false }, "Pending revoke lost its captured target.");
            sources.SelectSource(Second);
            await FinishPendingOnSelectionAsync(window, sources, pending);
            pending = null; failSave = false;
            AssertSource(sources, providers[1], granted: false, active: true, hasPolicy: true);
            Require(first.GetView().IsGranted && !C<Ursa.Controls.Banner>(sources, "SourceMediaIntegrityErrorBanner").IsVisible
                && !C<Ursa.Controls.Banner>(sources, "SourcesActionErrorBanner").IsVisible,
                "Late failure changed consent or attached another source's error to the selection.");
            Observation(window, "late-failure-strict-selection", new { action = saves[^1], first = first.GetView(), second = second.GetView() });

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
                Require(Math.Abs(window.RenderScaling - scale) < 0.025, "Actual scale changed during the copy matrix.");
                string cell = mode.ToLowerInvariant() + "-" + language + "-" + theme.Key!.ToString()!.ToLowerInvariant();
                Navigate(window, "nav.sources"); sources.Refresh(providers, session.GetView(), true);
                int savesBefore = saves.Count;
                foreach ((int index, bool granted, bool active, bool hasPolicy) in new[]
                {
                    (0, true, true, true), (1, false, true, true), (2, false, false, false), (3, true, false, true)
                })
                {
                    sources.SelectSource(providers[index].SourceId);
                    await Settle();
                    AssertSource(sources, providers[index], granted, active, hasPolicy);
                    if (index == 0)
                    {
                        CheckBox permission = C<CheckBox>(sources, "SourceAllowProviderMismatch");
                        await RevealAsync(window, permission);
                        Require(permission.Focus(NavigationMethod.Tab), "Granted permission refused keyboard focus.");
                        await Settle();
                        Require(permission.IsFocused && InViewport(window, permission), "Granted permission is not keyboard reachable at minimum size.");
                        Program.CaptureWindow(window, CaptureName("granted-focus-" + cell));
                        TextBlock hint = C<TextBlock>(sources, "SourceMediaIntegrityRetryHint");
                        await RevealAsync(window, hint);
                        AssertTextFits(hint);
                        Require(permission.IsFocused && !hint.Focusable
                            && InViewport(window, C<TextBlock>(sources, "SourceMediaIntegrityStatus")),
                            "Retry hint stole focus or lost its visible source-policy context.");
                        Program.CaptureWindow(window, CaptureName("granted-hint-" + cell));
                        Observation(window, "granted-hint", new { cell, source = providers[index].SourceId,
                            policy = first.GetView(), hint = State(window, hint), permission = State(window, permission) });
                    }
                    else
                    {
                        Control target = hasPolicy ? C<TextBlock>(sources, "SourceMediaIntegrityStatus") : C<TextBlock>(sources, "SourceHeading");
                        await RevealAsync(window, target);
                        if (target is TextBlock text) AssertTextFits(text);
                        Program.CaptureWindow(window, CaptureName((index == 1 ? "strict-" : index == 2 ? "no-policy-" : "inactive-") + cell));
                        Observation(window, "hidden-hint", new { cell, source = providers[index].SourceId, granted, active, hasPolicy,
                            hint = State(window, C<TextBlock>(sources, "SourceMediaIntegrityRetryHint")), context = State(window, target) });
                    }
                }
                Require(saves.Count == savesBefore && navigationCalls == 0 && submitCalls == 0,
                    "Passive source presentation changed permissions or invoked a work/navigation action.");

                Navigate(window, "nav.rules"); rules.EditScope(MediaRuleScope.Global);
                rules.RuleEnabled = true; rules.SizeMode = (int)MediaSizeLimitMode.Limited; rules.MegabytesText = "10";
                await Settle();
                TextBox sizeInput = C<TextBox>(rules, "RuleMegabytesInput");
                await RevealAsync(window, sizeInput);
                Require(sizeInput.Focus(NavigationMethod.Tab), "Rules size input refused keyboard focus.");
                await Settle();
                foreach ((string key, string value) in new[]
                {
                    ("megabytes", rules.MegabytesHint), ("future", rules.FutureHint), ("unknown", rules.UnknownHint)
                })
                {
                    // Locate the live bound text; do not duplicate its translated wording.
                    TextBlock hint = rules.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == value && text.IsEffectivelyVisible);
                    await RevealAsync(window, hint); AssertTextFits(hint);
                    Require(sizeInput.IsFocused && ReferenceEquals(sizeInput, C<TextBox>(rules, "RuleMegabytesInput"))
                        && sizeInput.Text == "10" && rules.IsEditable,
                        "Reading Rules help lost the focused draft input or replaced its editor.");
                    Program.CaptureWindow(window, CaptureName("rules-" + key + "-" + cell));
                    Observation(window, "rules-help", new { cell, key, hint = State(window, hint), input = State(window, sizeInput), ruleWrites });
                }
            }
            Require(ruleWrites == 0 && navigationCalls == 0 && submitCalls == 0 && first.GetView().IsGranted
                && !second.GetView().IsGranted && inactive.GetView() is { IsGranted: true, IsActive: false },
                "Copy matrix changed durable source/rule intent or submitted work.");
            Console.WriteLine("ALPHA2_COPY_GUI PASS: actual scale " + scaleText
                + "; twelve EN/DE Dark/Light B/S/A minimum-size cells with granted/strict/null/inactive Sources and three Rules hints; local failed grant/revoke and delayed target/focus guards. Synthetic routed keys; no native input, HTTP or media acceptance proof.");
        }
        finally
        {
            pending?.TrySetResult();
            first.SetActive(false); second.SetActive(false); inactive.SetActive(false);
            sources.EndLifetime(); rules.EndLifetime();
            sourcesHost.Content = retainedSources; rulesHost.Content = retainedRules;
            Texts.SetLanguage(initial.Language); Application.Current.RequestedThemeVariant = originalTheme;
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = originalWidth; window.Height = originalHeight; window.WindowState = originalState;
        }
    }

    private static ProviderDescriptor Provider(string id, string name) => new(id, name, new("https://alpha2-copy.example.test/"),
        ProviderCapabilities.ThreadWatch | ProviderCapabilities.OriginalMedia, ["alpha2-copy.example.test"], "fixture", true);

    private static void AssertSource(SourcesPage page, ProviderDescriptor provider, bool granted, bool active, bool hasPolicy)
    {
        Require(C<TextBlock>(page, "SourceHeading").Text == provider.DisplayName,
            "The page shows another source's identity.");
        TextBlock hint = C<TextBlock>(page, "SourceMediaIntegrityRetryHint");
        Require(C<StackPanel>(page, "SourceMediaIntegrityPanel").IsVisible == hasPolicy
            && hint.IsVisible == (hasPolicy && active && granted)
            && hint.IsEffectivelyVisible == (hasPolicy && active && granted),
            "Retry guidance is not scoped to the selected active granted policy.");
        if (!hasPolicy) return;
        string status = !active ? "sources.providerMismatchInactive" : granted ? "sources.providerMismatchAllowed" : "sources.providerMismatchStrict";
        Require(C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == granted
            && C<TextBlock>(page, "SourceMediaIntegrityScope").Text == Texts.T("sources.providerMismatchScope", provider.DisplayName)
            && C<TextBlock>(page, "SourceMediaIntegrityStatus").Text == Texts.T(status),
            "The current identity, saved permission, scope or status was overwritten by retry guidance.");
    }

    private static async Task FinishPendingOnSelectionAsync(MainWindow window, SourcesPage page, TaskCompletionSource gate)
    {
        // The current source's settings button remains enabled while a consent
        // save is pending. Drain the intentional selection-focus request first,
        // then observe whether completion displaces this new selection's focus.
        await Settle();
        Button focus = C<Button>(page, "SourcePluginButton");
        await RevealAsync(window, focus);
        Require(focus.IsEffectivelyEnabled && focus.Focus(NavigationMethod.Tab),
            "Current source settings button refused focus during the pending action.");
        await Settle();
        Require(focus.IsFocused, "Focus was not established before completing the delayed action.");
        gate.SetResult();
        await Program.WaitUntilAsync(() => !page.IsBusy);
        await Settle();
        Require(focus.IsFocused && InViewport(window, focus)
            && window.SelectedPage == MainWindow.WorkspacePage.Sources,
            "A late completion navigated away or displaced the selected source focus.");
    }

    private static async Task ToggleAsync(MainWindow window, SourcesPage page, bool intended, bool? expectedSaved = null)
    {
        Require(C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked != intended, "Permission action did not start from the opposite saved state.");
        await ActivatePermissionAsync(window, page);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == (expectedSaved ?? intended), "Permission completion rendered the wrong saved state.");
    }

    private static async Task ActivatePermissionAsync(MainWindow window, SourcesPage page)
    {
        CheckBox permission = C<CheckBox>(page, "SourceAllowProviderMismatch");
        await RevealAsync(window, permission);
        Require(permission.IsEffectivelyEnabled && permission.Focus(NavigationMethod.Tab), "Source checkbox is not keyboard reachable.");
        foreach (var routed in new[] { InputElement.KeyDownEvent, InputElement.KeyUpEvent })
            permission.RaiseEvent(new KeyEventArgs { RoutedEvent = routed, Source = permission, Key = Key.Space });
        await Settle();
    }

    private static async Task AssertFailureAsync(MainWindow window, SourcesPage page, bool expectedSaved)
    {
        TextBlock error = C<TextBlock>(page, "SourceMediaIntegrityError");
        // Product code owns revealing failures: do not scroll/reveal after action.
        await Program.WaitUntilAsync(() => error.IsEffectivelyVisible && InViewport(window, error));
        Require(C<Ursa.Controls.Banner>(page, "SourceMediaIntegrityErrorBanner").IsVisible
            && error.Text == Texts.T("sources.providerMismatchSaveFailed", Failure)
            && C<CheckBox>(page, "SourceAllowProviderMismatch").IsChecked == expectedSaved
            && !C<Ursa.Controls.Banner>(page, "SourcesActionErrorBanner").IsVisible,
            "Save failure lost its local identity, saved state or visible error banner.");
        AssertTextFits(error);
    }

    private static void Navigate(MainWindow window, string key) => window.GetLogicalDescendants().OfType<MenuItem>()
        .First(item => Equals(item.Tag, key)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task RevealAsync(MainWindow window, Control target)
    {
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); target.BringIntoView(); window.UpdateLayout(); return InViewport(window, target); });
        await Settle();
    }
    private static bool InViewport(Window window, Control target)
    {
        if (!target.IsAttachedToVisualTree() || !target.IsEffectivelyVisible || !ReferenceEquals(TopLevel.GetTopLevel(target), window)
            || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return false;
        Point? origin = target.TranslatePoint(default, window);
        if (origin is not { } point || point.X < -1 || point.Y < -1 || point.X + target.Bounds.Width > window.ClientSize.Width + 1
            || point.Y + target.Bounds.Height > window.ClientSize.Height + 1) return false;
        return target.GetVisualAncestors().OfType<ScrollViewer>().All(scroll => target.TranslatePoint(default, scroll) is { } p
            && p.X >= -1 && p.Y >= -1 && p.X + target.Bounds.Width <= scroll.Viewport.Width + 1
            && p.Y + target.Bounds.Height <= scroll.Viewport.Height + 1);
    }
    private static void AssertTextFits(TextBlock text) => Require(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 1
        && text.TextLayout.Height <= text.Bounds.Height + 1, "Changed copy is clipped: " + text.Name);
    private static object State(Window window, Control control) => new { control.Name, bounds = control.Bounds.ToString(),
        origin = control.TranslatePoint(default, window)?.ToString(), control.IsEffectivelyVisible, control.IsEffectivelyEnabled,
        control.IsFocused, inViewport = InViewport(window, control), text = (control as TextBlock)?.Text };
    private static void Observation(MainWindow window, string task, object data) => Console.WriteLine("ALPHA2_COPY_GUI "
        + JsonSerializer.Serialize(new { task, sourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_DIGEST"),
            language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode, theme = window.ActualThemeVariant.Key?.ToString(),
            window.RenderScaling, client = window.ClientSize.ToString(), data }));
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static T C<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing alpha2 copy control: " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
