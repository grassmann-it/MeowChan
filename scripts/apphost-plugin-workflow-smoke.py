#!/usr/bin/env python3
"""Linux SDK-free packaged PluginSession E2E, on a private desktop with terminal fixture HTTP.

Requires an extracted original archive; copies it to a new location, binds it read-only,
then runs download/filter/archive and paused offline restore in separate apphost processes.
No build, live source request or physical desktop interaction is performed.
"""
from __future__ import annotations
import argparse
import json
import hashlib
import struct
import uuid
import zlib
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



BUNDLE_SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
REQUIRED_ASSEMBLIES = {'MeowChan', 'libmeowchan'}


def module_version_id(data):
    """Read Module.Mvid from a PE/CLI metadata image, without executing its code."""
    def unpack(fmt, offset):
        size = struct.calcsize(fmt)
        if offset < 0 or offset + size > len(data):
            raise ValueError('PE field outside image')
        return struct.unpack_from(fmt, data, offset)
    if data[:2] != b'MZ':
        raise ValueError('Managed assembly is not a PE image')
    pe, = unpack('<I', 0x3c)
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('PE signature missing')
    sections, = unpack('<H', pe + 6)
    optional_size, = unpack('<H', pe + 20)
    optional = pe + 24
    magic, = unpack('<H', optional)
    directory = optional + {0x10b: 96, 0x20b: 112}.get(magic, -1000000)
    if directory < optional or directory + 15 * 8 > optional + optional_size or not 0 < sections <= 96:
        raise ValueError('Unsupported PE optional header')
    section_table = optional + optional_size

    def rva_offset(rva, size):
        for index in range(sections):
            _, address, raw_size, raw = unpack('<IIII', section_table + 40 * index + 8)
            if address <= rva and rva + size <= address + raw_size:
                offset = raw + rva - address
                if offset + size <= len(data):
                    return offset
        raise ValueError('CLI address outside PE sections')

    cli_rva, cli_size = unpack('<II', directory + 14 * 8)
    cli = rva_offset(cli_rva, cli_size)
    metadata_rva, metadata_size = unpack('<II', cli + 8)
    metadata = rva_offset(metadata_rva, metadata_size)
    end = metadata + metadata_size
    if data[metadata:metadata + 4] != b'BSJB':
        raise ValueError('CLI metadata signature missing')
    version_length, = unpack('<I', metadata + 12)
    cursor = metadata + ((16 + version_length + 3) & ~3)
    _, count = unpack('<HH', cursor)
    cursor += 4
    streams = {}
    if not 1 <= count <= 64:
        raise ValueError('CLI stream count invalid')
    for _ in range(count):
        offset, size = unpack('<II', cursor)
        cursor += 8
        name_end = data.find(b'\0', cursor, min(cursor + 32, end))
        if name_end < 0:
            raise ValueError('CLI stream name invalid')
        name = data[cursor:name_end].decode('ascii')
        cursor = (name_end + 4) & ~3
        if name in streams or offset + size > metadata_size:
            raise ValueError('CLI stream bounds/identity invalid')
        streams[name] = (metadata + offset, size)
    tables, table_size = streams.get('#~', streams.get('#-', (0, 0)))
    guid, guid_size = streams.get('#GUID', (0, 0))
    if table_size < 28 or not guid_size:
        raise ValueError('Required CLI metadata streams missing')
    heap_sizes, = unpack('<B', tables + 6)
    valid, = unpack('<Q', tables + 8)
    if not valid & 1:
        raise ValueError('CLI Module table missing')
    rows, = unpack('<I', tables + 24)
    if rows != 1:
        raise ValueError('CLI Module table must have one row')
    row = tables + 24 + 4 * valid.bit_count()
    string_index_size = 4 if heap_sizes & 1 else 2
    guid_index_size = 4 if heap_sizes & 2 else 2
    if row + 2 + string_index_size + guid_index_size > tables + table_size:
        raise ValueError('CLI Module row outside table stream')
    index, = unpack('<I' if guid_index_size == 4 else '<H', row + 2 + string_index_size)
    if not 1 <= index <= guid_size // 16:
        raise ValueError('CLI MVID outside GUID stream')
    return str(uuid.UUID(bytes_le=data[guid + (index - 1) * 16:guid + index * 16]))


