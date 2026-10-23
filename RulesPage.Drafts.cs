using System;
using System.Linq;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Rules;

namespace MeowChan;
public partial class RulesPage
{
    private readonly DraftDiscardConfirmation _discardConfirmation;
    internal bool IsDiscardConfirmationOpen => _discardConfirmation.IsOpen;
    internal bool HasDiscardableChanges => _draft.HasDiscardableChanges;
    internal void RequestDiscardCurrentDraft()
    {
        if (!IsEditable) return;
        RuleExtensionsInput.FlushPendingText();
        if (!_draft.HasDiscardableChanges) { DiscardCurrentDraft(); return; }
        RuleDraft captured = _draft; long generation = captured.Generation;
        _discardConfirmation.Open(FormatScope(captured.Scope), () =>
        {
            if (IsEditable && ReferenceEquals(captured, _draft) && captured.Generation == generation && captured.HasDiscardableChanges)
                DiscardCurrentDraft();
        }, context: () => !_ended && IsEditable && IsEffectivelyVisible && _draft.Scope == captured.Scope,
            completedFocus: () => DiscardRuleButton);

    }
    private void RefreshBreadcrumb()
    {
        RuleScopeBreadcrumb.Items.Clear();
        MediaRuleScope scope = _draft.Scope;
        var scopes = new System.Collections.Generic.List<MediaRuleScope> { MediaRuleScope.Global };
        if (scope.Kind >= MediaRuleScopeKind.Source) scopes.Add(MediaRuleScope.ForSource(scope.Source!));
        if (scope.Kind >= MediaRuleScopeKind.Board) scopes.Add(MediaRuleScope.ForBoard(scope.Source!, scope.Board!));
        if (scope.Kind == MediaRuleScopeKind.Thread) scopes.Add(scope);
        foreach (MediaRuleScope item in scopes)
        {
            var button = new Ursa.Controls.BreadcrumbItem { Content = FormatScope(item), Tag = item, IsEnabled = item != scope && IsEditable,
                MaxWidth = 320 };
            AutomationProperties.SetName(button, FormatScope(item) + (item == scope ? " · " + Desktop.Localization.Texts.T("rules.currentScope") : string.Empty));
            ToolTip.SetTip(button, FormatScope(item));
            button.Click += (_, _) => { if (IsEditable && item != _draft.Scope) OpenNavigatedScope(item); };
            RuleScopeBreadcrumb.Items.Add(button);
        }
    }
    internal sealed partial class RuleDraft
    {
        private string _initial = string.Empty;
        private string _initialMegabytes = string.Empty;
        private CultureInfo _initialCulture = CultureInfo.InvariantCulture;
        internal Desktop.Plugins.PluginSettingsDraft? BoundFilterParameters { get; set; }
        internal long Generation { get; set; }
        internal RuleDraft? TemplateUndo { get; set; }
        internal bool TemplateUndoInvalidated { get; set; }
        private string ValuesFingerprint => Enabled + "\0" + TypesMode + "\0" + ExtensionInput.Fingerprint + "\0" + SizeMode
            + "\0" + ImageFilter.Mode + "\0" + ImageFilter.PluginId + "\0" + ImageFilter.FilterKey + "\0" + ImageFilter.SchemaVersion
            + "\0" + string.Join("\0", ImageFilter.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
            + "\0" + FilterSchemaAcknowledged + "\0" + (LastPluginFilter == null || ImageFilter.Mode == MediaImageFilterMode.Plugin ? "" : LastPluginFilter.Mode + ":" + LastPluginFilter.PluginId + ":" + LastPluginFilter.FilterKey);
        internal bool HasDiscardableChanges => ValuesFingerprint != _initial || Megabytes != _initialMegabytes
            || NumberCulture.Name != _initialCulture.Name || FilterParameters?.HasDiscardableChanges == true;
        internal void CaptureInitial() { _initial = ValuesFingerprint; _initialMegabytes = Megabytes; _initialCulture = NumberCulture; }
        internal void ReformatInitialNumber(CultureInfo culture)
        {
            if (TryReadMegabytes(_initialMegabytes, _initialCulture, out long bytes))
            { _initialMegabytes = FormatMegabytes(bytes, culture); _initialCulture = culture; }
        }
        internal RuleDraft DeepClone()
        {
            var copy = (RuleDraft)MemberwiseClone(); copy.ExtensionInput = ExtensionInput.Clone(); copy.FilterParameters = FilterParameters?.DeepClone(); copy.TemplateUndo = null; copy.BoundFilterParameters = null; return copy;
        }
        internal void Restore(RuleDraft copy)
        {
            Enabled = copy.Enabled; TypesMode = copy.TypesMode; ExtensionInput = copy.ExtensionInput.Clone();
            SizeMode = copy.SizeMode; Megabytes = copy.Megabytes; NumberCulture = copy.NumberCulture;
            ImageFilter = copy.ImageFilter; LastPluginFilter = copy.LastPluginFilter; FilterParameters = copy.FilterParameters?.DeepClone();
            FilterSchemaAcknowledged = copy.FilterSchemaAcknowledged; FilterValidationErrors = copy.FilterValidationErrors;
            BoundFilterParameters = null;
            Stale = copy.Stale; Dirty = copy.Dirty;
        }
    }
}
