#!/usr/bin/env python3
"""Check a slim desktop payload without changing it or inspecting native trees.

Pass a flat publish directory or a macOS Contents/MacOS alias view. Bundle
signatures, native architectures and symlink confinement are checked separately.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re


LEGAL_DOCS = (
    'native-video-licenses.txt',
    'native-video-sources.md',
    'native-video-source-manifest.json',
    'native-video-macos-sources.md',
    'native-video-macos-source-manifest.json',
    'native-video-macos-COPYING',
)
LEGAL_FILES = ('LICENSE', 'THIRD-PARTY-NOTICES.md', *(f'docs/{name}' for name in LEGAL_DOCS))
FORBIDDEN_ROOT_DOCUMENTS = frozenset(('readme.md', 'changelog.md'))
FORBIDDEN_REPOSITORY_ROOTS = frozenset(('desktop', 'libmeowchan', 'tests', 'samples'))
FORBIDDEN_DEVELOPER_FILES = frozenset(('meowchan.pdb', 'meowchan.xml', 'libmeowchan.pdb', 'libmeowchan.xml'))
RUNTIMES = ('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')


def require_file(root: Path, relative: str) -> int:
    path = root / relative
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError('Required desktop package file missing or empty: ' + relative)
    return path.stat().st_size


def validate_payload(root: Path) -> dict:
    """Validate legal/docs layout independent of RID; follow legitimate Mac aliases."""
    root = Path(root).resolve(strict=True)
    if not root.is_dir():
        raise ValueError('Desktop package root must be a directory')
    for entry in root.iterdir():
        name = entry.name.casefold()
        if name in FORBIDDEN_ROOT_DOCUMENTS or name in FORBIDDEN_REPOSITORY_ROOTS or name in FORBIDDEN_DEVELOPER_FILES:
            raise ValueError('Repository/development artifact in desktop package: ' + entry.name)
    legal_bytes = sum(require_file(root, relative) for relative in LEGAL_FILES)
    documentation = root / 'docs'
    actual = {entry.name for entry in documentation.iterdir()}
    if actual != set(LEGAL_DOCS):
        raise ValueError('Desktop docs must contain only the six legal files; unexpected entries: '
                         + ', '.join(sorted(actual - set(LEGAL_DOCS))))
    icon_bytes = require_file(root, 'Assets/meowchan.png')
    return {'root': str(root), 'legal_files': len(LEGAL_FILES), 'legal_bytes': legal_bytes,
            'documentation_files': len(LEGAL_DOCS), 'icon_bytes': icon_bytes}


def validate(root: Path, runtime: str) -> dict:
    if runtime not in RUNTIMES:
        raise ValueError('Unsupported desktop package runtime: ' + runtime)
    result = validate_payload(root)
    executable = 'MeowChan.exe' if runtime.startswith('win-') else 'MeowChan'
    result.update(rid=runtime, apphost=executable, apphost_bytes=require_file(Path(result['root']), executable))
    if runtime.startswith('linux-'):
        result.update(validate_linux_notices(Path(result['root']), runtime))
    return result


def bounded_json(path: Path) -> dict:
    if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= 8 * 1024 * 1024:
        raise ValueError('Linux package metadata must be a bounded regular file: ' + path.name)
    value = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(value, dict):
        raise ValueError('Linux package metadata must be an object: ' + path.name)
    return value


def validate_linux_notices(root: Path, runtime: str) -> dict:
    marker_path = root / 'linux-video-runtime.json'
    inventory_path = root / 'linux-video-runtime-inventory.json'
    legal_root = root / 'linux-video-licenses'
    if not marker_path.exists() and not marker_path.is_symlink():
        if any(path.exists() or path.is_symlink() for path in (inventory_path, legal_root)):
            raise ValueError('Linux bundled notices require package metadata')
        return {'linux_video_mode': 'system (source publish)'}
    marker = bounded_json(marker_path)
    mode = marker.get('Mode')
    if type(marker.get('SchemaVersion')) is not int or marker['SchemaVersion'] != 1 or marker.get('RuntimeIdentifier') != runtime or mode not in ('bundled', 'system'):
        raise ValueError('Linux package metadata does not match the selected runtime/mode')
    if mode == 'system':
        if any(path.exists() or path.is_symlink() for path in (inventory_path, legal_root)):
            raise ValueError('System VLC package unexpectedly contains bundled runtime notices')
        return {'linux_video_mode': mode}

    inventory = bounded_json(inventory_path)
    licenses = inventory.get('license_files')
    if not isinstance(licenses, dict) or not 0 < len(licenses) <= 10000:
        raise ValueError('Bundled Linux runtime lacks its license inventory')
    if legal_root.is_symlink() or not legal_root.is_dir():
        raise ValueError('Bundled Linux runtime lacks its regular license directory')
    total = 0
    for name, expected in licenses.items():
        if not isinstance(name, str):
            raise ValueError('Invalid bundled Linux license path/hash')
        path = PurePosixPath(name)
        if (not name or '\\' in name or path.is_absolute()
                or any(part in ('', '.', '..') for part in name.split('/')) or str(path) != name
                or not isinstance(expected, str) or not re.fullmatch('[0-9a-fA-F]{64}', expected)):
            raise ValueError('Invalid bundled Linux license path/hash')
        current = legal_root
        for part in path.parts:
            current = current / part
            if current.is_symlink():
                raise ValueError('Bundled Linux license path cannot contain symlinks')
        size = require_file(legal_root, name)
        total += size
        if size > 64 * 1024 * 1024 or total > 256 * 1024 * 1024:
            raise ValueError('Bundled Linux license inventory exceeds its byte limit')
        with current.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != expected.lower():
                raise ValueError('Bundled Linux license checksum differs: ' + name)
    actual = set()
    for path in legal_root.rglob('*'):
        if path.is_symlink():
            raise ValueError('Bundled Linux license tree cannot contain symlinks')
        if path.is_file():
            actual.add(path.relative_to(legal_root).as_posix())
        elif not path.is_dir():
            raise ValueError('Bundled Linux license tree contains a special file')
    if actual != set(licenses):
        raise ValueError('Bundled Linux license files differ from their inventory')
    return {'linux_video_mode': mode, 'linux_license_files': len(licenses), 'linux_license_bytes': total}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path, help='publish directory or macOS Contents/MacOS')
    parser.add_argument('rid', choices=RUNTIMES)
    args = parser.parse_args(argv)
    print(json.dumps(validate(args.directory, args.rid), sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
