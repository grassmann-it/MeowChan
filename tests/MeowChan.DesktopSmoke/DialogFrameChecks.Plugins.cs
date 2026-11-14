using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

// Plugin consent and risk flows: bundled repair/new-selection review, experimental source
// activation and the board admission draft. Frame geometry only; the consent, retry and
// commit contracts stay with PluginReselectionChecks, ExperimentalActivationChecks and
// BoardAdmissionContractChecks.
internal static partial class DialogFrameChecks
{
    private static partial async Task RunPluginsAsync(MainWindow owner)
    {
        string language = Texts.Language;
        try
        {
            foreach (string current in DialogGallery.Active ? new[] { language } : new[] { "en", "de" })
            {
                Texts.SetLanguage(current); await Settle();
                await CheckBundledReviewFrameAsync(owner, current);
                await CheckExperimentalActivationFrameAsync(owner, current);
                await CheckBoardAdmissionFrameAsync(owner, current);
            }
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task CheckBundledReviewFrameAsync(MainWindow owner, string language)
    {
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "dialog-frame-plugins-" + language + (DialogGallery.Active ? "-" + DialogGallery.Theme : "")));
        PreviewOnlyStore store = new(fixture.Store);
        var original = fixture.Services;
        PluginManagementServices services = new(store, original.Compiler, original.Inventory, original.PluginDirectory,
            original.DescribeTestAsync, original.StartTestWorkerAsync, original.ProbeAsync);
        PluginsPage page = new(); page.Attach(services);
        ContentControl host = owner.FindControl<ContentControl>("PluginsPageHost")!;
        object? previous = host.Content;
        host.Content = page;
        try
        {
            owner.GetLogicalDescendants().OfType<MenuItem>().First(item => Equals(item.Tag, "nav.plugins"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await page.ActivateAsync();
            foreach (bool escape in new[] { false, true })
            {
                Window modal = await OpenBundledReviewAsync(owner, page);
                string label = "plugins-bundled-review-" + language;
                if (!escape)
                {
                    await VerifyFrameAsync(modal, label, label);
                    Control strip = Part<Control>(modal, DialogFrame.ErrorStripName, label);
                    Require(!strip.IsVisible, label + ": empty error slot is visible.");
                    // The modal sets no MinWidth/MinHeight; 320 is its own floor for both sides.
                    modal.Width = 320; modal.Height = 320;
                    await VerifyFrameAsync(modal, label + "-minimum", label + "-minimum");
                    Button cancel = modal.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, Texts.T("plugins.repair.cancel")));
                    Require(ReferenceEquals(modal.FocusManager?.GetFocusedElement(), cancel), label + ": Cancel lost initial focus.");
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await Program.WaitUntilAsync(() => !modal.IsVisible && !page.IsBusy);
                Require(store.Writes == 0, label + ": closing the review wrote profile state.");
            }
            Require(fixture.ForbiddenCalls == 0, "Bundled review frame invoked compiler, worker or network services.");
        }
        finally { page.EndLifetime(); host.Content = previous; }
    }

    private static async Task<Window> OpenBundledReviewAsync(MainWindow owner, PluginsPage page)
    {
        owner.Activate();
        await Program.WaitUntilAsync(() => owner.IsActive && !page.IsBusy);
        page.FindControl<Button>("PluginsReviewRepairs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string title = Texts.T("plugins.reselection.reviewTitle");
        await Program.WaitUntilAsync(() => owner.OwnedWindows.Any(w => w.IsVisible && w.Title == title));
        Window modal = owner.OwnedWindows.Single(w => w.IsVisible && w.Title == title);
        await Program.WaitUntilAsync(() => modal.GetLogicalDescendants().OfType<CheckBox>().Any());
        return modal;
    }

    private static async Task CheckExperimentalActivationFrameAsync(MainWindow owner, string language)
    {
        var target = new ExperimentalSourceActivationTarget(Guid.NewGuid(), "dialog-frame-profile", "dialog.frame.owner",
            "dialog-frame-source", "Dialog frame source", "https://dialog-frame.invalid/demo/res/1.html", "candidate",
            "execution", "fingerprint", "sha256", 1, 1, 1, IsEnabled: false, IsGranted: false);
        var actual = new ExperimentalSourceActivationActualState(true, true, false, false, 1, 1, 1);
        const string failure = "Controlled dialog frame grant failure";
        foreach (bool escape in new[] { false, true })
        {
            int calls = 0;
            ExperimentalActivationDialog modal = new(target, _ =>
            {
                calls++;
                return Task.FromResult(new ExperimentalSourceActivationResult(ExperimentalSourceActivationStatus.GrantFailed, actual, target, failure));
            });
            Task<ExperimentalActivationDecision?> result = modal.ShowDialog<ExperimentalActivationDecision?>(owner);
            string label = "plugins-experimental-activation-" + language;
            await Program.WaitUntilAsync(() => modal.IsVisible && modal.FindControl<Button>("ActivationConfirm")!.IsEnabled);
            if (escape)
            {
                modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require((await result)?.ContinueAdmission == false && calls == 0, label + ": Escape did not dismiss without activation.");
                continue;
            }
            Require(!Part<Control>(modal, DialogFrame.ErrorStripName, label).IsVisible, label + ": error strip shown before a failure.");
            await VerifyFrameAsync(modal, label, label);
            modal.FindControl<Button>("ActivationConfirm")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => calls == 1 && modal.FindControl<TextBlock>("ActivationStatus")!.IsEffectivelyVisible);
            Require(!modal.FindControl<Grid>("ActivationBusyPanel")!.IsVisible, label + ": busy status remained beside the failure.");
            await VerifyErrorStripAsync(modal, label + "-error", failure);
            Capture(modal, label + "-error");
            modal.Width = modal.MinWidth; modal.Height = modal.MinHeight;
            await Program.WaitUntilAsync(() => Math.Abs(modal.ClientSize.Width - modal.MinWidth) <= 1
                && Math.Abs(modal.ClientSize.Height - modal.MinHeight) <= 1);
            await VerifyErrorStripAsync(modal, label + "-error-minimum", failure);
            Capture(modal, label + "-error-minimum");
            modal.FindControl<Button>("ActivationCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ExperimentalActivationDecision? decision = await result;
            Require(decision?.ContinueAdmission == false && decision.Error?.Contains(failure, StringComparison.Ordinal) == true && calls == 1,
                label + ": Close after failure changed the decision or retried.");
        }
    }

    private static async Task CheckBoardAdmissionFrameAsync(MainWindow owner, string language)
    {
        const string board = "https://boards.4chan.org/g/";
        var policy = new BoardAdmissionPolicy([new(ThreadTextField.Title, ThreadTextAction.Include, "cat")]);
        BoardAdmissionPreview Preview(string url) => new(url, "Offline dialog frame catalog", policy,
            Enumerable.Range(1, 12).Select(i => new BoardCatalogThread(i.ToString(), url + "thread/" + i, "cat thread " + i, "Opening post " + i)).ToArray());
        foreach (bool failing in new[] { false, true })
        {
            BoardAdmissionWindow modal = new([board], _ => policy, (url, _) => failing
                ? Task.FromException<BoardAdmissionPreview>(new IOException("Controlled dialog frame catalog failure"))
                : Task.FromResult(Preview(url)), showWizard: true);
            Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
            string label = "plugins-board-admission-" + language + (failing ? "-catalog-failure" : "");
            await Program.WaitUntilAsync(() => failing
                ? Find<Button>(modal, "BoardAdmissionRetry")?.IsVisible == true
                : Find<Button>(modal, "BoardAdmissionApply")?.IsEnabled == true);
            await VerifyFrameAsync(modal, label, label);
            modal.Width = modal.MinWidth; modal.Height = modal.MinHeight;
            await Program.WaitUntilAsync(() => Math.Abs(modal.ClientSize.Width - modal.MinWidth) <= 1
                && Math.Abs(modal.ClientSize.Height - modal.MinHeight) <= 1);
            await VerifyFrameAsync(modal, label + "-minimum", label + "-minimum");
            if (failing) Find<Button>(modal, "BoardAdmissionCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(await result == null, label + ": Cancel/Escape returned a selection.");
        }
    }

    /// <summary>Passive preview rows over the fixture store; no write API is reachable from Cancel/Escape.</summary>
    private sealed class PreviewOnlyStore(IPluginProfileStore inner) : IPluginProfileStore
    {
        private const long Revision = 4242;
        internal int Writes { get; private set; }
        private static readonly BundledCandidateReselection NewChoice = new("frame-new-choice", "old-id", "new-id", "slot",
            "/old/path.cs", "/new/path.cs", "old-execution", "new-execution", "{\n  \"pluginSettings\": {}\n}", null);
        private static readonly BundledCandidateReselection Blocked = NewChoice with { PluginId = "frame-blocked-choice", BlockedReason = "pinned" };
        private static readonly BundledCandidateRepair Repair = new("frame-proven-repair", "proven-old", "proven-new", "slot",
            "/proven/old", "/proven/new", "execution-old", "execution-new", true, null);
        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BundledCandidateRepairPreview(Revision, Enumerable.Range(0, 6)
                .Select(i => Repair with { PluginId = "frame-legacy-" + i, BlockedReason = "unproven-history" }).Append(Repair).ToArray()));
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BundledCandidateReselectionPreview(Revision, new[] { NewChoice, Blocked }));
        private Task<PluginProfileSnapshot> Forbidden() { Writes++; throw new InvalidOperationException("Unexpected dialog frame mutation."); }
        public Task<PluginProfileSnapshot> ConfirmBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ReauthorizeBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ConfirmBundledReselectionAsync(BundledCandidateReselection selection, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
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
