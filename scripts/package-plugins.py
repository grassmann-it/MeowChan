#!/usr/bin/env python3
"""Verify the external plugin payload produced by packaging/PluginDelivery.targets.

No build, code loading, network access, or package mutation occurs here. The MSBuild
restore graph owns all helper/plugin dependencies; verification never repairs a package.
"""
import argparse
import hashlib
import json
import re
import stat
import struct
from pathlib import Path

RUNTIMES = ('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
PLUGINS = ('MeowChan.Plugin.FourChan', 'MeowChan.Plugin.EightKun', 'MeowChan.Plugin.Lainchan',
           'MeowChan.Plugin.Kohlchan', 'MeowChan.ImageDimensionsFilter', 'MeowChan.Plugin.Wizchan', 'MeowChan.Plugin.Endchan',
           'MeowChan.FileRulesFilter', 'MeowChan.HashBlocklistFilter', 'MeowChan.AnimationFormatFilter', 'MeowChan.PngMetadataFilter',
           'MeowChan.Plugin.Leftypol', 'MeowChan.Plugin.Dvach', 'MeowChan.Plugin.Uboachan', 'MeowChan.Plugin.Zzzchan', 'MeowChan.Plugin.SushiChan')
SOURCES = ('Lainchan.cs', 'Kohlchan.cs', 'ImageDimensionsFilter.cs', 'Wizchan.cs', 'Endchan.cs',
           'FileRulesFilter.cs', 'HashBlocklistFilter.cs', 'AnimationFormatFilter.cs', 'PngMetadataFilter.cs',
           'Leftypol.cs', 'Dvach.cs', 'Uboachan.cs', 'Zzzchan.cs', 'SushiChan.cs')
OPTIONS = 'net10.0;csharp14;release;deterministic;nullable=enable;unsafe=false;generators=none;v1'
HASH = re.compile(r'[0-9a-f]{64}\Z')


def regular(root, relative, maximum=128 * 1024 * 1024):
    if not isinstance(relative, str) or not relative or '\\' in relative or ':' in relative or relative.startswith('/'):
        raise ValueError('Invalid relative package path: ' + repr(relative))
    parts = relative.split('/')
    if any(part in ('', '.', '..') for part in parts):
        raise ValueError('Package path escapes or is not normalized: ' + relative)
    current = Path(root)
    for part in parts:
        current = current / part
        if current.is_symlink():
            raise ValueError('Plugin package paths cannot traverse symlinks: ' + relative)
    if not current.exists() or not stat.S_ISREG(current.stat().st_mode) or not 0 < current.stat().st_size <= maximum:
        raise ValueError('Missing, empty, oversized or special package file: ' + relative)
    return current


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def json_file(root, relative):
    data = json.loads(regular(root, relative, 1024 * 1024).read_text(encoding='utf-8'))
    if not isinstance(data, dict):
        raise ValueError('Package JSON must be an object: ' + relative)
    return data


def hash_parts(parts):
    result = hashlib.sha256()
    for part in parts:
        encoded = part.encode('utf-8')
        result.update(struct.pack('!i', len(encoded)))
        result.update(encoded)
    return result.hexdigest()


def file_set_hash(files):
    return hash_parts(value for item in sorted(files, key=lambda row: row['Path'])
                      for value in (item['Role'], item['Path'], item['Sha256']))


def native_architecture(path, runtime):
    with path.open('rb') as stream:
        header = stream.read(4096)
    arch = runtime.split('-')[1]
    if runtime.startswith('win-'):
        if len(header) < 64 or header[:2] != b'MZ':
            raise ValueError('Expected a PE apphost: ' + str(path))
        offset = struct.unpack_from('<I', header, 0x3c)[0]
        if offset + 6 > len(header) or header[offset:offset + 4] != b'PE\0\0' or struct.unpack_from('<H', header, offset + 4)[0] != 0x8664:
            raise ValueError('PE helper architecture does not match ' + runtime)
    elif runtime.startswith('linux-'):
        expected = 62 if arch == 'x64' else 183
        if len(header) < 20 or header[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', header, 18)[0] != expected:
            raise ValueError('ELF helper architecture does not match ' + runtime)
    else:
        expected = 0x01000007 if arch == 'x64' else 0x0100000c
        if len(header) < 8 or header[:4] != b'\xcf\xfa\xed\xfe' or struct.unpack_from('<I', header, 4)[0] != expected:
            raise ValueError('Mach-O helper architecture does not match ' + runtime)
    if not runtime.startswith('win-') and not path.stat().st_mode & stat.S_IXUSR:
        raise ValueError('Unix helper is not executable: ' + str(path))


def verify_toolchain(root):
    compiler = root / 'plugin-compiler'
    manifest = json_file(compiler, 'toolchain.json')
    files = manifest.get('Files')
    if manifest.get('SchemaVersion') != 1 or manifest.get('TargetFramework') != 'net10.0' or manifest.get('Options') != OPTIONS:
        raise ValueError('Unsupported compiler toolchain contract')
    if not isinstance(files, list) or not 4 <= len(files) <= 512:
        raise ValueError('Compiler toolchain file inventory is incomplete')
    seen = set()
    for item in files:
        if not isinstance(item, dict) or item.get('Role') not in ('compiler', 'sdk', 'framework') or not HASH.fullmatch(str(item.get('Sha256', ''))):
            raise ValueError('Invalid compiler toolchain entry')
        key = str(item.get('Path')).casefold()
        if key in seen:
            raise ValueError('Duplicate compiler toolchain file')
        seen.add(key)
        if digest(regular(compiler, item['Path'], 64 * 1024 * 1024)) != item['Sha256']:
            raise ValueError('Compiler toolchain hash differs: ' + item['Path'])
    sdk = [item for item in files if item['Role'] == 'sdk']
    compilers = [item for item in files if item['Role'] == 'compiler']
    references = [item for item in files if item['Role'] != 'compiler']
    if len(sdk) != 1 or len(compilers) < 3 or not any(item['Role'] == 'framework' for item in files):
        raise ValueError('Compiler toolchain roles are incomplete')
    if (manifest.get('CompilerFingerprint') != file_set_hash(compilers)
            or manifest.get('ReferenceFingerprint') != file_set_hash(references)
            or manifest.get('SdkFingerprint') != sdk[0]['Sha256']):
        raise ValueError('Compiler toolchain fingerprints differ')
    for relative in ('licenses/framework/LICENSE.TXT', 'licenses/framework/THIRD-PARTY-NOTICES.TXT',
                     'licenses/roslyn/LICENSE-Roslyn.txt', 'licenses/roslyn/ThirdPartyNotices.rtf', 'licenses/roslyn/provenance.json'):
        regular(compiler, relative)
    if digest(regular(root / 'plugin-worker', 'MeowChan.PluginSdk.dll')) != manifest['SdkFingerprint']:
        raise ValueError('Worker SDK does not match compiler SDK')
    return manifest


def verify(root, runtime, development=False):
    if runtime not in RUNTIMES:
        raise ValueError('Unsupported plugin package runtime')
    root = Path(root).resolve(strict=True)
    if not root.is_dir():
        raise ValueError('Plugin package root must be a directory')
    for subtree in ('plugin-worker', 'plugin-compiler', 'plugins'):
        directory = root / subtree
        if directory.is_symlink() or not directory.is_dir():
            raise ValueError('Required regular plugin directory missing: ' + subtree)
        for path in directory.rglob('*'):
            if path.is_symlink() or not (path.is_file() or path.is_dir()):
                raise ValueError('Plugin payload contains a link or special file: ' + str(path))
    for directory, name in (('plugin-worker', 'MeowChan.PluginWorker'), ('plugin-compiler', 'MeowChan.PluginCompiler')):
        helper = root / directory
        native_architecture(regular(helper, name + ('.exe' if runtime.startswith('win-') else '')), runtime)
        regular(helper, name + '.dll')
        regular(helper, name + '.deps.json')
        config = json_file(helper, name + '.runtimeconfig.json').get('runtimeOptions', {})
        if not development:
            if config.get('framework') or config.get('frameworks') or not config.get('includedFrameworks'):
                raise ValueError('Published helper requires an external .NET runtime: ' + directory)
            native = ('coreclr.dll', 'hostpolicy.dll', 'hostfxr.dll') if runtime.startswith('win-') else (
                ('libcoreclr.dylib', 'libhostpolicy.dylib', 'libhostfxr.dylib') if runtime.startswith('osx-')
                else ('libcoreclr.so', 'libhostpolicy.so', 'libhostfxr.so'))
            for filename in native:
                regular(helper, filename)
    toolchain = verify_toolchain(root)
    manifests = {path.name for path in (root / 'plugins').glob('*.plugin.json')}
    expected = {name + '.plugin.json' for name in PLUGINS}
    if manifests != expected:
        raise ValueError('Shipped plugin manifests differ from the required plugin inventory')
    for name in PLUGINS:
        bundle = json_file(root / 'plugins', name + '.plugin.json')
        if bundle.get('SchemaVersion') != 1 or bundle.get('Resources') != []:
            raise ValueError('Unexpected shipped plugin manifest schema/resources: ' + name)
        if bundle.get('AssemblyRelativePath') != 'assemblies/' + name + '.dll':
            raise ValueError('Plugin assembly path differs from its declared package identity: ' + name)
        assembly = regular(root / 'plugins', bundle['AssemblyRelativePath'], 32 * 1024 * 1024)
        if digest(assembly) != bundle.get('AssemblySha256'):
            raise ValueError('Plugin assembly hash differs: ' + name)
        if bundle.get('SdkFingerprint') != toolchain['SdkFingerprint'] or bundle.get('CompilerFingerprint') != toolchain['CompilerFingerprint']:
            raise ValueError('Plugin build/SDK fingerprint differs: ' + name)
    if any(path.is_file() and path.name not in expected for path in (root / 'plugins').iterdir()):
        raise ValueError('Shipped plugin root contains an unintended installation candidate')
    for name in SOURCES:
        regular(root / 'plugins', 'sources/' + name, 4 * 1024 * 1024)
    for deps in root.glob('*.deps.json'):
        libraries = json_file(root, deps.name).get('libraries', {})
        if any(name.startswith(('Microsoft.CodeAnalysis', 'MeowChan.PluginCompiler/', 'MeowChan.PluginWorker/')) for name in libraries):
            raise ValueError('Helper/compiler dependency leaked into the main process: ' + deps.name)
    return {'runtime': runtime, 'self_contained_required': not development, 'plugins': len(PLUGINS),
            'editable_sources': len(SOURCES), 'compiler_files_verified': len(toolchain['Files']),
            'sdk_sha256': toolchain['SdkFingerprint'], 'root': str(root), 'passed': True}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify', type=Path, required=True, metavar='DIRECTORY')
    parser.add_argument('--runtime', choices=RUNTIMES, required=True)
    parser.add_argument('--development', action='store_true', help='Allow framework-dependent source-build helpers')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(argv)
    try:
        report = verify(args.verify, args.runtime, args.development)
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(1, 'Plugin package verification failed: ' + str(error) + '\n')
    content = json.dumps(report, sort_keys=True, indent=2) + '\n'
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(content, encoding='utf-8')
    print(content, end='')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
