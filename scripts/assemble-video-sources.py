#!/usr/bin/env python3
"""Assemble and verify the self-contained corresponding-source release asset."""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


SCHEMA = 'meowchan.current-video-sources.v1'
LINUX_RIDS = ('linux-x64', 'linux-arm64')
MAC_RIDS = ('osx-arm64', 'osx-x64')
WINDOWS_REFERENCE = 'docs/native-video-windows-inventory.json'
BASE = 'base/MeowChan-video-sources/'
MAC_NOTICES = 'macos/release-source/docs/'
LINUX_RECIPES = ('scripts/package-linux.py', 'scripts/package-linux-sources.py', 'scripts/check-video-native.py',
                 'docs/native-video-linux-sources.md')
ARCHIVE_OPTIONS = ('-t7z', '-m0=LZMA2', '-mx=3', '-md=32m', '-mmt=2', '-ms=on',
                   '-mtc=off', '-mta=off', '-mtm=off')
SHA = re.compile(r'[0-9a-f]{64}')
UNCHECKED = object()
LINUX_SPEC = importlib.util.spec_from_file_location('video_linux_sources', Path(__file__).with_name('package-linux-sources.py'))
linux_sources = importlib.util.module_from_spec(LINUX_SPEC)
LINUX_SPEC.loader.exec_module(linux_sources)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key: ' + key)
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def current_layout(version):
    return tuple(map(int, version.split('-')[0].split('.')[:2])) >= (1, 2)


def archive_name(version):
    return f'MeowChan-{version}-current-video-sources.7z'


def public_names(version):
    prefix = f'MeowChan-{version}-'
    return [prefix + suffix for suffix in (
        'win-x64.zip', 'osx-arm64.dmg', 'osx-x64.dmg', 'linux-x64.AppImage', 'linux-arm64.AppImage',
        'linux-x64-bundled-vlc.tar.gz', 'linux-x64-system-vlc.tar.gz',
        'linux-arm64-bundled-vlc.tar.gz', 'linux-arm64-system-vlc.tar.gz',
        'current-video-sources.7z')]


def safe_path(name):
    path = PurePosixPath(name)
    require(name and '/' != name[0] and '\\' not in name and ':' not in name and path.as_posix() == name
            and name != '.' and '..' not in path.parts and all(ord(c) >= 32 for c in name),
            'Unsafe source path: ' + repr(name))
    return name


def binary_record(path, name):
    require(path.is_file() and not path.is_symlink(), 'Missing regular package: ' + str(path))
    return {'file': name, 'bytes': path.stat().st_size, 'sha256': digest(path)}


def merge_sources(inputs, stage):
    files, canonical = {}, {}
    for namespace, path in inputs.items():
        with tarfile.open(path, 'r|gz') as archive:
            for member in archive:
                if member.isdir():
                    continue
                require(member.isfile(), 'Unexpected source archive member: ' + member.name)
                logical = namespace + '/' + safe_path(member.name)
                require(logical not in files, 'Duplicate source path: ' + logical)
                output = stage / logical
                output.parent.mkdir(parents=True, exist_ok=True)
                checksum = hashlib.sha256()
                with archive.extractfile(member) as source, output.open('xb') as target:
                    while chunk := source.read(1024 * 1024):
                        checksum.update(chunk)
                        target.write(chunk)
                sha = checksum.hexdigest()
                first = canonical.setdefault(sha, logical)
                if first != logical:
                    output.unlink()
                files[logical] = {'path': first, 'bytes': member.size, 'sha256': sha}
    return files


