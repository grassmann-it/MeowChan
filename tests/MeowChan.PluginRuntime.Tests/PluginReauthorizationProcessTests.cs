using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using MeowChan.TestSupport;
using Xunit.Abstractions;

namespace MeowChan.PluginRuntime.Tests;

// Synthetic process E2E only. These are separate host lifetimes using the production
// store/inventory/worker, NOT DesktopSessionHost, packaged release or historical upgrade evidence.
public sealed class PluginReauthorizationProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ExactConsentIsRequiredAndSurvivesTwoIndependentHostRestarts(bool material, bool enabled)
    {
        Probe probe = new(output);
        var seed = await probe.Run("seed", material.ToString(), enabled.ToString());
        Assert.NotEmpty(seed.State.SelectedSourceOwners);
        Assert.Single(seed.State.Grants);
        Assert.NotNull(Other(seed).Pinned);
        var blocked = await probe.Run("stage-B");
        AssertBlocked(seed, blocked);
        var review = await probe.Run("review");
        AssertReview(seed, review, material);
        var rejected = await probe.Run("consent-no-rights");
        Assert.Equal("InvalidOperationException", rejected.Rejection);
        Assert.Equal(review.StoreHash, rejected.StoreHash);
        Assert.Null(rejected.BackupHash);
        var deniedRestart = await probe.Run("start-B");
        AssertBlocked(seed, deniedRestart);
        Assert.Null(deniedRestart.WorkerPid);
        Assert.Null(deniedRestart.Text);
        Assert.Equal(0, deniedRestart.ImageReads);

        byte[] original = await File.ReadAllBytesAsync(probe.StorePath);
        var consent = await probe.Run("consent");
        Assert.Null(consent.Rejection);
        Assert.Null(Plugin(consent).Active); // Consent must not activate in the saving process.
        Assert.Equal(consent.TargetExecution, Plugin(consent).Pending!.ExecutionRevision);
        Assert.Equal(seed.OldExecution, Plugin(consent).LastGood!.ExecutionRevision);
        AssertRetained(seed, consent);
        Assert.Equal(Hash(original), consent.BackupHash);
        Assert.NotEqual(deniedRestart.StoreHash, consent.StoreHash);
        var backupFiles = probe.BackupFiles();
        Assert.NotEmpty(backupFiles);

        var first = await probe.Run("start-B");
        var second = await probe.Run("start-B");
        Assert.NotEqual(first.Pid, second.Pid);
        foreach (var restarted in new[] { first, second })
        {
            Assert.Null(restarted.Rejection);
            Assert.Equal(consent.TargetExecution, Plugin(restarted).Active!.ExecutionRevision);
            Assert.Equal(consent.TargetAssemblyHash, Plugin(restarted).Active!.Artifact.AssemblySha256);
            Assert.Equal(seed.OldExecution, Plugin(restarted).LastGood!.ExecutionRevision);
            Assert.Null(Plugin(restarted).Pending);
            Assert.Null(Plugin(restarted).Problem);
            EqualJson(Plugin(seed).Active!.Configuration, Plugin(restarted).Active!.Configuration);
            EqualJson(Plugin(seed).Active, Plugin(restarted).LastGood);
            AssertRetained(seed, restarted);
            Assert.Equal(consent.BackupHash, restarted.BackupHash);
            if (enabled)
            {
                Assert.NotNull(restarted.WorkerPid);
                Assert.NotEqual(restarted.Pid, restarted.WorkerPid);
                Assert.Equal(material ? "B:73" : "b:73", restarted.Text);
                Assert.Equal(1, restarted.ImageReads);
            }
            else
            {
                Assert.Null(restarted.WorkerPid); Assert.Null(restarted.Text);
                Assert.Equal(0, restarted.ImageReads); // Active metadata is not permission to run a disabled plugin.
            }
        }
        Assert.Equal(first.StoreHash, second.StoreHash); // Second host reads the same already-settled persistent state.
        if (enabled) Assert.NotEqual(first.WorkerPid, second.WorkerPid);
        Assert.Equal(backupFiles, probe.BackupFiles());

        var third = await probe.Run("stage-C");
        Assert.Null(Plugin(third).Active);
        Assert.NotEqual(third.TargetExecution, third.ThirdExecution);
        Assert.Equal(third.ThirdExecution, third.Row!.NewExecutionRevision);
        Assert.Equal(consent.TargetExecution, Plugin(third).LastGood!.ExecutionRevision);
        AssertRetained(seed, third);
        // Replaying B's frozen row with the LATEST profile revision is still not consent for C.
        string before = third.StoreHash;
        var stale = await probe.Run("consent");
        Assert.Equal("InvalidOperationException", stale.Rejection);
        Assert.Equal(before, stale.StoreHash);
        var deniedC = await probe.Run("start-C");
        Assert.Null(Plugin(deniedC).Active);
        Assert.Null(deniedC.WorkerPid);
        Assert.Equal(0, deniedC.ImageReads);
        AssertRetained(seed, deniedC);
        Assert.Equal(backupFiles, probe.BackupFiles());
    }

    [Theory]
    [InlineData(false, "forged")]
    [InlineData(true, "forged")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    public async Task InvalidConsentInAnotherProcessPublishesNothingAndCannotEnableUse(bool material, string fault)
    {
        Probe probe = new(output);
        var seed = await probe.Run("seed", material.ToString(), "true");
        await probe.Run("stage-B");
        var review = await probe.Run("review");
        AssertReview(seed, review, material);
        if (fault == "stale")
        {
            // Same access and scope, a different grant revision: a different review is mandatory.
            var changed = await probe.Run("bump-grant");
            Assert.NotEqual(seed.State.Grants.Single().Revision, changed.State.Grants.Single().Revision);
            Assert.NotEqual(material ? review.Row!.ChangeFingerprint : review.Row!.PermissionFingerprint,
                material ? changed.Row!.ChangeFingerprint : changed.Row!.PermissionFingerprint);
        }
        var before = probe.StoreFiles();
        var rejected = await probe.Run(fault == "stale" ? "consent" : "consent-" + fault);
        if (fault == "missing")
            Assert.Contains(rejected.Rejection, new[] { "IOException", "FileNotFoundException" });
        else if (fault == "cancel")
            Assert.Contains(rejected.Rejection, new[] { "OperationCanceledException", "TaskCanceledException" });
        else
            Assert.Equal("InvalidOperationException", rejected.Rejection);
        Assert.Equal(before, probe.StoreFiles());
        Assert.Null(Plugin(rejected).Active);
        Assert.Null(Plugin(rejected).Pending);
        Assert.Null(rejected.BackupHash);
        var restart = await probe.Run("start-B");
        Assert.Null(Plugin(restart).Active);
        Assert.Null(restart.WorkerPid);
        Assert.Null(restart.Text);
        Assert.Equal(0, restart.ImageReads);
        EqualJson(rejected.State.Grants, restart.State.Grants);
        EqualJson(seed.State.SelectedSourceOwners, restart.State.SelectedSourceOwners);
        Assert.Equal(seed.OldExecution, Plugin(restart).LastGood!.ExecutionRevision);
        Assert.Equal(seed.OldExecution, Plugin(rejected).LastGood!.ExecutionRevision);
        EqualJson(Other(seed), Other(restart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingArtifactAfterConsentFailsClosedOnNextHostStart(bool material)
    {
        Probe probe = new(output);
        var seed = await probe.Run("seed", material.ToString(), "true");
        await probe.Run("stage-B"); await probe.Run("review");
        var consent = await probe.Run("consent");
        Assert.Null(consent.Rejection);
        var backups = probe.BackupFiles();
        await probe.Run("delete-target");
        var restart = await probe.Run("start-B");
        // A persisted Active snapshot is not proof the bytes still exist. Startup is
        // supplied a successful inventory observation here; the real runtime hash/open
        // boundary must refuse use, without inventing a new store persistence policy.
        Assert.Equal(consent.TargetExecution, Plugin(restart).Active!.ExecutionRevision);
        Assert.Equal("FileNotFoundException", restart.Rejection);
        AssertRetained(seed, restart);
        Assert.Null(restart.WorkerPid);
        Assert.Null(restart.Text);
        Assert.Equal(0, restart.ImageReads);
        Assert.Equal(backups, probe.BackupFiles());
    }

    private static void AssertReview(ProbeResult seed, ProbeResult reviewed, bool material)
    {
        var row = Assert.IsType<BundledCandidateRepair>(reviewed.Row);
        Assert.Equal(reviewed.TargetExecution, row.NewExecutionRevision);
        Assert.True(row.BuildChanged);
        Assert.False(row.CanConfirm);
        BundledRepairPermissions permissions;
        if (material)
        {
            Assert.True(row.CanReviewChanges); Assert.False(row.CanReauthorize);
            Assert.Equal("description-changed", row.BlockedReason);
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(row.ChangeReviewJson!)), row.ChangeFingerprint);
            var changes = SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(row.ChangeReviewJson!));
            EqualJson(Plugin(seed).Active!.Description, changes.PreviousDescription);
            EqualJson(Plugin(seed).Active!.Configuration, changes.PreviousConfiguration);
            Assert.Equal("Synthetic B", changes.NewDescription.Name);
            permissions = changes.Permissions;
        }
        else
        {
            Assert.True(row.CanReauthorize); Assert.False(row.CanReviewChanges);
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(row.PermissionReviewJson!)), row.PermissionFingerprint);
            permissions = SdkJson.Deserialize<BundledRepairPermissions>(Encoding.UTF8.GetBytes(row.PermissionReviewJson!));
        }
        EqualJson(seed.State.Grants, permissions.FilterGrants);
        Assert.Equal(new[] { "shared" }, permissions.SourceIds);
    }
    private static void AssertBlocked(ProbeResult seed, ProbeResult current)
    {
        Assert.Null(Plugin(current).Active);
        Assert.Equal(seed.OldExecution, Plugin(current).LastGood!.ExecutionRevision);
        AssertRetained(seed, current);
    }
    private static void AssertRetained(ProbeResult seed, ProbeResult current)
    {
        Assert.Equal(Plugin(seed).Enabled, Plugin(current).Enabled);
        Assert.Equal(Plugin(seed).SelectedCandidateId, Plugin(current).SelectedCandidateId);
        EqualJson(Plugin(seed).Pinned, Plugin(current).Pinned);
        EqualJson(seed.State.Grants, current.State.Grants);
        EqualJson(seed.State.SelectedSourceOwners, current.State.SelectedSourceOwners);
        EqualJson(Other(seed), Other(current));
        Assert.Equal(0, current.HttpRequests);
    }
    private static PluginActivationSnapshot Plugin(ProbeResult result) => result.State.Plugins.Single(plugin => plugin.PluginId == "process.plugin");
    private static PluginActivationSnapshot Other(ProbeResult result) => result.State.Plugins.Single(plugin => plugin.PluginId == "process.other");
    private static void EqualJson<T>(T expected, T actual) => Assert.Equal(SdkJson.Serialize(expected), SdkJson.Serialize(actual));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Probe
    {
        private readonly ITestOutputHelper output;
        private readonly string root;
        private int sequence;
        internal string StorePath => Path.Combine(root, "profile", "plugin-state", "state.json");
        internal Probe(ITestOutputHelper output)
        {
            this.output = output;
            string evidence = Environment.GetEnvironmentVariable("OCHAN_REAUTH_EVIDENCE") ?? Path.GetTempPath();
            root = Path.Combine(evidence, "reauth-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            output.WriteLine("Persistent synthetic profile/evidence: " + root);
        }
        internal SortedDictionary<string, string> StoreFiles() => new(Directory.GetFiles(Path.GetDirectoryName(StorePath)!)
            .Where(path => Path.GetFileName(path) != "state.lock")
            .ToDictionary(path => Path.GetFileName(path), path => Hash(File.ReadAllBytes(path))), StringComparer.Ordinal);
        internal SortedDictionary<string, string> BackupFiles() => new(StoreFiles()
            .Where(pair => pair.Key.Contains("pre-", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
        internal async Task<ProbeResult> Run(string phase, params string[] arguments)
        {
            string executable = BuiltTestBinaries.AssemblyOf(Path.Combine("tests", "PluginReauthorizationProbe"));
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root
            };
            foreach (string argument in new[] { executable, root, phase, BuiltTestBinaries.WorkerAssembly() }.Concat(arguments)) start.ArgumentList.Add(argument);
            foreach (string name in new[] { "HOME", "TMPDIR", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR" })
            {
                string directory = Path.Combine(root, "private", name); Directory.CreateDirectory(directory);
                start.Environment[name] = directory;
            }
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start private probe.");
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            string prefix = Path.Combine(root, $"{++sequence:D2}-{phase}");
            await File.WriteAllTextAsync(prefix + ".pid", child.Id.ToString());
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            finally
            {
                // Only this newly owned process tree; never enumerate or kill unrelated dotnet processes.
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            string resultJson = await stdout.WaitAsync(TimeSpan.FromSeconds(5)), errors = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(prefix + ".json", resultJson);
            await File.WriteAllTextAsync(prefix + ".stderr", errors);
            Assert.True(child.ExitCode == 0, $"Probe {phase} PID {child.Id} exited {child.ExitCode}: {errors}\n{resultJson}");
            var result = JsonSerializer.Deserialize<ProbeResult>(resultJson) ?? throw new InvalidDataException("Missing probe result.");
            Assert.Equal(child.Id, result.Pid);
            Assert.Equal(Environment.GetEnvironmentVariable("OCHAN_TEST_SOURCE_SHA"), result.SourceSha);
            Assert.Equal(Hash(await File.ReadAllBytesAsync(executable)), result.ProbeHash);
            Assert.Equal(Hash(await File.ReadAllBytesAsync(BuiltTestBinaries.WorkerAssembly())), result.WorkerHash);
            Assert.Equal(0, result.HttpRequests);
            output.WriteLine($"{phase}: host PID={result.Pid}, worker PID={result.WorkerPid}, SHA={result.SourceSha}, store={result.StoreHash}");
            return result;
        }
    }
    private sealed record ProbeResult(int Pid, string? SourceSha, PluginProfileSnapshot State, BundledCandidateRepair? Row,
        string? Rejection, int? WorkerPid, string? Text, int ImageReads, int HttpRequests,
        string StoreHash, string? BackupHash, string ProbeHash, string WorkerHash,
        string TargetExecution, string TargetAssemblyHash, string OldExecution, string? ThirdExecution);
}
