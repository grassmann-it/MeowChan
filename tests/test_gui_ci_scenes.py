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

    def test_collects_after_ordinary_failure(self):
        calls = []
        def run(command, env, **kwargs):
            calls.append(command)
            destination = Path(command[command.index('--output') + 1])
            destination.mkdir(exist_ok=True)
            (destination / 'results.json').write_text(json.dumps(dict(
                passed=True, selected_modes=['reader-ux'],
                results=[dict(mode='reader-ux', error=None, exit_code=0,
                              scenario='reader-ux+' + destination.name, passed=True)])))
            return SimpleNamespace(returncode=1 if destination.name == 'a' else 0)
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            self.assertEqual(gui.collect([self.entry(n) for n in 'abc'], list('abc'),
                                        Path('smoke.dll'), output, 180, run=run), 1)
            report = json.loads((output / 'collection.json').read_text())
            self.assertEqual(len(calls), 3)
            self.assertEqual([r['passed'] for r in report['results']], [False, True, True])
            self.assertTrue(report['complete'])
            self.assertFalse(report['passed'])
            self.assertEqual(len({c[c.index('--output') + 1] for c in calls}), 3)

    def test_success_native_flags_and_selector_isolation(self):
        import os
        from unittest.mock import patch
        def run(command, env, **kwargs):
            self.assertIn('--allow-native-desktop', command)
            self.assertNotIn('MEOWCHAN_STALE_ONLY', env)
            self.assertNotIn('MEOWCHAN_GALLERY_TRANSITION_REVIEW', env)
            self.assertEqual(kwargs['timeout'], 345)
            self.assertEqual(env['MEOWCHAN_A_ONLY'], '1')
            self.assertEqual(command[command.index('--timeout') + 1], '300')
            destination = Path(command[command.index('--output') + 1])
            destination.mkdir(exist_ok=True)
            (destination / 'results.json').write_text(json.dumps(dict(
                passed=True, selected_modes=['reader-ux'], results=[dict(mode='reader-ux', error=None, exit_code=0, scenario='reader-ux+a', passed=True)])))
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as tmp, patch.dict(os.environ, {'MEOWCHAN_STALE_ONLY': '1', 'MEOWCHAN_GALLERY_TRANSITION_REVIEW': 'observe'}):
            self.assertEqual(gui.collect([self.entry('a')], ['a'], Path('smoke.dll'),
                                        Path(tmp), 300, native=True, run=run), 0)
            with self.assertRaises(ValueError):
                gui.collect([], [], Path('smoke.dll'), Path(tmp), 300)

    def test_missing_duplicate_and_skipped_entries_fail_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            e = self.entry('a')
            self.assertEqual(gui.collect([e, e], ['a', 'missing'], Path('a'), Path(tmp), 180,
                                        run=lambda *a, **kw: self.fail('must not invoke')), 1)

    def test_reader_baseline_atomic_transition_preserves_old_coverage(self):
        with self.assertRaises(ValueError):
            gui.reader_baseline([{'id': 'reader-ux'}])
        entries = [{'id': n} for n in gui.READER]
        self.assertEqual(gui.reader_baseline(entries), gui.READER)
        with self.assertRaises(ValueError):
            gui.reader_baseline([{'id': 'reader-owned-navigation'}, {'id': 'reader-ux'}])
        required = gui.NORMAL + gui.MODES + gui.READER
        self.assertEqual(len(required), len(set(required)))
        self.assertNotIn('reader-ux', required)
        self.assertEqual(len(gui.NORMAL), 9)

    def test_missing_receipt_quarantines_but_prelaunch_failure_continues(self):
        for prelaunch in (False, True):
            calls = []
            def run(command, **kwargs):
                calls.append(command)
                if prelaunch:
                    raise gui.RunnerNotStarted('fixture launch error')
                return SimpleNamespace(returncode=1)
            with tempfile.TemporaryDirectory() as tmp:
                output = Path(tmp)
                self.assertEqual(gui.collect([self.entry(n) for n in 'ab'], list('ab'),
                    Path('a'), output, 180, run=run), 1)
                self.assertEqual(len(calls), 2 if prelaunch else 1)
                self.assertEqual((output / 'unsafe-cleanup').exists(), not prelaunch)
                self.assertFalse(json.loads((output / 'collection.json').read_text())['passed'])

    def test_outer_deadline_has_bounded_grace_and_quarantines(self):
        import io
        import subprocess
        from unittest.mock import Mock, patch
        process = Mock(pid=123, returncode=-15)
        process.wait.side_effect = [subprocess.TimeoutExpired(['runner'], 1), None]
        with patch.object(gui.subprocess, 'Popen', return_value=process):
            result = gui.bounded_runner(['runner'], env={}, stdout=io.StringIO(), stderr=-2, timeout=1)
        self.assertTrue(result.outer_timeout)
        process.terminate.assert_called_once()
        self.assertEqual(process.wait.call_args_list[-1].kwargs['timeout'], 20)
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            return result
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(gui.collect([self.entry(n) for n in 'ab'], list('ab'),
                Path('a'), Path(tmp), 180, run=run), 1)
            self.assertEqual(len(calls), 1)
            self.assertTrue((Path(tmp) / 'unsafe-cleanup').exists())

    def test_discovery_failure_retains_report_and_manifest(self):
        import sys
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as tmp, patch.object(sys, 'argv', ['runner', '--assembly',
                'absent.dll', '--output', tmp, '--timeout', '300']), patch('subprocess.run', side_effect=OSError('fixture')):
            self.assertEqual(gui.main(), 1)
            self.assertTrue((Path(tmp) / 'scene-shard.json').exists())
            report = json.loads((Path(tmp) / 'collection.json').read_text())
            self.assertFalse(report['complete'])
            self.assertFalse(report['passed'])




class NativeShardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[1]
        spec = importlib.util.spec_from_file_location('plan', cls.root / 'scripts/gui-ci-plan.py')
        cls.plan = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.plan)

    def test_required_scenes_exactly_once_and_bounded(self):
        from collections import Counter
        required = gui.NORMAL + gui.MODES + gui.READER
        for workers in (4, 8):
            groups = [self.plan.scenes_for(required, shard, workers) for shard in range(1, workers + 1)]
            self.assertEqual(Counter(n for group in groups for n in group), Counter(required))
            self.assertLessEqual(max(map(len, groups)), 7 if workers == 4 else 4)
            for shard, group in enumerate(groups, 1):
                dedicated = [n for n, owner in self.plan.owners(workers).items() if owner == shard]
                seconds = len(group) * (180 if workers == 4 else 300) + sum(
                    240 if n == 'media_input' else 180 if n == 'tray_lifecycle' else 300 for n in dedicated)
                # Existing offline step deadlines are unchanged. Reserve >=10min
                # beyond their sum with GUI timeouts, not a whole-job guarantee.
                self.assertLessEqual(seconds + (35 if workers == 4 else 22) * 60, 65 * 60)

    def test_workflow_owners_and_gates_match_for_every_worker(self):
        import re
        source = (self.root / '.github/workflows/dotnet-desktop.yml').read_text()
        blocks = re.split(r'(?m)^      - name: ', source)
        steps = {}
        for block in blocks:
            identifier = re.search(r'(?m)^        id: (\w+)$', block)
            condition = re.search(r'(?m)^        if: (.+)$', block)
            if identifier and condition:
                steps[identifier[1]] = {'if': condition[1]}
        from test_validation_gate import predicate
        for runner_os, workers in (('macOS', 8), ('Linux', 4), ('Windows', 4)):
            for shard in range(1, workers + 1):
                lane = ('native' if shard == 1 else f'native-{shard:02}') if workers == 8 else 'lane-' + chr(96 + shard)
                for scenario in ('all', 'gui-only'):
                    required = self.plan.overlay([], shard, runner_os, scenario)
                    for step in self.plan.PREREQUISITES + ['smoke_normal'] + list(self.plan.DEDICATED):
                        observed = predicate(steps[step]['if'], 'workflow_dispatch', 'validate', scenario,
                                             shard == 1, runner_os, 'X64', lane, shard)
                        self.assertEqual(observed, step in required, (runner_os, shard, scenario, step))
        upload = next(b for b in blocks if b.startswith('Upload regression and graphical evidence'))
        self.assertIn('if: always()', upload)
        self.assertIn('artifacts/desktop-smoke/**', upload)
        gate = next(b for b in blocks if b.startswith('Fail validation after collecting all evidence'))
        self.assertIn('gui-ci-plan.py', gate)

    def test_cleanup_failure_quarantines_remainder_with_evidence(self):
        calls = []
        def run(command, env, **kwargs):
            calls.append(command)
            output = Path(command[command.index('--output') + 1])
            (output / 'results.json').write_text(json.dumps(dict(passed=False,
                selected_modes=['reader-ux'], results=[dict(mode='reader-ux', passed=False, exit_code=1, error='Process cleanup failed: Operation not permitted')])) )
            return SimpleNamespace(returncode=1)
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            entries = [CollectionTests().entry(n) for n in 'ab']
            self.assertEqual(gui.collect(entries, list('ab'), Path('a'), output, 300, run=run), 1)
            self.assertEqual(len(calls), 1)
            self.assertTrue((output / 'unsafe-cleanup').exists())
            self.assertTrue((output / 'a/runner.log').exists())
            report = json.loads((output / 'collection.json').read_text())
            self.assertIn('desktop unsafe', report['results'][1]['error'])
            self.assertFalse(report['passed'])

    def test_cleanup_preflight_distinguishes_product_failures(self):
        spec = importlib.util.spec_from_file_location('safety', self.root / 'scripts/gui-ci-safety.py')
        safety = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(safety)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            result = root / 'results.json'
            result.write_text(json.dumps({'results': [{'error': 'ordinary assertion failure'}]}))
            self.assertEqual(safety.cleanup_failures(root), [])
            result.write_text(json.dumps({'results': [{'error': 'Process cleanup failed: EPERM'}]}))
            self.assertEqual(safety.cleanup_failures(root), [str(result)])
            result.unlink()
            (root / 'unsafe-cleanup').write_text('missing receipt')
            self.assertEqual(safety.cleanup_failures(root), [str(root / 'unsafe-cleanup')])


if __name__ == '__main__':
    unittest.main()
