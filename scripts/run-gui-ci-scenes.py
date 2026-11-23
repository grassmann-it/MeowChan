#!/usr/bin/env python3
"""Collect independent source-built GUI scenes; never admit incomplete evidence."""
import argparse
import importlib.util
import json
import os
import signal
from pathlib import Path
import subprocess
import sys
import time
from types import SimpleNamespace

NORMAL = ['normal-core', 'normal-grid', 'normal-settings-page', 'normal-diagnostics',
          'sources-page', 'normal-board-admission', 'pause-controls', 'rules-page', 'settings-core']
# Coverage-equivalent split supplied by the sole C# reader-dispatch owner.
# Requires the complete registry from shared reader dispatch commit 98f6dc213.
READER = ['reader-navigation', 'reader-owned-navigation', 'reader-gallery-roundtrip',
          'reader-list-gallery-return', 'reader-returns', 'reader-stability',
          'reader-groups', 'gallery-close', 'video-lifecycle', 'window-minimum']
MODES = ['locked-profile', 'no-tray', 'tray-failure', 'download-session', 'selection']

GIF = ['gif-playback-core', 'gif-successors', 'gif-verified-failures', 'gif-reader-association', 'gif-ownership']
GALLERY_ERRORS = ['gallery-errors-retirement-clip', 'gallery-errors-layout-roots',
                  'gallery-errors-notifications', 'gallery-errors-failure-reader-return']


def full_scene_inventory():
    return NORMAL + MODES + READER + GIF + GALLERY_ERRORS


def validate_scene_assignment(entries, assigned):
    inventory = full_scene_inventory()
    registry_counts = {}
    for entry in entries:
        registry_counts[entry['id']] = registry_counts.get(entry['id'], 0) + 1
    if any(registry_counts.get(name) != 1 for name in inventory):
        raise ValueError('Required GUI registry inventory is missing or duplicated')
    if not assigned or len(assigned) != len(set(assigned)) or not set(assigned) <= set(inventory):
        raise ValueError('Empty, duplicate or unknown GUI assignment')


def reader_baseline(entries):
    present = {e['id'] for e in entries}
    if not set(READER).issubset(present):
        raise ValueError('Reader decomposition registry is partial; refusing incomplete coverage')
    return list(READER)


class RunnerNotStarted(OSError):
    """Popen failed: no runner (and therefore no app) was launched."""


def bounded_runner(command, *, env, stdout, stderr, timeout):
    """Bound lock wait, app, capture and runner teardown, not just app.wait()."""
    try:
        process = subprocess.Popen(command, env=env, stdout=stdout, stderr=stderr,
                                   start_new_session=os.name != 'nt')
    except OSError as error:
        raise RunnerNotStarted(str(error)) from error
    print(f'Runner pid={process.pid}; outer deadline={timeout}s', file=stdout, flush=True)
    outer_timeout = False
    forced_kill = False
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        outer_timeout = True
        # The POSIX runner handles SIGTERM and cleans its separately owned app
        # group. Windows termination has no such guarantee: its receipt decides
        # nothing after forced termination, so the worker is quarantined below.
        process.terminate()
        if os.name == 'nt':
            forced_kill = True
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            forced_kill = True
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                               stdout=stdout, stderr=stderr, timeout=10, check=False)
            else:
                os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
    return SimpleNamespace(returncode=process.returncode, outer_timeout=outer_timeout, forced_kill=forced_kill)


