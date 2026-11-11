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
            TextBlock status = RepairText("");
            status.Focusable = true;
            AutomationProperties.SetAutomationId(status, "BundledApprovalStatus");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            Button submit = new();
            AutomationProperties.SetAutomationId(submit, "BundledApprovalSubmit");
            Button reload = new() { Content = RepairText(Texts.T("plugins.approval.reload")) };
            AutomationProperties.SetAutomationId(reload, "BundledApprovalReload");
            Button cancel = new() { Content = Texts.T("plugins.repair.cancel") };
            AutomationProperties.SetName(cancel, Texts.T("plugins.repair.cancel"));
            AutomationProperties.SetAutomationId(cancel, "BundledApprovalClose");
            void UpdateSubmit()
            {
                int count = reauthorizations.Count(item => item.Ack.IsChecked == true);
                submit.Content = RepairText(Texts.T("plugins.approval.submit", count));
                AutomationProperties.SetName(submit, Texts.T("plugins.approval.submit", count));
                submit.IsEnabled = !saving && coherent && count > 0;
            }
            void SetStatus(string key) { status.Text = Texts.T(key); AutomationProperties.SetName(status, status.Text); }
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
                        SetStatus(approval.IssueKey ?? (coherent ? "plugins.approval.reviewAgain" : "plugins.approval.needsReview"));
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
                    status.Focus();
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
                repairRows.Children.Add(RepairText(Texts.T("plugins.reselection.repairSection")));
                repairRows.Children.Add(RepairText(Texts.T("plugins.repair.policy")));
                if (preview.Rows.Count == 0) repairRows.Children.Add(RepairText(Texts.T("plugins.repair.empty")));
                int repairRowIndex = 0;
                foreach (BundledCandidateRepair row in preview.Rows.OrderByDescending(row => row.CanReauthorize || row.CanConfirm))
                {
                    string automationPrefix = "BundledRepairReauthorization" + repairRowIndex++;
                    BundledRepairPermissions? permissions = ReadBundledRepairPermissions(row);
                    StackPanel content = new() { Spacing = 6 };
                    content.Children.Add(new TextBlock { Text = row.PluginId, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    content.Children.Add(RepairText(Texts.T("plugins.repair.identities", row.OldCandidateId,
                        row.NewCandidateId ?? Texts.T("plugins.repair.unknown"), row.SlotId ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T("plugins.repair.paths", row.OldPath ?? Texts.T("plugins.repair.unknown"),
                        row.NewPath ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T("plugins.repair.executions", row.OldExecutionRevision ?? Texts.T("plugins.repair.unknown"),
                        row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T(permissions is not null ? "plugins.repair.reauthorize.ready" :
                        row.BlockedReason is { } reason ? "plugins.repair.blocked." + reason :
                        row.BuildChanged ? "plugins.repair.changed" : "plugins.repair.sameBuild")));
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
                    content.Children.Add(RepairText(Texts.T("plugins.repair.identities", row.OldCandidateId,
                        row.NewCandidateId ?? Texts.T("plugins.repair.unknown"), row.SlotId ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T("plugins.repair.paths", row.OldPath ?? Texts.T("plugins.repair.unknown"),
                        row.NewPath ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T("plugins.repair.executions", row.OldExecutionRevision ?? Texts.T("plugins.repair.unknown"),
                        row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"))));
                    content.Children.Add(RepairText(Texts.T("plugins.reselection.configuration")));
                    TextBox configuration = new()
                    {
                        Text = row.ConfigurationJson ?? Texts.T("plugins.reselection.configurationUnavailable"),
                        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    AutomationProperties.SetName(configuration, Texts.T("plugins.reselection.configuration"));
                    content.Children.Add(configuration);
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
                // Keep every proven repair available, but do not bury usable new choices below legacy denials.
                bool hasNewChoice = selections.Rows.Any(row => row.CanConfirm);
                if (receipts.Children.Count != 0) rows.Children.Add(receipts);
                rows.Children.Add(hasNewChoice ? selectionRows : repairRows);
                rows.Children.Add(hasNewChoice ? repairRows : selectionRows);
                rows.Children.Add(RepairText(Texts.T("plugins.approval.policy")));
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
                    { RenderRows(); SetStatus(coherent ? "plugins.approval.reviewAgain" : "plugins.approval.needsReview"); }
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
            StackPanel footer = new() { Spacing = 6 };
            StackPanel buttons = new() { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            buttons.Children.Add(submit); buttons.Children.Add(reload); buttons.Children.Add(cancel); footer.Children.Add(buttons);
            TextBlock heading = new() { Text = window.Title, TextWrapping = TextWrapping.Wrap };
            heading.Classes.Add("page-title");
            Border body = new() { Child = rows };
            body.Classes.Add("workspace-dialog-body");
            window.Content = DialogFrame.Build(heading, body, footer, DialogFrame.ErrorStrip(status));
            window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
            window.Opened += (_, _) => cancel.Focus();
            await window.ShowDialog<object?>(owner);
            if (approval.InFlight is { } inFlight) await inFlight;
            _migrationWindow = null; _migrationApproval = null;
            if (Current(generation)) await RefreshAsync(); // Never refresh page selection under the open modal.
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
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
