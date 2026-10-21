#!/usr/bin/env python3
"""Exercise an extracted apphost's offline desktop workflow in fresh processes.

Linux always uses a private display, window manager and session bus. Other
platforms require a disposable desktop and explicit opt-in. This runner neither
builds the application nor starts browsers/file managers. The product intercepts
external handoff and HTTP at their boundaries; native clipboard remains real.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile


def load_helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


desktop = load_helper('workflow_desktop_runner', 'run-desktop-smoke.py')
playback = load_helper('workflow_playback_runner', 'apphost-playback-smoke.py')
PHASES = ('download', 'restore', 'removed', 'shutdown')
CHECKS = {
    'download': {'download-paused', 'download-resumed', 'download-verified', 'native-clipboard-roundtrip',
                 'compact-window', 'tray-fallback', 'exports-created-updated-new', 'external-handoff'},
    'restore': {'restored-media-no-redownload', 'remove-keeps-local-media'},
    'removed': {'removed-watch-not-restored'},
    'shutdown': {'active-transfer-shutdown-disposed'},
}
for required_checks in CHECKS.values():
    required_checks.update(('normal-shutdown', 'lease-released'))
RESERVE_BYTES = 2 * 1024 ** 3
MAX_TIMEOUT = 120
MAX_METRICS_BYTES = 1024 ** 2
MAX_SCREENSHOT_BYTES = 32 * 1024 ** 2
MAX_SCREENSHOTS = 16
MAX_ARCHIVE_MEMBER_BYTES = 256 * 1024 ** 2
MAX_CLOSURE_ENTRIES = 20000
MAX_CLOSURE_BYTES = 4 * 1024 ** 3
MAX_CLOSURE_FILE_BYTES = 512 * 1024 ** 2
MAX_LINK_BYTES = 4096


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def normalized_member(name):
    if not name or '\x00' in name or '\\' in name or name.startswith('/') or '..' in name.split('/'):
        raise ValueError('Archive member must be a relative path without parent traversal')
    value = str(PurePosixPath(name))
    if value == '.' or ':' in value.split('/')[0]:
        raise ValueError('Archive member must name a file')
    return value


def member_sha256(stream, expected_size, limit=None):
    limit = MAX_ARCHIVE_MEMBER_BYTES if limit is None else limit
    if not 0 <= expected_size <= limit:
        raise ValueError('Archive member exceeds its byte limit')
    digest, count = hashlib.sha256(), 0
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        count += len(chunk)
        if count > expected_size or count > limit:
            raise ValueError('Archive member exceeds its declared or bounded size')
        digest.update(chunk)
    if count != expected_size:
        raise ValueError('Archive member size mismatch')
    return digest.hexdigest()


class HashingReader:
    """Hash compressed TAR bytes during the single streaming archive scan."""
    def __init__(self, stream):
        self.stream, self.digest = stream, hashlib.sha256()

    def read(self, size=-1):
        data = self.stream.read(size)
        self.digest.update(data)
        return data


def archive_binding(path, member_name, apphost_sha256):
    path = path.resolve(strict=True)
    requested = normalized_member(member_name)
    found, member_hash, member_size = 0, None, None

    def matches(name):
        try:
            return normalized_member(name) == requested
        except ValueError:
            return False  # Unrelated paths are never read or extracted.

    def accept(stream, size, regular):
        nonlocal found, member_hash, member_size
        found += 1
        if found != 1:
            raise ValueError('Duplicate apphost archive member')
        if not regular:
            raise ValueError('Archive apphost member must be a regular file')
        member_size = size
        member_hash = member_sha256(stream, size)

    if path.name.lower().endswith('.zip'):
        archive_hash = sha256(path)
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                if matches(entry.filename):
                    regular = not entry.is_dir() and stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG)
                    with archive.open(entry) as stream:
                        accept(stream, entry.file_size, regular)
    elif path.name.lower().endswith(('.tar.gz', '.tgz')):
        with path.open('rb') as raw:
            compressed = HashingReader(raw)
            with tarfile.open(fileobj=compressed, mode='r|gz') as archive:
                for entry in archive:
                    if matches(entry.name):
                        if not entry.isfile():
                            raise ValueError('Archive apphost member must be a regular file')
                        with archive.extractfile(entry) as stream:
                            accept(stream, entry.size, True)
            # TAR ends at its zero blocks; hash any remaining compressed bytes too.
            while compressed.read(1024 * 1024):
                pass
            archive_hash = compressed.digest.hexdigest()
    else:
        raise ValueError('Archive must be ZIP or tar.gz')
    if found != 1:
        raise ValueError('Apphost archive member missing')
    if member_hash.lower() != apphost_sha256.lower():
        raise ValueError('Archive apphost member SHA256 differs from the executable')
    return {'path': str(path), 'sha256': archive_hash, 'member': member_name,
            'memberSha256': member_hash, 'memberBytes': member_size}


def records_sha256(records):
    return hashlib.sha256(json.dumps(records, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode('utf-8')).hexdigest()


def archive_entries(path):
    """Return the bounded canonical payload manifest, without extracting anything.

    ditto's __MACOSX AppleDouble envelope is separately bound; its copied mode
    bits do not make it executable payload. Every excluded file must refer to
    a payload entry.
    """
    entries, metadata, seen = {}, {}, set()
    is_zip = Path(path).name.lower().endswith('.zip')
    total = 0

    def add(name, kind, size=0, stream=None, target=None):
        nonlocal total
        canonical = '.' if name in ('.', './') and kind == 'directory' else normalized_member(name)
        if len(PurePosixPath(canonical).parts) > 64 or len(canonical.encode('utf-8')) > 4096:
            raise ValueError('Archive closure path exceeds depth or length limit')
        if canonical in seen:
            raise ValueError('Duplicate normalized archive entry: ' + canonical)
        seen.add(canonical)
        if len(seen) > MAX_CLOSURE_ENTRIES:
            raise ValueError('Archive closure entry count exceeds limit')
        record = {'path': canonical, 'kind': kind}
        if kind not in ('directory', 'file', 'symlink'):
            raise ValueError('Unsupported archive entry type: ' + canonical)
        if kind == 'directory' and size != 0:
            raise ValueError('Archive directory cannot contain file payload bytes')
        if not 0 <= size <= MAX_CLOSURE_FILE_BYTES:
            raise ValueError('Archive closure file exceeds byte limit')
        total += size
        if total > MAX_CLOSURE_BYTES:
            raise ValueError('Archive closure total bytes exceed limit')
        is_metadata = is_zip and (canonical == '__MACOSX' or canonical.startswith('__MACOSX/'))
        if is_metadata:
            if kind not in ('file', 'directory'):
                raise ValueError('Apple metadata cannot contain links or special files')
            if kind == 'file' and (not PurePosixPath(canonical).name.startswith('._') or PurePosixPath(canonical).name == '._'):
                raise ValueError('Only AppleDouble ._ files may be excluded from payload closure')
        if kind == 'file':
            record.update(bytes=size, sha256=member_sha256(stream, size, MAX_CLOSURE_FILE_BYTES))
        elif kind == 'symlink':
            if size > MAX_LINK_BYTES:
                raise ValueError('Archive link target exceeds limit')
            if target is None:
                raw = stream.read(MAX_LINK_BYTES + 1)
                if len(raw) != size:
                    raise ValueError('Archive link target size mismatch')
                target = raw.decode('utf-8')
            validate_link_text(target)
            record['target'] = target
        (metadata if is_metadata else entries)[canonical] = record

    path = Path(path)
    if path.name.lower().endswith('.zip'):
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                mode = entry.external_attr >> 16
                file_type = stat.S_IFMT(mode)
                kind = 'directory' if entry.is_dir() or file_type == stat.S_IFDIR else 'symlink' if file_type == stat.S_IFLNK else 'file' if file_type in (0, stat.S_IFREG) else 'other'
                with archive.open(entry) as stream:
                    add(entry.filename, kind, entry.file_size, stream)
    elif path.name.lower().endswith(('.tar.gz', '.tgz')):
        with tarfile.open(path, 'r|gz') as archive:
            for entry in archive:
                kind = 'directory' if entry.isdir() else 'symlink' if entry.issym() else 'file' if entry.isfile() else 'other'
                if kind == 'file':
                    with archive.extractfile(entry) as stream:
                        add(entry.name, kind, entry.size, stream)
                else:
                    add(entry.name, kind, len(entry.linkname.encode('utf-8')) if kind == 'symlink' else entry.size,
                        target=entry.linkname if kind == 'symlink' else None)
    else:
        raise ValueError('Archive must be ZIP or tar.gz')

    # Explicit and implied directories describe the same extracted tree.
    for name in tuple(entries):
        for parent in PurePosixPath(name).parents:
            key = str(parent)
            if key in entries and entries[key]['kind'] != 'directory':
                raise ValueError('Archive entry is nested below a non-directory: ' + name)
            entries.setdefault(key, {'path': key, 'kind': 'directory'})
    entries.setdefault('.', {'path': '.', 'kind': 'directory'})
    for name in tuple(metadata):
        for parent in PurePosixPath(name).parents:
            key = str(parent)
            if key == '.':
                continue
            if key in metadata and metadata[key]['kind'] != 'directory':
                raise ValueError('Apple metadata nested below a non-directory')
            metadata.setdefault(key, {'path': key, 'kind': 'directory'})
    if len(entries) + len(metadata) > MAX_CLOSURE_ENTRIES:
        raise ValueError('Archive closure entry count exceeds limit')
    for record in metadata.values():
        if record['kind'] == 'file':
            relative = PurePosixPath(record['path']).relative_to('__MACOSX')
            payload = str(relative.with_name(relative.name[2:]))
            if payload not in entries:
                raise ValueError('AppleDouble metadata does not refer to payload: ' + record['path'])
        elif record['path'] != '__MACOSX':
            payload = str(PurePosixPath(record['path']).relative_to('__MACOSX'))
            if entries.get(payload, {}).get('kind') != 'directory':
                raise ValueError('Apple metadata directory does not refer to payload')
    ordered, envelope = sorted(entries.values(), key=lambda item: item['path']), sorted(metadata.values(), key=lambda item: item['path'])
    return {'entries': ordered, 'contentSha256': records_sha256(ordered), 'entryCount': len(ordered),
            'fileCount': sum(item['kind'] == 'file' for item in ordered),
            'verifiedBytes': sum(item.get('bytes', 0) for item in ordered),
            'archiveMetadataEntries': envelope, 'archiveMetadataSha256': records_sha256(envelope)}


def validate_link_text(target):
    if not target or '\x00' in target or '\\' in target or target.startswith('/') or len(target.encode('utf-8')) > MAX_LINK_BYTES:
        raise ValueError('Archive link target must be a bounded relative path')
    if any(':' in part for part in target.split('/')):
        raise ValueError('Archive link target cannot contain a drive path')


def confined_path(path, root):
    path = Path(path)
    if not path.is_absolute() or not path.is_relative_to(root):
        raise ValueError('Loaded path must belong to the archive root')
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise ValueError('Archive contains a broken or cyclic link') from error
    if not resolved.is_relative_to(root):
        raise ValueError('Archive link escapes the extracted root')
    return resolved


def verify_extracted_closure(root, manifest):
    """Hash the physical tree without following links while traversing it."""
    root = Path(root)
    if not root.is_dir() or root.resolve(strict=True) != root or root.is_symlink():
        raise ValueError('Archive root must be an existing canonical directory')
    expected = {record['path']: record for record in manifest['entries']}
    metadata = {record['path']: record for record in manifest['archiveMetadataEntries']}
    for name in tuple(metadata):
        for parent in PurePosixPath(name).parents:
            if str(parent) != '.':
                metadata.setdefault(str(parent), {'path': str(parent), 'kind': 'directory'})
    actual, total = {}, 0

    def visit(directory):
        nonlocal total
        with os.scandir(directory) as scan:
            for item in scan:
                path = Path(item.path)
                name = path.relative_to(root).as_posix()
                if name not in expected and name not in metadata:
                    raise ValueError('Unexpected extracted entry: ' + name)
                details = item.stat(follow_symlinks=False)
                kind = 'symlink' if stat.S_ISLNK(details.st_mode) else 'directory' if stat.S_ISDIR(details.st_mode) else 'file' if stat.S_ISREG(details.st_mode) else 'other'
                record = {'path': name, 'kind': kind}
                if kind == 'file':
                    if details.st_size > MAX_CLOSURE_FILE_BYTES:
                        raise ValueError('Extracted closure file exceeds byte limit')
                    total += details.st_size
                    if total > MAX_CLOSURE_BYTES:
                        raise ValueError('Extracted closure total bytes exceed limit')
                    record.update(bytes=details.st_size, sha256=sha256(path))
                elif kind == 'symlink':
                    target = os.readlink(path)
                    validate_link_text(target)
                    if not Path(os.path.abspath(path.parent / target)).is_relative_to(root):
                        raise ValueError('Archive link escapes the extracted root')
                    resolved = confined_path(path, root)
                    if resolved.relative_to(root).as_posix() not in expected:
                        raise ValueError('Archive link does not target payload')
                    record['target'] = target
                if record != expected.get(name, metadata.get(name)):
                    raise ValueError('Extracted closure differs from archive: ' + name)
                if name in expected:
                    actual[name] = record
                if kind == 'directory':
                    visit(path)

    actual['.'] = {'path': '.', 'kind': 'directory'}
    visit(root)
    if set(actual) != set(expected):
        raise ValueError('Extracted archive payload is incomplete')
    fingerprint = records_sha256(sorted(actual.values(), key=lambda item: item['path']))
    if fingerprint != manifest['contentSha256']:
        raise ValueError('Extracted archive closure fingerprint mismatch')
    return fingerprint


def archive_closure(archive, root, member_name, apphost):
    root = Path(root).resolve(strict=True)
    manifest = archive_entries(archive)
    verify_extracted_closure(root, manifest)
    packaged_apphost = confined_path(root / normalized_member(member_name), root)
    if packaged_apphost != Path(apphost).resolve(strict=True):
        raise ValueError('Executed apphost is outside the verified archive closure')
    expected = {record['path']: record for record in manifest['entries']}
    base = root / PurePosixPath(normalized_member(member_name)).parent
    name = PurePosixPath(member_name).name.removesuffix('.exe')
    for required in (name + '.dll', name + '.deps.json', name + '.runtimeconfig.json', 'libmeowchan.dll'):
        resolved = confined_path(base / required, root)
        if expected.get(resolved.relative_to(root).as_posix(), {}).get('kind') != 'file':
            raise ValueError('Required managed runtime payload is not a regular archive member: ' + required)
    return dict(manifest, root=str(root), verifiedBeforeRun=True, verifiedAfterRun=False)


def validate_loaded_assemblies(metrics, closure):
    root = Path(closure['root'])
    expected = {record['path']: record for record in closure['entries']}
    base = metrics.get('appBaseDirectory')
    if not isinstance(base, str):
        raise ValueError('Product application base directory is not in the verified closure')
    resolved_base = confined_path(Path(base), root)
    if expected.get(resolved_base.relative_to(root).as_posix(), {}).get('kind') != 'directory':
        raise ValueError('Product application base directory is not in the verified closure')
    rows = metrics.get('managedAssemblies')
    if not isinstance(rows, list):
        raise ValueError('Product managed assembly evidence missing')
    bindings, names = [], set()
    for row in rows:
        if not isinstance(row, dict) or not all(isinstance(row.get(key), str) and row[key] for key in ('name', 'path', 'sha256')):
            raise ValueError('Product managed assembly evidence malformed')
        if row['name'] in names:
            raise ValueError('Product managed assembly evidence duplicated')
        names.add(row['name'])
        physical = confined_path(Path(row['path']), root)
        relative = physical.relative_to(root).as_posix()
        archived = expected.get(relative, {})
        if (archived.get('kind') != 'file' or PurePosixPath(relative).name != row['name'] + '.dll'
                or row['sha256'].lower() != archived['sha256'] or sha256(physical) != archived['sha256']):
            raise ValueError('Loaded managed assembly differs from verified archive payload')
        bindings.append({'name': row['name'], 'path': row['path'], 'archivePath': relative, 'sha256': archived['sha256']})
    if not {'MeowChan', 'libmeowchan'}.issubset(names):
        raise ValueError('Main and library managed assembly evidence required for archive closure')
    return bindings


def ensure_disk_reserve(*paths):
    for path in paths:
        if shutil.disk_usage(path).free < RESERVE_BYTES:
            raise ValueError('Disk space fell below the 2 GiB reserve: ' + str(path))


def owned_path(path, root, *, exists=False):
    """Reject escapes and links inside a runner-owned, canonical temporary root."""
    root = Path(root)
    path = Path(path)
    if not root.is_absolute() or root.resolve(strict=True) != root or not root.is_dir():
        raise ValueError('Workflow root must be an existing absolute canonical directory')
    if not path.is_absolute() or path == root or not path.is_relative_to(root):
        raise ValueError('Workflow path must be strictly below its owned root')
    relative = path.relative_to(root)
    if '..' in relative.parts:
        raise ValueError('Workflow path cannot traverse parents')
    current = root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError('Workflow path cannot contain symlinks: ' + str(current))
    if path.resolve(strict=exists) != path:
        raise ValueError('Workflow path must be canonical')
    return path


def root_owner(root, expected=None):
    if root.parent != Path(tempfile.gettempdir()).resolve(strict=True) or not root.name.startswith('meowchan-workflow-'):
        raise ValueError('Workflow root must be a private temporary root with the expected prefix')
    owner_path = owned_path(root / '.workflow-owner', root, exists=True)
    if not owner_path.is_file() or owner_path.stat().st_size != 32:
        raise ValueError('Workflow root owner token is not a bounded regular file')
    owner_token = owner_path.read_text(encoding='utf-8')
    if not re.fullmatch('[0-9a-f]{32}', owner_token) or (expected is not None and owner_token != expected):
        raise ValueError('Workflow root owner token is invalid')
    return owner_token


def create_descriptor(root, phase, scale, previous_profile):
    if phase not in PHASES or type(scale) not in (int, float) or scale not in (1, 1.5, 2):
        raise ValueError('Unknown workflow phase or scale')
    owner_token = root_owner(root)
    profile = owned_path(root / ('profile-' + phase), root)
    if profile.exists():
        raise ValueError('Each workflow process requires a new, nonexistent profile')
    if previous_profile is not None:
        previous_profile = owned_path(previous_profile, root, exists=True)
        if not previous_profile.is_dir() or previous_profile == profile:
            raise ValueError('Previous profile must be a distinct owned directory')
    if (phase in ('restore', 'removed')) != (previous_profile is not None):
        raise ValueError('Only restore and removed phases require a previous profile')
    expected_previous = {'restore': 'download', 'removed': 'restore'}.get(phase)
    if expected_previous and previous_profile != root / ('profile-' + expected_previous):
        raise ValueError('Previous profile does not match the preceding workflow phase')
    path = owned_path(root / (phase + '.json'), root)
    descriptor = {'schemaVersion': 1, 'phase': phase, 'root': str(root),
                  'previousProfile': str(previous_profile) if previous_profile is not None else None,
                  'expectedScale': scale, 'ownerToken': owner_token}
    with path.open('x', encoding='utf-8') as target:
        json.dump(descriptor, target, indent=2)
        target.write('\n')
    return path, profile


def validate_metrics(metrics, phase, scale, *, isolated=True, working=None, profile=None, process_id=None):
    if not isinstance(metrics, dict) or type(metrics.get('schemaVersion')) is not int or metrics['schemaVersion'] != 1:
        raise ValueError('Unsupported workflow metrics schema')
    if metrics.get('completed') is not True or 'failure' not in metrics or metrics['failure'] is not None:
        raise ValueError('Product did not complete workflow checks')
    if metrics.get('phase') != phase:
        raise ValueError('Workflow metrics phase mismatch')
    checks = metrics.get('checks')
    if not isinstance(checks, list) or any(not isinstance(check, str) for check in checks):
        raise ValueError('Workflow checks must be a list of strings')
    missing = CHECKS[phase] - set(checks)
    if missing:
        raise ValueError('Product omitted required workflow checks: ' + ', '.join(sorted(missing)))
    actual = metrics.get('renderScaling')
    if type(actual) not in (int, float) or not math.isfinite(actual) or actual <= 0:
        raise ValueError('Product did not report a positive finite render scale')
    if isolated and not math.isclose(actual, scale, abs_tol=0.01):
        raise ValueError(f'Requested scale {scale}; observed {actual!r}')
    if type(metrics.get('processId')) is not int or metrics['processId'] <= 0:
        raise ValueError('Product process identity missing')
    if not isolated and process_id is not None and metrics['processId'] != process_id:
        raise ValueError('Product process identity mismatch')
    if working is not None and metrics.get('workingDirectory') != str(working):
        raise ValueError('Product working directory mismatch')
    if profile is not None and metrics.get('profile') != str(profile):
        raise ValueError('Product profile mismatch')
    if not isinstance(metrics.get('assemblyInformationalVersion'), str) or not metrics['assemblyInformationalVersion'].strip():
        raise ValueError('Product assembly informational version missing')
    if phase == 'download':
        dispatch = metrics.get('externalDispatch')
        if not isinstance(dispatch, dict) or dispatch.get('kind') != 'intercepted-export-command' or not isinstance(dispatch.get('target'), str) or not dispatch['target']:
            raise ValueError('External handoff interception evidence missing')


def retain_evidence(profile, root, destination, owner_token=None):
    """Copy only bounded, regular product evidence, never profiles or downloads."""
    if not profile.exists():
        return []
    root_owner(root, owner_token)
    owned_path(profile, root, exists=True)
    sources = [(profile / 'apphost-workflow.json', destination / 'metrics.json', MAX_METRICS_BYTES)]
    screenshots = sorted(profile.glob('apphost-workflow*.png'))
    if len(screenshots) > MAX_SCREENSHOTS:
        raise ValueError('Too many workflow screenshots')
    sources += [(path, destination / path.name, MAX_SCREENSHOT_BYTES) for path in screenshots]
    retained = []
    for source, target, limit in sources:
        root_owner(root, owner_token)
        if not source.exists() and not source.is_symlink():
            continue
        owned_path(source, root, exists=True)
        if not source.is_file() or source.stat().st_size > limit:
            raise ValueError('Workflow evidence is not a bounded regular file: ' + str(source))
        if shutil.disk_usage(destination).free < RESERVE_BYTES + source.stat().st_size:
            raise ValueError('Copying workflow evidence would cross the 2 GiB reserve')
        shutil.copy2(source, target)
        root_owner(root, owner_token)
        ensure_disk_reserve(destination)
        retained.append(target.name)
    return retained


def phase_environment(root, phase, profile, isolated, scale):
    working = owned_path(root / ('cwd-' + phase), root)
    working.mkdir()
    environment = desktop.prepare_environment(dict(os.environ), isolated, working)
    environment['MEOWCHAN_PROFILE'] = str(profile)
    environment['AVALONIA_GLOBAL_SCALE_FACTOR'] = str(scale)
    return working, environment


def run_phase(apphost, root, phase, scale, previous_profile, destination, timeout, isolated, closure=None):
    destination.mkdir()
    result = {'phase': phase, 'expectedScale': scale, 'passed': False, 'timedOut': False,
              'interrupted': False, 'exitCode': None}
    process = None
    owner_token = None
    profile = root / ('profile-' + phase)
    try:
        ensure_disk_reserve(root, destination)
        descriptor, profile = create_descriptor(root, phase, scale, previous_profile)
        owner_token = root_owner(root)
        shutil.copy2(descriptor, destination / 'descriptor.json')
        working, environment = phase_environment(root, phase, profile, isolated, scale)
        command = [str(apphost), '--smoke-workflow', str(descriptor)]
        if isolated:
            command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--',
                       'xvfb-run', '--auto-servernum', '--server-args=-screen 0 3200x2000x24 -nolisten tcp',
                       sys.executable, str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()),
                       '--', *command]
        result.update(command=command, workingDirectory=str(working), profile=str(profile),
                      previousProfile=str(previous_profile) if previous_profile is not None else None)
        with (destination / 'output.log').open('x', encoding='utf-8') as log:
            try:
                process = subprocess.Popen(command, cwd=working, env=environment, stdout=log,
                                           stderr=subprocess.STDOUT, start_new_session=os.name != 'nt')
                result['runnerProcessId'] = process.pid
                deadline = time.monotonic() + timeout
                while True:
                    ensure_disk_reserve(root, destination)
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        result['timedOut'] = True
                        desktop.sample_timeout(process, destination)
                        break
                    try:
                        result['exitCode'] = process.wait(timeout=min(remaining, 0.25))
                        break
                    except subprocess.TimeoutExpired:
                        continue
            finally:
                if process is not None:
                    desktop.stop_owned_processes(process)
        if result['timedOut'] or result['exitCode'] != 0:
            raise ValueError('Apphost timed out or exited unsuccessfully')
    except (KeyboardInterrupt, InterruptedError):
        result.update(interrupted=True, error='Runner interrupted')
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
    finally:
        try:
            result['retainedEvidence'] = retain_evidence(profile, root, destination, owner_token) if owner_token is not None else []
        except (OSError, ValueError) as error:
            result['error'] = result.get('error', '') + ' Evidence collection: ' + str(error)
    if 'error' not in result:
        try:
            if 'APPHOST WORKFLOW PASS' not in (destination / 'output.log').read_text(encoding='utf-8', errors='replace'):
                raise ValueError('Apphost completion marker missing')
            metrics = json.loads((destination / 'metrics.json').read_text(encoding='utf-8'))
            validate_metrics(metrics, phase, scale, isolated=isolated, working=working, profile=profile,
                             process_id=result['runnerProcessId'])
            if closure is not None:
                result['managedAssemblyBindings'] = validate_loaded_assemblies(metrics, closure)
                result['managedAssemblyBindingVerified'] = True
                result['appBaseDirectory'] = metrics['appBaseDirectory']
            result['renderScaling'] = metrics['renderScaling']
            result['productProcessId'] = metrics['processId']
            result['assemblyInformationalVersion'] = metrics['assemblyInformationalVersion']
            result['screenshots'] = {path.name: playback.validate_png(path)
                                     for path in sorted(destination.glob('apphost-workflow*.png'))}
            if phase == 'download' and not {'apphost-workflow-compact.png', 'apphost-workflow-export.png'}.issubset(result['screenshots']):
                raise ValueError('Packaged compact-window/export screenshots missing')
            result['passed'] = True
        except (OSError, ValueError, KeyError, TypeError) as error:
            result['error'] = str(error)
    (destination / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


def run_scale(apphost, scale, destination, timeout, isolated, closure=None):
    destination.mkdir()
    results = []
    with tempfile.TemporaryDirectory(prefix=f'meowchan-workflow-{scale}-') as temporary:
        root = Path(temporary).resolve(strict=True)
        (root / '.workflow-owner').write_text(os.urandom(16).hex(), encoding='utf-8')
        for phase in PHASES:
            if results and not results[-1]['passed']:
                results.append({'phase': phase, 'expectedScale': scale, 'passed': False,
                                'skipped': True, 'reason': 'Stopped after previous failure'})
                continue
            previous = root / ('profile-' + {'restore': 'download', 'removed': 'restore'}[phase]) if phase in ('restore', 'removed') else None
            results.append(run_phase(apphost, root, phase, scale, previous, destination / phase, timeout, isolated, closure))
    return {'scale': scale, 'passed': all(item['passed'] for item in results), 'phases': results}


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path, help='Executable from an extracted candidate package')
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--source-commit', required=True, help='Full source SHA declared by the candidate packaging job')
    parser.add_argument('--archive', type=Path, help='Optional candidate ZIP/tar.gz to bind to the extracted executable')
    parser.add_argument('--archive-member', help='Relative apphost member path inside --archive; no extraction is performed')
    parser.add_argument('--archive-root', type=Path, help='Verify every extracted archive payload file/link and loaded managed assemblies')
    parser.add_argument('--runtime', required=True, choices=('linux-x64', 'linux-arm64', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64'))
    parser.add_argument('--scale', type=float, action='append', choices=(1, 1.5, 2),
                        help='Default: Linux 1 and 2; native 1 (actual native scaling is recorded, not forced)')
    parser.add_argument('--timeout', type=float, default=MAX_TIMEOUT, help='Per phase deadline, at most 120 seconds')
    parser.add_argument('--allow-native-desktop', action='store_true')
    args = parser.parse_args(argv)
    if (args.archive is None) != (args.archive_member is None):
        parser.error('--archive and --archive-member must be supplied together')
    if args.archive_root is not None and args.archive is None:
        parser.error('--archive-root requires --archive and --archive-member')
    if args.archive_member is not None:
        try:
            normalized_member(args.archive_member)
        except ValueError as error:
            parser.error(str(error))
    if not re.fullmatch(r'[0-9a-fA-F]{40}', args.source_commit):
        parser.error('--source-commit must be a full 40-character SHA')
    if not math.isfinite(args.timeout) or not 0 < args.timeout <= MAX_TIMEOUT:
        parser.error('--timeout must be finite, positive, and at most 120 seconds')
    args.isolated = sys.platform.startswith('linux')
    if not args.isolated and not args.allow_native_desktop:
        parser.error('Native workflow checks require --allow-native-desktop on a disposable desktop/CI')
    if args.isolated:
        missing = [tool for tool in desktop.ISOLATED_TOOLS if not shutil.which(tool)]
        if missing:
            parser.error('Private desktop dependencies missing: ' + ', '.join(missing))
    args.scales = list(dict.fromkeys(args.scale or ((1, 2) if args.isolated else (1,))))
    args.scales = [int(value) if value == int(value) else value for value in args.scales]
    return args


def main(argv=None):
    args = parse_args(argv)
    apphost = args.apphost.resolve(strict=True)
    if not apphost.is_file():
        raise ValueError('Apphost must be a regular file')
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    destination = args.evidence_dir.resolve(strict=True)
    ensure_disk_reserve(destination, tempfile.gettempdir())
    report = {'schemaVersion': 1, 'apphost': str(apphost), 'apphostSha256': sha256(apphost),
              'declaredSourceCommit': args.source_commit.lower(), 'declaredRuntime': args.runtime,
              'host': platform.platform(), 'machine': platform.machine(), 'isolatedDesktop': args.isolated,
              'nativeDesktopOptIn': args.allow_native_desktop and not args.isolated,
              'scope': 'Extracted apphost offline workflow; native clipboard on private/disposable desktop; '
                       'HTTP and external browser/folder handoff intercepted. Declared provenance must be bound '
                       'to this executable hash by the candidate packaging job. No physical desktop or audio claim.',
              'minimumDiskReserveBytes': RESERVE_BYTES, 'phaseTimeoutSeconds': args.timeout,
              'selectedScales': args.scales, 'scaleEnforced': args.isolated,
              'fullScaleCoverage': {1, 2}.issubset(args.scales) if args.isolated else None, 'scales': [],
              'passed': False, 'completed': False}
    if args.archive is not None:
        report['archiveBinding'] = archive_binding(args.archive, args.archive_member, report['apphostSha256'])
    if args.archive_root is not None:
        report['archiveClosure'] = archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
    (destination / 'workflow.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    for scale in args.scales:
        if report['scales'] and not report['scales'][-1]['passed']:
            report['scales'].append({'scale': scale, 'passed': False, 'skipped': True,
                                     'reason': 'Stopped after previous failure', 'phases': []})
            continue
        report['scales'].append(run_scale(apphost, scale, destination / f'scale-{scale}', args.timeout, args.isolated,
                                         report.get('archiveClosure')))
    report['passed'] = all(item['passed'] for item in report['scales'])
    if 'archiveClosure' in report:
        try:
            closure = report['archiveClosure']
            closure['afterContentSha256'] = verify_extracted_closure(Path(closure['root']), closure)
            closure['verifiedAfterRun'] = True
        except (OSError, ValueError) as error:
            report.update(passed=False, error='Archive closure changed during workflow: ' + str(error))
    report['completed'] = True
    report['diskFreeAfterBytes'] = shutil.disk_usage(destination).free
    (destination / 'workflow.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, desktop.interrupted)
    sys.exit(main())
