using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using PluginReauthorizationProbe;

// Every invocation is a fresh host process. The only bridge is the fixture input and the
// real profile store on disk; no snapshot, binding or consent is reconstructed by the parent.
string root = Path.GetFullPath(args[0]), phase = args[1], workerPath = args[2];
Directory.CreateDirectory(root);
using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(25));
using SemaphoreSlim permissions = new(1, 1);
PluginProfileStore store = new(Path.Combine(root, "profile"), static _ => true, () => new[] { "shared" }, permissions);
string inputPath = Path.Combine(root, "fixture.json"), reviewPath = Path.Combine(root, "review.json");
Fixture fixture;
if (phase == "seed")
{
    bool material = bool.Parse(args[3]), enabled = bool.Parse(args[4]);
    fixture = new(await Deliver("old", material ? 'A' : 'a'), await Deliver("target", material ? 'B' : 'b'),
        null, await Deliver("other", 'x'), material);
    await File.WriteAllBytesAsync(inputPath, SdkJson.Serialize(fixture));
    var initial = await store.ApplyBundledStartupAsync([fixture.Old.Candidate, fixture.Other.Candidate],
        [fixture.Old.Proof, fixture.Other.Proof], ["shared"], 0, lifetime.Token);
    var old = Plugin(initial);
    initial = await store.SelectCandidateAsync(old.PluginId, old.SelectedCandidateId!, initial.Revision, lifetime.Token);
    initial = await store.SaveConfigurationAsync(old.PluginId, old.SelectedCandidateId!, old.Active!.Configuration with
        { PluginSettings = new("1", [new("count", SettingKind.Integer, Integer: 73)]) }, initial.Revision, lifetime.Token);
    initial = await Startup(fixture.Old, initial);
    initial = await store.SetEnabledAsync(old.PluginId, enabled, initial.Revision, lifetime.Token);
    initial = await store.SetGrantAsync(old.PluginId, "bytes", new("shared", "b", "rule"), FilterDataAccess.OriginalImage, initial.Revision, lifetime.Token);
    initial = await store.SetEnabledAsync("process.other", false, initial.Revision, lifetime.Token);
    initial = await store.SelectRollbackAsync("process.other", initial.Revision, lifetime.Token);
    await Startup(fixture.Old, initial);
}
else fixture = SdkJson.Deserialize<Fixture>(await File.ReadAllBytesAsync(inputPath));

string? rejection = null, text = null;
int? workerPid = null;
int imageReads = 0, httpRequests = 0;
if (phase is "stage-B" or "start-B" or "stage-C" or "start-C")
{
    if (phase == "stage-C")
    {
        fixture = fixture with { Third = await Deliver("target", fixture.Material ? 'C' : 'c') };
        await File.WriteAllBytesAsync(inputPath, SdkJson.Serialize(fixture));
    }
    var state = await store.ReadAsync(lifetime.Token);
    await Startup(phase.EndsWith('C') ? fixture.Third! : fixture.Target, state);
}
if (phase == "review")
{
    var preview = await store.PreviewBundledRepairsAsync(lifetime.Token);
    await File.WriteAllBytesAsync(reviewPath, SdkJson.Serialize(preview.Rows.Single(row => row.PluginId == "process.plugin")));
}
if (phase == "bump-grant")
{
    var state = await store.ReadAsync(lifetime.Token);
    var grant = state.Grants.Single();
    await store.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, grant.Access, state.Revision, lifetime.Token);
}
if (phase.StartsWith("consent", StringComparison.Ordinal))
{
    var row = SdkJson.Deserialize<BundledCandidateRepair>(await File.ReadAllBytesAsync(reviewPath));
    if (phase == "consent-forged") row = fixture.Material
        ? row with { ChangeReviewJson = "{}", ChangeFingerprint = Hash("{}"u8.ToArray()) }
        : row with { PermissionReviewJson = "{}", PermissionFingerprint = Hash("{}"u8.ToArray()) };
    if (phase == "consent-missing") File.Delete(fixture.Target.Candidate.Artifact!.AssemblyPath);
    using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    if (phase == "consent-cancel") cancellation.Cancel();
    try
    {
        var current = await store.ReadAsync(lifetime.Token);
        if (fixture.Material)
            await store.ConfirmBundledChangeAsync(row, Plugin(current).LastGood!.Configuration, true,
                phase != "consent-no-rights", current.Revision, cancellation.Token);
        else if (phase == "consent-no-rights")
            await store.ConfirmBundledRepairAsync(row, current.Revision, cancellation.Token);
        else await store.ReauthorizeBundledRepairAsync(row, current.Revision, cancellation.Token);
    }
    catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException)
    { rejection = error.GetType().Name; }
}
if (phase == "delete-target") File.Delete(fixture.Target.Candidate.Artifact!.AssemblyPath);

