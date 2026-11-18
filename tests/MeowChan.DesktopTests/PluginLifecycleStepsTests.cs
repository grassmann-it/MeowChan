using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

/// <summary>Installed → Enabled → Active, and the one next action, for the states a plugin file goes through.</summary>
public sealed class PluginLifecycleStepsTests
{
    private const string PluginId = "fixture.lifecycle";
    private const string CandidateId = "candidate-a";

    private static PluginStepState[] States(PluginLifecycleView view) => view.Steps.Select(step => step.State).ToArray();
    private static PluginLifecycleView View(PluginCatalogEntry entry, IReadOnlyList<PluginProductionWorkerSnapshot>? workers = null) =>
        PluginLifecycleSteps.Create(entry, PluginEntryPresentation.Create(entry, workers));

    [Fact]
    public void AFileJustAddedIsInstalledAndWaitsForTheRestartThatRegistersIt()
    {
        PluginCatalogEntry entry = new("new-file", null, "new-file", "/plugins/new.cs", null, null, null, []);
        PluginLifecycleView view = View(entry);
        Assert.Equal(new[] { "installed", "enabled", "active" }, view.Steps.Select(step => step.Id));
        Assert.Equal(new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later }, States(view));
        Assert.Equal(PluginNextAction.RestartToRegister, view.Next);
        Assert.False(view.ActiveNow);
    }

    [Fact]
    public void AFileWithAnInventoryProblemIsBlockedAtInstalled()
    {
        PluginCatalogEntry entry = new("bad", null, "bad", "/plugins/bad.cs", null, null, null,
            [new("MCEMPTY", "The source file is empty.", "/plugins/bad.cs")]);
        PluginLifecycleView view = View(entry);
        Assert.Equal(new[] { PluginStepState.Problem, PluginStepState.Later, PluginStepState.Later }, States(view));
        Assert.Equal(PluginNextAction.FixProblem, view.Next);
    }

    [Fact]
    public void AnInventoryWarningDoesNotBlockTheFile()
    {
        PluginCatalogEntry entry = new("ok", null, "ok", "/plugins/ok.cs", null, null, null,
            [new("MCLIMIT", "Inventory stopped at 256 candidates.", "/plugins", Severity: "warning")]);
        Assert.Equal(PluginStepState.Done, View(entry).Steps[0].State);
    }

    [Fact]
    public void ARegisteredPluginWhoseFileIsGoneIsMissing()
    {
        PluginActivationSnapshot state = new(PluginId, true, 1, null, null, null, null, null, [], null);
        PluginCatalogEntry entry = new("plugin:" + PluginId, PluginId, null, null, null, state, null, []);
        PluginLifecycleView view = View(entry);
        Assert.Equal(PluginStepState.Problem, view.Steps[0].State);
        Assert.Equal(PluginNextAction.FileMissing, view.Next);

        PluginCandidateSnapshot absent = new(CandidateId, PluginId, null, null, false, "Not found at startup.");
        Assert.Equal(PluginNextAction.FileMissing, View(Entry(Release("old"), absent)).Next);
    }

    [Fact]
    public void ADisabledPluginNamesEnableAndTheEnabledPluginIsActiveNow()
    {
        PluginLifecycleView disabled = View(Entry(Release("build"), Candidate("build"), enabled: false));
        Assert.Equal(new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later }, States(disabled));
        Assert.Equal(PluginNextAction.Enable, disabled.Next);
        Assert.False(disabled.ActiveNow);

        PluginLifecycleView active = View(Entry(Release("build"), Candidate("build")));
        Assert.Equal(new[] { PluginStepState.Done, PluginStepState.Done, PluginStepState.Done }, States(active));
        Assert.Equal(PluginNextAction.None, active.Next);
        Assert.True(active.ActiveNow);
    }

    [Fact]
    public void AChangedBuildNeedsTheExplicitChoiceThenARestart()
    {
        PluginLifecycleView changed = View(Entry(Release("old"), Candidate("new")));
        Assert.Equal(PluginNextAction.SelectBuild, changed.Next);
        Assert.Equal(new[] { PluginStepState.Done, PluginStepState.Done, PluginStepState.Next }, States(changed));
        Assert.False(changed.ActiveNow);

        PluginLifecycleView saved = View(Entry(Release("old"), Candidate("new"), pending: Release("new")));
        Assert.Equal(PluginNextAction.RestartToUse, saved.Next);
    }

    [Fact]
    public void SavedSettingsOfTheRunningBuildApplyAtTheNextRestart()
    {
        PluginLifecycleView view = View(Entry(Release("same"), Candidate("same"), pending: Release("same", configuration: "next")));
        Assert.Equal(PluginNextAction.SavedChangesApplyAtRestart, view.Next);
        Assert.True(view.ActiveNow);
    }

    [Fact]
    public void AnAutomaticSdkTransitionIsNamedBeforeAnythingElseAndKeepsItsOwnButton()
    {
        PluginCatalogEntry entry = Entry(Release("new"), Candidate("new"));
        entry = entry with { Activation = entry.Activation! with { AutomaticSdkTransition = new PluginSdkTransitionSnapshot("1.0.0", "1.1.0", "new") } };
        Assert.Equal(PluginNextAction.ApproveSdkTransition, View(entry).Next);
    }

    [Fact]
    public void AFailingWorkerOfTheActiveBuildIsAProblemAtTheLastStep()
    {
        PluginCatalogEntry entry = Entry(Release("build"), Candidate("build"));
        PluginWorkerSnapshot failing = new(new("instance", "build", "active-config"), "intervention-required", 100, null, "Worker failed.", []);
        PluginLifecycleView view = View(entry, [new(PluginId, failing)]);
        Assert.Equal(new[] { PluginStepState.Done, PluginStepState.Done, PluginStepState.Problem }, States(view));
        Assert.Equal(PluginNextAction.FixProblem, view.Next);
        Assert.False(view.ActiveNow);
    }

    [Fact]
    public void ACandidateWithABuildProblemIsBlockedAtInstalled()
    {
        PluginCandidateSnapshot broken = new(CandidateId, PluginId, null, null, true, "Compilation failed.", "/fixture/plugin.cs");
        PluginLifecycleView view = View(Entry(null, broken));
        Assert.Equal(PluginStepState.Problem, view.Steps[0].State);
        Assert.Equal(PluginNextAction.FixProblem, view.Next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABundledBuildWaitingForItsRepairReviewSaysSoInsteadOfRestart(bool enabled)
    {
        // The observed state: enabled, not running, last good build kept, the changed bundled build present.
        PluginCatalogEntry plain = Entry(null, Candidate("changed"), enabled: enabled);
        PluginActivationSnapshot waiting = plain.Activation! with
        {
            LastGood = Release("old"), Problem = MeowChan.PluginHost.Configuration.PluginActivationProblems.ExecutableChanged
        };
        PluginCatalogEntry entry = plain with { Activation = waiting };
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry);
        Assert.True(presentation.NeedsRepairReview);
        PluginLifecycleView view = View(entry);
        Assert.Equal(PluginNextAction.ReviewRepair, view.Next);
        Assert.False(view.ActiveNow);
        if (enabled) Assert.Equal(PluginStepState.Problem, view.Steps[2].State);
        // Any other startup problem keeps its own next step.
        PluginCatalogEntry other = plain with { Activation = waiting with { Problem = "Settings correction required: x" } };
        Assert.False(PluginEntryPresentation.Create(other).NeedsRepairReview);
        Assert.NotEqual(PluginNextAction.ReviewRepair, View(other).Next);
    }

    private static PluginDescription Description() =>
        new(PluginId, "Lifecycle fixture", "1.0.0", SdkContract.Version, "Reads a supported source.", [], [], new("1", []), []);

    private static PluginArtifact Artifact(string execution) =>
        new("compile-" + execution, execution, "/fixture/plugin.dll", "assembly-hash", "sdk-hash", "compiler-hash", []);

    private static PluginReleaseSnapshot Release(string execution, string configuration = "active-config") =>
        new(CandidateId, Artifact(execution), Description(), new(configuration, new("1", []), new Dictionary<string, SettingsSnapshot>()));

    private static PluginCandidateSnapshot Candidate(string execution) =>
        new(CandidateId, PluginId, Artifact(execution), Description(), true, null, "/fixture/plugin.cs");

    private static PluginCatalogEntry Entry(PluginReleaseSnapshot? active, PluginCandidateSnapshot candidate,
        PluginReleaseSnapshot? pending = null, bool enabled = true)
    {
        PluginActivationSnapshot state = new(PluginId, enabled, 1, candidate.CandidateId, active, pending, null, null, [candidate], null);
        return new(candidate.CandidateId, PluginId, candidate.CandidateId, "/fixture/plugin.cs",
            candidate.Description ?? active?.Description, state, candidate, Array.Empty<PluginBuildDiagnostic>());
    }
}