def bundle_inventory(apphost):
    """Parse supported .NET v6 bundles, including bounded raw-deflate entries.

    An absent/zero bundle offset denotes unbundled delivery. Any populated but
    unsupported or corrupt header fails closed rather than falling back to DLLs.
    """
    if apphost.stat().st_size > workflow.MAX_CLOSURE_FILE_BYTES:
        raise ValueError('Apphost exceeds closure byte limit')
    data = apphost.read_bytes()
    marker = data.find(BUNDLE_SIGNATURE)
    if marker < 0:
        return None
    if marker < 8 or data.find(BUNDLE_SIGNATURE, marker + 1) >= 0:
        raise ValueError('Ambiguous apphost bundle marker')
    header, = struct.unpack_from('<q', data, marker - 8)
    if header == 0:
        return None
    if not marker + len(BUNDLE_SIGNATURE) <= header < len(data):
        raise ValueError('Bundle header does not follow apphost marker')
    cursor = header

    def read(fmt):
        nonlocal cursor
        size = struct.calcsize(fmt)
        if cursor < 0 or cursor + size > len(data):
            raise ValueError('Bundle header outside apphost')
        value = struct.unpack_from(fmt, data, cursor)
        cursor += size
        return value

    def text():
        nonlocal cursor
        length = 0
        for shift in range(0, 35, 7):
            byte, = read('<B')
            length |= (byte & 127) << shift
            if not byte & 128:
                break
        else:
            raise ValueError('Bundle string length invalid')
        if not 0 < length <= 4096 or cursor + length > len(data):
            raise ValueError('Bundle string outside header')
        value = data[cursor:cursor + length].decode('utf-8')
        cursor += length
        return value

    major, minor, count = read('<IIi')
    if (major, minor) != (6, 0) or not 0 < count <= 20000:
        raise ValueError('Unsupported .NET bundle header')
    bundle_id = text()
    deps_offset, deps_size, config_offset, config_size, flags = read('<qqqqQ')
    if flags & ~1:
        raise ValueError('Unsupported .NET bundle flags')
    entries, spans, total_bytes = {}, [], 0
    for _ in range(count):
        offset, size, compressed = read('<qqq')
        kind, = read('<B')
        name = workflow.normalized_member(text())
        length = compressed or size
        if (name in entries or not 0 <= kind <= 5 or offset < 0 or size <= 0
                or size > workflow.MAX_CLOSURE_FILE_BYTES or compressed < 0
                or offset + length > header):
            raise ValueError('Invalid or duplicate .NET bundle entry')
        total_bytes += size
        if total_bytes > workflow.MAX_CLOSURE_BYTES or offset < marker + len(BUNDLE_SIGNATURE):
            raise ValueError('Bundle payload exceeds bounds')
        spans.append((offset, offset + length))
        raw = data[offset:offset + length]
        if compressed:
            inflater = zlib.decompressobj(-15)
            raw = inflater.decompress(raw, size + 1)
            if not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
                raise ValueError('Invalid/oversized compressed bundle entry')
        if len(raw) != size:
            raise ValueError('Bundle entry size mismatch')
        record = {'path': name, 'type': kind, 'offset': offset, 'bytes': size,
                  'compressedBytes': compressed, 'sha256': hashlib.sha256(raw).hexdigest()}
        if kind == 1 and name in {value + '.dll' for value in REQUIRED_ASSEMBLIES}:
            record['moduleVersionId'] = module_version_id(raw)
        entries[name] = record
    ordered = sorted(spans)
    if any(left[1] > right[0] for left, right in zip(ordered, ordered[1:])):
        raise ValueError('Overlapping .NET bundle entries')
    for name, offset, size, kind in (('MeowChan.deps.json', deps_offset, deps_size, 3),
                                     ('MeowChan.runtimeconfig.json', config_offset, config_size, 4)):
        entry = entries.get(name, {})
        if entry.get('offset') != offset or entry.get('bytes') != size or entry.get('type') != kind:
            raise ValueError('Bundle runtime metadata header/entry mismatch')
    for name in REQUIRED_ASSEMBLIES:
        if 'moduleVersionId' not in entries.get(name + '.dll', {}):
            raise ValueError('Required managed assembly absent from bundle')
    return {'majorVersion': major, 'minorVersion': minor, 'bundleId': bundle_id,
            'entries': list(entries.values())}


def plugin_archive_closure(archive, root, member_name, apphost):
    root = Path(root).resolve(strict=True)
    manifest = workflow.archive_entries(archive)
    workflow.verify_extracted_closure(root, manifest)
    member = workflow.normalized_member(member_name)
    if workflow.confined_path(root / member, root) != Path(apphost).resolve(strict=True):
        raise ValueError('Executed apphost is outside verified archive closure')
    expected = {record['path']: record for record in manifest['entries']}
    if expected.get(member, {}).get('kind') != 'file':
        raise ValueError('Apphost must be a regular archive member')
    bundle = bundle_inventory(Path(apphost))
    if bundle is None:
        # Keep the established external-assembly closure requirements intact.
        workflow.archive_closure(archive, root, member, apphost)
    return dict(manifest, root=str(root), apphostMember=member, bundle=bundle,
                verifiedBeforeRun=True, verifiedAfterRun=False)