var final = await store.ReadAsync(lifetime.Token);
// This is the composition boundary under test, NOT a new product policy. A disabled or
// non-active release never gets an operation. Enabled, active releases use the real worker.
if (phase.StartsWith("start-", StringComparison.Ordinal) && Plugin(final) is { Enabled: true, Active: { } active })
{
    await using PluginWorkerManager manager = new(new() { HandshakeTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5), CancellationGrace = TimeSpan.FromSeconds(1) });
    Services services = new(final);
    string workspace = Path.Combine(root, "workers"); Directory.CreateDirectory(workspace);
    string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet";
    await using (var worker = await manager.StartAsync(new(dotnet, [workerPath], workspace), active.Artifact, active.Configuration, services, cancellationToken: lifetime.Token))
    {
        workerPid = worker.Snapshot.ProcessId;
        await File.WriteAllTextAsync(Path.Combine(root, phase + ".worker-pid"), workerPid!.Value.ToString());
        if (!SdkJson.Serialize(active.Description).AsSpan().SequenceEqual(SdkJson.Serialize(worker.Description)))
            throw new InvalidDataException("Worker did not describe the exact approved release.");
        var operation = new PluginOperationContext("shared/b/1", 1);
        var snapshot = (ThreadSnapshot)await worker.ReadThreadAsync(new(new("shared", "b", "1")), operation, lifetime.Token);
        text = snapshot.Snapshot.Posts.Single().Text;
        var grant = PluginGrantAuthorization.Resolve(final, "process.plugin", "bytes", "shared", "b", "rule");
        var request = new ImageFilterRequest("bytes", new(new(new("shared", "b", "1"), "image"), "1", 0,
            new("https://fixture.invalid/a.png"), MimeType: "image/png"), "input", "media", "asset", 4,
            Hash([1, 2, 3, 4]), "rule", new("1", []), active.ExecutionRevision, active.Configuration.Revision, grant.Revision);
        if ((await worker.EvaluateAsync(request, operation with { RuleRevision = "rule", GrantRevision = grant.Revision }, lifetime.Token)).Outcome != ImageFilterOutcome.Match)
            throw new InvalidDataException("Approved exact grant did not reach the synthetic image service.");
        imageReads = services.ImageReads; httpRequests = services.HttpRequests;
    }
    // Disposal must have reaped this privately owned worker before reporting success.
    try { using var child = Process.GetProcessById(workerPid!.Value); if (!child.HasExited) throw new InvalidOperationException("Worker still running."); }
    catch (ArgumentException) { }
    if (Directory.EnumerateDirectories(workspace).Any()) throw new InvalidOperationException("Worker workspace leaked.");
}
var repair = (await store.PreviewBundledRepairsAsync(lifetime.Token)).Rows.SingleOrDefault(row => row.PluginId == "process.plugin");
Console.WriteLine(JsonSerializer.Serialize(new
{
    Pid = Environment.ProcessId, SourceSha = Environment.GetEnvironmentVariable("OCHAN_TEST_SOURCE_SHA"), Phase = phase,
    State = final, Row = repair, Rejection = rejection, WorkerPid = workerPid, Text = text, ImageReads = imageReads, HttpRequests = httpRequests,
    StoreHash = Hash(await File.ReadAllBytesAsync(store.StorePath)),
    BackupHash = File.Exists(store.PreReselectionBackupPath) ? Hash(await File.ReadAllBytesAsync(store.PreReselectionBackupPath)) : null,
    ProbeHash = Hash(await File.ReadAllBytesAsync(typeof(ProbePlugin).Assembly.Location)),
    WorkerHash = Hash(await File.ReadAllBytesAsync(workerPath)),
    TargetExecution = fixture.Target.Candidate.Artifact!.ExecutionRevision,
    TargetAssemblyHash = fixture.Target.Candidate.Artifact.AssemblySha256,
    OldExecution = fixture.Old.Candidate.Artifact!.ExecutionRevision,
    ThirdExecution = fixture.Third?.Candidate.Artifact!.ExecutionRevision
}));

