#!/usr/bin/env python3
"""Own one smoke child, observe its UI heartbeat from a separate process, bound evidence and cleanup.
Never attaches to an arbitrary PID. /proc stacks are kernel wait evidence, not managed UI stacks.
"""
from __future__ import annotations
import argparse
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
    temporary.replace(path)


def managed_stacks(pid, destination, tool):
    """Bound pipe draining in a reader thread: tool output cannot exhaust memory or fill disk."""
    import threading
    limit = 262144
    state = {'tool': str(tool), 'timeout_seconds': 5, 'max_bytes': limit, 'bytes_seen': 0, 'truncated': False}
    process = None
    try:
        path = Path(tool)
        if not path.is_absolute() or not path.is_file():
            return {'error': 'MEOWCHAN_DOTNET_STACK must name an existing absolute executable'}
        process = subprocess.Popen([str(path), 'report', '--process-id', str(pid)], stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, start_new_session=os.name != 'nt')
        def drain():
            with Path(destination).open('wb') as output:
                while chunk := process.stdout.read(4096):
                    remaining = max(0, limit - state['bytes_seen'])
                    output.write(chunk[:remaining])
                    state['bytes_seen'] += len(chunk)
                    if state['bytes_seen'] > limit:
                        state['truncated'] = True
        reader = threading.Thread(target=drain, daemon=True)
        reader.start()
        try:
            state['exit_code'] = process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            state['error'] = 'dotnet-stack exceeded 5-second capture deadline'
        finally:
            stop_owned(process)
            reader.join(timeout=1)
            if reader.is_alive():
                state['error'] = 'dotnet-stack output reader did not drain before deadline'
            process.stdout.close()
    except (OSError, subprocess.TimeoutExpired) as error:
        state['error'] = str(error)
    return state


def evidence(pid, destination, include_managed=False):
    result = {'pid': pid, 'kind': 'bounded OS thread/wait evidence; not managed stacks', 'threads': []}
    proc = Path('/proc') / str(pid)
    if proc.exists():
        for name in ('status', 'stat', 'io'):
            try:
                result[name] = (proc / name).read_text()[:16384]
            except OSError as error:
                result[name] = str(error)
        try:
            tasks = sorted((proc / 'task').iterdir(), key=lambda p: int(p.name))[:32]
            for task in tasks:
                thread = {'tid': task.name}
                for name in ('comm', 'wchan', 'stack', 'stat'):
                    try:
                        thread[name] = (task / name).read_text()[:4096]
                    except OSError as error:
                        thread[name] = str(error)
                result['threads'].append(thread)
        except OSError as error:
            result['error'] = str(error)
    elif sys.platform == 'darwin' and include_managed:
        try:
            sampled = subprocess.run(['/usr/bin/sample', str(pid), '1', '1'], capture_output=True,
                                     timeout=4, check=False)
            result['sample'] = sampled.stdout.decode(errors='replace')[:131072]
            result['sample_error'] = sampled.stderr.decode(errors='replace')[:4096]
        except (OSError, subprocess.TimeoutExpired) as error:
            result['error'] = str(error)
    else:
        result['limitation'] = 'OS thread stacks unavailable; harness resource/GC/threadpool samples are separate.'
    if include_managed:
        tool = os.environ.get('MEOWCHAN_DOTNET_STACK')
        result['managed'] = managed_stacks(pid, Path(destination).with_suffix('.managed.txt'), tool) if tool else {
            'limitation': 'MEOWCHAN_DOTNET_STACK is unset; managed UI/lock-owner stacks unavailable'}
    else:
        result['managed'] = {'limitation': 'No managed attach during primary timing; enabled only on failed/stalled control after timing has stopped'}
    atomic_json(destination, result)


def stop_owned(process):
    if os.name == 'nt':
        if process.poll() is None:
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], timeout=5,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=0.7)
        except (ProcessLookupError, subprocess.TimeoutExpired):
            pass
        finally:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    process.wait(timeout=5)


