#!/usr/bin/env python3
"""Check a slim desktop payload without changing it or inspecting native trees.

Pass a flat publish directory or a macOS Contents/MacOS alias view. Bundle
signatures, native architectures and symlink confinement are checked separately.
"""
import argparse
import json
from pathlib import Path


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
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path, help='publish directory or macOS Contents/MacOS')
    parser.add_argument('rid', choices=RUNTIMES)
    args = parser.parse_args(argv)
    print(json.dumps(validate(args.directory, args.rid), sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
