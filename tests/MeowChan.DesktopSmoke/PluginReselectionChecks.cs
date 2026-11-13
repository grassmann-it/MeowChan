using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
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
        store.ModalReady = () => page.IsBusy && !CommandRouter.IsRoutingContextActive(page, page) &&
            owner.OwnedWindows.Any(modal => modal.IsVisible && ReferenceEquals(modal.Owner, owner) &&
                CommandRouter.IsRoutingContextActive(modal, modal));
        ContentControl host = owner.FindControl<ContentControl>("PluginsPageHost")!;
        object? previous = host.Content;
        string language = Texts.Language;
        host.Content = page;
        try
        {
            owner.GetLogicalDescendants().OfType<MenuItem>().First(item => Equals(item.Tag, "nav.plugins"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await page.ActivateAsync();
            // Long localized footer labels at the modal's 320 px floor. Runs first: the catalog-reload boundary
            // below reinitializes Texts without the bundled packs, after which RU would fall back to English.
            foreach (string narrowLanguage in new[] { "de", "ru" })
            {
                Texts.SetLanguage(narrowLanguage); await Settle();
                Require(Texts.Language == narrowLanguage && Texts.T("plugins.approval.reload") != "Reload review",
                    "Bundled language pack is not active for the footer check: " + narrowLanguage);
                store.ResetOffers();
                Window modal = await OpenAsync(owner, page);
                bool oneRow = VerifyFooter(modal, closePrimary: false, requireOneRow: false);
                Program.CaptureWindow(modal, "smoke-approval-footer-default-" + narrowLanguage + ".png");
                modal.Width = 320; modal.Height = 320;
                await Program.WaitUntilAsync(() => { modal.UpdateLayout(); return Math.Abs(modal.ClientSize.Width - 320) <= 1; });
                VerifyFooter(modal, closePrimary: false, requireOneRow: false);
                Program.CaptureWindow(modal, "smoke-approval-footer-minimum-" + narrowLanguage + ".png");
                using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Cancel(modal));
                await ClosedAsync(page, modal);
                Console.WriteLine("PLUGIN_APPROVAL_FOOTER " + JsonSerializer.Serialize(new { result = "pass", language = narrowLanguage, oneRowAtDefaultWidth = oneRow, defaultWidth = modal.Width, minimumWidth = 320 }));
            }
            foreach (string currentLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(currentLanguage); await Settle();
                store.ResetOffers();
                int before = store.Calls;
                Window modal = await OpenAsync(owner, page);
                await VerifyContentsAsync(modal, store);
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
                await SavedAndCloseAsync(page, modal);
                Require(store.Calls == before + 1 && ReferenceEquals(store.Chosen, store.Row) && store.ChosenRevision == store.Revision - 1,
                    "New confirmation did not deliver the exact frozen row and revision exactly once.");
                Require(store.Chosen!.ConfigurationJson == RecordingStore.Configuration, "Exact configuration changed in transit.");
                Require(store.ModalReadyAtConfirmation, "New choice committed outside the active modal/lifetime routing context.");

                // The old proven repair remains independently confirmable through its original API.
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                    await ClickAsync(modal, input, Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.repair.confirmNamed", store.Repair.PluginId)));
                await SavedAndCloseAsync(page, modal);
                Require(store.Calls == before + 2 && ReferenceEquals(store.ChosenRepair, store.Repair)
                    && store.RepairRevision == store.Revision - 1, "Proven repair path lost its row/revision or called new-selection API.");
                Require(store.ModalReadyAtConfirmation, "Proven repair committed outside its active modal/lifetime.");
                store.OfferNewChoice = false;
                modal = await OpenAsync(owner, page);
                VerifyOrder(modal, newFirst: false);
                using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Cancel(modal));
                await ClosedAsync(page, modal);
                Require(store.Calls == before + 2, "Blocked-only review mutated the profile.");
                store.OfferNewChoice = true;
                // Changed executable with existing rights is actionable only through
                // its separate explicit per-build review, never ordinary repair.
                int beforeReauthorization = store.Calls;
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Button consent = ReauthorizeConfirm(modal, store.Reauthorization.PluginId);
                    CheckBox ack = Ack(consent);
                    Require(!consent.IsEnabled && ack.IsChecked == false, "Permission review was preconsented.");
                    consent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(store.Calls == beforeReauthorization && modal.IsVisible, "Unchecked reauthorization was accepted.");
                    string[] labels = modal.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
                    Require(labels.Contains(Texts.T("plugins.repair.reauthorize.source", "source-with-md5-exception")), "Source exception not disclosed.");
                    Require(labels.Any(text => text.Contains("board-exact") && text.Contains("rule-exact") && text.Contains("dimensions")), "Filter grant scope not disclosed.");
                    await ClickAsync(modal, input, ack);
                    Require(consent.IsEnabled, "Acknowledgement did not enable explicit permission confirmation.");
                    await ClickAsync(modal, input, ack);
                    Require(!consent.IsEnabled, "Unchecking did not disable permission confirmation.");
                    await ClickAsync(modal, input, ack);
                    await input.KeyAsync(MediaNativeInput.Escape);
                }
                await ClosedAsync(page, modal);
                Require(store.Calls == beforeReauthorization, "Escape after permission review wrote profile state.");
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Button consent = ReauthorizeConfirm(modal, store.Reauthorization.PluginId);
                    Require(!consent.IsEnabled && Ack(consent).IsChecked == false, "Consent leaked across modal openings.");
                    await ClickAsync(modal, input, Ack(consent));
                    Program.CaptureWindow(modal, "smoke-reauthorization-modal-" + currentLanguage + ".png");
                    await ClickAsync(modal, input, consent);
                }
                await SavedAndCloseAsync(page, modal);
                Require(store.Calls == beforeReauthorization + 1 && ReferenceEquals(store.ChosenReauthorization, store.Reauthorization) &&
                    store.ReauthorizationRevision == store.Revision - 1 && store.ModalReadyAtConfirmation,
                    "Permission review did not submit the exact frozen row/revision to its own API exactly once.");
                // The single-row route stays in this same modal but does NOT retain B's old check.
                store.ResetOffers();
                int beforeSingles = store.Calls;
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Button first = ReauthorizeConfirm(modal, store.Reauthorization.PluginId);
                    await ClickAsync(modal, input, Ack(first));
                    await ClickAsync(modal, input, Ack(ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId)));
                    await ClickAsync(modal, input, first);
                    await SavedAsync(page, modal);
                    Require(store.Calls == beforeSingles + 1 && !ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId).IsEnabled,
                        "Single save silently submitted or retained the other row's old consent.");
                    Button second = ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId);
                    await ClickAsync(modal, input, Ack(second));
                    await ClickAsync(modal, input, second);
                }
                await SavedAndCloseAsync(page, modal);
                Require(store.Calls == beforeSingles + 2, "Two single saves did not complete in the same open modal.");
                store.ResetOffers();
                int beforeBatch = store.Calls;
                long batchRevision = store.Revision;
                modal = await OpenAsync(owner, page);
                using (MediaNativeInput input = new(modal))
                {
                    Button first = ReauthorizeConfirm(modal, store.Reauthorization.PluginId);
                    Button second = ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId);
                    Button batch = ById(modal, "BundledApprovalSubmit");
                    Require(!batch.IsEnabled && Ack(first).IsChecked == false && Ack(second).IsChecked == false,
                        "Batch submit introduced blanket or remembered consent.");
                    await ClickAsync(modal, input, Ack(first));
                    await ClickAsync(modal, input, Ack(second));
                    store.HoldWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        await ClickAsync(modal, input, batch);
                        Require(store.Calls == beforeBatch + 1 && !batch.IsEnabled && !Ack(first).IsEffectivelyEnabled,
                            "The in-flight write did not lock consent and submit controls.");
                        batch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(store.Calls == beforeBatch + 1, "Double submit admitted a second in-flight write.");
                    }
                    finally { store.HoldWrite.TrySetResult(); store.HoldWrite = null; }
                }
                await SavedAndCloseAsync(page, modal);
                Require(store.Calls == beforeBatch + 2 && store.Revision == batchRevision + 2 &&
                    store.ReauthorizationRevisions.TakeLast(2).SequenceEqual(new[] { batchRevision, batchRevision + 1 }) &&
                    store.ModalReadyAtConfirmation, "Batch did not follow its own monotonic return-revision chain in the same modal.");
                // Footer row, singular label, neutral status and finish emphasis: with only the two
                // reauthorizations offered, one batch save leaves nothing actionable.
                store.ResetOffers(); store.MarkSaved(store.Row.PluginId, store.Repair.PluginId);
                int beforeFinish = store.Calls;
                modal = await OpenAsync(owner, page);
                VerifyFooter(modal, closePrimary: false);
                Program.CaptureWindow(modal, "smoke-approval-footer-" + currentLanguage + ".png");
                using (MediaNativeInput input = new(modal))
                {
                    await ClickAsync(modal, input, Ack(ReauthorizeConfirm(modal, store.Reauthorization.PluginId)));
                    Require(AutomationProperties.GetName(ById(modal, "BundledApprovalSubmit")) == Texts.T("plugins.approval.submitOne"),
                        "One checked row did not use the singular submit label.");
                    await ClickAsync(modal, input, Ack(ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId)));
                    Require(AutomationProperties.GetName(ById(modal, "BundledApprovalSubmit")) == Texts.T("plugins.approval.submit", 2),
                        "Two checked rows did not use the plural submit label.");
                    await ClickAsync(modal, input, ById(modal, "BundledApprovalSubmit"));
                }
                await SavedAsync(page, modal);
                VerifyNeutralStatus(modal, Texts.T("plugins.approval.reviewAgain"));
                Require(ReferenceEquals(modal.FocusManager?.GetFocusedElement(), ById(modal, "BundledApprovalClose")),
                    "Keyboard focus did not move to Close once nothing actionable remained.");
                VerifyFooter(modal, closePrimary: true);
                Program.CaptureWindow(modal, "smoke-approval-done-" + currentLanguage + ".png");
                using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Cancel(modal));
                await ClosedAsync(page, modal);
                Require(store.Calls == beforeFinish + 2, "Finish scenario did not save exactly the two checked rows.");
                // Closing, same-code catalog reload and navigation must stop the frozen remainder,
                // while still observing the already-admitted task (not claiming rollback).
                foreach (string boundary in new[] { "close", "catalog", "deactivate" })
                {
                    store.ResetOffers();
                    int beforeStop = store.Calls;
                    modal = await OpenAsync(owner, page);
                    using (MediaNativeInput input = new(modal))
                    {
                        await ClickAsync(modal, input, Ack(ReauthorizeConfirm(modal, store.Reauthorization.PluginId)));
                        await ClickAsync(modal, input, Ack(ReauthorizeConfirm(modal, store.SecondReauthorization.PluginId)));
                        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        store.HoldWrite = hold;
                        try
                        {
                            await ClickAsync(modal, input, ById(modal, "BundledApprovalSubmit"));
                            Require(store.Calls == beforeStop + 1, "Stop fixture did not hold exactly one admitted write.");
                            if (boundary == "close")
                            {
                                await ClickAsync(modal, input, Cancel(modal));
                                VerifyNeutralStatus(modal, Texts.T("plugins.approval.stopping"));
                            }
                            else if (boundary == "catalog")
                            {
                                Texts.InitializeWithoutBundlesForTests();
                                Require(Texts.Language == currentLanguage, "Catalog reload changed the language code in the epoch test.");
                                await Settle();
                            }
                            else { page.Deactivate(); await Settle(); }
                            Require(modal.IsVisible, "Modal completed before its in-flight write was observed.");
                        }
                        finally { hold.TrySetResult(); store.HoldWrite = null; }
                    }
                    await ClosedAsync(page, modal);
                    Require(store.Calls == beforeStop + 1, "Invalidated modal admitted a remaining frozen action: " + boundary);
                    if (boundary == "deactivate") await page.ActivateAsync();
                }
                Console.WriteLine("PLUGIN_REAUTHORIZATION " + JsonSerializer.Serialize(new { result = "pass", language = currentLanguage,
                    explicitSourceAndScopedFilterRights = true, uncheckedUncheckEscape = true, exactRowRevision = true, activeModalAdmission = true, batchOwnRevisionChain = true, singleConsentReset = true, inFlightCloseCatalogDeactivate = true }));
                Console.WriteLine("PLUGIN_RESELECTION " + JsonSerializer.Serialize(new { result = "pass", language = currentLanguage,
                    backend = "private Xvfb/XTest; passive profile-store decorator", exactConfiguration = true,
                    consentCancelEscapeBlockedLanguageDeactivate = true, provenRepair = true, legacyBlockedRows = 15, newChoicesFirst = true, activeModalAdmission = true, forbiddenCalls = fixture.ForbiddenCalls }));
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
    private static async Task VerifyContentsAsync(Window modal, RecordingStore store)
    {
        VerifyOrder(modal, newFirst: true); // Check initial ordering before scrolling to optional details.
        Expander technical = modal.GetVisualDescendants().OfType<Expander>().Single(expander =>
            AutomationProperties.GetAutomationId(expander) == "BundledReselectionTechnical:" + store.Row.PluginId);
        Expander configurationDetails = modal.GetVisualDescendants().OfType<Expander>().Single(expander =>
            AutomationProperties.GetAutomationId(expander) == "BundledReselectionConfiguration:" + store.Row.PluginId);
        using MediaNativeInput input = new(modal);
        foreach (Expander details in new[] { technical, configurationDetails })
        {
            Require(!details.IsExpanded, "Technical details should not overwhelm the initial repair review.");
            ToggleButton header = details.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => ReferenceEquals(button.TemplatedParent, details));
            await ClickAsync(modal, input, header);
            await Program.WaitUntilAsync(() => details.IsExpanded && (details.Content as Control)?.IsEffectivelyVisible == true);
        }
        string[] labels = modal.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(labels.Contains(Texts.T("plugins.reselection.repairSection")) && labels.Contains(Texts.T("plugins.reselection.section")),
            "Proven repair and new selection sections are not separately displayed.");
        string exactDetails = (technical.Content as TextBlock)?.Text ?? "";
        Require(exactDetails.Contains(Texts.T("plugins.repair.identities", store.Row.OldCandidateId, store.Row.NewCandidateId!, store.Row.SlotId!), StringComparison.Ordinal), "Exact IDs/slot missing.");
        Require(exactDetails.Contains(Texts.T("plugins.repair.paths", store.Row.OldPath!, store.Row.NewPath!), StringComparison.Ordinal), "Exact paths missing.");
        Require(exactDetails.Contains(Texts.T("plugins.repair.executions", store.Row.OldExecutionRevision!, store.Row.NewExecutionRevision!), StringComparison.Ordinal), "Exact execution revisions missing.");
        TextBox configuration = (TextBox)configurationDetails.Content!;
        Require(configuration.IsReadOnly && configuration.AcceptsReturn && configuration.MaxHeight == 240 &&
            configuration.Text == RecordingStore.Configuration && configuration.Text == store.Row.ConfigurationJson &&
            configuration.IsEffectivelyVisible && AutomationProperties.GetName(configuration) == Texts.T("plugins.reselection.configuration"),
            "Full exact configuration is not read-only, copyable and reviewable.");
        Require(labels.Contains(Texts.T("plugins.reselection.backup")) && labels.Contains(Texts.T("plugins.reselection.policy")), "Safety/backup explanation missing.");
        Require(labels.Contains(Texts.T("plugins.reselection.blocked.pinned")), "Blocked reason is not localized.");
        foreach (BundledCandidateRepair repair in store.RepairRows.Where(row => !row.CanReauthorize))
            Require(Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.repair.confirmNamed", repair.PluginId)).IsEnabled == repair.CanConfirm,
                "A proven repair row disappeared or changed confirmability.");
        Program.CaptureWindow(modal, "smoke-reselection-exact-details-" + Texts.Language + ".png");
        // A second native click must return to the compact decision without discarding any review data.
        foreach (Expander details in new[] { configurationDetails, technical })
        {
            ToggleButton header = details.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => ReferenceEquals(button.TemplatedParent, details));
            await ClickAsync(modal, input, header);
            await Program.WaitUntilAsync(() => !details.IsExpanded);
        }
    }
    private static void VerifyOrder(Window modal, bool newFirst)
    {
        StackPanel selections = modal.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => AutomationProperties.GetAutomationId(p) == "BundledNewSelections");
        StackPanel repairs = modal.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => AutomationProperties.GetAutomationId(p) == "BundledProvenRepairs");
        modal.UpdateLayout();
        // UX103 P7: the fixed title sits above the scrolling rows, so the scroll origin is the frame body.
        ScrollViewer body = modal.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "DialogFrameBody");
        Point selectionOrigin = selections.TranslatePoint(default, body)!.Value;
        Point repairOrigin = repairs.TranslatePoint(default, body)!.Value;
        Require(newFirst ? selectionOrigin.Y < repairOrigin.Y : repairOrigin.Y < selectionOrigin.Y,
            "The usable section was buried below the legacy repair rows.");
        Require((newFirst ? selectionOrigin.Y : repairOrigin.Y) >= 0 && (newFirst ? selectionOrigin.Y : repairOrigin.Y) < 40,
            "The first review section is not at the initial scroll origin.");
    }
    private static IEnumerable<Button> Buttons(Window modal) => modal.GetVisualDescendants().OfType<Button>();
    private static Button NewConfirm(Window modal, string id) => Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.reselection.confirmNamed", id));
    private static Button ReauthorizeConfirm(Window modal, string id) => Buttons(modal).Single(b => AutomationProperties.GetName(b) == Texts.T("plugins.repair.reauthorize.confirmNamed", id));
    private static CheckBox Ack(Button confirm) => ((StackPanel)confirm.Parent!).Children.OfType<CheckBox>().Single();
    private static Button Cancel(Window modal) => Buttons(modal).Single(b => Equals(b.Content, Texts.T("plugins.repair.cancel")));
    private static async Task ClickAsync(Window window, MediaNativeInput input, Control control)
    {
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); control.BringIntoView(); window.UpdateLayout();
            Point? point = control.TranslatePoint(default, window);
            return point is { } p && p.Y >= 0 && p.Y + control.Bounds.Height <= window.ClientSize.Height && control.Bounds.Width > 0; });
        await input.ClickAsync(control); await Settle();
    }
    private static Button ById(Window modal, string id) => Buttons(modal).Single(b => AutomationProperties.GetAutomationId(b) == id);
    private static async Task SavedAsync(PluginsPage page, Window modal)
    {
        await Program.WaitUntilAsync(() => modal.IsVisible && ById(modal, "BundledApprovalReload").IsEnabled &&
            modal.GetVisualDescendants().OfType<TextBlock>().Any(t => AutomationProperties.GetAutomationId(t) == "BundledApprovalStatus" &&
                t.Text == Texts.T("plugins.approval.reviewAgain")));
        Require(page.IsBusy && modal.IsVisible, "Saving prematurely closed the modal or ended the page operation.");
        Require(modal.GetVisualDescendants().OfType<CheckBox>().All(ack => ack.IsChecked == false),
            "A save retained unconsumed consent instead of requiring a new explicit check.");
    }
    // Neutral status sits in the footer, muted and polite; the assertive error strip stays empty and hidden.
    private static void VerifyNeutralStatus(Window modal, string text)
    {
        // Logical tree: a hidden strip's scroll content is not templated into the visual tree.
        TextBlock status = modal.GetLogicalDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "BundledApprovalStatus");
        TextBlock issue = modal.GetLogicalDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "BundledApprovalIssue");
        Control strip = modal.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == DialogFrame.ErrorStripName);
        Control footer = modal.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == DialogFrame.FooterName);
        Require(status.Text == text && status.IsEffectivelyVisible, "Neutral status text is not shown: " + text);
        Require(status.GetLogicalAncestors().Contains(footer) && !status.GetLogicalAncestors().Contains(strip),
            "Neutral status is not in the footer or sits in the error strip: " + text);
        Require(!status.Classes.Contains("workspace-error") && status.Classes.Contains("workspace-meta") &&
            AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite, "Neutral status is styled or announced as an error: " + text);
        Require(string.IsNullOrEmpty(issue.Text) && !strip.IsVisible && AutomationProperties.GetLiveSetting(issue) == AutomationLiveSetting.Assertive,
            "Error strip shows or repeats a neutral message: " + text);
    }
    // One right-aligned row at the default width, Close/Reload/Submit, with exactly one primary action.
    private static bool VerifyFooter(Window modal, bool closePrimary, bool requireOneRow = true)
    {
        modal.UpdateLayout();
        Control footer = modal.GetVisualDescendants().OfType<Control>().Single(c => c.Name == DialogFrame.FooterName);
        WrapPanel actions = modal.GetVisualDescendants().OfType<WrapPanel>().Single(p => AutomationProperties.GetAutomationId(p) == "BundledApprovalActions");
        Button close = ById(modal, "BundledApprovalClose"), reload = ById(modal, "BundledApprovalReload"), submit = ById(modal, "BundledApprovalSubmit");
        Rect Box(Control c) => new(c.TranslatePoint(default, footer)!.Value, c.Bounds.Size);
        Rect footerBox = new Rect(footer.Bounds.Size).Inflate(0.5);
        foreach (Button button in new[] { close, reload, submit })
            Require(footerBox.Contains(Box(button)) && button.Bounds.Width > 0, "Footer button is clipped or hidden: " + AutomationProperties.GetAutomationId(button));
        bool oneRow = Math.Abs(Box(close).Y - Box(reload).Y) < 1 && Math.Abs(Box(reload).Y - Box(submit).Y) < 1 &&
            Box(close).X < Box(reload).X && Box(reload).X < Box(submit).X;
        if (requireOneRow)
        {
            Require(oneRow, "Footer buttons do not share one ordered row.");
            Require(footerBox.Right - Box(submit).Right < 40, "Footer row is not right-aligned.");
        }
        Require(actions.Classes.Contains("workspace-actions"), "Footer row does not use the shared action-row style.");
        Require(submit.Classes.Contains("primary") == !closePrimary && close.Classes.Contains("primary") == closePrimary &&
            !reload.Classes.Contains("primary"), "Footer emphasis is wrong (close primary expected: " + closePrimary + ").");
        return oneRow;
    }
    private static async Task SavedAndCloseAsync(PluginsPage page, Window modal)
    {
        await SavedAsync(page, modal);
        using (MediaNativeInput input = new(modal)) await ClickAsync(modal, input, Cancel(modal));
        await ClosedAsync(page, modal);
    }
    private static async Task ClosedAsync(PluginsPage page, Window modal)
    { await Program.WaitUntilAsync(() => !modal.IsVisible && !page.IsBusy); await Settle(); }
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordingStore(IPluginProfileStore inner) : IPluginProfileStore
    {
        internal long Revision { get; private set; } = 71029;
        private readonly HashSet<string> saved = new(StringComparer.Ordinal);
        internal void ResetOffers() => saved.Clear();
        internal void MarkSaved(params string[] pluginIds) => saved.UnionWith(pluginIds);
        internal TaskCompletionSource? HoldWrite { get; set; }
        internal List<long> ReauthorizationRevisions { get; } = new();
        internal const string Configuration = "{\n  \"revision\": \"settings ü exact\",\n  \"pluginSettings\": {\"label\": \" spaces  and \\\"quotes\\\" \"},\n  \"sourceSettings\": {\"pux-source\": {\"limit\": 17}},\n  \"filterSettings\": {},\n  \"reviewTail\": \"END exact configuration 漢字\"\n}";
        internal BundledCandidateReselection Row { get; } = new("new-explicit-choice", "old-id ü", "new-id ü", "delivered-slot", "/old location/ü.cs", "/new location/漢.cs", "old-execution", "new-execution", Configuration, null);
        internal BundledCandidateReselection Blocked { get; } = new("blocked-choice", "pinned-old", "pinned-new", "slot-blocked", "/old", "/new", "old", "new", "{\"blocked\":true}", "pinned");
        internal BundledCandidateRepair Repair { get; } = new("proven-repair", "proven-old", "proven-new", "proven-slot", "/proven/old", "/proven/new", "execution-old", "execution-new", true, null);
        internal BundledCandidateRepair Reauthorization { get; } = CreateReauthorization("reauthorize-plugin");
        internal BundledCandidateRepair SecondReauthorization { get; } = CreateReauthorization("reauthorize-second");
        private static BundledCandidateRepair CreateReauthorization(string pluginId)
        {
            string json = Encoding.UTF8.GetString(SdkJson.Serialize(new BundledRepairPermissions(
                [new(pluginId, "dimensions", new("filter-source", "board-exact", "rule-exact"), FilterDataAccess.OriginalImage, 9)],
                ["source-with-md5-exception"])));
            return new(pluginId, "old-reauthorize", "new-reauthorize", "slot", "/old", "/new", "old-execution", "new-execution", true, "inherited-grants")
            {
                PermissionReviewJson = json,
                PermissionFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            };
        }
        internal BundledCandidateRepair? ChosenReauthorization { get; private set; }
        internal long ReauthorizationRevision { get; private set; }
        internal bool OfferNewChoice { get; set; } = true;
        internal Func<bool>? ModalReady { get; set; }
        internal bool ModalReadyAtConfirmation { get; private set; }
        internal IReadOnlyList<BundledCandidateRepair> RepairRows => Enumerable.Range(0, 15)
            .Select(i => Repair with { PluginId = "legacy-blocked-" + i, BlockedReason = "unproven-history" })
            .Append(Repair).Append(Reauthorization).Append(SecondReauthorization).Where(row => !saved.Contains(row.PluginId)).ToArray();
        internal int Calls { get; private set; }
        internal BundledCandidateReselection? Chosen { get; private set; }
        internal long ChosenRevision { get; private set; }
        internal BundledCandidateRepair? ChosenRepair { get; private set; }
        internal long RepairRevision { get; private set; }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public async Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            (await inner.ReadAsync(cancellationToken)) with { Revision = Revision };
        public Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BundledCandidateRepairPreview(Revision, RepairRows));
        public Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BundledCandidateReselectionPreview(Revision, new[] { OfferNewChoice ? Row : Row with { BlockedReason = "unproven-configuration" }, Blocked }.Where(row => !saved.Contains(row.PluginId)).ToArray()));
        public Task<PluginProfileSnapshot> ConfirmBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default)
        { ChosenRepair = repair; RepairRevision = expectedRevision; return CommitAsync(repair.PluginId, expectedRevision, cancellationToken); }
        public Task<PluginProfileSnapshot> ReauthorizeBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision, CancellationToken cancellationToken = default)
        { ChosenReauthorization = repair; ReauthorizationRevision = expectedRevision; ReauthorizationRevisions.Add(expectedRevision); return CommitAsync(repair.PluginId, expectedRevision, cancellationToken); }
        public Task<PluginProfileSnapshot> ConfirmBundledReselectionAsync(BundledCandidateReselection selection, long expectedRevision, CancellationToken cancellationToken = default)
        { Chosen = selection; ChosenRevision = expectedRevision; return CommitAsync(selection.PluginId, expectedRevision, cancellationToken); }
        private async Task<PluginProfileSnapshot> CommitAsync(string pluginId, long expectedRevision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (expectedRevision != Revision) throw new PluginProfileConflictException(expectedRevision, Revision);
            ModalReadyAtConfirmation = ModalReady?.Invoke() == true;
            Require(ModalReadyAtConfirmation, "A write was admitted without the active owned modal and page lifetime.");
            Calls++;
            if (HoldWrite is { } hold) await hold.Task;
            saved.Add(pluginId); Revision++;
            return await ReadAsync(token);
        }
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