PluginActivationSnapshot Plugin(PluginProfileSnapshot state) => state.Plugins.Single(plugin => plugin.PluginId == "process.plugin");
Task<PluginProfileSnapshot> Startup(Delivery delivery, PluginProfileSnapshot state) => store.ApplyBundledStartupAsync(
    [delivery.Candidate, fixture.Other.Candidate], [delivery.Proof, fixture.Other.Proof], ["shared"], state.Revision, lifetime.Token);
async Task<Delivery> Deliver(string slot, char marker)
{
    string directory = Path.Combine(root, "delivery", slot); Directory.CreateDirectory(directory);
    byte[] bytes = [.. await File.ReadAllBytesAsync(typeof(ProbePlugin).Assembly.Location), (byte)marker];
    string sha = Hash(bytes), sdk = Hash(await File.ReadAllBytesAsync(typeof(IPlugin).Assembly.Location)), compiler = Hash("synthetic-PE-marker-v1"u8.ToArray());
    string compilation = HashParts("prebuilt-v1", sha, sdk, compiler), execution = HashParts("artifact-v1", compilation);
    string cache = Path.Combine(root, "artifacts", execution); Directory.CreateDirectory(cache);
    await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.dll"), bytes);
    await File.WriteAllBytesAsync(Path.Combine(cache, "plugin.dll"), bytes);
    string manifestName = slot == "other" ? "other.plugin.json" : "fixture.plugin.json";
    await File.WriteAllTextAsync(Path.Combine(directory, manifestName), JsonSerializer.Serialize(new PluginBundleManifest(1, "fixture.dll", sha, sdk, compiler, [])));
    var installed = (await new PluginInstallationInventory().ScanAsync(directory)).Candidates.Single(candidate => candidate.Kind == PluginCandidateKind.PrebuiltManifest);
    PluginArtifact artifact = new(compilation, execution, Path.Combine(cache, "plugin.dll"), sha, sdk, compiler, [], cache);
    var description = ProbePlugin.Description(marker);
    PluginCandidateObservation candidate = new(installed.CandidateId, description.PluginId, artifact, description, CandidatePath: installed.Path);
    return new(candidate, await PluginInstallationInventory.RecordDeliveredAsync(directory, installed, artifact, description));
}
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashParts(params string[] parts)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (string part in parts) { byte[] bytes = Encoding.UTF8.GetBytes(part), length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes); }
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}
internal sealed record Delivery(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof);
internal sealed record Fixture(Delivery Old, Delivery Target, Delivery? Third, Delivery Other, bool Material);
internal sealed class Services(PluginProfileSnapshot profile) : IPluginHostServices
{
    internal int ImageReads { get; private set; }
    internal int HttpRequests { get; private set; }
    public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
    { HttpRequests++; throw new InvalidOperationException("Network is forbidden in this offline probe."); }
    public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
    {
        var grant = PluginGrantAuthorization.Resolve(profile, "process.plugin", "bytes", "shared", "b", "rule");
        if (!grant.Allows(FilterDataAccess.OriginalImage) || operation.GrantRevision != grant.Revision || operation.RuleRevision != "rule"
            || assetToken != "asset" || offset != 0 || count != 4) throw new InvalidOperationException("Unexpected synthetic image access.");
        ImageReads++; return Task.FromResult(new ImageAssetChunk(0, [1, 2, 3, 4], true));
    }
}
