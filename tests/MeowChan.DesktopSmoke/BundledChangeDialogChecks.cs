using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

// The actual attached PluginsPage owns this native modal. Only passive synthetic catalog data is used.
internal static class BundledChangeDialogChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        using IDisposable clipboardScope = PluginNativeRepairHelper.IsolateClipboard();
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "bundled-change-dialog"));
        RecordingStore store = new(fixture);
        var original = fixture.Services;
        SourcesPage sources = MergedSourcesSmoke.Create(new(store, original.Compiler, original.Inventory, original.PluginDirectory,
            original.DescribeTestAsync, original.StartTestWorkerAsync, original.ProbeAsync), out PluginsPage page);
        ContentControl host = owner.FindControl<ContentControl>("SourcesPageHost")!;
        object? previous = host.Content;
        string language = Texts.Language;
        host.Content = sources;
        store.Admit = () => page.IsBusy && !CommandRouter.IsRoutingContextActive(page, page) &&
            owner.OwnedWindows.Any(modal => modal.IsVisible && modal.IsActive && ReferenceEquals(modal.Owner, owner) &&
                CommandRouter.IsRoutingContextActive(modal, modal));
        byte[] untouched = SdkJson.Serialize(await fixture.Store.ReadAsync());
        try
        {
            MergedSourcesSmoke.OpenPlugins(owner);
            await page.ActivateAsync();
            foreach (string current in new[] { "en", "de" })
            {
                Texts.SetLanguage(current); await Settle();
                store.Reset();
                int before = store.Writes.Count;
                Window modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Require(!Save(modal).IsEnabled && Changes(modal).IsChecked == false && Rights(modal).IsChecked == false,
                        "Declaration review started consented or enabled.");
                    Save(modal).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(store.Writes.Count == before, "Unchecked routed Save dispatched a mutation.");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(!Save(modal).IsEnabled, "Change acknowledgement silently authorized existing special rights.");
                    await ClickAsync(modal, input, Rights(modal));
                    Require(Save(modal).IsEnabled, "Independent acknowledgements did not enable Save.");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(!Save(modal).IsEnabled, "Rights consent substituted for change acknowledgement.");
                    await ClickAsync(modal, input, Close(modal));
                }
                await ClosedAsync(page, modal);
                Require(store.Writes.Count == before, "Cancel mutated the profile.");

                modal = await OpenAsync(owner, page);
                Require(!Save(modal).IsEnabled && Changes(modal).IsChecked == false && Rights(modal).IsChecked == false,
                    "Close/reopen revived the preceding row's consent.");
                using (MediaNativeInput input = new(modal))
                {
                    await ClickAsync(modal, input, Changes(modal));
                    await ClickAsync(modal, input, Rights(modal));
                    await input.KeyAsync(MediaNativeInput.Escape);
                }
                await ClosedAsync(page, modal);
                Require(store.Writes.Count == before, "Escape after both consents mutated the profile.");

                store.RemovedValues = true;
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Require(Changes(modal).IsChecked == false && Rights(modal).IsChecked == false,
                        "Escape/reopen revived consent.");
                    await ClickAsync(modal, input, Changes(modal));
                    await ClickAsync(modal, input, Rights(modal));
                    await ClickAsync(modal, input, ById<Button>(modal, "BundledChangeReviewSchemaAcknowledgement:fixture"));
                    RequireFreshChangeConsent(modal, store, before, "Removed-values acknowledgement");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(Save(modal).IsEnabled, "Explicit reconfirmation after removed values did not enable Save.");
                    CheckBox detachedChanges = Changes(modal);
                    await ClickAsync(modal, input, ById<Button>(modal, "BundledApprovalReload"));
                    await Program.WaitUntilAsync(() => ById<Button>(modal, "BundledApprovalReload").IsEnabled &&
                        !ReferenceEquals(Changes(modal), detachedChanges));
                    Require(Changes(modal).IsChecked == false && Rights(modal).IsChecked == false && !Save(modal).IsEnabled,
                        "Refresh revived an old row's change/rights consent.");
                    Require(ById<Button>(modal, "BundledChangeReviewSchemaAcknowledgement:fixture").IsEffectivelyVisible,
                        "Refresh inherited removed-values acknowledgement.");
                    Save(modal).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(store.Writes.Count == before, "Unconfirmed refreshed row dispatched a mutation.");
                    await ClickAsync(modal, input, Close(modal));
                }
                await ClosedAsync(page, modal);
                store.RemovedValues = false;

                // An independently hashed but malformed payload must not create editable/actionable controls.
                store.Malformed = true;
                modal = await OpenAsync(owner, page);
                Require(!modal.GetVisualDescendants().OfType<PluginSettingsEditor>().Any(), "Malformed review created settings editors.");
                Require(!modal.GetVisualDescendants().OfType<Button>().Any(b =>
                    AutomationProperties.GetAutomationId(b) == "BundledChangeReviewSave:fixture" && b.IsEnabled),
                    "Malformed payload remained actionable.");
                Require(modal.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == PluginChangeReviewCatalog.T("plugins.changeReview.invalidReview")),
                    "Malformed review was silently omitted instead of explaining its rejection.");
                Program.CaptureWindow(modal, "smoke-bundled-change-invalid-payload-" + current + ".png");
                using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Close(modal));
                await ClosedAsync(page, modal);
                Require(store.Writes.Count == before, "Malformed payload mutated the profile.");
                store.Malformed = false;

                modal = await OpenAsync(owner, page);
                Require(!Save(modal).IsEnabled && Changes(modal).IsChecked == false && Rights(modal).IsChecked == false,
                    "Consent leaked across canceled/malformed modal openings.");
                PluginSettingsEditor plugin = modal.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(e => e.Draft.Scope == SettingScope.Plugin);
                PluginSettingsEditor source = modal.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(e => e.Draft.Scope == SettingScope.Source);
                TextBox label = (TextBox)plugin.FindField("label")!;
                TextBox limit = (TextBox)source.FindField("limit")!;
                Require(label.Text == "old label" && limit.Text == "7", "Review lost the exact previous settings before editing.");
                Require(AutomationProperties.GetIsRequiredForForm(limit), "Required integer lost its accessible required state.");
                string fieldIdentity = AutomationProperties.GetAutomationId(limit)!;
                Require(!string.IsNullOrEmpty(fieldIdentity) && fieldIdentity != AutomationProperties.GetAutomationId(label), "Editor scopes share a field identity.");
                using (MediaNativeInput input = new(modal))
                {
                    await ClickAsync(modal, input, Changes(modal));
                    await ClickAsync(modal, input, Rights(modal));
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, label, "valid edited label");
                    RequireFreshChangeConsent(modal, store, before, "Valid plugin edit");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(Save(modal).IsEnabled, "Explicit consent after valid plugin edit did not enable Save.");
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, limit, "11");
                    RequireFreshChangeConsent(modal, store, before, "Valid source edit");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(Save(modal).IsEnabled, "Explicit consent after valid source edit did not enable Save.");
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, limit, "1.5");
                    RequireFreshChangeConsent(modal, store, before, "Invalid source edit");
                    await ClickAsync(modal, input, Changes(modal));
                    await ClickAsync(modal, input, Save(modal));
                    await Program.WaitUntilAsync(() => limit.IsFocused, () =>
                        "Invalid source integer must own native focus, not the first (valid) plugin editor; focused=" +
                        (modal.FocusManager?.GetFocusedElement() as Control)?.Name);
                    Require(ReferenceEquals(modal.FocusManager?.GetFocusedElement(), limit) && limit.IsEffectivelyVisible,
                        "Validation focused the first editor, not the invalid source field.");
                    Require(modal.IsVisible && page.IsBusy && store.Writes.Count == before && limit.Text == "1.5",
                        "Invalid required integer wrote, closed, or normalized rejected input.");
                    Require(source.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Classes.Contains("workspace-error") && !string.IsNullOrEmpty(t.Text)),
                        "Invalid source field has no visible validation message.");
                    Program.CaptureWindow(modal, "smoke-bundled-change-invalid-field-" + current + ".png");
                    // Correct the very same controls in the very same owned modal, not a settings-page detour.
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, limit, "23");
                    RequireFreshChangeConsent(modal, store, before, "Source correction after rejected Save");
                    await ClickAsync(modal, input, Changes(modal));
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, label, " ");
                    RequireFreshChangeConsent(modal, store, before, "Invalid required plugin edit");
                    await ClickAsync(modal, input, Changes(modal));
                    await ClickAsync(modal, input, Save(modal));
                    await Program.WaitUntilAsync(() => label.IsFocused,
                        () => "Invalid required plugin text must retain the native correction focus.");
                    Require(store.Writes.Count == before && modal.IsVisible, "Invalid plugin configuration escaped validation.");
                    await PluginNativeRepairHelper.ReplaceTextAsync(modal, input, label, "corrected label ü");
                    RequireFreshChangeConsent(modal, store, before, "Plugin correction after rejected Save");
                    await ClickAsync(modal, input, Changes(modal));
                    Require(Save(modal).IsEnabled, "Corrected configuration needs an effective new explicit acknowledgement.");
                    Require(ReferenceEquals(limit, source.FindField("limit")) && modal.IsVisible, "Correction replaced the modal/editor.");
                    VerifyReviewEvidence(modal, store);
                    Program.CaptureWindow(modal, "smoke-bundled-change-ready-" + current + ".png");
                    await ClickAsync(modal, input, Save(modal));
                    await Program.WaitUntilAsync(() => store.Writes.Count == before + 1 &&
                        ById<Button>(modal, "BundledApprovalReload").IsEnabled &&
                        ById<TextBlock>(modal, "BundledApprovalStatus").Text == Texts.T("plugins.changeReview.saved"),
                        () => $"Change review writes={store.Writes.Count - before}; revision={store.Revision}; " +
                            $"status={ById<TextBlock>(modal, "BundledApprovalStatus").Text}");
                    TextBlock success = ById<TextBlock>(modal, "BundledApprovalStatus");
                    Require(success.IsEffectivelyVisible && success.Classes.Contains("workspace-meta") &&
                        AutomationProperties.GetLiveSetting(success) == AutomationLiveSetting.Polite && Close(modal).Classes.Contains("primary"),
                        "Pending-only success must be a visible neutral receipt with Close as the next action.");
                    var write = store.Writes.Single(w => w.Revision == store.Revision - 1);
                    Require(write.Row == store.ValidRow && write.Revision == store.StartRevision && write.Changes && write.Rights,
                        "Save did not dispatch the exact frozen row/revision with separate consents.");
                    Require(write.Configuration.PluginSettings.Values.Single().Text == "corrected label ü" &&
                        write.Configuration.SourceSettings.Count == 1 &&
                        write.Configuration.SourceSettings["review-source"].Values.Single().Integer == 23,
                        "Save dispatched stale, defaulted or cross-scope settings.");
                    var state = await store.ReadAsync();
                    var saved = state.Plugins.Single(p => p.PluginId == "fixture");
                    Require(saved.Active is null && saved.Pending?.CandidateId == store.ValidRow.NewCandidateId &&
                        saved.Pending.ExecutionRevision == store.ValidRow.NewExecutionRevision &&
                        SdkJson.Serialize(saved.Pending.Configuration).SequenceEqual(SdkJson.Serialize(write.Configuration)),
                        "Saved outcome is not pending-only with the reviewed settings.");
                    Require(modal.IsVisible && page.IsBusy && !modal.GetVisualDescendants().OfType<CheckBox>().Any(c => c.IsChecked == true),
                        "Save closed the receipt modal or retained consumed consent.");
                    // Since the per-plugin receipt cards (16.11.) the exact receipt line sits in the card's collapsed technical
                    // details and the visible status says "Confirmed … next start".
                    Expander receiptDetails = modal.GetVisualDescendants().OfType<Expander>()
                        .Single(e => AutomationProperties.GetAutomationId(e) == "BundledApprovalReceiptTechnical:fixture");
                    Require(receiptDetails.Content is StackPanel receiptLines && receiptLines.Children.OfType<TextBlock>().Any(t => t.Text == Texts.T("plugins.approval.receipt", "fixture",
                        store.ValidRow.NewExecutionRevision, Texts.T("plugins.approval.state.Saved")))
                        && ById<TextBlock>(modal, "BundledApprovalReceiptStatus:fixture").Text == Texts.T("plugins.repair.status.confirmed"), "Saved receipt is absent.");
                    Program.CaptureWindow(modal, "smoke-bundled-change-saved-" + current + ".png");
                    await ClickAsync(modal, input, Close(modal));
                }
                await ClosedAsync(page, modal);
                Require(store.Writes.Count == before + 1, "One save dispatched more than once.");
                byte[] afterReview = SdkJson.Serialize(await fixture.Store.ReadAsync());
                Require(untouched.SequenceEqual(afterReview) && fixture.ForbiddenCalls == 0,
                    "Review mutated the underlying catalog or invoked plugin execution/network services.");
                Console.WriteLine("PLUGIN_CHANGE_REVIEW " + JsonSerializer.Serialize(new { result = "pass", language = current,
                    nativeOwnedModal = true, nativeConfigurationPaste = true, invalidSourceFocus = true, sameModalCorrection = true,
                    editConsentInvalidation = true, removedValuesConsentInvalidation = true, refreshConsentReset = true,
                    exactChangeDispatch = true, pendingOnly = true }));
            }
        }
        finally { sources.EndLifetime(); host.Content = previous; Texts.SetLanguage(language); }
    }

    private static void RequireFreshChangeConsent(Window modal, RecordingStore store, int before, string action)
    {
        Require(Changes(modal).IsChecked == false && !Save(modal).IsEnabled,
            action + " retained the old configuration consent.");
        Require(Rights(modal).IsChecked == true, action + " erased independent permission consent.");
        Save(modal).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(store.Writes.Count == before, action + " allowed an unconfirmed routed Save.");
    }

    private static void VerifyReviewEvidence(Window modal, RecordingStore store)
    {
        string visible = string.Join("\n", modal.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Require(visible.Contains(PluginContentPresentation.DescribeCapabilities(store.Review.PreviousDescription.Sources[0].Capabilities)) &&
            visible.Contains(PluginContentPresentation.DescribeCapabilities(store.Review.NewDescription.Sources[0].Capabilities)),
            "Old/new source capabilities are not displayed in the review.");
        Require(visible.Contains("old.review.test") && visible.Contains("new.review.test"), "Changed old/new HTTP hosts are missing from human-readable review.");
        Require(visible.Contains(PluginChangeReviewCatalog.T("plugins.changeReview.oldConfig", 2)) &&
            visible.Contains(PluginChangeReviewCatalog.T("plugins.changeReview.newConfig", 2)), "Old settings/new schema summary is missing.");
        Require(visible.Contains("old label") || modal.GetVisualDescendants().OfType<TextBox>().Any(t =>
            t.IsReadOnly && t.IsEffectivelyVisible && (t.Text ?? "").Contains("old label")),
            "Editing new settings erased the reviewable old settings evidence.");
    }
    private static T ById<T>(Window modal, string id) where T : Control => modal.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static Button Save(Window modal) => ById<Button>(modal, "BundledChangeReviewSave:fixture");
    private static CheckBox Changes(Window modal) => ById<CheckBox>(modal, "BundledChangeReviewChanges:fixture");
    private static CheckBox Rights(Window modal) => ById<CheckBox>(modal, "BundledChangeReviewRights:fixture");
    private static Button Close(Window modal) => ById<Button>(modal, "BundledApprovalClose");
    private static async Task<Window> OpenAsync(MainWindow owner, PluginsPage page)
    {
        owner.Activate();
        await Program.WaitUntilAsync(() => owner.IsActive && !page.IsBusy);
        using (MediaNativeInput input = new(owner)) await ClickAsync(owner, input, page.FindControl<Button>("PluginsReviewRepairs")!);
        await Program.WaitUntilAsync(() => owner.OwnedWindows.Any(w => w.IsVisible && w.Title == Texts.T("plugins.repair.review")));
        Window modal = owner.OwnedWindows.Single(w => w.IsVisible && w.Title == Texts.T("plugins.repair.review"));
        modal.Activate();
        await Program.WaitUntilAsync(() => modal.IsActive && modal.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "BundledApprovalClose"));
        Require(ReferenceEquals(modal.Owner, owner) && page.IsBusy, "Review is not the attached page's owned modal.");
        return modal;
    }
    private static Task ClickAsync(Window modal, MediaNativeInput input, Control control) =>
        PluginNativeRepairHelper.ClickAsync(modal, input, control);
    private static async Task ClosedAsync(PluginsPage page, Window modal) => await Program.WaitUntilAsync(() => !modal.IsVisible && !page.IsBusy);
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordingStore : IPluginProfileStore
    {
        private readonly PluginUxFixture _fixture;
        internal long StartRevision { get; private set; }
        internal long Revision { get; private set; } = 940;
        internal bool Malformed { get; set; }
        internal bool RemovedValues { get; set; }
        private BundledCandidateRepair RemovedValuesRow { get; }
        internal Func<bool>? Admit { get; set; }
        private bool _saved;
        private PluginReleaseSnapshot? _pending;
        internal BundledPluginChangeReview Review { get; }
        internal BundledCandidateRepair ValidRow { get; }
        internal List<(BundledCandidateRepair Row, PluginConfiguration Configuration, bool Changes, bool Rights, long Revision)> Writes { get; } = [];
        internal RecordingStore(PluginUxFixture fixture)
        {
            _fixture = fixture;
            SettingsSchema plugin = new("1", [new("label", SettingScope.Plugin, SettingKind.Text, "Review label", IsRequired: true)]);
            SettingsSchema source = new("1", [new("limit", SettingScope.Source, SettingKind.Integer, "Review limit", IsRequired: true, Minimum: 1, Maximum: 50)]);
            SourceDescription previousSource = new("review-source", "Review source", new Uri("https://old.review.test/"), SourceCapabilities.ThreadRead,
                [new(SourceResourceKind.Thread, "old.review.test", "/{board}/thread/{thread}", IsCanonical: true)],
                [new("old.review.test", HttpHostPurpose.Metadata)], source);
            PluginDescription old = new("fixture", "Bundled review fixture", "1.0", SdkContract.Version, "Inert review fixture",
                [previousSource], [], plugin, []);
            var next = old with { PluginVersion = "2.0", Sources = [previousSource with {
                BaseUri = new Uri("https://new.review.test/"),
                Routes = [new(SourceResourceKind.Thread, "new.review.test", "/{board}/thread/{thread}", IsCanonical: true)],
                Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.OriginalMedia,
                HttpHosts = [new("new.review.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)] }] };
            PluginConfiguration configuration = new("old-settings", new("1", [new("label", SettingKind.Text, Text: "old label")]),
                new Dictionary<string, SettingsSnapshot> { ["review-source"] = new("1", [new("limit", SettingKind.Integer, Integer: 7)]) });
            Require(SdkValidation.ValidateDescription(next).Count == 0, "Synthetic new description is invalid.");
            Review = new(old, next, configuration, new([], ["review-source"]));
            string json = Encoding.UTF8.GetString(SdkJson.Serialize(Review));
            ValidRow = new("fixture", "old-fixture", "new-fixture", "fixture-slot", "/fixture/old", "/fixture/new", "old-execution", "new-execution", true, "description-changed")
            { ChangeReviewJson = json, ChangeFingerprint = Hash(json) };
            var removedReview = Review with
            {
                PreviousDescription = old with
                {
                    Settings = plugin with { Fields = plugin.Fields.Append(new SettingDefinition("removed-plugin", SettingScope.Plugin, SettingKind.Text, "Removed plugin value")).ToArray() },
                    Sources = [previousSource with { Settings = source with { Fields = source.Fields.Append(new SettingDefinition("removed-source", SettingScope.Source, SettingKind.Text, "Removed source value")).ToArray() } }]
                },
                PreviousConfiguration = configuration with
                {
                    PluginSettings = new("1", configuration.PluginSettings.Values.Append(new SettingValue("removed-plugin", SettingKind.Text, Text: "historical plugin value")).ToArray()),
                    SourceSettings = new Dictionary<string, SettingsSnapshot> { ["review-source"] = new("1",
                        configuration.SourceSettings["review-source"].Values.Append(new SettingValue("removed-source", SettingKind.Text, Text: "historical source value")).ToArray()) }
                }
            };
            string removedJson = Encoding.UTF8.GetString(SdkJson.Serialize(removedReview));
            RemovedValuesRow = ValidRow with { ChangeReviewJson = removedJson, ChangeFingerprint = Hash(removedJson) };
        }
        internal void Reset() { _saved = false; _pending = null; Malformed = RemovedValues = false; StartRevision = Revision; }
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public event Action<PluginLifecycleChange>? LifecycleChanged { add { } remove { } }
        public event Action<PluginGrantChange>? GrantChanged { add { } remove { } }
        public async Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            var state = await _fixture.Store.ReadAsync(cancellationToken);
            return state with { Revision = Revision, Plugins = state.Plugins.Append(new PluginActivationSnapshot("fixture", false, 1,
                "old-fixture", null, _pending, null, null, [], null)).ToArray() };
        }
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default)
        {
            var row = Malformed ? ValidRow with { ChangeReviewJson = "{", ChangeFingerprint = Hash("{") } :
                RemovedValues ? RemovedValuesRow : ValidRow;
            return Task.FromResult(new BundledCandidateRepairPreview(Revision, _saved ? [] : [row]));
        }
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BundledCandidateReselectionPreview(Revision, []));
        public Task<PluginProfileSnapshot> ConfirmBundledChangeAsync(BundledCandidateRepair repair, PluginConfiguration configuration,
            bool acknowledgeChanges, bool authorizePermissions, long expectedRevision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(Admit?.Invoke() == true, "Change save escaped the active owned modal/lifetime gate.");
            Require(!_saved && repair == ValidRow && acknowledgeChanges && authorizePermissions, "Wrong row, duplicate save or missing independent consent.");
            if (expectedRevision != Revision) throw new PluginProfileConflictException(expectedRevision, Revision);
            Require(SdkValidation.ValidateConfiguration(Review.NewDescription, configuration).Count == 0, "Invalid configuration reached the store.");
            Writes.Add((repair, configuration, acknowledgeChanges, authorizePermissions, expectedRevision));
            var artifact = _fixture.Source.Artifact! with { ExecutionRevision = ValidRow.NewExecutionRevision! };
            _pending = new(ValidRow.NewCandidateId!, artifact, Review.NewDescription, configuration);
            _saved = true; Revision++;
            return ReadAsync(cancellationToken);
        }
        // Default repair/reselection/reauthorization methods fail closed; all other mutations are forbidden too.
        private static Task<PluginProfileSnapshot> Forbidden() => throw new InvalidOperationException("Unexpected review mutation API");
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
