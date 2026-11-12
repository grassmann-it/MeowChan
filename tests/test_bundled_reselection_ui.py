"""Source-contract checks only; not a substitute for native modal interaction QA.

Run: python3 -m unittest discover -s tests -p test_bundled_reselection_ui.py -v
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI = (ROOT / "Desktop/Views/Plugins/PluginsPage.Migration.cs").read_text()
CATALOG = (ROOT / "Desktop/Localization/BuiltInCatalog.cs").read_text()
APPROVAL = (ROOT / "Desktop/Views/Plugins/PluginMultiApproval.cs").read_text()
PAGE = (ROOT / "Desktop/Views/Plugins/PluginsPage.axaml.cs").read_text()
# The new-selection rows of the single multi-approval dialog (ends where the row loop hands over to the submit logic).
NEW = UI[UI.index("foreach (BundledCandidateReselection row"):UI.index("actionable = reauthorizations.Any(")]


def squash(text):
    """Collapse whitespace so wrapped C# conditions compare like their one-line form."""
    return re.sub(r"\s+", " ", text)


NEW_FLAT = squash(NEW)
UI_FLAT = squash(UI)
REASONS = (
    "pinned", "unproven-configuration", "pending-choice", "ambiguous",
    "no-proven-target", "configuration-incompatible", "inherited-grants",
    "source-owner-conflict", "source-identity-changed", "proven-history",
    "selection-available", "unknown",
)


