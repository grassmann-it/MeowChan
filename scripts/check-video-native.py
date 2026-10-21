#!/usr/bin/env python3
"""Check the selected external LibVLC tree in a publish/extracted app directory."""
import argparse
import json
from pathlib import Path
import struct
import hashlib


def validate(root: Path, rid: str) -> dict:
    native = root / 'libvlc'
    if rid.startswith('linux-'):
        marker = root / 'linux-video-runtime.json'
        if not marker.is_file():
            raise ValueError('Official Linux packages require linux-video-runtime.json')
        policy = json.loads(marker.read_text())
        if policy.get('SchemaVersion') != 1 or policy.get('RuntimeIdentifier') != rid or policy.get('Mode') not in ('system', 'bundled'):
            raise ValueError('Invalid Linux video runtime policy')
        if policy['Mode'] == 'system':
            if native.exists():
                raise ValueError('System VLC package unexpectedly contains a private runtime')
            return {'rid': rid, 'backend': 'system LibVLC (runtime prerequisite)'}
        tree = native / rid
        if not tree.is_dir() or sorted(p.name for p in native.iterdir()) != [rid]:
            raise ValueError('Missing selected Linux native tree or extra architectures')
        expected_files = policy.get('Files', {})
        if not isinstance(expected_files, dict) or not {'lib/libvlc.so.5', 'lib/libvlccore.so.9'} <= expected_files.keys():
            raise ValueError('Missing Linux LibVLC/core manifest entries')
        if not (tree / 'lib/vlc/share').is_dir() or not any(p.startswith('lib/vlc/plugins/') and p.endswith('.so') for p in expected_files):
            raise ValueError('Linux bundle lacks private data or plugins')
        actual = {}
        libraries = 0
        for path in tree.rglob('*'):
            if path.is_symlink():
                raise ValueError('Linux distribution payload must materialize symlinks')
            if path.is_file():
                relative = path.relative_to(tree).as_posix()
                digest = hashlib.sha256()
                with path.open('rb') as stream:
                    header = stream.read(20)
                    digest.update(header)
                    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                        digest.update(chunk)
                actual[relative] = digest.hexdigest()
                if header.startswith(b'\x7fELF'):
                    machine = 62 if rid == 'linux-x64' else 183
                    if len(header) < 20 or header[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', header, 18)[0] != machine:
                        raise ValueError('Linux native ELF architecture mismatch')
                    libraries += 1
        if actual != expected_files:
            raise ValueError('Linux native payload file inventory/hash mismatch')
        return {'rid': rid, 'backend': 'bundled LibVLC', 'native_libraries': libraries, 'native_files': len(actual)}
    tree = native / rid
    if not tree.is_dir() or sorted(p.name for p in native.iterdir()) != [rid]:
        raise ValueError('Missing selected native tree or extra native architectures')
    windows = rid == 'win-x64'
    suffix = '.dll' if windows else '.dylib'
    files = list(tree.rglob('*'))
    for path in files:
        if path.is_symlink() and (path.readlink().is_absolute() or not path.resolve().is_relative_to(tree.resolve())):
            raise ValueError(f'Unsafe native symlink: {path}')
    libraries = [p for p in files if p.is_file() and p.suffix == suffix]
    names = {p.name for p in libraries}
    for component in ['vlc', 'vlccore', 'mkv_plugin', 'vpx_plugin', 'vorbis_plugin', 'opus_plugin',
                      'mmdevice_plugin' if windows else 'auhal_plugin',
                      'direct3d11_plugin' if windows else 'vout_macosx_plugin']:
        if 'lib' + component + suffix not in names:
            raise ValueError(f'Missing native component: {component}')
    expected = 0x8664 if windows else (0x100000c if rid == 'osx-arm64' else 0x1000007)
    for path in libraries:
        with path.open('rb') as stream:
            header = stream.read(64)
            if windows:
                if header[:2] != b'MZ':
                    raise ValueError(f'Invalid PE: {path}')
                stream.seek(struct.unpack_from('<I', header, 60)[0])
                pe = stream.read(6)
                actual = struct.unpack_from('<H', pe, 4)[0] if pe[:4] == b'PE\0\0' else -1
            else:
                actual = struct.unpack_from('<I', header, 4)[0] if header[:4] == b'\xcf\xfa\xed\xfe' else -1
            if actual != expected:
                raise ValueError(f'Native architecture mismatch: {path} ({actual:#x})')
    return {'rid': rid, 'backend': 'bundled LibVLC', 'native_libraries': len(libraries),
            'native_files': sum(p.is_file() for p in files),
            'native_bytes': sum(p.stat().st_size for p in files if p.is_file())}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path, help='publish directory or macOS Contents/MacOS')
    parser.add_argument('rid', choices=['win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64'])
    args = parser.parse_args()
    print(json.dumps(validate(args.directory, args.rid), sort_keys=True))
