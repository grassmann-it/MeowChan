#!/usr/bin/env python3
"""Frozen packaged production Host policy across four actual OS processes.

Caller holds /tmp/meowchan-ui-1.0-build.lock. No build, restore or network leg.
Uses the existing package closure/read-only sandbox and controlled PNG bytes.
"""
from __future__ import annotations
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile

_spec = importlib.util.spec_from_file_location('policy_plugin_workflow', Path(__file__).with_name('apphost-plugin-workflow-smoke.py'))
plugin = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(plugin)
workflow, desktop = plugin.workflow, plugin.desktop
PHASES = ('acquire', 'revoke', 'deny', 'recovery')
OWNER, SOURCE = 'meowchan.source.wizchan', 'wizchan'
THREAD = 'https://wizchan.org/demo/res/123.json'
ORIGINAL = 'https://wizchan.org/demo/src/1000-0.png'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def group_exists(pid):
    try:
        os.killpg(pid, 0)
        return True
    except ProcessLookupError:
        return False


def validate_metrics(data, phase, root, profile, working, scale, closure):
    require(data.get('schemaVersion') == 1 and data.get('completed') is True
            and data.get('failure') is None and data.get('phase') == phase
            and data.get('profile') == str(profile) and data.get('workingDirectory') == str(working)
            and data.get('ownerToken') == (root / '.workflow-owner').read_text()
            and data.get('normalShutdown') is True and data.get('profileLeaseReleased') is True
            and type(data.get('processId')) is int and data['processId'] > 0
            and abs(data.get('renderScaling', 0) - scale) < .01
            and data.get('unexpectedRequests') == 0, 'Production policy process evidence differs')
    require(data.get('requests') == ([THREAD, ORIGINAL, ORIGINAL] if phase == 'acquire' else []),
            'Strict/retry inventory or zero offline HTTP attempts differs')
    before, after = data.get('initialPolicy'), data.get('finalPolicy')
    for value, granted in ((before, phase == 'revoke'), (after, phase == 'acquire')):
        require(isinstance(value, dict) and value.get('IsActive') is True and value.get('IsGranted') is granted,
                'Fresh or final production policy authority differs')
        snapshot = value.get('Snapshot', {})
        require(snapshot.get('ProfileId') == str(profile) and snapshot.get('PluginOwnerId') == OWNER
                and snapshot.get('SourceId') == SOURCE and snapshot.get('AllowProviderSizeMd5Mismatch') is granted
                and type(snapshot.get('Revision')) is int, 'Profile/owner/source policy identity differs')
    proof = data.get('proof', {})
    require(proof.get('exactRestoreWithoutNewAcceptance') is True and isinstance(proof.get('binding'), dict)
            and proof.get('galleryAssetId') == proof['binding'].get('AssetId')
            and proof.get('currentAssetId') != proof['binding'].get('AssetId'), 'Exact historical/current asset proof missing')
    plugin_proof = data.get('pluginProof', {})
    require(plugin_proof.get('owner') == OWNER and plugin_proof.get('source') == SOURCE
            and all(re.fullmatch('[0-9a-fA-F]{64}', plugin_proof.get(key, ''))
                    for key in ('ExecutionRevision', 'assemblySha256', 'bundleManifestSha256')), 'Delivered source proof missing')
    worker_pids = data.get('workerPids')
    require(isinstance(worker_pids, list) and (bool(worker_pids) if phase == 'acquire' else not worker_pids)
            and all(type(pid) is int and pid > 0 and not Path('/proc', str(pid)).exists() for pid in worker_pids),
            'Production worker absent from acquisition or survived normal shutdown')
    return plugin.validate_assembly_bindings(data, closure)


