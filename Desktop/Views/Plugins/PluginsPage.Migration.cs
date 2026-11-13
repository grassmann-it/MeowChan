using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class PluginsPage
{
    private Window? _migrationWindow;
    private PluginMultiApproval? _migrationApproval;
    private long _migrationCatalogEpoch;

    private void CloseBundledRepairPreview()
    {
        _migrationApproval?.Stop();
        _migrationWindow?.Close();
    }

    private async void OnReviewBundledRepairsClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || _migrationWindow is not null || _services is null || !IsEffectivelyVisible ||
            !CommandRouter.IsRoutingContextActive(this, this) || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_drafts.Values.Any(draft => draft.HasDiscardableChanges))
        { ErrorLocalized("plugins.repair.finishDraft"); return; }
        var services = _services;
        long catalogEpoch = Interlocked.Read(ref _migrationCatalogEpoch);
        string language = Texts.Language;
        var (generation, token) = BeginOperation();
        try
        {
            BundledCandidateRepairPreview preview = await services.ProfileStore.PreviewBundledRepairsAsync(token);
            if (!Current(generation) || !IsEffectivelyVisible || Texts.Language != language || Interlocked.Read(ref _migrationCatalogEpoch) != catalogEpoch) return;
            BundledCandidateReselectionPreview selections = await services.ProfileStore.PreviewBundledReselectionsAsync(token);
            if (!Current(generation) || !IsEffectivelyVisible || Texts.Language != language || Interlocked.Read(ref _migrationCatalogEpoch) != catalogEpoch) return;
            Window window = new()
            {
                Title = Texts.T("plugins.reselection.reviewTitle"),
                Width = Math.Max(320, Math.Min(880, owner.Bounds.Width - 32)),
                Height = Math.Max(320, Math.Min(700, owner.Bounds.Height - 32)),
                CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _migrationWindow = window;
            PluginMultiApproval approval = new(services.ProfileStore);
            _migrationApproval = approval;
            bool saving = false, closeRequested = false;
            bool coherent = preview.ProfileRevision == selections.ProfileRevision;
            bool SessionCurrent() => Current(generation) && ReferenceEquals(_services, services) &&
                ReferenceEquals(_migrationWindow, window) && IsEffectivelyVisible &&
                ReferenceEquals(TopLevel.GetTopLevel(this), owner) && owner.IsVisible &&
                Texts.Language == language && Interlocked.Read(ref _migrationCatalogEpoch) == catalogEpoch;
            // The modal owns input during a save; the disabled/inactive owner must NOT route it.
            bool Admit() => SessionCurrent() && !closeRequested && window.IsVisible &&
                ReferenceEquals(window.Owner, owner) && CommandRouter.IsRoutingContextActive(window, window);
            StackPanel rows = new() { Spacing = 16 };
            StackPanel repairRows = new() { Spacing = 16 };
            StackPanel selectionRows = new() { Spacing = 16 };
            StackPanel receipts = new() { Spacing = 6 };
            List<(CheckBox Ack, PluginApprovalChoice Choice)> reauthorizations = new();
            // Neutral progress/outcome text sits politely above the footer actions; only real problems use the
            // assertive error strip. Exactly one of the two carries a message at a time, so nothing is announced twice.
            TextBlock status = RepairText("");
            status.Focusable = true;
            status.Classes.Add("workspace-meta");
            // Long outcomes scroll inside the footer instead of pushing the actions out of a small window.
            ScrollViewer statusHost = new()
            {
                Content = status, MaxHeight = 96, IsVisible = false,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            AutomationProperties.SetAutomationId(status, "BundledApprovalStatus");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            TextBlock issue = RepairText("");
            issue.Focusable = true;
            AutomationProperties.SetAutomationId(issue, "BundledApprovalIssue");
            Button submit = new();
            AutomationProperties.SetAutomationId(submit, "BundledApprovalSubmit");
            Button reload = new() { Content = RepairText(Texts.T("plugins.approval.reload")) };
            AutomationProperties.SetAutomationId(reload, "BundledApprovalReload");
            Button cancel = new() { Content = Texts.T("plugins.repair.cancel") };
            AutomationProperties.SetName(cancel, Texts.T("plugins.repair.cancel"));
            AutomationProperties.SetAutomationId(cancel, "BundledApprovalClose");
            bool actionable = false, closeIsPrimary = false;
            void Emphasise(bool close)
            {
                // Submit is the primary action until a successful save leaves nothing to act on; then Close is.
                closeIsPrimary = close;
                submit.Classes.Set("primary", !close);
                cancel.Classes.Set("primary", close); cancel.Classes.Set("subtle", !close);
            }
            void UpdateSubmit()
            {
                int count = reauthorizations.Count(item => item.Ack.IsChecked == true);
                string label = count == 1 ? Texts.T("plugins.approval.submitOne") : Texts.T("plugins.approval.submit", count);
                submit.Content = RepairText(label);
                AutomationProperties.SetName(submit, label);
                submit.IsVisible = reauthorizations.Count > 0;
                submit.IsEnabled = !saving && coherent && count > 0;
            }
            void SetStatus(string key)
            {
                bool problem = PluginMultiApproval.IsProblem(key);
                TextBlock target = problem ? issue : status, other = problem ? status : issue;
                other.Text = ""; AutomationProperties.SetName(other, "");
                target.Text = Texts.T(key); AutomationProperties.SetName(target, target.Text);
                statusHost.IsVisible = !problem; // The error strip hides itself while its text is empty.
            }
            TextBlock Message() => string.IsNullOrEmpty(issue.Text) ? status : issue;
            async Task ReloadAsync()
            {
                // One bounded pair of reads. A mixed snapshot never becomes actionable.
                coherent = false;
                preview = await services.ProfileStore.PreviewBundledRepairsAsync(token);
                if (!SessionCurrent() || closeRequested) return;
                selections = await services.ProfileStore.PreviewBundledReselectionsAsync(token);
                if (!SessionCurrent() || closeRequested) return;
                coherent = preview.ProfileRevision == selections.ProfileRevision;
            }
            void RenderReceipts()
            {
                receipts.Children.Clear();
                foreach (var receipt in approval.Receipts)
                    receipts.Children.Add(RepairText(Texts.T("plugins.approval.receipt", receipt.Choice.PluginId, receipt.Choice.Repair?.NewExecutionRevision ?? receipt.Choice.Selection?.NewExecutionRevision,
                        Texts.T("plugins.approval.state." + receipt.State))));
            }
            async Task SaveAsync(IReadOnlyList<PluginApprovalChoice> choices)
            {
                if (saving || !coherent || !Admit()) return;
                saving = true;
                repairRows.IsEnabled = selectionRows.IsEnabled = reload.IsEnabled = false;
                UpdateSubmit();
                SetStatus("plugins.approval.saving");
                try
                {
                    await approval.SubmitAsync(choices, Admit, token);
                    // Even close/deactivation waits for this task. Cancellation never means rollback.
                    if (!SessionCurrent() || closeRequested) return;
                    RenderReceipts();
                    try
                    {
                        await ReloadAsync();
                        if (!SessionCurrent() || closeRequested) return;
                        RenderRows(); // All unconsumed checkboxes are freshly UNCHECKED, also on failure.
                        SetStatus(approval.IssueKey ?? (!coherent ? "plugins.approval.needsReview" :
                            choices.Count == 1 && choices[0].Kind == PluginApprovalKind.ChangeReview
                                ? "plugins.changeReview.saved" : "plugins.approval.reviewAgain"));
                        // The dialog stays open so receipts remain readable; with nothing left, Close is the next step.
                        Emphasise(approval.IssueKey is null && coherent && !actionable);
                    }
                    catch (Exception)
                    {
                        coherent = false;
                        if (SessionCurrent() && !closeRequested)
                        {
                            RenderRows();
                            SetStatus("plugins.approval.refreshFailed"); // Existing Saved receipts are not undone.
                        }
                    }
                    receipts.BringIntoView();
                    if (closeIsPrimary) cancel.Focus(); else Message().Focus();
                }
                finally
                {
                    saving = false;
                    if (closeRequested || !SessionCurrent()) window.Close();
                    else { repairRows.IsEnabled = selectionRows.IsEnabled = reload.IsEnabled = true; UpdateSubmit(); }
                }
            }
            void RenderRows()
            {
                repairRows.Children.Clear(); selectionRows.Children.Clear(); rows.Children.Clear();
                reauthorizations.Clear();
                AutomationProperties.SetAutomationId(repairRows, "BundledProvenRepairs");
                AutomationProperties.SetAutomationId(selectionRows, "BundledNewSelections");
                bool onlyChangeReviews = preview.Rows.Count > 0 && preview.Rows.All(row => row.CanReviewChanges) && selections.Rows.Count == 0;
                if (!onlyChangeReviews)
                {
                    repairRows.Children.Add(RepairText(Texts.T("plugins.reselection.repairSection")));
                    repairRows.Children.Add(RepairText(Texts.T("plugins.repair.policy")));
                }
                if (preview.Rows.Count == 0) repairRows.Children.Add(RepairText(Texts.T("plugins.repair.empty")));
                int repairRowIndex = 0;
                foreach (BundledCandidateRepair row in preview.Rows.OrderByDescending(row => row.CanReviewChanges || row.CanReauthorize || row.CanConfirm))
                {
                    string automationPrefix = "BundledRepairReauthorization" + repairRowIndex++;
                    BundledRepairPermissions? permissions = ReadBundledRepairPermissions(row);
                    StackPanel content = new() { Spacing = 6 };
                    content.Children.Add(new TextBlock { Text = row.PluginId, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    Expander technical = new() { Header = PluginChangeReviewCatalog.T("plugins.changeReview.technical"), IsExpanded = false,
                        Content = RepairText(Texts.T("plugins.repair.identities", row.OldCandidateId,
                            row.NewCandidateId ?? Texts.T("plugins.repair.unknown"), row.SlotId ?? Texts.T("plugins.repair.unknown")) + "\n" +
                            Texts.T("plugins.repair.paths", row.OldPath ?? Texts.T("plugins.repair.unknown"), row.NewPath ?? Texts.T("plugins.repair.unknown")) + "\n" +
                            Texts.T("plugins.repair.executions", row.OldExecutionRevision ?? Texts.T("plugins.repair.unknown"),
                                row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"))) };
                    content.Children.Add(technical);
                    content.Children.Add(RepairText(Texts.T(permissions is not null ? "plugins.repair.reauthorize.ready" :
                        row.BlockedReason is { } reason ? "plugins.repair.blocked." + reason :
                        row.BuildChanged ? "plugins.repair.changed" : "plugins.repair.sameBuild")));
                    if (row.CanReviewChanges)
                    {
                        AddBundledChangeReviewRow(repairRows, row, preview.ProfileRevision, approval, SaveAsync,
                            () => coherent, () => saving, UpdateSubmit);
                        continue;
                    }
                    if (row.CanReauthorize)
                    {
                        // Keep this permission decision bound to the exact backend row, fingerprint and profile revision.
                        var choice = new PluginApprovalChoice(PluginApprovalKind.Reauthorization, preview.ProfileRevision, Repair: row);
                        content.Children.Add(RepairText(Texts.T("plugins.repair.reauthorize.policy")));
                        if (permissions is not null)
                        {
                            content.Children.Add(RepairText(Texts.T("plugins.repair.reauthorize.rights", row.NewExecutionRevision!)));
                            foreach (string sourceId in permissions.SourceIds)
                                content.Children.Add(RepairText(Texts.T("plugins.repair.reauthorize.source", sourceId)));
                            foreach (PluginGrantSnapshot grant in permissions.FilterGrants)
                                content.Children.Add(RepairText(Texts.T("plugins.repair.reauthorize.filter", grant.FilterKey,
                                    grant.Scope.SourceId, grant.Scope.BoardId ?? Texts.T("plugins.allBoards"),
                                    grant.Scope.RuleId ?? Texts.T("plugins.allRules"), grant.Access)));
                        }
                        else content.Children.Add(RepairText(Texts.T("plugins.repair.reauthorize.unavailable")));
                        string acknowledgementText = Texts.T("plugins.repair.reauthorize.acknowledge", row.PluginId,
                            row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"));
                        CheckBox acknowledgement = new()
                        {
                            Content = RepairText(acknowledgementText), IsChecked = false, IsThreeState = false,
                            IsEnabled = coherent && approval.MayOffer(row.PluginId) && permissions is not null
                        };
                        AutomationProperties.SetAutomationId(acknowledgement, automationPrefix + "Acknowledge");
                        AutomationProperties.SetName(acknowledgement, acknowledgementText);
                        Button reauthorize = new()
                        {
                            Content = Texts.T("plugins.repair.reauthorize.confirm"), IsEnabled = false,
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        AutomationProperties.SetAutomationId(reauthorize, automationPrefix + "Confirm");
                        AutomationProperties.SetName(reauthorize, Texts.T("plugins.repair.reauthorize.confirmNamed", row.PluginId));
                        reauthorizations.Add((acknowledgement, choice));
                        acknowledgement.IsCheckedChanged += (_, _) =>
                        {
                            reauthorize.IsEnabled = !saving && coherent && approval.MayOffer(row.PluginId) &&
                                permissions is not null && row.CanReauthorize && acknowledgement.IsChecked == true;
                            UpdateSubmit();
                        };
                        reauthorize.Click += async (_, _) =>
                        {
                            if (!saving && coherent && approval.MayOffer(row.PluginId) && permissions is not null &&
                                row.CanReauthorize && acknowledgement.IsChecked == true)
                                await SaveAsync(new[] { choice });
                        };
                        content.Children.Add(acknowledgement);
                        content.Children.Add(reauthorize);
                    }
                    else
                    {
                        var choice = new PluginApprovalChoice(PluginApprovalKind.Repair, preview.ProfileRevision, Repair: row);
                        Button confirm = new()
                        {
                            Content = Texts.T("plugins.repair.confirm"), IsEnabled = coherent && approval.MayOffer(row.PluginId) && row.CanConfirm,
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        AutomationProperties.SetName(confirm, Texts.T("plugins.repair.confirmNamed", row.PluginId));
                        confirm.Click += async (_, _) =>
                        {
                            if (!saving && coherent && approval.MayOffer(row.PluginId) && row.CanConfirm)
                                await SaveAsync(new[] { choice });
                        };
                        content.Children.Add(confirm);
                    }
                    repairRows.Children.Add(new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1) });
                }
                selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.section")));
                selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.policy")));
                selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.backup")));
                if (selections.Rows.Count == 0) selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.empty")));
                foreach (BundledCandidateReselection row in selections.Rows)
                {
                    // Capture the exact backend row and revision. Never reconstruct settings or infer old provenance.
                    var choice = new PluginApprovalChoice(PluginApprovalKind.Reselection, selections.ProfileRevision, Selection: row);
                    StackPanel content = new() { Spacing = 6 };
                    content.Children.Add(RepairText(Texts.T("plugins.reselection.section")));
                    content.Children.Add(new TextBlock { Text = row.PluginId, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    Expander technical = new() { Header = PluginChangeReviewCatalog.T("plugins.changeReview.technical"), IsExpanded = false,
                        Content = RepairText(Texts.T("plugins.repair.identities", row.OldCandidateId,
                            row.NewCandidateId ?? Texts.T("plugins.repair.unknown"), row.SlotId ?? Texts.T("plugins.repair.unknown")) + "\n" +
                            Texts.T("plugins.repair.paths", row.OldPath ?? Texts.T("plugins.repair.unknown"), row.NewPath ?? Texts.T("plugins.repair.unknown")) + "\n" +
                            Texts.T("plugins.repair.executions", row.OldExecutionRevision ?? Texts.T("plugins.repair.unknown"),
                                row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"))) };
                    content.Children.Add(technical);
                    Expander configurationDetails = new() { Header = PluginChangeReviewCatalog.T("plugins.changeReview.technical"), IsExpanded = false };
                    TextBox configuration = new()
                    {
                        Text = Texts.T("plugins.reselection.configuration") + "\n" + (row.ConfigurationJson ?? Texts.T("plugins.reselection.configurationUnavailable")),
                        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    AutomationProperties.SetName(configurationDetails, Texts.T("plugins.reselection.configuration"));
                    configurationDetails.Content = configuration;
                    content.Children.Add(configurationDetails);
                    content.Children.Add(RepairText(ReselectionReason(row)));
                    CheckBox acknowledgement = new()
                    {
                        Content = RepairText(Texts.T("plugins.reselection.acknowledge")),
                        IsChecked = false, IsEnabled = coherent && approval.MayOffer(row.PluginId) && row.CanConfirm
                    };
                    AutomationProperties.SetName(acknowledgement, Texts.T("plugins.reselection.acknowledge"));
                    Button confirm = new()
                    {
                        Content = Texts.T("plugins.reselection.confirm"), IsEnabled = false,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    AutomationProperties.SetName(confirm, Texts.T("plugins.reselection.confirmNamed", row.PluginId));
                    acknowledgement.IsCheckedChanged += (_, _) => confirm.IsEnabled = !saving && coherent &&
                        approval.MayOffer(row.PluginId) && acknowledgement.IsChecked == true && row.CanConfirm;
                    confirm.Click += async (_, _) =>
                    {
                        if (!saving && coherent && approval.MayOffer(row.PluginId) && row.CanConfirm && acknowledgement.IsChecked == true)
                            await SaveAsync(new[] { choice });
                    };
                    content.Children.Add(acknowledgement);
                    content.Children.Add(confirm);
                    selectionRows.Children.Add(new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1) });
                }
                actionable = reauthorizations.Any(item => item.Ack.IsEnabled) ||
                    preview.Rows.Any(row => (row.CanReviewChanges || !row.CanReauthorize && row.CanConfirm) && coherent && approval.MayOffer(row.PluginId)) ||
                    selections.Rows.Any(row => row.CanConfirm && coherent && approval.MayOffer(row.PluginId));
                // Keep every proven repair available, but do not bury usable new choices below legacy denials.
                bool hasNewChoice = selections.Rows.Any(row => row.CanConfirm);
                if (receipts.Children.Count != 0) rows.Children.Add(receipts);
                if (hasNewChoice) rows.Children.Add(selectionRows);
                if (preview.Rows.Count != 0 || selections.Rows.Count == 0) rows.Children.Add(repairRows);
                if (!hasNewChoice && selections.Rows.Count != 0) rows.Children.Add(selectionRows);
                if (reauthorizations.Count > 1) rows.Children.Add(RepairText(Texts.T("plugins.approval.policy")));
                UpdateSubmit();
            }
            RenderRows();
            if (!coherent) SetStatus("plugins.approval.needsReview");
            submit.Click += async (_, _) =>
            {
                if (saving || !coherent || !Admit()) return;
                var frozen = reauthorizations.Where(item => item.Ack.IsEnabled && item.Ack.IsChecked == true)
                    .Select(item => item.Choice).ToArray();
                if (frozen.Length != 0) await SaveAsync(frozen);
            };
            reload.Click += async (_, _) =>
            {
                if (saving || !Admit()) return;
                saving = true; repairRows.IsEnabled = selectionRows.IsEnabled = reload.IsEnabled = false; UpdateSubmit();
                try
                {
                    await ReloadAsync();
                    if (SessionCurrent() && !closeRequested)
                    {
                        RenderRows(); SetStatus(coherent ? "plugins.approval.reviewAgain" : "plugins.approval.needsReview");
                        Emphasise(closeIsPrimary && coherent && !actionable);
                    }
                }
                catch (Exception)
                { coherent = false; if (SessionCurrent() && !closeRequested) { RenderRows(); SetStatus("plugins.approval.refreshFailed"); } }
                finally
                {
                    saving = false;
                    if (closeRequested || !SessionCurrent()) window.Close();
                    else { repairRows.IsEnabled = selectionRows.IsEnabled = reload.IsEnabled = true; UpdateSubmit(); }
                }
            };
            cancel.Click += (_, _) => window.Close();
            window.Closing += (_, args) =>
            {
                closeRequested = true; approval.Stop();
                if (saving) { args.Cancel = true; SetStatus("plugins.approval.stopping"); }
            };
            // One right-aligned row like the other dialogs; it wraps (and long labels wrap) at narrow widths.
            WrapPanel buttons = new();
            buttons.Classes.Add("workspace-actions");
            AutomationProperties.SetAutomationId(buttons, "BundledApprovalActions");
            buttons.Children.Add(cancel); buttons.Children.Add(reload); buttons.Children.Add(submit);
            Emphasise(false);
            StackPanel footer = new() { Spacing = 8 };
            footer.Children.Add(statusHost); footer.Children.Add(buttons);
            TextBlock heading = new() { Text = window.Title, TextWrapping = TextWrapping.Wrap };
            heading.Classes.Add("page-title");
            Border body = new() { Child = rows };
            body.Classes.Add("workspace-dialog-body");
            window.Content = MeowChan.Desktop.WindowChrome.Frame(window, DialogFrame.Build(heading, body, footer, DialogFrame.ErrorStrip(issue)));
            window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
            window.Opened += (_, _) => cancel.Focus();
            await window.ShowDialog<object?>(owner);
            if (approval.InFlight is { } inFlight) await inFlight;
            _migrationWindow = null; _migrationApproval = null;
            if (Current(generation)) await RefreshAsync(); // Never refresh page selection under the open modal.
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) ErrorFailure("plugins.error.repair", exception); }
        finally
        {
            CloseBundledRepairPreview(); _migrationWindow = null; _migrationApproval = null;
            FinishOperation(generation);
            if (!_ended && IsEffectivelyVisible && generation == _operationGeneration) C<Button>("PluginsReviewRepairs").Focus();
        }
    }

    private static BundledRepairPermissions? ReadBundledRepairPermissions(BundledCandidateRepair row)
    {
        if (!row.CanReauthorize || string.IsNullOrWhiteSpace(row.PermissionReviewJson) ||
            string.IsNullOrWhiteSpace(row.PermissionFingerprint) || string.IsNullOrWhiteSpace(row.NewExecutionRevision)) return null;
        try
        {
            BundledRepairPermissions permissions = SdkJson.Deserialize<BundledRepairPermissions>(
                Encoding.UTF8.GetBytes(row.PermissionReviewJson));
            // Fail closed if the UI cannot faithfully show every right; the backend revalidates on confirmation.
            if (permissions.FilterGrants is null || permissions.SourceIds is null ||
                permissions.FilterGrants.Count + permissions.SourceIds.Count == 0 ||
                permissions.SourceIds.Any(string.IsNullOrWhiteSpace) ||
                permissions.FilterGrants.Any(grant => grant is null || grant.Scope is null ||
                    grant.PluginId != row.PluginId || string.IsNullOrWhiteSpace(grant.FilterKey) ||
                    string.IsNullOrWhiteSpace(grant.Scope.SourceId))) return null;
            return permissions;
        }
        catch (JsonException) { return null; }
    }

    private static string ReselectionReason(BundledCandidateReselection row) => row.BlockedReason switch
    {
        null => Texts.T(row.CanConfirm ? "plugins.reselection.ready" : "plugins.reselection.blocked.unknown"),
        "pinned" or "unproven-configuration" or "pending-choice" or "ambiguous" or "no-proven-target" or
        "configuration-incompatible" or "inherited-grants" or "source-owner-conflict" or
        "source-identity-changed" or "proven-history" or "selection-available" => Texts.T("plugins.reselection.blocked." + row.BlockedReason),
        _ => Texts.T("plugins.reselection.blocked.unknown")
    };

    private static TextBlock RepairText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
