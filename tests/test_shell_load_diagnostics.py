"""PF00 Python-only subprocess/watchdog/provenance fixtures. Never launches .NET or a GUI."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


watchdog = module('shell_watchdog', 'shell-load-watchdog.py')
matrix = module('shell_matrix', 'run-shell-load-matrix.py')
runner = module('shell_runner', 'run-desktop-smoke.py')


class ShellLoadDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='pf00 diagnostics ')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.heartbeat = self.root / 'heartbeat.json'
        self.report = self.root / 'watchdog.json'

    def run_child(self, script, **kwargs):
        return watchdog.supervise([sys.executable, '-c', script, str(self.heartbeat)], self.heartbeat, self.report,
                                  timeout=4, startup=.6, silence=.4, trigger=.15, **kwargs)

    def test_healthy_answers_and_early_exit(self):
        script = '''import json,os,sys,time
from pathlib import Path
p=Path(sys.argv[1])
for i in range(6):
 t=p.with_suffix('.tmp');t.write_text(json.dumps({'pid':os.getpid(),'uiSequence':i+1}));t.replace(p);time.sleep(.05)
'''
        self.assertEqual(0, self.run_child(script))
        self.assertEqual('exited', json.loads(self.report.read_text())['reason'])
        self.heartbeat.unlink()
        self.assertEqual(1, self.run_child('raise SystemExit(1)'))
        self.assertIn('possibly reused PID', (self.root / 'watchdog-failure-threads.json').read_text())

    def test_writer_cannot_hide_stalled_ui_with_fresh_file_timestamps(self):
        script = '''import json,os,sys,time
from pathlib import Path
p=Path(sys.argv[1])
while True:
 t=p.with_suffix('.tmp');t.write_text(json.dumps({'pid':os.getpid(),'uiSequence':1}));t.replace(p);time.sleep(.03)
'''
        self.assertEqual(124, self.run_child(script))
        report = json.loads(self.report.read_text())
        self.assertEqual('ui-silence', report['reason'])
        self.assertTrue(report['thread_evidence_captured'])
        self.assertTrue((self.root / 'watchdog-failure-threads.json').exists())

    def test_startup_silence_and_forged_pid_fail_closed(self):
        self.assertEqual(124, self.run_child('import time;time.sleep(30)'))
        self.assertEqual('startup-silence', json.loads(self.report.read_text())['reason'])
        self.assertEqual(125, self.run_child("import sys,time,json;from pathlib import Path;Path(sys.argv[1]).write_text(json.dumps({'pid':1,'uiSequence':1}));time.sleep(30)"))
        self.assertEqual('invalid-heartbeat', json.loads(self.report.read_text())['reason'])

    def test_malformed_and_oversized_heartbeat_are_bounded_failures(self):
        for body in ('not-json', 'x' * 65537):
            self.heartbeat.unlink(missing_ok=True)
            script = f'import sys,time;from pathlib import Path;Path(sys.argv[1]).write_text({body!r});time.sleep(30)'
            self.assertEqual(125, self.run_child(script))

    @unittest.skipIf(os.name == 'nt', 'POSIX executable fixture')
    def test_managed_stack_output_is_bounded_and_tool_pid_is_owned(self):
        tool = self.root / 'managed-tool'
        tool.write_text('#!/usr/bin/env python3\nimport sys\nassert sys.argv[1:3]==["report","--process-id"]\nprint("STACK " + sys.argv[3])\nprint("x"*400000)\n')
        tool.chmod(0o700)
        destination = self.root / 'managed.txt'
        result = watchdog.managed_stacks(12345, destination, str(tool))
        self.assertEqual(0, result['exit_code'])
        self.assertTrue(result['truncated'])
        self.assertLessEqual(destination.stat().st_size, 262144)
        self.assertTrue(destination.read_text().startswith('STACK 12345'))
        self.assertIn('absolute executable', watchdog.managed_stacks(1, destination, 'relative')['error'])

    @unittest.skipIf(os.name == 'nt', 'POSIX process groups')
    def test_kills_only_owned_child_group(self):
        unrelated = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(30)'], start_new_session=True)
        try:
            self.assertEqual(124, self.run_child('import subprocess,sys,time;subprocess.Popen([sys.executable,"-c","import time;time.sleep(30)"]);time.sleep(30)'))
            self.assertIsNone(unrelated.poll())
            owned = json.loads(self.report.read_text())['child_pid']
            with self.assertRaises(ProcessLookupError):
                os.kill(owned, 0)
        finally:
            os.killpg(unrelated.pid, signal.SIGKILL)
            unrelated.wait(timeout=5)

    def test_shell_runner_wraps_only_owned_child_and_binds_output(self):
        assembly = self.root / 'fixture.dll'
        assembly.write_bytes(b'fixture-binary')
        destination = self.root / 'cell'
        def launch(command, **kwargs):
            self.assertIn('shell-load-watchdog.py', ' '.join(command))
            self.assertEqual(str(destination / 'heartbeat.json'), kwargs['env']['MEOWCHAN_SHELL_LOAD_HEARTBEAT'])
            self.assertTrue(Path(kwargs['env']['MEOWCHAN_SHELL_LOAD_PRIVATE_PROFILE']).is_relative_to(kwargs['cwd']))
            kwargs['stdout'].write('SMOKE PASS: fixture\n')
            (destination / 'watchdog.json').write_text(json.dumps({'reason': 'exited', 'exit_code': 0}))
            (destination / 'measurement.json').write_text(json.dumps({'failure': None}))
            child = mock.Mock()
            child.wait.return_value = 0
            return child
        with mock.patch.object(runner.subprocess, 'Popen', side_effect=launch), mock.patch.object(runner, 'stop_owned_processes'):
            result = runner.run_mode('shell-load', assembly, destination, {}, False, 60)
        self.assertTrue(result['passed'])
        binding = json.loads((destination / 'binding.json').read_text())
        self.assertEqual(matrix.digest(assembly), binding['assembly_sha256'])
        self.assertEqual('exited', result['watchdog']['reason'])

    def test_matrix_dry_run_binds_provenance_without_launching_runtime(self):
        arguments = ['--output', str(self.root / 'matrix'), '--baseline-description', 'release + measurement injection', '--dry-run']
        for name in ('baseline', 'candidate'):
            root = self.root / name
            for relative in matrix.HARNESS:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('identical harness source')
            assembly = root / 'bin' / 'fixture.dll'
            assembly.parent.mkdir()
            assembly.write_bytes(name.encode())
            arguments.extend(['--' + name + '-root', str(root), '--' + name + '-assembly', str(assembly)])
        with mock.patch.object(matrix.subprocess, 'Popen') as launch:
            self.assertEqual(0, matrix.main(arguments))
            launch.assert_not_called()
        manifest = json.loads((self.root / 'matrix' / 'manifest.json').read_text())
        self.assertEqual('release + measurement injection', manifest['baseline_description'])
        self.assertEqual(30, len(manifest['sequence']))
        self.assertNotEqual(manifest['variants']['baseline']['assembly_sha256'], manifest['variants']['candidate']['assembly_sha256'])

    def test_resource_summary_uses_counter_deltas_and_phase_time_bounds(self):
        def sample(tick, cpu, allocated):
            return dict(Timestamp=tick, CpuMilliseconds=cpu, AllocatedBytes=allocated, Gen0=tick, Gen1=0, Gen2=0,
                        WorkingSetBytes=100+tick, PrivateBytes=200, HeapBytes=50, OsThreads=4, Handles=8,
                        PoolThreads=2, PoolPending=tick, PoolCompleted=tick*2)
        summary = matrix.resource_deltas([sample(1, 500, 1000), sample(2, 510, 1100), sample(3, 515, 1300)], 2, 3)
        self.assertEqual(5, summary['cpu_ms'])
        self.assertEqual(200, summary['allocated_bytes'])
        self.assertEqual(2, summary['samples'])
        self.assertEqual(2, summary['pool_completed'])

    def test_ab_ba_and_case_order_are_reproducible(self):
        first = matrix.plan(2, ('paused', 'downloads'))
        self.assertEqual(first, matrix.plan(2, ('paused', 'downloads')))
        self.assertEqual([(0, 'paused', 'baseline'), (0, 'paused', 'candidate')], first[:2])
        self.assertEqual([(1, 'downloads', 'candidate'), (1, 'downloads', 'baseline')], first[4:6])
        summary = matrix.distribution([1, 2, 3, 100])
        self.assertEqual(2, summary['median'])
        self.assertEqual(100, summary['p95'])
        self.assertGreater(summary['stddev'], 0)

    def test_source_inventory_includes_uncommitted_inputs_excludes_build_outputs(self):
        (self.root / 'main.cs').write_text('initial')
        (self.root / '.gitignore').write_text('protected')
        (self.root / 'bin').mkdir()
        (self.root / 'bin' / 'generated.cs').write_text('ignored')
        initial = matrix.source_inventory(self.root)
        self.assertEqual({'.gitignore', 'main.cs'}, set(initial['files']))
        (self.root / 'main.cs').write_text('uncommitted')
        self.assertNotEqual(initial['sha256'], matrix.source_inventory(self.root)['sha256'])

    @unittest.skipIf(os.name == 'nt', 'POSIX flock fixture')
    def test_matrix_lock_rejects_parallel_runtime_owner(self):
        with matrix.serialized_lock(self.root / 'runtime.lock'):
            with self.assertRaises(BlockingIOError):
                with matrix.serialized_lock(self.root / 'runtime.lock'):
                    self.fail('A second owner acquired the runtime lock')


if __name__ == '__main__':
    unittest.main()
