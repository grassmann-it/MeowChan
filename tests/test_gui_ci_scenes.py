import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location('gui', Path(__file__).resolve().parents[1] / 'scripts/run-gui-ci-scenes.py')
gui = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gui)


class CollectionTests(unittest.TestCase):
    def entry(self, name):
        return dict(id=name, mode='reader-ux', scenario='reader-ux+' + name,
                    environment=['MEOWCHAN_' + name.upper() + '_ONLY=1'], error=None, skip=None)

    def test_collects_after_failure_and_missing_evidence(self):
        calls = []
        def run(command, env):
            calls.append(command)
            destination = Path(command[command.index('--output') + 1])
            destination.mkdir()
            if destination.name != 'b':
                (destination / 'results.json').write_text(json.dumps(dict(
                    passed=True, selected_modes=['reader-ux'],
                    results=[dict(scenario='reader-ux+' + destination.name)])))
            return SimpleNamespace(returncode=1 if destination.name == 'a' else 0)
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            self.assertEqual(gui.collect([self.entry(n) for n in 'abc'], list('abc'),
                                        Path('smoke.dll'), output, 180, run=run), 1)
            report = json.loads((output / 'collection.json').read_text())
            self.assertEqual(len(calls), 3)
            self.assertEqual([r['passed'] for r in report['results']], [False, False, True])
            self.assertTrue(report['complete'])
            self.assertFalse(report['passed'])
            self.assertEqual(len({c[c.index('--output') + 1] for c in calls}), 3)

    def test_success_native_flags_and_selector_isolation(self):
        import os
        from unittest.mock import patch
        def run(command, env):
            self.assertIn('--allow-native-desktop', command)
            self.assertNotIn('MEOWCHAN_STALE_ONLY', env)
            self.assertEqual(env['MEOWCHAN_A_ONLY'], '1')
            self.assertEqual(command[command.index('--timeout') + 1], '300')
            destination = Path(command[command.index('--output') + 1])
            destination.mkdir()
            (destination / 'results.json').write_text(json.dumps(dict(
                passed=True, selected_modes=['reader-ux'], results=[dict(scenario='reader-ux+a')])))
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as tmp, patch.dict(os.environ, {'MEOWCHAN_STALE_ONLY': '1'}):
            self.assertEqual(gui.collect([self.entry('a')], ['a'], Path('smoke.dll'),
                                        Path(tmp), 300, native=True, run=run), 0)
            with self.assertRaises(ValueError):
                gui.collect([], [], Path('smoke.dll'), Path(tmp), 300)

    def test_missing_duplicate_and_skipped_entries_fail_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            e = self.entry('a')
            self.assertEqual(gui.collect([e, e], ['a', 'missing'], Path('a'), Path(tmp), 180,
                                        run=lambda *a, **kw: self.fail('must not invoke')), 1)

    def test_inventory_preserves_baseline_and_every_reader_alternative(self):
        import re
        source = (Path(__file__).resolve().parents[1] / 'tests/MeowChan.DesktopSmoke/SmokeScenarios.cs').read_text()
        alternatives = re.findall(r'Alternative\("([^"]+)", "[^"]+", "--reader-ux"\)', source)
        self.assertEqual(set(gui.READER), set(alternatives))
        required = gui.NORMAL + gui.MODES + gui.READER
        self.assertEqual(len(required), len(set(required)))
        self.assertIn('reader-ux', required)
        self.assertEqual(len(gui.NORMAL), 9)


if __name__ == '__main__':
    unittest.main()