class SourceFiles:
    def __init__(self, root, files):
        self.root = root
        self.files = files

    def get(self, name, sha=UNCHECKED, size=UNCHECKED):
        row = self.files.get(name)
        require(row is not None and (sha is UNCHECKED or row['sha256'] == sha)
                and (size is UNCHECKED or row['bytes'] == size), 'Missing or changed source input: ' + name)
        return self.root / safe_path(row['path'])

    def json(self, name, sha=UNCHECKED):
        return read_json(self.get(name, sha))

    def resolve(self, *groups):
        for group in groups:
            for name in group:
                self.get(name)

    def verify(self):
        physical = set()
        for logical, row in self.files.items():
            safe_path(logical)
            path = safe_path(row['path'])
            require(self.files.get(path) == row and SHA.fullmatch(row['sha256'])
                    and type(row['bytes']) is int and row['bytes'] >= 0, 'Invalid source alias: ' + logical)
            if path in physical:
                continue
            physical.add(path)
            item = self.root / path
            require(item.is_file() and not item.is_symlink() and item.stat().st_size == row['bytes']
                    and digest(item) == row['sha256'], 'Missing or changed source file: ' + path)
        require(len(physical) == len({self.files[name]['sha256'] for name in physical}),
                'Duplicate source content')
        actual = set()
        for item in self.root.rglob('*'):
            require(not item.is_symlink() and (item.is_dir() or item.is_file()),
                    'Unexpected extracted source path: ' + str(item))
            if item.is_file():
                actual.add(item.relative_to(self.root).as_posix())
        require(actual == physical | {'manifest.json', 'README.md'}, 'Extracted source file set mismatch')


def component(tree, version, inputs, recipes, licenses, patches=()):
    require(isinstance(version, str) and version.strip() and inputs and recipes and licenses,
            'Incomplete corresponding source component')
    tree.resolve(inputs, recipes, licenses, patches)
    return {'version': version, 'inputs': list(inputs), 'recipes': list(recipes),
            'licenses': list(licenses), 'patches': list(patches)}


def base_components(tree):
    name = BASE + 'docs/native-video-source-manifest.json'
    manifest = tree.json(name)
    core, managed = manifest['core_source'], manifest['managed_binding_source']
    for item in (core, managed):
        tree.get(BASE + item['file'], item['sha256'], item['bytes'])
    contrib = []
    for item in manifest['inputs']:
        if item['in_companion_source_asset']:
            path = BASE + 'contrib-inputs/' + item['name']
            tree.get(path, item['sha256'], item['bytes'])
            contrib.append(path)
    require(bool(contrib), 'Empty shared VLC contrib inventory')
    core_path = BASE + core['file']
    licenses = [BASE + 'docs/native-video-licenses.txt', BASE + 'THIRD-PARTY-NOTICES.md']
    return {
        'vlc': component(tree, manifest['vlc_release'] + '+' + manifest['vlc_commit'],
                         [core_path, *contrib], [core_path, name], licenses, [core_path]),
        'libvlcsharp': component(tree, managed['tag'], [BASE + managed['file']], [BASE + managed['file']], licenses),
    }


def zip_native_files(path, prefix):
    files = {}
    with zipfile.ZipFile(path) as archive:
        require(len(archive.namelist()) == len(set(archive.namelist())), 'Duplicate native ZIP member')
        for info in archive.infolist():
            if not info.is_dir() and info.filename.startswith(prefix):
                name = safe_path(info.filename[len(prefix):])
                with archive.open(info) as stream:
                    files[name] = hashlib.file_digest(stream, 'sha256').hexdigest()
    return files


def windows_inventory(tree, packages, base, approved):
    name, asset = next(iter(packages.items()))
    require(approved['rid'] == 'win-x64' and approved['version'] == base['vlc']['version'],
            'Windows VLC source reference mismatch')
    native = zip_native_files(asset, 'libvlc/win-x64/')
    with zipfile.ZipFile(asset) as archive:
        require(all(not name.startswith('libvlc/') or name.startswith('libvlc/win-x64/')
                    for name in archive.namelist() if not name.endswith('/')), 'Windows ZIP has the wrong architecture')
    require(native and native == approved['files'], 'Windows ZIP native files differ from the reviewed VLC source inventory')
    byte_map = json.loads(tree.get(BASE + 'windows-publish-byte-map.json').read_text(encoding='utf-8'))
    require(set(native) == {path for path, _ in byte_map}, 'Windows source base path coverage mismatch')
    return {'components': base, 'native_files': {path: {'component': 'vlc', 'sha256': sha}
                                                 for path, sha in native.items()},
            'packages': [{**binary_record(asset, name), 'components': sorted(base)}]}


