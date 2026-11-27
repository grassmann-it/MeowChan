using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginProtocol;
using MeowChan.PluginSdk;
using MeowChan.TestSupport;
using Xunit.Abstractions;

namespace MeowChan.PluginRuntime.Tests;

public sealed class LegacyPluginCompatibilityTests(ITestOutputHelper output)
{
    private const string FourChan = "MeowChan.Plugin.FourChan";
    private const string Danbooru = "MeowChan.Plugin.Danbooru";
    private static readonly PluginOperationContext Operation = new("legacy-compatibility", 1);
    private static readonly CatalogReadRequest Catalog = new(new("4chan", "g"));
    private static readonly ThreadReadRequest Thread = new(new("4chan", "g", "42"));
    private static readonly BooruSearchRequest Search = new("danbooru", "cat", 1, 20, "1004");
    private static readonly BooruPostRequest Post = new(new("danbooru", "1003"));

    [Theory]
    [InlineData("1.0.0", FourChan, false)]
    [InlineData("1.0.0", FourChan, true)]
    [InlineData("1.1.0", Danbooru, false)]
    [InlineData("1.1.0", Danbooru, true)]
    public async Task PublishedAssemblyNegotiatesAndRunsOnItsReferenceAndCurrentWorker(
        string sdk, string plugin, bool withExistingApproval)
    {
        await using PluginProbeEvidenceRoot workspace = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        Directory.CreateDirectory(workspace.RootPath);
        string slot = Path.Combine(workspace.RootPath, "plugins");
        PluginArtifact artifact = Stage(sdk, plugin, slot, workspace.RootPath);
        PluginDescription reference = await RunReferenceAsync(sdk, artifact, plugin, workspace.RootPath);
        Assert.Equal(sdk, reference.SdkVersion);
        string sourceId = Assert.Single(reference.Sources).SourceId;
        SourceCapabilities capabilities = Assert.Single(reference.Sources).Capabilities;
        Assert.Equal(plugin == Danbooru, capabilities.HasFlag(SourceCapabilities.PostSearch));
        Assert.Equal(plugin == FourChan, capabilities.HasFlag(SourceCapabilities.BoardCatalog));

        string profile = Path.Combine(workspace.RootPath, "profile");
        PluginProfileSnapshot? approved = null;
        if (withExistingApproval)
        {
            PluginProfileStore prior = new(profile);
            var delivered = await ObserveDeliveryAsync(slot, artifact, reference);
            approved = await prior.ApplyBundledStartupAsync([delivered.Candidate], [delivered.Provenance], [sourceId], 0);
            approved = await prior.SelectCandidateAsync(reference.PluginId, delivered.Candidate.CandidateId, approved.Revision);
            Assert.Equal(reference.PluginId, approved.SelectedSourceOwners[sourceId]);
        }

        await using PluginWorkerManager manager = new();
        PluginWorkerLaunch launch = CurrentWorker(workspace.RootPath);
        PluginDescription observed = await manager.DescribeAsync(launch, artifact);
        Assert.Equal(SdkJson.Serialize(reference), SdkJson.Serialize(observed));
        var current = await ObserveDeliveryAsync(slot, artifact, observed);
        PluginProfileStore store = new(profile);
        PluginProfileSnapshot state = await store.ApplyBundledStartupAsync([current.Candidate], [current.Provenance],
            [sourceId], approved?.Revision ?? 0);
        PluginActivationSnapshot activation = Assert.Single(state.Plugins);
        PluginReleaseSnapshot active = Assert.IsType<PluginReleaseSnapshot>(activation.Active);
        Assert.True(activation.Enabled);
        Assert.Null(activation.Problem);
        Assert.Equal(artifact.ExecutionRevision, active.ExecutionRevision);
        if (approved is not null)
        {
            Assert.Equal(Assert.Single(approved.Plugins).SelectedCandidateId, activation.SelectedCandidateId);
            Assert.Equal(SdkJson.Serialize(approved.SelectedSourceOwners), SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(SdkJson.Serialize(approved.Grants), SdkJson.Serialize(state.Grants));
        }

        await RunCurrentAsync(manager, launch, active, plugin);
        output.WriteLine($"SDK {sdk}, {plugin}, protocol {ProtocolVersion.Major}, sha256 {artifact.AssemblySha256}, SDK fingerprint {artifact.SdkFingerprint}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkBridgeRetainsEstablishedSourceApprovalOnlyForTheSameDeliveredSlot(bool relocated)
    {
        await using PluginProbeEvidenceRoot workspace = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        string slot = Path.Combine(workspace.RootPath, "plugins");
        string profile = Path.Combine(workspace.RootPath, "profile");
        PluginArtifact oldArtifact = Stage("1.0.0", FourChan, slot, workspace.RootPath);
        PluginDescription oldDescription = await RunReferenceAsync("1.0.0", oldArtifact, FourChan, workspace.RootPath);
        var old = await ObserveDeliveryAsync(slot, oldArtifact, oldDescription);
        PluginProfileStore prior = new(profile);
        PluginProfileSnapshot approved = await prior.ApplyBundledStartupAsync([old.Candidate], [old.Provenance], ["4chan"], 0);
        approved = await prior.SelectCandidateAsync(oldDescription.PluginId, old.Candidate.CandidateId, approved.Revision);

        string nextSlot = relocated ? Path.Combine(workspace.RootPath, "relocated", "plugins") : slot;
        PluginArtifact newArtifact = Stage("1.1.0", FourChan, nextSlot, workspace.RootPath);
        await using PluginWorkerManager manager = new();
        PluginWorkerLaunch launch = CurrentWorker(workspace.RootPath);
        PluginDescription newDescription = await manager.DescribeAsync(launch, newArtifact);
        Assert.Equal(SdkJson.Serialize(oldDescription with { SdkVersion = "1.1.0" }), SdkJson.Serialize(newDescription));
        var next = await ObserveDeliveryAsync(nextSlot, newArtifact, newDescription);
        PluginProfileStore current = new(profile);
        PluginProfileSnapshot state = await current.ApplyBundledStartupAsync([next.Candidate], [next.Provenance], ["4chan"], approved.Revision);
        PluginActivationSnapshot activation = Assert.Single(state.Plugins);
        Assert.Equal(old.Candidate.CandidateId, activation.SelectedCandidateId);
        Assert.Equal(oldDescription.PluginId, state.SelectedSourceOwners["4chan"]);
        Assert.Equal(SdkJson.Serialize(approved.Grants), SdkJson.Serialize(state.Grants));
        if (relocated)
        {
            Assert.Null(activation.Active);
            Assert.Null(activation.AutomaticSdkTransition);
            var repair = Assert.Single((await current.PreviewBundledRepairsAsync()).Rows);
            Assert.False(repair.CanConfirm);
            Assert.True(repair.CanReauthorize);
            return;
        }

        PluginReleaseSnapshot active = Assert.IsType<PluginReleaseSnapshot>(activation.Active);
        Assert.Equal(newArtifact.ExecutionRevision, active.ExecutionRevision);
        Assert.Equal(new PluginSdkTransitionSnapshot("1.0.0", "1.1.0", newArtifact.ExecutionRevision), activation.AutomaticSdkTransition);
        await RunCurrentAsync(manager, launch, active, FourChan);
    }

    [Theory]
    [InlineData("1.0.0", 1, "booru.search", "Unsupported mandatory operation")]
    [InlineData("1.1.0", 2, "describe", "Invalid protocol version")]
    public async Task ReferenceWorkerRejectsAnUnsupportedMinimumBeforeLoadingAnyPlugin(
        string sdk, int protocol, string mandatoryOperation, string diagnostic)
    {
        await using PluginProbeEvidenceRoot workspace = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        await using ReferenceWorker worker = new(sdk, workspace.RootPath);
        WireEnvelope hello = worker.Request(protocol, "hello", new HelloRequest([mandatoryOperation]));
        await worker.WriteAsync(hello);
        Assert.Null(await worker.ReadAsync());
        Assert.Contains(diagnostic, await worker.ExitFailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0", FourChan)]
    [InlineData("1.1.0", Danbooru)]
    public async Task CurrentHostImportsTheUnchangedPublishedBundle(string sdk, string plugin)
    {
        await using PluginProbeEvidenceRoot workspace = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        string slot = Path.Combine(workspace.RootPath, "plugins");
        PluginArtifact historical = Stage(sdk, plugin, slot, workspace.RootPath);
        string currentSdk = Path.Combine(Path.GetDirectoryName(BuiltTestBinaries.WorkerAssembly())!, "MeowChan.PluginSdk.dll");
        await using PluginCompilationService installer = Installer(workspace.RootPath, currentSdk, out string fingerprint);

        Assert.NotEqual(fingerprint, historical.SdkFingerprint);
        PluginBuildResult result = await installer.ImportBundleAsync(Path.Combine(slot, plugin + ".plugin.json"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(historical.AssemblySha256, result.Artifact!.AssemblySha256);
        Assert.Equal(historical.SdkFingerprint, result.Artifact.SdkFingerprint);
    }

    [Fact]
    public async Task CurrentBundleImportsAndExecutesOnTheCurrentWorker()
    {
        await using PluginProbeEvidenceRoot workspace = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        string workerPath = BuiltTestBinaries.WorkerAssembly();
        string sdk = Path.Combine(Path.GetDirectoryName(workerPath)!, "MeowChan.PluginSdk.dll");
        await using PluginCompilationService installer = Installer(workspace.RootPath, sdk, out string fingerprint);
        string assembly = BuiltTestBinaries.LibraryOf(Path.Combine("plugins", "Official", "Danbooru"), Danbooru);
        const string relative = "assemblies/" + Danbooru + ".dll";
        string slot = Path.Combine(workspace.RootPath, "plugins");
        string destination = Path.Combine(slot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(assembly, destination);
        var manifest = new PluginBundleManifest(1, relative, CompilerWire.Hash(File.ReadAllBytes(assembly)), fingerprint, new string('1', 64), []);
        string manifestPath = Path.Combine(slot, Danbooru + ".plugin.json");
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));

        PluginBuildResult result = await installer.ImportBundleAsync(manifestPath);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        await using PluginWorkerManager manager = new();
        PluginWorkerLaunch launch = CurrentWorker(workspace.RootPath);
        PluginDescription description = await manager.DescribeAsync(launch, result.Artifact!);
        Assert.Equal("1.1.0", description.SdkVersion);
        var delivery = await ObserveDeliveryAsync(slot, result.Artifact!, description);
        PluginProfileStore store = new(Path.Combine(workspace.RootPath, "profile"));
        PluginProfileSnapshot state = await store.ApplyBundledStartupAsync([delivery.Candidate], [delivery.Provenance], [], 0);
        await RunCurrentAsync(manager, launch, Assert.IsType<PluginReleaseSnapshot>(Assert.Single(state.Plugins).Active), Danbooru);
    }

    private static string FixtureRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln")))
                directory = directory.Parent;
            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository fixture root missing."),
                "tests", "MeowChan.PluginRuntime.Tests", "Fixtures", "LegacyCompatibility");
        }
    }

    private static string PinnedFile(string sdk, string relative)
    {
        using JsonDocument pins = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "releases.lock.json")));
        string? expected = pins.RootElement.GetProperty(sdk).GetProperty("files").GetProperty(relative).GetString();
        string path = Path.Combine(FixtureRoot, sdk, relative);
        Assert.Equal(expected, CompilerWire.Hash(File.ReadAllBytes(path)));
        return path;
    }

