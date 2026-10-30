using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Configuration;

namespace MeowChan;

public partial class PluginsPage
{
    private Window? _migrationWindow;

    private void CloseBundledRepairPreview() => _migrationWindow?.Close();

    private async void OnReviewBundledRepairsClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || _services is null || !IsEffectivelyVisible ||
            !CommandRouter.IsRoutingContextActive(this, this) || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_drafts.Values.Any(draft => draft.HasDiscardableChanges))
        { Error(Texts.T("plugins.repair.finishDraft")); return; }
        var services = _services;
        string language = Texts.Language;
        var (generation, token) = BeginOperation();
        try
        {
            BundledCandidateRepairPreview preview = await services.ProfileStore.PreviewBundledRepairsAsync(token);
            if (!Current(generation) || !IsEffectivelyVisible || Texts.Language != language) return;
            BundledCandidateReselectionPreview selections = await services.ProfileStore.PreviewBundledReselectionsAsync(token);
            if (!Current(generation) || !IsEffectivelyVisible || Texts.Language != language) return;
            Window window = new()
            {
                Title = Texts.T("plugins.reselection.reviewTitle"),
                Width = Math.Max(320, Math.Min(880, owner.Bounds.Width - 32)),
                Height = Math.Max(320, Math.Min(700, owner.Bounds.Height - 32)),
                CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _migrationWindow = window;
            StackPanel rows = new() { Spacing = 16 };
            StackPanel repairRows = new() { Spacing = 16 };
            StackPanel selectionRows = new() { Spacing = 16 };
            AutomationProperties.SetAutomationId(repairRows, "BundledProvenRepairs");
            AutomationProperties.SetAutomationId(selectionRows, "BundledNewSelections");
            repairRows.Children.Add(RepairText(Texts.T("plugins.reselection.repairSection")));
            repairRows.Children.Add(RepairText(Texts.T("plugins.repair.policy")));
            if (preview.Rows.Count == 0) repairRows.Children.Add(RepairText(Texts.T("plugins.repair.empty")));
            foreach (BundledCandidateRepair row in preview.Rows)
            {
                StackPanel content = new() { Spacing = 6 };
                content.Children.Add(new TextBlock { Text = row.PluginId, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                content.Children.Add(RepairText(Texts.T("plugins.repair.identities", row.OldCandidateId,
                    row.NewCandidateId ?? Texts.T("plugins.repair.unknown"), row.SlotId ?? Texts.T("plugins.repair.unknown"))));
                content.Children.Add(RepairText(Texts.T("plugins.repair.paths", row.OldPath ?? Texts.T("plugins.repair.unknown"),
                    row.NewPath ?? Texts.T("plugins.repair.unknown"))));
                content.Children.Add(RepairText(Texts.T("plugins.repair.executions", row.OldExecutionRevision ?? Texts.T("plugins.repair.unknown"),
                    row.NewExecutionRevision ?? Texts.T("plugins.repair.unknown"))));
                content.Children.Add(RepairText(Texts.T(row.BlockedReason is { } reason
                    ? "plugins.repair.blocked." + reason : row.BuildChanged ? "plugins.repair.changed" : "plugins.repair.sameBuild")));
                Button confirm = new()
                {
                    Content = Texts.T("plugins.repair.confirm"), IsEnabled = row.CanConfirm,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetName(confirm, Texts.T("plugins.repair.confirmNamed", row.PluginId));
                confirm.Click += (_, _) =>
                {
                    // The row and revision belong to this modal's immutable preview, not current list selection.
                    if (Current(generation) && window.IsActive && IsEffectivelyVisible && Texts.Language == language && row.CanConfirm)
                        window.Close(new ProvenRepairChoice(row, preview.ProfileRevision));
                };
                content.Children.Add(confirm);
                repairRows.Children.Add(new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1) });
            }
            selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.section")));
            selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.policy")));
            selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.backup")));
            if (selections.Rows.Count == 0) selectionRows.Children.Add(RepairText(Texts.T("plugins.reselection.empty")));
            foreach (BundledCandidateReselection row in selections.Rows)
            {
                // Capture the exact backend row and revision. Never reconstruct settings or infer old provenance.
                var choice = new NewSelectionChoice(row, selections.ProfileRevision);
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
                    IsChecked = false, IsEnabled = row.CanConfirm
                };
                AutomationProperties.SetName(acknowledgement, Texts.T("plugins.reselection.acknowledge"));
                Button confirm = new()
                {
                    Content = Texts.T("plugins.reselection.confirm"), IsEnabled = false,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetName(confirm, Texts.T("plugins.reselection.confirmNamed", row.PluginId));
                acknowledgement.IsCheckedChanged += (_, _) => confirm.IsEnabled = acknowledgement.IsChecked == true && row.CanConfirm;
                confirm.Click += (_, _) =>
                {
                    if (Current(generation) && window.IsActive && IsEffectivelyVisible && Texts.Language == language &&
                        row.CanConfirm && acknowledgement.IsChecked == true) window.Close(choice);
                };
                content.Children.Add(acknowledgement);
                content.Children.Add(confirm);
                selectionRows.Children.Add(new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1) });
            }
            // Keep every proven repair available, but do not bury usable new choices below legacy denials.
            bool hasNewChoice = selections.Rows.Any(row => row.CanConfirm);
            rows.Children.Add(hasNewChoice ? selectionRows : repairRows);
            rows.Children.Add(hasNewChoice ? repairRows : selectionRows);
            Button cancel = new() { Content = Texts.T("plugins.repair.cancel"), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(cancel, Texts.T("plugins.repair.cancel"));
            cancel.Click += (_, _) => window.Close();
            Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
            layout.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(cancel, 1); layout.Children.Add(cancel); window.Content = layout;
            window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
            window.Opened += (_, _) => cancel.Focus();
            object? chosen = await window.ShowDialog<object?>(owner);
            _migrationWindow = null;
            if (chosen is null || !Current(generation) || !IsEffectivelyVisible || Texts.Language != language ||
                !CommandRouter.IsRoutingContextActive(this, this)) return;
            switch (chosen)
            {
                case ProvenRepairChoice repair:
                    await services.ProfileStore.ConfirmBundledRepairAsync(repair.Row, repair.Revision, token);
                    break;
                case NewSelectionChoice selection:
                    await services.ProfileStore.ConfirmBundledReselectionAsync(selection.Row, selection.Revision, token);
                    break;
                default: return;
            }
            if (!Current(generation)) return;
            await RefreshAsync();
            if (Current(generation)) SetTestStatus(chosen is NewSelectionChoice ? "plugins.reselection.saved" : "plugins.repair.saved");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
        finally
        {
            CloseBundledRepairPreview(); _migrationWindow = null;
            FinishOperation(generation);
            if (!_ended && IsEffectivelyVisible && generation == _operationGeneration) C<Button>("PluginsReviewRepairs").Focus();
        }
    }

    private sealed record ProvenRepairChoice(BundledCandidateRepair Row, long Revision);
    private sealed record NewSelectionChoice(BundledCandidateReselection Row, long Revision);

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
