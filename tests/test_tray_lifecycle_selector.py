"""Focused native tray retry must never pull in unrelated GUI/offline coverage."""
import re
import unittest
from tests import test_dotnet_desktop_workflow_contract as matrix_contract
from tests.test_validation_gate import compute, aggregate, outcomes, predicate, workflow_steps, WORKFLOW


class TrayLifecycleSelectorTests(unittest.TestCase):
    def test_both_native_mac_platforms_select_only_tray_with_prerequisites(self):
        matrix = matrix_contract.DotnetDesktopWorkflowContractTests()
        steps = workflow_steps()
        expected = {'record_source', 'prepare_desktop', 'smoke_restore', 'smoke_assets',
                    'smoke_build', 'plugin_delivery', 'tray_lifecycle'}
        # Include all prerequisite/build/offline/GUI checks with explicit step IDs;
        # setup-dotnet is unconditional and evidence/gate are checked separately.
        unrelated = {'core_tests', 'mac_tests', 'build_all', 'package_fixtures', 'public_api',
                     'dev_build', 'restore_release', 'retain_release_assets', 'local_package',
                     'smoke_runner_contract', 'process_probe', 'smoke_normal', 'smoke_plugins',
                     'smoke_1_1_registry', 'upgrade_replay', 'upgrade_102_replay', 'first_run',
                     'preference_startup', 'scoped_diagnostics', 'gif_playback', 'gallery_errors',
                     'reader_media', 'gallery_features', 'media_input', 'tray_host'}
        for platform, arch in [('macos-15', 'ARM64'), ('macos-15-intel', 'X64')]:
            rows = matrix.matrix(platform=platform, scenario='tray-lifecycle')
            self.assertEqual([dict(os=platform, lane='native', full=True, mac_shard=1)], rows)
            selected = {name for name in expected | unrelated
                        if predicate(steps[name], 'workflow_dispatch', 'validate', 'tray-lifecycle',
                                     True, 'macOS', arch, lane='native', shard=1)}
            self.assertEqual(expected, selected)
            required = compute.required_steps('workflow_dispatch', 'validate', 'tray-lifecycle', 'native', 'macOS', arch)
            self.assertEqual(expected, set(required))
            self.assertTrue(aggregate.step_gate(outcomes(required), required)['passed'])
            for step in expected:
                with self.subTest(platform=platform, missing=step):
                    for status in ('skipped', 'failure', 'cancelled'):
                        observed = outcomes(required)
                        observed[step] = dict(outcome=status, conclusion='success')
                        self.assertFalse(aggregate.step_gate(observed, required)['passed'])
                    observed = outcomes(required); observed.pop(step)
                    self.assertFalse(aggregate.step_gate(observed, required)['passed'])

    def test_existing_tray_budget_cycles_and_always_evidence_remain(self):
        text = WORKFLOW.read_text()
        tray = text.split('        id: tray_lifecycle', 1)[1].split('      - name:', 1)[0]
        self.assertIn('--timeout 180 --mode tray-lifecycle', tray)
        self.assertIn('scripts/gui-ci-safety.py', tray)
        self.assertIn('continue-on-error: true', tray)
        self.assertIn('artifacts/desktop-tray-stress/**', text)
        upload = text.split('      - name: Upload regression and graphical evidence', 1)[1]
        self.assertTrue(upload.lstrip().startswith('if: always()'))
        gate = text.split('scripts/aggregate-release-gates.py steps', 1)[1]
        self.assertIn('--required "$required_csv"', gate)
        # Workflow does not override the existing 16-cycle harness contract.
        from pathlib import Path
        root = Path(__file__).resolve().parents[1]
        source = (root/'tests/MeowChan.DesktopSmoke/TrayLifecycleChecks.cs').read_text()
        self.assertRegex(source, r'iteration\s*<\s*16')


if __name__ == '__main__': unittest.main()
