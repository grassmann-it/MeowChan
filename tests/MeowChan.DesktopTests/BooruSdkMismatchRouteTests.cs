using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// The Danbooru page's repair route for a running build that fails with sdk-mismatch: one truthful sentence with the whole
/// route, one name for the review dialog, a calm restart notice after a saved confirmation, a named reason when the route
/// cannot run or is blocked, and the Plugins card's matching states. Fixtures only: fake ports and a fake profile store,
/// no provider request and no trust decision (the store's own previews decide; these tests only present them).
/// </summary>
[Collection("Localization")]
public sealed class BooruSdkMismatchRouteTests : IDisposable
{
    private const string PluginId = "meowchan.official.danbooru";
    private const string Old = "b42eea85-old";
    private const string Current = "729f9a13-current";

    public BooruSdkMismatchRouteTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    // ---- the derived stage ---------------------------------------------------------------------------------

    [Fact]
    public void AnUnavailableStartupSelectionHasARepairRouteEvenWithoutABooruPage()
    {
        var missing = Plugin(active: null) with { Candidates = [Candidate(Old) with { IsPresent = false }] };
        Assert.True(PluginSession.NeedsDanbooruStartupRepair(missing));
        Assert.False(PluginSession.NeedsDanbooruStartupRepair(missing with { Enabled = false }));
        Assert.False(PluginSession.NeedsDanbooruStartupRepair(missing with { Active = Release(Current) }));
        Assert.True(PluginSession.NeedsDanbooruStartupRepair(Plugin(active: null, problem: PluginActivationProblems.ExecutableChanged)));
        Assert.False(PluginSession.NeedsDanbooruStartupRepair(Plugin(active: null) with { SelectedCandidateId = "other" }));
    }

    [Fact]
    public void APinnedSameBundleRebuildIsReviewableAndSaysThatConfirmingReleasesThePin()
    {
        PluginActivationSnapshot plugin = Plugin(active: Release(Old), pinned: Release(Old));
        BooruBuildRepair repair = BooruBuildRepair.Derive(true, plugin, Old, Selection(null), null);
        Assert.Equal(BooruRepairStage.Reviewable, repair.Stage);
        Assert.True(repair.ReleasesPin);
        Assert.False(BooruBuildRepair.Derive(true, Plugin(active: Release(Old)), Old, Selection(null), null).ReleasesPin);
    }

    [Fact]
    public void APendingBuildOtherThanTheRunningOneIsAConfirmedRepairAndASameBuildSettingsChangeIsNot()
    {
        PluginActivationSnapshot confirmed = Plugin(active: null, pending: Release(Current));
        Assert.Equal(BooruRepairStage.ConfirmedPending, BooruBuildRepair.Derive(true, confirmed, Old, null, null).Stage);
        PluginActivationSnapshot settings = Plugin(active: Release(Old), pending: Release(Old, "next-config"));
        Assert.NotEqual(BooruRepairStage.ConfirmedPending, BooruBuildRepair.Derive(true, settings, Old, Selection(null), null).Stage);
    }

    [Theory]
    [InlineData("pinned", "plugins.reselection.blocked.pinned")]
    [InlineData("ambiguous", "plugins.reselection.blocked.ambiguous")]
    [InlineData("inherited-grants", "plugins.reselection.blocked.inherited-grants")]
    [InlineData("future-reason", "plugins.reselection.blocked.unknown")]
    public void ABlockedPreviewNamesTheDialogsReason(string reason, string key)
    {
        BooruBuildRepair repair = BooruBuildRepair.Derive(true, Plugin(active: Release(Old), pinned: Release(Old)), Old, Selection(reason), null);
        Assert.Equal(BooruRepairStage.Blocked, repair.Stage);
        Assert.Equal(key, repair.BlockedReasonKey);
    }

