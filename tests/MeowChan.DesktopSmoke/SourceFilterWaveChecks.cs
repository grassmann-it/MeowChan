using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Updates;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Additive NW/FE acceptance against delivered workers and the actual Host/form/store.
/// No live HTTP, source recompilation, synthetic Describe, or filter evaluation claim.
/// Existing activation and generic form suites retain their broader edge-case coverage.
/// </summary>
internal static class SourceFilterWaveChecks
{
    private const string HashPlugin = "meowchan.filter.hash-blocklist";
    private const string InvalidHashes = "not-a-sha256\n";
    private static readonly string ValidHashes = new string('a', 64) + "\n" + new string('B', 64);

    internal static async Task RunAsync(MainWindow parent)
    {
        string profile = Path.Combine(parent.SessionHost.ProfileDirectory, "source-filter-wave-ui");
        string languageBefore = Texts.Language;
        ThemeVariant? themeBefore = Application.Current!.RequestedThemeVariant;
        new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
            SaveUrlsOnExit = true, AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, MinimumFreeSpaceGiB = 0, WorkspaceMode = "Barebones",
            Language = "en", Theme = "Dark" }.SaveConfig(profile);
        using BlockedHttp http = new();
        await using DesktopSessionHost host = CreateHost(profile, http);
        MainWindow window = new(host);
        window.Show(parent);
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready && C<Button>(window, "AddUrlButton").IsEnabled);
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));

            foreach ((string source, string url, bool permission) in new[]
            {
                ("2ch", "https://2ch.hk/demo/res/123.html", false),
                ("leftypol", "https://leftypol.org/demo/res/123.html", true)
            })
                await CheckActivationAsync(window, host, source, url, permission);

            RulesPage page = window.RulesPage;
            SmokeActionClick.Execute(C<MenuItem>(window, "RulesMenuItem"));
            Require(window.SelectedPage == MainWindow.WorkspacePage.Rules, "Rules navigation failed.");
            page.EditScope(MediaRuleScope.Global);
            await page.RefreshPluginFiltersAsync();
            await SettleAsync();
            ComboBox mode = Combo(page, "plugins.ruleFilter");
            mode.SelectedIndex = (int)MediaImageFilterMode.Plugin;
            await SettleAsync();

            foreach ((string pluginId, string[] keys) in new[]
            {
                ("meowchan.filter.file-rules", new[] { "minimumBytes", "maximumBytes", "nameMode", "nameText", "ignoreCase" }),
                ("meowchan.filter.animation-format", new[] { "format", "animation" }),
                ("meowchan.filter.png-metadata", new[] { "field", "customKeyword", "presence" }),
                (HashPlugin, new[] { "blockedSha256" })
            })
            {
                var installed = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins.Single(item => item.PluginId == pluginId);
                var release = installed.Active ?? throw new InvalidOperationException("Delivered filter is not active: " + pluginId);
                PluginDescription described = await host.Plugins.DescribeTestAsync(release.Artifact);
                FilterDescription filter = described.Filters.Single();
                Require(described.PluginId == pluginId && keys.All(key => filter.Settings.Fields.Any(field => field.Key == key)),
                    "Delivered filter Describe omitted its expected fields: " + pluginId);
                ComboBox choices = Combo(page, "plugins.filterChoice");
                choices.SelectedItem = choices.Items.Cast<object>().Single(item => item.ToString() == described.Name + " · " + filter.Name);
                await SettleAsync();
                Require(keys.All(key => Editor(page).FindField(key) != null), "Actual rule form omitted a delivered field: " + pluginId);
                if (pluginId == HashPlugin)
                {
                    TextBox hashes = (TextBox)Editor(page).FindField("blockedSha256")!;
                    Require(hashes.AcceptsReturn && hashes.MaxHeight <= 192, "Hash-list editor is not bounded multiline.");
                    SetText(Editor(page), "blockedSha256", InvalidHashes);
                    string revision = host.MediaRules.Revision;
                    await page.SaveCurrentAsync();
                    Require(page.HasError && host.MediaRules.Revision == revision
                        && ((TextBox)Editor(page).FindField("blockedSha256")!).Text == InvalidHashes,
                        "Worker-rejected hash input was persisted, normalized or discarded.");
                    await CaptureFilterMatrixAsync(window, page, pluginId, "blockedSha256", rejected: true);
                    Require(host.MediaRules.Revision == revision, "Presentation changes persisted invalid hash input.");
                    SetText(Editor(page), "blockedSha256", ValidHashes);
                }
                else
                {
                    if (pluginId.EndsWith("file-rules", StringComparison.Ordinal)) SetText(Editor(page), "minimumBytes", "2");
                    if (pluginId.EndsWith("animation-format", StringComparison.Ordinal)) SetEnum(Editor(page), "format", "png");
                    if (pluginId.EndsWith("png-metadata", StringComparison.Ordinal)) SetEnum(Editor(page), "field", "workflow");
                    await CaptureFilterMatrixAsync(window, page, pluginId, keys[0], rejected: false);
                }
                await page.SaveCurrentAsync();
                MediaImageFilterRule saved = host.MediaRules.Rules.Single(rule => rule.Scope == MediaRuleScope.Global).ImageFilter;
                Require(!page.HasError && saved.PluginId == pluginId && saved.FilterKey == filter.Key
                    && saved.SchemaVersion == filter.Settings.Version, "Actual rule Save did not persist the selected delivered filter.");
                if (pluginId == HashPlugin) Require(saved.Parameters["blockedSha256"] == ValidHashes, "Valid multiline hash text changed on Save.");
                if (pluginId.EndsWith("file-rules", StringComparison.Ordinal)) Require(saved.Parameters["minimumBytes"] == "2", "File-rules parameter did not reach the real store.");
                if (pluginId.EndsWith("animation-format", StringComparison.Ordinal)) Require(saved.Parameters["format"] == "png", "Format parameter did not reach the real store.");
                if (pluginId.EndsWith("png-metadata", StringComparison.Ordinal)) Require(saved.Parameters["field"] == "workflow", "PNG keyword choice did not reach the real store.");
                Console.WriteLine("SOURCE_FILTER_WAVE_FILTER " + JsonSerializer.Serialize(new
                { pluginId, release.ExecutionRevision, release.Artifact.AssemblySha256, filter.Key, filter.Settings.Version,
                    fields = keys, actualScale = window.RenderScaling, savedParameters = saved.Parameters }));
            }

            Require(http.RequestCount == 0 && host.GetThreadWorkSnapshots().Count == 0,
                "Local activation/forms started HTTP or a watch.");
            window.Close();
            await Program.WaitUntilAsync(() => !window.IsVisible);
            await host.DisposeAsync(); // Release this profile lease before a distinct real Host opens it.
            await using DesktopSessionHost restarted = CreateHost(profile, http);
            await restarted.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Require(restarted.State == DesktopSessionState.Ready, "Restart Host failed.");
            Require(restarted.MediaRules.Rules.Single(rule => rule.Scope == MediaRuleScope.Global)
                .ImageFilter.Parameters["blockedSha256"] == ValidHashes, "Restart lost the exact multiline hash configuration.");
            AssertPolicy(restarted, "2ch", false);
            AssertPolicy(restarted, "leftypol", true);
            Require(http.RequestCount == 0 && restarted.GetThreadWorkSnapshots().Count == 0, "Restart caused HTTP or work.");
            Console.WriteLine("SOURCE_FILTER_WAVE_GUI PASS: delivered worker Describes, four real rule forms/Saves, retained invalid multiline hash with worker error, exact restart persistence, strict 2ch vs explicit Leftypol consent, EN/DE Light/Dark at actual scale "
                + window.RenderScaling + "; HTTP=0; no live provider, IME or physical-keyboard claim.");
        }
        finally
        {
            foreach (ExperimentalActivationDialog modal in window.OwnedWindows.OfType<ExperimentalActivationDialog>().ToArray()) modal.Close();
            if (window.IsVisible) window.Close();
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Texts.SetLanguage(languageBefore); Application.Current.RequestedThemeVariant = themeBefore;
        }
    }

    private static DesktopSessionHost CreateHost(string profile, BlockedHttp http) => new(profile, providers: null,
        updateSessionFactory: (path, settings) => new UpdateSession(path, settings, http.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: http.Client);

    private static async Task CheckActivationAsync(MainWindow window, DesktopSessionHost host, string source, string url, bool permission)
    {
        var projection = await host.ProjectExperimentalSourceActivationAsync(url);
        Require(projection.Status == ExperimentalSourceActivationProjectionStatus.ConfirmationRequired
            && projection.Target is { IsEnabled: false } && projection.Target.RequestsMismatchPermission == permission,
            "Delivered source projected the wrong activation authority: " + source + "; " + projection.Error);
        var target = projection.Target!;
        var release = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins.Single(item => item.PluginId == target.PluginOwnerId).Active!;
        PluginDescription described = await host.Plugins.DescribeTestAsync(release.Artifact);
        Require(described.Sources.Single(item => item.SourceId == source).Maturity == SourceMaturity.Experimental,
            "Actual worker did not describe the source as Experimental.");
        foreach (string language in new[] { "en", "de" })
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            ExperimentalActivationDialog modal = new(target, host.ActivateAndAllowExperimentalSourceAsync);
            var decision = modal.ShowDialog<ExperimentalActivationDecision?>(window);
            try
            {
                modal.Width = modal.MinWidth; modal.Height = modal.MinHeight;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(modal, modal.MinWidth, modal.MinHeight));
                await SettleAsync();
                Require(C<TextBlock>(modal, "ActivationExplanation").Text == Texts.T(permission ? "activation.explanation" : "activation.strictExplanation", target.SourceName),
                    "Experimental explanation does not distinguish activation from mismatch permission.");
                Require(C<TextBlock>(modal, "ActivationConfirmLabel").Text == Texts.T(permission ? "activation.confirm" : "activation.strictConfirm"),
                    "Experimental confirm does not state the actual permission action.");
                foreach (string name in new[] { "ActivationHeading", "ActivationExplanation", "ActivationScope", "ActivationConfirmLabel" })
                {
                    TextBlock label = C<TextBlock>(modal, name);
                    Require(label.TextLayout.Width <= label.Bounds.Width + 1 && label.TextLayout.Height <= label.Bounds.Height + 1,
                        "Minimum activation text is clipped: " + name);
                }
                Require(FullyVisible(modal, C<Button>(modal, "ActivationConfirm")) && FullyVisible(modal, C<Button>(modal, "ActivationCancel")),
                    "Minimum activation actions are clipped.");
                Require(modal.ActualThemeVariant.Equals(theme) && Math.Abs(modal.RenderScaling - window.RenderScaling) < .025,
                    "Activation scene theme/scale differs from its requested matrix cell.");
                Program.CaptureWindow(modal, $"smoke-source-wave-{source}-{language}-{theme.Key!.ToString()!.ToLowerInvariant()}-activation.png");
                if (language == "de" && theme == ThemeVariant.Light)
                {
                    SmokeActionClick.Execute(C<Button>(modal, "ActivationConfirm"));
                    await Program.WaitUntilAsync(() => decision.IsCompleted);
                    Require((await decision)?.ContinueAdmission == true, "Real source activation did not succeed.");
                }
                else
                {
                    SmokeActionClick.Execute(C<Button>(modal, "ActivationCancel"));
                    Require((await decision)?.ContinueAdmission != true, "Cancel activated a source.");
                }
            }
            finally { if (modal.IsVisible) modal.Close(); }
        }
        AssertPolicy(host, source, permission);
        SmokeActionClick.Execute(C<MenuItem>(window, "SourcesMenuItem"));
        Require(window.SelectedPage == MainWindow.WorkspacePage.Sources, "Sources navigation failed.");
        SourcesPage page = (SourcesPage)C<ContentControl>(window, "SourcesPageHost").Content!;
        page.Refresh(host.GetProviderDescriptors(), host.GetView(), true); page.SelectSource(source);
        await SettleAsync();
        CheckBox checkbox = C<CheckBox>(page, "SourceAllowProviderMismatch");
        Require(C<StackPanel>(page, "SourceMediaIntegrityPanel").IsVisible == permission
            && (permission || !checkbox.IsEffectivelyVisible), "Strict source exposes a mismatch-permission checkbox.");
        Require(!permission || checkbox.IsChecked == true, "Explicit Leftypol consent is not reflected in Sources.");
        Console.WriteLine("SOURCE_FILTER_WAVE_ACTIVATION " + JsonSerializer.Serialize(new
        { source, permission, target.ExecutionRevision, target.AssemblySha256, actualPolicy = host.GetSourceMediaIntegrityPolicies().Single(item => item.Snapshot.SourceId == source) }));
    }

    private static void AssertPolicy(DesktopSessionHost host, string source, bool granted)
    {
        var policy = host.GetSourceMediaIntegrityPolicies().Single(item => item.Snapshot.SourceId == source);
        Require(policy.IsActive && policy.IsGranted == granted && policy.Snapshot.AllowProviderSizeMd5Mismatch == granted,
            "Wrong active/granted/effective source policy: " + source);
    }

    private static async Task CaptureFilterMatrixAsync(MainWindow window, RulesPage page, string pluginId, string key, bool rejected)
    {
        foreach (string language in new[] { "en", "de" })
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await SettleAsync();
            Control field = Editor(page).FindField(key)!;
            field.BringIntoView();
            await SettleAsync();
            await Program.WaitUntilAsync(() => FullyVisible(window, field));
            Require(window.ActualThemeVariant.Equals(theme) && field.ActualThemeVariant.Equals(theme)
                && PresentationChecks.MatchesRequestedSize(window, 640, 480), "Filter matrix lost actual minimum geometry/theme.");
            if (rejected)
            {
                await page.SaveCurrentAsync(); // Revalidate with the actual delivered worker in every locale/theme cell.
                field = Editor(page).FindField(key)!;
                TextBlock error = Editor(page).GetLogicalDescendants().OfType<TextBlock>().Single(label =>
                    label.Classes.Contains("error") && !string.IsNullOrWhiteSpace(label.Text));
                error.BringIntoView();
                await SettleAsync();
                await Program.WaitUntilAsync(() => FullyVisible(window, error) && FullyVisible(window, field));
                Require(((TextBox)field).Text == InvalidHashes && page.HasError
                    && (AutomationProperties.GetHelpText(field) ?? "").Contains(error.Text!, StringComparison.Ordinal),
                    "Hash worker error or rejected raw text disappeared after localization/theme change.");
            }
            Program.CaptureWindow(window, $"smoke-filter-wave-{pluginId.Split('.').Last()}-{language}-{theme.Key!.ToString()!.ToLowerInvariant()}.png");
        }
    }

    private static PluginSettingsEditor Editor(RulesPage page) => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => editor.Draft.Scope == SettingScope.FilterRule);
    private static ComboBox Combo(RulesPage page, string key) => page.GetVisualDescendants().OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == Texts.T(key));
    private static void SetText(PluginSettingsEditor editor, string key, string value)
    {
        TextBox input = (TextBox)editor.FindField(key)!;
        CheckBox? set = input.GetLogicalParent() is StackPanel panel ? panel.Children.OfType<CheckBox>().SingleOrDefault() : null;
        if (set != null) set.IsChecked = true;
        input.Text = value;
    }
    private static void SetEnum(PluginSettingsEditor editor, string key, string value)
    {
        ComboBox input = (ComboBox)editor.FindField(key)!;
        input.SelectedItem = input.Items.Cast<ComboBoxItem>().Single(item => item.Tag as string == value);
    }
    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static bool FullyVisible(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Point? point = control.TranslatePoint(default, window);
        if (point == null || point.Value.X < -1 || point.Value.Y < -1
            || point.Value.X + control.Bounds.Width > window.ClientSize.Width + 1
            || point.Value.Y + control.Bounds.Height > window.ClientSize.Height + 1) return false;
        foreach (ScrollViewer scroll in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            Point? location = control.TranslatePoint(default, scroll);
            if (location == null || location.Value.X < -1 || location.Value.Y < -1
                || location.Value.X + control.Bounds.Width > scroll.Bounds.Width + 1
                || location.Value.Y + control.Bounds.Height > scroll.Bounds.Height + 1) return false;
        }
        return true;
    }
    private static T C<T>(Control root, string name) where T : Control => root.FindControl<T>(name)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class BlockedHttp : HttpMessageHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);
        internal HttpClient Client() => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new InvalidOperationException("NW/FE GUI acceptance forbids HTTP: " + request.RequestUri);
        }
    }
}