    private static PluginArtifact Stage(string sdk, string plugin, string slot, string workspace)
    {
        string manifestName = plugin + ".plugin.json";
        string manifestPath = PinnedFile(sdk, "plugins/" + manifestName);
        PluginBundleManifest manifest = JsonSerializer.Deserialize<PluginBundleManifest>(File.ReadAllBytes(manifestPath))!;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Empty(manifest.Resources);
        Assert.Equal(CompilerWire.Hash(File.ReadAllBytes(PinnedFile(sdk, "plugin-worker/MeowChan.PluginSdk.dll"))), manifest.SdkFingerprint);
        string source = PinnedFile(sdk, "plugins/" + manifest.AssemblyRelativePath);
        Assert.Equal(manifest.AssemblySha256, CompilerWire.Hash(File.ReadAllBytes(source)));

        string destination = Path.Combine(slot, manifest.AssemblyRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(manifestPath, Path.Combine(slot, manifestName), overwrite: true);
        File.Copy(source, destination, overwrite: true);
        string compilation = CompilerWire.HashParts("prebuilt-v1", manifest.AssemblySha256, manifest.SdkFingerprint, manifest.CompilerFingerprint);
        string revision = CompilerWire.HashParts("artifact-v1", compilation);
        string cached = Path.Combine(workspace, "observed-artifacts", revision, "plugin.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.Copy(source, cached, overwrite: true);
        return new(compilation, revision, cached, manifest.AssemblySha256,
            manifest.SdkFingerprint, manifest.CompilerFingerprint, manifest.Resources) { VerifyAssemblyOnLoad = true };
    }

    // Verify the actual worker SDK bytes through the production importer. The compiler and reference
    // entries are inert fixtures since these tests import prebuilt DLLs and never invoke a compiler.
    private static PluginCompilationService Installer(string workspace, string sdk, out string sdkFingerprint)
    {
        string toolchain = Path.Combine(workspace, "toolchain");
        Directory.CreateDirectory(toolchain);
        string sdkName = Path.GetFileName(sdk);
        File.Copy(sdk, Path.Combine(toolchain, sdkName));
        sdkFingerprint = CompilerWire.Hash(File.ReadAllBytes(sdk));
        List<ToolchainFile> files = [new(sdkName, sdkFingerprint, "sdk")];
        foreach (string name in new[] { "compiler", "compiler-deps", "compiler-config", "framework-reference" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            File.WriteAllBytes(Path.Combine(toolchain, name), bytes);
            files.Add(new(name, CompilerWire.Hash(bytes), name == "framework-reference" ? "framework" : "compiler"));
        }
        ToolchainManifest manifest = new(1, "net10.0", CompilerWire.Options,
            CompilerWire.FileSetHash(files.Where(file => file.Role == "compiler")), sdkFingerprint,
            CompilerWire.FileSetHash(files.Where(file => file.Role != "compiler")), files.ToArray());
        File.WriteAllBytes(Path.Combine(toolchain, "toolchain.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        return new(new(Path.Combine(toolchain, "compiler"), toolchain, Path.Combine(workspace, "import-cache")));
    }

    private static async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Provenance)> ObserveDeliveryAsync(
        string slot, PluginArtifact artifact, PluginDescription description)
    {
        PluginInstallationCandidate installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(slot)).Candidates,
            item => item.Kind == PluginCandidateKind.PrebuiltManifest);
        var candidate = new PluginCandidateObservation(installed.CandidateId, description.PluginId, artifact, description, CandidatePath: installed.Path);
        var proof = await PluginInstallationInventory.RecordDeliveredAsync(slot, installed, artifact, description);
        return (candidate, proof);
    }

    private static PluginWorkerLaunch CurrentWorker(string workspace) =>
        new(PluginProbeEnvironment.ResolveDotnetHost(), [BuiltTestBinaries.WorkerAssembly()], workspace);

    private static async Task RunCurrentAsync(PluginWorkerManager manager, PluginWorkerLaunch launch, PluginReleaseSnapshot active, string plugin)
    {
        FixtureServices services = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(launch, active.Artifact, active.Configuration, services);
        Assert.Equal(SdkJson.Serialize(active.Description), SdkJson.Serialize(worker.Description));
        if (plugin == FourChan)
        {
            AssertCatalog(await worker.ReadCatalogAsync(Catalog, Operation));
            AssertThread(await worker.ReadThreadAsync(Thread, Operation));
            var unsupported = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.SearchBooruPostsAsync(Search, Operation));
            Assert.Equal("capability", unsupported.Code);
        }
        else
        {
            AssertSearch(await worker.SearchBooruPostsAsync(Search, Operation));
            AssertPost(await worker.ReadBooruPostAsync(Post, Operation));
        }
        Assert.Equal(2, services.HttpRequests);
    }

    private static async Task<PluginDescription> RunReferenceAsync(string sdk, PluginArtifact artifact, string plugin, string workspace)
    {
        await using ReferenceWorker worker = new(sdk, workspace);
        string[] sourceOperations = plugin == FourChan ? ["catalog", "thread"] : ["booru.search", "booru.post"];
        string[] required = ["describe", "http", .. sourceOperations];
        HelloResponse hello = await worker.CallAsync<HelloRequest, HelloResponse>("hello", new(required));
        Assert.All(required, operation => Assert.Contains(operation, hello.Operations));
        Assert.Equal(sdk == "1.1.0", hello.Operations.Contains("booru.search", StringComparer.Ordinal));
        Assert.DoesNotContain(ProtocolVersion.TaggerBatch, hello.Operations);
        string sourceId = plugin == FourChan ? "4chan" : "danbooru";
        PluginConfiguration configuration = new("legacy-config", new("1", []), new Dictionary<string, SettingsSnapshot> { [sourceId] = new("1", []) });
        PluginDescription description = await worker.CallAsync<InitializeRequest, PluginDescription>(
            "describe", new(artifact.AssemblyPath, [], configuration));
        if (plugin == FourChan)
        {
            AssertCatalog(await worker.CallAsync<CatalogReadRequest, CatalogReadResult>("catalog", Catalog));
            AssertThread(await worker.CallAsync<ThreadReadRequest, ThreadReadResult>("thread", Thread));
        }
        else
        {
            AssertSearch(await worker.CallAsync<BooruSearchRequest, BooruSearchResult>("booru.search", Search));
            AssertPost(await worker.CallAsync<BooruPostRequest, BooruPostResult>("booru.post", Post));
        }
        Assert.Equal(2, worker.HttpRequests);
        await worker.ExitCleanlyAsync();
        return description;
    }

    private static void AssertCatalog(CatalogReadResult result) =>
        Assert.Equal("42", Assert.Single(Assert.IsType<CatalogSnapshot>(result).Snapshot.Threads).Id.ThreadId);

    private static void AssertThread(ThreadReadResult result) =>
        Assert.Equal("historical catalog thread", Assert.Single(Assert.IsType<ThreadSnapshot>(result).Snapshot.Posts).Text);

    private static void AssertSearch(BooruSearchResult result) =>
        Assert.Equal(new BooruPostId("danbooru", "1003"), Assert.Single(Assert.IsType<BooruSearchSnapshot>(result).Page.Posts).Id);

    private static void AssertPost(BooruPostResult result) =>
        Assert.Equal("cat", Assert.Single(Assert.IsType<BooruPostSnapshot>(result).Post.Tags!).Name);

    private static HostHttpResponse HttpResponse(HostHttpRequest request)
    {
        const string post = """{"id":1003,"rating":"g","tag_string_general":"cat"}""";
        Assert.Equal(HostHttpMethod.Get, request.Method);
        Assert.Null(request.Validators);
        string body = request.Url.AbsoluteUri switch
        {
            "https://a.4cdn.org/g/catalog.json" => """[{"threads":[{"no":42}]}]""",
            "https://a.4cdn.org/g/thread/42.json" => """{"posts":[{"no":42,"com":"historical catalog thread"}]}""",
            "https://danbooru.donmai.us/posts.json?tags=cat&page=b1004&limit=20" => "[" + post + "]",
            "https://danbooru.donmai.us/posts/1003.json" => post,
            _ => throw new InvalidOperationException("Unexpected fixture HTTP request: " + request.Url)
        };
        Assert.Equal(request.Url.Host == "a.4cdn.org" ? "4chan" : "danbooru", request.SourceId);
        return new(200, request.Url, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(body));
    }

    private sealed class FixtureServices : IPluginHostServices
    {
        private int httpRequests;
        internal int HttpRequests => Volatile.Read(ref httpRequests);

        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Operation, operation);
            Interlocked.Increment(ref httpRequests);
            return Task.FromResult<HostHttpResult>(HttpResponse(request));
        }

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The metadata operations have no image grant.");
    }

    private sealed class ReferenceWorker : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private readonly FrameCodec codec;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal int HttpRequests { get; private set; }

        internal ReferenceWorker(string sdk, string workspace)
        {
            string assembly = PinnedFile(sdk, "plugin-worker/MeowChan.PluginWorker.dll");
            _ = PinnedFile(sdk, "plugin-worker/MeowChan.PluginSdk.dll");
            ProcessStartInfo start = new(PluginProbeEnvironment.ResolveDotnetHost())
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = workspace
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(FixtureRoot, "reference.runtimeconfig.json"));
            start.ArgumentList.Add(assembly);
            PluginProbeEnvironment.ApplyPrivateEnvironment(start, workspace);
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the reference worker.");
            stderr = process.StandardError.ReadToEndAsync();
            codec = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        internal WireEnvelope Request<T>(int protocol, string operation, T payload) =>
            new(protocol, "request", operation, Guid.NewGuid().ToString("N"), "reference", "legacy-build",
                "legacy-config", null, null, null, null, null, WireJson.Element(payload));

        internal Task WriteAsync(WireEnvelope message) => codec.WriteAsync(message, deadline.Token);
        internal Task<WireEnvelope?> ReadAsync() => codec.ReadAsync(deadline.Token);

        internal async Task<TResult> CallAsync<TRequest, TResult>(string operation, TRequest payload)
        {
            WireEnvelope request = Request(1, operation, payload);
            await WriteAsync(request);
            WireEnvelope? response = await ReadAsync();
            while (response is { Direction: "service" })
            {
                Assert.Equal("http", response.Operation);
                Assert.Equal(request.RequestId, response.ParentRequestId);
                HttpRequests++;
                await WriteAsync(response with { Direction = "serviceResult",
                    Payload = WireJson.Element<HostHttpResult>(HttpResponse(WireJson.Read<HostHttpRequest>(response.Payload))) });
                response = await ReadAsync();
            }
            Assert.True(response is not null, "Reference worker closed the wire: " +
                (process.HasExited ? await stderr.WaitAsync(deadline.Token) : operation));
            Assert.True(response.Direction == "response", response.Payload.GetRawText());
            Assert.Equal(request.Operation, response.Operation);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.WorkerInstance, response.WorkerInstance);
            return WireJson.Read<TResult>(response.Payload);
        }

        internal async Task<string> ExitFailureAsync()
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.NotEqual(0, process.ExitCode);
            return await stderr.WaitAsync(deadline.Token);
        }

        internal async Task ExitCleanlyAsync()
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await stderr.WaitAsync(deadline.Token));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                codec.Dispose();
                deadline.Dispose();
                process.Dispose();
            }
        }
    }
}