    [Fact]
    public void NothingToReviewIsBlockedWithItsOwnReasonAndAnOfferedRepairRowIsReviewable()
    {
        BooruBuildRepair none = BooruBuildRepair.Derive(true, Plugin(active: Release(Old)), Old, null, null);
        Assert.Equal(BooruRepairStage.Blocked, none.Stage);
        Assert.Equal(BooruBuildRepair.NothingToReviewKey, none.BlockedReasonKey);
        BundledCandidateRepair proven = new(PluginId, "old", "new", "slot", "/old", "/new", Old, Current, true, null);
        Assert.Equal(BooruRepairStage.Reviewable, BooruBuildRepair.Derive(true, Plugin(active: Release(Old)), Old, null, proven).Stage);
        Assert.Equal("plugins.repair.blocked.unproven-history",
            BooruBuildRepair.Derive(true, Plugin(active: Release(Old)), Old, null, proven with { BlockedReason = "unproven-history" }).BlockedReasonKey);
    }

    [Fact]
    public async Task TheProbeReadsTheStoreOnlyAfterAMismatchAndUsesTheSessionObservation()
    {
        FakeStore store = new() { Profile = Profile(Plugin(active: Release(Old), pinned: Release(Old))), Selection = Selection(null) };
        PluginSdkMismatchLedger ledger = new();
        BooruBuildRepairProbe probe = new(PluginId, store, () => Old, ledger);
        Assert.Same(BooruBuildRepair.None, await probe.ReadAsync(mismatchKnown: false, CancellationToken.None));
        Assert.Equal(0, store.Reads);
        int changes = 0;
        ledger.Changed += () => changes++;
        Assert.True(ledger.Record(PluginId, Old));
        Assert.False(ledger.Record(PluginId, Old));
        Assert.Equal(1, changes);
        BooruBuildRepair observed = await probe.ReadAsync(mismatchKnown: false, CancellationToken.None);
        Assert.True(observed.MismatchObserved);
        Assert.Equal(BooruRepairStage.Reviewable, observed.Stage);
        Assert.True(observed.ReleasesPin);
        // After the dialog saved the confirmation the store holds a pending build: the page shows the restart notice.
        store.Profile = Profile(Plugin(active: null, pending: Release(Current)));
        store.Selection = null;
        Assert.Equal(BooruRepairStage.ConfirmedPending, (await probe.ReadAsync(false, CancellationToken.None)).Stage);
        Assert.Equal(0, store.Writes);
    }

    // ---- the page --------------------------------------------------------------------------------------------

