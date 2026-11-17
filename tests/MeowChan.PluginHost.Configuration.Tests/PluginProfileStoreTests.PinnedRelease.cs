using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.PluginHost.Configuration.Tests;

/// <summary>
/// Decision #130 (17.11.2026, option C with the guardrails of option B): a pin on a bundled plugin means "stay on this
/// release", not "stay on this exact binary". The same shipped manifest rebuilt in place with the same plugin version and
/// a declaration identical apart from the operational post order moves the pin to the new build; every other change keeps
/// the stale pin and the explicit review. Synthetic delivered fixtures under a temporary profile only.
/// </summary>
public sealed partial class PluginProfileStoreTests
{
    private const string PinnedLogMarker = "pinned in-place bundled update";

    /// <summary>One startup of a pinned bundled plugin. Expected: carried (pin follows the rebuild) or stale (pin stays).</summary>
    [Theory]
    // All five conditions hold.
    [InlineData("identical", true)]
    [InlineData("post-order", true)]
    // 1. Same plugin, slot and manifest path in the same app folder.
    [InlineData("worktree-folder", false)]
    [InlineData("other-manifest-file", false)]
    [InlineData("ambiguous-second-candidate", false)]
    // 2. Same pluginVersion.
    [InlineData("version", false)]
    [InlineData("version-and-post-order", false)]
    // 3. Identical declaration except operational metadata (even changes the unpinned path accepts as narrowing).
    [InlineData("display-name", false)]
    [InlineData("narrowed-capability", false)]
    [InlineData("host", false)]
    [InlineData("host-purpose", false)]
    [InlineData("route", false)]
    [InlineData("capability", false)]
    [InlineData("filter", false)]
    [InlineData("filter-access", false)]
    [InlineData("source-id", false)]
    [InlineData("settings-schema", false)]
    [InlineData("sdk-version", false)]
    // 4. Rights: exactly the unpinned path's checks (see PinnedReleaseRightsMatchTheUnpinnedInPlacePath).
    [InlineData("grant", true)]
    [InlineData("restricted-source", true)]
    // 5. Current, verified delivery provenance.
    [InlineData("no-current-proof", false)]
    [InlineData("tampered-manifest", false)]
    [InlineData("tampered-assembly", false)]
    [InlineData("ungated-live-authority", false)]
    public async Task PinnedBundledReleaseFollowsItsInPlaceRebuildOnlyWhenEveryConditionHolds(string change, bool carried)
    {
        CapturedPinLog log = new();
        var old = await Delivered("pin-install", "one", source: true);
        string[] restricted = change == "restricted-source" ? ["shared"] : [];
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], restricted, 0);
        if (change == "grant")
            state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        string oldExecution = old.Candidate.Artifact!.ExecutionRevision;
        Assert.Equal(oldExecution, state.Plugins[0].Pinned!.ExecutionRevision);
        // Settings saved for the pinned build are the ones the carried-over release keeps.
        state = await Store().SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            state.Plugins[0].Pinned!.Configuration with { PluginSettings = Values("1", 42) }, state.Revision);
        Assert.Equal(oldExecution, state.Plugins[0].Pending!.ExecutionRevision);

        // The same app folder now ships the plugin under another manifest file name (another delivered slot).
        if (change == "other-manifest-file") File.Delete(old.Candidate.CandidatePath!);
        var next = await Delivered(change == "worktree-folder" ? "pin-worktree" : "pin-install", "two", source: true,
            fileName: change == "other-manifest-file" ? "renamed.plugin.json" : "fixture.plugin.json",
            settings: change == "settings-schema" ? NumberSchema("2", 99) : null);
        Func<PluginDescription, PluginDescription>? redeclare = change switch
        {
            "post-order" => d => d with { Sources = [d.Sources[0] with { PostOrder = PostOrderKind.DecimalPostId }] },
            "version" => d => d with { PluginVersion = "1.1" },
            "version-and-post-order" => d => d with { PluginVersion = "1.1", Sources = [d.Sources[0] with { PostOrder = PostOrderKind.DecimalPostId }] },
            "display-name" => d => d with { Name = "Renamed plugin" },
            // A narrowing the unpinned in-place path accepts silently; a pinned release accepts only the post order.
            "narrowed-capability" => d => d with { Filters = [d.Filters[0] with { RequiredAccess = FilterDataAccess.CandidateMetadata }] },
            "host" => d => d with { Sources = [d.Sources[0] with { HttpHosts = [.. d.Sources[0].HttpHosts, new("media.fixture.test", HttpHostPurpose.OriginalMedia)] }] },
            "host-purpose" => d => d with { Sources = [d.Sources[0] with { HttpHosts = [new("fixture.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)] }] },
            "route" => d => d with { Sources = [d.Sources[0] with { Routes = [.. d.Sources[0].Routes,
                new(SourceResourceKind.Thread, "fixture.test", "/{board}/res/{thread}.html")] }] },
            "capability" => d => d with { Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] },
            "filter" => d => d with { Filters = [.. d.Filters, d.Filters[0] with { Key = "second-filter", Name = "Second filter" }] },
            "filter-access" => d => d with { Filters = [d.Filters[0] with { SupportedMimeTypes = [.. d.Filters[0].SupportedMimeTypes, "image/jpeg"] }] },
            "source-id" => d => d with { Sources = [d.Sources[0] with { SourceId = "renamed-source" }] },
            "sdk-version" => d => d with { SdkVersion = "1.0.0" },
            _ => null
        };
        if (redeclare is not null) next = await Redeclared(next, redeclare);
        List<PluginCandidateObservation> candidates = [next.Candidate];
        List<DeliveredCandidateProvenance> proofs = change == "no-current-proof" ? [] : [next.Proof];
        if (change == "ambiguous-second-candidate")
            candidates.Add(next.Candidate with { CandidateId = "independent-local-choice", CandidatePath = Path.Combine(profile, "plugins", "local.cs") });
        if (change == "tampered-manifest") await File.WriteAllTextAsync(next.Candidate.CandidatePath!, "tampered after delivery observation");
        if (change == "tampered-assembly")
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(next.Candidate.CandidatePath!)!, "fixture.dll"), "tampered");
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);

        PluginProfileStore StartupStore() => change == "ungated-live-authority"
            ? new PluginProfileStore(profile, static _ => true, () => Array.Empty<string>()) { Logger = log.Logger }
            : new PluginProfileStore(profile) { Logger = log.Logger };
        for (int restart = 0; restart < 3; restart++)
        {
            state = await StartupStore().ApplyBundledStartupAsync(candidates, proofs, restricted, state.Revision);
            var plugin = state.Plugins.Single(p => p.PluginId == "test.plugin");
            // A pin is never cleared automatically, and rights and owner choices never change.
            Assert.NotNull(plugin.Pinned);
            Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            if (carried)
            {
                string nextExecution = next.Candidate.Artifact!.ExecutionRevision;
                Assert.Equal(nextExecution, plugin.Active!.ExecutionRevision);
                Assert.Equal(nextExecution, plugin.Pinned!.ExecutionRevision);
                Assert.Equal(oldExecution, plugin.LastGood!.ExecutionRevision);
                Assert.Equal(SdkJson.Serialize(next.Candidate.Description!), SdkJson.Serialize(plugin.Active.Description));
                Assert.Equal(42, Number(plugin.Active));
                Assert.Null(plugin.Pending);
                // The once-only note on the first start after the update; the next start clears it.
                Assert.Equal(restart == 0 ? PluginActivationProblems.PinnedReleaseUpdatedInPlace : null, plugin.Problem);
                Assert.False(PluginActivationProblems.RequiresRepairReview(plugin.Problem));
                Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
                Assert.Empty((await Store().PreviewBundledReselectionsAsync()).Rows);
            }
            else
            {
                // Today's behavior: the pinned build stays (stale) and the explicit review route remains.
                Assert.Equal(oldExecution, plugin.Pinned!.ExecutionRevision);
                Assert.Equal(oldExecution, plugin.Active?.ExecutionRevision);
                Assert.NotEqual(PluginActivationProblems.PinnedReleaseUpdatedInPlace, plugin.Problem);
            }
        }
        if (carried)
        {
            string message = Assert.Single(log.PinnedCarryOvers).RenderMessage();
            Assert.Contains("test.plugin", message, StringComparison.Ordinal);
            Assert.Contains("kept the pin on release \"1.0\"", message, StringComparison.Ordinal);
            Assert.Contains("\"" + oldExecution + "\" -> \"" + next.Candidate.Artifact!.ExecutionRevision + "\"", message, StringComparison.Ordinal);
            Assert.Contains("the previous build stays the last good build for rollback", message, StringComparison.Ordinal);
            Assert.Contains(next.Candidate.CandidatePath!, message, StringComparison.Ordinal);
            Assert.Contains(change switch
            {
                "post-order" => "a post-order-only declaration change",
                "grant" => "1 filter grant(s)",
                "restricted-source" => "size/MD5 exception(s) for shared",
                // The owner choice the rollback recorded for the plugin's source is carried as on the unpinned path.
                _ => "source owner choice(s) for shared"
            }, message, StringComparison.Ordinal);
        }
        else Assert.Empty(log.PinnedCarryOvers);
    }

    /// <summary>Condition 4: a pinned release carries a grant or size/MD5 exception exactly when the unpinned in-place path would.</summary>
    [Theory]
    [InlineData("grant", false, true)]
    [InlineData("grant", true, true)]
    [InlineData("restricted-source", false, true)]
    [InlineData("restricted-source", true, true)]
    [InlineData("grant-and-restricted-source", false, true)]
    [InlineData("grant-and-restricted-source", true, true)]
    [InlineData("grant-relocated", false, false)]
    [InlineData("grant-relocated", true, false)]
    [InlineData("restricted-source-relocated", false, false)]
    [InlineData("restricted-source-relocated", true, false)]
    [InlineData("restriction-added-later", false, true)]
    [InlineData("restriction-added-later", true, true)]
    public async Task PinnedReleaseRightsMatchTheUnpinnedInPlacePath(string rights, bool pinned, bool expectedCarried)
    {
        bool restrictedAtStart = rights is "restricted-source" or "grant-and-restricted-source" or "restricted-source-relocated";
        var old = await Delivered("rights-install", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], restrictedAtStart ? ["shared"] : [], 0);
        if (rights.StartsWith("grant", StringComparison.Ordinal))
            state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        if (pinned) state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        var next = await Delivered(rights.EndsWith("relocated", StringComparison.Ordinal) ? "rights-worktree" : "rights-install", "two", source: true);
        string[] restricted = restrictedAtStart || rights == "restriction-added-later" ? ["shared"] : [];
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], restricted, state.Revision);
        bool carried = state.Plugins[0].Active?.ExecutionRevision == next.Candidate.Artifact!.ExecutionRevision;
        Assert.Equal(expectedCarried, carried);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        if (pinned) Assert.Equal(carried ? next.Candidate.Artifact.ExecutionRevision : old.Candidate.Artifact!.ExecutionRevision,
            state.Plugins[0].Pinned!.ExecutionRevision);
    }

    /// <summary>A right that appears during the verification IO is never carried over: the pin stays on the old build.</summary>
    [Fact]
    public async Task PinnedReleaseRightAddedDuringVerificationKeepsThePinnedBuild()
    {
        var old = await Delivered("race-install", "one", source: true);
        bool restricted = false; int calls = 0;
        using SemaphoreSlim gate = new(1, 1);
        PluginProfileStore GatedStore() => new(profile, static _ => true, () =>
        {
            // The first sample freezes the rights; the second (after integrity IO) sees a new size/MD5 exception.
            if (restricted && Interlocked.Increment(ref calls) == 2) return ["shared"];
            return [];
        }, gate);
        var state = await GatedStore().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await GatedStore().SelectRollbackAsync("test.plugin", state.Revision);
        var next = await Delivered("race-install", "two", source: true);
        restricted = true;
        state = await GatedStore().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.True(calls >= 2, "The live authority was not sampled again after the integrity verification.");
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        Assert.Equal(old.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Active!.ExecutionRevision);
    }

    /// <summary>Previews of a stale pinned plugin never write and never clear the pin.</summary>
    [Fact]
    public async Task PreviewsOfAStalePinnedReleaseNeverMutateState()
    {
        var old = await Delivered("preview-install", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        // A changed version keeps the pin stale (condition 2), so there is something for the previews to look at.
        var next = await Redeclared(await Delivered("preview-install", "two", source: true), d => d with { PluginVersion = "2.0" });
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        var store = Store();
        byte[] before = File.ReadAllBytes(store.StorePath);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            await store.PreviewBundledRepairsAsync();
            await store.PreviewBundledReselectionsAsync();
            Assert.Equal(before, File.ReadAllBytes(store.StorePath));
        }
        Assert.False(File.Exists(store.PreReselectionBackupPath));
        Assert.Equal(old.Candidate.Artifact.ExecutionRevision, (await store.ReadAsync()).Plugins[0].Pinned!.ExecutionRevision);
    }

    /// <summary>
    /// Tom's profile on 17.11.2026: Danbooru pinned (via rollback) on an older bundled build, several later rebuilds of the
    /// same app folder observed but never activated (an earlier app kept the pin stale), and the current build renames an SDK
    /// type (new SDK fingerprint, the old build fails with sdk-mismatch) and declares DecimalPostId post order, otherwise an
    /// identical declaration of the same version. The first start of this app moves the pin to the current build.
    /// </summary>
    [Fact]
    public async Task TomsPinnedDanbooruAfterAnSdkTypeRenameIsCarriedToTheCurrentBuild()
    {
        CapturedPinLog log = new();
        var pinned = await Delivered("oChan-bin-Debug", "danbooru-pinned", source: true, settings: NumberSchema("1", 42));
        var state = await Store().ApplyBundledStartupAsync([pinned.Candidate], [pinned.Proof], [], 0);
        Assert.Equal(42, Number(Active(state)));
        var later = await Delivered("oChan-bin-Debug", "danbooru-later", source: true, settings: NumberSchema("1", 42));
        state = await Store().ApplyBundledStartupAsync([later.Candidate], [later.Proof], [], state.Revision);
        Assert.Equal(later.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        // Roll back to the last good build: that is what pins a bundled plugin.
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        Assert.Equal(pinned.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        // Later rebuilds observed by a host that never carried pins (stale history, provenance retained).
        PluginProfileStore earlierApp = new(profile, static _ => true, () => Array.Empty<string>());
        foreach (string build in new[] { "danbooru-r2", "danbooru-r3", "danbooru-r4" })
        {
            var historical = await Delivered("oChan-bin-Debug", build, source: true, settings: NumberSchema("1", 42));
            state = await earlierApp.ApplyBundledStartupAsync([historical.Candidate], [historical.Proof], [], state.Revision);
            Assert.Equal(pinned.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        }
        var current = await Delivered("oChan-bin-Debug", "danbooru-current", source: true, settings: NumberSchema("1", 42),
            sdkFingerprint: new string('d', 64));
        current = await Redeclared(current, d => d with { Sources = [d.Sources[0] with { PostOrder = PostOrderKind.DecimalPostId }] });
        Assert.Equal(pinned.Candidate.CandidateId, current.Candidate.CandidateId);
        Assert.Equal(pinned.Proof.SlotId, current.Proof.SlotId);
        Assert.NotEqual(pinned.Candidate.Artifact.SdkFingerprint, current.Candidate.Artifact!.SdkFingerprint);

        state = await new PluginProfileStore(profile) { Logger = log.Logger }
            .ApplyBundledStartupAsync([current.Candidate], [current.Proof], [], state.Revision);
        var plugin = state.Plugins[0];
        Assert.Equal(current.Candidate.Artifact.ExecutionRevision, plugin.Active!.ExecutionRevision);
        Assert.Equal(current.Candidate.Artifact.ExecutionRevision, plugin.Pinned!.ExecutionRevision);
        Assert.Equal(pinned.Candidate.Artifact.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
        Assert.Equal(PostOrderKind.DecimalPostId, plugin.Active.Description.Sources[0].PostOrder);
        Assert.Equal(42, Number(plugin.Active));
        Assert.Equal(PluginActivationProblems.PinnedReleaseUpdatedInPlace, plugin.Problem);
        Assert.Contains("a post-order-only declaration change", Assert.Single(log.PinnedCarryOvers).RenderMessage(), StringComparison.Ordinal);
        Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.Empty((await Store().PreviewBundledReselectionsAsync()).Rows);
        // Rollback still returns to the formerly pinned build and pins it again.
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        Assert.Equal(pinned.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
    }

    /// <summary>Repeated rebuilds of the pinned release keep following, each one with its own log line.</summary>
    [Fact]
    public async Task PinnedReleaseFollowsRepeatedInPlaceRebuilds()
    {
        CapturedPinLog log = new();
        var old = await Delivered("repeat-install", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        string previous = old.Candidate.Artifact!.ExecutionRevision;
        foreach (string build in new[] { "two", "three" })
        {
            var next = await Delivered("repeat-install", build, source: true);
            for (int restart = 0; restart < 2; restart++)
            {
                state = await new PluginProfileStore(profile) { Logger = log.Logger }
                    .ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
                Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
                Assert.Equal(previous, state.Plugins[0].LastGood!.ExecutionRevision);
            }
            previous = next.Candidate.Artifact!.ExecutionRevision;
        }
        Assert.Equal(2, log.PinnedCarryOvers.Count);
    }

    private sealed class CapturedPinLog : ILogEventSink
    {
        private readonly List<LogEvent> events = new();
        internal CapturedPinLog() => Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();
        internal Serilog.ILogger Logger { get; }
        public void Emit(LogEvent logEvent) { lock (events) events.Add(logEvent); }
        internal IReadOnlyList<LogEvent> PinnedCarryOvers
        {
            get
            {
                lock (events)
                    return events.Where(e => e.Level == LogEventLevel.Information
                        && e.MessageTemplate.Text.Contains(PinnedLogMarker, StringComparison.Ordinal)).ToArray();
            }
        }
    }
}
