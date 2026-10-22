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
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string compilerDirectory = Environment.GetEnvironmentVariable("MEOWCHAN_COMPILER_TEST_BUNDLE") ?? Path.Combine(repository, "MeowChan.PluginCompiler/bin/Release/net10.0");
        string workerDirectory = Environment.GetEnvironmentVariable("MEOWCHAN_WORKER_TEST_BUNDLE") ?? Path.Combine(repository, "MeowChan.PluginWorker/bin/Release/net10.0");
        string root = Path.Combine(Config.GetConfigDirectory(), "plugin-ui-fixture");
        Directory.CreateDirectory(root);
        string plugins = Path.Combine(root, "sources"); Directory.CreateDirectory(plugins);
        string source = Path.Combine(plugins, "editor fixture ' literal.cs");
        await File.WriteAllTextAsync(source, FixtureSource);
        PluginCompilationService compiler = new(new(Path.Combine(compilerDirectory, "MeowChan.PluginCompiler"), compilerDirectory, Path.Combine(root, "cache")));
        PluginInstallationInventory inventory = new();
        PluginProfileStore profile = new(root);
        PluginWorkerLaunch launch = new(Path.Combine(workerDirectory, "MeowChan.PluginWorker"), Array.Empty<string>(), Path.Combine(root, "workers"));
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
            Button revoke = page.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is PluginGrantScope);
            Require(((PluginGrantScope)revoke.Tag!).BoardId == null, "The revoke action lost the broader grant origin.");
            revoke.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Program.WaitUntilAsync(() => !page.IsBusy);
            PluginProfileSnapshot revoked = await profile.ReadAsync();
            Require(!PluginGrantAuthorization.Resolve(revoked, "fixture.management", "fixture-filter", "fixture-source", "b", MeowChan.PluginHost.Filters.FilterRuleIdentity.ForScope(MediaRuleScope.Global)).Allows(FilterDataAccess.OriginalImage), "Revocation left the displayed broader grant effective.");
            Require(Grant("plugins.grantBoard").Text == "b", "Grant refresh lost the target board.");
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
