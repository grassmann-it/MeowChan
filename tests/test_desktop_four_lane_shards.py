"""Desktop-only portable LPT / actual-TRX gate contracts; no builds or native hosts."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module
cells = load('desktop_cells_test', 'desktop-test-shards.py')
runner = load('portable_runner_test', 'run-dotnet-shards.py')
SOURCE = 'a' * 40
CASES = [dict(id=f'id-{i}', method=f'Example.C{i}.Test', deferred=False) for i in range(12)]


def trx(path, cases):
    root = ET.Element('TestRun')
    results = ET.SubElement(root, 'Results')
    definitions = ET.SubElement(root, 'TestDefinitions')
    for i, case in enumerate(cases):
        unit = ET.SubElement(definitions, 'UnitTest', id=case['id'])
        cls, method = case['method'].rsplit('.', 1)
        ET.SubElement(unit, 'TestMethod', className=cls, name=method)
        ET.SubElement(results, 'UnitTestResult', testId=case['id'], executionId=f'exec-{i}', outcome='Passed')
    summary = ET.SubElement(root, 'ResultSummary', outcome='Completed')
    ET.SubElement(summary, 'Counters', total=str(len(cases)), passed=str(len(cases)), executed=str(len(cases)),
        **{k: '0' for k in ('failed', 'error', 'timeout', 'aborted', 'notRunnable', 'disconnected', 'inProgress', 'pending')})
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(root).write(path)


class DesktopFourLaneTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def matrix(self, count=4):
        for index, lane in enumerate(cells.lanes(count)):
            directory = self.root / lane
            # Local IDs may differ; only method/cardinality shape is cross-machine.
            inventory = cells.planner.validate_inventory([dict(case, id=case['id'] + lane) for case in CASES])
            assigned = cells.planner.partition(inventory, shard_count=count)[index]
            target = directory / 'run/MeowChan.DesktopTests/tests.trx'
            trx(target, [case for case in inventory if case['method'] in assigned])
            receipt = dict(schema=1, shard_count=count, lane=lane, platform='windows-2022', architecture='x64',
                source=SOURCE, operating_system='Windows', cleanup_grace_seconds=30, passed=True, budget_seconds=1800, inventory=inventory,
                inventory_sha256=cells.planner.digest(inventory),
                plan_sha256=cells.plan_digest(cells.planner.inventory_shape(inventory), count), assigned=assigned,
                discovery=dict(status='passed', exit_code=0, timeout_seconds=60, duration_seconds=2),
                run=dict(status='passed', exit_code=0, timeout_seconds=1797, duration_seconds=1),
                coverage=cells.planner.trx_coverage(target.parent, inventory, assigned))
            cells.write_json(directory / 'desktop-manifest.json', receipt)
            shard_names = [s for s in runner.LANE_SHARDS.get(lane, ()) if s != 'shard-02']
            shards = [dict(status='passed', projects=[dict(project=p, status='passed') for p in runner.SHARDS[s]]) for s in shard_names]
            shards.append(dict(status='passed', projects=[dict(project=cells.PROJECT, status='passed')]))
            cells.write_json(directory / 'aggregate-status.json', dict(lane=lane, coverage_platform='windows-2022',
                source=SOURCE, aggregate_status='passed', coverage_scope=cells.cell_scope(count),
                desktop_shard_count=count, lane_execution_budget_seconds=1800, shards=shards))

    def gate(self, count=4):
        return cells.aggregate(self.root, ['windows-2022'], SOURCE, count)

    def mutate(self, file, callback):
        path = self.root / 'lane-a' / file
        value = json.loads(path.read_text())
        callback(value)
        cells.write_json(path, value)

    def test_four_bins_exact_once_atomic_and_native_default_unchanged(self):
        bins = cells.planner.partition(CASES, shard_count=4)
        self.assertEqual(4, len(bins))
        self.assertEqual(sorted(c['method'] for c in CASES), sorted(m for b in bins for m in b))
        self.assertEqual(8, len(cells.planner.partition(CASES)))
        shape = cells.planner.inventory_shape(CASES)
        self.assertNotEqual(cells.plan_digest(shape), cells.planner.plan_digest(shape))

    def test_complete_matrix_accepts_local_case_ids_actual_trx(self):
        self.matrix()
        self.assertTrue(self.gate()['passed'])
        self.assertEqual(12, self.gate()['coverage'][0]['methods'])

    def test_missing_lane_not_full_proof(self):
        self.matrix()
        (self.root / 'lane-d/desktop-manifest.json').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing/extra Desktop workers'):
            self.gate()

    def test_new_lane_required_without_replaying_other_projects(self):
        self.matrix(5)
        self.assertEqual(12, self.gate(5)['coverage'][0]['methods'])
        receipt = self.root / 'lane-e/aggregate-status.json'
        status = json.loads(receipt.read_text())
        status['shards'].append(dict(status='passed', projects=[
            dict(project=runner.SHARDS['shard-01'][0], status='passed')]))
        cells.write_json(receipt, status)
        with self.assertRaisesRegex(ValueError, 'Offline project owners changed'):
            self.gate(5)
        receipt.unlink()
        with self.assertRaisesRegex(ValueError, 'Missing offline lane'):
            self.gate(5)

    def test_wrong_source_arch_count_plan_inventory_and_assignment_fail(self):
        for field, value in [('source', 'b'*40), ('architecture', 'arm64'), ('operating_system', 'Darwin'), ('cleanup_grace_seconds', 31), ('shard_count', 8),
                             ('plan_sha256', 'bad'), ('inventory_sha256', 'bad'), ('assigned', []), ('passed', False)]:
            with self.subTest(field=field):
                self.matrix()
                self.mutate('desktop-manifest.json', lambda r: r.update({field: value}))
                with self.assertRaises(ValueError): self.gate()

    def test_no_trx_console_or_forged_coverage_cannot_pass(self):
        self.matrix()
        path = self.root / 'lane-a/run/MeowChan.DesktopTests/tests.trx'
        path.unlink()
        (path.parent / 'console.log').write_text('Passed!')
        with self.assertRaises(ValueError): self.gate()
        self.matrix()
        self.mutate('desktop-manifest.json', lambda r: r['coverage'].update(runtime_rows=999))
        with self.assertRaises(ValueError): self.gate()

    def test_failed_truncated_and_duplicate_trx_failclosed(self):
        for mode in ('failed', 'truncated', 'duplicate'):
            self.matrix()
            path = self.root / 'lane-a/run/MeowChan.DesktopTests/tests.trx'
            if mode == 'failed':
                tree = ET.parse(path); tree.getroot().find('./Results/UnitTestResult').set('outcome', 'Failed'); tree.write(path)
            elif mode == 'truncated': path.write_text('<TestRun>')
            else: (path.parent / 'extra.trx').write_bytes(path.read_bytes())
            with self.assertRaises((ValueError, ET.ParseError)): self.gate()
            if mode == 'duplicate': (path.parent / 'extra.trx').unlink()

    def test_changed_other_project_owner_or_unfiltered_replay_rejected(self):
        self.matrix()
        self.mutate('aggregate-status.json', lambda r: r['shards'][0]['projects'].append(dict(project=cells.PROJECT, status='passed')))
        with self.assertRaisesRegex(ValueError, 'owners changed or Desktop replayed'): self.gate()
        self.matrix()
        (self.root / 'lane-b/aggregate-status.json').unlink()
        with self.assertRaisesRegex(ValueError, 'ownership receipts'): self.gate()

    def test_overdeadline_or_timeout_never_passes(self):
        for mutation in (lambda r: r.update(budget_seconds=1801),
                         lambda r: r['run'].update(status='timed_out', exit_code=124),
                         lambda r: r['run'].update(timeout_seconds=1801)):
            self.matrix(); self.mutate('desktop-manifest.json', mutation)
            with self.assertRaises(ValueError): self.gate()

    def test_queue_keeps_twelve_owners_no_legacy_desktop_two_hosts_collects_failures(self):
        for lane in cells.LANES:
            active = 0; maximum = 0; seen = []; lock = threading.Lock()
            def task(name, projects):
                nonlocal active, maximum
                with lock: active += 1; maximum = max(maximum, active); seen.extend(projects)
                time.sleep(.01)
                with lock: active -= 1
                return runner.ShardResult(name, 'failed' if name == 'desktop-cell' else 'passed', 1 if name == 'desktop-cell' else 0, .01, 1800,
                    projects=[dict(project=p, status='passed') for p in projects])
            def shard(name, projects, **kwargs): return task(name, projects)
            with mock.patch('builtins.print'):
                code = runner.run_all(results_root=self.root/lane, repo_root=ROOT, dotnet='dotnet',
                    shard_timeout_seconds=1800, light_shard_timeout_seconds=900, project_timeout_seconds=1800,
                    light_project_timeout_seconds=900, cleanup_grace_seconds=30, lane=lane,
                    desktop_task=lambda remaining: task('desktop-cell', [cells.PROJECT]), shard_runner=shard)
            self.assertEqual(1, code)
            self.assertLessEqual(maximum, 2)
            expected = [p for s in runner.LANE_SHARDS[lane] if s != 'shard-02' for p in runner.SHARDS[s]] + [cells.PROJECT]
            self.assertCountEqual(expected, seen)
            self.assertEqual(1, seen.count(cells.PROJECT))

    def test_queued_shard_inherits_absolute_deadline_and_writes_not_run_receipt(self):
        clock = [0.0]
        barrier = threading.Barrier(2)
        expired = threading.Event()
        budgets = {}
        def desktop(remaining):
            self.assertEqual(1800, remaining)
            barrier.wait(timeout=5)
            clock[0] = 1800.0
            expired.set()
            return runner.ShardResult('desktop-cell', 'failed', 1, 1800, remaining)
        def shard(name, projects, **kwargs):
            budgets[name] = kwargs['shard_timeout_seconds']
            if name == 'shard-04':
                barrier.wait(timeout=5)
                expired.wait(timeout=5)
                return runner.ShardResult(name, 'failed', 1, 1800, kwargs['shard_timeout_seconds'])
            def forbidden(*args, **kw):
                raise AssertionError('Expired queued project must not launch')
            return runner.run_shard(name, projects, project_runner=forbidden, **kwargs)
        with mock.patch.object(runner.time, 'monotonic', side_effect=lambda: clock[0]), mock.patch('builtins.print'):
            result = runner.run_all(results_root=self.root/'queue', repo_root=ROOT, dotnet='dotnet',
                shard_timeout_seconds=1800, light_shard_timeout_seconds=900, project_timeout_seconds=1800,
                light_project_timeout_seconds=900, cleanup_grace_seconds=30, lane='lane-d',
                desktop_task=desktop, shard_runner=shard)
        self.assertEqual(1, result)
        self.assertEqual(0, budgets['shard-05'])
        status = json.loads((self.root/'queue/shard-05/status.json').read_text())
        self.assertEqual('failed', status['status'])
        self.assertEqual('skipped', status['projects'][0]['status'])
        self.assertTrue((self.root/'queue/aggregate-status.json').is_file())

    def test_portable_cell_discovery_and_filtered_execution_share_1800_budget(self):
        repo = self.root / 'repo'
        assembly = repo / 'tests/MeowChan.DesktopTests/bin/Release/net10.0/MeowChan.DesktopTests.dll'
        assembly.parent.mkdir(parents=True)
        assembly.touch()
        calls = []
        def project(project, **kwargs):
            calls.append(kwargs)
            directory = kwargs['shard_directory'] / Path(project).stem
            directory.mkdir(parents=True, exist_ok=True)
            if len(calls) == 2:
                assigned = cells.planner.partition(CASES, shard_count=4)[0]
                trx(directory / 'tests.trx', [c for c in CASES if c['method'] in assigned])
            return runner.ProjectResult(project, Path(project).stem, 'passed', 0, .001,
                kwargs['timeout_seconds'], str(directory), str(directory / 'project.log'), kwargs['command_override'])
        fake = mock.Mock(run_project=project, build_test_command=runner.build_test_command, ShardResult=runner.ShardResult)
        ticks = iter((0.0, 5.0))
        with mock.patch.object(cells, 'architecture', return_value='x64'), \
             mock.patch.object(cells.platform, 'system', return_value='Windows'), \
             mock.patch.object(cells.subprocess, 'check_output', return_value=SOURCE), \
             mock.patch.object(cells.planner, 'discovery_inventory', return_value=cells.planner.validate_inventory(CASES)), \
             mock.patch.object(cells.time, 'monotonic', side_effect=lambda: next(ticks, 5.0)):
            result = cells.run_cell(fake, 'lane-a', 'windows-2022', SOURCE, self.root/'out', repo, 'dotnet', 1800, 30)
        self.assertEqual('passed', result.status)
        self.assertEqual(2, len(calls))
        self.assertLessEqual(calls[0]['timeout_seconds'], 60)
        self.assertEqual(1795.0, calls[1]['timeout_seconds'])
        self.assertIn('--ListTests', calls[0]['command_override'])
        self.assertIn('--settings', calls[1]['command_override'])
        settings = self.root/'out/lane-a/desktop.runsettings'
        self.assertIn('FullyQualifiedName=', settings.read_text())

    def test_discovery_timeout_never_launches_desktop_or_accepts_stale_results(self):
        repo = self.root/'repo'
        assembly = repo/'tests/MeowChan.DesktopTests/bin/Release/net10.0/MeowChan.DesktopTests.dll'
        assembly.parent.mkdir(parents=True); assembly.touch()
        fake = mock.Mock(ShardResult=runner.ShardResult)
        fake.run_project.return_value = runner.ProjectResult(cells.PROJECT, 'MeowChan.DesktopTests', 'timed_out', 124, 60, 60, '', '', [], 'deadline')
        with mock.patch.object(cells, 'architecture', return_value='x64'), \
             mock.patch.object(cells.platform, 'system', return_value='Windows'), \
             mock.patch.object(cells.subprocess, 'check_output', return_value=SOURCE):
            result = cells.run_cell(fake, 'lane-a', 'windows-2022', SOURCE, self.root/'out', repo, 'dotnet', 1800, 30)
        self.assertEqual('failed', result.status)
        fake.run_project.assert_called_once()

    def test_workflow_full_gate_and_focused_subset_artifacts(self):
        text = (ROOT / '.github/workflows/dotnet-desktop.yml').read_text()
        self.assertIn('--desktop-platform "${{ matrix.os }}" --source "$(git rev-parse HEAD)"', text)
        gate = text.split('  desktop-offline-gate:', 1)[1].split('  library-package:', 1)[0]
        self.assertIn("inputs.scenario == 'all'", gate)
        self.assertNotIn("inputs.mode == 'offline-retest'", gate)
        self.assertIn('if: always()', gate)
        self.assertIn('--required validate,macos-offline-gate,desktop-offline-gate,', text)
        self.assertIn('test-results/offline-shards/desktop-cells/**', text)
        self.assertEqual(1800, runner.DEFAULT_PROJECT_TIMEOUT_SECONDS)


if __name__ == '__main__': unittest.main()
