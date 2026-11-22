#!/usr/bin/env python3
"""Plan Validate workers and preview the class split from VSTest discovery."""
import argparse
from collections import Counter
import importlib
import json
import os
from pathlib import Path

offline = importlib.import_module('desktop-test-shards')
native = offline.planner
gates = importlib.import_module('compute-validation-required')
scenes = importlib.import_module('run-gui-ci-scenes')
gui = importlib.import_module('gui-ci-plan')

TIMINGS = Path(__file__).resolve().parents[1] / 'build/validate-ci-timings.json'
PLATFORMS = (*offline.ARCHITECTURES, *native.PLATFORMS)
CANDIDATES = ('prerelease-candidate', 'stable-candidate')
GUI_TASKS = tuple(name for name in gui.DEDICATED if name not in gui.SPLIT_FAMILIES)
UPGRADES = ('upgrade_replay', 'upgrade_102_replay')
ARCHITECTURES = {**offline.ARCHITECTURES, **native.ARCHITECTURES}


def balanced_rows(platform, count, scenario, mode, timing):
    mac = platform in native.PLATFORMS
    linux = platform.startswith('ubuntu')
    full = scenario == 'all'
    gui_tasks = [name for name in GUI_TASKS if full or name not in ('media_input', 'tray_lifecycle')]
    if linux:
        gui_tasks.append('smoke_plugins')
    if mode in CANDIDATES and platform == 'ubuntu-24.04':
        gui_tasks.append('smoke_1_1_registry')
    if full and linux:
        gui_tasks.append('tray_host')
    if full:
        gui_tasks.extend(UPGRADES)

    scene_ids = scenes.full_scene_inventory()
    scene_weights = timing['scene_seconds']
    weights = {name: scene_weights.get(name, timing['fallback_scene_seconds']) for name in scene_ids}
    weights.update({name: timing['task_seconds'][name] for name in gui_tasks})
    native.require(all(type(seconds) is int and seconds > 0 for seconds in weights.values()),
                   'Invalid GUI or upgrade timing')
    # The test planner splits by class independently. Model the serial work on
    # top of the offline step, including the retained non-Desktop project owners.
    offline_cost = timing['offline_seconds'] * timing['observed_shards'] // count
    loads = [offline_cost if full else 0] * count
    if full:
        for i, seconds in enumerate(timing['portable_owner_seconds']):
            loads[i] = max(loads[i], seconds)
        loads[0] += timing['primary_checks_seconds']
    assignments, _ = native.balance(weights, loads)
    native.require(Counter(name for batch in assignments for name in batch) ==
                   Counter(scene_ids + gui_tasks), 'Missing/duplicate GUI or upgrade assignment')

    rows = []
    for i, assigned in enumerate(assignments):
        lane = ('native' if i == 0 else f'native-{i + 1:02}') if mac else offline.lanes(count)[i]
        row = dict(os=platform, lane=lane, full=i == 0, balanced=True, workers=count, shard=i + 1,
                   mac_shard=i + 1 if mac else 0, scenes=','.join(name for name in scene_ids if name in assigned),
                   tasks=[name for name in gui_tasks if name in assigned])
        if full:
            required = ['record_source', 'dev_build', 'restore_release', 'retain_release_assets', 'build_all']
            required += ['mac_tests'] if mac else ['process_probe', 'core_tests']
            if mode in CANDIDATES:
                required.append('candidate_matrix')
            if i == 0:
                required += ['public_api', 'package_fixtures', 'local_package', 'smoke_runner_contract']
        else:
            required = ['record_source']
        required += gui.PREREQUISITES + ['linux_setup' if linux else 'prepare_desktop']
        if row['scenes']:
            required.append('smoke_normal')
        row['required'] = ','.join(required + row['tasks'])
        rows.append(row)
    return rows


def matrix(event, mode, scenario, platform, offline_lane, portable_shards, macos_shards):
    native.require(mode in ('', 'validate', 'offline-retest', *CANDIDATES), 'Unknown validation mode')
    native.require(platform == 'all' or platform in PLATFORMS, 'Unknown platform')
    native.require(native.SHARD_COUNT <= macos_shards <= native.MAX_WORKERS, 'Invalid macOS shard count')
    offline.lanes(portable_shards)
    scenario = gates.effective_scenario(event, scenario)
    native.require(scenario in ('all', *gates.TARGETED_STEPS), 'Unknown scenario')
    selected = PLATFORMS if platform == 'all' else (platform,)
    if mode in CANDIDATES:
        native.require(platform == 'all' and scenario == 'all', 'Candidates require all platforms and checks')
    if mode == 'offline-retest':
        native.require(platform not in native.PLATFORMS and scenario == 'all' and offline_lane in offline.lanes(portable_shards),
                       'offline-retest requires a portable lane and scenario all')
        selected = tuple(os for os in selected if os not in native.PLATFORMS)
    balanced = mode != 'offline-retest' and scenario in ('all', 'gui-only')
    if balanced:
        profile = json.loads(TIMINGS.read_text(encoding='utf-8'))
        native.require(profile['schema'] == 1, 'Unknown Validate timing schema')
    rows = []
    for target in selected:
        mac = target in native.PLATFORMS
        if balanced:
            count = macos_shards if mac else portable_shards
            rows.extend(balanced_rows(target, count, scenario, mode, profile['platforms'][target]))
            continue
        os_name = 'macOS' if mac else 'Windows' if target.startswith('windows') else 'Linux'
        lane = offline_lane if mode == 'offline-retest' else 'native' if mac else 'lane-a'
        required = gates.required_steps(event, mode, scenario, lane, os_name, ARCHITECTURES[target])
        rows.append(dict(os=target, lane=lane, full=mode != 'offline-retest', balanced=False,
                         workers=portable_shards if mode == 'offline-retest' else 1, mac_shard=1 if mac else 0,
                         scenes='', tasks=[], required=','.join(required)))
    return {'include': rows}