def descriptor_inputs(tree, directory, source, version, expected):
    descriptors = [name for name in expected if name.endswith('.dsc')]
    require(len(descriptors) == 1, 'Expected one source descriptor: ' + source)
    descriptor = descriptors[0]
    path = tree.get(directory + descriptor, expected[descriptor])
    entries = linux_sources.descriptor_files(path, source, version)
    require(entries.keys() | {descriptor} == expected.keys()
            and all(expected[name] == sha for name, (_, sha) in entries.items()), 'Distro source inventory mismatch: ' + source)
    for name, (size, sha) in entries.items():
        tree.get(directory + safe_path(name), sha, size)
    paths = sorted(directory + name for name in entries)
    patches = [name for name in paths if '.debian.tar.' in name or '.diff.gz' in name]
    return [directory + descriptor, *paths], patches


def linux_inventory(tree, packages, base, rid, commit, version):
    namespace = rid + '/'
    source = tree.json(namespace + 'manifest.json')
    runtime_name = namespace + f'inventories/{rid}-runtime-manifest.json'
    package_name = f'inventories/{rid}-package-manifest.json'
    package_manifest = tree.json(package_name)
    require(source['schema'] == 'meowchan.linux-sources.v1' and source['source_commit'] == commit
            and set(source['runtimes']) == {rid} and package_manifest['schema'] == 'meowchan.linux-packages.v1'
            and package_manifest['runtime'] == rid and package_manifest['version'] == version
            and package_manifest['source_commit'] == commit
            and source['runtimes'][rid]['sha256'] == package_manifest['runtime_manifest_sha256'],
            'Linux source inventory RID/package binding mismatch: ' + rid)
    runtime = tree.json(runtime_name, package_manifest['runtime_manifest_sha256'])
    require(runtime['schema'] == 'meowchan.linux-runtime.v1' and runtime['runtime'] == rid
            and runtime['files'] == source['runtimes'][rid]['native_runtime_files'] and runtime['files'],
            'Linux native source inventory architecture or file mismatch: ' + rid)
    licenses = namespace + 'licenses/' + rid + '/'
    common_licenses = []
    for name, sha in runtime['licenses'].items():
        tree.get(licenses + name, sha)
        if name.startswith('common-licenses/'):
            common_licenses.append(licenses + name)
    recipes = [namespace + 'packaging/' + name for name in LINUX_RECIPES]
    definitions = {}
    for row in source['distro_sources']:
        key = row['source'], row['version']
        require(key not in definitions, 'Duplicate distro source component: ' + row['source'])
        definitions[key] = row
    components = {'libvlcsharp': base['libvlcsharp']}
    binding = source['managed_binding']
    require(binding['sha256'] == tree.files[base['libvlcsharp']['inputs'][0]]['sha256'],
            'Linux managed binding version mismatch: ' + rid)
    tree.get(namespace + 'managed-binding/' + binding['file'], binding['sha256'], binding['bytes'])
    owners = {}
    for package in runtime['packages']:
        owner = package['package']
        arch = {'linux-x64': 'amd64', 'linux-arm64': 'arm64'}[rid]
        require(owner not in owners and package['architecture'] in (arch, 'all'),
                'Linux source package owner/architecture mismatch: ' + owner)
        key = package['source'], package['source_version']
        require(key in definitions, 'Missing distro source package: ' + repr(key))
        row = definitions[key]
        identity = '='.join(key)
        owners[owner] = identity
        license_file = licenses + owner.replace(':', '_') + '.copyright'
        if identity in components:
            tree.resolve([license_file])
            components[identity]['licenses'].append(license_file)
        else:
            inputs, patches = descriptor_inputs(tree, namespace + row['directory'] + '/', *key, row['inputs'])
            components[identity] = component(tree, key[1], inputs, recipes + [inputs[0], *patches],
                                            [license_file, *common_licenses], patches)
    native = {}
    for mapping in runtime['mappings']:
        path, owner = mapping['file'], mapping['package']
        require(path not in native and owner in owners and runtime['files'].get(path) == mapping['sha256'],
                'Unmapped Linux runtime member: ' + path)
        native[path] = {'component': owners[owner], 'sha256': mapping['sha256']}
    require(native.keys() == runtime['files'].keys(), 'Incomplete Linux native source mapping: ' + rid)
    appimage_components = []
    identities = ('type2-runtime', 'libfuse', 'squashfuse')
    for identity, row, pinned in zip(identities, source['appimage_runtime'], linux_sources.RUNTIME_INPUTS, strict=True):
        input_name = namespace + 'appimage-runtime/' + row['file']
        require(all(row.get(field) == pinned.get(field) for field in ('file', 'sha256', 'commit')),
                'AppImage source input mismatch: ' + identity)
        tree.get(input_name, row['sha256'], row['bytes'])
        license_file = namespace + 'packaging/docs/native-video-appimage-runtime-LICENSE'
        version = row.get('commit') or {'libfuse': '3.15.0', 'squashfuse': '0.5.2'}[identity]
        components[identity] = component(tree, version, [input_name], [input_name, *recipes], [input_name, license_file])
        appimage_components.append(identity)

    records = []
    for row in package_manifest['artifacts']:
        name, flavor = row['file'], row['flavor']
        record = binary_record(packages[name], name)
        require(all(record[key] == row[key] for key in record), 'Linux final package SHA-256/size mismatch: ' + name)
        prefix = 'usr/lib/meowchan/' if flavor == 'appimage' else ''
        payload = row['payload_files']
        native_prefix = prefix + 'libvlc/' + rid + '/'
        expected = {native_prefix + path: entry['sha256'] for path, entry in native.items()}
        actual = {name: sha for name, sha in payload.items() if name.startswith(prefix + 'libvlc/')}
        require(actual == ({} if flavor == 'system-vlc' else expected),
                'Linux final package native inventory mismatch: ' + name)
        if flavor == 'appimage':
            require(package_manifest['appimage_inputs']['runtime'] == linux_sources.common.tool_assets(rid)['runtime'],
                    'Wrong AppImage runtime architecture or digest')
        base_components = sorted(set(owners.values()) | {'libvlcsharp'})
        records.append({**record, 'components': (['libvlcsharp'] if flavor == 'system-vlc'
                        else sorted(base_components + (appimage_components if flavor == 'appimage' else [])))})
    require(len(records) == len(packages) == 3 and {row['file'] for row in records} == packages.keys(),
            'Incomplete Linux package inventory: ' + rid)
    return {'inventory': runtime_name, 'package_inventory': package_name, 'components': components,
            'native_files': native, 'packages': records}


