#!/usr/bin/env python3
"""GUI-only overlay for existing native8 / offline4 workers; offline planner untouched."""
import argparse

DEDICATED = {'first_run': 2, 'preference_startup': 3, 'scoped_diagnostics': 4,
             'gif_playback': 5, 'gallery_errors': 6, 'reader_media': 7,
             'gallery_features': 8, 'media_input': 2, 'tray_lifecycle': 3}
SPLIT_FAMILIES = {'gif_playback', 'gallery_errors'}
PREREQUISITES = ['smoke_restore', 'smoke_assets', 'smoke_build', 'plugin_delivery']


def owners(workers):
    if workers == 8:
        return {name: owner for name, owner in DEDICATED.items() if name not in SPLIT_FAMILIES}
    if workers == 4:
        assigned = {name: 2 + (owner - 2) % 3 for name, owner in DEDICATED.items()}
        assigned['preference_startup'] = 1
        assigned['tray_lifecycle'] = 1
        assigned['media_input'] = 3  # Balance the 240s native-input driver off lane B.
        return {name: owner for name, owner in assigned.items() if name not in SPLIT_FAMILIES}
    raise ValueError('GUI worker count must be 4 or 8')


def scenes_for(required, shard, workers=8):
    owners(workers)
    if shard not in range(1, workers + 1):
        raise ValueError('invalid GUI shard')
    # Native primary retains release/upgrade work. The four-lane platforms need
    # all four workers to keep their GUI + offline deadline budgets bounded.
    if workers == 4:
        # Balance declared GUI budgets only, never the separately owned offline
        # planner. Build/release costs are explicitly excluded from this model.
        load = [0, 0, 0, 0]
        for name, owner in owners(workers).items():
            load[owner - 1] += 240 if name == 'media_input' else 180 if name == 'tray_lifecycle' else 300
        assignment = {}
        # Longest GUI cells first, stable original order for equal budgets.
        for _, name in sorted(enumerate(required), key=lambda item: (-scene_timeout(item[1], 180), item[0])):
            owner = min(range(workers), key=lambda i: (load[i], i))
            load[owner] += scene_timeout(name, 180)
            assignment[name] = owner + 1
        return [name for name in required if assignment[name] == shard]

    return required[:1] if shard == 1 else [name for i, name in enumerate(required[1:]) if i % 7 == shard - 2]


def scene_timeout(name, default):
    return 300 if name.startswith(('gif-', 'gallery-errors-')) else default


def budgets(required, shard, runner_os, scenario='all'):
    workers = 8 if runner_os == 'macOS' else 4
    scenes = scenes_for(required, shard, workers)
    default = 300 if runner_os == 'macOS' else 180
    apps = sum(scene_timeout(name, default) for name in scenes)
    dedicated = [name for name, owner in owners(workers).items() if owner == shard
                 and (scenario == 'all' or name not in ('media_input', 'tray_lifecycle'))]
    dedicated_seconds = sum(240 if name == 'media_input' else 180 if name == 'tray_lifecycle' else 300
                            for name in dedicated)
    offline = (22 if workers == 8 else 35) * 60 if scenario == 'all' else 0
    audio = sum(45 for name in scenes if runner_os == 'Linux' and name.startswith('gallery-errors-'))
    return dict(scene_app_seconds=apps, dedicated_app_seconds=dedicated_seconds,
                offline_step_seconds=offline, declared_app_plus_offline_seconds=apps + dedicated_seconds + offline,
                registry_deadline_seconds=120, runner_outer_overhead_seconds=45 * len(scenes) + audio,
                outer_expiry_cleanup_max_seconds=35, job_timeout_seconds=75 * 60,
                excludes=['build/restore', 'fixture suites/upgrades', 'desktop preparation',
                          'capture/runner overhead', 'private audio startup', 'artifact upload'],
                whole_job_worst_case_fits=False)


def overlay(required, shard, runner_os='macOS', scenario='all'):
    workers = 8 if runner_os == 'macOS' else 4
    assigned = owners(workers)
    if scenario == 'gui-only':
        assigned = {s: owner for s, owner in assigned.items() if s not in ('media_input', 'tray_lifecycle')}
    elif scenario != 'all':
        raise ValueError('GUI overlay supports only all/gui-only')
    if shard not in range(1, workers + 1):
        raise ValueError('invalid GUI shard')
    prerequisites = PREREQUISITES + (['linux_setup'] if runner_os == 'Linux' else ['prepare_desktop'])
    result = [step for step in required if step not in DEDICATED]
    for step in prerequisites + ['smoke_normal'] + [s for s, owner in assigned.items() if owner == shard]:
        if step not in result:
            result.append(step)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--required', required=True)
    parser.add_argument('--shard', required=True, type=int)
    parser.add_argument('--runner-os', required=True, choices=['macOS', 'Windows', 'Linux'])
    parser.add_argument('--scenario', default='all', choices=['all', 'gui-only'])
    args = parser.parse_args()
    print(','.join(overlay(args.required.split(','), args.shard, args.runner_os, args.scenario)))


if __name__ == '__main__':
    main()
