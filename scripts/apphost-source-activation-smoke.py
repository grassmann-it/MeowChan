#!/usr/bin/env python3
"""SDK-free, same-profile author .cs install/select, production, and offline restart proof."""
from __future__ import annotations
import argparse
import tempfile
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
_spec = importlib.util.spec_from_file_location('plugin_workflow', Path(__file__).with_name('apphost-plugin-workflow-smoke.py'))
plugin = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(plugin)
workflow, desktop = plugin.workflow, plugin.desktop
PHASES = ('install', 'acquire', 'offline')
# The author workflow retains its two production sources independently of PluginWorkflow.
AUTHOR_REQUESTS = {'https://lainchan.org/demo/res/123.json', 'https://kohlchan.net/demo/res/123.json',
                   'https://lainchan.org/demo/src/1000-0.png', 'https://lainchan.org/demo/src/1001-1.png',
                   'https://kohlchan.net/.media/landscape.png', 'https://kohlchan.net/.media/portrait.png'}
SOURCES = {'meowchan.source.lainchan': 'plugins/sources/Lainchan.cs',
           'meowchan.example.image-dimensions': 'plugins/sources/ImageDimensionsFilter.cs'}
PARAMETERS = {'minimumWidth': '5', 'minimumHeight': '0', 'orientation': 'landscape', 'aspectRatioTolerance': '0'}
TEMPLATE_CHECKS = {'dynamic-integer-error-no-rule-write', 'dynamic-form-real-rule-store',
                   'filter-only-template-preserves-explicit-zero-and-unset', 'preview-apply-explicit-rule-save'}


def validate_template(data):
    if (data.get('schemaVersion') != 1 or data.get('completed') is not True
            or not TEMPLATE_CHECKS.issubset(data.get('checks', []))
            or data.get('pluginId') != 'meowchan.example.image-dimensions'
            or data.get('filterKey') != 'dimensions' or data.get('schema') != '1'
            or data.get('ruleParameters') != PARAMETERS or data.get('templateParameters') != PARAMETERS
            or len({data.get(key) for key in ('initialRevision', 'formRevision', 'finalRevision')}) != 3
            or not all(isinstance(data.get(key), str) and data[key] for key in ('initialRevision', 'formRevision', 'finalRevision', 'templateId'))
            or not re.fullmatch('[0-9a-f]{64}', data.get('rulesSha256', ''))):
        raise ValueError('Actual form/template/rule evidence differs')


SELECTED_ADVISORY = 'Multiple candidates exist; the selected candidate is retained.'


def validate_startup(metrics, phase):
    startup = metrics.get('startup')
    if not isinstance(startup, dict) or startup.get('paused') is not True:
        raise ValueError('Source activation startup was not paused')
    rows = startup.get('plugins')
    if (not isinstance(rows, list) or len(rows) != 7
            or {row.get('pluginId') for row in rows} != set(metrics['revisions'])):
        raise ValueError('Source activation startup plugin inventory differs')
    expected = []
    for row in rows:
        if row.get('enabled') is not True or not row.get('activeCandidateId') or row.get('activeExecutionRevision') != metrics['revisions'][row['pluginId']]:
            raise ValueError('Source activation startup active inventory differs')
        if phase != 'install' and row['pluginId'] in SOURCES:
            if (row.get('problem') != SELECTED_ADVISORY or row.get('selectedCandidateId') != row['activeCandidateId']
                    or row.get('pendingCandidateId') is not None or len(row.get('presentCandidateIds', [])) != 2
                    or len(set(row['presentCandidateIds'])) != 2 or row['activeCandidateId'] not in row['presentCandidateIds']
                    or not row.get('activePluginName')):
                raise ValueError('Explicit author selection startup advisory differs')
            expected.append(row['activePluginName'] + ': ' + SELECTED_ADVISORY)
        elif row.get('problem') is not None:
            raise ValueError('Unexpected startup plugin problem')
    if (phase != 'install' and len(expected) != 2) or sorted(startup.get('warnings', [])) != sorted(expected):
        raise ValueError('Exact ID-bound author startup warning inventory differs')