def mac_inventory(tree, packages, base, commit, version):
    overlay = tree.json('macos/manifest.json')
    require(overlay['schema'] == 'meowchan.macos-source-overlay.v1' and overlay['version'] == version
            and overlay['application_source_commit'] == commit and set(overlay['architectures']) == set(MAC_RIDS),
            'Mac source overlay architecture/source mismatch')
    inputs_name = MAC_NOTICES + 'native-video-macos-source-manifest.json'
    inputs = tree.json(inputs_name, overlay['source_input_manifest_sha256'])
    require(inputs['vlc']['release'] + '+' + inputs['vlc']['exact_commit'] == base['vlc']['version'],
            'Mac VLC source version mismatch')
    tree.get('macos/application-source.tar', overlay['application_source_archive_sha256'])
    for recipe in inputs['contrib_comparison']['rows']:
        for item in recipe['base_inputs']:
            tree.get(BASE + 'contrib-inputs/' + item['name'], item['sha256'])
    results = {}
    for rid, row in overlay['architectures'].items():
        name = f'MeowChan-{version}-{rid}.dmg'
        record = binary_record(packages[name], name)
        inventory_path = 'macos/' + row['inventory']
        inventory = tree.json(inventory_path)
        require(row['signed_dmg'] == inventory['signed_dmg'] == record
                and row['inventory_zip'] == inventory['signed_zip'] and inventory['rid'] == rid,
                'Mac final DMG/ZIP source inventory binding mismatch: ' + rid)
        report_record = inventory['native_validation_report']
        report = tree.json('macos/' + report_record['path'], report_record['sha256'])
        require(report['source_commit'] == commit and report['rid'] == rid and report['dmg_sha256'] == record['sha256']
                and report['inventory_zip_sha256'] == row['inventory_zip']['sha256'],
                'Mac native evidence is from a different package or architecture: ' + rid)
        expected = {entry['path']: entry['sha256'] for entry in inputs['architectures'][rid]['original_files']}
        rows = inventory['native_files']
        prefix = f'MeowChan.app/Contents/Resources/application/libvlc/{rid}/'
        require(expected and len(rows) == len(expected)
                and {entry['original_path']: entry['original_sha256'] for entry in rows} == expected
                and all(entry['zip_path'] == prefix + entry['original_path'] for entry in rows),
                'Incomplete Mac native coverage: ' + rid)
        dylibs = {entry['zip_path'].removeprefix('MeowChan.app/'): entry['packaged_sha256']
                  for entry in rows if entry['zip_path'].endswith('.dylib')}
        mounted = report['dmg_validation']['copiedSignatures']['files']
        require(dylibs == {name: sha for name, sha in mounted.items()
                          if name.startswith(prefix.removeprefix('MeowChan.app/')) and name.endswith('.dylib')},
                'Mac source inventory differs from the final mounted DMG: ' + rid)
        vlc = base['vlc']
        components = {**base, 'vlc': component(tree, vlc['version'], vlc['inputs'], vlc['recipes'] + [
            inputs_name, 'macos/release-source/scripts/native-video/package-nuget-mac.sh',
            'macos/release-source/scripts/package-macos.py', 'macos/release-source/scripts/macos-signing.py'],
            vlc['licenses'] + [MAC_NOTICES + 'native-video-macos-COPYING'], vlc['patches'])}
        results[rid] = {'inventory': inventory_path, 'components': components,
                        'native_files': {entry['zip_path']: {'sha256': entry['packaged_sha256'], 'component': 'vlc'}
                                         for entry in rows},
                        'packages': [{**record, 'components': sorted(components)}]}
    return results