def verify_cell(row, outcomes, smoke_output, source):
    """Check actual GUI receipts and reject any unassigned step that ran."""
    assigned = row['scenes'].split(',') if row['scenes'] else []
    expected = set(row['tasks']) | ({'smoke_normal'} if assigned else set())
    checked = {*GUI_TASKS, *UPGRADES, 'smoke_plugins', 'smoke_1_1_registry', 'tray_host',
               'smoke_normal', *gui.SPLIT_FAMILIES}
    for name in checked:
        actual = outcomes.get(name, {}).get('outcome', 'skipped')
        native.require(actual == ('success' if name in expected else 'skipped'),
                       f'Unexpected GUI/upgrade result: {name} = {actual}')
    if assigned:
        manifest = json.loads((smoke_output / 'scene-shard.json').read_text(encoding='utf-8'))
        collection = json.loads((smoke_output / 'collection.json').read_text(encoding='utf-8'))
        native.require(manifest['source'] == source and manifest['inventory'] == scenes.full_scene_inventory(),
                       'Wrong source or GUI inventory')
        native.require(manifest['required'] == assigned == collection['required'], 'Wrong GUI assignment')
        native.require([item['id'] for item in collection['results']] == assigned, 'Missing/duplicate GUI results')
        native.require(all(collection.get(key) is True for key in ('complete', 'coverage_complete', 'passed'))
                       and all(item.get('passed') is True and item.get('cleanup_proven') is True
                               for item in collection['results']), 'Incomplete GUI collection')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    workflow = commands.add_parser('matrix')
    workflow.add_argument('--event', default='push')
    workflow.add_argument('--mode', default='validate')
    workflow.add_argument('--scenario', default='all')
    workflow.add_argument('--platform', default='all')
    workflow.add_argument('--offline-lane', default='lane-c')
    workflow.add_argument('--portable-shards', type=int, default=5)
    workflow.add_argument('--macos-shards', type=int, default=9)
    workflow.add_argument('--github-output', type=Path)
    workflow.add_argument('--report', type=Path)
    classes = commands.add_parser('classes')
    inventory_input = classes.add_mutually_exclusive_group(required=True)
    inventory_input.add_argument('--discovery', type=Path, help='VSTest --list-tests diagnostic log')
    inventory_input.add_argument('--inventory', type=Path, help='Retained complete discovery inventory JSON')
    classes.add_argument('--shards', type=int, required=True)
    cell = commands.add_parser('verify-cell')
    cell.add_argument('--smoke-output', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'verify-cell':
        row = json.loads(os.environ['VALIDATION_CELL'])
        if row['balanced']:
            verify_cell(row, json.loads(os.environ['VALIDATION_STEPS']), args.smoke_output, os.environ['GITHUB_SHA'])
        return
    if args.command == 'classes':
        inventory = (native.discovery_inventory(args.discovery) if args.discovery else
                     native.validate_inventory(json.loads(args.inventory.read_text(encoding='utf-8'))))
        assignments = native.partition(inventory, shard_count=args.shards)
        native.require(Counter(method for shard in assignments for method in shard) ==
                       Counter({case['method']: 1 for case in inventory}), 'Missing/duplicate test method')
        weights = native.class_weights(inventory, native.timing_profile())
        print(json.dumps([dict(shard=i, milliseconds=sum(weights[name] for name in
              {m.rsplit('.', 1)[0] for m in methods}), methods=methods)
              for i, methods in enumerate(assignments, 1)]))
        return
    plan = matrix(args.event, args.mode, args.scenario, args.platform, args.offline_lane,
                  args.portable_shards, args.macos_shards)
    if args.report:
        native.write_json(args.report, dict(source=os.environ.get('GITHUB_SHA'), mode=args.mode,
                                           scenario=gates.effective_scenario(args.event, args.scenario), **plan))
    payload = json.dumps(plan, separators=(',', ':'))
    if args.github_output:
        with args.github_output.open('a', encoding='utf-8') as stream:
            print(f'matrix={payload}', file=stream)
            print(f'portable_shards={args.portable_shards}', file=stream)
            print(f'macos_shards={args.macos_shards}', file=stream)
    else:
        print(payload)


if __name__ == '__main__':
    main()
