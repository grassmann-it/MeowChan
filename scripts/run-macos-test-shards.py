#!/usr/bin/env python3
"""Eight native macOS workers; complete discovery, exact filters and TRX coverage.

Partition whole classes, never display names or theory arguments. xUnit's deferred
(non-serializable) theories retain their discovery testId for all runtime rows;
those rows have distinct executionIds. Ordinary discovered cases require exactly
one result, deferred cases one or more. No result is inferred from console text.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import importlib.util
import json
from pathlib import Path
import platform as host_platform
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location('mac_watchdog', Path(__file__).with_name('macos-test-watchdog.py'))
watchdog = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(watchdog)
SHARD_COUNT = 8
PLATFORMS = ('macos-15', 'macos-15-intel')
ARCHITECTURES = {'macos-15': 'arm64', 'macos-15-intel': 'x86_64'}
SCHEMA = 1
DISCOVERY_TIMEOUT = 60


def require(condition, message):
    if not condition:
        raise ValueError(message)


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def validate_inventory(cases):
    require(isinstance(cases, list) and cases, 'Empty discovery inventory')
    ids = {}
    for case in cases:
        require(set(case) == {'id', 'method', 'deferred'}, 'Invalid inventory case schema')
        require(isinstance(case['id'], str) and case['id'], 'Missing discovery testId')
        if case['id'] in ids:
            raise ValueError(f"Duplicate discovery testId {case['id']!r}: "
                             f"{ids[case['id']]!r} and {case['method']!r}")
        ids[case['id']] = case['method']
        # Fail closed rather than allowing filter operators into an exact FQN.
        require(isinstance(case['method'], str) and re.fullmatch(r'[\w.`+]+\.[\w`]+', case['method']),
                f'Unsupported method identity: {case["method"]!r}')
        require(type(case['deferred']) is bool, 'Invalid deferred-theory marker')
    return sorted(cases, key=lambda case: (case['method'], case['id']))


def discovery_inventory(path, evidence_path=None):
    """Read native VSTest protocol records, not human --list-tests output.

    The SDK is pinned by global.json. Missing, changed or partial protocol fails
    closed. Only the console's receive side is read, so host echo lines cannot
    double-count the inventory. We do not persist serialized theory arguments.
    """
    found, completed = [], []
    marker = 'TestRequestSender.OnDiscoveryMessageReceived: Received message: '
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        if marker not in line:
            continue
        message = json.loads(line.split(marker, 1)[1])
        if message['MessageType'] == 'TestDiscovery.TestFound':
            found.extend(message['Payload'])
        elif message['MessageType'] == 'TestDiscovery.Completed':
            completed.append(message['Payload'])
            found.extend(message['Payload'].get('LastDiscoveredTests') or [])
    require(len(completed) == 1, 'Missing/duplicate discovery completion')
    final = completed[0]
    require(final.get('IsAborted') is False and final.get('TotalTests') == len(found),
            'Aborted/incomplete discovery')
    require(not any(final.get(key) for key in ('PartiallyDiscoveredSources', 'NotDiscoveredSources',
                                               'SkippedDiscoverySources')), 'Incomplete discovery sources')
    cases = []
    for test in found:
        require(test.get('ExecutorUri') == 'executor://xunit/VsTestRunner2/netcoreapp', 'Unexpected test adapter')
        serialized = [p['Value'] for p in test.get('Properties', []) if p['Key']['Id'] == 'XunitTestCase']
        require(len(serialized) == 1, 'Missing xUnit case identity')
        cases.append(dict(id=test['Id'], method=test['FullyQualifiedName'],
                          deferred=serialized[0].startswith('Xunit.Sdk.XunitTheoryTestCase,')))
    # Keep compact identities even when validation rejects them: the raw protocol
    # can exceed the watchdog log bound before the offending record is retained.
    if evidence_path is not None:
        write_json(evidence_path, cases)
    return validate_inventory(cases)



def inventory_shape(cases):
    # Case IDs may include a theory's native temporary-directory argument. Those
    # IDs must match THIS worker's TRX, but cannot be compared between machines.
    # Cross-worker planning uses method identity and discovery cardinality only;
    # random/localized display strings are never coverage evidence.
    methods = defaultdict(Counter)
    for case in validate_inventory(cases):
        methods[case['method']]['deferred' if case['deferred'] else 'concrete'] += 1
    return [dict(method=method, concrete=counts['concrete'], deferred=counts['deferred'])
            for method, counts in sorted(methods.items())]


def partition(cases):
    """Deterministic largest-first balancing by discovered rows, class-atomic."""
    classes = defaultdict(list)
    for case in validate_inventory(cases):
        classes[case['method'].rsplit('.', 1)[0]].append(case)
    bins = [[] for _ in range(SHARD_COUNT)]
    weights = [0] * SHARD_COUNT
    for name in sorted(classes, key=lambda name: (-len(classes[name]), name)):
        shard = min(range(SHARD_COUNT), key=lambda index: (weights[index], index))
        bins[shard].extend({case['method'] for case in classes[name]})
        weights[shard] += len(classes[name])
    require(all(bins), 'Inventory cannot populate all eight shards')
    return [sorted(methods) for methods in bins]


def write_settings(path, methods):
    require(methods, 'Refusing an empty filter (would run the whole suite)')
    root = ET.Element('RunSettings')
    config = ET.SubElement(root, 'RunConfiguration')
    ET.SubElement(config, 'MaxCpuCount').text = '1'
    ET.SubElement(config, 'TestCaseFilter').text = '|'.join('FullyQualifiedName=' + method for method in methods)
    ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)


def process_passed(result):
    return (result.get('timeout') is False and result.get('exit_code') == 0
            and result.get('cleanup', {}).get('complete') is True and not result.get('error'))


def trx_coverage(results, inventory, assigned):
    expected = {case['id']: case for case in validate_inventory(inventory) if case['method'] in assigned}
    require(expected, 'No assigned cases')
    files = list(results.glob('*.trx'))
    require(len(files) == 1, 'Expected exactly one untruncated TRX')
    root = ET.parse(files[0]).getroot()
    summary = root.find('./{*}ResultSummary')
    require(summary is not None and summary.get('outcome') in ('Completed', 'Passed'), 'TRX run incomplete/failed')
    counters = summary.find('./{*}Counters')
    require(counters is not None, 'Missing TRX counters')
    definitions = {}
    for test in root.findall('./{*}TestDefinitions/{*}UnitTest'):
        identity = test.get('id')
        method = test.find('./{*}TestMethod')
        require(identity not in definitions and method is not None, 'Duplicate/missing TRX definition')
        definitions[identity] = method.get('className', '') + '.' + method.get('name', '')
    require(set(definitions) == set(expected), 'TRX definition inventory differs from assigned discovery cases')
    rows = root.findall('./{*}Results/{*}UnitTestResult')
    require(rows and int(counters.get('total', '-1')) == len(rows), 'Empty/incomplete TRX results')
    counts, outcomes, executions = Counter(), Counter(), set()
    for row in rows:
        identity, execution, outcome = row.get('testId'), row.get('executionId'), row.get('outcome')
        require(identity in expected, 'Unassigned runtime testId')
        require(definitions[identity] == expected[identity]['method'], 'Runtime method identity changed')
        require(execution and execution not in executions, 'Missing/duplicate runtime executionId')
        executions.add(execution)
        require(outcome in ('Passed', 'NotExecuted'), f'Nonpassing result: {outcome}')
        if outcome == 'NotExecuted':
            reason = row.find('./{*}Output/{*}ErrorInfo/{*}Message')
            require(reason is not None and reason.text and reason.text.strip(), 'Skip without explicit reason')
        counts[identity] += 1
        outcomes[outcome] += 1
    for identity, case in expected.items():
        require(counts[identity] >= 1 if case['deferred'] else counts[identity] == 1,
                f'Missing/duplicate result for {case["method"]} ({identity})')
    require(int(counters.get('passed', '-1')) == outcomes['Passed'], 'TRX passed counter mismatch')
    require(int(counters.get('executed', '-1')) == outcomes['Passed'], 'TRX executed counter mismatch')
    for key in ('failed', 'error', 'timeout', 'aborted', 'notRunnable', 'disconnected', 'inProgress', 'pending'):
        require(int(counters.get(key, '-1')) == 0, f'Nonzero/missing TRX {key} counter')
    return dict(passed=True, discovered_cases=len(expected), runtime_rows=len(rows),
                passed_rows=outcomes['Passed'], explicit_skips=outcomes['NotExecuted'],
                deferred_cases=sum(case['deferred'] for case in expected.values()),
                methods=sorted(set(case['method'] for case in expected.values())))


def run_suite(name, project, output, shard, budget):
    started = time.monotonic()
    report = dict(name=name, budget_seconds=budget, passed=False)
    try:
        project = Path(project)
        assembly = project.parent / 'bin/Release/net10.0' / (project.stem + '.dll')
        require(assembly.is_file(), f'Missing built test assembly: {assembly}')
        discovery = output / 'discovery'
        command = ['dotnet', 'vstest', str(assembly), '--ListTests', '--Diag:' + str(discovery / 'vstest-diag.log')]
        report['discovery'] = watchdog.run_one('discovery', command, discovery, min(DISCOVERY_TIMEOUT, budget))
        require(process_passed(report['discovery']), 'Discovery process failed/timed out/left children')
        inventory = discovery_inventory(discovery / 'vstest-diag.log',
                                        discovery / 'discovery-inventory.json')
        report['inventory'] = inventory
        report['inventory_sha256'] = digest(inventory)
        report['plan_sha256'] = digest(inventory_shape(inventory))
        report['assigned'] = assigned = partition(inventory)[shard - 1]
        settings = output / 'shard.runsettings'
        write_settings(settings, assigned)
        remaining = budget - (time.monotonic() - started)
        require(remaining > 0, 'Suite deadline exhausted during discovery')
        target = output / 'run'
        command = watchdog.build_test_command(str(project), target) + ['--settings', str(settings)]
        report['run'] = watchdog.run_one(name, command, target, remaining)
        # A post-cleanup exit 0 is NEVER acceptance after a timeout.
        require(process_passed(report['run']), 'Test process failed/timed out/left children')
        report['coverage'] = trx_coverage(target / 'results', inventory, assigned)
        report['passed'] = True
    except Exception as exc:
        report['error'] = str(exc)
    report['seconds'] = round(time.monotonic() - started, 2)
    return report


def run_worker(shard, platform, source, output, candidate=False):
    require(1 <= shard <= SHARD_COUNT and platform in PLATFORMS, 'Invalid shard/platform')
    require(sys.platform == 'darwin' and host_platform.machine() == ARCHITECTURES[platform],
            'Worker must run on the requested native macOS architecture')
    require(re.fullmatch('[0-9a-f]{40}', source), 'Expected full source commit')
    actual = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True, timeout=10).strip()
    require(source == actual, 'Worker source differs from requested source')
    output = output.resolve() / f'shard-{shard:02}'
    # Never let a rerun/stale result satisfy coverage.
    output.mkdir(parents=True, exist_ok=False)
    budgets = ((watchdog.CANDIDATE_CORE_TIMEOUT, watchdog.CANDIDATE_DESKTOP_TIMEOUT) if candidate else
               (watchdog.STANDARD_CORE_TIMEOUT, watchdog.STANDARD_DESKTOP_TIMEOUT))
    manifest = dict(schema=SCHEMA, shard=shard, shard_count=SHARD_COUNT, platform=platform,
                    source=source, architecture=host_platform.machine(), candidate=candidate, suites=[], passed=False)
    for (name, project), budget in zip(watchdog.PROJECTS, budgets):
        result = run_suite(name, project, output / name, shard, budget)
        manifest['suites'].append(result)
        write_json(output / 'manifest.json', manifest)
        print(json.dumps({key: value for key, value in result.items() if key not in ('inventory', 'assigned')}), flush=True)
    manifest['passed'] = all(suite['passed'] for suite in manifest['suites'])
    write_json(output / 'manifest.json', manifest)
    watchdog.bound_evidence(output)
    return manifest['passed']


def aggregate(root, platforms, source):
    """Recheck the actual TRX, not worker booleans; require the complete matrix."""
    require(re.fullmatch('[0-9a-f]{40}', source), 'Expected full source commit')
    require(platforms and len(platforms) == len(set(platforms)) and set(platforms) <= set(PLATFORMS),
            'Invalid aggregate platforms')
    manifests = {}
    for path in root.rglob('manifest.json'):
        manifest = json.loads(path.read_text())
        key = (manifest['platform'], manifest['shard'])
        require(type(manifest['shard']) is int and type(manifest['candidate']) is bool, 'Invalid worker metadata')
        require(manifest['architecture'] == ARCHITECTURES.get(manifest['platform']), 'Wrong native architecture')
        require(key not in manifests, f'Duplicate worker: {key}')
        require(manifest['schema'] == SCHEMA and manifest['shard_count'] == SHARD_COUNT, 'Wrong manifest version/count')
        require(manifest['source'] == source, 'Wrong source commit')
        require(manifest['passed'] is True, f'Failed worker: {key}')
        manifests[key] = (manifest, path.parent)
    expected = {(platform, shard) for platform in platforms for shard in range(1, SHARD_COUNT + 1)}
    require(set(manifests) == expected, f'Missing/extra workers: {sorted(expected ^ set(manifests))}')
    checks = []
    for platform in platforms:
        canonical, observed = {}, defaultdict(list)
        profiles = set()
        for shard in range(1, SHARD_COUNT + 1):
            manifest, directory = manifests[(platform, shard)]
            profiles.add(manifest['candidate'])
            suites = manifest['suites']
            require([s['name'] for s in suites] == [name for name, _ in watchdog.PROJECTS], 'Missing/duplicate suite')
            for suite in suites:
                name = suite['name']
                budgets = ((watchdog.CANDIDATE_CORE_TIMEOUT, watchdog.CANDIDATE_DESKTOP_TIMEOUT) if manifest['candidate'] else
                           (watchdog.STANDARD_CORE_TIMEOUT, watchdog.STANDARD_DESKTOP_TIMEOUT))
                budget = dict(zip((n for n, _ in watchdog.PROJECTS), budgets))[name]
                require(suite['budget_seconds'] == budget and 0 < suite['discovery'].get('budget_seconds', 0) <= DISCOVERY_TIMEOUT
                        and 0 < suite['run'].get('budget_seconds', 0) <= budget, 'Unexpected/unbounded process deadline')
                require(suite['passed'] is True and process_passed(suite['discovery']) and process_passed(suite['run']),
                        f'Incomplete process result: {platform}/{shard}/{name}')
                inventory = validate_inventory(suite['inventory'])
                require(digest(inventory) == suite['inventory_sha256'], 'Inventory digest mismatch')
                if name not in canonical:
                    canonical[name] = inventory_shape(inventory)
                require(inventory_shape(inventory) == canonical[name], f'Workers disagree on native inventory: {platform}/{name}')
                require(suite['plan_sha256'] == digest(canonical[name]), 'Plan digest mismatch')
                assigned = partition(inventory)[shard - 1]
                require(suite['assigned'] == assigned, 'Wrong shard assignment')
                coverage = trx_coverage(directory / name / 'run/results', inventory, assigned)
                require(suite['coverage'] == coverage, 'Worker coverage report differs from TRX')
                observed[name].extend(coverage['methods'])
        require(len(profiles) == 1, 'Mixed timeout profiles')
        for name, shape in canonical.items():
            require(Counter(observed[name]) == Counter({method['method']: 1 for method in shape}),
                    f'Missing/duplicate method coverage: {platform}/{name}')
            checks.append(dict(platform=platform, suite=name, methods=len(set(observed[name])),
                               discovered_cases=sum(m['concrete'] + m['deferred'] for m in shape), plan_sha256=digest(shape)))
    return dict(passed=True, source=source, shard_count=SHARD_COUNT, platforms=platforms, coverage=checks)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    run = sub.add_parser('run')
    run.add_argument('--shard', type=int, required=True, choices=range(1, SHARD_COUNT + 1))
    run.add_argument('--platform', choices=PLATFORMS, required=True)
    run.add_argument('--source', required=True)
    run.add_argument('--candidate-validation', action='store_true')
    run.add_argument('--output', type=Path, default=Path('artifacts/macos-test-shards'))
    gate = sub.add_parser('aggregate')
    gate.add_argument('--input', type=Path, required=True)
    gate.add_argument('--platform', choices=PLATFORMS, action='append', required=True)
    gate.add_argument('--source', required=True)
    gate.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.action == 'run':
            return int(not run_worker(args.shard, args.platform, args.source, args.output, args.candidate_validation))
        result = aggregate(args.input, args.platform, args.source)
    except Exception as exc:
        result = dict(passed=False, error=str(exc))
    if args.action == 'aggregate':
        write_json(args.report, result)
    print(json.dumps(result), flush=True)
    return int(not result['passed'])


if __name__ == '__main__':
    raise SystemExit(main())
