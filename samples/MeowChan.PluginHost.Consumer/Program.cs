using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Consumer;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;

Dictionary<string, string> options = new(StringComparer.Ordinal);
for (int index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || args[index] is not ("--workspace" or "--report" or "--application-directory") || !options.TryAdd(args[index], args[index + 1]))
        throw new ArgumentException("Arguments: [--workspace NEW_DIRECTORY] [--report JSON_PATH] [--application-directory DELIVERED_APP_DIRECTORY].");
}
string workspace = Path.GetFullPath(options.GetValueOrDefault("--workspace") ?? Path.Combine(Path.GetTempPath(), "meowchan package host " + Guid.NewGuid().ToString("N")));
if (Directory.Exists(workspace) || File.Exists(workspace)) throw new IOException("The probe workspace must be new; existing data is never reused.");
Directory.CreateDirectory(workspace);
bool ownWorkspace = !options.ContainsKey("--workspace");
Dictionary<string, object?> evidence = new()
{
    ["scope"] = "PackageReference host APIs; optional delivered compiler/worker with synthetic callbacks, not full product E2E",
    ["developerSdkUsedToBuildConsumer"] = true,
    ["publicHttpRequests"] = 0,
    ["profileStorePassed"] = false,
    ["compilerWorkerStatus"] = options.ContainsKey("--application-directory") ? "requested" : "not_requested"
};
try
{
    using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
    CancellationToken token = deadline.Token;
    PluginDescription description = new PackageProbePlugin().Describe();
    SdkValidation.ThrowIfInvalid(SdkValidation.ValidateDescription(description));
    SettingsSnapshot empty = new("1", Array.Empty<SettingValue>());
    PluginConfiguration configuration = new("consumer-config", new("1", [new SettingValue("label", SettingKind.Text, Text: "saved")]),
        new Dictionary<string, SettingsSnapshot> { [PackageProbePlugin.SourceId] = empty });
    SdkValidation.ThrowIfInvalid(SdkValidation.ValidateConfiguration(description, configuration));

    // This first section tests passive persistence APIs, never claims this synthetic artifact was compiled or loaded.
    string consumerAssembly = typeof(PackageProbePlugin).Assembly.Location;
    PluginArtifact passiveArtifact = new("consumer-contract", "consumer-contract-release", consumerAssembly, Hash(File.ReadAllBytes(consumerAssembly)),
        Hash(File.ReadAllBytes(typeof(IPlugin).Assembly.Location)), "consumer-contract-compiler", Array.Empty<PluginArtifactResource>());
    PluginCandidateObservation candidate = new("consumer-candidate", description.PluginId, passiveArtifact, description);
    string profile = Path.Combine(workspace, "isolated profile");
    PluginProfileStore store = new(profile);
    PluginProfileSnapshot state = await store.ReadAsync(token);
    Require(state.Revision == 0 && state.Plugins.Count == 0, "Fresh profile must be empty.");
    state = await store.ApplyStartupAsync([candidate], state.Revision, token);
    Require(state.Plugins.Single().Active is not null, "Valid startup candidate was not selected.");
    string originalConfiguration = state.Plugins.Single().Active!.Configuration.Revision;
    long staleRevision = state.Revision;
    state = await store.SaveConfigurationAsync(description.PluginId, candidate.CandidateId, configuration, state.Revision, token);
    Require(state.Plugins.Single().Active!.Configuration.Revision == originalConfiguration, "Saving changed active configuration.");
    Require(state.Plugins.Single().Pending is not null, "Saved configuration is not pending.");
    try
    {
        await store.SetEnabledAsync(description.PluginId, false, staleRevision, token);
        throw new InvalidOperationException("Stale profile write was accepted.");
    }
    catch (PluginProfileConflictException) { }
    store = new PluginProfileStore(profile);
    state = await store.ReadAsync(token);
    state = await store.ApplyStartupAsync([candidate], state.Revision, token);
    Require(state.Plugins.Single().Active!.Configuration.PluginSettings.Values.Single(value => value.Key == "label").Text == "saved", "Saved settings did not survive reopen/startup.");
    PluginGrantScope scope = new(PackageProbePlugin.SourceId, "demo", "consumer-rule");
    state = await store.SetGrantAsync(description.PluginId, PackageProbePlugin.FilterKey, scope,
        FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, state.Revision, token);
    PluginGrantAuthorizationSnapshot grant = PluginGrantAuthorization.Resolve(state, description.PluginId, PackageProbePlugin.FilterKey, scope.SourceId, scope.BoardId, scope.RuleId);
    Require(grant.Allows(FilterDataAccess.OriginalImage), "Exact-scope image grant missing.");
    Require(!PluginGrantAuthorization.Resolve(state, description.PluginId, PackageProbePlugin.FilterKey, "other-source", scope.BoardId, scope.RuleId).Allows(FilterDataAccess.OriginalImage), "Grant escaped source scope.");
    state = await store.SetGrantAsync(description.PluginId, PackageProbePlugin.FilterKey, scope, FilterDataAccess.None, state.Revision, token);
    PluginGrantAuthorizationSnapshot revoked = PluginGrantAuthorization.Resolve(state, description.PluginId, PackageProbePlugin.FilterKey, scope.SourceId, scope.BoardId, scope.RuleId);
    Require(!revoked.Allows(FilterDataAccess.OriginalImage) && revoked.Revision != grant.Revision, "Revocation did not invalidate grant revision.");
    state = await store.SelectRollbackAsync(description.PluginId, state.Revision, token);
    Require(state.Plugins.Single().Pinned is not null && state.Plugins.Single().Pending is not null, "Explicit rollback was not pinned/pending.");
    PluginProfileSnapshot reopened = await new PluginProfileStore(profile).ReadAsync(token);
    Require(reopened.Revision == state.Revision && reopened.Plugins.Single().Pinned is not null, "Pinned rollback did not persist.");
    evidence["profileStorePassed"] = true;
    evidence["profileRevision"] = state.Revision;

    if (options.TryGetValue("--application-directory", out string? suppliedApplication))
    {
        string application = Path.GetFullPath(suppliedApplication);
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string compilerDirectory = Path.Combine(application, "plugin-compiler");
        string compiler = Path.Combine(compilerDirectory, "MeowChan.PluginCompiler" + suffix);
        string workerExecutable = Path.Combine(application, "plugin-worker", "MeowChan.PluginWorker" + suffix);
        Require(File.Exists(compiler) && File.Exists(workerExecutable), "Delivered compiler and worker apphosts are required.");
        string source = Path.Combine(workspace, "editable synthetic plugin.cs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PackageProbePlugin.cs"), source);
        await using PluginCompilationService compilation = new(new PluginCompilerOptions(compiler, compilerDirectory, Path.Combine(workspace, "plugin cache")));
        PluginBuildResult first = await compilation.BuildAsync(source, cancellationToken: token);
        Require(first.Succeeded, "Delivered compiler failed: " + string.Join("; ", first.Diagnostics.Select(item => item.Code + " " + item.Message)));
        PluginBuildResult repeated = await compilation.BuildAsync(source, cancellationToken: token);
        Require(repeated.Succeeded && repeated.CacheHit && repeated.Artifact!.ExecutionRevision == first.Artifact!.ExecutionRevision, "Unchanged source did not reuse its verified artifact.");
        string brokenSource = Path.Combine(workspace, "syntax error.cs");
        await File.WriteAllTextAsync(brokenSource, "public class Broken { public int Value => ; }", token);
        PluginBuildResult broken = await compilation.BuildAsync(brokenSource, cancellationToken: token);
        Require(!broken.Succeeded && broken.Diagnostics.Any(item => item.FilePath == brokenSource && item.Line > 0), "Syntax error did not carry source diagnostics.");
        PluginArtifact artifact = first.Artifact!;
        PluginWorkerLaunch launch = new(workerExecutable, Array.Empty<string>(), Path.Combine(workspace, "worker spaces"));
        ProbeServices services = new();
        int? workerPid = null;
        await using (PluginWorkerManager manager = new())
        {
            PluginDescription remote = await manager.DescribeAsync(launch, artifact, token);
            Require(remote.PluginId == description.PluginId, "Worker described a different plugin.");
            await using (IPluginWorkerClient worker = await manager.StartAsync(launch, artifact, configuration, services, testWorker: true, cancellationToken: token, filterWorker: true))
            {
                workerPid = worker.Snapshot.ProcessId;
                Require(workerPid is > 0, "Worker did not expose its owned process ID.");
                ThreadReadRequest threadRequest = new(new(PackageProbePlugin.SourceId, "demo", "1"));
                PluginOperationContext operation = new("consumer.synthetic/demo/1", 1, "consumer-rule-revision", "consumer-grant-revision");
                services.Operation = operation;
                ThreadReadResult threadResult = await worker.ReadThreadAsync(threadRequest, operation, token);
                SdkValidation.ThrowIfInvalid(SdkValidation.ValidateThreadResult(threadRequest, threadResult, remote.Sources.Single()));
                Require(threadResult is ThreadSnapshot snapshot && snapshot.Snapshot.Posts.Single().Text == "package host response", "Duplex metadata bytes did not reach the source result.");
                CatalogReadRequest catalogRequest = new(new(PackageProbePlugin.SourceId, "demo"));
                CatalogReadResult catalog = await worker.ReadCatalogAsync(catalogRequest, operation, token);
                SdkValidation.ThrowIfInvalid(SdkValidation.ValidateCatalogResult(catalogRequest, catalog, remote.Sources.Single()));
                Require(catalog is CatalogSnapshot catalogSnapshot && catalogSnapshot.Snapshot.Threads.Count == 1, "Synthetic catalog was not returned.");
                ImageFilterRequest filter = new(PackageProbePlugin.FilterKey,
                    new FilterCandidate(new SourceMediaId(threadRequest.Thread, "1-0"), "1", 0, new Uri("https://package-fixture.invalid/image.png"), MimeType: "image/png"),
                    "consumer-input", "consumer-media", ProbeServices.AssetToken, 4, Hash(ProbeServices.ImageBytes),
                    operation.RuleRevision!, empty, artifact.ExecutionRevision, configuration.Revision, operation.GrantRevision!);
                ImageFilterResult evaluated = await worker.EvaluateAsync(filter, operation, token);
                Require(evaluated.Outcome == ImageFilterOutcome.Match, "Exact synthetic image bytes did not match.");
                Require((await worker.ValidateSettingsAsync(new SettingsValidationRequest(SettingScope.Plugin, configuration.PluginSettings), token)).Count == 0, "Settings validator failed.");
                Require(services.MetadataCalls == 1 && services.ImageReads == 1, "Unexpected host-service callback counts.");
            }
        }
        if (workerPid is int pid)
        {
            using CancellationTokenSource shutdownDeadline = new(TimeSpan.FromSeconds(5));
            while (ProcessAlive(pid)) await Task.Delay(25, shutdownDeadline.Token);
        }
        Require(!Directory.Exists(launch.WorkingDirectory) || Directory.GetDirectories(launch.WorkingDirectory).Length == 0, "Worker retained an owned workspace after shutdown.");
        evidence["compilerWorkerStatus"] = "passed";
        evidence["applicationDirectory"] = application;
        evidence["compilerSha256"] = Hash(File.ReadAllBytes(compiler));
        evidence["workerSha256"] = Hash(File.ReadAllBytes(workerExecutable));
        evidence["sourceSha256"] = Hash(File.ReadAllBytes(source));
        evidence["artifactSha256"] = artifact.AssemblySha256;
        evidence["compilationFingerprint"] = artifact.CompilationFingerprint;
        evidence["executionRevision"] = artifact.ExecutionRevision;
        evidence["compilerCacheHit"] = true;
        evidence["syntaxDiagnosticPassed"] = true;
        evidence["metadataCallbacks"] = services.MetadataCalls;
        evidence["imageCallbacks"] = services.ImageReads;
        evidence["workerExited"] = true;
    }
    evidence["passed"] = true;
}
catch (Exception exception)
{
    evidence["passed"] = false;
    if (options.ContainsKey("--application-directory") && !Equals(evidence["compilerWorkerStatus"], "passed"))
        evidence["compilerWorkerStatus"] = "failed";
    evidence["error"] = exception.GetType().Name + ": " + exception.Message;
    throw;
}
finally
{
    string json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
    if (options.TryGetValue("--report", out string? report)) await File.WriteAllTextAsync(Path.GetFullPath(report), json + Environment.NewLine);
    Console.WriteLine(json);
    if (ownWorkspace) Directory.Delete(workspace, recursive: true);
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static bool ProcessAlive(int pid)
{
    try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

internal sealed class ProbeServices : IPluginHostServices
{
    internal const string AssetToken = "consumer-image-token";
    internal static readonly byte[] ImageBytes = [1, 2, 3, 4];
    internal PluginOperationContext? Operation { get; set; }
    internal int MetadataCalls { get; private set; }
    internal int ImageReads { get; private set; }
    public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != Operation || request.SourceId != PackageProbePlugin.SourceId || request.Url.AbsoluteUri != "https://package-fixture.invalid/metadata.json")
            throw new UnauthorizedAccessException("Unexpected synthetic metadata callback.");
        MetadataCalls++;
        return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("{\"text\":\"package host response\"}")));
    }
    public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != Operation || assetToken != AssetToken || offset != 0 || count != 4)
            throw new UnauthorizedAccessException("Unexpected image token or operation.");
        ImageReads++;
        return Task.FromResult(new ImageAssetChunk(0, ImageBytes.ToArray(), IsEnd: true));
    }
}
