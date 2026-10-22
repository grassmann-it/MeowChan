using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Http;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class PluginManagementChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string compilerDirectory = Environment.GetEnvironmentVariable("MEOWCHAN_COMPILER_TEST_BUNDLE") ?? Path.Combine(AppContext.BaseDirectory, "plugin-compiler");
        string workerDirectory = Environment.GetEnvironmentVariable("MEOWCHAN_WORKER_TEST_BUNDLE") ?? Path.Combine(AppContext.BaseDirectory, "plugin-worker");
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string root = Path.Combine(Config.GetConfigDirectory(), "plugin-ui-fixture");
        Directory.CreateDirectory(root);
        string plugins = Path.Combine(root, "sources"); Directory.CreateDirectory(plugins);
        string source = Path.Combine(plugins, "editor fixture ' literal.cs");
        await File.WriteAllTextAsync(source, FixtureSource);
        PluginCompilationService compiler = new(new(Path.Combine(compilerDirectory, "MeowChan.PluginCompiler" + executableSuffix), compilerDirectory, Path.Combine(root, "cache")));
        PluginInstallationInventory inventory = new();
        PluginProfileStore profile = new(root);
        PluginWorkerLaunch launch = new(Path.Combine(workerDirectory, "MeowChan.PluginWorker" + executableSuffix), Array.Empty<string>(), Path.Combine(root, "workers"));
        Directory.CreateDirectory(launch.WorkingDirectory);
        await using PluginWorkerManager runtime = new(new() { OperationTimeout = TimeSpan.FromSeconds(5) });
        FixtureTransport transportHandler = new();
        using HttpClient transport = new(transportHandler);
        PluginBuildResult original = await compiler.BuildAsync(source);
        Require(original.Succeeded, "Real fixture compilation failed: " + string.Join("; ", original.Diagnostics.Select(item => item.Message)));
        PluginDescription description = await runtime.DescribeAsync(launch, original.Artifact!);
        PluginConfiguration configuration = PluginSettingsMigration.Migrate(null, description).Configuration!;
        using FixtureServices services = new(transport, description);
        string candidateId = (await inventory.ScanAsync(plugins)).Candidates.Single().CandidateId;
        PluginProfileSnapshot startup = await profile.ApplyStartupAsync(new[] { new PluginCandidateObservation(candidateId, description.PluginId, original.Artifact, description) }, 0);
        await using IPluginWorkerClient production = await runtime.StartAsync(launch, original.Artifact!, configuration, services);
        string productionIdentity = production.Identity.InstanceId;
        PluginManagementServices management = new(profile, compiler, inventory, plugins,
            (artifact, token) => runtime.DescribeAsync(launch, artifact, token),
            (artifact, settings, token) => runtime.StartAsync(launch, artifact, settings, services, testWorker: true, cancellationToken: token), PluginProbe.ReadAsync);
        PluginsPage page = new(); page.Attach(management);
        ContentControl host = window.FindControl<ContentControl>("PluginsPageHost")!;
        object? previous = host.Content; host.Content = page;
        try
        {
            OpenPlugins(window);
            await page.ActivateAsync();
            Require(transportHandler.Requests == 0, "Selecting/refreshing a plugin fetched a site.");
            await File.AppendAllTextAsync(source, "\n// saved source revision two\n");
            page.Select(candidateId);
            await page.BuildAndTestAsync();
            Require(page.FindControl<TextBlock>("PluginTestStatus")!.Text?.Contains("passed", StringComparison.OrdinalIgnoreCase) == true,
                "The real build/test workflow did not report validated success: " + page.FindControl<TextBlock>("PluginPageError")!.Text);
            PluginProfileSnapshot tested = await profile.ReadAsync();
            Require(tested.Plugins.Single().Active!.ExecutionRevision == original.Artifact!.ExecutionRevision,
                "Build and test replaced the active revision.");
            Require(production.Identity.InstanceId == productionIdentity && production.Snapshot.State != "stopped", "Test disposed the production worker.");
            Require(transportHandler.Requests == 0, "Build/describe/validation fetched a site without an explicit probe.");
            page.FindControl<TextBox>("PluginProbeUrl")!.Text = "https://fixture.example.test/b/thread/42";
            await page.ProbeAsync();
            Require(page.FindControl<TextBox>("PluginProbeResult")!.Text?.Contains("2 posts, 1 attachments", StringComparison.Ordinal) == true,
                "The real metadata probe did not show normalized posts/attachments: " + page.FindControl<TextBlock>("PluginPageError")!.Text);
            Require(transportHandler.Requests == 1 && transportHandler.OriginalRequests == 0, "Probe made unexpected HTTP requests.");
            Require(window.UrlList.Count == 0 && window.BoardsList.Count == 0, "Probe created a watch.");
            Require(!Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Any(), "Probe downloaded an original image.");
            // The productive worker still executes independently after an isolated test.
            object live = await PluginProbe.ReadAsync(description, "https://fixture.example.test/b/thread/43", production, new("production-fixture", 1), CancellationToken.None);
            Require(live is MeowChan.PluginSdk.ThreadSnapshot or ThreadDeferred && production.Identity.InstanceId == productionIdentity, "Production worker did not survive the test.");

            await page.RefreshAsync();
            page.Select(candidateId);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TextBox name = page.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "PluginField_label");
            name.Text = "retained draft";
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
                window.Width = mode == "Barebones" ? 640 : 760;
                window.Height = mode == "Barebones" ? 480 : 500;
                OpenPlugins(window);
                await page.ActivateAsync(); page.Select(candidateId);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(page.CurrentDraft!.Plugin.Text("label") == "retained draft", "Status/language/mode refresh discarded the draft.");
                await Program.WaitUntilAsync(() => { window.UpdateLayout(); return page.Bounds.Width < 850 && page.FindControl<Button>("PluginBack")!.IsEffectivelyVisible; });
                Require(page.FindControl<TextBlock>("PluginTestStatus")!.Text == Texts.T("plugins.probeFinished"), "Test status did not follow the selected UI language.");
                Program.CaptureWindow(window, "smoke-plugins-" + mode.ToLowerInvariant() + "-" + language + "-" + theme.Key.ToString()!.ToLowerInvariant() + ".png");
                page.FindControl<Button>("PluginBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Button card = page.FindControl<StackPanel>("PluginCards")!.Children.OfType<Button>().First();
                Require(card.IsFocused && page.FindControl<Grid>("PluginListPane")!.IsVisible, "Back did not restore focus to the same plugin card.");
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            await CapturePluginDraftAndPendingAsync(window, page, profile, original.Artifact!, production, productionIdentity);
            Texts.SetLanguage("en");
            page.FindControl<TextBox>("PluginProbeUrl")!.Text = "https://fixture.example.test/b/thread/hang";
            Task pending = page.ProbeAsync();
            await Program.WaitUntilAsync(() => page.IsBusy);
            await Task.Delay(150);
            page.Deactivate();
            string? before = page.FindControl<TextBox>("PluginProbeResult")!.Text;
            await pending.WaitAsync(TimeSpan.FromSeconds(8));
            Require(page.FindControl<TextBox>("PluginProbeResult")!.Text == before, "Cancelled late probe changed the displayed result.");
            Require(production.Identity.InstanceId == productionIdentity, "Cancelling a test replaced the production worker.");
            await CheckRuleFiltersAsync(window, management, profile, root);
            OpenPlugins(window);
            await page.ActivateAsync(); page.Select(candidateId);
            page.FindControl<TextBox>("PluginProbeUrl")!.Text = "https://fixture.example.test/b/thread/hang";
            Task teardownProbe = page.ProbeAsync();
            await Program.WaitUntilAsync(() => page.IsBusy);
            page.EndLifetime();
            string? teardownResult = page.FindControl<TextBox>("PluginProbeResult")!.Text;
            await teardownProbe.WaitAsync(TimeSpan.FromSeconds(8));
            Require(!page.IsEnabled && page.FindControl<TextBox>("PluginProbeResult")!.Text == teardownResult, "Ended page accepted a late test result or remained interactive.");
            page = new PluginsPage(); page.Attach(management); host.Content = page;
            await page.ActivateAsync(); page.Select(candidateId);
            await File.WriteAllTextAsync(source, "public class Broken { public int Value => ; }");
            await page.BuildAndTestAsync();
            Require(page.FindControl<StackPanel>("PluginBuildDiagnostics")!.Children.OfType<Button>().Any(), "Syntax error has no file/line editor action.");
            Require((await profile.ReadAsync()).Plugins.Single().Active!.ExecutionRevision == original.Artifact.ExecutionRevision, "Syntax failure changed the active build.");
            page.FindControl<Expander>("PluginSourceExpander")!.IsExpanded = true;
            await RevealAsync(window, page.FindControl<StackPanel>("PluginBuildDiagnostics")!.Children.OfType<Button>().First());
            Program.CaptureWindow(window, "smoke-plugins-syntax-error.png");
            Console.WriteLine("PLUGIN UI PASS: real compiler + worker + host HTTP; no watch/archive/original path; retained draft/focus; EN/DE Light/Dark B/S/A minima; real test cancellation.");
        }
        finally { page.EndLifetime(); host.Content = previous; }
    }
    private static async Task CheckRuleFiltersAsync(MainWindow window, PluginManagementServices management, PluginProfileStore profile, string root)
    {
        string rulesDirectory = Path.Combine(root, "rule-profile"); Directory.CreateDirectory(rulesDirectory);
        using ProfileLease lease = ProfileLease.Acquire(rulesDirectory);
        MediaRuleStore store = new(rulesDirectory, lease); await store.LoadAsync();
        MediaRule baseline = new(MediaRuleScope.Global) { ImageFilter = new(MediaImageFilterMode.Plugin, "fixture.management", "fixture-filter", "2", new Dictionary<string, string> { ["rule-label"] = "allowed" }) };
        await store.SaveAsync(new[] { baseline }, store.Current.Revision);
        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        RulesPage page = new(() => store.Current, (rules, revision) => store.SaveAsync(rules, revision)); page.AttachPluginServices(management); host.Content = page;
        try
        {
            page.IsAvailable = true; page.EditScope(MediaRuleScope.Global); await page.RefreshPluginFiltersAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TextBox Field() => page.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "PluginField_rule_label");
            TextBox Grant(string key) => page.GetVisualDescendants().OfType<TextBox>().Single(input => Avalonia.Automation.AutomationProperties.GetName(input) == Texts.T(key));
            string revision = store.Current.Revision;
            Field().Text = "rejected"; await Program.WaitUntilAsync(() => page.CanSave); await page.SaveCurrentAsync();
            Require(store.Current.Revision == revision && page.HasError, "Worker-rejected filter check failed: revision changed=" + (store.Current.Revision != revision) + ", error=" + page.ErrorMessage);
            Require(page.GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "Fixture rejects this label"), "Field validation disappeared after the save finally block.");
            await RevealAsync(window, page.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Text == "Fixture rejects this label"));
            Program.CaptureWindow(window, "smoke-plugin-filter-domain-error.png");
            Field().Text = "allowed again"; await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await page.SaveCurrentAsync();
            Require(store.Current.Rules.Single().ImageFilter.Parameters["rule-label"] == "allowed again", "Accepted parameters did not reach the real Core rule store.");
            Grant("plugins.grantSource").Text = "fixture-source"; await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T("plugins.grantRequired"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !page.IsBusy);
            Require(Grant("plugins.grantSource").Text == "fixture-source", "Grant save lost the target source.");
            Grant("plugins.grantBoard").Text = "b"; await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(page.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "RuleFilterGrantStatus").Text == Texts.T("plugins.accessGranted"), "A source-wide grant was not effective for its board.");
            await CaptureGrantStateAsync(window, page, granted: true);
            Button revoke = page.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is PluginGrantScope);
            Require(((PluginGrantScope)revoke.Tag!).BoardId == null, "The revoke action lost the broader grant origin.");
            revoke.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Program.WaitUntilAsync(() => !page.IsBusy);
            PluginProfileSnapshot revoked = await profile.ReadAsync();
            Require(!PluginGrantAuthorization.Resolve(revoked, "fixture.management", "fixture-filter", "fixture-source", "b", MeowChan.PluginHost.Filters.FilterRuleIdentity.ForScope(MediaRuleScope.Global)).Allows(FilterDataAccess.OriginalImage), "Revocation left the displayed broader grant effective.");
            Require(Grant("plugins.grantBoard").Text == "b", "Grant refresh lost the target board.");
            await CaptureGrantStateAsync(window, page, granted: false);
            await RevealAsync(window, Grant("plugins.grantBoard"));
            Program.CaptureWindow(window, "smoke-plugin-filter-rule-grants.png");
            MediaRule oldSchema = baseline with { ImageFilter = new(MediaImageFilterMode.Plugin, "fixture.management", "fixture-filter", "1", new Dictionary<string, string> { ["rule-label"] = "old label", ["removed"] = "do not silently drop" }) };
            await store.SaveAsync(new[] { oldSchema }, store.Current.Revision); page.DiscardCurrentDraft(); await page.RefreshPluginFiltersAsync();
            page.ExtensionsText = "gif"; string oldRevision = store.Current.Revision; await page.SaveCurrentAsync();
            Require(store.Current.Revision == oldRevision && store.Current.Rules.Single().ImageFilter.Parameters.ContainsKey("removed"), "A schema change silently discarded old parameters.");
            page.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, Texts.T("plugins.acceptSchema"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.SaveCurrentAsync();
            Require(store.Current.Rules.Single().ImageFilter.SchemaVersion == "2" && !store.Current.Rules.Single().ImageFilter.Parameters.ContainsKey("removed"), "Explicit schema migration did not save the new schema atomically.");
            Console.WriteLine("PLUGIN RULE UI PASS: real worker domain rejection, persistent field errors, real Core commit, effective broader grant/revoke, retained targets and explicit schema migration.");
        }
        finally { page.EndLifetime(); host.Content = retained; }
    }
    private static async Task CapturePluginDraftAndPendingAsync(MainWindow window, PluginsPage page, PluginProfileStore profile,
        PluginArtifact activeArtifact, IPluginWorkerClient production, string productionIdentity)
    {
        await AtBarebonesMinimumAsync(window, async () =>
        {
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                TextBox field = page.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "PluginField_label");
                Button save = page.FindControl<Button>("PluginSave")!;
                TextBlock draftStatus = page.FindControl<TextBlock>("PluginDraftStatus")!;
                Require(field.Text == "retained draft" && page.CurrentDraft?.Dirty == true && save.IsEnabled
                    && draftStatus.Text == Texts.T("plugins.draft"), "Draft capture does not represent a saveable unsaved edit.");
                await RevealAsync(window, field);
                Program.CaptureWindow(window, "smoke-plugins-draft-field-barebones-" + language + ".png");
                await RevealRangeAsync(window, draftStatus, save);
                Program.CaptureWindow(window, "smoke-plugins-draft-actions-barebones-" + language + ".png");
            }
            page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentDraft?.Dirty == false);
            PluginActivationSnapshot saved = (await profile.ReadAsync()).Plugins.Single();
            PluginReleaseSnapshot active = saved.Active ?? throw new InvalidOperationException("Saving settings lost the active release.");
            PluginReleaseSnapshot pending = saved.Pending ?? throw new InvalidOperationException("Saving settings did not stage a pending release.");
            Require(active.ExecutionRevision == activeArtifact.ExecutionRevision && pending.ExecutionRevision != active.ExecutionRevision
                && active.Configuration.PluginSettings.Values.Single(value => value.Key == "label").Text == "fixture"
                && pending.Configuration.PluginSettings.Values.Single(value => value.Key == "label").Text == "retained draft",
                "Saving the plugin draft did not retain the active build while staging the tested build and settings.");
            Require(production.Identity.InstanceId == productionIdentity && production.Snapshot.State != "stopped", "Saving settings replaced the productive worker.");
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                TextBlock status = page.FindControl<TextBlock>("PluginStatus")!;
                string statusText = status.Text ?? string.Empty;
                Require(statusText.Contains(Texts.T("plugins.activeRevision", string.Empty).Trim(), StringComparison.Ordinal)
                    && statusText.Contains(Texts.T("plugins.pendingRevision", string.Empty).Trim(), StringComparison.Ordinal)
                    && statusText.Contains(active.ExecutionRevision[..12], StringComparison.Ordinal)
                    && statusText.Contains(pending.ExecutionRevision[..12], StringComparison.Ordinal),
                    "The active/pending capture omits a persisted revision or its distinct status label.");
                await RevealAsync(window, status);
                Program.CaptureWindow(window, "smoke-plugins-active-pending-barebones-" + language + ".png");
            }
            Console.WriteLine("PLUGIN SETTINGS CAPTURE: real UI save retained active " + active.ExecutionRevision
                + " and staged pending " + pending.ExecutionRevision + "; pending label=retained draft.");
        });
    }
    private static async Task CaptureGrantStateAsync(MainWindow window, RulesPage page, bool granted)
    {
        await AtBarebonesMinimumAsync(window, async () =>
        {
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                TextBlock status = page.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "RuleFilterGrantStatus");
                TextBox source = page.GetVisualDescendants().OfType<TextBox>().Single(input => Avalonia.Automation.AutomationProperties.GetName(input) == Texts.T("plugins.grantSource"));
                TextBox board = page.GetVisualDescendants().OfType<TextBox>().Single(input => Avalonia.Automation.AutomationProperties.GetName(input) == Texts.T("plugins.grantBoard"));
                Button allow = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T("plugins.grantRequired")));
                Require(source.Text == "fixture-source" && board.Text == "b" && status.Text == Texts.T(granted ? "plugins.accessGranted" : "plugins.accessMissing"),
                    "Grant capture does not show the verified target and effective access state.");
                string prefix = "smoke-plugins-grant-" + (granted ? "effective" : "revoked") + "-barebones-" + language;
                await RevealRangeAsync(window, source, status);
                Require(InScrollViewport(board), "Board field is clipped between source and grant status.");
                Program.CaptureWindow(window, prefix + "-scope.png");
                if (granted)
                {
                    Button revoke = page.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is PluginGrantScope);
                    PluginGrantScope scope = (PluginGrantScope)revoke.Tag!;
                    TextBlock origin = ((StackPanel)revoke.Parent!).Children.OfType<TextBlock>().Single();
                    Require(scope.SourceId == "fixture-source" && scope.BoardId == null && revoke.IsEnabled,
                        "The visible revoke action does not target the actual broader source grant.");
                    await RevealRangeAsync(window, status, revoke);
                    Require(InScrollViewport(origin), "Grant origin is clipped above the revoke action.");
                    Program.CaptureWindow(window, prefix + "-revoke.png");
                }
                else Require(!page.GetVisualDescendants().OfType<Button>().Any(button => button.Tag is PluginGrantScope), "Revoked grant still exposes an effective-grant revoke action.");
                Control firstAction = granted
                    ? page.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is PluginGrantScope)
                    : status;
                await RevealRangeAsync(window, firstAction, allow);
                Require(allow.IsEnabled, "Grant action is disabled in its capture.");
                Program.CaptureWindow(window, prefix + "-grant.png");
            }
        });
    }
    private static async Task AtBarebonesMinimumAsync(MainWindow window, Func<Task> capture)
    {
        string mode = Config.LoadConfig().WorkspaceMode;
        string language = Texts.Language;
        double width = window.Width, height = window.Height;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => { window.UpdateLayout(); return Math.Abs(window.ClientSize.Width - 640) <= 1 && Math.Abs(window.ClientSize.Height - 480) <= 1; });
            await capture();
        }
        finally
        {
            Texts.SetLanguage(language);
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
    private static Task RevealRangeAsync(Window window, Control first, Control last) => Program.WaitUntilAsync(() =>
    {
        window.UpdateLayout();
        ScrollViewer scroll = first.GetVisualAncestors().OfType<ScrollViewer>().First();
        Point? start = first.TranslatePoint(default, scroll), end = last.TranslatePoint(default, scroll);
        if (start is not { } top || end is not { } bottom) return false;
        Require(bottom.Y + last.Bounds.Height - top.Y <= scroll.Viewport.Height + 1,
            "The requested capture range is taller than the minimum-size viewport.");
        if (!InScrollViewport(first) || !InScrollViewport(last))
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + top.Y));
        window.UpdateLayout();
        return InScrollViewport(first) && InScrollViewport(last);
    });
    private static bool InScrollViewport(Control control)
    {
        ScrollViewer scroll = control.GetVisualAncestors().OfType<ScrollViewer>().First();
        Point? position = control.TranslatePoint(default, scroll);
        return control.IsEffectivelyVisible && position is { } point && point.Y >= -1 && point.Y + control.Bounds.Height <= scroll.Bounds.Height + 1;
    }
    private static Task RevealAsync(Window window, Control control) => Program.WaitUntilAsync(() =>
    {
        window.UpdateLayout(); control.BringIntoView();
        ScrollViewer scroll = control.GetVisualAncestors().OfType<ScrollViewer>().First();
        Point? position = control.TranslatePoint(default, scroll);
        return position is { } point && point.Y >= -1 && point.Y + control.Bounds.Height <= scroll.Bounds.Height + 1;
    });
    private static void OpenPlugins(MainWindow window) => window.GetLogicalDescendants().OfType<MenuItem>()
        .First(item => Equals(item.Tag, "nav.plugins")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FixtureServices : IPluginHostServices, IDisposable
    {
        private readonly PluginHostHttp _http;
        internal FixtureServices(HttpClient transport, PluginDescription description) => _http = new(transport, new RequestPolicyState(), description.Sources);
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) => _http.SendAsync(request, cancellationToken);
        public Task<ImageAssetChunk> ReadImageAsync(string token, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) => throw new InvalidOperationException("A metadata probe must never request original image bytes.");
        public void Dispose() => _http.Dispose();
    }
    private sealed class FixtureTransport : HttpMessageHandler
    {
        internal int Requests;
        internal int OriginalRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (request.RequestUri!.AbsolutePath.Contains("media", StringComparison.Ordinal)) Interlocked.Increment(ref OriginalRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("metadata"), RequestMessage = request });
        }
    }
    private const string FixtureSource = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;
