#!/usr/bin/env python3
"""Linux SDK-free packaged PluginSession E2E, on a private desktop with terminal fixture HTTP.

Requires an extracted original archive; copies it to a new location, binds it read-only,
then runs download/filter/archive and paused offline restore in separate apphost processes.
No build, live source request or physical desktop interaction is performed.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile

# Reuse the existing bounded archive/ownership and private desktop helpers, without
# redefining the provider-injected workflow's phases, checks or product switch.
import importlib.util
_spec = importlib.util.spec_from_file_location('provider_workflow', Path(__file__).with_name('apphost-workflow-smoke.py'))
workflow = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(workflow)
desktop = workflow.desktop
REQUESTS = {'https://lainchan.org/demo/res/123.json', 'https://kohlchan.net/demo/res/123.json',
            'https://lainchan.org/demo/src/1000-0.png', 'https://lainchan.org/demo/src/1001-1.png',
            'https://kohlchan.net/.media/landscape.png', 'https://kohlchan.net/.media/portrait.png'}
COMMON = {'delivered-plugins-activated', 'archive-verified', 'filter-match-and-no-match-durable',
          'normal-shutdown', 'lease-released'}


def sandbox(apphost, root, working):
    """Whitelist filesystem mounts: source tree, home, NuGet and optional SDK roots are absent."""
    command = ['bwrap', '--die-with-parent', '--unshare-net', '--ro-bind', '/usr', '/usr']
    for path in ('/lib', '/lib64', '/etc'):
        if Path(path).exists():
            command += ['--ro-bind', path, path]
    for path in ('/usr/share/dotnet', '/usr/lib/dotnet', '/usr/local/share/dotnet', '/usr/local/lib/dotnet'):
        if Path(path).exists():
            command += ['--tmpfs', path]
    command += ['--proc', '/proc', '--dev', '/dev', '--tmpfs', '/tmp',
                '--bind', str(root), str(root), '--ro-bind', str(root / 'package'), str(root / 'package'),
                '--ro-bind', '/tmp/.X11-unix', '/tmp/.X11-unix', '--chdir', str(working),
                '--setenv', 'PATH', '/no-sdk', '--setenv', 'HOME', '/no-home',
                '--setenv', 'DOTNET_ROOT', '/no-sdk', '--setenv', 'NUGET_PACKAGES', '/no-nuget',
                str(apphost), '--smoke-plugin-workflow']
    return command


def validate_metrics(metrics, phase, profile, working, scale, closure):
    required = COMMON | ({'real-source-and-filter-workers', 'delivered-compiler-uncached'} if phase == 'download' else {'paused-offline-restore'})
    if (metrics.get('schemaVersion') != 1 or metrics.get('completed') is not True
            or metrics.get('failure') is not None or metrics.get('phase') != phase
            or not required.issubset(metrics.get('checks', []))
            or metrics.get('profile') != str(profile) or metrics.get('workingDirectory') != str(working)
            or abs(metrics.get('renderScaling', 0) - scale) > .01
            or metrics.get('unexpectedRequests') != 0
            or not isinstance(metrics.get('revisions'), dict) or len(metrics['revisions']) != 5
            or not isinstance(metrics.get('processId'), int) or metrics['processId'] <= 0):
        raise ValueError('Product PluginSession evidence failed validation')
    requests = metrics.get('requests')
    if not isinstance(requests, list) or (phase == 'download' and (len(requests) != 6 or set(requests) != REQUESTS)) or (phase == 'restore' and requests):
        raise ValueError('Terminal fixture request inventory differs')
    return workflow.validate_loaded_assemblies(metrics, closure)


def run_phase(apphost, root, phase, destination, timeout, scale, closure):
    destination.mkdir()
    previous = root / 'profile-download' if phase == 'restore' else None
    descriptor, profile = workflow.create_descriptor(root, phase, scale, previous)
    working, environment = workflow.phase_environment(root, phase, profile, True, scale)
    # Keep only the explicit isolated GUI/runtime variables. All package helpers
    # inherit this environment and the same filesystem/network namespace.
    allowed = {'LANG', 'LC_ALL', 'TZ', 'MEOWCHAN_PROFILE', 'MEOWCHAN_DISABLE_UPDATE_CHECK',
               'AVALONIA_GLOBAL_SCALE_FACTOR', 'XDG_RUNTIME_DIR', 'XDG_CONFIG_HOME',
               'XDG_CACHE_HOME', 'XDG_DATA_HOME', 'MEOWCHAN_DESKTOP_SMOKE_XVFB', 'XDG_SESSION_TYPE'}
    environment = {key: value for key, value in environment.items() if key in allowed}
    environment['PATH'] = '/usr/bin:/bin'
    authority = root / ('xauthority-' + phase)
    authority.touch(mode=0o600)
    command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--',
               'xvfb-run', '--auth-file=' + str(authority), '--auto-servernum',
               '--server-args=-screen 0 1600x1000x24 -nolisten tcp', sys.executable,
               str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--',
               *sandbox(apphost, root, working), str(descriptor)]
    result = {'phase': phase, 'passed': False, 'command': command}
    shutil.copy2(descriptor, destination / 'descriptor.json')
    process = None
    try:
        with (destination / 'output.log').open('wb') as log:
            process = subprocess.Popen(command, cwd=working, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                result['exitCode'] = process.wait(timeout=timeout)
            finally:
                desktop.stop_owned_processes(process)
        if result['exitCode'] != 0:
            raise ValueError('Apphost failed; see output.log')
        if 'APPHOST PLUGIN WORKFLOW PASS' not in (destination / 'output.log').read_text(errors='replace'):
            raise ValueError('Apphost completion marker missing')
        source = workflow.owned_path(profile / 'apphost-workflow.json', root, exists=True)
        if not source.is_file() or source.stat().st_size > workflow.MAX_METRICS_BYTES:
            raise ValueError('Unbounded product evidence')
        shutil.copy2(source, destination / 'metrics.json')
        metrics = json.loads(source.read_text())
        result['managedAssemblyBindings'] = validate_metrics(metrics, phase, profile, working, scale, closure)
        result.update(passed=True, revisions=metrics['revisions'], processId=metrics['processId'])
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        source = profile / 'apphost-workflow.json'
        if source.is_file() and not source.is_symlink() and source.stat().st_size <= workflow.MAX_METRICS_BYTES:
            shutil.copy2(source, destination / 'metrics.json')
    (destination / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--archive-root', type=Path, required=True)
    parser.add_argument('--archive-member', required=True)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--timeout', type=float, default=120)
    parser.add_argument('--scale', type=float, choices=(1, 1.5, 2), default=1)
    args = parser.parse_args(argv)
    if not sys.platform.startswith('linux'):
        parser.error('This SDK-free desktop proof requires Linux namespaces')
    if not re.fullmatch('[0-9a-fA-F]{40}', args.source_commit) or not 0 < args.timeout <= 120:
        parser.error('Full source SHA and timeout in (0,120] required')
    missing = [tool for tool in (*desktop.ISOLATED_TOOLS, 'bwrap') if not shutil.which(tool)]
    if missing:
        parser.error('Isolation dependencies missing: ' + ', '.join(missing))
    apphost = args.apphost.resolve(strict=True)
    closure = workflow.archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    destination = args.evidence_dir.resolve(strict=True)
    report = {'schemaVersion': 1, 'passed': False, 'declaredSourceCommit': args.source_commit,
              'originalApphost': str(apphost), 'apphostSha256': workflow.sha256(apphost), 'phases': [],
              'sdkAccessible': False, 'nugetAccessible': False, 'networkAvailable': False,
              'installationReadOnly': True, 'relocated': True, 'isolatedDesktop': True,
              'scope': 'Linux packaged desktop PluginSession with terminal fixture HTTP; no live-source or physical-desktop claim.'}
    with tempfile.TemporaryDirectory(prefix='meowchan-workflow-plugin-') as temporary:
        root = Path(temporary).resolve(strict=True)
        workflow.ensure_disk_reserve(destination, root)
        (root / '.workflow-owner').write_text(os.urandom(16).hex())
        package = root / 'package'
        shutil.copytree(args.archive_root.resolve(strict=True), package, symlinks=True)
        workflow.ensure_disk_reserve(destination, root)
        closure = dict(closure, root=str(package))
        workflow.verify_extracted_closure(package, closure)
        relocated = package / apphost.relative_to(args.archive_root.resolve(strict=True))
        report['executedApphost'] = str(relocated)
        for phase in ('download', 'restore'):
            result = run_phase(relocated, root, phase, destination / phase, args.timeout, args.scale, closure)
            report['phases'].append(result)
            if not result['passed']:
                break
        if len(report['phases']) == 2 and all(item['passed'] for item in report['phases']):
            report['passed'] = (report['phases'][0]['revisions'] == report['phases'][1]['revisions']
                                and report['phases'][0]['processId'] != report['phases'][1]['processId'])
        closure['afterContentSha256'] = workflow.verify_extracted_closure(package, closure)
        closure['verifiedAfterRun'] = True
        report['archiveClosure'] = closure
    (destination / 'plugin-workflow.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': report['passed'], 'report': str(destination / 'plugin-workflow.json')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    sys.exit(main())
