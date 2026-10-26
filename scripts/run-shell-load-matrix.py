#!/usr/bin/env python3
"""Serial source-built offline A/B measurements; applies no code, builds nothing, preserves all previous evidence.
Both prepared variants must contain the SAME PF00 harness. Root must verify the compiler provenance of their binaries.
Fresh process vs second navigation window are distinct; no OS-cache clearing or native Windows/USB claim.
"""
from __future__ import annotations
import argparse
from contextlib import contextmanager
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
import uuid

CASES = ('held-250', 'held-1000', 'paused', 'metadata', 'downloads')
HARNESS = ('Shared/PerformanceDiagnostics.cs', 'Shared/UiResponsivenessProbe.cs',
           'tests/MeowChan.DesktopSmoke/ShellLoadChecks.cs', 'tests/MeowChan.DesktopSmoke/ShellLoadFixture.cs',
           'tests/MeowChan.DesktopSmoke/ShellLoadMeasurement.cs', 'tests/MeowChan.DesktopSmoke/Program.cs',
           'tests/MeowChan.DesktopSmoke/ShellLoadSessionObservation.cs',
           'tests/MeowChan.DesktopSmoke/ISyntheticShellRows.cs')
EXCLUDED = {'.git', 'bin', 'obj', 'artifacts', '.architecture-sparring', 'docs', '.nuget', 'node_modules', '__pycache__'}
SUFFIXES = {'.cs', '.axaml', '.csproj', '.props', '.targets', '.json', '.py', '.sln', '.config'}


def digest(path):
    with Path(path).open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def source_inventory(root):
    files = {}
    for parent, directories, names in os.walk(root):
        directories[:] = sorted(name for name in directories if name not in EXCLUDED)
        for name in sorted(names):
            path = Path(parent) / name
            if path.suffix not in SUFFIXES and name not in {'.gitignore', 'NuGet.Config', 'global.json'}:
                continue
            if path.is_symlink():
                raise ValueError(f'Source binding refuses symlinks: {path}')
            files[path.relative_to(root).as_posix()] = digest(path)
    encoded = json.dumps(files, sort_keys=True, separators=(',', ':')).encode()
    return {'sha256': hashlib.sha256(encoded).hexdigest(), 'files': files}


def binary_inventory(assembly):
    # Bind loaded sibling project dependencies too, not only the smoke executable.
    return {path.relative_to(assembly.parent).as_posix(): digest(path) for path in sorted(assembly.parent.rglob('*'))
            if path.suffix in {'.dll', '.json', '.so', '.dylib'} and path.is_file()}


def plan(repetitions, cases):
    result = []
    for repetition in range(repetitions):
        # AB/BA for each condition, and reversed condition order every other repetition.
        for case in cases if repetition % 2 == 0 else tuple(reversed(cases)):
            for variant in ('baseline', 'candidate') if repetition % 2 == 0 else ('candidate', 'baseline'):
                result.append((repetition, case, variant))
    return result


def distribution(values):
    ordered = sorted(values)
    if not ordered:
        return {'count': 0}
    mean = sum(ordered) / len(ordered)
    return {'count': len(ordered), 'median': ordered[math.ceil(.5 * len(ordered)) - 1],
            'p95': ordered[math.ceil(.95 * len(ordered)) - 1], 'maximum': ordered[-1], 'mean': mean,
            'stddev': math.sqrt(sum((value - mean) ** 2 for value in ordered) / len(ordered))}


@contextmanager
def serialized_lock(path, already_held=False):
    if already_held:
        yield
        return
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as lock:
        if os.name == 'nt':
            import msvcrt
            lock.seek(0)
            if not lock.read(1):
                lock.write(b'0'); lock.flush()
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            try:
                yield
            finally:
                lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                yield
            finally:
                fcntl.flock(lock, fcntl.LOCK_UN)


def resource_deltas(samples, start=None, end=None):
    selected = [sample for sample in samples if (start is None or sample['Timestamp'] >= start)
                and (end is None or sample['Timestamp'] <= end)]
    if len(selected) < 2:
        return {'samples': len(selected)}
    first, last = selected[0], selected[-1]
    return {'samples': len(selected),
            'cpu_ms': last['CpuMilliseconds'] - first['CpuMilliseconds'],
            'allocated_bytes': last['AllocatedBytes'] - first['AllocatedBytes'],
            'gen0': last['Gen0'] - first['Gen0'], 'gen1': last['Gen1'] - first['Gen1'], 'gen2': last['Gen2'] - first['Gen2'],
            'peak_working_set_bytes': max(sample['WorkingSetBytes'] for sample in selected),
            'last_working_set_bytes': last['WorkingSetBytes'],
            'peak_private_bytes': max(sample['PrivateBytes'] for sample in selected),
            'peak_heap_bytes': max(sample['HeapBytes'] for sample in selected),
            'peak_os_threads': max(sample['OsThreads'] for sample in selected),
            'peak_handles': max(sample['Handles'] for sample in selected),
            'peak_pool_threads': max(sample['PoolThreads'] for sample in selected),
            'peak_pool_pending': max(sample['PoolPending'] for sample in selected),
            'pool_completed': last['PoolCompleted'] - first['PoolCompleted']}


