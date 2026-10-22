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
                Require(page.FindControl<Button>("PluginBack")!.IsEffectivelyVisible, "Minimum size did not use list-to-detail navigation.");
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
            await File.WriteAllTextAsync(source, "public class Broken { public int Value => ; }");
            await page.BuildAndTestAsync();
            Require(page.FindControl<StackPanel>("PluginBuildDiagnostics")!.Children.OfType<Button>().Any(), "Syntax error has no file/line editor action.");
            Require((await profile.ReadAsync()).Plugins.Single().Active!.ExecutionRevision == original.Artifact.ExecutionRevision, "Syntax failure changed the active build.");
            Program.CaptureWindow(window, "smoke-plugins-syntax-error.png");
            Console.WriteLine("PLUGIN UI PASS: real compiler + worker + host HTTP; no watch/archive/original path; retained draft/focus; EN/DE Light/Dark B/S/A minima; real test cancellation.");
        }
        finally { page.EndLifetime(); host.Content = previous; }
    }
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
public sealed class ManagementFixture : ISourcePlugin, IPluginSettingsValidator
{
    public PluginDescription Describe() => new("fixture.management", "Management fixture", "1.0.0", SdkContract.Version,
        "A real local compiler and worker fixture with five setting types.",
        new[] { new SourceDescription("fixture-source", "Fixture source", new Uri("https://fixture.example.test/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            new[] { new UrlRoute(SourceResourceKind.Thread, "fixture.example.test", "/{board}/thread/{thread}", IsCanonical: true) },
            new[] { new HttpHostDeclaration("fixture.example.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia) }, new("1", Array.Empty<SettingDefinition>())) },
        Array.Empty<FilterDescription>(), new("1", new SettingDefinition[] {
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
    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, PluginCallContext context, CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<FieldValidationError>>(request.Settings.Values.Any(value => value.Key == "label" && value.Text == "rejected") ? new[] { new FieldValidationError("label", "rejected", "Fixture rejects this label") } : Array.Empty<FieldValidationError>());
}
""";
}
