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
        required = gui.NORMAL + gui.MODES + gui.READER + gui.GIF + gui.GALLERY_ERRORS
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

    def test_all_gallery_leaves_keep_private_audio_and_exact_receipts(self):
        import sys
        from unittest.mock import patch
        calls = []
        entries = [dict(id=n, mode='normal', scenario='normal+' + n, error=None, skip=None,
                       environment=['MEOWCHAN_GALLERY_TEST_ONLY=1', 'MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1',
                                    'MEOWCHAN_VIDEO_FIXTURE={repo}/fixture.webm']) for n in gui.GALLERY_ERRORS]
        def run(command, **kwargs):
            calls.append(command)
            self.assertIn('scripts/with-private-null-audio.py', command)
            self.assertIn('MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1', command)
            self.assertEqual(command[command.index('--timeout') + 1], '300')
            self.assertEqual(kwargs['timeout'], 390)
            output = Path(command[command.index('--output') + 1])
            (output / 'results.json').write_text(json.dumps(dict(passed=True, selected_modes=['normal'],
                results=[dict(mode='normal', scenario='normal+' + output.name, error=None, passed=True, exit_code=0)])))
            (output / 'private-audio.json').write_text(json.dumps(dict(status='passed',
                readiness=dict(before={'ready': True}, after={'ready': True}),
                cleanup=dict(complete=True, processes={'child': {'exit_code': 0}}))))
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as tmp, patch.object(sys, 'platform', 'linux'):
            self.assertEqual(gui.collect(entries, gui.GALLERY_ERRORS, Path('a'), Path(tmp), 180, run=run), 0)
            self.assertEqual(len(calls), 4)
            self.assertTrue(json.loads((Path(tmp) / 'collection.json').read_text())['coverage_complete'])

    def test_split_families_are_exact_and_unsplit_modes_not_in_default(self):
        self.assertEqual(len(gui.GIF), 5)
        self.assertEqual(len(gui.GALLERY_ERRORS), 4)
        inventory = gui.NORMAL + gui.MODES + gui.READER + gui.GIF + gui.GALLERY_ERRORS
        self.assertEqual(len(inventory), 33)
        self.assertNotIn('gif-playback', inventory)
        self.assertNotIn('gallery-error', inventory)
        self.assertNotIn('reader-ux', inventory)

    def test_real_runner_deadline_reaps_owned_process(self):
        import io
        import sys
        import time
        started = time.monotonic()
        with tempfile.TemporaryFile(mode='w+') as log:
            result = gui.bounded_runner([sys.executable, '-c', 'import time; time.sleep(30)'],
                env={}, stdout=log, stderr=-2, timeout=.05)
        self.assertTrue(result.outer_timeout)
        self.assertLess(time.monotonic() - started, 10)
        self.assertIsNotNone(result.returncode)

    def test_missing_gif_leaf_fails_without_hiding_other_leaves(self):
        calls = []
        entries = [self.entry(n) for n in gui.GIF[1:]]
        def run(command, **kwargs):
            calls.append(command)
            output = Path(command[command.index('--output') + 1])
            (output / 'results.json').write_text(json.dumps(dict(passed=True, selected_modes=['reader-ux'],
                results=[dict(mode='reader-ux', scenario='reader-ux+' + output.name, error=None, passed=True, exit_code=0)])))
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(gui.collect(entries, gui.GIF, Path('a'), Path(tmp), 180, run=run), 1)
            self.assertEqual(len(calls), 4)
            report = json.loads((Path(tmp) / 'collection.json').read_text())
            self.assertFalse(report['coverage_complete'])
            self.assertFalse(report['passed'])

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
        required = gui.NORMAL + gui.MODES + gui.READER + gui.GIF + gui.GALLERY_ERRORS
        for workers in (4, 8):
            groups = [self.plan.scenes_for(required, shard, workers) for shard in range(1, workers + 1)]
            self.assertEqual(Counter(n for group in groups for n in group), Counter(required))
            self.assertLessEqual(max(map(len, groups)), 11 if workers == 4 else 5)
            for runner_os in (('Linux', 'Windows') if workers == 4 else ('macOS',)):
                for shard in range(1, workers + 1):
                    report = self.plan.budgets(required, shard, runner_os)
                    self.assertFalse(report['whole_job_worst_case_fits'])
                    self.assertEqual(report['job_timeout_seconds'], 4500)
                    # This is the declared app + offline sum ONLY, not a claim
                    # that build/capture/audio/cleanup/upload fit in the remainder.
                    self.assertLessEqual(report['declared_app_plus_offline_seconds'], (73 if workers == 4 else 56) * 60)

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
        from tests.test_validation_gate import balance, predicate
        for scenario in ('all', 'gui-only'):
            for row in balance.matrix('workflow_dispatch', 'validate', scenario, 'all', 'lane-c', 5, 9)['include']:
                runner_os = 'macOS' if row['mac_shard'] else 'Linux' if row['os'].startswith('ubuntu') else 'Windows'
                required = row['required'].split(',')
                for step in self.plan.PREREQUISITES + ['smoke_normal'] + list(self.plan.DEDICATED):
                    observed = predicate(steps[step]['if'], 'workflow_dispatch', 'validate', scenario,
                                         row['full'], runner_os, 'X64', row=row)
                    self.assertEqual(observed, step in required, (row['os'], row['lane'], scenario, step))
        upload = next(b for b in blocks if b.startswith('Upload regression and graphical evidence'))
        self.assertIn('if: always()', upload)
        self.assertIn('artifacts/desktop-smoke/**', upload)
        gate = next(b for b in blocks if b.startswith('Fail validation after collecting all evidence'))
        self.assertIn('balance-validation.py verify-cell', gate)

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