def validate_metrics(metrics, phase, profile, working, scale, closure):
    required = {'author-normal-shutdown-and-lease'} | ({'author-source-installed', 'author-build-test-probe-isolated', 'author-explicit-pending-selection'}
        if phase == 'install' else {'author-source-cache-hit-on-restart', 'author-archive-verified', 'author-durable-filter-decisions',
        'author-production-workers-download-filter' if phase == 'acquire' else 'author-paused-offline-restart'})
    if (metrics.get('schemaVersion') != 1 or metrics.get('completed') is not True or metrics.get('failure') is not None
            or metrics.get('phase') != 'source-activation' or metrics.get('sourcePhase') != phase
            or not required.issubset(metrics.get('checks', [])) or metrics.get('profile') != str(profile)
            or metrics.get('workingDirectory') != str(working) or abs(metrics.get('renderScaling', 0) - scale) > .01
            or metrics.get('unexpectedRequests') != 0 or not isinstance(metrics.get('revisions'), dict) or set(metrics['revisions']) != plugin.PLUGIN_IDS
            or type(metrics.get('processId')) is not int or metrics['processId'] <= 0):
        raise ValueError('Source activation process evidence differs')
    validate_startup(metrics, phase)
    requests = metrics.get('requests')
    expected_requests = ['https://lainchan.org/demo/res/123.json'] if phase == 'install' else AUTHOR_REQUESTS if phase == 'acquire' else []
    if not isinstance(requests, list) or len(requests) != len(expected_requests) or set(requests) != set(expected_requests):
        raise ValueError('Source activation request inventory differs')
    candidates = metrics.get('candidates')
    if not isinstance(candidates, list) or len(candidates) != 2 or {row.get('pluginId') for row in candidates} != set(SOURCES):
        raise ValueError('Author candidate inventory differs')
    archived = {entry['path']: entry for entry in closure['entries']}
    for proof in candidates:
        source = SOURCES[proof['pluginId']]
        member = (Path(closure['apphostMember']).parent / source).as_posix()
        if (proof.get('deliveredSource') != source or proof.get('sourceSha256') != archived.get(member, {}).get('sha256')
                or not archived.get(member, {}).get('sha256') or proof.get('cacheHit') is not (phase != 'install')
                or any(not re.fullmatch('[0-9a-fA-F]{64}', proof.get(key, '')) for key in ('candidateId', 'sourceSha256', 'assemblySha256', 'executionRevision'))
                or not isinstance(proof.get('localSource'), str) or Path(proof['localSource']).is_absolute()
                or '..' in Path(proof['localSource']).parts):
            raise ValueError('Author source/cache proof differs from delivered archive')
        if phase == 'install':
            if proof.get('activeCandidateId') == proof['candidateId'] or proof.get('activeExecutionRevision') == proof['executionRevision']:
                raise ValueError('Build/test implicitly activated author code')
        elif (proof.get('activeCandidateId') != proof['candidateId'] or proof.get('activeExecutionRevision') != proof['executionRevision']
                or metrics['revisions'].get(proof['pluginId']) != proof['executionRevision']):
            raise ValueError('Startup did not activate explicitly selected author candidate')
    return plugin.validate_assembly_bindings(metrics, closure)


def validate_continuity(phases):
    if len(phases) != 3 or [row['phase'] for row in phases] != list(PHASES) or not all(row['passed'] for row in phases):
        return False
    if len({row['processId'] for row in phases}) != 3:
        return False
    keys = ('pluginId', 'deliveredSource', 'localSource', 'candidateId', 'sourceSha256', 'assemblySha256', 'executionRevision')
    def proofs(row):
        return sorted(tuple(proof[key] for key in keys) for proof in row['candidates'])
    return (proofs(phases[0]) == proofs(phases[1]) == proofs(phases[2])
            and phases[1]['revisions'] == phases[2]['revisions']
            and phases[1]['templateSha256'] == phases[2]['templateSha256'])


