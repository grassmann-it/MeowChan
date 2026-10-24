using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginEntryPresentationTests
{
    private const string PluginId = "fixture.presentation";
    private const string CandidateId = "candidate-a";

    [Fact]
    public void SamePathAndVersionWithNewBuildIsNotTheActiveBuildOrAnImplicitPending()
    {
        PluginReleaseSnapshot active = Release("old");
        PluginCatalogEntry entry = Entry(active, Candidate("new"));

        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);

        Assert.Equal(PluginAvailability.Active, view.Availability);
        Assert.True(view.IsSelectedCandidate);
        Assert.False(view.IsActiveCandidate);
        Assert.True(view.HasChangedCandidateBuild);
        Assert.True(view.CandidateChoiceRequired);
        Assert.Equal(PluginPendingKind.None, view.PendingKind);
        Assert.False(view.IsPendingCandidateBuild);
        Assert.Null(view.Pending);
        Assert.Equal(active.Description.PluginVersion, view.CandidateVersion);
        Assert.Equal("old", view.Active!.ExecutionRevision);
    }

    [Fact]
    public void ActivePendingConfigurationAndInvalidStaleDraftRemainSeparate()
    {
        PluginReleaseSnapshot active = Release("same-build");
        PluginReleaseSnapshot pending = Release("same-build", configuration: "next-config");
        PluginCatalogEntry entry = Entry(active, Candidate("same-build"), pending: pending);
        PluginConfigurationDraft draft = new(entry, 3);
        draft.Plugin.Set("count", "invalid integer");

        PluginEntryPresentation view = PluginEntryPresentation.Create(entry, draft: draft, profileRevision: 4);

        Assert.Equal(PluginAvailability.Active, view.Availability);
        Assert.Equal(PluginPendingKind.Configuration, view.PendingKind);
        Assert.True(view.IsPendingCandidateBuild);
        Assert.True(view.IsActiveCandidate);
        Assert.False(view.CandidateChoiceRequired);
        Assert.True(view.HasDraft);
        Assert.True(view.HasUnsavedChanges);
        Assert.True(view.HasDiscardableChanges);
        Assert.True(view.IsDraftStale);
        Assert.Equal("invalid integer", draft.Plugin.Text("count"));
        Assert.Same(active, view.Active);
        Assert.Same(pending, view.Pending);
        Assert.Equal("active-config", view.Active!.Configuration.Revision);
    }

    [Fact]
    public void PendingBuildAndDisabledStateAreBothPreserved()
    {
        PluginCatalogEntry entry = Entry(Release("old"), Candidate("new"),
            pending: Release("new", configuration: "next-config"), enabled: false);

        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);

        Assert.Equal(PluginAvailability.Disabled, view.Availability);
        Assert.Equal(PluginPendingKind.Build, view.PendingKind);
        Assert.True(view.IsPendingCandidateBuild);
        Assert.False(view.CandidateChoiceRequired);
        Assert.Equal("old", view.Active!.ExecutionRevision);
        Assert.Equal("new", view.Pending!.ExecutionRevision);
    }

    [Fact]
    public void PinnedRollbackAtTheSamePathDoesNotHideChoosingTheCurrentCandidate()
    {
        PluginReleaseSnapshot rollback = Release("retained");
        PluginCatalogEntry entry = Entry(rollback, Candidate("current"), pending: rollback, pinned: rollback);

        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);

        Assert.True(view.IsRollbackPinned);
        Assert.True(view.IsSelectedCandidate);
        Assert.True(view.CanSelectCandidate);
        Assert.True(view.CandidateChoiceRequired);
        Assert.Equal("current", entry.Candidate!.Artifact!.ExecutionRevision);
        Assert.Equal("retained", view.Pinned!.ExecutionRevision);
    }

    [Fact]
    public void PinMustBeReleasableEvenWhenItsBuildMatchesTheCandidate()
    {
        PluginReleaseSnapshot pinned = Release("same");
        PluginEntryPresentation view = PluginEntryPresentation.Create(
            Entry(pinned, Candidate("same"), pending: pinned, pinned: pinned));

        Assert.True(view.IsActiveCandidate);
        Assert.True(view.CandidateChoiceRequired);
    }

    [Fact]
    public void PendingAtTheSamePathIsNotTheNewlyObservedBuild()
    {
        PluginEntryPresentation view = PluginEntryPresentation.Create(
            Entry(Release("old"), Candidate("new"), pending: Release("previously-staged")));

        Assert.True(view.IsSelectedCandidate);
        Assert.Equal(PluginPendingKind.Build, view.PendingKind);
        Assert.False(view.IsPendingCandidateBuild);
        Assert.True(view.CandidateChoiceRequired);
        Assert.Equal("previously-staged", view.Pending!.ExecutionRevision);
    }

    [Fact]
    public void BrokenCandidateUsesLabelledActiveDescriptionWithoutInventingItsVersion()
    {
        PluginReleaseSnapshot active = Release("active", version: "1.2.3");
        PluginCandidateSnapshot missing = new(CandidateId, PluginId, null, null, false, "Not found at startup.");
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(active, missing));

        Assert.Equal(PluginDescriptionOrigin.ActiveFallback, view.DescriptionOrigin);
        Assert.True(view.HasRetainedDescription);
        Assert.Equal(active.Description.Description, view.Description);
        Assert.Null(view.CandidateVersion);
        Assert.True(view.CandidateMissing);
        Assert.False(view.CanSelectCandidate);
        Assert.False(view.IsActiveCandidate);
        Assert.Equal(PluginAvailability.Active, view.Availability);
    }

    [Fact]
    public void FailedStartupRetainsLastGoodAsContextWithoutExecutingIt()
    {
        PluginReleaseSnapshot lastGood = Release("last-good");
        PluginCatalogEntry entry = Entry(null, new(CandidateId, PluginId, null, null, true, "Invalid update."),
            lastGood: lastGood, problem: "Startup validation failed.");
        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);

        Assert.Equal(PluginAvailability.Unavailable, view.Availability);
        Assert.Equal(PluginDescriptionOrigin.LastGoodFallback, view.DescriptionOrigin);
        Assert.Null(view.Active);
        Assert.Null(view.CandidateVersion);
        Assert.Same(lastGood, view.LastGood);
        Assert.Equal("Startup validation failed.", view.ActivationProblem);
        Assert.Equal("Invalid update.", view.CandidateProblem);
    }

    [Fact]
    public void DisabledWithoutAnActiveReleaseIsStillDisabled()
    {
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(null, Candidate("valid"), enabled: false));
        Assert.Equal(PluginAvailability.Disabled, view.Availability);
        Assert.True(view.CandidateChoiceRequired);
    }

    [Fact]
    public void AnUnobservedCSharpFileHasNoInventedSourceCapability()
    {
        PluginCatalogEntry entry = new("new-file", null, "new-file", "/fixture/filter.cs", null, null, null, []);
        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);

        Assert.Equal(PluginAvailability.NotLoaded, view.Availability);
        Assert.Equal(PluginDescriptionOrigin.Unavailable, view.DescriptionOrigin);
        Assert.Null(view.Description);
        Assert.False(view.IsSource);
        Assert.False(view.IsFilter);
        Assert.False(view.CanSelectCandidate);
        Assert.Empty(view.Sources);
        Assert.Empty(view.Filters);
    }

    [Fact]
    public void MixedPluginKeepsBothDeclaredCapabilityKinds()
    {
        SourceDescription source = new("fixture-source", "Fixture source", new("https://fixture.invalid/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.OriginalMedia, [], [], new("1", []));
        FilterDescription filter = new("fixture-filter", "Fixture filter", "Checks static JPEG.",
            FilterDataAccess.OriginalImage, new("1", []), ["image/jpeg"]);
        PluginDescription description = Description() with { Sources = [source], Filters = [filter] };
        PluginCandidateSnapshot candidate = Candidate("current") with { Description = description };
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(Release("old"), candidate));

        Assert.True(view.IsSource);
        Assert.True(view.IsFilter);
        Assert.Same(source, Assert.Single(view.Sources));
        Assert.Same(filter, Assert.Single(view.Filters));
        Assert.Equal(PluginDescriptionOrigin.Candidate, view.DescriptionOrigin);
        Assert.False(view.HasRetainedDescription);
    }

    [Fact]
    public void DuplicateCandidatesKeepExactKeysAndPluginWideAvailability()
    {
        PluginReleaseSnapshot active = Release("active");
        PluginCandidateSnapshot alternative = Candidate("alternative", "candidate-b");
        PluginCatalogEntry first = Entry(active, Candidate("active"));
        PluginActivationSnapshot state = first.Activation! with
        {
            Candidates = new[] { first.Candidate!, alternative },
            Problem = "Several candidates exist; the selected candidate is retained."
        };
        PluginCatalogEntry second = new(alternative.CandidateId, PluginId, alternative.CandidateId, "/fixture/b.cs",
            alternative.Description, state, alternative, []);

        PluginEntryPresentation view = PluginEntryPresentation.Create(second);

        Assert.Equal("candidate-b", view.Key);
        Assert.Equal(PluginAvailability.Active, view.Availability);
        Assert.True(view.HasCandidateConflict);
        Assert.False(view.IsSelectedCandidate);
        Assert.False(view.IsActiveCandidate);
        Assert.True(view.CandidateChoiceRequired);
        Assert.Equal("active", view.Active!.ExecutionRevision);
    }

    [Fact]
    public void AWorkerNeedsExactProductionPluginBuildAndConfigurationIdentity()
    {
        PluginCatalogEntry entry = Entry(Release("build"), Candidate("build"));
        PluginWorkerSnapshot exact = Worker("build", "active-config");
        PluginProductionWorkerSnapshot[] observations =
        [
            new("other-plugin", exact),
            new(PluginId, Worker("other-build", "active-config")),
            new(PluginId, Worker("build", "test-config"))
        ];
        Assert.Null(PluginEntryPresentation.Create(entry, observations).Worker);
        Assert.Null(PluginEntryPresentation.Create(entry).Worker);

        observations = [new(PluginId, exact)];
        Assert.Same(exact, PluginEntryPresentation.Create(entry, observations).Worker);
        Assert.False(PluginEntryPresentation.Create(entry, observations).HasWorkerProblem);
        PluginWorkerSnapshot intervention = exact with { State = "intervention-required" };
        PluginEntryPresentation failing = PluginEntryPresentation.Create(entry, [new(PluginId, intervention)]);
        Assert.True(failing.HasWorkerProblem);
        Assert.True(failing.WorkerNeedsIntervention);
        PluginWorkerSnapshot unavailable = exact with { State = "unavailable" };
        Assert.True(PluginEntryPresentation.Create(entry, [new(PluginId, unavailable)]).HasWorkerProblem);
        PluginWorkerSnapshot idle = exact with { State = "idle", FailureMessage = "Retained earlier failure." };
        Assert.False(PluginEntryPresentation.Create(entry, [new(PluginId, idle)]).HasWorkerProblem);
        Assert.Null(PluginEntryPresentation.Create(entry, [observations[0], observations[0]]).Worker);
        Assert.Null(PluginEntryPresentation.Create(entry with { Activation = entry.Activation! with { Enabled = false } }, observations).Worker);
    }

    [Fact]
    public void AnotherCandidateDraftDoesNotLeakIntoTheSelectedEntry()
    {
        PluginCatalogEntry other = Entry(Release("other", candidateId: "candidate-b"), Candidate("other", "candidate-b"));
        PluginConfigurationDraft draft = new(other, 2);
        draft.Plugin.Set("count", "not a number");
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(Release("active"), Candidate("active")),
            draft: draft, profileRevision: 3);

        Assert.False(view.HasDraft);
        Assert.False(view.HasUnsavedChanges);
        Assert.False(view.IsDraftStale);
    }

    private static PluginWorkerSnapshot Worker(string build, string configuration) =>
        new(new("instance", build, configuration), "ready", 100, null, null, []);

    private static PluginDescription Description(string version = "1.0.0") =>
        new(PluginId, "Presentation fixture", version, SdkContract.Version, "Reads a supported source.",
            [], [], new("1", [new("count", SettingScope.Plugin, SettingKind.Integer, "Count")]), []);

    private static PluginArtifact Artifact(string execution) =>
        new("compile-" + execution, execution, "/fixture/plugin.dll", "assembly-hash", "sdk-hash", "compiler-hash", []);

    private static PluginReleaseSnapshot Release(string execution, string candidateId = CandidateId,
        string configuration = "active-config", string version = "1.0.0") =>
        new(candidateId, Artifact(execution), Description(version),
            new(configuration, new("1", []), new Dictionary<string, SettingsSnapshot>()));

    private static PluginCandidateSnapshot Candidate(string execution, string candidateId = CandidateId) =>
        new(candidateId, PluginId, Artifact(execution), Description(), true, null, "/fixture/plugin.cs");

    private static PluginCatalogEntry Entry(PluginReleaseSnapshot? active, PluginCandidateSnapshot candidate,
        PluginReleaseSnapshot? pending = null, PluginReleaseSnapshot? pinned = null,
        PluginReleaseSnapshot? lastGood = null, bool enabled = true, string? problem = null)
    {
        PluginActivationSnapshot state = new(PluginId, enabled, 1, candidate.CandidateId, active, pending,
            lastGood, pinned, [candidate], problem);
        return new(candidate.CandidateId, PluginId, candidate.CandidateId, "/fixture/plugin.cs",
            candidate.Description ?? active?.Description ?? lastGood?.Description, state, candidate,
            Array.Empty<PluginBuildDiagnostic>());
    }
}