def collect(entries, required, assembly, output, timeout, native=False, run=bounded_runner):
    output.mkdir(parents=True, exist_ok=True)
    report = output / 'collection.json'
    if report.exists():
        raise ValueError('Choose a fresh output directory; prior evidence is preserved')
    rows = []
    def write(complete):
        passed = complete and len(rows) == len(required) and all(r['passed'] for r in rows)
        report.write_text(json.dumps(dict(required=required, results=rows, complete=complete,
                                         coverage_complete=complete and all(r.get('cleanup_proven') for r in rows),
                                         passed=passed), indent=2) + '\n')
        return passed
    write(False)
    unsafe = False
    for identifier in required:
        row = dict(id=identifier, passed=False)
        launched = False
        started = time.monotonic()
        try:
            if unsafe:
                raise ValueError('Blocked: previous scene process cleanup failed; desktop unsafe')
            matches = [e for e in entries if e['id'] == identifier]
            if len(matches) != 1:
                raise ValueError('Required registry entry missing or duplicated')
            entry = matches[0]
            if entry.get('error') or entry.get('skip'):
                raise ValueError(str(entry.get('error') or entry.get('skip')))
            environment = {k: v for k, v in os.environ.items()
                           if k not in {'MEOWCHAN_GALLERY_TRANSITION_REVIEW', 'MEOWCHAN_GALLERY_HELP_REVIEW'}
                           and (not k.startswith('MEOWCHAN_') or not (k.endswith('_ONLY') or k in SELECTORS))}
            scene_timeout = 300 if identifier in GIF + GALLERY_ERRORS else timeout
            audio_report = None
            audio_passed = True
            forwarded = []
            for pair in entry['environment']:
                key, value = pair.split('=', 1)
                value = value.replace('{repo}', str(Path.cwd()))
                if key == 'MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO' and sys.platform != 'linux':
                    continue  # Preserve the prior native Windows/macOS audio contract.
                environment[key] = value
                forwarded.append(key + '=' + value)
            destination = output / identifier
            command = [sys.executable, 'scripts/run-desktop-smoke.py', '--assembly', str(assembly),
                       '--mode', entry['mode'], '--expect-scenario', entry['scenario'],
                       '--timeout', str(scene_timeout), '--output', str(destination)]
            if native:
                command.append('--allow-native-desktop')
            if sys.platform == 'linux' and identifier in GALLERY_ERRORS:
                audio_report = destination / 'private-audio.json'
                command = [sys.executable, 'scripts/with-private-null-audio.py', '--report', str(audio_report),
                           '--', 'env', *forwarded, *command]

            destination.mkdir(parents=True, exist_ok=True)
            # Retain runner diagnostics even if it fails before writing child results.
            with (destination / 'runner.log').open('w') as log:
                launched = True
                result = run(command, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=scene_timeout + 45 + (45 if audio_report else 0))
            row['outer_timeout'] = getattr(result, 'outer_timeout', False)
            if row['outer_timeout'] or getattr(result, 'forced_kill', False):
                raise RuntimeError('Runner outer deadline/forced termination: desktop safety unproven')
            if result.returncode:
                print((destination / 'runner.log').read_text(errors='replace')[-8000:], flush=True)
            elif sys.platform == 'darwin' and environment.get('MEOWCHAN_MAC_SCREENSHOTS') == '1':
                for line in (destination / 'runner.log').read_text(errors='replace').splitlines():
                    if line.startswith('WARNING macOS screenshot '):
                        print(line, flush=True)
            if audio_report:
                audio = json.loads(audio_report.read_text())
                if audio.get('cleanup', {}).get('complete') is not True:
                    raise RuntimeError('Private audio cleanup unproven')
                audio_passed = (audio.get('status') == 'passed' and bool(audio.get('readiness', {}).get('before'))
                                and bool(audio.get('readiness', {}).get('after')))
                if 'child' not in audio['cleanup'].get('processes', {}):
                    raise RunnerNotStarted('Private audio failed before child launch; cleanup complete')
            evidence = json.loads((destination / 'results.json').read_text())
            receipts = evidence.get('results', [])
            if (evidence.get('selected_modes') != [entry['mode']] or len(receipts) != 1
                    or receipts[0].get('mode') != entry['mode']
                    or not {'error', 'passed', 'exit_code'}.issubset(receipts[0])):
                raise ValueError('Invalid runner cleanup receipt')
            if any('Process cleanup failed' in str(r.get('error', '')) for r in evidence.get('results', [])):
                unsafe = True
                (output / 'unsafe-cleanup').write_text(identifier + '\n')
            row['cleanup_proven'] = not unsafe
            row.update(exit_code=result.returncode, passed=audio_passed and not unsafe and not row['outer_timeout'] and result.returncode == 0 and
                       evidence.get('passed') is True and evidence.get('selected_modes') == [entry['mode']]
                       and len(evidence.get('results', [])) == 1
                       and evidence['results'][0].get('scenario') == entry['scenario']
                       and evidence['results'][0].get('passed') is True)
        except RunnerNotStarted as error:
            row['error'] = 'Runner not started: ' + str(error)
        except Exception as error:
            row['error'] = str(error)
            if launched:
                unsafe = True
                (output / 'unsafe-cleanup').write_text(identifier + ': ' + str(error) + '\n')
        row['duration_seconds'] = round(time.monotonic() - started, 3)
        rows.append(row)
        write(False)
        print(f"GUI {identifier}: {'PASS' if row['passed'] else 'FAIL'}", flush=True)
    return 0 if write(True) else 1


