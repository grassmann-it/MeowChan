#!/usr/bin/env python3
"""Check a real Linux apphost's video-preflight error UI on a private desktop.

The expected error must come from the actual package/system environment. This
runner does not replace preflight, install VLC, build the app, or access HTTP.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time


spec = importlib.util.spec_from_file_location('video_error_workflow_helpers', Path(__file__).with_name('apphost-workflow-smoke.py'))
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)
desktop = workflow.desktop
KINDS = ('SystemBackendUnavailable', 'SystemBackendUnsupportedVersion', 'BundleDamaged', 'InvalidPackageMetadata')
CHECKS = frozenset(('preflight-real', 'error-window-visible', 'localized-hint', 'exit-button', 'session-not-created'))
PASS_MARKER = 'APPHOST VIDEO ERROR PASS'
MAX_TIMEOUT = 120
MAX_LOG_BYTES = 4 * 1024 ** 2
MAX_JSON_BYTES = 256 * 1024
MAX_PNG_BYTES = 16 * 1024 ** 2


def reserve(path, additional=0):
    if shutil.disk_usage(path).free < workflow.RESERVE_BYTES + additional:
        raise ValueError('Video-error evidence would cross the 2 GiB disk reserve')


def inspect_runtime(apphost):
    with apphost.open('rb') as stream:
        header = stream.read(20)
    if len(header) != 20 or header[:4] != b'\x7fELF' or header[4] != 2 or header[5] not in (1, 2):
        raise ValueError('Expected a 64-bit Linux ELF apphost')
    machine = struct.unpack('<H' if header[5] == 1 else '>H', header[18:20])[0]
    runtime = {62: 'linux-x64', 183: 'linux-arm64'}.get(machine)
    if runtime is None:
        raise ValueError('Unsupported Linux apphost ELF architecture')
    return runtime


def private_command(apphost, kind, root):
    return ['dbus-run-session', '--config-file=' + str(root / 'bus.conf'), '--',
            'xvfb-run', '--auto-servernum', '--server-args=-screen 0 3200x2000x24 -nolisten tcp',
            sys.executable, str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()),
            '--', str(apphost), '--smoke-video-error', kind]


class BoundedOutput:
    """Drain the child pipe while retaining at most a small diagnostic log."""
    def __init__(self, stream, destination):
        self.stream, self.destination = stream, destination
        self.truncated, self.error = False, None
        self.thread = threading.Thread(target=self.collect, daemon=True)
        self.thread.start()

    def collect(self):
        retained = 0
        try:
            with self.destination.open('xb') as output:
                for chunk in iter(lambda: self.stream.read(65536), b''):
                    keep = chunk[:max(0, MAX_LOG_BYTES - retained)]
                    if keep:
                        reserve(self.destination.parent, len(keep))
                        output.write(keep)
                        retained += len(keep)
                    if len(keep) != len(chunk):
                        self.truncated = True
        except (OSError, ValueError) as error:
            self.error = str(error)
        finally:
            self.stream.close()

    def finish(self):
        self.thread.join(timeout=5)
        if self.thread.is_alive():
            raise ValueError('Child output pipe did not close after owned-process cleanup')
        if self.error:
            raise ValueError('Output capture failed: ' + self.error)
        if self.truncated:
            raise ValueError('Child output exceeded the bounded diagnostic log')


def validate_metrics(metrics, kind, root, profile):
    if not isinstance(metrics, dict) or type(metrics.get('schemaVersion')) is not int or metrics['schemaVersion'] != 1:
        raise ValueError('Unsupported video-error metrics schema')
    if metrics.get('completed') is not True or 'failure' not in metrics or metrics['failure'] is not None:
        raise ValueError('Product did not complete the real video-error UI check')
    if metrics.get('expectedKind') != kind or metrics.get('observedKind') != kind:
        raise ValueError('Actual video-preflight error did not match the expected kind')
    checks = metrics.get('checks')
    if not isinstance(checks, list) or any(not isinstance(check, str) for check in checks) or not CHECKS.issubset(checks):
        raise ValueError('Product omitted required video-error UI/session checks')
    for field in ('windowVisible', 'profileAbsent', 'normalExitButtonClicked'):
        if metrics.get(field) is not True:
            raise ValueError('Video-error UI evidence missing: ' + field)
    if type(metrics.get('exitCode')) is not int or metrics['exitCode'] != 2:
        raise ValueError('Product did not report the normal error-dialog exit code')
    if type(metrics.get('processId')) is not int or metrics['processId'] <= 0:
        raise ValueError('Product process identity missing')
    if metrics.get('workingDirectory') != str(root) or metrics.get('profile') != str(profile):
        raise ValueError('Product did not use the owned working directory/profile')
    for field in ('assemblyInformationalVersion', 'message'):
        if not isinstance(metrics.get(field), str) or not metrics[field].strip():
            raise ValueError('Product evidence missing: ' + field)
    scaling = metrics.get('renderScaling')
    if type(scaling) not in (int, float) or not math.isfinite(scaling) or not math.isclose(scaling, 1, abs_tol=0.01):
        raise ValueError('Private desktop render scale was not observed')


def profile_artifacts(root, profile):
    allowed = {profile.name + '.video-error.json', profile.name + '.video-error.png'}
    return sorted(path.name for path in root.iterdir() if path.name.startswith(profile.name) and path.name not in allowed)


def retain_sidecars(root, profile, destination):
    for suffix, name, limit in [('.video-error.json', 'metrics.json', MAX_JSON_BYTES),
                                 ('.video-error.png', 'error.png', MAX_PNG_BYTES)]:
        source = Path(str(profile) + suffix)
        if not source.exists() and not source.is_symlink():
            continue
        workflow.owned_path(source, root, exists=True)
        if not source.is_file() or not 0 < source.stat().st_size <= limit:
            raise ValueError('Video-error sidecar is not a bounded regular file: ' + name)
        reserve(destination, source.stat().st_size)
        shutil.copy2(source, destination / name)


def run_check(apphost, kind, destination, timeout):
    destination.mkdir()
    result = {'passed': False, 'expectedKind': kind, 'exitCode': None, 'timedOut': False,
              'interrupted': False, 'profileAbsent': True, 'unexpectedProfileArtifacts': []}
    with tempfile.TemporaryDirectory(prefix='meowchan-video-error-') as temporary:
        root = Path(temporary).resolve(strict=True)
        environment = desktop.prepare_environment(dict(os.environ), True, root)
        environment['AVALONIA_GLOBAL_SCALE_FACTOR'] = '1'
        profile = Path(environment['MEOWCHAN_PROFILE'])
        command = private_command(apphost, kind, root)
        result.update(workingDirectory=str(root), profile=str(profile), command=command)
        process, capture = None, None
        try:
            reserve(root)
            reserve(destination)
            if profile_artifacts(root, profile):
                raise ValueError('Video-error profile must start nonexistent')
            process = subprocess.Popen(command, cwd=root, env=environment, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            result['runnerProcessId'] = process.pid
            capture = BoundedOutput(process.stdout, destination / 'output.log')
            deadline = time.monotonic() + timeout
            while True:
                reserve(root)
                reserve(destination)
                unexpected = profile_artifacts(root, profile)
                if unexpected:
                    result.update(profileAbsent=False, unexpectedProfileArtifacts=unexpected)
                    raise ValueError('Video-error startup wrote profile/session artifacts')
                if capture.truncated or capture.error:
                    raise ValueError('Video-error output exceeded its capture limits or failed')
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    result['timedOut'] = True
                    raise ValueError('Video-error apphost timed out')
                try:
                    result['exitCode'] = process.wait(timeout=min(remaining, .1))
                    break
                except subprocess.TimeoutExpired:
                    continue
        except (KeyboardInterrupt, InterruptedError):
            result.update(interrupted=True, error='Runner interrupted')
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            result['error'] = str(error)
        finally:
            try:
                if process is not None:
                    desktop.stop_owned_processes(process)
                if capture is not None:
                    capture.finish()
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                result['error'] = result.get('error', '') + ' Cleanup/capture: ' + str(error)
            finally:
                try:
                    unexpected = profile_artifacts(root, profile)
                    if unexpected:
                        result.update(profileAbsent=False, unexpectedProfileArtifacts=unexpected,
                                      error='Video-error startup wrote profile/session artifacts')
                    retain_sidecars(root, profile, destination)
                except (OSError, ValueError) as error:
                    result['error'] = result.get('error', '') + ' Evidence collection: ' + str(error)
        if 'error' not in result:
            try:
                if result['exitCode'] != 2:
                    raise ValueError('Expected actual desktop exit code 2')
                if PASS_MARKER not in (destination / 'output.log').read_text(encoding='utf-8', errors='replace').splitlines():
                    raise ValueError('Exact video-error completion marker missing')
                metrics = json.loads((destination / 'metrics.json').read_text(encoding='utf-8'))
                validate_metrics(metrics, kind, root, profile)
                result['screenshot'] = workflow.playback.validate_png(destination / 'error.png')
                result.update(passed=True, observedKind=metrics['observedKind'], checks=metrics['checks'],
                              productProcessId=metrics['processId'], assemblyInformationalVersion=metrics['assemblyInformationalVersion'],
                              message=metrics['message'], renderScaling=metrics['renderScaling'])
            except (OSError, ValueError, KeyError, TypeError) as error:
                result['error'] = str(error)
    (destination / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--expected-kind', choices=KINDS, required=True)
    parser.add_argument('--runtime', choices=('linux-x64', 'linux-arm64'), required=True)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--expected-apphost-sha256', required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--archive', type=Path)
    parser.add_argument('--archive-member')
    parser.add_argument('--timeout', type=float, default=MAX_TIMEOUT)
    args = parser.parse_args(argv)
    if not sys.platform.startswith('linux'):
        parser.error('Video-error checks require Linux with a private desktop')
    if not re.fullmatch('[0-9a-fA-F]{40}', args.source_commit):
        parser.error('--source-commit must be a full 40-character SHA')
    if not re.fullmatch('[0-9a-fA-F]{64}', args.expected_apphost_sha256):
        parser.error('--expected-apphost-sha256 must be a 64-character SHA256')
    if not math.isfinite(args.timeout) or not 0 < args.timeout <= MAX_TIMEOUT:
        parser.error('--timeout must be finite, positive, and at most 120 seconds')
    if (args.archive is None) != (args.archive_member is None):
        parser.error('--archive and --archive-member must be supplied together')
    if args.archive_member is not None:
        try:
            workflow.normalized_member(args.archive_member)
        except ValueError as error:
            parser.error(str(error))
    missing = [tool for tool in desktop.ISOLATED_TOOLS if not shutil.which(tool)]
    if missing:
        parser.error('Private desktop dependencies missing: ' + ', '.join(missing))
    return args


def main(argv=None):
    args = parse_args(argv)
    apphost = args.apphost.resolve(strict=True)
    observed_hash = workflow.sha256(apphost)
    if observed_hash != args.expected_apphost_sha256.lower():
        raise ValueError('Supplied apphost does not match its expected SHA256')
    observed_runtime = inspect_runtime(apphost)
    if observed_runtime != args.runtime:
        raise ValueError('Supplied apphost ELF architecture does not match its expected runtime')
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    destination = args.evidence_dir.resolve(strict=True)
    reserve(destination)
    report = {'schemaVersion': 1, 'passed': False, 'completed': False, 'apphost': str(apphost),
              'expectedKind': args.expected_kind, 'expectedApphostSha256': args.expected_apphost_sha256.lower(),
              'observedApphostSha256': observed_hash, 'declaredSourceCommit': args.source_commit.lower(),
              'declaredRuntime': args.runtime, 'observedRuntime': observed_runtime,
              'host': platform.platform(), 'machine': platform.machine(), 'isolatedDesktop': True,
              'minimumDiskReserveBytes': workflow.RESERVE_BYTES, 'timeoutSeconds': args.timeout,
              'scope': 'Real Linux video preflight and StartupErrorWindow, native Exit button, expected desktop exit2; '
                       'private display/bus/runtime, no profile/session creation. Source commit is declared packaging provenance.'}
    if args.archive is not None:
        report['archiveBinding'] = workflow.archive_binding(args.archive, args.archive_member, observed_hash)
    report_path = destination / 'video-error.json'
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    report['result'] = run_check(apphost, args.expected_kind, destination / 'check', args.timeout)
    report['apphostSha256After'] = workflow.sha256(apphost)
    report['passed'] = report['result']['passed'] and report['apphostSha256After'] == observed_hash
    if report['apphostSha256After'] != observed_hash:
        report['error'] = 'Apphost changed during video-error verification'
    report['observedKind'] = report['result'].get('observedKind')
    report['completed'] = True
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    raise SystemExit(main())