class BundledReselectionUiContractTests(unittest.TestCase):
    def test_untouched_exact_configuration_is_fully_reviewable(self):
        self.assertIn('Text = row.ConfigurationJson ??', NEW)
        self.assertIn('IsReadOnly = true, AcceptsReturn = true', NEW)
        for forbidden in ('JsonSerializer', 'Substring', '.Trim(', 'MaxLength', 'PasswordChar'):
            self.assertNotIn(forbidden, NEW)
        for field in ('OldCandidateId', 'NewCandidateId', 'SlotId', 'OldPath',
                      'NewPath', 'OldExecutionRevision', 'NewExecutionRevision'):
            self.assertIn('row.' + field, NEW)

    def test_explicit_unchecked_acknowledgement_gates_click_and_button(self):
        self.assertIn('IsChecked = false, IsEnabled = coherent && approval.MayOffer(row.PluginId) && row.CanConfirm', NEW_FLAT)
        self.assertIn('Content = Texts.T("plugins.reselection.confirm"), IsEnabled = false', NEW_FLAT)
        self.assertIn('confirm.IsEnabled = !saving && coherent && approval.MayOffer(row.PluginId) && '
                      'acknowledgement.IsChecked == true && row.CanConfirm', NEW_FLAT)
        self.assertIn('if (!saving && coherent && approval.MayOffer(row.PluginId) && row.CanConfirm && '
                      'acknowledgement.IsChecked == true) await SaveAsync(new[] { choice });', NEW_FLAT)

    def test_each_path_submits_its_own_frozen_row_and_revision(self):
        # The UI freezes the exact backend row together with the revision of the preview that produced it.
        self.assertIn('new PluginApprovalChoice(PluginApprovalKind.Reselection, selections.ProfileRevision, Selection: row)', NEW_FLAT)
        self.assertIn('new PluginApprovalChoice(PluginApprovalKind.Repair, preview.ProfileRevision, Repair: row)', UI_FLAT)
        self.assertIn('new PluginApprovalChoice(PluginApprovalKind.Reauthorization, preview.ProfileRevision, Repair: row)', UI_FLAT)
        # Mixed previews never become actionable.
        self.assertIn('coherent = preview.ProfileRevision == selections.ProfileRevision', UI_FLAT)
        # Dispatch uses the frozen row and the expected revision, and re-checks the row by value first.
        approval = squash(APPROVAL)
        self.assertIn('store.ReauthorizeBundledRepairAsync(choice.Repair!, expected, token)', approval)
        self.assertIn('store.ConfirmBundledRepairAsync(choice.Repair!, expected, token)', approval)
        self.assertIn('store.ConfirmBundledReselectionAsync(choice.Selection!, expected, token)', approval)
        self.assertIn('matches[0] == choice.Selection && matches[0].CanConfirm', approval)
        self.assertIn('matches[0] == choice.Repair', approval)
        # Only reauthorizations may be batched; repair and new selection are always submitted alone.
        self.assertIn('frozen.Any(choice => choice.Kind != PluginApprovalKind.Reauthorization)', approval)
        self.assertIn('window.ShowDialog<object?>(owner)', UI)

    def test_cancel_focus_staleness_visibility_language_and_deactivation_guards(self):
        # Opening: owner must be visible, routing context active, language and catalog epoch unchanged across both reads.
        self.assertIn('!CommandRouter.IsRoutingContextActive(this, this) || TopLevel.GetTopLevel(this) is not Window owner', UI_FLAT)
        for _ in range(2):
            self.assertIn('if (!Current(generation) || !IsEffectivelyVisible || Texts.Language != language || '
                          'Interlocked.Read(ref _migrationCatalogEpoch) != catalogEpoch) return;', UI_FLAT)
        # Session and admission guards used before every write.
        self.assertIn('bool SessionCurrent() => Current(generation) && ReferenceEquals(_services, services) && '
                      'ReferenceEquals(_migrationWindow, window) && IsEffectivelyVisible && '
                      'ReferenceEquals(TopLevel.GetTopLevel(this), owner) && owner.IsVisible && '
                      'Texts.Language == language && Interlocked.Read(ref _migrationCatalogEpoch) == catalogEpoch;', UI_FLAT)
        self.assertIn('bool Admit() => SessionCurrent() && !closeRequested && window.IsVisible && '
                      'ReferenceEquals(window.Owner, owner) && CommandRouter.IsRoutingContextActive(window, window);', UI_FLAT)
        # Cancel, Escape and initial focus; closing stops the approval and is held back while a save runs.
        self.assertIn('cancel.Click += (_, _) => window.Close()', UI)
        self.assertIn('args.Key == Key.Escape', UI)
        self.assertIn('window.Opened += (_, _) => cancel.Focus()', UI)
        self.assertIn('closeRequested = true; approval.Stop(); if (saving) { args.Cancel = true;', UI_FLAT)
        # Language change invalidates the epoch and stops the approval synchronously, then closes the dialog.
        start = PAGE.index('private void OnLanguageChanged')
        handler = PAGE[start:PAGE.index('\n    }\n', start)]
        for part in ('Interlocked.Increment(ref _migrationCatalogEpoch)', '_migrationApproval?.Stop()', 'CloseBundledRepairPreview()'):
            self.assertIn(part, handler)
        self.assertRegex(PAGE, r'internal void Deactivate\(\)[^\n]*CloseBundledRepairPreview\(\)')
        self.assertRegex(PAGE, r'_ended = true;[^\n]*Texts\.Changed -= OnLanguageChanged')
        self.assertRegex(PAGE, r'if \(_ended\) return; CloseBundledRepairPreview\(\)')

    def test_every_reason_has_both_translations_and_safe_fallback(self):
        entries = {}
        # The built-in catalog intentionally keeps each EN/DE pair on one line.
        for line in CATALOG.splitlines():
            if '("plugins.reselection.' in line:
                values = re.findall(r'"(?:[^"\\]|\\.)*"', line)
                key, en, de = map(json.loads, values)
                self.assertNotIn(key, entries)
                self.assertTrue(en and de)
                entries[key] = (en, de)
        for reason in REASONS:
            self.assertIn('plugins.reselection.blocked.' + reason, entries)
        self.assertIn('_ => Texts.T("plugins.reselection.blocked.unknown")', UI)
        for key in re.findall(r'Texts.T\("(plugins.reselection.[^\"]+)"', UI):
            if not key.endswith('.'):
                self.assertIn(key, entries)

    def test_new_choice_explains_policy_backup_and_has_no_other_mutation(self):
        for key in ('section', 'policy', 'backup', 'configuration', 'acknowledge'):
            self.assertIn('Texts.T("plugins.reselection.' + key + '")', UI)
        self.assertIn('state.pre-v2.json', CATALOG)
        self.assertIn('enabled/disabled choice is preserved', CATALOG)
        for forbidden in ('SetEnabledAsync', 'SelectCandidateAsync', 'SetGrantAsync', 'SaveConfigurationAsync'):
            self.assertNotIn(forbidden, UI)


if __name__ == '__main__':
    unittest.main()