public sealed class ManagementFixture : ISourcePlugin, IImageFilterPlugin, IPluginSettingsValidator
{
    public PluginDescription Describe() => new("fixture.management", "Management fixture", "1.0.0", SdkContract.Version,
        "A real local compiler and worker fixture with five setting types.",
        new[] { new SourceDescription("fixture-source", "Fixture source", new Uri("https://fixture.example.test/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            new[] { new UrlRoute(SourceResourceKind.Thread, "fixture.example.test", "/{board}/thread/{thread}", IsCanonical: true) },
            new[] { new HttpHostDeclaration("fixture.example.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia) }, new("1", Array.Empty<SettingDefinition>())) },
        new[] { new FilterDescription("fixture-filter", "Fixture filter", "Domain validated test filter", FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage,
            new("2", new SettingDefinition[] { new("rule-label", SettingScope.FilterRule, SettingKind.Text, "Rule label", IsRequired: true, Default: new("rule-label", SettingKind.Text, Text: "allowed")) }), new[] { "image/png" }) }, new("1", new SettingDefinition[] {
            new("label", SettingScope.Plugin, SettingKind.Text, "Label", "A label for the fixture", true, new("label", SettingKind.Text, Text: "fixture")),
            new("number", SettingScope.Plugin, SettingKind.Integer, "Minimum count", Default: new("number", SettingKind.Integer, Integer: 2), Minimum: 1, Maximum: 10),
            new("flag", SettingScope.Plugin, SettingKind.Boolean, "Include replies", Default: new("flag", SettingKind.Boolean, Boolean: true)),
            new("mode", SettingScope.Plugin, SettingKind.Enum, "Output mode", Default: new("mode", SettingKind.Enum, Text: "normal"), Options: new[] { new SettingOption("normal", "Normal"), new SettingOption("compact", "Compact") }),
            new("uri", SettingScope.Plugin, SettingKind.Uri, "Documentation", Default: new("uri", SettingKind.Uri, Text: "https://example.invalid/")) }),
        new[] { new InformationEntry("information", "Fixture provenance", InformationKind.Text, "Controlled metadata fixture") });
    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context, CancellationToken token = default)
    {
        if (request.Thread.ThreadId == "hang") await Task.Delay(Timeout.Infinite, token);
        HostHttpResult response = await context.Http.SendAsync(new(request.Thread.SourceId, HostHttpMethod.Get, new Uri("https://fixture.example.test/api/" + request.Thread.ThreadId)), token);
        if (response is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (response is HostHttpDeferred deferred) return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode);
        return new ThreadSnapshot(new(request.Thread, new Uri("https://fixture.example.test/" + request.Thread.BoardId + "/thread/" + request.Thread.ThreadId), "Probe result", SourceThreadState.Active,
            new[] { new SourcePost("1", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments, Text: "First post", TextFormat: SourceTextFormat.PlainText,
                Attachments: new[] { new SourceAttachment(0, "image-one", Url: new Uri("https://fixture.example.test/media/one.png"), OriginalFileName: "one.png", Extension: ".png", MimeType: "image/png") }),
                new SourcePost("2", 1, SourceFieldPresence.Text, Text: "Second post", TextFormat: SourceTextFormat.PlainText) }));
    }
    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context, CancellationToken token = default) => Task.FromResult<CatalogReadResult>(new CatalogFailure(request.Board, new("unsupported", "No catalog")));
    public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken token = default) => Task.FromResult(new ImageFilterResult(ImageFilterOutcome.Match, "fixture"));
    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, PluginCallContext context, CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<FieldValidationError>>(request.Settings.Values.Where(value => value.Key is "label" or "rule-label" && value.Text == "rejected")
            .Select(value => new FieldValidationError(value.Key, "rejected", "Fixture rejects this label")).ToArray());
}
""";
}