def runtimes(tree, assets, version, commit, approved):
    require(set(assets) == set(public_names(version)[:-1]), 'Public binary package names differ from source contract')
    base = base_components(tree)
    project = ET.parse(tree.get('macos/release-source/MeowChan.csproj'))
    versions = {node.get('Include'): node.get('Version') for node in project.iter('PackageReference')}
    mac_input = tree.json(MAC_NOTICES + 'native-video-macos-source-manifest.json')
    require(versions['LibVLCSharp.Avalonia'] == base['libvlcsharp']['version']
            and versions['VideoLAN.LibVLC.Windows'] == approved['nuget_version']
            and versions['VideoLAN.LibVLC.Mac'] == mac_input['native_package']['nuget_version'],
            'Application video dependency versions differ from the source inventory')
    result = {'win-x64': windows_inventory(tree, {name: path for name, path in assets.items() if name.endswith('-win-x64.zip')}, base, approved)}
    for rid in LINUX_RIDS:
        result[rid] = linux_inventory(tree, {name: path for name, path in assets.items() if f'-{rid}' in name},
                                      base, rid, commit, version)
    result.update(mac_inventory(tree, {name: path for name, path in assets.items() if name.endswith('.dmg')}, base, commit, version))
    return result


def approved_windows(repository, commit):
    raw = subprocess.check_output(['git', '-C', str(repository), 'show', commit + ':' + WINDOWS_REFERENCE])
    return json.loads(raw, object_pairs_hook=unique_object)