    [BooruSharedFact]
    public async Task TheMismatchStatusStatesTheWholeRouteOnBothTabsAndOffersTheSharedReviewName()
    {
        using Fixture f = await Fixture.MismatchAsync();
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Reviewable, ReleasesPin: true);
        var search = f.View.SearchRepairForTests;
        string text = search.Text.Text!;
        Assert.Contains("(sdk-mismatch)", text);
        Assert.Contains("open “Review bundled repairs”, confirm the current bundled Danbooru build there and restart MeowChan", text);
        Assert.Contains("confirming releases the pin and replaces the pinned build with the current bundled one", text);
        Assert.Contains("stays in the backup", text);
        Assert.DoesNotContain("not changed automatically", text);
        Assert.True(search.Host.Classes.Contains("workspace-error"));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(search.Text));
        Assert.True(search.Review.IsVisible);
        Assert.True(search.Review.IsEnabled);
        Assert.True(search.Review.Focusable && search.Review.IsTabStop); // Tab reaches it; Enter/Space are the Button's own activation
        Assert.Equal("Review bundled repairs", search.Review.Content);
        Assert.Equal("Review bundled repairs", AutomationProperties.GetName(search.Review));
        Assert.False(search.Retry.IsVisible); // searching again before the restart fails the same way
        Assert.False(search.Open.IsVisible);

        var watchers = f.View.WatcherRepairForTests;
        Assert.True(watchers.Host.IsVisible);
        Assert.Equal(text, watchers.Text.Text);
        Assert.True(watchers.Review.IsVisible && watchers.Review.IsEnabled);
        Assert.Equal("Review bundled repairs", AutomationProperties.GetName(watchers.Review));
    }

    [BooruSharedFact]
    public async Task AConfirmedRepairShowsTheCalmRestartNoticeWithoutSearchAgainOrReview()
    {
        using Fixture f = await Fixture.MismatchAsync();
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.ConfirmedPending);
        var search = f.View.SearchRepairForTests;
        Assert.StartsWith("Repair confirmed. Restart MeowChan to use the current Danbooru build.", search.Text.Text);
        Assert.Contains("Quit MeowChan completely", search.Text.Text);
        Assert.False(search.Host.Classes.Contains("workspace-error"));
        Assert.True(search.Host.Classes.Contains("workspace-status"));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(search.Text));
        Assert.False(search.Retry.IsVisible);
        Assert.False(search.Review.IsVisible);
        Assert.False(search.Open.IsVisible);
        Assert.StartsWith("Repair confirmed.", f.View.WatcherRepairForTests.Text.Text);
        Assert.False(f.View.WatcherRepairForTests.Review.IsVisible);
    }

    [BooruSharedFact]
    public void TheSessionObservationAloneShowsTheRouteWhenThePageIsOpenedLater()
    {
        // A fresh page of the same session: nothing was searched here, the session observed the mismatch earlier.
        BooruUiHarness h = new();
        using BooruWorkbenchModel model = new(h.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false) { ReviewCurrentBuild = () => Task.CompletedTask };
        Assert.False(view.WatcherRepairForTests.Host.IsVisible);
        Assert.StartsWith("Enter tags and press Search", view.SearchRepairForTests.Text.Text);
        view.Repair = new BooruBuildRepair(true, BooruRepairStage.ConfirmedPending);
        Assert.StartsWith("Repair confirmed. Restart MeowChan", view.SearchRepairForTests.Text.Text);
        Assert.True(view.WatcherRepairForTests.Host.IsVisible);
    }

    [BooruSharedFact]
    public async Task ABlockedPreviewNamesTheReasonAndTheNextStepInsteadOfOpeningAnEmptyDialog()
    {
        using Fixture f = await Fixture.MismatchAsync();
        f.View.OpenPluginDetails = () => Task.CompletedTask;
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Blocked, BlockedReasonKey: "plugins.reselection.blocked.pinned");
        var search = f.View.SearchRepairForTests;
        Assert.Contains("cannot be confirmed from here: " + Texts.T("plugins.reselection.blocked.pinned"), search.Text.Text);
        Assert.Contains("Next: open the Danbooru plugin under Plugins", search.Text.Text);
        Assert.False(search.Review.IsVisible);
        Assert.True(search.Open.IsVisible);
        Assert.Equal("Open Danbooru plugin", AutomationProperties.GetName(search.Open));
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Blocked, BlockedReasonKey: BooruBuildRepair.NothingToReviewKey);
        Assert.Contains("No current bundled Danbooru build is waiting for review.", search.Text.Text);
    }

    [BooruSharedFact]
    public async Task TheReviewIsDisabledWithAVisibleReasonWhileTheRouteCannotRunAndANoticeIsShownAndCleared()
    {
        using Fixture f = await Fixture.MismatchAsync();
        string? reason = "booru.ui.repair.busy";
        f.View.ReviewUnavailableReason = () => reason;
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Reviewable);
        var search = f.View.SearchRepairForTests;
        Assert.True(search.Review.IsVisible);
        Assert.False(search.Review.IsEnabled);
        Assert.Equal(Texts.T("booru.ui.repair.busy"), ToolTip.GetTip(search.Review));
        Assert.Equal(Texts.T("booru.ui.repair.busy"), AutomationProperties.GetHelpText(search.Review));
        Assert.True(search.Note.IsVisible);
        Assert.Equal(Texts.T("booru.ui.repair.busy"), search.Note.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(search.Note));
        reason = null;
        f.View.RefreshRepairPresentation();
        Assert.True(search.Review.IsEnabled);
        Assert.False(search.Note.IsVisible);
        f.View.ShowRepairNotice("booru.ui.repair.notOpened", Texts.T("plugins.repair.review"));
        Assert.Equal("The review did not open. Open Plugins and choose “Review bundled repairs” there.", search.Note.Text);
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Reviewable);
        Assert.False(search.Note.IsVisible); // a new reading retires the transient notice
    }

    [BooruSharedFact]
    public async Task WithoutAHostRouteNoReviewButtonIsOffered()
    {
        using Fixture f = await Fixture.MismatchAsync(route: false);
        f.View.Repair = new BooruBuildRepair(true, BooruRepairStage.Reviewable);
        Assert.False(f.View.SearchRepairForTests.Review.IsVisible);
        Assert.False(f.View.SearchRepairForTests.Retry.IsVisible);
    }

    [Fact]
    public void AWatcherCheckOfAMismatchedBuildSaysWhatItIsInsteadOfDisabledOrNotRunning()
    {
        DateTimeOffset now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        DanbooruWatcherCheckSummary summary = new(DanbooruWatcherCheckKind.SourceUnavailable, now, now, 0, 0, 0, 0, 0, 0, 0,
            Code: BooruText.SdkMismatchCode, Manual: true);
        BooruWatcherCommandResult result = BooruSchedulerWatcherPort.Check(summary);
        Assert.Equal(BooruCodes.PluginSdkMismatch, result.Code);
        string text = BooruCodeTexts.Localized(result.Code!);
        Assert.Contains("(sdk-mismatch)", text);
        Assert.DoesNotContain("disabled or not running", text);
        Assert.Equal(BooruCodes.SourceUnavailable, BooruSchedulerWatcherPort.Check(summary with { Code = "worker-exit" }).Code);
    }

    // ---- the Plugins page card ---------------------------------------------------------------------------------

    [Fact]
    public void AMismatchedRunningBuildIsNotPresentedAsActiveAndOffersTheSameReview()
    {
        PluginCatalogEntry entry = Entry(Plugin(active: Release(Old), pinned: Release(Old)), Candidate(Current));
        PluginEntryPresentation plain = PluginEntryPresentation.Create(entry);
        Assert.False(plain.HasSdkMismatch);
        PluginEntryPresentation view = PluginEntryPresentation.Create(entry, sdkMismatch: (id, build) => id == PluginId && build == Old);
        Assert.True(view.HasSdkMismatch);
        Assert.True(view.NeedsRepairReview);
        string status = PluginContentPresentation.DescribeStatus(view);
        Assert.StartsWith("Plugin: Incompatible build (sdk-mismatch)", status);
        Assert.DoesNotContain("Plugin: Active", status);
        Assert.Contains(Texts.T("plugins.problem"), status);
        Assert.StartsWith("The running build was made for another MeowChan version and cannot work (sdk-mismatch). Choose “Review bundled repairs”",
            PluginContentPresentation.DescribeStatusHint(view));
        PluginLifecycleView lifecycle = PluginLifecycleSteps.Create(entry, view);
        Assert.Equal(PluginNextAction.ReviewRepair, lifecycle.Next);
        Assert.False(lifecycle.ActiveNow);
        Assert.Equal(PluginStepState.Problem, lifecycle.Steps[2].State);
        // Another build of the same plugin is not this session's mismatch.
        Assert.False(PluginEntryPresentation.Create(entry, sdkMismatch: (_, build) => build == "other").HasSdkMismatch);
    }

    [Fact]
    public void AfterConfirmationTheCardSaysConfirmedForNextStartInsteadOfAProblem()
    {
        PluginActivationSnapshot confirmed = Plugin(active: null, pending: Release(Current), problem: PluginEntryPresentation.ReselectionConfirmedNote);
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(confirmed, Candidate(Current)), sdkMismatch: (_, _) => true);
        Assert.False(view.HasSdkMismatch); // nothing is active in the profile any more
        Assert.True(view.IsConfirmedForNextStart);
        Assert.Null(view.VisibleActivationProblem);
        string status = PluginContentPresentation.DescribeStatus(view);
        Assert.StartsWith("Plugin: Confirmed for next start — restart to apply", status);
        Assert.DoesNotContain(Texts.T("plugins.problem"), status);
        Assert.Contains("Quit MeowChan completely and start it again", PluginContentPresentation.DescribeStatusHint(view));
        Assert.Equal(PluginNextAction.RestartToUse, PluginLifecycleSteps.Create(Entry(confirmed, Candidate(Current)), view).Next);
        // Any other problem text stays a problem.
        PluginEntryPresentation failing = PluginEntryPresentation.Create(Entry(confirmed with { Problem = "Startup failed." }, Candidate(Current)));
        Assert.Equal("Startup failed.", failing.VisibleActivationProblem);
    }

    [Fact]
    public void TheConfirmationNoteTheCardRecognizesIsTheStoresOwnNote()
    {
        string store = File.ReadAllText(Source("MeowChan.PluginHost/Configuration/PluginProfileStore.Reselection.cs"));
        Assert.Contains("Problem = \"" + PluginEntryPresentation.ReselectionConfirmedNote + "\"", store);
    }

    // ---- one name ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("pl")]
    public void EveryEntryPointOfTheReviewDialogUsesTheOneSharedName(string language)
    {
        Texts.SetLanguage(language);
        string name = Texts.T("plugins.repair.review");
        Assert.NotEqual("plugins.repair.review", name);
        // The Booru route sentence and the Plugins hint quote exactly that name.
        Assert.Contains(name, Texts.T("booru.ui.status.pluginMismatch", name));
        Assert.Contains(name, PluginContentPresentation.SdkMismatchHint());
        foreach (string key in new[] { "booru.ui.repair.releasesPin", "booru.ui.repair.confirmed", "booru.ui.repair.blocked",
            "plugins.reselection.releasesPin", "plugins.state.sdkMismatch", "plugins.state.confirmedNextStart", "plugins.step.next.sdkMismatch" })
            Assert.NotEqual(key, Texts.T(key));
        if (language is not ("en" or "de"))
            Assert.NotEqual(BuiltInCatalog.English["booru.ui.repair.confirmed"], Texts.T("booru.ui.repair.confirmed"));
    }

    [Fact]
    public void TheEntryPointsShareOneKeyAndTheOldNamesAreGone()
    {
        foreach (string key in new[] { "booru.ui.status.reviewBuild", "plugins.repair.reviewNow", "plugins.reselection.reviewTitle" })
        {
            Assert.False(BuiltInCatalog.English.ContainsKey(key), key);
            foreach (string pack in Directory.GetFiles(Source("Desktop/Localization/Packs"), "*.json"))
                Assert.False(JsonDocument.Parse(File.ReadAllText(pack)).RootElement.GetProperty("strings").TryGetProperty(key, out _), pack + ": " + key);
        }
        Assert.Contains("DialogWindow.Create(Texts.T(\"plugins.repair.review\")", File.ReadAllText(Source("Desktop/Views/Plugins/PluginsPage.Migration.cs")));
        Assert.Contains("reviewRepair.Content = Texts.T(\"plugins.repair.review\");", File.ReadAllText(Source("Desktop/Views/Plugins/PluginsPage.Actions.cs")));
        Assert.Contains("(\"PluginsReviewRepairs\",\"plugins.repair.review\")", File.ReadAllText(Source("Desktop/Views/Plugins/PluginsPage.axaml.cs")));
        Assert.Contains("string label = Texts.T(\"plugins.repair.review\");", File.ReadAllText(Source("Desktop/Views/Booru/BooruWorkbenchView.Repair.cs")));
        Assert.Equal("Gebündelte Reparaturen prüfen", BuiltInCatalog.German["plugins.repair.review"]);
    }

    [Fact]
    public void TheHostRouteReturnsToTheBooruPageAndReadsThePluginSnapshotAgain()
    {
        string host = File.ReadAllText(Source("Desktop/Booru/MainWindow.BooruRepair.cs"));
        int review = host.IndexOf("shown = await plugins.ReviewBundledBuildsAsync();", StringComparison.Ordinal);
        int back = host.IndexOf("NavigateWorkspace(WorkspacePage.Booru);", StringComparison.Ordinal);
        int reread = host.IndexOf("await RefreshBooruRepairAsync(page);", StringComparison.Ordinal);
        Assert.True(review > 0 && back > review && reread > back, "route order");
        // No new relaunch mechanism: the confirmed state names the restart.
        Assert.DoesNotContain("Process.Start", host);
        Assert.DoesNotContain("ProfileLauncher", host);
    }

    // ---- fixtures --------------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private Fixture(BooruUiHarness harness, BooruWorkbenchModel model, BooruWorkbenchView view) { Harness = harness; Model = model; View = view; }
        internal BooruUiHarness Harness { get; }
        internal BooruWorkbenchModel Model { get; }
        internal BooruWorkbenchView View { get; }

        internal static async Task<Fixture> MismatchAsync(bool route = true)
        {
            BooruUiHarness h = new();
            h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Failed, BooruText.SdkMismatchCode));
            BooruWorkbenchModel model = new(h.Services);
            BooruWorkbenchView view = new(model, ownsModel: false);
            if (route) view.ReviewCurrentBuild = () => Task.CompletedTask;
            await BooruTestSupport.SearchAsync(model.Search, "cat");
            Assert.True(BooruText.CanReviewCurrentBuild(model.Search.View), "the fixture search did not fail with sdk-mismatch");
            return new Fixture(h, model, view);
        }

        public void Dispose() { View.Dispose(); Model.Dispose(); }
    }

    private static PluginActivationSnapshot Plugin(PluginReleaseSnapshot? active, PluginReleaseSnapshot? pending = null,
        PluginReleaseSnapshot? pinned = null, string? problem = null) =>
        new(PluginId, true, 1, "bundled-danbooru", active, pending, null, pinned, [Candidate(Current)], problem);

    private static PluginProfileSnapshot Profile(PluginActivationSnapshot plugin) => new(7, [plugin], []);

    private static BundledCandidateReselection Selection(string? blocked) =>
        new(PluginId, "bundled-danbooru", "bundled-danbooru", "slot", "/plugins/danbooru", "/plugins/danbooru", Old, Current, "{}", blocked);

    private static PluginReleaseSnapshot Release(string execution, string configuration = "config") =>
        new("bundled-danbooru", Artifact(execution), Description(), new(configuration, new("1", []), new Dictionary<string, SettingsSnapshot>()));

    private static PluginCandidateSnapshot Candidate(string execution) =>
        new("bundled-danbooru", PluginId, Artifact(execution), Description(), true, null, "/plugins/danbooru/plugin.json");

    private static PluginCatalogEntry Entry(PluginActivationSnapshot state, PluginCandidateSnapshot candidate) =>
        new(candidate.CandidateId, PluginId, candidate.CandidateId, null, candidate.Description, state, candidate, Array.Empty<PluginBuildDiagnostic>());

    private static PluginDescription Description() =>
        new(PluginId, "Danbooru", "1.1.0", SdkContract.Version, "Searches Danbooru.", [], [], new("1", []), []);

    private static PluginArtifact Artifact(string execution) =>
        new("compile-" + execution, execution, "/plugins/danbooru/" + execution + ".dll", "assembly", "sdk", "compiler", []);

    private static string Source(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", relative));

    /// <summary>Read-only fake: previews and reads are counted; any write fails the test.</summary>
    private sealed class FakeStore : IPluginProfileStore
    {
        internal PluginProfileSnapshot Profile { get; set; } = new(1, [], []);
        internal BundledCandidateReselection? Selection { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add { } remove { } }
        public event Action<PluginGrantChange>? GrantChanged { add { } remove { } }
        public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(Profile); }
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new BundledCandidateReselectionPreview(Profile.Revision, Selection is null ? [] : [Selection])); }
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new BundledCandidateRepairPreview(Profile.Revision, [])); }
        private Task<PluginProfileSnapshot> Write() { Writes++; throw new InvalidOperationException("The repair route must not write."); }
        public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default) => Write();
        public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default) => Write();
    }
}
