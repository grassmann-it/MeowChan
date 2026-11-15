#!/usr/bin/env python3
"""Check a slim desktop payload without changing it.

Pass a flat publish directory or a macOS Contents/MacOS alias view. Bundle
signatures, native architectures and symlink confinement are checked separately.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import os
import re
import stat


LEGAL_DOCS = (
    'native-video-licenses.txt',
    'native-video-sources.md',
    'native-video-linux-sources.md',
    'native-video-source-manifest.json',
    'native-video-macos-sources.md',
    'native-video-macos-source-manifest.json',
    'native-video-macos-COPYING',
)
LEGAL_FILES = ('LICENSE', 'THIRD-PARTY-NOTICES.md', *(f'docs/{name}' for name in LEGAL_DOCS))
FORBIDDEN_ROOT_DOCUMENTS = frozenset(('readme.md', 'changelog.md'))
FORBIDDEN_REPOSITORY_ROOTS = frozenset(('desktop', 'libmeowchan', 'tests', 'samples'))
FORBIDDEN_DEVELOPER_FILES = frozenset(('meowchan.pdb', 'meowchan.xml', 'libmeowchan.pdb', 'libmeowchan.xml'))
FORBIDDEN_PAYLOAD_COMPONENTS = frozenset(('bin', 'obj', '.git', '.vs', 'profile', 'profiles'))
RUNTIMES = ('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')


def require_file(root: Path, relative: str) -> int:
    path = root / relative
    # validate_payload has already confined first-level macOS aliases. A link
    # deeper in the payload is never a legal file input.
    if path.is_symlink() and len(Path(relative).parts) > 1:
        raise ValueError('Nested desktop package symlink is not allowed: ' + relative)
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError('Required desktop package file missing or empty: ' + relative)
    metadata = path.stat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1:
        raise ValueError('Required desktop package file is not a single-link regular file: ' + relative)
    return metadata.st_size


# macOS ships /tmp, /var and /etc as symlinks into /private. These three
# first-level aliases are the only parent-path links tolerated for the package
# root: the alias must match a listed absolute path (a direct child of the
# filesystem root), resolve to exactly its listed physical target and must not
# be the root directory itself. Every other link in the root path -
# including the root directory itself, any user-created parent alias, or a
# payload entry - is still rejected. The accepted alias is replaced by its
# physical target, so later checks and the reported root use the real path.
SYSTEM_PARENT_ALIASES = {'/tmp': '/private/tmp', '/var': '/private/var', '/etc': '/private/etc'}


def _system_alias_target(component):
    target = SYSTEM_PARENT_ALIASES.get(str(component))
    if target is not None and os.path.realpath(component) == target and not Path(target).is_symlink():
        return Path(target)
    return None


def _ordinary_directory(path: Path) -> Path:
    """Return the physical package root; only the named system aliases may be links."""
    absolute = Path(os.path.abspath(path))
    parts = absolute.parts[1:]
    current = Path(absolute.root)
    for index, part in enumerate(parts):
        current /= part
        if current.is_symlink():
            target = _system_alias_target(current) if index < len(parts) - 1 else None
            if target is None:
                raise ValueError('Desktop package root path contains a symlink: ' + str(current))
            current = target
    if not current.is_dir():
        raise ValueError('Desktop package root must be a directory')
    return current


def _internal_payload_target(root: Path, path: Path, relative: Path | None = None) -> Path:
    """Validate a publish-time relative link without following external data.

    Native macOS runtimes legitimately contain file aliases (for example VLC's
    libvlccore alias). They are safe only when they are relative, resolve
    strictly inside this physical payload, and end at a regular file. Bundle
    layout aliases are handled separately by _macos_alias_target because they
    may target directories as well.
    """
    target = Path(os.readlink(path))
    nested = relative is not None and len(relative.parts) > 1
    if target.is_absolute():
        message = ('Nested desktop package symlink is not allowed: ' + relative.as_posix()
                   if nested else 'Desktop package symlink must be relative: ' + str(path))
        raise ValueError(message)
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as error:
        message = ('Nested desktop package symlink is not allowed: ' + relative.as_posix()
                   if nested else 'Invalid desktop package symlink target: ' + str(path))
        raise ValueError(message) from error
    if not resolved.is_relative_to(root) or not resolved.is_file():
        message = ('Nested desktop package symlink is not allowed: ' + relative.as_posix()
                   if nested else 'Desktop package symlink target escapes the payload: ' + str(path))
        raise ValueError(message)
    return resolved


def _bundle_alias_view(root: Path) -> bool:
    return ((root.name == 'MacOS' and root.parent.name == 'Contents')
            or (root.name == 'application' and root.parent.name == 'Resources'
                and root.parent.parent.name == 'Contents'))


def _macos_alias_target(root: Path, path: Path) -> Path:
    """Accept only the relative aliases generated by package-macos.py."""
    if root.name == 'MacOS' and root.parent.name == 'Contents' and path.name != 'MeowChan':
        relative = Path('../Resources/application') / path.name
    elif (root.name == 'application' and root.parent.name == 'Resources'
          and root.parent.parent.name == 'Contents' and path.name == 'MeowChan'):
        relative = Path('../../MacOS/MeowChan')
    else:
        raise ValueError('Unexpected desktop package symlink: ' + str(path))
    if os.readlink(path) != str(relative):
        raise ValueError('Unexpected macOS desktop package symlink target: ' + str(path))
    target = Path(os.path.abspath(root / relative))
    # Validate each physical parent too: an exact textual alias must not hide a
    # redirected Resources/application directory or another target-side link.
    _ordinary_directory(target.parent)
    if target.is_symlink() or not (target.is_file() or target.is_dir()):
        raise ValueError('Invalid macOS desktop package symlink target: ' + str(path))
    if path.name == 'MeowChan' and not target.is_file():
        raise ValueError('Invalid macOS apphost symlink target: ' + str(path))
    return target


def _payload_entries(root: Path):
    """Walk physical alias targets as well, without following any nested link."""
    def entries(directory, prefix):
        for path in directory.iterdir():
            relative = prefix / path.name
            was_symlink = path.is_symlink()
            followed_bundle_alias = False
            if was_symlink:
                # First-level links in the two bundle alias views are created
                # by package-macos.py. Recognise them by their exact textual
                # target; any other relative link is a publish file alias
                # (including nested VLC aliases).
                generated_target = None
                if root.name == 'MacOS' and root.parent.name == 'Contents' and path.name != 'MeowChan':
                    generated_target = str(Path('../Resources/application') / path.name)
                elif (root.name == 'application' and root.parent.name == 'Resources'
                      and root.parent.parent.name == 'Contents' and path.name == 'MeowChan'):
                    generated_target = str(Path('../../MacOS/MeowChan'))
                if (_bundle_alias_view(root) and generated_target is not None
                        and os.readlink(path) == generated_target):
                    path = _macos_alias_target(root, path)
                    followed_bundle_alias = True
                else:
                    path = _internal_payload_target(root, path, relative)
            yield path, relative
            if path.is_dir() and (not was_symlink or followed_bundle_alias):
                yield from entries(path, relative)
    yield from entries(root, Path())


def validate_payload(root: Path) -> dict:
    """Validate legal/docs layout independent of RID; follow legitimate Mac aliases."""
    root = _ordinary_directory(Path(root))
    for entry in root.iterdir():
        name = entry.name.casefold()
        if name in FORBIDDEN_ROOT_DOCUMENTS or name in FORBIDDEN_REPOSITORY_ROOTS or name in FORBIDDEN_DEVELOPER_FILES:
            raise ValueError('Repository/development artifact in desktop package: ' + entry.name)
    for path, relative in _payload_entries(root):
        if any(part.casefold() in FORBIDDEN_PAYLOAD_COMPONENTS for part in relative.parts):
            raise ValueError('Build or profile artifact in desktop package: ' + relative.as_posix())
        if not (path.is_file() or path.is_dir()):
            raise ValueError('Desktop package contains a special file: ' + relative.as_posix())
        if path.is_file() and path.stat().st_nlink != 1:
            raise ValueError('Desktop package contains a hardlink: ' + relative.as_posix())
    legal_bytes = sum(require_file(root, relative) for relative in LEGAL_FILES)
    documentation = root / 'docs'
    actual = {entry.name for entry in documentation.iterdir()}
    if actual != set(LEGAL_DOCS):
        raise ValueError('Desktop docs must contain only the approved legal files; unexpected entries: '
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
        current_stat = current.stat()
        if current_stat.st_nlink != 1:
            raise ValueError('Bundled Linux license tree contains a hardlink: ' + name)
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