def validate_assembly_bindings(metrics, closure):
    """Bind loaded module identity to authenticated package bytes.

    Empty Location is accepted only for an actual inspected bundle. MVID identifies
    the loaded module; it is not represented as an in-memory cryptographic hash.
    The executable/file hashes authenticate delivered bytes under the runner's
    immutable package mount and isolated filesystem/load environment.
    """
    root = Path(closure['root'])
    expected = {row['path']: row for row in closure['entries']}
    apphost = workflow.confined_path(root / closure['apphostMember'], root)
    apphost_record = expected[closure['apphostMember']]
    if (metrics.get('processPath') != str(apphost) or metrics.get('processSha256') != apphost_record['sha256']
            or workflow.sha256(apphost) != apphost_record['sha256']):
        raise ValueError('Running executable differs from archived apphost')
    base = workflow.confined_path(Path(metrics.get('appBaseDirectory', '')), root)
    if base != apphost.parent:
        raise ValueError('Application base differs from packaged apphost directory')
    rows = metrics.get('managedAssemblies')
    if not isinstance(rows, list) or len(rows) != len(REQUIRED_ASSEMBLIES):
        raise ValueError('Required loaded managed assembly evidence missing')
    names, bindings = set(), []
    bundled = {row['path']: row for row in (closure.get('bundle') or {}).get('entries', [])}
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError('Malformed loaded managed assembly evidence')
        name = row.get('name')
        if name not in REQUIRED_ASSEMBLIES or name in names:
            raise ValueError('Unexpected/duplicate loaded managed assembly')
        names.add(name)
        if row.get('path') == '':
            entry = bundled.get(name + '.dll')
            if not entry or row.get('sha256') is not None or row.get('moduleVersionId') != entry.get('moduleVersionId'):
                raise ValueError('Loaded bundled assembly identity is not proven')
            bindings.append(dict(entry, name=name, binding='bundle-entry-mvid-and-archived-apphost', apphostSha256=apphost_record['sha256']))
        else:
            path = workflow.confined_path(Path(row.get('path', '')), root)
            relative = path.relative_to(root).as_posix()
            entry = expected.get(relative, {})
            if (entry.get('kind') != 'file' or path.name != name + '.dll' or row.get('sha256') != entry.get('sha256')
                    or workflow.sha256(path) != entry['sha256'] or row.get('moduleVersionId') != module_version_id(path.read_bytes())):
                raise ValueError('Loaded external assembly differs from archived bytes/identity')
            bindings.append(dict(name=name, path=str(path), sha256=entry['sha256'], moduleVersionId=row['moduleVersionId'], binding='archive-file-hash-and-mvid'))
    return bindings


def prepare_private_home(root, working):
    """Defaults may query UserProfile before fixture config overrides are applied."""
    home = workflow.owned_path(working / 'home', root)
    home.mkdir(mode=0o700)
    return home


def sandbox(apphost, root, working):
    """Whitelist mounts: source tree, personal home, NuGet and optional SDK roots are absent."""
    home = workflow.owned_path(working / 'home', root, exists=True)
    if not home.is_dir() or home.stat().st_mode & 0o777 != 0o700:
        raise ValueError('Sandbox HOME must be an existing private owned directory')
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
                '--setenv', 'PATH', '/no-sdk', '--setenv', 'HOME', str(home),
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
    return validate_assembly_bindings(metrics, closure)


def run_phase(apphost, root, phase, destination, timeout, scale, closure):
    destination.mkdir()
    previous = root / 'profile-download' if phase == 'restore' else None
    descriptor, profile = workflow.create_descriptor(root, phase, scale, previous)
    working, environment = workflow.phase_environment(root, phase, profile, True, scale)
    home = prepare_private_home(root, working)
    # Keep only the explicit isolated GUI/runtime variables. All package helpers
    # inherit this environment and the same filesystem/network namespace.
    allowed = {'LANG', 'LC_ALL', 'TZ', 'MEOWCHAN_PROFILE', 'MEOWCHAN_DISABLE_UPDATE_CHECK',
               'AVALONIA_GLOBAL_SCALE_FACTOR', 'XDG_RUNTIME_DIR', 'XDG_CONFIG_HOME',
               'XDG_CACHE_HOME', 'XDG_DATA_HOME', 'MEOWCHAN_DESKTOP_SMOKE_XVFB', 'XDG_SESSION_TYPE'}
    environment = {key: value for key, value in environment.items() if key in allowed}
    environment['PATH'] = '/usr/bin:/bin'
    # The private Xvfb display uses Mesa. Selecting the NVIDIA vendor here can
    # crash Xvfb before the packaged application starts on NVIDIA hosts.
    environment['__EGL_VENDOR_LIBRARY_FILENAMES'] = '/usr/share/glvnd/egl_vendor.d/50_mesa.json'
    environment['HOME'] = str(home)
    authority = root / ('xauthority-' + phase)
    authority.touch(mode=0o600)
    command = ['dbus-run-session', '--config-file=' + str(working / 'bus.conf'), '--',
               'xvfb-run', '--auth-file=' + str(authority), '--auto-servernum',
               '--server-args=-screen 0 1600x1000x24 -nolisten tcp', sys.executable,
               str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--',
               *sandbox(apphost, root, working), str(descriptor)]
    result = {'phase': phase, 'passed': False, 'command': command, 'privateHome': str(home)}
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
        if (home / '.dotnet/sdk').exists() or (home / '.nuget/packages').exists():
            raise ValueError('SDK or NuGet packages appeared in the private HOME')
        result['privateHomeSdkAndNugetPackagesAbsent'] = True
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
    closure = plugin_archive_closure(args.archive, args.archive_root, args.archive_member, apphost)
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
