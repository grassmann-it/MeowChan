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
NEW = UI[UI.index("foreach (BundledCandidateReselection row"):UI.index("Button cancel =")]
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
        self.assertIn('IsChecked = false, IsEnabled = row.CanConfirm', NEW)
        self.assertIn('Content = Texts.T("plugins.reselection.confirm"), IsEnabled = false', NEW)
        self.assertIn('confirm.IsEnabled = acknowledgement.IsChecked == true && row.CanConfirm', NEW)
        self.assertIn('row.CanConfirm && acknowledgement.IsChecked == true) window.Close(choice)', NEW)

    def test_both_paths_submit_their_own_frozen_row_and_revision(self):
        self.assertIn('new ProvenRepairChoice(row, preview.ProfileRevision)', UI)
        self.assertIn('new NewSelectionChoice(row, selections.ProfileRevision)', NEW)
        self.assertIn('ConfirmBundledRepairAsync(repair.Row, repair.Revision, token)', UI)
        self.assertIn('ConfirmBundledReselectionAsync(selection.Row, selection.Revision, token)', UI)
        self.assertIn('window.ShowDialog<object?>(owner)', UI)

    def test_cancel_focus_staleness_visibility_language_and_deactivation_guards(self):
        self.assertIn('Current(generation) && window.IsActive && IsEffectivelyVisible && Texts.Language == language', NEW)
        self.assertIn('chosen is null || !Current(generation) || !IsEffectivelyVisible || Texts.Language != language', UI)
        self.assertIn('!CommandRouter.IsRoutingContextActive(this, this)) return;', UI)
        self.assertIn('cancel.Click += (_, _) => window.Close()', UI)
        self.assertIn('args.Key == Key.Escape', UI)
        self.assertIn('window.Opened += (_, _) => cancel.Focus()', UI)
        owner = (ROOT / 'Desktop/Views/Plugins/PluginsPage.axaml.cs').read_text()
        self.assertRegex(owner, r'OnLanguageChanged[^\n]*CloseBundledRepairPreview\(\)')
        self.assertRegex(owner, r'internal void Deactivate\(\)[^\n]*CloseBundledRepairPreview\(\)')

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
