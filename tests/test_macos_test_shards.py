"""Native inventory/sharding/gate contracts; no full .NET suite or GUI needed."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('mac_shards', ROOT / 'scripts/run-macos-test-shards.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)
SOURCE = 'a' * 40
OK = dict(timeout=False, exit_code=0, cleanup=dict(complete=True))


def inventory():
    # Each class stays on one worker, including all theory rows/methods.
    return [dict(id=f'{i}-{j}', method=f'Example.C{i:02}.Theory', deferred=False)
            for i in range(16) for j in range(1 + i % 3)]


def write_trx(path, cases):
    root = ET.Element('TestRun')
    results = ET.SubElement(root, 'Results')
    definitions = ET.SubElement(root, 'TestDefinitions')
    for i, case in enumerate(cases):
        test = ET.SubElement(definitions, 'UnitTest', id=case['id'])
        cls, method = case['method'].rsplit('.', 1)
        ET.SubElement(test, 'TestMethod', className=cls, name=method)
        for j in range(2 if case['deferred'] else 1):
            ET.SubElement(results, 'UnitTestResult', testId=case['id'], executionId=f'{i}-{j}',
                          testName='Runtime-random (commas, ") | FullyQualifiedName=Other.Test', outcome='Passed')
    summary = ET.SubElement(root, 'ResultSummary', outcome='Completed')
    ET.SubElement(summary, 'Counters', total=str(len(results)), passed=str(len(results)), executed=str(len(results)),
                  **{k: '0' for k in ('failed', 'error', 'timeout', 'aborted', 'notRunnable', 'disconnected', 'inProgress', 'pending')})
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(root).write(path)
    return root


class ShardTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)

    def test_partition_covers_every_method_once_and_keeps_classes_atomic(self):
        cases = inventory() + [dict(id='extra', method='Example.C01.Another', deferred=True)]
        bins = runner.partition(cases)
        flat = [m for b in bins for m in b]
        self.assertEqual(8, len(bins))
        self.assertTrue(all(bins))
        self.assertEqual(len(flat), len(set(flat)))
        self.assertEqual(set(flat), {c['method'] for c in cases})
        self.assertEqual(bins, runner.partition(list(reversed(cases))))
        self.assertEqual(next(i for i, b in enumerate(bins) if 'Example.C01.Theory' in b),
                         next(i for i, b in enumerate(bins) if 'Example.C01.Another' in b))

    def test_measured_low_row_costs_are_separated_and_growth_has_fallback(self):
        profile = dict(fallback_case_ms=10, classes={
            'Example.C00': dict(milliseconds=10000, cases=1),
            'Example.C01': dict(milliseconds=9000, cases=2)})
        cases = inventory()
        bins = runner.partition(cases, profile)
        self.assertNotEqual(next(i for i, b in enumerate(bins) if 'Example.C00.Theory' in b),
                            next(i for i, b in enumerate(bins) if 'Example.C01.Theory' in b))
        self.assertEqual(bins, runner.partition(list(reversed(cases)), profile))
        weights = runner.class_weights(cases, profile)
        self.assertEqual(30, weights['Example.C02'])  # Unseen class: three rows.
        cases.append(dict(id='new', method='Example.C01.New', deferred=True))
        self.assertEqual(13500, runner.class_weights(cases, profile)['Example.C01'])
        self.assertEqual(9000, runner.class_weights(cases[:1] + [cases[1]], profile)['Example.C01'])

    def test_plan_digest_binds_timing_profile_and_gate_rejects_changed_weights(self):
        shape = runner.inventory_shape(inventory())
        before = runner.plan_digest(shape)
        self.matrix()
        profile = copy.deepcopy(runner.timing_profile())
        profile['fallback_case_ms'] += 1
        with mock.patch.object(runner, 'timing_profile', return_value=profile):
            self.assertNotEqual(before, runner.plan_digest(shape))
            with self.assertRaisesRegex(ValueError, 'Plan digest mismatch'):
                runner.aggregate(self.root, ['macos-15'], SOURCE)

    def test_invalid_timing_profile_fails_closed(self):
        profile = runner.timing_profile()
        invalid = [dict(profile, schema=99), dict(profile, classes={}),
                   dict(profile, fallback_case_ms=0), dict(profile, fallback_case_ms=True),
                   dict(profile, classes={'N.Class': dict(milliseconds=-1, cases=2)}),
                   dict(profile, classes={'N.Class': dict(milliseconds=1, cases=0)})]
        for value in invalid:
            with self.subTest(value=str(value)[:50]), \
                 mock.patch.object(Path, 'read_text', return_value=json.dumps(value)), \
                 self.assertRaises(ValueError):
                runner.timing_profile()

    def test_checked_in_measured_profile_balances_full_class_inventory(self):
        profile = runner.timing_profile()
        cases = [dict(id=f'{name}-{i}', method=name + '.Measured', deferred=False)
                 for name, entry in profile['classes'].items() for i in range(entry['cases'])]
        weights = runner.class_weights(cases, profile)
        bins = runner.partition(cases)
        flat = [method for shard in bins for method in shard]
        self.assertEqual(len(profile['classes']), len(flat))
        self.assertEqual(len(flat), len(set(flat)))
        self.assertEqual({c['method'] for c in cases}, set(flat))
        loads = [sum(weights[m.rsplit('.', 1)[0]] for m in shard) for shard in bins]
        self.assertLess(max(loads) - min(loads), 1000)
        # Reconstruct the retired case-count algorithm on the same measured data.
        old_bins, counts = [[] for _ in range(8)], [0] * 8
        for name in sorted(weights, key=lambda n: (-profile['classes'][n]['cases'], n)):
            shard = min(range(8), key=lambda i: (counts[i], i))
            old_bins[shard].append(name)
            counts[shard] += profile['classes'][name]['cases']
        old_loads = [sum(weights[name] for name in shard) for shard in old_bins]
        self.assertGreater(max(old_loads), max(loads) * 1.3)

    def test_empty_or_invalid_inventory_fails_instead_of_running_unfiltered(self):
        for cases in ([], inventory()[:1], inventory() + inventory()[:1],
                      [dict(id='evil', method='X.Y|FullyQualifiedName~Any', deferred=False)]):
            with self.subTest(cases=cases[:1]), self.assertRaises(ValueError):
                runner.partition(cases)
        with self.assertRaises(ValueError):
            runner.write_settings(self.root / 'bad.xml', [])

    def test_settings_uses_exact_fqns_in_a_file_not_display_name_or_substring(self):
        path = self.root / 'filter.runsettings'
        runner.write_settings(path, ['N.Type.Test', 'N.Type.TestLong'])
        root = ET.parse(path).getroot()
        self.assertEqual('FullyQualifiedName=N.Type.Test|FullyQualifiedName=N.Type.TestLong',
                         root.find('./RunConfiguration/TestCaseFilter').text)
        self.assertEqual('1', root.find('./RunConfiguration/MaxCpuCount').text)

    def discovery_log(self, *, aborted=False, total=2, complete=True):
        tests = []
        for i in range(2):
            tests.append(dict(Id=f'id-{i}', FullyQualifiedName='N.Class.Test', DisplayName=f'random({i}, )',
                              ExecutorUri='executor://xunit/VsTestRunner2/netcoreapp',
                              Properties=[dict(Key=dict(Id='XunitTestCase'), Value=':F:Class:Test')]))
        messages = [dict(MessageType='TestDiscovery.TestFound', Payload=tests[:1])]
        if complete:
            messages.append(dict(MessageType='TestDiscovery.Completed', Payload=dict(
                IsAborted=aborted, TotalTests=total, LastDiscoveredTests=tests[1:], FullyDiscoveredSources=['X.dll'])))
        path = self.root / 'discovery.log'
        path.write_text('\n'.join('prefix TestRequestSender.OnDiscoveryMessageReceived: Received message: ' + json.dumps(m)
                                  for m in messages))
        return path

    def test_discovery_accounts_for_batches_and_final_batch_without_parsing_display(self):
        cases = runner.discovery_inventory(self.discovery_log())
        self.assertEqual(['id-0', 'id-1'], [c['id'] for c in cases])
        self.assertEqual({'N.Class.Test'}, {c['method'] for c in cases})

    def test_duplicate_ids_fail_closed_and_preserve_compact_rejected_evidence(self):
        for other_method in ('N.Class.Test', 'N.Other.Test'):
            with self.subTest(other_method=other_method):
                path = self.discovery_log()
                lines = path.read_text().splitlines()
                lines[1] = lines[1].replace('id-1', 'id-0').replace('N.Class.Test', other_method)
                path.write_text('\n'.join(lines))
                evidence = self.root / 'discovery-inventory.json'
                with self.assertRaisesRegex(ValueError, "Duplicate discovery testId 'id-0'.*N.Class.Test"):
                    runner.discovery_inventory(path, evidence)
                cases = json.loads(evidence.read_text())
                self.assertEqual(['id-0', 'id-0'], [c['id'] for c in cases])
                self.assertEqual(other_method, cases[1]['method'])
                self.assertNotIn('DisplayName', evidence.read_text())
                self.assertNotIn('XunitTestCase', evidence.read_text())
                path.write_bytes(b'x' * (9 * 1024 * 1024))
                runner.watchdog.bound_evidence(self.root)
                self.assertEqual(cases, json.loads(evidence.read_text()))
                self.assertTrue(path.read_bytes().startswith(b'[Evidence truncated;'))

    def test_identical_display_names_are_not_duplicate_identities(self):
        path = self.discovery_log()
        path.write_text(path.read_text().replace('random(1, )', 'random(0, )'))
        self.assertEqual(2, len(runner.discovery_inventory(path)))

    def test_keyboard_alias_rows_keep_all_eight_distinct_name_inputs(self):
        source = (ROOT / 'tests/MeowChan.DesktopTests/BooruHitKeyboardTests.cs').read_text()
        theory = source[source.index('    [Theory]'):source.index('    [Fact]')]
        import re
        names = re.findall(r'InlineData\(nameof\(Avalonia.Input.Key.(\w+)\)', theory)
        self.assertEqual(['Up', 'Down', 'Home', 'End', 'Enter', 'Return', 'Left', 'Space'], names)
        self.assertIn('string keyName, string expected', theory)
        self.assertIn('Enum.Parse<Key>(keyName)', theory)

    def test_discovery_requires_real_nonaborted_complete_inventory(self):
        for kwargs in (dict(aborted=True), dict(total=3), dict(complete=False)):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                runner.discovery_inventory(self.discovery_log(**kwargs))

    def coverage(self, change=None, *, cases=None):
        cases = cases or inventory()
        path = self.root / 'results/tests.trx'
        root = write_trx(path, cases)
        if change:
            change(root)
            ET.ElementTree(root).write(path)
        return runner.trx_coverage(path.parent, cases, sorted({c['method'] for c in cases}))

    def test_runtime_rows_match_case_ids_and_methods_not_random_display_strings(self):
        result = self.coverage()
        self.assertTrue(result['passed'])
        self.assertEqual(len(inventory()), result['runtime_rows'])

    def test_deferred_theory_rows_share_case_id_but_have_distinct_execution_ids(self):
        cases = [dict(id='deferred', method='N.Class.Deferred', deferred=True)]
        self.assertEqual(2, self.coverage(cases=cases)['runtime_rows'])
        cases[0]['deferred'] = False
        def duplicate(root):
            row = copy.deepcopy(root.find('./Results/UnitTestResult'))
            row.set('executionId', 'another')
            root.find('./Results').append(row)
            for key in ('total', 'passed', 'executed'):
                root.find('./ResultSummary/Counters').set(key, '2')
        with self.assertRaises(ValueError):
            self.coverage(duplicate, cases=cases)

    def test_missing_extra_duplicate_empty_failed_or_incomplete_results_fail_closed(self):
        def remove(root):
            results = root.find('Results')
            results.remove(results[0])
        mutations = [remove,
                     lambda r: r.find('Results').clear(),
                     lambda r: r.find('./Results/UnitTestResult').set('testId', 'unassigned'),
                     lambda r: r.find('./Results/UnitTestResult').set('outcome', 'Failed'),
                     lambda r: r.find('./ResultSummary').set('outcome', 'Aborted'),
                     lambda r: r.find('./TestDefinitions/UnitTest/TestMethod').set('name', 'Wrong'),
                     lambda r: r.find('./Results')[1].set('executionId', r.find('./Results')[0].get('executionId'))]
        for mutate in mutations:
            with self.subTest(mutate=mutate), self.assertRaises(ValueError):
                self.coverage(mutate)

    def test_only_explicit_skips_are_accounted_not_silent_nonexecution(self):
        def skip(root):
            row = root.find('./Results/UnitTestResult')
            row.set('outcome', 'NotExecuted')
            counters = root.find('./ResultSummary/Counters')
            counters.set('passed', str(len(inventory()) - 1))
            counters.set('executed', str(len(inventory()) - 1))
            return row
        with self.assertRaises(ValueError):
            self.coverage(skip)
        def explicit(root):
            output = ET.SubElement(skip(root), 'Output')
            ET.SubElement(ET.SubElement(output, 'ErrorInfo'), 'Message').text = 'Existing opt-in fixture'
        self.assertEqual(1, self.coverage(explicit)['explicit_skips'])

    def test_missing_or_multiple_or_truncated_trx_cannot_pass(self):
        cases = inventory()
        for payload in (None, '<TestRun>'):
            path = self.root / 'tests.trx'
            if payload:
                path.write_text(payload)
            with self.assertRaises((ValueError, ET.ParseError)):
                runner.trx_coverage(self.root, cases, [c['method'] for c in cases])
        write_trx(self.root / 'a.trx', cases)
        write_trx(self.root / 'b.trx', cases)
        with self.assertRaises(ValueError):
            runner.trx_coverage(self.root, cases, [c['method'] for c in cases])

    def matrix(self, platforms=('macos-15',)):
        cases = runner.validate_inventory(inventory())
        bins = runner.partition(cases)
        for platform in platforms:
            for shard, assigned in enumerate(bins, 1):
                directory = self.root / platform / f'shard-{shard:02}'
                suites = []
                for name, _ in runner.watchdog.PROJECTS:
                    target = directory / name / 'run/results'
                    write_trx(target / 'tests.trx', [c for c in cases if c['method'] in assigned])
                    suites.append(dict(name=name, passed=True, budget_seconds=300 if name == 'core' else 600,
                                       discovery=dict(OK, budget_seconds=60), run=dict(OK, budget_seconds=250),
                                       inventory=cases, inventory_sha256=runner.digest(cases), assigned=assigned,
                                       plan_sha256=runner.plan_digest(runner.inventory_shape(cases)),
                                       coverage=runner.trx_coverage(target, cases, assigned)))
                runner.write_json(directory / 'manifest.json', dict(schema=1, shard=shard, shard_count=8,
                    platform=platform, architecture=runner.ARCHITECTURES[platform], source=SOURCE, candidate=False, suites=suites, passed=True))

    def test_aggregate_requires_both_suites_and_all_eight_native_workers_per_platform(self):
        self.matrix(runner.PLATFORMS)
        result = runner.aggregate(self.root, list(runner.PLATFORMS), SOURCE)
        self.assertTrue(result['passed'])
        self.assertEqual(4, len(result['coverage']))
        (self.root / 'macos-15/shard-08/manifest.json').unlink()
        with self.assertRaises(ValueError):
            runner.aggregate(self.root, list(runner.PLATFORMS), SOURCE)

    def test_aggregate_rejects_source_inventory_assignment_status_and_suite_tampering(self):
        mutations = [lambda m: m.update(source='b'*40), lambda m: m.update(passed=False),
                     lambda m: m['suites'].pop(), lambda m: m['suites'][0].update(assigned=[]),
                     lambda m: m['suites'][0]['inventory'].pop(),
                     lambda m: m['suites'][0].update(run=dict(OK, timeout=True)),
                     lambda m: m['suites'][0].update(run=dict(OK, cleanup=dict(complete=False)))]
        self.matrix()
        path = self.root / 'macos-15/shard-01/manifest.json'
        original = path.read_text()
        for mutate in mutations:
            m = json.loads(original)
            mutate(m)
            runner.write_json(path, m)
            with self.subTest(mutate=mutate), self.assertRaises(ValueError):
                runner.aggregate(self.root, ['macos-15'], SOURCE)
        path.write_text(original)
        # A lying passed boolean must not hide missing runtime evidence.
        next((path.parent / 'core/run/results').glob('*.trx')).unlink()
        with self.assertRaises(ValueError):
            runner.aggregate(self.root, ['macos-15'], SOURCE)

    def test_native_theory_argument_case_ids_may_differ_between_workers_not_methods_or_counts(self):
        self.matrix()
        directory = self.root / 'macos-15/shard-02'
        path = directory / 'manifest.json'
        manifest = json.loads(path.read_text())
        for suite in manifest['suites']:
            for case in suite['inventory']:
                case['id'] = 'different-native-temp-argument-' + case['id']
            suite['inventory_sha256'] = runner.digest(suite['inventory'])
            target = directory / suite['name'] / 'run/results'
            write_trx(target / 'tests.trx', [c for c in suite['inventory'] if c['method'] in suite['assigned']])
            suite['coverage'] = runner.trx_coverage(target, suite['inventory'], suite['assigned'])
        runner.write_json(path, manifest)
        self.assertTrue(runner.aggregate(self.root, ['macos-15'], SOURCE)['passed'])
        # Same method names but missing theory rows is NOT a matching inventory.
        suite = manifest['suites'][0]
        suite['inventory'].pop()
        suite['inventory_sha256'] = runner.digest(suite['inventory'])
        runner.write_json(path, manifest)
        with self.assertRaises(ValueError):
            runner.aggregate(self.root, ['macos-15'], SOURCE)

    def test_aggregate_rejects_duplicate_workers(self):
        self.matrix()
        first = self.root / 'macos-15/shard-01/manifest.json'
        runner.write_json(self.root / 'duplicate/manifest.json', json.loads(first.read_text()))
        with self.assertRaises(ValueError):
            runner.aggregate(self.root, ['macos-15'], SOURCE)

    def test_deadline_exit_zero_is_not_success_and_old_budgets_remain(self):
        self.assertFalse(runner.process_passed(dict(OK, timeout=True)))
        self.assertEqual((300, 600, 390, 810), (runner.watchdog.STANDARD_CORE_TIMEOUT,
                         runner.watchdog.STANDARD_DESKTOP_TIMEOUT, runner.watchdog.CANDIDATE_CORE_TIMEOUT,
                         runner.watchdog.CANDIDATE_DESKTOP_TIMEOUT))

    def test_discovery_time_is_deducted_from_execution_deadline(self):
        project = self.root / 'Probe.csproj'
        assembly = self.root / 'bin/Release/net10.0/Probe.dll'
        assembly.parent.mkdir(parents=True)
        assembly.touch()
        output = self.root / 'out'
        def run(name, command, target, timeout):
            target.mkdir(parents=True)
            return OK
        with mock.patch.object(runner.watchdog, 'run_one', side_effect=run) as invoke, \
             mock.patch.object(runner, 'discovery_inventory', return_value=inventory()), \
             mock.patch.object(runner, 'trx_coverage', return_value=dict(passed=True)), \
             mock.patch.object(runner.time, 'monotonic', side_effect=[0, 12, 14]):
            result = runner.run_suite('core', str(project), output, 1, 300)
        self.assertTrue(result['passed'], result)
        self.assertEqual(60, invoke.call_args_list[0].args[-1])
        self.assertEqual(288, invoke.call_args_list[1].args[-1])
        self.assertIn('--settings', invoke.call_args_list[1].args[1])

    def test_worker_continues_after_core_failure_and_refuses_stale_result_reuse(self):
        def run(name, project, directory, shard, budget):
            return dict(name=name, passed=name == 'desktop')
        with mock.patch.object(runner.sys, 'platform', 'darwin'), \
             mock.patch.object(runner.host_platform, 'machine', return_value='arm64'), \
             mock.patch.object(runner.subprocess, 'check_output', return_value=SOURCE + '\n'), \
             mock.patch.object(runner, 'run_suite', side_effect=run) as suites:
            self.assertFalse(runner.run_worker(1, 'macos-15', SOURCE, self.root))
            self.assertEqual(['core', 'desktop'], [call.args[0] for call in suites.call_args_list])
            self.assertEqual([300, 600], [call.args[-1] for call in suites.call_args_list])
            with self.assertRaises(FileExistsError):
                runner.run_worker(1, 'macos-15', SOURCE, self.root)
        manifest = json.loads((self.root / 'shard-01/manifest.json').read_text())
        self.assertFalse(manifest['passed'])
        self.assertEqual(2, len(manifest['suites']))

    def test_worker_rejects_non_native_architecture(self):
        with mock.patch.object(runner.sys, 'platform', 'linux'), self.assertRaises(ValueError):
            runner.run_worker(1, 'macos-15', SOURCE, self.root)
        with mock.patch.object(runner.sys, 'platform', 'darwin'), \
             mock.patch.object(runner.host_platform, 'machine', return_value='x86_64'), self.assertRaises(ValueError):
            runner.run_worker(1, 'macos-15', SOURCE, self.root)

    def test_trx_and_inventory_are_never_truncated_for_artifact_budget(self):
        size = 8 * 1024 * 1024 + 1
        for name in ('proof.trx', 'manifest.json', 'shard.runsettings', 'console.log'):
            (self.root / name).write_bytes(b'x' * size)
        runner.watchdog.bound_evidence(self.root)
        for name in ('proof.trx', 'manifest.json', 'shard.runsettings'):
            self.assertEqual(size, (self.root / name).stat().st_size)
        self.assertTrue((self.root / 'console.log').read_bytes().startswith(b'[Evidence truncated;'))


if __name__ == '__main__':
    unittest.main()