def aggregate(runs):
    groups = {}
    for run in runs:
        measurement = run.get('measurement')
        if not measurement:
            continue
        for phase in measurement['phases']:
            key = (run['variant'], run['case'], phase['Name'])
            groups.setdefault(key, []).append(dict(phase, Resources=resource_deltas(measurement['resources'], phase['StartedAt'], phase['FinishedAt'])))
    return [{'variant': variant, 'case': case, 'phase': name,
             'max_ui_gap_ms': distribution([phase['MaxUiGapMilliseconds'] for phase in phases]),
             'navigation_median_ms': distribution([phase['NavigationMilliseconds']['Median'] for phase in phases]),
             'transfer_bytes_per_second': distribution([phase['TransferBytesPerSecond'] for phase in phases]),
             'commits_per_second': distribution([phase['CommitsPerSecond'] for phase in phases]),
             'cpu_ms': distribution([phase['Resources']['cpu_ms'] for phase in phases if 'cpu_ms' in phase['Resources']]),
             'allocated_bytes': distribution([phase['Resources']['allocated_bytes'] for phase in phases if 'allocated_bytes' in phase['Resources']]),
             'peak_working_set_bytes': distribution([phase['Resources']['peak_working_set_bytes'] for phase in phases if 'peak_working_set_bytes' in phase['Resources']]),
             'gen2': distribution([phase['Resources']['gen2'] for phase in phases if 'gen2' in phase['Resources']]),
             'peak_pool_pending': distribution([phase['Resources']['peak_pool_pending'] for phase in phases if 'peak_pool_pending' in phase['Resources']])}
            for (variant, case, name), phases in sorted(groups.items())]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for variant in ('baseline', 'candidate'):
        parser.add_argument('--' + variant + '-root', type=Path, required=True)
        parser.add_argument('--' + variant + '-assembly', type=Path, required=True)
    parser.add_argument('--baseline-description', required=True, help='Exact baseline provenance, including harness/constructor instrumentation and any partial fixes.')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--case', choices=CASES, action='append')
    parser.add_argument('--timeout', type=float, default=90)
    parser.add_argument('--lock', type=Path, default=Path('/tmp/meowchan-ui-1.0-build.lock'))
    parser.add_argument('--lock-held', action='store_true', help='Build owner already holds --lock for this invocation.')
    parser.add_argument('--allow-native-desktop', action='store_true')
    parser.add_argument('--native-display', action='store_true')
    parser.add_argument('--window-manager', choices=('openbox', 'kwin', 'kwin-nested'), default='openbox')
    parser.add_argument('--dry-run', action='store_true', help='Prepare immutable inventory and plan; launch no runtime.')
    args = parser.parse_args(argv)
    if not 1 <= args.repetitions <= 20 or not math.isfinite(args.timeout) or args.timeout < 10:
        parser.error('Repetitions must be 1..20 and timeout finite and >= 10 seconds')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    variants = {}
    for name in ('baseline', 'candidate'):
        root = getattr(args, name + '_root').resolve(strict=True)
        assembly = getattr(args, name + '_assembly').resolve(strict=True)
        source = source_inventory(root)
        missing = [path for path in HARNESS if path not in source['files']]
        if missing:
            raise ValueError(f'{name}: prepare frozen PF00 harness in source copy first: {missing}')
        variants[name] = {'root': str(root), 'assembly': str(assembly), 'source': source,
                          'binary_files': binary_inventory(assembly), 'assembly_sha256': digest(assembly),
                          'harness': {path: source['files'][path] for path in HARNESS}}
    if variants['baseline']['harness'] != variants['candidate']['harness']:
        raise ValueError('A/B requires byte-identical PF00 harness sources in both prepared variants')
    sequence = plan(args.repetitions, tuple(dict.fromkeys(args.case or CASES)))
    manifest = {'schema': 1, 'baseline_description': args.baseline_description, 'variants': variants, 'sequence': sequence,
                'binding_limit': 'Source and binary inventories at execution time; root verifies build receipts. No relabeling of historical reports.',
                'warmth': 'Fresh process: first measured navigation window after initial target pixel control. Warm: subsequent same-process window; no OS-cache claim.'}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    if args.dry_run:
        return 0
    runner = Path(__file__).with_name('run-desktop-smoke.py').resolve()
    results = []
    interrupted = False
    fixture_hash = None
    with serialized_lock(args.lock, args.lock_held):
        for number, (repetition, case, variant) in enumerate(sequence):
            destination = output / f'{number:03d}-{variant}-{case}-r{repetition + 1}'
            binding = variants[variant]
            environment = os.environ.copy()
            environment.pop("MEOWCHAN_SHELL_LOAD_WATCHDOG_STALL", None)
            scenario = 'held' if case.startswith('held-') else case
            environment.update(MEOWCHAN_SHELL_LOAD_SOURCE_ROOT=binding['root'],
                               MEOWCHAN_SHELL_LOAD_SCENARIO=scenario,
                               MEOWCHAN_SHELL_LOAD_HOLD_MS=case.split('-')[1] if scenario == 'held' else '1000',
                               MEOWCHAN_SHELL_LOAD_SOURCE_HASH=binding['source']['sha256'],
                               MEOWCHAN_SHELL_LOAD_ASSEMBLY_HASH=binding['assembly_sha256'],
                               MEOWCHAN_SHELL_LOAD_RUN_ID=str(uuid.uuid4()))
            command = [sys.executable, str(runner), '--assembly', binding['assembly'], '--output', str(destination),
                       '--mode', 'shell-load', '--timeout', str(args.timeout), '--window-manager', args.window_manager]
            if args.allow_native_desktop:
                command.append('--allow-native-desktop')
            if args.native_display:
                command.append('--native-display')
            start = time.monotonic()
            # Recheck both source and binary inventories before EVERY run; no accidental mixed build evidence.
            if source_inventory(Path(binding['root'])) != binding['source'] or binary_inventory(Path(binding['assembly'])) != binding['binary_files']:
                raise ValueError(f'{variant} source/binaries changed during matrix')
            process = None
            code = 1
            with (output / f'{number:03d}-runner.log').open('w') as log:
                try:
                    process = subprocess.Popen(command, env=environment, stdout=log, stderr=subprocess.STDOUT,
                                               start_new_session=os.name != 'nt')
                    code = process.wait(timeout=args.timeout + 15)
                except (KeyboardInterrupt, InterruptedError, subprocess.TimeoutExpired):
                    interrupted = True
                finally:
                    if process is not None and process.poll() is None:
                        # Runner handles SIGTERM by cleaning its display/session, whose watchdog cleans the child.
                        if os.name == 'nt':
                            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], timeout=10, check=False)
                        else:
                            os.killpg(process.pid, signal.SIGTERM)
                        try:
                            process.wait(timeout=12)
                        except subprocess.TimeoutExpired:
                            if os.name != 'nt':
                                os.killpg(process.pid, signal.SIGKILL)
                            process.wait(timeout=5)
            measurement_path = destination / 'shell-load' / 'measurement.json'
            measurement = json.loads(measurement_path.read_text()) if measurement_path.exists() else None
            error = None
            if measurement:
                observed = measurement['fixtureHash']
                if fixture_hash is None:
                    fixture_hash = observed
                if observed != fixture_hash:
                    error = 'Fixture hash differs across A/B runs'
                if measurement['sourceHash'] != binding['source']['sha256'] or measurement['assemblyHash'] != binding['assembly_sha256']:
                    error = 'Measurement provenance mismatch'
            else:
                error = 'No complete measurement; inspect watchdog and progress evidence'
            passed = code == 0 and error is None and measurement.get('failure') is None
            results.append({'number': number, 'repetition': repetition + 1, 'case': case, 'variant': variant,
                            'passed': passed, 'exit_code': code, 'error': error, 'elapsed_seconds': time.monotonic() - start,
                            'resource_summary': resource_deltas(measurement['resources']) if measurement else None,
                            'measurement': measurement})
            # Failed baseline characterization is preserved, then candidate and other cells still run.
            (output / 'matrix.json').write_text(json.dumps({'schema': 1, 'complete': len(results) == len(sequence),
                'interrupted': interrupted, 'fixture_hash': fixture_hash, 'runs': results, 'aggregate': aggregate(results)}, indent=2) + '\n')
            print(f'{number + 1}/{len(sequence)} {variant} {case}: {"passed" if passed else "FAILED"}', flush=True)
            if interrupted:
                break
    return 0 if len(results) == len(sequence) and all(run['passed'] for run in results) else 1


def interrupted(signum, frame):
    raise InterruptedError(f'Received signal {signum}')


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
