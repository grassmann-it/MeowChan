using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class PluginsPage
{
    internal static bool TryReadBundledChangeReview(BundledCandidateRepair row,
        out BundledPluginChangeReview review)
    {
        review = null!;
        if (!row.CanReviewChanges || string.IsNullOrWhiteSpace(row.ChangeReviewJson) ||
            string.IsNullOrWhiteSpace(row.ChangeFingerprint)) return false;
        try
        {
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.ChangeReviewJson)));
            if (!string.Equals(fingerprint, row.ChangeFingerprint, StringComparison.Ordinal)) return false;
            BundledPluginChangeReview candidate = SdkJson.Deserialize<BundledPluginChangeReview>(
                Encoding.UTF8.GetBytes(row.ChangeReviewJson));
            if (candidate.PreviousDescription is null || candidate.NewDescription is null ||
                candidate.PreviousConfiguration is null || candidate.Permissions is null ||
                candidate.PreviousDescription.PluginId != row.PluginId || candidate.NewDescription.PluginId != row.PluginId ||
                candidate.PreviousDescription.Sources is null || candidate.NewDescription.Sources is null ||
                candidate.NewDescription.Filters is null || candidate.NewDescription.Settings is null ||
                SdkValidation.ValidateDescription(candidate.NewDescription).Count != 0 ||
                !candidate.PreviousDescription.Sources.Select(source => source.SourceId).Order(StringComparer.Ordinal)
                    .SequenceEqual(candidate.NewDescription.Sources.Select(source => source.SourceId).Order(StringComparer.Ordinal)) ||
                candidate.Permissions.FilterGrants is null || candidate.Permissions.SourceIds is null ||
                candidate.Permissions.SourceIds.Any(string.IsNullOrWhiteSpace) ||
                candidate.Permissions.SourceIds.Distinct(StringComparer.Ordinal).Count() != candidate.Permissions.SourceIds.Count ||
                candidate.Permissions.FilterGrants.Any(grant => grant is null || grant.Scope is null ||
                    grant.PluginId != row.PluginId || string.IsNullOrWhiteSpace(grant.FilterKey) ||
                    string.IsNullOrWhiteSpace(grant.Scope.SourceId))) return false;
            review = candidate;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NullReferenceException) { return false; }
    }

    private static string ChangeSettingsSummary(PluginConfiguration configuration) =>
        PluginChangeReviewCatalog.T("plugins.changeReview.oldConfig",
            configuration.PluginSettings.Values.Count + configuration.SourceSettings.Values.Sum(settings => settings.Values.Count));

    private static string NewSettingsSummary(PluginDescription description) =>
        PluginChangeReviewCatalog.T("plugins.changeReview.newConfig",
            description.Settings.Fields.Count + description.Sources.Sum(source => source.Settings.Fields.Count));

    private static string CapabilitySummary(PluginDescription description)
    {
        if (description.Sources.Count == 0) return PluginChangeReviewCatalog.T("plugins.changeReview.noCapabilities");
        return string.Join(Environment.NewLine, description.Sources.Select(source =>
            PluginChangeReviewCatalog.T("plugins.changeReview.sourceCapabilities", source.Name,
                PluginContentPresentation.DescribeCapabilities(source.Capabilities))));
    }

    private static string RightsSummary(BundledRepairPermissions permissions)
    {
        List<string> lines = new();
        lines.AddRange(permissions.SourceIds.Select(source =>
            PluginChangeReviewCatalog.T("plugins.changeReview.sourceRight", source)));
        lines.AddRange(permissions.FilterGrants.Select(grant => PluginChangeReviewCatalog.T(
            "plugins.changeReview.filterRight", grant.FilterKey, grant.Scope.SourceId,
            grant.Scope.BoardId ?? Texts.T("plugins.allBoards"), grant.Scope.RuleId ?? Texts.T("plugins.allRules"), grant.Access)));
        return lines.Count == 0 ? PluginChangeReviewCatalog.T("plugins.changeReview.rightsNone") : string.Join(Environment.NewLine, lines);
    }

    private void AddBundledChangeReviewRow(StackPanel parent, BundledCandidateRepair row, long revision,
        PluginMultiApproval approval, Func<IReadOnlyList<PluginApprovalChoice>, Task> saveAsync,
        Func<bool> coherent, Func<bool> saving, Action updateSubmit)
    {
        StackPanel content = new() { Spacing = 8 };
        if (!TryReadBundledChangeReview(row, out BundledPluginChangeReview review))
        {
            content.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.invalidReview"), TextWrapping = TextWrapping.Wrap });
            parent.Children.Add(new Border { Child = content, Padding = new Avalonia.Thickness(12), BorderThickness = new Avalonia.Thickness(1) });
            return;
        }

        content.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.plugin", review.NewDescription.Name),
            FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.explanation", review.NewDescription.Name), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.capabilities"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1*"),
            Children =
            {
                new Border { Child = new StackPanel { Spacing = 4, Children = {
                    new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.old"), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = CapabilitySummary(review.PreviousDescription), TextWrapping = TextWrapping.Wrap }
                }}, Padding = new Avalonia.Thickness(0, 0, 8, 0) },
                new Border { Child = new StackPanel { Spacing = 4, Children = {
                    new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.new"), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = CapabilitySummary(review.NewDescription), TextWrapping = TextWrapping.Wrap }
                }}, Padding = new Avalonia.Thickness(8, 0, 0, 0) }
            }
        });
        Grid.SetColumn(((Grid)content.Children[^1]).Children[1], 1);

        BundledPluginChangeDraft draft = new(review);
        StackPanel settingsPanel = new() { Spacing = 6 };
        StackPanel editorPanel = new() { Spacing = 8 };
        Dictionary<PluginSettingsEditor, string> editors = new();
        Button? removedAcknowledgement = null;
        void RenderEditors()
        {
            editorPanel.Children.Clear();
            if (draft.NeedsRemovedValueAcknowledgement)
            {
                removedAcknowledgement ??= new Button { Content = Texts.T("plugins.acceptSchema"), HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetAutomationId(removedAcknowledgement, "BundledChangeReviewSchemaAcknowledgement:" + row.PluginId);
                removedAcknowledgement.Click -= AcknowledgeRemoved;
                removedAcknowledgement.Click += AcknowledgeRemoved;
                editorPanel.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.removedValues"), TextWrapping = TextWrapping.Wrap });
                editorPanel.Children.Add(removedAcknowledgement);
            }
            AddReviewEditor(editorPanel, PluginChangeReviewCatalog.T("plugins.changeReview.newSettings"), string.Empty, draft.Plugin);
            foreach ((string sourceId, PluginSettingsDraft source) in draft.Sources)
                AddReviewEditor(editorPanel, sourceId, sourceId, source);
            foreach ((string sourceId, SettingsSnapshot _) in draft.RemovedSources)
                editorPanel.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.removedSource", sourceId), TextWrapping = TextWrapping.Wrap });
        }
        void AcknowledgeRemoved(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
        {
            draft.AcknowledgeRemovedValues();
            RenderEditors();
        }
        void AddReviewEditor(StackPanel host, string title, string scope, PluginSettingsDraft settings)
        {
            if (settings.Schema.Fields.Count == 0 && settings.UnmappedValues.Count == 0) return;
            PluginSettingsEditor editor = new(settings, "bundled-change:" + row.PluginId + ":" + scope);
            editors[editor] = scope;
            Expander expander = new() { Header = title, Content = editor, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            host.Children.Add(expander);
        }
        RenderEditors();
        settingsPanel.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.settings"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        settingsPanel.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.settingsHelp"), TextWrapping = TextWrapping.Wrap });
        settingsPanel.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1*"),
            Children =
            {
                new TextBlock { Text = ChangeSettingsSummary(review.PreviousConfiguration), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = NewSettingsSummary(review.NewDescription), TextWrapping = TextWrapping.Wrap }
            }
        });
        Grid.SetColumn(((Grid)settingsPanel.Children[^1]).Children[1], 1);
        settingsPanel.Children.Add(editorPanel);
        Expander settingsExpander = new() { Header = PluginChangeReviewCatalog.T("plugins.changeReview.settings"), Content = settingsPanel,
            IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(settingsExpander);

        bool hasRights = review.Permissions.SourceIds.Count > 0 || review.Permissions.FilterGrants.Count > 0;
        CheckBox changes = new() { Content = new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.ackChanges"), TextWrapping = TextWrapping.Wrap }, IsThreeState = false };
        AutomationProperties.SetAutomationId(changes, "BundledChangeReviewChanges:" + row.PluginId);
        CheckBox? rights = null;
        if (hasRights)
        {
            content.Children.Add(new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.rights"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = RightsSummary(review.Permissions), TextWrapping = TextWrapping.Wrap });
            rights = new CheckBox { Content = new TextBlock { Text = PluginChangeReviewCatalog.T("plugins.changeReview.ackRights"), TextWrapping = TextWrapping.Wrap }, IsThreeState = false };
            AutomationProperties.SetAutomationId(rights, "BundledChangeReviewRights:" + row.PluginId);
            content.Children.Add(rights);
        }
        content.Children.Add(changes);
        TextBlock validation = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Classes = { "error", "workspace-error" } };
        content.Children.Add(validation);
        Button save = new() { Content = PluginChangeReviewCatalog.T("plugins.changeReview.save"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(save, "BundledChangeReviewSave:" + row.PluginId);
        AutomationProperties.SetName(save, PluginChangeReviewCatalog.T("plugins.changeReview.save"));
        void UpdateSave()
        {
            save.IsEnabled = !saving() && coherent() && approval.MayOffer(row.PluginId) && changes.IsChecked == true &&
                (!hasRights || rights?.IsChecked == true);
            updateSubmit();
        }
        changes.IsCheckedChanged += (_, _) => UpdateSave();
        if (rights is not null) rights.IsCheckedChanged += (_, _) => UpdateSave();
        save.Click += async (_, _) =>
        {
            if (!save.IsEnabled) return;
            PluginConfiguration configuration = draft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors);
            foreach ((PluginSettingsEditor editor, string scope) in editors)
                editor.ShowErrors(PluginConfigurationFieldError.ForScope(errors, scope));
            if (errors.Count != 0)
            {
                validation.Text = PluginChangeReviewCatalog.T("plugins.changeReview.invalidFields");
                validation.IsVisible = true;
                PluginSettingsEditor? first = editorPanel.GetVisualDescendants().OfType<PluginSettingsEditor>().FirstOrDefault();
                if (first is not null && !first.TryFocusFirstError(() => coherent() && !saving())) first.Focus();
                else if (removedAcknowledgement is not null && draft.NeedsRemovedValueAcknowledgement) removedAcknowledgement.Focus();
                return;
            }
            validation.IsVisible = false;
            await saveAsync(new[] { new PluginApprovalChoice(PluginApprovalKind.ChangeReview, revision, Repair: row,
                ChangeConfiguration: configuration, AcknowledgeChanges: true, AuthorizePermissions: hasRights && rights?.IsChecked == true) });
        };
        content.Children.Add(save);
        Expander technical = new() { Header = PluginChangeReviewCatalog.T("plugins.changeReview.technical"), IsExpanded = false,
            Content = new TextBox { Text = PluginChangeReviewCatalog.T("plugins.changeReview.identities", row.OldCandidateId, row.NewCandidateId ?? "?",
                row.SlotId ?? "?", row.OldExecutionRevision ?? "?", row.NewExecutionRevision ?? "?") + "\n\n" +
                PluginChangeReviewCatalog.T("plugins.changeReview.json") + "\n" + row.ChangeReviewJson,
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 } };
        content.Children.Add(technical);
        parent.Children.Add(new Border { Child = content, Padding = new Avalonia.Thickness(12), BorderThickness = new Avalonia.Thickness(1) });
        UpdateSave();
    }
}
