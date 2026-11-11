using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// No UI, plugin execution, compilation, network or installed profiles. The recording adapter
// delegates every admitted reauthorization to the REAL atomic store with real delivered/cache bytes.
public sealed class PluginMultiApprovalTests
{
    [Fact]
    public async Task ThreeIndividuallyConsentedRowsUseOnlyOwnReturnedRevisionsAndPreserveOriginalState()
    {
        using Fixture f = await Fixture.CreateAsync();
        var before = await f.Store.ReadAsync();
        byte[] original = await File.ReadAllBytesAsync(f.Store.StorePath);
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);

        Assert.Equal(3, f.Recording.Writes.Count);
        Assert.Equal(new[] { before.Revision, before.Revision + 1, before.Revision + 2 },
            f.Recording.Writes.Select(write => write.Expected));
        Assert.Equal(new[] { before.Revision + 1, before.Revision + 2, before.Revision + 3 }, f.Recording.ReturnedRevisions);
        Assert.All(session.Receipts, receipt => Assert.Equal(PluginApprovalState.Saved, receipt.State));
        Assert.Null(session.IssueKey);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        var after = await new PluginProfileStore(f.Root).ReadAsync();
        Assert.Equal(before.Revision + 3, after.Revision);
        Assert.Equal(SdkJson.Serialize(before.Grants), SdkJson.Serialize(after.Grants));
        Assert.Equal(SdkJson.Serialize(before.SelectedSourceOwners), SdkJson.Serialize(after.SelectedSourceOwners));
        foreach (var plugin in after.Plugins)
        {
            var old = before.Plugins.Single(p => p.PluginId == plugin.PluginId);
            var row = f.Choices.Single(choice => choice.PluginId == plugin.PluginId).Repair!;
            Assert.Null(plugin.Active); // Save is not activation.
            Assert.Equal(row.NewExecutionRevision, plugin.Pending!.ExecutionRevision);
            Assert.Equal(row.NewCandidateId, plugin.Pending.CandidateId);
            Assert.Equal(SdkJson.Serialize(old.LastGood!.Configuration), SdkJson.Serialize(plugin.Pending.Configuration));
            Assert.Equal(SdkJson.Serialize(old.LastGood), SdkJson.Serialize(plugin.LastGood));
            Assert.Equal(old.SelectedCandidateId, plugin.SelectedCandidateId);
            Assert.Equal(old.Enabled, plugin.Enabled);
            Assert.Equal(old.LifecycleRevision, plugin.LifecycleRevision);
            Assert.Equal(old.Pinned, plugin.Pinned);
        }
        Assert.Empty((await f.Store.PreviewBundledRepairsAsync()).Rows);
        // Re-clicking old controls must not repeat any successful operation.
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Equal(3, f.Recording.Writes.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidOrUnexpectedReturnRevisionCannotBeReplacedWithLocallyIncrementedExpected(int offset)
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Recording.ResponseRevision = expected => expected + offset;
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Single(f.Recording.Writes);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[0].State);
        Assert.Equal(f.Choices[0].Revision + offset, session.Receipts[0].Revision);
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
        Assert.Equal(f.Choices[0].Revision + 1, (await f.Store.ReadAsync()).Revision);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("configuration")]
    [InlineData("grant-revision")]
    [InlineData("pin")]
    public async Task ForeignRevisionAfterFirstCommitStopsWithoutRebasingEvenHiddenRowState(string change)
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Recording.AfterWrite = async count =>
        {
            if (count != 1) return;
            var snapshot = await f.Store.ReadAsync();
            var plugin = snapshot.Plugins.Single(p => p.PluginId == f.Choices[1].PluginId);
            if (change == "enabled") await f.Store.SetEnabledAsync(plugin.PluginId, !plugin.Enabled, snapshot.Revision);
            else if (change == "pin") await f.Store.SelectRollbackAsync(plugin.PluginId, snapshot.Revision);
            else if (change == "configuration")
            {
                // Ordinary settings writes must not bypass bundled selection admission either.
                // Pin the retained old release, then change its configuration through the real API.
                var pinned = await f.Store.SelectRollbackAsync(plugin.PluginId, snapshot.Revision);
                await f.Store.SaveConfigurationAsync(plugin.PluginId, plugin.LastGood!.CandidateId,
                    plugin.LastGood.Configuration, pinned.Revision);
            }
            else
            {
                var grant = snapshot.Grants.Single(g => g.PluginId == plugin.PluginId);
                await f.Store.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, grant.Access, snapshot.Revision);
            }
        };
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Single(f.Recording.Writes);
        Assert.Equal(new[] { PluginApprovalState.Saved, PluginApprovalState.NeedsReview, PluginApprovalState.NotExecuted },
            session.Receipts.Select(receipt => receipt.State));
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
        Assert.Equal(f.Choices[0].Revision + (change == "configuration" ? 3 : 2), (await f.Store.ReadAsync()).Revision);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("old-execution")]
    [InlineData("candidate")]
    [InlineData("path")]
    [InlineData("slot")]
    [InlineData("rights-json")]
    [InlineData("fingerprint")]
    [InlineData("build-changed")]
    [InlineData("blocked")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task FreshRederivationMustMatchEntireFrozenRowAndBeUnambiguous(string change)
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Recording.RewritePreview = preview =>
        {
            if (f.Recording.Writes.Count != 1) return preview;
            var row = preview.Rows.Single(r => r.PluginId == f.Choices[1].PluginId);
            var changed = change switch
            {
                "execution" => row with { NewExecutionRevision = "different bytes at same path" },
                "old-execution" => row with { OldExecutionRevision = "different basis" },
                "candidate" => row with { NewCandidateId = "another candidate" },
                "path" => row with { NewPath = "/another/path" },
                "slot" => row with { SlotId = "another slot" },
                "rights-json" => row with { PermissionReviewJson = row.PermissionReviewJson + " " },
                "fingerprint" => row with { PermissionFingerprint = "different grant revision" },
                "build-changed" => row with { BuildChanged = !row.BuildChanged },
                "blocked" => row with { BlockedReason = "source-owner-conflict" },
                _ => row
            };
            var rows = preview.Rows.Where(r => r.PluginId != row.PluginId).ToList();
            if (change != "missing") rows.Add(changed);
            if (change == "duplicate") rows.Add(changed);
            return preview with { Rows = rows };
        };
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Single(f.Recording.Writes);
        Assert.Equal(new[] { PluginApprovalState.Saved, PluginApprovalState.NeedsReview, PluginApprovalState.NotExecuted },
            session.Receipts.Select(receipt => receipt.State));
    }

    [Fact]
    public async Task PreviewToCommitRaceStillUsesBackendWriterAdmissionWithoutRetry()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Recording.BeforeWrite = async count =>
        {
            if (count == 2) await f.Store.SetEnabledAsync(f.Choices[1].PluginId, true, (await f.Store.ReadAsync()).Revision);
        };
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Equal(2, f.Recording.Writes.Count); // Second API is rejected by the real writerlease revision gate.
        Assert.Single(f.Recording.ReturnedRevisions);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[0].State);
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[1].State);
        Assert.Equal(PluginApprovalState.NotExecuted, session.Receipts[2].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclearIoAfterFirstSuccessStopsAndNeverRepeatsPossiblyPublishedAction(bool afterPublication)
    {
        using Fixture f = await Fixture.CreateAsync();
        if (afterPublication) f.Recording.AfterWrite = count => count == 2
            ? Task.FromException(new IOException("adapter lost response after publication")) : Task.CompletedTask;
        else f.Recording.BeforeWrite = count => count == 2
            ? Task.FromException(new IOException("publication cannot be established")) : Task.CompletedTask;
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Equal(new[] { PluginApprovalState.Saved, PluginApprovalState.CheckStore, PluginApprovalState.NotExecuted },
            session.Receipts.Select(receipt => receipt.State));
        Assert.Equal(f.Choices[0].Revision + (afterPublication ? 2 : 1), (await f.Store.ReadAsync()).Revision);
        Assert.False(session.MayOffer(f.Choices[1].PluginId));
        await session.SubmitAsync(new[] { f.Choices[1] }, () => true);
        Assert.Equal(2, f.Recording.Writes.Count);
    }

    [Fact]
    public async Task RealBackupTamperingAfterPartialSuccessCannotPublishSecondAction()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Recording.AfterWrite = async count =>
        {
            if (count == 1) await File.AppendAllTextAsync(f.Store.PreReselectionBackupPath, "tamper");
        };
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Equal(2, f.Recording.Writes.Count);
        Assert.Equal(f.Choices[0].Revision + 1, (await f.Store.ReadAsync()).Revision);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[0].State);
        Assert.Equal(PluginApprovalState.CheckStore, session.Receipts[1].State);
        Assert.Equal(PluginApprovalState.NotExecuted, session.Receipts[2].State);
        Assert.EndsWith("tamper", await File.ReadAllTextAsync(f.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task UngatedLiveAuthorityStillRejectsReauthorizationAndNeverFallsBackToNormalRepair()
    {
        using Fixture f = await Fixture.CreateAsync();
        IPluginProfileStore ungated = new PluginProfileStore(f.Root, _ => true, () => Array.Empty<string>());
        PluginMultiApproval session = new(ungated);
        byte[] original = await File.ReadAllBytesAsync(f.Store.StorePath);
        await session.SubmitAsync(f.Choices, () => true);
        Assert.Equal(PluginApprovalState.Failed, session.Receipts[0].State);
        Assert.All(session.Receipts.Skip(1), receipt => Assert.Equal(PluginApprovalState.NotExecuted, receipt.State));
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.StorePath));
        Assert.False(File.Exists(f.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task DoubleSubmitReturnsSameInFlightTaskAndCloseStopsOnlyRemainingActions()
    {
        using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Recording.BeforeWrite = async _ => { entered.SetResult(); await release.Task; };
        PluginMultiApproval session = new(f.Recording);
        var task = session.SubmitAsync(f.Choices, () => true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.Saving);
            Assert.Same(task, session.SubmitAsync(f.Choices, () => true));
            Assert.Single(f.Recording.Writes);
            session.Stop();
        }
        finally { release.TrySetResult(); await task; }
        Assert.False(session.Saving);
        Assert.Equal(new[] { PluginApprovalState.Saved, PluginApprovalState.NotExecuted, PluginApprovalState.NotExecuted },
            session.Receipts.Select(receipt => receipt.State));
        Assert.Equal(f.Choices[0].Revision + 1, (await f.Store.ReadAsync()).Revision);
    }

    [Fact]
    public async Task SubmitFreezesCallerListBeforeFirstAwaitAndCannotAbsorbNewConsent()
    {
        using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Recording.BeforeWrite = async count => { if (count == 1) { entered.SetResult(); await release.Task; } };
        PluginMultiApproval session = new(f.Recording);
        List<PluginApprovalChoice> selected = [f.Choices[0], f.Choices[1]];
        var task = session.SubmitAsync(selected, () => true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            selected.Clear(); selected.Add(f.Choices[2]);
        }
        finally { release.TrySetResult(); await task; }
        Assert.Equal(f.Choices.Take(2).Select(choice => choice.PluginId), f.Recording.Writes.Select(write => write.Row.PluginId));
        Assert.Equal(2, session.Receipts.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("mixed-revisions")]
    [InlineData("duplicate-plugin")]
    [InlineData("ordinary-repair")]
    [InlineData("mixed-reselection")]
    [InlineData("inactive-context")]
    [InlineData("lost-context-after-preview")]
    [InlineData("foreign-start-revision")]
    public async Task InvalidOrUnconsentedSubmissionMakesNoWrites(string reason)
    {
        using Fixture f = await Fixture.CreateAsync();
        var choices = f.Choices.ToArray();
        bool active = true;
        switch (reason)
        {
            case "empty": choices = []; break;
            case "mixed-revisions": choices[1] = choices[1] with { Revision = choices[1].Revision + 1 }; break;
            case "duplicate-plugin": choices[1] = choices[0]; break;
            case "ordinary-repair": choices[1] = choices[1] with { Kind = PluginApprovalKind.Repair, Repair = choices[1].Repair! with { BlockedReason = null } }; break;
            case "mixed-reselection": choices[1] = new(PluginApprovalKind.Reselection, choices[1].Revision,
                Selection: new("other", "old", "new", "slot", "/old", "/new", "old-execution", "new-execution", "{}", null)); break;
            case "inactive-context": active = false; break;
            case "lost-context-after-preview": f.Recording.RewritePreview = preview => { active = false; return preview; }; break;
            case "foreign-start-revision": await f.Store.SetEnabledAsync(choices[2].PluginId, true, choices[0].Revision); break;
        }
        byte[] before = await File.ReadAllBytesAsync(f.Store.StorePath);
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(choices, () => active);
        Assert.Empty(f.Recording.Writes);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.StorePath));
        Assert.False(File.Exists(f.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task LostLifetimeAfterFirstWriteDoesNotAdmitNextRow()
    {
        using Fixture f = await Fixture.CreateAsync();
        bool active = true;
        f.Recording.AfterWrite = _ => { active = false; return Task.CompletedTask; };
        PluginMultiApproval session = new(f.Recording);
        await session.SubmitAsync(f.Choices, () => active);
        Assert.Single(f.Recording.Writes);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[0].State);
        Assert.All(session.Receipts.Skip(1), receipt => Assert.Equal(PluginApprovalState.NotExecuted, receipt.State));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "meowchan-multiapproval-" + Guid.NewGuid().ToString("N"));
        internal PluginProfileStore Store { get; }
        internal RecordingStore Recording { get; }
        internal PluginApprovalChoice[] Choices { get; private set; } = [];
        private Fixture() { Store = new(Root); Recording = new(Store); }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                List<PluginCandidateObservation> old = [], targets = [];
                List<DeliveredCandidateProvenance> oldProofs = [], newProofs = [];
                foreach (string id in new[] { "alpha", "beta", "gamma" })
                {
                    var previous = await f.DeliveredAsync(id, "old"); old.Add(previous.Candidate); oldProofs.Add(previous.Proof);
                    var target = await f.DeliveredAsync(id, "new"); targets.Add(target.Candidate); newProofs.Add(target.Proof);
                }
                var state = await f.Store.ApplyBundledStartupAsync(old, oldProofs, [], 0);
                foreach (var plugin in state.Plugins.ToArray())
                {
                    Assert.NotNull(plugin.Active);
                    state = await f.Store.SetGrantAsync(plugin.PluginId, "dimensions", new("fixture-source", "board", "rule"),
                        FilterDataAccess.OriginalImage, state.Revision);
                    state = await f.Store.SetEnabledAsync(plugin.PluginId, false, state.Revision);
                }
                state = await f.Store.ApplyBundledStartupAsync(targets, newProofs, [], state.Revision);
                var preview = await f.Store.PreviewBundledRepairsAsync();
                Assert.Equal(3, preview.Rows.Count); Assert.All(preview.Rows, row => Assert.True(row.CanReauthorize));
                f.Choices = preview.Rows.OrderBy(row => row.PluginId, StringComparer.Ordinal)
                    .Select(row => new PluginApprovalChoice(PluginApprovalKind.Reauthorization, preview.ProfileRevision, Repair: row)).ToArray();
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> DeliveredAsync(string id, string build)
        {
            string directory = Path.Combine(Root, build, id); Directory.CreateDirectory(directory);
            byte[] bytes = Encoding.UTF8.GetBytes("inert synthetic assembly " + id + build);
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes)), sdk = new('b', 64), compiler = new('c', 64);
            string compilation = HashParts("prebuilt-v1", sha, sdk, compiler);
            string execution = HashParts("artifact-v1", compilation);
            string cache = Path.Combine(Root, "cache", execution); Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.dll"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(cache, "plugin.dll"), bytes);
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".plugin.json"),
                JsonSerializer.Serialize(new PluginBundleManifest(1, "fixture.dll", sha, sdk, compiler, [])));
            var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(directory)).Candidates,
                candidate => candidate.Kind == PluginCandidateKind.PrebuiltManifest);
            PluginArtifact artifact = new(compilation, execution, Path.Combine(cache, "plugin.dll"), sha, sdk, compiler, [], cache);
            SettingsSchema empty = new("1", []);
            PluginDescription description = new(id, id, "1.0", SdkContract.Version, "Inert test filter", [],
                [new("dimensions", "Dimensions", "Filter", FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, empty, ["image/png"])], empty, []);
            PluginCandidateObservation observation = new(installed.CandidateId, id, artifact, description, CandidatePath: installed.Path);
            return (observation, await PluginInstallationInventory.RecordDeliveredAsync(directory, installed, artifact, description));
        }
        private static string HashParts(params string[] parts)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string part in parts)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part), length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class RecordingStore(PluginProfileStore inner) : IPluginProfileStore
    {
        internal List<(BundledCandidateRepair Row, long Expected)> Writes { get; } = [];
        internal List<long> ReturnedRevisions { get; } = [];
        internal Func<int, Task>? BeforeWrite { get; set; }
        internal Func<int, Task>? AfterWrite { get; set; }
        internal Func<long, long>? ResponseRevision { get; set; }
        internal Func<BundledCandidateRepairPreview, BundledCandidateRepairPreview>? RewritePreview { get; set; }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public async Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default)
        {
            var preview = await inner.PreviewBundledRepairsAsync(cancellationToken);
            return RewritePreview?.Invoke(preview) ?? preview;
        }
        public async Task<PluginProfileSnapshot> ReauthorizeBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default)
        {
            Writes.Add((repair, expectedRevision));
            if (BeforeWrite is { } before) await before(Writes.Count);
            var result = await inner.ReauthorizeBundledRepairAsync(repair, expectedRevision, cancellationToken);
            Assert.Equal(expectedRevision + 1, result.Revision); // The adapter cannot return a constant fake revision.
            ReturnedRevisions.Add(result.Revision);
            if (AfterWrite is { } after) await after(Writes.Count);
            return ResponseRevision is { } revision ? result with { Revision = revision(expectedRevision) } : result;
        }
        // Any shortcut, activation or widening to a different operation fails this fixture.
        private static Task<PluginProfileSnapshot> Forbidden() => throw new InvalidOperationException("Unexpected mutation API");
        public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
    }
}
