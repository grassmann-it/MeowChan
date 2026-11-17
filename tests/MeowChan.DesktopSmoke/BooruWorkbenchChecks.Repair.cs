using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Step of the root scenario "booru-workbench": the sdk-mismatch repair route with real control activation over a passive
// profile-store decorator (no worker, compiler, provider or network). Mismatch → native Enter on "Review bundled repairs"
// → the shared dialog over the shell → native consent + confirm → native Escape → back on the Booru page with the restart
// notice and no "Search again"; the blocked preview names its reason and next step. Light/Dark × EN/DE screenshots of
// the three states.
internal static partial class BooruWorkbenchChecks
{
    private const string RepairPluginId = "meowchan.official.danbooru";
    private const string RepairOld = "b42eea85fixtureold";
    private const string RepairCurrent = "729f9a13fixturecurrent";

    private static async Task CheckRepairRouteAsync(MainWindow owner, BooruPage page, BooruUiHarness h, string shots)
    {
        _ = owner.SettingsEditor; // builds the deferred Sources/Plugins pages the route opens
        PluginsPage plugins = (PluginsPage)typeof(MainWindow).GetField("_pluginsPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        FieldInfo servicesField = typeof(PluginsPage).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PluginManagementServices? originalServices = (PluginManagementServices?)servicesField.GetValue(plugins);
        MethodInfo refresh = typeof(MainWindow).GetMethod("RequestBooruRepairRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo navigate = typeof(MainWindow).GetMethod("NavigateWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "booru-repair-route"));
        RepairStore store = new(fixture.Store);
        PluginManagementServices fake = new(store, fixture.Services.Compiler, fixture.Services.Inventory, fixture.Services.PluginDirectory,
            fixture.Services.DescribeTestAsync, fixture.Services.StartTestWorkerAsync, fixture.Services.ProbeAsync);
        PluginSdkMismatchLedger ledger = new();
        TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
        var search = page.View.SearchRepairForTests;
        var watchers = page.View.WatcherRepairForTests;
        try
        {
            Texts.SetLanguage("en");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            tabs.SelectedIndex = 0;
            plugins.Attach(fake);
            owner.BooruRepairProbeForTests = new BooruBuildRepairProbe(RepairPluginId, store, () => RepairOld, ledger);
            h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Failed, BooruText.SdkMismatchCode));
            TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
            input.Text = "cat";
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => page.View.Repair.Stage == BooruRepairStage.Reviewable && search.Review.IsEffectivelyVisible,
                () => "mismatch route not shown: stage=" + page.View.Repair.Stage + " text=" + search.Text.Text);
            string mismatch = search.Text.Text ?? "";
            Require(mismatch.Contains("open “Review bundled repairs”, confirm the current bundled Danbooru build there and restart MeowChan")
                && mismatch.Contains("confirming releases the pin") && page.View.Repair.ReleasesPin,
                "The mismatch status does not state the whole route and the pin consequence: " + mismatch);
            Require(!search.Retry.IsVisible && search.Review.IsEffectivelyEnabled && search.Review.Focusable && search.Review.IsTabStop
                && AutomationProperties.GetName(search.Review) == Texts.T("plugins.repair.review")
                && AutomationProperties.GetLiveSetting(search.Text) == AutomationLiveSetting.Assertive,
                "The mismatch actions are wrong: retry=" + search.Retry.IsVisible + " review=" + search.Review.IsEffectivelyEnabled);
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => watchers.Host.IsEffectivelyVisible && watchers.Review.IsEffectivelyVisible,
                () => "the Watchers tab lacks the repair route");
            Require(watchers.Text.Text == mismatch, "The Watchers tab states another route: " + watchers.Text.Text);
            Capture(owner, shots, "repair-mismatch-watchers-light-en");
            tabs.SelectedIndex = 0;
            await CaptureRepairVariantsAsync(owner, page, shots, "repair-mismatch");

            // ---- blocked: the reason and the next step, never an empty dialog ---------------------------------------------
            store.BlockedReason = "pinned";
            refresh.Invoke(owner, null);
            await Program.WaitUntilAsync(() => page.View.Repair.Stage == BooruRepairStage.Blocked && search.Open.IsEffectivelyVisible,
                () => "blocked route not shown: " + page.View.Repair.Stage);
            Require(!search.Review.IsVisible && (search.Text.Text ?? "").Contains(Texts.T("plugins.reselection.blocked.pinned"))
                && (search.Text.Text ?? "").Contains("Next: open the Danbooru plugin under Plugins"), "The blocked route lacks reason or next step: " + search.Text.Text);
            await CaptureRepairVariantsAsync(owner, page, shots, "repair-blocked");
            store.BlockedReason = null;
            refresh.Invoke(owner, null);
            await Program.WaitUntilAsync(() => page.View.Repair.Stage == BooruRepairStage.Reviewable && search.Review.IsEffectivelyVisible,
                () => "the route did not return to reviewable");

            // ---- real activation: native Enter on the focused primary action -------------------------------------------------
            Texts.SetLanguage("en");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive && !plugins.IsBusy && search.Review.IsEffectivelyEnabled, () => "the shell is not ready for the route");
            search.Review.BringIntoView();
            search.Review.Focus(Avalonia.Input.NavigationMethod.Tab);
            await Program.WaitUntilAsync(() => search.Review.IsFocused, () => "the review action does not take keyboard focus");
            using (MediaNativeInput input0 = new(owner)) await input0.KeyAsync(MediaNativeInput.Enter);
            string title = Texts.T("plugins.repair.review");
            await Program.WaitUntilAsync(() => owner.OwnedWindows.Any(w => w.IsVisible && w.Title == title), () => "the review dialog did not open from the Booru page");
            Window modal = owner.OwnedWindows.Single(w => w.IsVisible && w.Title == title);
            Require(owner.SelectedPage == MainWindow.WorkspacePage.Sources, "The dialog is not owned by the Plugins page route.");
            await Program.WaitUntilAsync(() => modal.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => AutomationProperties.GetAutomationId(t) == "BundledReselectionReleasesPin:" + RepairPluginId && t.IsEffectivelyVisible),
                () => "the dialog does not say that confirming releases the pin");
            Capture(modal, shots, "repair-dialog-light-en");
            modal.Activate();
            Button confirm = modal.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.reselection.confirmNamed", RepairPluginId));
            CheckBox ack = ((StackPanel)confirm.Parent!).Children.OfType<CheckBox>().Single();
            using (MediaNativeInput input1 = new(modal))
            {
                await Program.WaitUntilAsync(() => modal.IsActive, () => "the review dialog is not active");
                await PluginNativeRepairHelper.ClickAsync(modal, input1, ack);
                await Program.WaitUntilAsync(() => ack.IsChecked == true && confirm.IsEffectivelyEnabled, () => "consent did not enable the confirmation");
                await PluginNativeRepairHelper.ClickAsync(modal, input1, confirm);
                await Program.WaitUntilAsync(() => store.Confirmed && modal.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => AutomationProperties.GetAutomationId(t) == "BundledApprovalRestartNote"), () => "the confirmation was not saved");
                Capture(modal, shots, "repair-dialog-saved-light-en");
                await input1.KeyAsync(MediaNativeInput.Escape);
            }
            await Program.WaitUntilAsync(() => !modal.IsVisible, () => "the review dialog did not close");

            // ---- back on Booru: the calm restart notice, no "Search again", focus on the route's status ---------------------
            await Program.WaitUntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Booru
                && page.View.Repair.Stage == BooruRepairStage.ConfirmedPending,
                () => "the route did not return with the confirmed state: page=" + owner.SelectedPage + " stage=" + page.View.Repair.Stage);
            await Program.WaitUntilAsync(() => search.Text.IsFocused, () => "focus did not return to the Booru status: "
                + TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement());
            Require((search.Text.Text ?? "").StartsWith("Repair confirmed. Restart MeowChan to use the current Danbooru build.", StringComparison.Ordinal)
                && !search.Retry.IsVisible && !search.Review.IsVisible && search.Host.Classes.Contains("workspace-status")
                && AutomationProperties.GetLiveSetting(search.Text) == AutomationLiveSetting.Polite,
                "The confirmed state is wrong: " + search.Text.Text + " retry=" + search.Retry.IsVisible);
            Require(store.ConfirmedPluginId == RepairPluginId && store.Writes == 1, "The route wrote something else than the one confirmation.");
            await CaptureRepairVariantsAsync(owner, page, shots, "repair-confirmed");
            // Opened again later in the same session: derived from the session observation and the plugin snapshot, not
            // from a transient flag of the route (the reading is dropped here and the page is left and shown again).
            Click(owner.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Downloads, () => "Downloads navigation refused");
            ledger.Record(RepairPluginId, RepairOld);
            page.View.Repair = BooruBuildRepair.None;
            Require(navigate.Invoke(owner, new object[] { MainWindow.WorkspacePage.Booru, true }) is true, "Booru navigation refused");
            await Program.WaitUntilAsync(() => page.View.Repair.Stage == BooruRepairStage.ConfirmedPending && page.View.Repair.MismatchObserved
                && (search.Text.Text ?? "").StartsWith("Repair confirmed.", StringComparison.Ordinal), () => "reopening lost the confirmed state");
            Console.WriteLine("SMOKE booru repair route: " + JsonSerializer.Serialize(new
            {
                result = "pass", mismatchRoute = true, watchersRoute = true, blockedReasonAndNextStep = true, nativeEnterOpensSharedDialog = true,
                pinLineInDialog = true, nativeConsentConfirmEscape = true, returnedToBooru = true, restartNoticeWithoutSearchAgain = true,
                focusOnStatus = true, restartButton = "none: no safe same-profile relaunch exists", writes = store.Writes, forbiddenCalls = fixture.ForbiddenCalls, shots
            }));
            Require(fixture.ForbiddenCalls == 0, "The route invoked compiler, worker, describe or network services.");
        }
        finally
        {
            owner.BooruRepairProbeForTests = null;
            if (originalServices is not null) plugins.Attach(originalServices); else servicesField.SetValue(plugins, null);
            h.Serve(pages: 3, perPage: 3);
            Texts.SetLanguage("en");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    private static async Task CaptureRepairVariantsAsync(MainWindow owner, BooruPage page, string shots, string name)
    {
        foreach ((string language, ThemeVariant theme, string suffix) in new[]
        {
            ("en", ThemeVariant.Light, "light-en"), ("en", ThemeVariant.Dark, "dark-en"),
            ("de", ThemeVariant.Light, "light-de"), ("de", ThemeVariant.Dark, "dark-de")
        })
        {
            Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = theme;
            string expected = page.View.RepairStatusText;
            await Program.WaitUntilAsync(() => page.ActualThemeVariant == theme && page.View.SearchRepairForTests.Text.Text == expected,
                () => name + " " + suffix + " not rendered");
            await SettleAsync(owner);
            Require(!Text(page).Contains("booru.ui.") && !Text(page).Contains("plugins."), "A raw key is visible in " + name + " " + suffix);
            page.View.SearchRepairForTests.Host.BringIntoView();
            await SettleAsync(owner);
            Capture(owner, shots, name + "-" + suffix);
        }
        Texts.SetLanguage("en");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        await SettleAsync(owner);
    }

    /// <summary>
    /// Passive decorator: the fixture profile plus a pinned Danbooru plugin whose current bundled rebuild is offered as a
    /// confirmable reselection; confirming flips it to "pending for next start" exactly like the real store's result shape.
    /// </summary>
    private sealed class RepairStore(IPluginProfileStore inner) : IPluginProfileStore
    {
        private long _revision = 4102;
        internal string? BlockedReason { get; set; }
        internal bool Confirmed { get; private set; }
        internal string? ConfirmedPluginId { get; private set; }
        internal int Writes { get; private set; }

        private static PluginDescription Description() =>
            new(RepairPluginId, "Danbooru", "1.1.0", SdkContract.Version, "Searches Danbooru.", [], [], new("1", []), []);
        private static PluginArtifact Artifact(string execution) =>
            new("compile-" + execution, execution, "/fixture/danbooru/" + execution + ".dll", "assembly", "sdk", "compiler", []);
        private static PluginReleaseSnapshot Release(string execution) =>
            new("bundled-danbooru", Artifact(execution), Description(), new("config", new("1", []), new Dictionary<string, SettingsSnapshot>()));

        private PluginActivationSnapshot Danbooru() => Confirmed
            ? new(RepairPluginId, true, 1, "bundled-danbooru", null, Release(RepairCurrent), null, null,
                [new("bundled-danbooru", RepairPluginId, Artifact(RepairCurrent), Description(), true, null, "/fixture/danbooru/plugin.json")],
                PluginEntryPresentation.ReselectionConfirmedNote)
            : new(RepairPluginId, true, 1, "bundled-danbooru", Release(RepairOld), null, Release(RepairOld), Release(RepairOld),
                [new("bundled-danbooru", RepairPluginId, Artifact(RepairCurrent), Description(), true, null, "/fixture/danbooru/plugin.json")], null);

        private BundledCandidateReselection Row() => new(RepairPluginId, "bundled-danbooru", "bundled-danbooru", "slot-danbooru",
            "/fixture/danbooru/plugin.json", "/fixture/danbooru/plugin.json", RepairOld, RepairCurrent, "{\"revision\":\"config\"}", BlockedReason);

        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public async Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            PluginProfileSnapshot state = await inner.ReadAsync(cancellationToken);
            return state with { Revision = _revision, Plugins = state.Plugins.Append(Danbooru()).ToArray() };
        }
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BundledCandidateRepairPreview(_revision, []));
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BundledCandidateReselectionPreview(_revision, Confirmed ? [] : [Row()]));
        public async Task<PluginProfileSnapshot> ConfirmBundledReselectionAsync(BundledCandidateReselection selection, long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (expectedRevision != _revision) throw new PluginProfileConflictException(expectedRevision, _revision);
            Require(selection == Row() && selection.CanConfirm, "The dialog confirmed another row than the offered one.");
            Writes++; Confirmed = true; ConfirmedPluginId = selection.PluginId; _revision++;
            return await ReadAsync(cancellationToken);
        }
        private Task<PluginProfileSnapshot> Refuse() { Writes++; throw new InvalidOperationException("The repair route must not write this."); }
        public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
        public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default) => Refuse();
    }
}
