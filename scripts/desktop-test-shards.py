#!/usr/bin/env python3
"""Exact portable Desktop coverage across the requested number of lanes.

Discovery (60s maximum) is inside the existing 1800s Desktop deadline. The
portable offline runner owns Windows/POSIX process lifetime and a two-host queue.
Historical LPT weights are advisory, not a promise that all timeouts fit a lane.
"""
import argparse
from collections import Counter
from dataclasses import asdict
import importlib.util
import json
from pathlib import Path
import platform
import re
import subprocess
import time

SPEC = importlib.util.spec_from_file_location('desktop_pure_planner', Path(__file__).with_name('run-macos-test-shards.py'))
planner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(planner)
LANES = ('lane-a', 'lane-b', 'lane-c', 'lane-d')
ARCHITECTURES = {'ubuntu-24.04': 'x64', 'ubuntu-24.04-arm': 'arm64', 'windows-2022': 'x64'}
PROJECT = 'tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj'
BUDGET = 1800
require = planner.require
write_json = planner.write_json


def lanes(shard_count):
    require(len(LANES) <= shard_count <= planner.MAX_WORKERS, 'Invalid portable shard count')
    return tuple('lane-' + chr(ord('a') + index) for index in range(shard_count))


def plan_digest(shape, shard_count=len(LANES)):
    return planner.plan_digest(shape, shard_count)


def architecture():
    return {'amd64': 'x64', 'x86_64': 'x64', 'aarch64': 'arm64', 'arm64': 'arm64'}.get(platform.machine().lower())


def process_passed(result):
    return result.get('status') == 'passed' and result.get('exit_code') == 0 and not result.get('error')


def run_cell(runner, lane, target, source, root, repo, dotnet, budget, grace, shard_count=len(LANES)):
    started = time.monotonic()
    output = root.resolve() / lane
    manifest = dict(schema=1, lane=lane, shard_count=shard_count, platform=target, source=source,
                    architecture=architecture(), operating_system=platform.system(), budget_seconds=budget, cleanup_grace_seconds=grace, passed=False)
    try:
        require(lane in lanes(shard_count) and target in ARCHITECTURES, 'Invalid lane/platform')
        require(0 < budget <= BUDGET, 'Invalid Desktop deadline')
        require(0 <= grace <= 30, 'Invalid cleanup grace')
        require(architecture() == ARCHITECTURES[target], 'Wrong worker architecture')
        require(platform.system() == ('Windows' if target == 'windows-2022' else 'Linux'), 'Wrong worker OS')
        require(re.fullmatch('[0-9a-f]{40}', source), 'Expected full source commit')
        actual = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True, timeout=10).strip()
        require(actual == source, 'Worker source differs from requested source')
        # Reject stale evidence, never merge a previous attempt into this cell.
        output.mkdir(parents=True, exist_ok=False)
        assembly = repo / Path(PROJECT).parent / 'bin/Release/net10.0/MeowChan.DesktopTests.dll'
        require(assembly.is_file(), 'Missing built Desktop assembly')
        discovery_dir = output / 'discovery' / Path(PROJECT).stem
        command = [dotnet, 'vstest', str(assembly), '--ListTests', '--Diag:' + str(discovery_dir / 'vstest-diag.log')]
        remaining = budget - (time.monotonic() - started)
        require(remaining > 0, 'Desktop deadline exhausted before discovery')
        discovery = runner.run_project(PROJECT, shard_directory=output / 'discovery', repo_root=repo,
            dotnet=dotnet, timeout_seconds=min(60, remaining),
            cleanup_grace_seconds=grace, command_override=command)
        manifest['discovery'] = asdict(discovery)
        require(process_passed(manifest['discovery']), 'Discovery failed/timed out')
        inventory = planner.discovery_inventory(discovery_dir / 'vstest-diag.log', discovery_dir / 'inventory.json')
        manifest['inventory'] = inventory
        manifest['inventory_sha256'] = planner.digest(inventory)
        manifest['plan_sha256'] = plan_digest(planner.inventory_shape(inventory), shard_count)
        assigned = planner.partition(inventory, shard_count=shard_count)[lanes(shard_count).index(lane)]
        manifest['assigned'] = assigned
        settings = output / 'desktop.runsettings'
        planner.write_settings(settings, assigned)
        remaining = budget - (time.monotonic() - started)
        require(remaining > 0, 'Desktop deadline exhausted during discovery')
        results = output / 'run' / Path(PROJECT).stem
        command = runner.build_test_command(dotnet, PROJECT, results) + ['--settings', str(settings)]
        run = runner.run_project(PROJECT, shard_directory=output / 'run', repo_root=repo,
            dotnet=dotnet, timeout_seconds=remaining, cleanup_grace_seconds=grace, command_override=command)
        manifest['run'] = asdict(run)
        require(process_passed(manifest['run']), 'Desktop failed/timed out')
        manifest['coverage'] = planner.trx_coverage(results, inventory, assigned)
        manifest['passed'] = True
    except Exception as exc:
        manifest['error'] = str(exc)
    # A stale existing directory is a failure, not evidence to overwrite.
    receipt = output / 'desktop-manifest.json'
    if receipt.exists():
        raise ValueError('Refusing to overwrite prior Desktop receipt')
    write_json(receipt, manifest)
    return runner.ShardResult('desktop-cell', 'passed' if manifest['passed'] else 'failed',
        0 if manifest['passed'] else 1, round(time.monotonic() - started, 3), budget,
        projects=[dict(project=PROJECT, status='passed' if manifest['passed'] else 'failed')],
        error=manifest.get('error'))