SELECTORS = set()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', required=True, type=int)
    parser.add_argument('--shard', type=int)
    parser.add_argument('--workers', type=int, choices=[4, 8], default=8)
    parser.add_argument('--scenes', help='Exact comma-separated scene assignment from the Validate plan')
    parser.add_argument('--selection-only', action='store_true')
    parser.add_argument('--allow-native-desktop', action='store_true')
    args = parser.parse_args()
    if args.scenes is not None and (args.shard is not None or args.selection_only):
        parser.error('--scenes is exclusive with --shard and --selection-only')
    required = ['selection'] if args.selection_only else full_scene_inventory()
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = args.output / 'scene-shard.json'
    if manifest.exists():
        raise ValueError('Choose a fresh output directory; prior shard evidence is preserved')
    manifest.write_text(json.dumps(dict(source=os.environ.get('GITHUB_SHA'),
        shard=args.shard, workers=args.workers, planned=required, timeout_seconds=args.timeout), indent=2) + '\n')
    try:
        spec = importlib.util.spec_from_file_location('smoke', Path(__file__).with_name('run-desktop-smoke.py'))
        smoke = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(smoke)
        entries = smoke.registry_entries(args.assembly.resolve())
        # Clear every registered selector, including branch selectors without _ONLY.
        SELECTORS.update(pair.split('=', 1)[0] for e in entries for pair in e['environment'])
        baseline = reader_baseline(entries)
        required = ['selection'] if args.selection_only else NORMAL + MODES + baseline + GIF + GALLERY_ERRORS
        inventory = list(required)
        if args.scenes is not None:
            required = args.scenes.split(',')
            validate_scene_assignment(entries, required)
        if args.shard is not None:
            plan_spec = importlib.util.spec_from_file_location('gui_plan', Path(__file__).with_name('gui-ci-plan.py'))
            plan = importlib.util.module_from_spec(plan_spec)
            plan_spec.loader.exec_module(plan)
            required = plan.scenes_for(required, args.shard, args.workers)
            runner_os = 'macOS' if sys.platform == 'darwin' else 'Windows' if sys.platform == 'win32' else 'Linux'
            budget = plan.budgets(inventory, args.shard, runner_os, os.environ.get('GUI_CI_SCENARIO', 'all'))
        manifest.write_text(json.dumps(dict(source=os.environ.get('GITHUB_SHA'), shard=args.shard,
            workers=args.workers, inventory=inventory, required=required, timeout_seconds=args.timeout,
            reader_decomposed=True, budgets=budget if args.shard is not None else None), indent=2) + '\n')
    except Exception as error:
        (args.output / 'collection.json').write_text(json.dumps(dict(required=required,
            results=[], complete=False, passed=False, error=str(error)), indent=2) + '\n')
        print(f'GUI registry discovery failed: {error}', file=sys.stderr)
        return 1
    return collect(entries, required,
                   args.assembly, args.output, args.timeout, args.allow_native_desktop)


if __name__ == '__main__':
    sys.exit(main())