def run_phase(apphost, root, phase, destination, timeout, scale, closure):
    destination.mkdir()
    descriptor, profile = root / 'download.json', root / 'profile-download'
    if phase == 'acquire':
        descriptor, profile = workflow.create_descriptor(root, 'download', scale, None)
    data = json.loads(descriptor.read_text())
    data['policyPhase'] = phase
    descriptor.write_text(json.dumps(data, indent=2) + '\n')
    working, environment = workflow.phase_environment(root, 'policy-' + phase, profile, True, scale)
    home = plugin.prepare_private_home(root, working)
    allowed = {'LANG', 'LC_ALL', 'TZ', 'MEOWCHAN_PROFILE', 'MEOWCHAN_DISABLE_UPDATE_CHECK', 'AVALONIA_GLOBAL_SCALE_FACTOR',
               'XDG_RUNTIME_DIR', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'XDG_DATA_HOME', 'MEOWCHAN_DESKTOP_SMOKE_XVFB', 'XDG_SESSION_TYPE'}
    environment = {key: value for key, value in environment.items() if key in allowed}
    environment.update(PATH='/usr/bin:/bin', HOME=str(home), TMPDIR=str(root.parent),
                       __EGL_VENDOR_LIBRARY_FILENAMES='/usr/share/glvnd/egl_vendor.d/50_mesa.json')
    authority = root / ('xauthority-policy-' + phase)
    authority.touch(mode=0o600)
    sandbox = plugin.sandbox(apphost, root, working)
    require(sandbox[-1] == '--smoke-plugin-workflow', 'Sandbox switch contract changed')
    sandbox[-1] = '--smoke-source-policy-process'
    command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--', 'xvfb-run', '--auth-file=' + str(authority),
               '--auto-servernum', '--server-args=-screen 0 1600x1000x24 -nolisten tcp', sys.executable,
               str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--', *sandbox, str(descriptor)]
    result = dict(phase=phase, passed=False, command=command)
    shutil.copy2(descriptor, destination / 'descriptor.json')
    try:
        with (destination / 'output.log').open('wb') as log:
            process = subprocess.Popen(command, cwd=working, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                result['exitCode'] = process.wait(timeout=timeout)
            finally:
                if group_exists(process.pid):
                    result['forcedCleanup'] = True
                    desktop.stop_owned_processes(process)
        require(result['exitCode'] == 0 and not result.get('forcedCleanup'), 'Apphost failed or needed forced cleanup')
        require('APPHOST SOURCE POLICY PROCESS PASS' in (destination / 'output.log').read_text(errors='replace'), 'Completion marker absent')
        require(not (home / '.dotnet/sdk').exists() and not (home / '.nuget/packages').exists(), 'Private HOME contains SDK/NuGet packages')
        source = workflow.owned_path(profile / 'apphost-policy-process.json', root, exists=True)
        require(source.is_file() and source.stat().st_size <= 65536, 'Unbounded production policy evidence')
        shutil.copy2(source, destination / 'metrics.json')
        data = json.loads(source.read_text())
        result['managedAssemblyBindings'] = validate_metrics(data, phase, root, profile, working, scale, closure)
        result.update(passed=True, processId=data['processId'], proof=data['proof'], pluginProof=data['pluginProof'],
                      initialPolicy=data['initialPolicy'], finalPolicy=data['finalPolicy'])
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        source = profile / 'apphost-policy-process.json'
        if source.is_file() and not source.is_symlink() and source.stat().st_size <= 65536:
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
    if not sys.platform.startswith('linux') or not re.fullmatch('[0-9a-fA-F]{40}', args.source_commit) or not 0 < args.timeout <= 120:
        parser.error('Linux, full source SHA and timeout in (0,120] required')
    missing = [name for name in (*desktop.ISOLATED_TOOLS, 'bwrap') if not shutil.which(name)]
    if missing:
        parser.error('Isolation dependencies missing: ' + ', '.join(missing))
    apphost = args.apphost.resolve(strict=True)
    closure = plugin.plugin_archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    destination = args.evidence_dir.resolve(strict=True)
    report = dict(schemaVersion=1, passed=False, declaredSourceCommit=args.source_commit, phases=[],
                  scope='Actual packaged production Host/config/plugin policy across four OS processes; controlled terminal HTTP and local exact bytes; no live or decoded-gallery proof.')
    try:
        with tempfile.TemporaryDirectory(prefix='meowchan-workflow-policy-') as temporary:
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
            grant_config = None
            for phase in PHASES:
                if phase == 'recovery':
                    require(grant_config is not None, 'Original granted config missing')
                    profile = root / 'profile-download'
                    (profile / 'settings.json.bak').write_bytes(grant_config)
                    (profile / 'settings.json').write_text('{ controlled corrupt current config')
                    report['recoveryInput'] = dict(currentCorrupt=True, backupFromAcquire=True,
                                                  backupSha256=workflow.sha256(profile / 'settings.json.bak'))
                result = run_phase(relocated, root, phase, destination / phase, args.timeout, args.scale, closure)
                report['phases'].append(result)
                if not result['passed']:
                    break
                if phase == 'acquire':
                    grant_config = (root / 'profile-download/settings.json').read_bytes()
            rows = report['phases']
            report['passed'] = (len(rows) == 4 and all(row['passed'] for row in rows)
                and len({row['processId'] for row in rows}) == 4
                and all(row['proof'] == rows[0]['proof'] and row['pluginProof'] == rows[0]['pluginProof'] for row in rows)
                and rows[1]['finalPolicy']['Snapshot']['Revision'] > rows[1]['initialPolicy']['Snapshot']['Revision']
                and rows[3]['initialPolicy']['Snapshot']['Revision'] == rows[0]['finalPolicy']['Snapshot']['Revision'] + 1)
            closure['afterContentSha256'] = workflow.verify_extracted_closure(package, closure)
            closure['verifiedAfterRun'] = True
            report['archiveClosure'] = closure
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report['error'] = str(error)
    (destination / 'source-policy-process.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(passed=report['passed'], report=str(destination / 'source-policy-process.json'))))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    sys.exit(main())