def aggregate(root, platforms, source, shard_count=len(LANES)):
    require(re.fullmatch('[0-9a-f]{40}', source), 'Expected full source commit')
    require(platforms and len(platforms) == len(set(platforms)) and set(platforms) <= set(ARCHITECTURES), 'Invalid platforms')
    all_lanes = lanes(shard_count)
    receipts = {}
    for path in root.rglob('desktop-manifest.json'):
        receipt = json.loads(path.read_text())
        key = receipt['platform'], receipt['lane']
        require(key not in receipts, 'Duplicate Desktop worker')
        require(receipt['schema'] == 1 and receipt['shard_count'] == shard_count, 'Wrong schema/count')
        require(receipt['source'] == source, 'Wrong source commit')
        require(receipt['architecture'] == ARCHITECTURES.get(key[0]), 'Wrong architecture')
        require(receipt.get('operating_system') == ('Windows' if key[0] == 'windows-2022' else 'Linux'), 'Wrong operating system')
        require(receipt['passed'] is True, 'Failed Desktop worker')
        require(0 < receipt['budget_seconds'] <= BUDGET, 'Invalid Desktop deadline')
        require(0 <= receipt.get('cleanup_grace_seconds', -1) <= 30, 'Invalid cleanup grace')
        receipts[key] = receipt, path.parent
    expected = {(target, lane) for target in platforms for lane in all_lanes}
    require(set(receipts) == expected, 'Missing/extra Desktop workers')
    # Bind retained non-Desktop work to each lane as well; no hidden full-assembly replay.
    owner_projects = {
        'lane-a': ('MeowChan.ImageHeader.Tests', 'MeowChan.MetadataFilters.Tests', 'MeowChan.OriginalMediaHttp.Tests'),
        'lane-b': ('MeowChan.Tests', 'MeowChan.OfficialPlugins.Tests', 'MeowChan.ExamplePlugins.Tests'),
        'lane-c': ('MeowChan.PluginRuntime.Tests', 'MeowChan.PluginCompiler.Tests', 'MeowChan.PluginHost.Configuration.Tests', 'MeowChan.PluginSdk.Tests'),
        'lane-d': ('MeowChan.PluginFilters.Tests', 'MeowChan.SourceAdapter.Tests'),
    }
    owners = {}
    for path in root.rglob('aggregate-status.json'):
        status = json.loads(path.read_text())
        key = status.get('coverage_platform'), status.get('lane')
        require(key not in owners and key in expected, 'Duplicate/unexpected offline lane receipt')
        require(status.get('source') == source and status.get('aggregate_status') == 'passed', 'Failed/wrong-source offline lane')
        require(status.get('coverage_scope') == cell_scope(shard_count), 'Unsplit/unknown offline lane')
        require(shard_count == len(LANES) or status.get('desktop_shard_count') == shard_count, 'Wrong owner shard count')
        require(0 < status.get('lane_execution_budget_seconds', 0) <= BUDGET, 'Missing/unbounded lane execution deadline')
        projects = [p['project'] for shard in status['shards'] for p in shard['projects']]
        wanted = [PROJECT] + [f'tests/{name}/{name}.csproj' for name in owner_projects.get(key[1], ())]
        require(Counter(projects) == Counter(wanted), 'Offline project owners changed or Desktop replayed')
        require(all(shard['status'] == 'passed' and all(p['status'] == 'passed' for p in shard['projects']) for shard in status['shards']), 'Failed offline work')
        owners[key] = status
    require(set(owners) == expected, 'Missing offline lane ownership receipts')
    checks = []
    for target in platforms:
        canonical = None
        observed = []
        for lane in all_lanes:
            receipt, directory = receipts[target, lane]
            inventory = planner.validate_inventory(receipt['inventory'])
            shape = planner.inventory_shape(inventory)
            if canonical is None:
                canonical = shape
            require(shape == canonical, 'Workers disagree on Desktop inventory')
            require(planner.digest(inventory) == receipt['inventory_sha256'], 'Inventory digest mismatch')
            require(receipt['plan_sha256'] == plan_digest(shape, shard_count), 'Plan digest mismatch')
            assigned = planner.partition(inventory, shard_count=shard_count)[all_lanes.index(lane)]
            require(receipt['assigned'] == assigned, 'Wrong Desktop assignment')
            for name, maximum in [('discovery', 60), ('run', receipt['budget_seconds'])]:
                process = receipt[name]
                require(process_passed(process), 'Failed/incomplete Desktop process')
                require(0 < process['timeout_seconds'] <= maximum, 'Unbounded process deadline')
            require(receipt['discovery']['duration_seconds'] + receipt['run']['timeout_seconds'] <= receipt['budget_seconds'] + 0.1,
                    'Invalid process budgets')
            coverage = planner.trx_coverage(directory / 'run' / Path(PROJECT).stem, inventory, assigned)
            require(coverage == receipt['coverage'], 'Coverage report differs from actual TRX')
            observed.extend(coverage['methods'])
        require(Counter(observed) == Counter({row['method']: 1 for row in canonical}), 'Missing/duplicate Desktop coverage')
        checks.append(dict(platform=target, methods=len(observed), plan_sha256=plan_digest(canonical, shard_count)))
    return dict(passed=True, scope=coverage_scope(shard_count), source=source, shard_count=shard_count, coverage=checks)


def cell_scope(shard_count):
    return 'desktop-four-lane-cell-not-full-proof' if shard_count == len(LANES) else 'desktop-sharded-cell'


def coverage_scope(shard_count):
    return 'complete-four-lane-desktop' if shard_count == len(LANES) else 'complete-sharded-desktop'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--platform', action='append', required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--shard-count', type=int, default=len(LANES))
    args = parser.parse_args()
    try:
        report = aggregate(args.input, args.platform, args.source, args.shard_count)
    except Exception as exc:
        report = dict(passed=False, scope=coverage_scope(args.shard_count), error=str(exc))
    write_json(args.report, report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
