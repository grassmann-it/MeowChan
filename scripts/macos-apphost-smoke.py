#!/usr/bin/env python3
"""Run the actual extracted apphost with private state and a finite outer watchdog.

This is startup/shutdown evidence only, not VLC playback or Gatekeeper acceptance.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile


boundary_spec = importlib.util.spec_from_file_location('macos_acceptance_boundary', Path(__file__).with_name('apphost-acceptance-boundary.py'))
boundary = importlib.util.module_from_spec(boundary_spec)
boundary_spec.loader.exec_module(boundary)

def main(argv=None) -> int:
    if boundary.dry_run(argv, __file__):
        return 0
    parser = argparse.ArgumentParser(description=__doc__ + '\nRequires a NON-SHIPPING Debug apphost. Use APPHOST --dry-run for a routing-only check.')
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--evidence-dir', required=True, type=Path)
    args = parser.parse_args(argv)
    if sys.platform != 'darwin':
        parser.error('Native macOS is required')
    boundary.require_debug(args.apphost, __file__)
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
        report = {'acceptanceConfiguration': 'Debug', 'shippingArtifactExecuted': False, 'passed': passed, 'exit_code': code, 'outer_timeout': timed_out,
                  'apphost': str(args.apphost), 'scope': 'actual extracted apphost startup/shutdown; no VLC playback or Gatekeeper claim'}
        (args.evidence_dir / 'apphost-startup.json').write_text(json.dumps(report, indent=2) + '\n')
        print(output)
        print(json.dumps(report))
        return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