def supervise(command, heartbeat, report, timeout=60, startup=20, silence=10, trigger=1):
    started = time.monotonic()
    reason, detail, code = 'launch-error', None, 1
    process = None
    latest = None
    last_sequence = None
    last_answer = started
    last_producer = started
    last_modified = None
    seen = False
    captured = False
    try:
        process = subprocess.Popen(command, start_new_session=os.name != 'nt')
        while True:
            now = time.monotonic()
            exited = process.poll()
            if exited is not None:
                reason, code = 'exited', exited
                break
            if now - started >= timeout:
                reason, code = 'hard-deadline', 124
                break
            try:
                path = Path(heartbeat)
                metadata = path.stat()
                with path.open('rb') as source:
                    encoded = source.read(65537)
                if len(encoded) > 65536:
                    raise ValueError('Heartbeat exceeds bounded protocol')
                frame = json.loads(encoded)
                if not isinstance(frame, dict):
                    raise ValueError('Heartbeat must be an object')
                if metadata.st_mtime_ns != last_modified:
                    last_modified, last_producer = metadata.st_mtime_ns, now
                if frame.get('pid') != process.pid or type(frame.get('uiSequence')) is not int:
                    raise ValueError('Heartbeat PID/sequence mismatch')
                sequence = frame['uiSequence']
                if sequence < 0 or (last_sequence is not None and sequence < last_sequence):
                    raise ValueError('Heartbeat sequence went backwards')
                latest = frame
                # A producer that continues writing the same UI answer cannot conceal a stalled UI.
                if sequence > 0 and sequence != last_sequence:
                    last_answer, seen = now, True
                last_sequence = sequence
            except FileNotFoundError:
                pass
            except (OSError, ValueError, TypeError) as error:
                reason, detail, code = 'invalid-heartbeat', str(error), 125
                break
            age = now - last_answer
            if seen and age >= trigger and not captured:
                evidence(process.pid, Path(report).with_name('watchdog-threads.json'))
                captured = True
            if (seen and age >= silence) or (not seen and now - started >= startup):
                reason, code = ('heartbeat-producer-silence' if seen and now - last_producer >= silence else 'ui-silence' if seen else 'startup-silence'), 124
                break
            time.sleep(0.05)
        if code:
            failure_path = Path(report).with_name('watchdog-failure-threads.json')
            if process.poll() is None:
                evidence(process.pid, failure_path, include_managed=True)
            else:
                atomic_json(failure_path, {'pid': process.pid, 'limitation': 'Owned child exited; no attach to a possibly reused PID'})
            captured = True
    except (KeyboardInterrupt, InterruptedError):
        reason, code = 'interrupted', 130
    except OSError as error:
        detail = str(error)
    finally:
        if process is not None:
            try:
                stop_owned(process)
            except (OSError, subprocess.TimeoutExpired) as error:
                reason, detail, code = 'cleanup-error', str(error), 126
        atomic_json(report, {'reason': reason, 'exit_code': code, 'error': detail,
                             'child_pid': process.pid if process else None,
                             'elapsed_seconds': time.monotonic() - started, 'last_heartbeat': latest,
                             'thread_evidence_captured': captured})
    return code


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--heartbeat', required=True)
    parser.add_argument('--report', required=True)
    parser.add_argument('--timeout', type=float, default=60)
    parser.add_argument('--startup', type=float, default=20)
    parser.add_argument('--silence', type=float, default=10)
    parser.add_argument('--trigger', type=float, default=1)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    if any(not math.isfinite(v) or v <= 0 for v in (args.timeout, args.startup, args.silence, args.trigger)):
        parser.error('Deadlines must be finite and positive')
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('A child command is required after --')
    return supervise(command, args.heartbeat, args.report, args.timeout, args.startup, args.silence, args.trigger)


def interrupted(signum, frame):
    raise InterruptedError(f'Received signal {signum}')


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
