using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

/// <summary>Real native modal/input checks over a passive catalog. No candidate code executes.</summary>
internal static class PluginReselectionChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "reselection-ui"));
        RecordingStore store = new(fixture.Store);
        var original = fixture.Services;
        PluginManagementServices services = new(store, original.Compiler, original.Inventory, original.PluginDirectory,
            original.DescribeTestAsync, original.StartTestWorkerAsync, original.ProbeAsync);
        PluginsPage page = new(); page.Attach(services);
        store.OwnerReady = () => owner.IsActive && CommandRouter.IsRoutingContextActive(page, page);
        ContentControl host = owner.FindControl<ContentControl>("PluginsPageHost")!;
        object? previous = host.Content;
        string language = Texts.Language;
        host.Content = page;
        try
        {
            owner.GetLogicalDescendants().OfType<MenuItem>().First(item => Equals(item.Tag, "nav.plugins"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await page.ActivateAsync();
            foreach (string currentLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(currentLanguage); await Settle();
                int before = store.Calls;
                Window modal = await OpenAsync(owner, page);
                VerifyContents(modal, store);
                using (MediaNativeInput input = new(modal))
                {
                    Button confirm = NewConfirm(modal, store.Row.PluginId);
                    CheckBox ack = Ack(confirm);
                    Require(!confirm.IsEnabled && ack.IsChecked == false, "New selection was initially consented/enabled.");
                    // Even a forged routed click must not bypass the handler's acknowledgement guard.
                    confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(store.Calls == before && modal.IsVisible, "Unchecked click mutated or closed the modal.");
                    await ClickAsync(modal, input, ack);
                    Require(ack.IsChecked == true && confirm.IsEnabled, "Native acknowledgement did not enable confirmation.");
                    await ClickAsync(modal, input, ack);
                    Require(ack.IsChecked == false && !confirm.IsEnabled, "Revoking acknowledgement left confirmation enabled.");
                    Button blocked = NewConfirm(modal, store.Blocked.PluginId);
                    Require(!blocked.IsEnabled && !Ack(blocked).IsEnabled, "Blocked row was actionable.");
                    blocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(store.Calls == before && modal.IsVisible, "Blocked row handler accepted a forged click.");
                    await ClickAsync(modal, input, Cancel(modal));
                }
                await ClosedAsync(page, modal);
                Require(store.Calls == before, "Cancel mutated the profile.");

                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    await ClickAsync(modal, input, Ack(NewConfirm(modal, store.Row.PluginId)));
                    await input.KeyAsync(MediaNativeInput.Escape);
                }
                await ClosedAsync(page, modal);
                Require(store.Calls == before, "Escape after consent mutated the profile.");

                // Language change closes an acknowledged preview rather than reinterpreting its consent.
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                    await ClickAsync(modal, input, Ack(NewConfirm(modal, store.Row.PluginId)));
                Texts.SetLanguage(currentLanguage == "en" ? "de" : "en");
                await ClosedAsync(page, modal);
                Require(store.Calls == before, "Language change submitted a stale preview.");
                Texts.SetLanguage(currentLanguage); await Settle();

                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                    await ClickAsync(modal, input, Ack(NewConfirm(modal, store.Row.PluginId)));
                page.Deactivate();
                await ClosedAsync(page, modal);
                Require(store.Calls == before, "Deactivation submitted a stale preview.");
                await page.ActivateAsync();

                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    await ClickAsync(modal, input, Ack(NewConfirm(modal, store.Row.PluginId)));
                    await ClickAsync(modal, input, NewConfirm(modal, store.Row.PluginId));
                }
                await ClosedAsync(page, modal);
                Require(store.Calls == before + 1 && ReferenceEquals(store.Chosen, store.Row) && store.ChosenRevision == RecordingStore.Revision,
                    "New confirmation did not deliver the exact frozen row and revision exactly once.");
                Require(store.Chosen!.ConfigurationJson == RecordingStore.Configuration, "Exact configuration changed in transit.");
                Require(store.OwnerReadyAtConfirmation, "New choice committed without owner reactivation and active routing context.");

                // The old proven repair remains independently confirmable through its original API.
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                    await ClickAsync(modal, input, Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.repair.confirmNamed", store.Repair.PluginId)));
                await ClosedAsync(page, modal);
                Require(store.Calls == before + 2 && ReferenceEquals(store.ChosenRepair, store.Repair)
                    && store.RepairRevision == RecordingStore.Revision, "Proven repair path lost its row/revision or called new-selection API.");
                Require(store.OwnerReadyAtConfirmation, "Proven repair committed without owner reactivation.");
                store.OfferNewChoice = false;
                modal = await OpenAsync(owner, page);
                VerifyOrder(modal, newFirst: false);
                using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Cancel(modal));
                await ClosedAsync(page, modal);
                Require(store.Calls == before + 2, "Blocked-only review mutated the profile.");
                store.OfferNewChoice = true;
                Console.WriteLine("PLUGIN_RESELECTION " + JsonSerializer.Serialize(new { result = "pass", language = currentLanguage,
                    backend = "private Xvfb/XTest; passive profile-store decorator", exactConfiguration = true,
                    consentCancelEscapeBlockedLanguageDeactivate = true, provenRepair = true, legacyBlockedRows = 15, newChoicesFirst = true, ownerReactivated = true, forbiddenCalls = fixture.ForbiddenCalls }));
            }
            Require(fixture.ForbiddenCalls == 0, "Reselection UI invoked compiler, worker, describe or network services.");
        }
        finally
        {
            page.EndLifetime(); host.Content = previous; Texts.SetLanguage(language);
        }
    }

    private static async Task<Window> OpenAsync(MainWindow owner, PluginsPage page)
    {
        owner.Activate();
        await Program.WaitUntilAsync(() => owner.IsActive && !page.IsBusy);
        Button review = page.FindControl<Button>("PluginsReviewRepairs")!;
        using (MediaNativeInput input = new(owner)) await ClickAsync(owner, input, review);
        Console.WriteLine("PLUGIN_RESELECTION owned " + JsonSerializer.Serialize(owner.OwnedWindows.Select(w => new { w.Title, w.IsVisible, w.IsActive })));
        await Program.WaitUntilAsync(() => owner.OwnedWindows.Any(w => w.IsVisible && w.Title == Texts.T("plugins.reselection.reviewTitle")));
        Window modal = owner.OwnedWindows.Single(w => w.IsVisible && w.Title == Texts.T("plugins.reselection.reviewTitle"));
        Console.WriteLine("PLUGIN_RESELECTION modal " + JsonSerializer.Serialize(new { modal.Title, modal.IsActive,
            checkboxes = modal.GetVisualDescendants().OfType<CheckBox>().Count(), page.IsBusy }));
        Program.CaptureWindow(modal, "smoke-reselection-modal-" + Texts.Language + ".png");
        modal.Activate();
        using (MediaNativeInput focus = new(modal))
            await Program.WaitUntilAsync(() => modal.IsActive && modal.GetVisualDescendants().OfType<CheckBox>().Any());
        return modal;
    }
    private static void VerifyContents(Window modal, RecordingStore store)
    {
        string[] labels = modal.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(labels.Contains(Texts.T("plugins.reselection.repairSection")) && labels.Contains(Texts.T("plugins.reselection.section")),
            "Proven repair and new selection sections are not separately displayed.");
        Require(labels.Contains(Texts.T("plugins.repair.identities", store.Row.OldCandidateId, store.Row.NewCandidateId!, store.Row.SlotId!)), "Exact IDs/slot missing.");
        Require(labels.Contains(Texts.T("plugins.repair.paths", store.Row.OldPath!, store.Row.NewPath!)), "Exact paths missing.");
        Require(labels.Contains(Texts.T("plugins.repair.executions", store.Row.OldExecutionRevision!, store.Row.NewExecutionRevision!)), "Exact execution revisions missing.");
        TextBox configuration = modal.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == RecordingStore.Configuration);
        Require(configuration.IsReadOnly && configuration.AcceptsReturn && configuration.MaxHeight == 240 && configuration.Text == store.Row.ConfigurationJson,
            "Full exact configuration is not read-only and reviewable.");
        Require(labels.Contains(Texts.T("plugins.reselection.backup")) && labels.Contains(Texts.T("plugins.reselection.policy")), "Safety/backup explanation missing.");
        Require(labels.Contains(Texts.T("plugins.reselection.blocked.pinned")), "Blocked reason is not localized.");
        VerifyOrder(modal, newFirst: true);
        foreach (BundledCandidateRepair repair in store.RepairRows)
            Require(Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.repair.confirmNamed", repair.PluginId)).IsEnabled == repair.CanConfirm,
                "A proven repair row disappeared or changed confirmability.");
    }
    private static void VerifyOrder(Window modal, bool newFirst)
    {
        StackPanel selections = modal.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => AutomationProperties.GetAutomationId(p) == "BundledNewSelections");
        StackPanel repairs = modal.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => AutomationProperties.GetAutomationId(p) == "BundledProvenRepairs");
        modal.UpdateLayout();
        Point selectionOrigin = selections.TranslatePoint(default, modal)!.Value;
        Point repairOrigin = repairs.TranslatePoint(default, modal)!.Value;
        Require(newFirst ? selectionOrigin.Y < repairOrigin.Y : repairOrigin.Y < selectionOrigin.Y,
            "The usable section was buried below the legacy repair rows.");
        Require((newFirst ? selectionOrigin.Y : repairOrigin.Y) >= 0 && (newFirst ? selectionOrigin.Y : repairOrigin.Y) < 40,
            "The first review section is not at the initial scroll origin.");
    }
    private static IEnumerable<Button> Buttons(Window modal) => modal.GetVisualDescendants().OfType<Button>();
    private static Button NewConfirm(Window modal, string id) => Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.reselection.confirmNamed", id));
    private static CheckBox Ack(Button confirm) => ((StackPanel)confirm.Parent!).Children.OfType<CheckBox>().Single();
    private static Button Cancel(Window modal) => Buttons(modal).Single(b => Equals(b.Content, Texts.T("plugins.repair.cancel")));
    private static async Task ClickAsync(Window window, MediaNativeInput input, Control control)
    {
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); control.BringIntoView(); window.UpdateLayout();
            Point? point = control.TranslatePoint(default, window);
            return point is { } p && p.Y >= 0 && p.Y + control.Bounds.Height <= window.ClientSize.Height && control.Bounds.Width > 0; });
        await input.ClickAsync(control); await Settle();
    }
    private static async Task ClosedAsync(PluginsPage page, Window modal)
    { await Program.WaitUntilAsync(() => !modal.IsVisible && !page.IsBusy); await Settle(); }
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordingStore(IPluginProfileStore inner) : IPluginProfileStore
    {
        internal const long Revision = 71029;
        internal const string Configuration = "{\n  \"revision\": \"settings ü exact\",\n  \"pluginSettings\": {\"label\": \" spaces  and \\\"quotes\\\" \"},\n  \"sourceSettings\": {\"pux-source\": {\"limit\": 17}},\n  \"filterSettings\": {},\n  \"reviewTail\": \"END exact configuration 漢字\"\n}";
        internal BundledCandidateReselection Row { get; } = new("new-explicit-choice", "old-id ü", "new-id ü", "delivered-slot", "/old location/ü.cs", "/new location/漢.cs", "old-execution", "new-execution", Configuration, null);
        internal BundledCandidateReselection Blocked { get; } = new("blocked-choice", "pinned-old", "pinned-new", "slot-blocked", "/old", "/new", "old", "new", "{\"blocked\":true}", "pinned");
        internal BundledCandidateRepair Repair { get; } = new("proven-repair", "proven-old", "proven-new", "proven-slot", "/proven/old", "/proven/new", "execution-old", "execution-new", true, null);
        internal bool OfferNewChoice { get; set; } = true;
        internal Func<bool>? OwnerReady { get; set; }
        internal bool OwnerReadyAtConfirmation { get; private set; }
        internal IReadOnlyList<BundledCandidateRepair> RepairRows => Enumerable.Range(0, 15)
            .Select(i => Repair with { PluginId = "legacy-blocked-" + i, BlockedReason = "unproven-history" })
            .Append(Repair).ToArray();
        internal int Calls { get; private set; }
        internal BundledCandidateReselection? Chosen { get; private set; }
        internal long ChosenRevision { get; private set; }
        internal BundledCandidateRepair? ChosenRepair { get; private set; }
        internal long RepairRevision { get; private set; }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BundledCandidateRepairPreview(Revision, RepairRows));
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BundledCandidateReselectionPreview(Revision, new[] { OfferNewChoice ? Row : Row with { BlockedReason = "unproven-configuration" }, Blocked }));
        public Task<PluginProfileSnapshot> ConfirmBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); OwnerReadyAtConfirmation = OwnerReady?.Invoke() == true; Calls++; ChosenRepair = repair; RepairRevision = expectedRevision; return ReadAsync(cancellationToken); }
        public Task<PluginProfileSnapshot> ConfirmBundledReselectionAsync(BundledCandidateReselection selection, long expectedRevision, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); OwnerReadyAtConfirmation = OwnerReady?.Invoke() == true; Calls++; Chosen = selection; ChosenRevision = expectedRevision; return ReadAsync(cancellationToken); }
        private static Task<PluginProfileSnapshot> Forbidden() => throw new InvalidOperationException("Unexpected UI mutation API.");
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
