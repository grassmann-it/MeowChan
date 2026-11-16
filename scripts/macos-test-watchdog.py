#!/usr/bin/env python3
"""Run both offline suites independently; preserve bounded timeout evidence."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

PROJECTS = (
    ('core', 'tests/MeowChan.Tests/MeowChan.Tests.csproj'),
    ('desktop', 'tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj'),
)

# Candidate validation uses the smallest evidence-based headroom over run
# 43806146605: macOS core reached 372.16s and desktop 788.56s while the
# completed desktop process exited 0 and cleanup found no live members. The
# budgets remain bounded and leave two minutes in the 22-minute workflow step
# for timeout sampling, process-group cleanup, and writing bounded evidence.
STANDARD_CORE_TIMEOUT = 300
STANDARD_DESKTOP_TIMEOUT = 600
CANDIDATE_CORE_TIMEOUT = 390
CANDIDATE_DESKTOP_TIMEOUT = 810

# Keep MSBuild single-node and let vstest terminate a genuinely stalled test host
# before the suite watchdog has to kill the whole process group. The latter is
# diagnostic/containment, not a suite timeout or a skipped-test mechanism.
DOTNET_TEST_OPTIONS = (
    '-m:1',
    '--blame-hang',
    '--blame-hang-timeout', '2min',
    '--blame-hang-dump-type', 'mini',
)


def processes(group):
    result = subprocess.run(['ps', '-axo', 'pid=,ppid=,pgid=,stat=,etime=,command='],
                            capture_output=True, text=True, timeout=5, check=True)
    rows = []
    for line in result.stdout.splitlines():
        fields = line.strip().split(None, 5)
        if len(fields) == 6 and fields[2] == str(group):
            rows.append((int(fields[0]), line, fields[5]))
    return rows


def kill_group(group, sig):
    if group <= 1 or group == os.getpgrp():
        raise ValueError('Refusing to signal the watchdog or a system process group')
    try:
        os.killpg(group, sig)
    except ProcessLookupError:
        pass
    except PermissionError as exc:
        # macOS can reject the group signal while its last member is exiting.
        # The caller must still verify that no live members remain.
        return dict(signal=sig.name, error=repr(exc))


def live_processes(group):
    # Defunct children cannot execute or retain the suite's resources. Their
    # eventual reaping belongs to their parent, which can have exited already.
    return [row for row in processes(group) if not row[1].split()[3].startswith('Z')]


def cleanup_group(child):
    errors = []
    for sig in (signal.SIGTERM, signal.SIGKILL):
        error = kill_group(child.pid, sig)
        if error:
            errors.append(error)
        try:
            child.wait(timeout=3)
        except subprocess.TimeoutExpired:
            pass
    remaining = []
    verified = False
    try:
        deadline = time.monotonic() + 3
        while True:
            remaining = live_processes(child.pid)
            if not remaining or time.monotonic() >= deadline:
                break
            time.sleep(0.05)
        verified = not remaining
    except (OSError, subprocess.SubprocessError) as exc:
        errors.append(dict(operation='verify-process-group', error=repr(exc)))
    return dict(complete=child.returncode is not None and verified,
                errors=errors, remaining=[row[1] for row in remaining])


def capture_timeout(group, output):
    try:
        rows = processes(group)
        (output / 'processes.txt').write_text('\n'.join(row[1] for row in rows) + '\n')
        # At most two short samples, restricted to this invocation's process group.
        candidates = sorted(rows, key=lambda row: ('testhost' not in row[2], row[0] != group))[:2]
        if sys.platform == 'darwin':
            for pid, _, _ in candidates:
                with (output / f'sample-{pid}-command.log').open('w') as log:
                    try:
                        subprocess.run(['/usr/bin/sample', str(pid), '3', '1', '-file',
                                        str(output / f'sample-{pid}.txt')],
                                       stdout=log, stderr=subprocess.STDOUT, timeout=8)
                    except subprocess.TimeoutExpired:
                        log.write('\nsample exceeded 8 seconds\n')
    except Exception as exc:
        (output / 'capture-error.txt').write_text(repr(exc))


def build_test_command(project, target):
    return ['dotnet', 'test', project, '--configuration', 'Release',
            '--no-build', '--no-restore', *DOTNET_TEST_OPTIONS,
            '--logger', 'trx', '--logger', 'console;verbosity=normal',
            '--diag', str(target / 'vstest-diag.log'),
            '--results-directory', str(target / 'results')]


def run_one(name, command, output, timeout):
    output.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    timed_out = False
    with (output / 'console.log').open('wb') as log:
        child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                 start_new_session=True)
        try:
            child.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            capture_timeout(child.pid, output)
        finally:
            # Also remove surviving children after an ordinary runner exit.
            cleanup = cleanup_group(child)
    result = dict(name=name, command=command, pid=child.pid, timeout=timed_out,
                  budget_seconds=timeout, exit_code=child.returncode,
                  seconds=round(time.monotonic() - started, 2), cleanup=cleanup)
    (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


def bound_evidence(output):
    # Coverage/gate evidence is never truncated. Bound only diagnostic logs/samples.
    limit = 8 * 1024 * 1024
    changes = []
    remaining = 64 * 1024 * 1024
    for path in sorted(output.rglob('*')):
        if not path.is_file() or path.suffix in ('.trx', '.json', '.runsettings'):
            continue
        original = path.stat().st_size
        allowance = min(limit, max(0, remaining))
        if original <= allowance:
            remaining -= original
            continue
        with path.open('rb') as stream:
            stream.seek(-allowance, os.SEEK_END) if allowance else stream.seek(0, os.SEEK_END)
            tail = stream.read()
        remaining -= len(tail)
        path.write_bytes(b'[Evidence truncated; see truncation.json]\n' + tail)
        changes.append(dict(file=str(path.relative_to(output)), original_bytes=original))
    (output / 'truncation.json').write_text(json.dumps(changes, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('artifacts/macos-test-diagnosis'))
    parser.add_argument('--candidate-validation', action='store_true',
                        help='Use the larger bounded budgets reserved for release candidates')
    parser.add_argument('--timeout', type=int,
                        help='Core-suite budget in seconds (profile default when omitted)')
    parser.add_argument('--desktop-timeout', type=int,
                        help='Desktop-suite budget in seconds (profile default when omitted)')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    core_limit = CANDIDATE_CORE_TIMEOUT if args.candidate_validation else STANDARD_CORE_TIMEOUT
    desktop_limit = CANDIDATE_DESKTOP_TIMEOUT if args.candidate_validation else STANDARD_DESKTOP_TIMEOUT
    core_timeout = args.timeout
    if core_timeout is None:
        core_timeout = core_limit if args.candidate_validation else STANDARD_CORE_TIMEOUT
    if not 1 <= core_timeout <= core_limit:
        parser.error(f'--timeout must be 1..{core_limit} seconds')
    if args.desktop_timeout is not None and not 1 <= args.desktop_timeout <= desktop_limit:
        parser.error(f'--desktop-timeout must be 1..{desktop_limit} seconds')
    desktop_timeout = args.desktop_timeout
    if desktop_timeout is None and args.candidate_validation:
        desktop_timeout = CANDIDATE_DESKTOP_TIMEOUT
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    results = []
    if args.self_test:
        # Exercise failure, timeout/group cleanup, then continuation, without dotnet.
        commands = [
            ('failure', [sys.executable, '-c', 'raise SystemExit(7)']),
            ('timeout', [sys.executable, '-c',
                         'import subprocess,sys,time; subprocess.Popen([sys.executable,"-c",'
                         '"import signal,time; signal.signal(signal.SIGTERM, signal.SIG_IGN); time.sleep(60)"]); time.sleep(60)']),
            ('success', [sys.executable, '-c', 'print("continued")']),
        ]
    else:
        commands = []
        for name, project in PROJECTS:
            target = output / name
            commands.append((name, build_test_command(project, target)))
    for name, command in commands:
        budget = desktop_timeout if name == 'desktop' and desktop_timeout is not None else core_timeout
        if args.self_test and name == 'timeout':
            # Keep the fixture quick while still forcing the timeout path for every profile.
            budget = 1
        print(f'Running {name}, watchdog {budget}s', flush=True)
        try:
            results.append(run_one(name, command, output / name, budget))
        except Exception as exc:
            results.append(dict(name=name, error=repr(exc)))
        print(json.dumps(results[-1]), flush=True)
    (output / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
    bound_evidence(output)
    if args.self_test:
        assert results[0]['exit_code'] == 7
        assert results[1]['timeout']
        assert results[1]['cleanup']['complete'], 'Timed-out process group survived cleanup'
        assert results[2]['exit_code'] == 0
        return 0
    return int(any(r.get('timeout') or r.get('exit_code') != 0
                   or not r.get('cleanup', {}).get('complete', False) for r in results))


if __name__ == '__main__':
    raise SystemExit(main())
