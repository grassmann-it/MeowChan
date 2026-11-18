#!/usr/bin/env python3
"""Export pinned local release source plus synthetic overlay; no build or profile."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess

REVISION = 'eec6c0169f8d52f7481ea0bf7febfd20981e97c3'
MAX_FILES = 20000
MAX_FILE = 32 * 1024 * 1024
MAX_TOTAL = 512 * 1024 * 1024
OVERLAY = 'tests/Upgrade102FixtureGenerator/'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    destination = args.destination.absolute()
    if destination.exists() or destination.is_symlink():
        parser.error('destination must not exist (including dangling links)')
    # Disable lazy fetching in partial clones as well as replacement objects.
    env = dict(os.environ, GIT_NO_LAZY_FETCH='1', GIT_NO_REPLACE_OBJECTS='1',
               GIT_TERMINAL_PROMPT='0')

    def git(*arguments):
        return subprocess.check_output(['git', *arguments], cwd=root, env=env, timeout=30)

    entries = []
    total = 0
    # NUL-delimited plumbing avoids git show's human-readable tree quoting.
    for row in git('ls-tree', '-rlz', '--full-tree', REVISION).split(b'\0'):
        if not row:
            continue
        metadata, raw_path = row.split(b'\t', 1)
        mode, kind, oid, size = metadata.split()
        path = raw_path.decode('utf-8')
        parts = PurePosixPath(path).parts
        if (not parts or path.startswith('/') or '\\' in path or
                any(p in ('.', '..', '.git') for p in parts) or
                any(ord(c) < 32 for c in path)):
            raise ValueError('Unsupported source path: ' + repr(path))
        if kind != b'blob' or mode not in (b'100644', b'100755', b'120000'):
            raise ValueError('Unsupported source entry: ' + path)
        length = int(size)
        total += length
        entries.append((path, mode.decode(), oid.decode(), length))
        if length > MAX_FILE or total > MAX_TOTAL or len(entries) > MAX_FILES:
            raise ValueError('Pinned source exceeds export limits')
    if not entries or any(p.startswith(OVERLAY) or p == 'UPGRADE-102-SOURCE.json' for p, *_ in entries):
        raise ValueError('Empty tree or overlay collision')
    destination.mkdir(parents=True)
    files = {}
    for path, mode, oid, length in sorted(entries):
        data = git('cat-file', 'blob', oid)
        if len(data) != length:
            raise ValueError('Blob length mismatch: ' + path)
        target = destination / path
        target.parent.mkdir(parents=True, exist_ok=True)
        # Even symlink blobs stay inert ordinary files. Preserve executable bits.
        with target.open('xb') as stream:
            stream.write(data)
        target.chmod(0o755 if mode == '100755' else 0o644)
        files[path] = {'sha256': hashlib.sha256(data).hexdigest(), 'bytes': length,
                       'git_blob': oid, 'git_mode': mode}
    overlays = {}
    for name in ('Program.cs', 'UpgradeFixtureGenerator.csproj'):
        data = Path(__file__).with_name(name).read_bytes()
        target = destination / OVERLAY / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open('xb') as stream:
            stream.write(data)
        overlays[OVERLAY + name] = {'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
    manifest = {'schema': 'meowchan.synthetic-upgrade-102-source.v2',
                'source_commit': REVISION, 'synthetic': True,
                'export': 'local git ls-tree/cat-file; links materialized as inert files',
                'files': files, 'overlay': overlays,
                'prepare_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    with (destination / 'UPGRADE-102-SOURCE.json').open('x', encoding='utf-8', newline='\n') as stream:
        stream.write(json.dumps(manifest, sort_keys=True, indent=2) + '\n')
    print(f'SYNTHETIC SOURCE EXPORTED files={len(files)} bytes={total} overlay=2')
    print(destination / OVERLAY)


if __name__ == '__main__':
    main()