def unpack(archive, directory):
    command = ['7z', 'l', '-slt', str(archive.resolve())]
    listing = subprocess.check_output(command, text=True)
    header, _, entries = listing.partition('\n----------\n')
    require('Type = 7z\n' in header and 'Method = LZMA2' in header and 'Solid = +\n' in header,
            'Corresponding source archive must be solid 7z with LZMA2')
    paths = set()
    for line in entries.splitlines():
        if line.startswith('Path = '):
            name = safe_path(line.removeprefix('Path = '))
            require(name not in paths, 'Duplicate source archive entry: ' + name)
            paths.add(name)
        require(not line.startswith(('Symbolic Link = ', 'Hard Link = '))
                and not re.search(r'^Attributes = .* [lpsbc][r-]', line), 'Unexpected source archive link or device')
    require({'manifest.json', 'README.md'} <= paths, 'Source archive lacks its manifest or instructions')
    subprocess.run(['7z', 'x', '-y', str(archive.resolve())], cwd=directory, check=True, stdout=subprocess.DEVNULL)


def verify(archive, assets, version, commit, approved):
    require(archive.name == archive_name(version), 'Wrong public corresponding-source filename')
    with tempfile.TemporaryDirectory(prefix='meowchan-source-extract-', dir=archive.parent) as temporary:
        root = Path(temporary)
        unpack(archive, root)
        manifest = read_json(root / 'manifest.json')
        require(manifest['schema'] == SCHEMA and manifest['version'] == version
                and manifest['source_commit'] == commit, 'Corresponding source release identity mismatch')
        files = SourceFiles(root, manifest['files'])
        files.verify()
        require(manifest['runtimes'] == runtimes(files, assets, version, commit, approved),
                'Corresponding source references differ from final package inventories')


README = """# MeowChan corresponding video sources

The release version, binary package hashes and source components are in
`manifest.json`. Under `runtimes`, select a runtime and package, then follow the
component's inputs, recipes, patches and licenses. The `files` table gives the
physical path and SHA-256 for every logical path. Some identical files have
more than one logical name. All referenced paths are in this archive.

To recreate the original names after extraction, run this in the extracted
directory with Python 3 and keep the resulting directory structure:

    python3 - <<'PYTHON'
    import json
    from pathlib import Path
    from shutil import copyfile
    for name, record in json.loads(Path('manifest.json').read_text())['files'].items():
        if record['path'] != name:
            target = Path(name)
            target.parent.mkdir(parents=True, exist_ok=True)
            copyfile(record['path'], target)
    PYTHON

See the platform documentation alongside each set of components. Keep inner
upstream archives and their checksums intact. The AppImage runtime uses the
upstream recipe's Alpine build environment.
"""


def assemble(inputs, package_manifests, assets, archive, version, commit, approved):
    with tempfile.TemporaryDirectory(prefix='meowchan-source-stage-', dir=archive.parent) as temporary:
        stage = Path(temporary)
        files = merge_sources(inputs, stage)
        for rid, path in package_manifests.items():
            name = f'inventories/{rid}-package-manifest.json'
            target = stage / name
            target.parent.mkdir(exist_ok=True)
            shutil.copyfile(path, target)
            files[name] = {'path': name, 'sha256': digest(target), 'bytes': target.stat().st_size}
        tree = SourceFiles(stage, files)
        manifest = {'schema': SCHEMA, 'version': version, 'source_commit': commit, 'files': files,
                    'runtimes': runtimes(tree, assets, version, commit, approved)}
        (stage / 'README.md').write_text(README, encoding='utf-8')
        write_json(stage / 'manifest.json', manifest)
        subprocess.run(['7z', 'a', *ARCHIVE_OPTIONS, str(archive.resolve()), '.'],
                       cwd=stage, check=True, stdout=subprocess.DEVNULL)
    verify(archive, assets, version, commit, approved)
