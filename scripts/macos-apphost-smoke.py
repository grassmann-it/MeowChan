#!/usr/bin/env python3
"""Run the actual extracted apphost with private state and a finite outer watchdog.

This is startup/shutdown evidence only, not VLC playback or Gatekeeper acceptance.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--evidence-dir', required=True, type=Path)
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('Native macOS is required')
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='meowchan-macos-startup-', dir=os.environ.get('RUNNER_TEMP')) as temporary:
        env = os.environ.copy()
        env.pop('OCHAN_PROFILE', None)
        env['MEOWCHAN_PROFILE'] = str(Path(temporary).resolve() / 'profile')
        with (args.evidence_dir / 'apphost-startup.log').open('w') as log:
            process = subprocess.Popen([str(args.apphost.resolve()), '--smoke-startup-seconds', '3'], env=env,
                                       stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            timed_out = False
            try:
                code = process.wait(timeout=60)
            except subprocess.TimeoutExpired:
                timed_out = True
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                code = 124
        output = (args.evidence_dir / 'apphost-startup.log').read_text(errors='replace')
        passed = code == 0 and 'APPHOST STARTUP PASS' in output and not timed_out
        report = {'passed': passed, 'exit_code': code, 'outer_timeout': timed_out,
                  'apphost': str(args.apphost), 'scope': 'actual extracted apphost startup/shutdown; no VLC playback or Gatekeeper claim'}
        (args.evidence_dir / 'apphost-startup.json').write_text(json.dumps(report, indent=2) + '\n')
        print(output)
        print(json.dumps(report))
        return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
