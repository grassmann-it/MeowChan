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
        var (generation, token) = BeginOperation();
        try
        {
            BundledCandidateRepairPreview preview = await services.ProfileStore.PreviewBundledRepairsAsync(token);
            if (!Current(generation) || !IsEffectivelyVisible) return;
            Window window = new()
            {
                Title = Texts.T("plugins.repair.title"),
                Width = Math.Max(320, Math.Min(880, owner.Bounds.Width - 32)),
                Height = Math.Max(320, Math.Min(700, owner.Bounds.Height - 32)),
                CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _migrationWindow = window;
            StackPanel rows = new() { Spacing = 16 };
            rows.Children.Add(RepairText(Texts.T("plugins.repair.policy")));
            if (preview.Rows.Count == 0) rows.Children.Add(RepairText(Texts.T("plugins.repair.empty")));
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
                    if (Current(generation) && window.IsActive && row.CanConfirm) window.Close(row);
                };
                content.Children.Add(confirm);
                rows.Children.Add(new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1) });
            }
            Button cancel = new() { Content = Texts.T("plugins.repair.cancel"), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(cancel, Texts.T("plugins.repair.cancel"));
            cancel.Click += (_, _) => window.Close();
            Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
            layout.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(cancel, 1); layout.Children.Add(cancel); window.Content = layout;
            window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
            window.Opened += (_, _) => cancel.Focus();
            BundledCandidateRepair? chosen = await window.ShowDialog<BundledCandidateRepair?>(owner);
            _migrationWindow = null;
            if (chosen is null || !Current(generation) || !IsEffectivelyVisible) return;
            await services.ProfileStore.ConfirmBundledRepairAsync(chosen, preview.ProfileRevision, token);
            if (!Current(generation)) return;
            await RefreshAsync();
            if (Current(generation)) SetTestStatus("plugins.repair.saved");
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

    private static TextBlock RepairText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
