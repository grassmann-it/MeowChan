#!/usr/bin/env python3
"""Frozen production Host policy across four actual OS processes.

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
import stat
import subprocess
import sys
import tempfile
import time

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


def build_closure(root, apphost):
    """Inventory a caller-frozen build without creating or claiming a delivery package."""
    root = Path(root)
    require(not root.is_symlink(), 'Build root must not be a symlink')
    root = root.resolve(strict=True)
    require(root.is_dir() and Path(apphost).resolve(strict=True).is_relative_to(root), 'Apphost must belong to build root')
    entries = [dict(path='.', kind='directory')]
    total = 0

    def visit(directory):
        nonlocal total
        with os.scandir(directory) as scan:
            for item in scan:
                path = Path(item.path)
                details = item.stat(follow_symlinks=False)
                name = path.relative_to(root).as_posix()
                kind = 'symlink' if stat.S_ISLNK(details.st_mode) else 'directory' if stat.S_ISDIR(details.st_mode) else 'file' if stat.S_ISREG(details.st_mode) else 'other'
                require(kind != 'other', 'Unsupported build closure entry: ' + name)
                record = dict(path=name, kind=kind)
                if kind == 'file':
                    require(details.st_size <= workflow.MAX_CLOSURE_FILE_BYTES, 'Build file exceeds closure bound')
                    total += details.st_size
                    require(total <= workflow.MAX_CLOSURE_BYTES, 'Build closure exceeds byte bound')
                    record.update(bytes=details.st_size, sha256=workflow.sha256(path))
                elif kind == 'symlink':
                    target = os.readlink(path)
                    workflow.validate_link_text(target)
                    workflow.confined_path(path, root)
                    record['target'] = target
                entries.append(record)
                require(len(entries) <= workflow.MAX_CLOSURE_ENTRIES, 'Build closure exceeds entry bound')
                if kind == 'directory':
                    visit(path)

    visit(root)
    entries.sort(key=lambda row: row['path'])
    expected = {row['path']: row for row in entries}
    member = Path(apphost).resolve(strict=True).relative_to(root).as_posix()
    require(expected.get(member, {}).get('kind') == 'file', 'Apphost must be a regular build member')
    bundle = plugin.bundle_inventory(Path(apphost))
    if bundle is None:
        for required in ('MeowChan.dll', 'MeowChan.deps.json', 'MeowChan.runtimeconfig.json', 'libmeowchan.dll'):
            path = workflow.confined_path(Path(apphost).parent / required, root)
            require(expected.get(path.relative_to(root).as_posix(), {}).get('kind') == 'file', 'Required managed build payload missing: ' + required)
    manifest = dict(entries=entries, contentSha256=workflow.records_sha256(entries), entryCount=len(entries),
        fileCount=sum(row['kind'] == 'file' for row in entries), verifiedBytes=total,
        archiveMetadataEntries=[], archiveMetadataSha256=workflow.records_sha256([]),
        root=str(root), apphostMember=member, bundle=bundle, verifiedBeforeRun=True, verifiedAfterRun=False,
        binding='frozen-build-directory', scope='Exact build directory bytes; no archive/package or SDK-free acceptance claim')
    workflow.verify_extracted_closure(root, manifest)
    return manifest


def build_sandbox(apphost, root, working):
    """Reuse the existing network/read-only sandbox; allow installed /usr runtime.

    Archive mode retains its stricter SDK-free runtime masks unchanged. This mode
    executes a framework-dependent build and makes no SDK-free/package claim.
    """
    original = plugin.sandbox(apphost, root, working)
    runtime_roots = {'/usr/share/dotnet', '/usr/lib/dotnet', '/usr/local/share/dotnet', '/usr/local/lib/dotnet'}
    command, index = [], 0
    while index < len(original):
        if original[index] == '--tmpfs' and index + 1 < len(original) and original[index + 1] in runtime_roots:
            index += 2
        elif original[index:index + 3] == ['--setenv', 'DOTNET_ROOT', '/no-sdk']:
            index += 3
        else:
            command.append(original[index]); index += 1
    return command


TERMINAL_PROCESS_STATES = {'Z', 'X', 'x'}


class ProcessIdentityError(ValueError):
    """Never signal a group whose original session/PID identity cannot be proved."""


def process_stat(pid, proc=Path('/proc')):
    try:
        text = (proc / str(pid) / 'stat').read_text()
    except FileNotFoundError:
        return None  # A process may normally exit while /proc is enumerated.
    opening, closing = text.find('('), text.rfind(')')
    require(opening > 0 and closing > opening, 'Malformed /proc process identity')
    fields = text[closing + 1:].split()
    require(len(fields) >= 20 and int(text[:opening].strip()) == pid, 'Truncated /proc process identity')
    return dict(pid=pid, name=text[opening + 1:closing], state=fields[0], parentPid=int(fields[1]),
                processGroupId=int(fields[2]), sessionId=int(fields[3]), startTimeTicks=int(fields[19]))


def group_inventory(supervisor, identities, proc=Path('/proc')):
    rows = []
    for directory in proc.iterdir():
        if not directory.name.isdecimal():
            continue
        row = process_stat(int(directory.name), proc)
        if row is None or row['processGroupId'] != supervisor['processGroupId']:
            continue
        pid, started = row['pid'], row['startTimeTicks']
        if (row['sessionId'] != supervisor['sessionId'] or started < supervisor['startTimeTicks']
                or pid in identities and identities[pid] != started):
            raise ProcessIdentityError('Owned process group session/PID identity changed: ' + json.dumps(row))
        identities[pid] = started
        try:
            row['executable'] = os.readlink(proc / str(pid) / 'exe')
        except FileNotFoundError:
            row['executable'] = None  # Expected for a zombie or a concurrently exiting process.
        except PermissionError:
            row['executable'] = 'unavailable'  # stat still supplies the kernel liveness/identity proof.
        row['terminal'] = row['state'] in TERMINAL_PROCESS_STATES
        rows.append(row)
    return sorted(rows, key=lambda row: row['pid'])


def observe_normal_group_exit(supervisor, deadline, result, identities):
    """No signals; the original phase deadline also bounds native helper shutdown."""
    audit = result.setdefault('processGroupExit', dict(supervisor=supervisor, observations=[], signalsSent=False))
    while True:
        rows = group_inventory(supervisor, identities)
        remaining = deadline - time.monotonic()  # Completion must be observed by the original deadline.
        observations = audit['observations']
        if 'finalMembers' not in audit or audit['finalMembers'] != rows:
            if len(observations) < 128:
                observations.append(dict(remainingSeconds=remaining, members=rows))
            else:
                audit['omittedIntermediateTransitions'] = audit.get('omittedIntermediateTransitions', 0) + 1
        audit['finalMembers'] = rows  # Always retain the complete final inventory, even after many state changes.
        audit['finalObservedRemainingSeconds'] = remaining
        live = [row for row in rows if not row['terminal']]
        if not live:
            if remaining < 0:
                audit['disposition'] = 'completion-observed-after-original-deadline'
                return False
            audit['disposition'] = 'terminated-zombies-only' if rows else 'empty'
            return True
        if remaining <= 0:
            audit['disposition'] = 'live-members-at-original-deadline'
            return False
        time.sleep(min(.05, remaining))


def cleanup_bound_group(process, supervisor, identities, result):
    """Failure cleanup only, with a fresh identity check before every group signal."""
    audit = result['processGroupExit']
    cleanup_deadline = time.monotonic() + 2  # Existing emergency-cleanup grace, never a PASS budget.
    for requested_signal in (signal.SIGTERM, signal.SIGKILL):
        rows = group_inventory(supervisor, identities)
        if not any(not row['terminal'] for row in rows):
            break
        try:
            os.killpg(supervisor['processGroupId'], requested_signal)
        except ProcessLookupError:
            break
        result['forcedCleanup'] = True
        audit['signalsSent'] = True
        audit.setdefault('cleanupSignals', []).append(dict(signal=requested_signal.name, members=rows))
        if requested_signal == signal.SIGTERM:
            while time.monotonic() < cleanup_deadline:
                process.poll()
                rows = group_inventory(supervisor, identities)
                if not any(not row['terminal'] for row in rows):
                    break
                time.sleep(min(.05, max(0, cleanup_deadline - time.monotonic())))
    process.wait(timeout=10)
    audit['afterCleanup'] = group_inventory(supervisor, identities)


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
    for value, granted, active in ((before, phase == 'revoke', phase != 'acquire'), (after, phase == 'acquire', True)):
        require(isinstance(value, dict) and value.get('IsActive') is active and value.get('IsGranted') is granted,
                'Fresh or final production policy authority differs')
        snapshot = value.get('Snapshot', {})
        require(snapshot.get('ProfileId') == str(profile) and snapshot.get('PluginOwnerId') == OWNER
                and snapshot.get('SourceId') == SOURCE and snapshot.get('AllowProviderSizeMd5Mismatch') is granted
                and type(snapshot.get('Revision')) is int, 'Profile/owner/source policy identity differs')
    validate_activation_metrics(data, phase, profile)
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


def validate_activation_metrics(data, phase, profile):
    """The same process proof now starts from host default-off, never implicit consent."""
    activation = data.get('activationEvidence')
    if phase == 'acquire':
        require(isinstance(activation, dict) and activation.get('status') == 'Succeeded'
                and activation.get('before') == data.get('initialPolicy')
                and activation.get('requestsBeforeAdmission') == 0 and activation.get('watchesBeforeAdmission') == 0,
                'Explicit combined consent or pre-admission no-work proof missing')
        before, after = activation['before'], activation.get('after', {})
        require(before.get('IsActive') is False and before.get('IsGranted') is False
                and after.get('IsActive') is True and after.get('IsGranted') is True,
                'Combined consent did not change fresh disabled/strict state')
        old, fresh = before.get('Snapshot', {}), after.get('Snapshot', {})
        require(fresh.get('ProfileId') == str(profile) and fresh.get('PluginOwnerId') == OWNER
                and fresh.get('SourceId') == SOURCE and fresh.get('AllowProviderSizeMd5Mismatch') is True
                and type(old.get('Revision')) is int and type(fresh.get('Revision')) is int
                and fresh['Revision'] >= old['Revision'] + 2,
                'Enable and grant did not use fresh revisions on the same authority')
    else:
        require(activation is None, 'A restart silently repeated combined activation')
    admission = data.get('urlAdmissionEvidence')
    if phase == 'deny':
        require(isinstance(admission, dict) and admission.get('url') == 'https://wizchan.org/demo/res/124.html'
                and admission.get('before') == admission.get('after') == data.get('finalPolicy')
                and admission['after'].get('IsActive') is True and admission['after'].get('IsGranted') is False
                and admission['after'].get('Snapshot', {}).get('AllowProviderSizeMd5Mismatch') is False
                and admission.get('watches') == 2 and admission.get('requests') == 0,
                'Further URL changed revoked policy, started work or was not admitted')
    else:
        require(admission is None, 'Unexpected further-URL admission phase')


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
    sandbox = (build_sandbox if closure.get('binding') == 'frozen-build-directory' else plugin.sandbox)(apphost, root, working)
    require(sandbox[-1] == '--smoke-plugin-workflow', 'Sandbox switch contract changed')
    sandbox[-1] = '--smoke-source-policy-process'
    command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--', 'xvfb-run', '--auth-file=' + str(authority),
               '--auto-servernum', '--server-args=-screen 0 1600x1000x24 -nolisten tcp', sys.executable,
               str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--', *sandbox, str(descriptor)]
    result = dict(phase=phase, passed=False, command=command)
    shutil.copy2(descriptor, destination / 'descriptor.json')
    try:
        with (destination / 'output.log').open('wb') as log:
            deadline = time.monotonic() + timeout
            process = subprocess.Popen(command, cwd=working, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            supervisor = process_stat(process.pid)
            require(supervisor is not None and supervisor['processGroupId'] == process.pid
                    and supervisor['sessionId'] == process.pid, 'Isolated supervisor identity missing')
            identities = {process.pid: supervisor['startTimeTicks']}
            try:
                result['exitCode'] = process.wait(timeout=max(0, deadline - time.monotonic()))
            finally:
                try:
                    normal = observe_normal_group_exit(supervisor, deadline, result, identities)
                    if not normal:
                        # Every remaining member was just rebound to this exact original session.
                        # A live survivor still fails, regardless of whether it is an app or helper.
                        result['normalGroupExitDeadlineExceeded'] = True
                        result['liveGroupAtDeadline'] = any(not row['terminal'] for row in result['processGroupExit']['finalMembers'])
                        cleanup_bound_group(process, supervisor, identities, result)
                except ProcessIdentityError:
                    result['cleanupWithheldForIdentityChange'] = True
                    raise
        require(result['exitCode'] == 0 and not result.get('forcedCleanup') and not result.get('normalGroupExitDeadlineExceeded'),
                'Apphost failed, exceeded normal group-exit deadline or needed forced cleanup')
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
    parser.add_argument('--archive', type=Path)
    parser.add_argument('--archive-root', type=Path)
    parser.add_argument('--archive-member')
    parser.add_argument('--build-root', type=Path, help='Caller-frozen build output; mutually exclusive with all archive arguments')
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--source-digest', help='Full frozen source digest; required for direct build output')
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
    if args.build_root:
        if any(value is not None for value in (args.archive, args.archive_root, args.archive_member)):
            parser.error('--build-root cannot be combined with archive arguments')
        if not args.source_digest or not re.fullmatch('[0-9a-fA-F]{64}', args.source_digest):
            parser.error('--build-root requires the full --source-digest from the integrated freeze receipt')
        closure = build_closure(args.build_root, apphost)
    else:
        if any(value is None for value in (args.archive, args.archive_root, args.archive_member)):
            parser.error('Provide --build-root or all three archive arguments')
        closure = plugin.plugin_archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
    input_root = Path(closure['root'])
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    destination = args.evidence_dir.resolve(strict=True)
    report = dict(schemaVersion=1, passed=False, declaredSourceCommit=args.source_commit, phases=[],
                  declaredSourceDigest=args.source_digest,
                  driverPath=str(Path(__file__).resolve()), driverSha256=workflow.sha256(Path(__file__).resolve()),
                  scope='Actual production Host/config/plugin policy across four OS processes; controlled terminal HTTP and local exact bytes; no live or decoded-gallery proof.'
                        + (' Frozen build output; no package or SDK-free claim.' if args.build_root else ' Verified existing delivery archive.'))
    try:
        with tempfile.TemporaryDirectory(prefix='meowchan-workflow-policy-') as temporary:
            root = Path(temporary).resolve(strict=True)
            workflow.ensure_disk_reserve(destination, root)
            (root / '.workflow-owner').write_text(os.urandom(16).hex())
            package = root / 'package'
            shutil.copytree(input_root, package, symlinks=True)
            workflow.ensure_disk_reserve(destination, root)
            closure = dict(closure, root=str(package))
            workflow.verify_extracted_closure(package, closure)
            relocated = package / apphost.relative_to(input_root)
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
            phases_passed = (len(rows) == 4 and all(row['passed'] for row in rows)
                and len({row['processId'] for row in rows}) == 4
                and all(row['proof'] == rows[0]['proof'] and row['pluginProof'] == rows[0]['pluginProof'] for row in rows)
                and rows[1]['finalPolicy']['Snapshot']['Revision'] > rows[1]['initialPolicy']['Snapshot']['Revision']
                and rows[3]['initialPolicy']['Snapshot']['Revision'] == rows[0]['finalPolicy']['Snapshot']['Revision'] + 1)
            closure['afterContentSha256'] = workflow.verify_extracted_closure(package, closure)
            closure['inputRootAfterContentSha256'] = workflow.verify_extracted_closure(input_root, closure)
            closure['verifiedAfterRun'] = True
            report['buildClosure' if args.build_root else 'archiveClosure'] = closure
            report['passed'] = phases_passed
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report['passed'] = False
        report['error'] = str(error)
    (destination / 'source-policy-process.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(passed=report['passed'], report=str(destination / 'source-policy-process.json'))))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    sys.exit(main())