def run_phase(apphost, root, phase, destination, timeout, scale, closure):
    destination.mkdir()
    descriptor, profile = root / 'download.json', root / 'profile-download'
    if phase == 'install':
        descriptor, profile = workflow.create_descriptor(root, 'download', scale, None)
    data = json.loads(descriptor.read_text())
    data['sourcePhase'] = phase
    descriptor.write_text(json.dumps(data, indent=2) + '\n')
    working, environment = workflow.phase_environment(root, phase, profile, True, scale)
    home = plugin.prepare_private_home(root, working)
    allowed = {'LANG', 'LC_ALL', 'TZ', 'MEOWCHAN_PROFILE', 'MEOWCHAN_DISABLE_UPDATE_CHECK', 'AVALONIA_GLOBAL_SCALE_FACTOR',
               'XDG_RUNTIME_DIR', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'XDG_DATA_HOME', 'MEOWCHAN_DESKTOP_SMOKE_XVFB', 'XDG_SESSION_TYPE'}
    environment = {key: value for key, value in environment.items() if key in allowed}
    # Keep the app's owned-root guard consistent with Python's selected temporary parent.
    environment.update(PATH='/usr/bin:/bin', HOME=str(home), TMPDIR=str(root.parent), __EGL_VENDOR_LIBRARY_FILENAMES='/usr/share/glvnd/egl_vendor.d/50_mesa.json')
    authority = root / ('xauthority-' + phase)
    authority.touch(mode=0o600)
    sandbox = plugin.sandbox(apphost, root, working)
    if sandbox[-1] != '--smoke-plugin-workflow':
        raise ValueError('Unexpected sandbox switch contract')
    sandbox[-1] = '--smoke-source-activation'
    command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--', 'xvfb-run', '--auth-file=' + str(authority),
               '--auto-servernum', '--server-args=-screen 0 1600x1000x24 -nolisten tcp', sys.executable,
               str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--', *sandbox, str(descriptor)]
    result = {'phase': phase, 'passed': False, 'command': command, 'privateHome': str(home)}
    shutil.copy2(descriptor, destination / 'descriptor.json')
    try:
        with (destination / 'output.log').open('wb') as log:
            process = subprocess.Popen(command, cwd=working, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                result['exitCode'] = process.wait(timeout=timeout)
            finally:
                desktop.stop_owned_processes(process)
        if result['exitCode'] != 0 or 'APPHOST PLUGIN WORKFLOW PASS' not in (destination / 'output.log').read_text(errors='replace'):
            raise ValueError('Apphost failed or completion marker missing; see output.log')
        if (home / '.dotnet/sdk').exists() or (home / '.nuget/packages').exists():
            raise ValueError('Private HOME contains SDK/NuGet packages')
        result['privateHomeSdkAndNugetPackagesAbsent'] = True
        source = workflow.owned_path(profile / 'apphost-workflow.json', root, exists=True)
        if not source.is_file() or source.stat().st_size > workflow.MAX_METRICS_BYTES:
            raise ValueError('Unbounded source activation evidence')
        shutil.copy2(source, destination / 'metrics.json')
        metrics = json.loads(source.read_text())
        result['managedAssemblyBindings'] = validate_metrics(metrics, phase, profile, working, scale, closure)
        if phase != 'install':
            template = workflow.owned_path(profile / 'apphost-filter-template.json', root, exists=True)
            if not template.is_file() or template.stat().st_size > workflow.MAX_METRICS_BYTES:
                raise ValueError('Unbounded filter template evidence')
            validate_template(json.loads(template.read_text()))
            shutil.copy2(template, destination / template.name)
            result['templateSha256'] = workflow.sha256(template)
            if json.loads(template.read_text())['rulesSha256'] != workflow.sha256(profile / 'media_rules.json'):
                raise ValueError('Persisted rules differ from actual UI evidence')
        result.update(passed=True, revisions=metrics['revisions'], processId=metrics['processId'], candidates=metrics['candidates'])
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
    closure = plugin.plugin_archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
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
        for phase in PHASES:
            result = run_phase(relocated, root, phase, destination / phase, args.timeout, args.scale, closure)
            report['phases'].append(result)
            if not result['passed']:
                break
        report['passed'] = validate_continuity(report['phases'])
        closure['afterContentSha256'] = workflow.verify_extracted_closure(package, closure)
        closure['verifiedAfterRun'] = True
        report['archiveClosure'] = closure
    (destination / 'source-activation.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': report['passed'], 'report': str(destination / 'source-activation.json')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    sys.exit(main())
