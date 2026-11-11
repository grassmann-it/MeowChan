import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/run-stability-live.py'
SPEC = importlib.util.spec_from_file_location('run_stability_live', SCRIPT)
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)

CONTRACT_COMMIT = '0123456789abcdef0123456789abcdef01234567'

# Stands in for `dotnet vstest`: records how it was called and plays the three phases without any test assembly.
FAKE_DOTNET = r'''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
P = 'MEOWCHAN_STABILITY_RUN_'
mode = os.environ[P + 'MODE']
log = Path(os.environ['FAKE_DOTNET_LOG'])
with log.open('a') as out:
    out.write(json.dumps({'mode': mode, 'args': sys.argv[1:], 'network': os.environ.get(P + 'NETWORK'),
                          'live': os.environ.get(P + 'LIVE'), 'wayland': os.environ.get('WAYLAND_DISPLAY'),
                          'only': os.environ.get(P + 'ONLY'), 'pid': os.getpid()}) + '\n')
evidence = Path(os.environ[P + 'EVIDENCE']); work = Path(os.environ[P + 'WORK'])
results = Path(next(a for a in sys.argv if a.startswith('/ResultsDirectory:')).split(':', 1)[1])
logger = next(a for a in sys.argv if a.startswith('/Logger:'))
name = logger.split('LogFileName=')[1]
passed = 0 if os.environ.get('FAKE_SKIP') == mode else 1
results.mkdir(parents=True, exist_ok=True)
(results / name).write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="1" passed="%d"/></ResultSummary></TestRun>' % passed)
if os.environ.get('FAKE_FAIL') == mode:
    sys.exit(1)
work.mkdir(parents=True, exist_ok=True)
(work / 'profile-lainchan').mkdir(exist_ok=True)
if mode == 'plan':
    (evidence / 'plan.txt').write_text('lainchan\n  GET https://lainchan.org/sec/catalog.json\nDry run: no request was sent.\n')
elif mode == 'online':
    (evidence / 'phase1.json').write_text('{}')
else:
    repo = Path(os.environ[P + 'REPO'])
    target = repo / 'plugins/Examples/Lainchan/Evidence/stability-20261111.json'
    target.parent.mkdir(parents=True, exist_ok=True)
    document = json.loads(os.environ['FAKE_EVIDENCE'])
    document['contract'] = os.environ[P + 'CONTRACT']
    target.write_text(json.dumps(document))
    (evidence / 'phase2.json').write_text(json.dumps({'chans': [{'sourceId': 'lainchan', 'evidenceFile': str(target), 'problems': [],
                                                               'profileDeleted': True}]}))
'''

FAKE_PACKAGE = '#!/usr/bin/env python3\nimport sys\nsys.exit(0)\n'


def valid_evidence(outcome='pass'):
    return {
        'kind': 'source-stability-run', 'contract': 'docs/plugins/source-stability.md@' + CONTRACT_COMMIT,
        'pluginId': 'meowchan.source.lainchan', 'sourceId': 'lainchan', 'executionRevision': 'r', 'assemblySha256': 'a',
        'descriptionSha256': 'd', 'package': 'MeowChan.Plugin.Lainchan.plugin.json sha256:x', 'approvedByUser': '2026-11-11, chat',
        'windowUtc': ['2026-11-11T10:00:00Z', '2026-11-11T10:01:00Z'], 'integrityExceptionUsed': False, 'failuresObserved': [],
        'receipts': [{'path': 'audit-lainchan.json', 'sha256': 'x'}], 'outcome': outcome,
        'boards': [{'board': 'sec', 'thread': '1', 'requests': [{'url': 'https://lainchan.org/sec/catalog.json', 'status': 200, 'bytes': 1}],
                    'original': {'expectedBytes': 1, 'receivedBytes': 1, 'providerHash': 'md5:x', 'hashMatched': True, 'sha256': 'x'},
                    'archive': 'pass', 'reader': 'pass', 'offlineAfterRestart': 'pass'}],
    }


class DriverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=SCRIPT.parents[1] / 'artifacts' if (SCRIPT.parents[1] / 'artifacts').is_dir() else None)
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.dotnet = self.write_executable('dotnet', FAKE_DOTNET)
        self.package = self.write_executable('package-plugins.py', FAKE_PACKAGE)
        self.assembly = self.root / 'bin' / 'MeowChan.DesktopTests.dll'
        self.assembly.parent.mkdir()
        self.assembly.write_bytes(b'x')
        self.log = self.root / 'calls.jsonl'
        self.repo = self.root / 'repo'
        (self.repo / 'plugins/Examples/Lainchan').mkdir(parents=True)
        self.manifest = self.root / 'run.json'
        self.manifest.write_text(json.dumps({'schemaVersion': 1, 'sources': [
            {'pluginId': 'meowchan.source.lainchan', 'sourceId': 'lainchan', 'approval': '2026-11-11, chat', 'boards': [{'board': 'sec'}]},
            {'pluginId': 'meowchan.source.kohlchan', 'sourceId': 'kohlchan', 'approval': '2026-11-11, chat', 'boards': [{'board': 'b'}]}]}))
        os.environ['FAKE_DOTNET_LOG'] = str(self.log)
        os.environ['FAKE_EVIDENCE'] = json.dumps(valid_evidence())
        for name in ('FAKE_SKIP', 'FAKE_FAIL'):
            os.environ.pop(name, None)
        self.addCleanup(lambda: [os.environ.pop(name, None) for name in ('FAKE_DOTNET_LOG', 'FAKE_EVIDENCE', 'FAKE_SKIP', 'FAKE_FAIL')])
        os.environ['WAYLAND_DISPLAY'] = 'wayland-0'
        self.addCleanup(os.environ.pop, 'WAYLAND_DISPLAY', None)

    def write_executable(self, name, text):
        path = self.root / name
        path.write_text(text)
        path.chmod(path.stat().st_mode | stat.S_IXUSR)
        return path

    def run_driver(self, *extra, manifest=True):
        argv = ['--dotnet', str(self.dotnet), '--test-assembly', str(self.assembly), '--package-script', str(self.package),
                '--artifacts-dir', str(self.root / 'artifacts'), '--evidence-repo', str(self.repo), '--no-xvfb', '--net-block', 'off',
                '--contract-commit', CONTRACT_COMMIT]
        if manifest:
            argv += ['--manifest', str(self.manifest)]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = DRIVER.main(argv + list(extra))
            except SystemExit as stop:
                code = stop.code
        return code, out.getvalue(), err.getvalue()

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_default_is_a_dry_run_that_only_plans(self):
        code, out, _ = self.run_driver()
        self.assertEqual(0, code)
        self.assertEqual(['plan'], [call['mode'] for call in self.calls()])
        call = self.calls()[0]
        self.assertIsNone(call['network'])
        self.assertIsNone(call['live'])
        self.assertIsNone(call['wayland'])
        self.assertIn('GET https://lainchan.org/sec/catalog.json', out)
        self.assertIn('add --live', out)
        self.assertFalse(list((self.repo / 'plugins').rglob('*.json')))
        self.assertFalse(list((self.root / 'artifacts').rglob('work')))

    def test_live_runs_plan_online_offline_in_that_order_in_different_processes(self):
        code, out, err = self.run_driver('--live')
        self.assertEqual(0, code, err + out)
        calls = self.calls()
        self.assertEqual(['plan', 'online', 'offline'], [call['mode'] for call in calls])
        self.assertEqual(3, len({call['pid'] for call in calls}))
        by_mode = {call['mode']: call for call in calls}
        self.assertEqual(('live', '1'), (by_mode['online']['network'], by_mode['online']['live']))
        for mode in ('plan', 'offline'):
            self.assertIsNone(by_mode[mode]['network'], mode)
            self.assertIsNone(by_mode[mode]['live'], mode)
        self.assertTrue(all('/TestCaseFilter:FullyQualifiedName=' + DRIVER.TEST_NAME in call['args'] for call in calls))
        self.assertIn('lainchan: pass', out)
        self.assertTrue((self.repo / 'plugins/Examples/Lainchan/Evidence/stability-20261111.json').is_file())
        self.assertFalse(list((self.root / 'artifacts').rglob('work')), 'the work directory holds profiles and media and must be deleted')

    def test_inherited_stability_variables_never_enable_a_dry_run(self):
        os.environ['MEOWCHAN_STABILITY_RUN_LIVE'] = '1'
        os.environ['MEOWCHAN_STABILITY_RUN_NETWORK'] = 'live'
        self.addCleanup(os.environ.pop, 'MEOWCHAN_STABILITY_RUN_LIVE', None)
        self.addCleanup(os.environ.pop, 'MEOWCHAN_STABILITY_RUN_NETWORK', None)
        self.assertEqual(0, self.run_driver()[0])
        call = self.calls()[0]
        self.assertIsNone(call['live'])
        self.assertIsNone(call['network'])

    def test_only_narrows_and_unknown_chan_is_refused_before_any_process_starts(self):
        self.assertEqual(0, self.run_driver('--only', 'lainchan')[0])
        self.assertEqual('lainchan', self.calls()[0]['only'])
        self.log.unlink()
        code, _, err = self.run_driver('--only', 'sushichan')
        self.assertEqual(DRIVER.EXIT_USAGE, code)
        self.assertIn('unknown: sushichan', err)
        self.assertEqual([], self.calls())

    def test_live_and_selftest_exclude_each_other_and_manifest_is_required(self):
        self.assertEqual(2, self.run_driver('--live', '--selftest')[0])
        self.assertEqual(2, self.run_driver(manifest=False)[0])
        self.assertEqual([], self.calls())

    def test_failing_chan_outcome_is_exit_one_with_evidence(self):
        os.environ['FAKE_EVIDENCE'] = json.dumps(valid_evidence('fail'))
        code, out, _ = self.run_driver('--live')
        self.assertEqual(DRIVER.EXIT_FAIL, code)
        self.assertIn('lainchan: fail', out)

    def test_malformed_evidence_is_exit_one(self):
        document = valid_evidence()
        del document['receipts']
        document['contract'] = 'docs/plugins/source-stability.md'
        os.environ['FAKE_EVIDENCE'] = json.dumps(document)
        code, out, _ = self.run_driver('--live')
        self.assertEqual(DRIVER.EXIT_FAIL, code)
        self.assertIn('receipts missing', out)

    def test_skipped_phase_test_is_an_infrastructure_error_and_work_is_still_deleted(self):
        os.environ['FAKE_SKIP'] = 'online'
        code, _, err = self.run_driver('--live')
        self.assertEqual(DRIVER.EXIT_INFRA, code)
        self.assertIn('did not pass', err)
        self.assertEqual(['plan', 'online'], [call['mode'] for call in self.calls()])
        self.assertFalse(list((self.root / 'artifacts').rglob('work')))

    def test_failed_phase_process_stops_the_run(self):
        os.environ['FAKE_FAIL'] = 'plan'
        code, _, err = self.run_driver('--live')
        self.assertEqual(DRIVER.EXIT_INFRA, code)
        self.assertEqual(['plan'], [call['mode'] for call in self.calls()])

    def test_uncommitted_contract_aborts(self):
        with self.assertRaises(DRIVER.DriverError):
            DRIVER.contract_reference(self.root, None)

    def test_offline_command_is_wrapped_in_a_network_namespace_and_others_are_not(self):
        args = DRIVER.argparse.Namespace(xvfb='/usr/bin/xvfb-run', bwrap='/usr/bin/bwrap', dotnet='dotnet', test_assembly=self.assembly)
        offline = DRIVER.phase_command(args, 'offline', self.root, True)
        self.assertEqual(['/usr/bin/bwrap', '--die-with-parent', '--unshare-net'], offline[:3])
        self.assertNotIn('--unshare-net', DRIVER.phase_command(args, 'online', self.root, False))

    def test_phase_environment_refuses_an_online_phase_without_a_send_switch(self):
        args = DRIVER.argparse.Namespace(selftest=False, live=False, only=None, configuration='Debug', evidence_repo=self.repo)
        with self.assertRaises(DRIVER.DriverError):
            DRIVER.phase_environment(args, 'online', self.manifest, self.root, self.root, 'c', None)
        self.assertNotIn('MEOWCHAN_STABILITY_RUN_LIVE', DRIVER.phase_environment(args, 'plan', self.manifest, self.root, self.root, 'c', None))

    def test_validate_evidence_accepts_the_contract_shape(self):
        self.assertEqual([], DRIVER.validate_evidence(valid_evidence()))


if __name__ == '__main__':
    unittest.main()
