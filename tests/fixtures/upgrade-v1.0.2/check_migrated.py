#!/usr/bin/env python3
"""Read-only, offline post-migration check of a finished AP04 three-process attempt.

Runs AFTER Released102RestPackageUpgradeTests published current-result.json. It never
opens SQLite (only reads the 100-byte header), writes nothing and starts no product
code. Expected schema markers come from local Git objects of the pinned release and of
the current revision stamped into the cycle results, not from this checkout.
Asserted: the documented 1.1.0 upgrade outcomes that need no user action (archive
user_version released -> current, journal marker, no private-tags/booru file created
by opening). Manifest versions, prune backup and other new files are only reported.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

REVISION = 'eec6c0169f8d52f7481ea0bf7febfd20981e97c3'
ROOT = Path(__file__).resolve().parents[3]
ARCHIVE_SQL = 'libmeowchan/Archive/ArchiveSql.cs'
WORK_STATE = 'libmeowchan/Persistence/WorkStateStore.cs'
# Created only by a tag or Danbooru write; opening an upgraded profile must not create them.
# Covers private-tags.json(.bak), the permanent private-tags.v1.bak and the writers'
# ".<name>.<guid>.tmp" files (PrivateTagStore, BooruCatalog, AtomicJsonFile).
FORBIDDEN_PREFIXES = ('private-tags.', 'booru-catalog.')
SQLITE_MAGIC = b'SQLite format 3\x00'


def require(value, message):
    if not value:
        raise ValueError(message)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON key: ' + key)
        result[key] = value
    return result


def read(path, limit=32 * 1024 * 1024):
    require(not path.is_symlink() and path.is_file(), 'not an ordinary file: ' + str(path))
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    require(len(data) <= limit, 'file budget: ' + str(path))
    return data


def document(path):
    return json.loads(read(path), object_pairs_hook=unique)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def git_text(revision, path):
    env = dict(os.environ, GIT_NO_LAZY_FETCH='1', GIT_NO_REPLACE_OBJECTS='1', GIT_TERMINAL_PROMPT='0')
    try:
        data = subprocess.check_output(['git', 'cat-file', 'blob', f'{revision}:{path}'],
                                       cwd=ROOT, env=env, timeout=30, stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError:
        raise ValueError(f'local Git object missing: {revision}:{path}') from None
    return data.decode('utf-8')


def schema_constant(source, pattern):
    """The single integer of `pattern` in source, or None when the marker does not exist."""
    found = re.findall(pattern, source)
    require(len(found) <= 1, 'ambiguous schema constant')
    return int(found[0]) if found else None


def archive_schema(revision):
    value = schema_constant(git_text(revision, ARCHIVE_SQL), r'internal const int SchemaVersion = (\d+);')
    require(value is not None, 'archive schema constant missing at ' + revision)
    return value


def journal_schema(revision):
    # Builds before the journal marker never stamp it; SQLite then reports 0.
    value = schema_constant(git_text(revision, WORK_STATE), r'private const long SchemaVersion = (\d+);')
    return 0 if value is None else value


def sqlite_user_version(path):
    header = read(path)[:100]
    require(len(header) == 100 and header[:16] == SQLITE_MAGIC, 'not an SQLite database: ' + path.name)
    return int.from_bytes(header[60:64], 'big')


def forbidden_files(profile):
    return sorted(p.name for p in profile.iterdir() if p.name.removeprefix('.').startswith(FORBIDDEN_PREFIXES))


def clean_status(paths):
    """Each `git status --porcelain=v1` capture of the frozen worktree must be empty."""
    for path in paths:
        require(read(path) == b'', 'source tree was not clean: ' + str(path))
    return 'clean' if paths else 'unverified'


def cycle_revision(version):
    require(isinstance(version, str) and version.count('+') == 1, 'cycle version lacks a source revision')
    revision = version.split('+', 1)[1]
    require(re.fullmatch(r'[0-9a-f]{40}', revision) is not None, 'cycle revision is not a full commit id')
    return revision


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--results', type=Path, required=True)
    parser.add_argument('--expect-revision', help='require this full current commit id in every cycle')
    parser.add_argument('--status', type=Path, action='append', default=[],
                        help='empty `git status --porcelain=v1` capture of the built worktree (repeatable)')
    args = parser.parse_args()
    source = clean_status([path.absolute() for path in args.status])
    profile, results = args.profile.absolute(), args.results.absolute()
    for directory in (profile, results):
        require(not directory.is_symlink() and directory.is_dir(), 'not an ordinary directory: ' + str(directory))

    final = document(results / 'current-result.json')
    require(final['Schema'] == 'meowchan.released-102-rest-package-result.v1' and final['Synthetic'] is True and
            final['SourceRevision'] == REVISION and final['SubsequentOfflineProcessRestarts'] == 2,
            'final result provenance/shape mismatch')
    require([c['Cycle'] for c in final['Cycles']] == [0, 1, 2], 'final result is not cycles 0, 1, 2')
    revisions = set()
    for cycle in final['Cycles']:
        name = cycle['Result']
        require(name == f"cycle-{cycle['Cycle']}.json", 'unexpected cycle file name')
        data = read(results / name)
        require(digest(data) == cycle['Sha256'].lower(), 'cycle digest mismatch: ' + name)
        current = json.loads(data, object_pairs_hook=unique)
        require(current['Schema'] == 'meowchan.released-102-current-cycle.v1' and current['HostStopped'] is True and
                current['NetworkRequests'] == [] and current['Profile'] == str(profile), 'cycle shape mismatch: ' + name)
        revisions.add(cycle_revision(current['CurrentVersion']))
    require(len(revisions) == 1, 'cycles ran different current revisions')
    current_revision = revisions.pop()
    if args.expect_revision:
        require(current_revision == args.expect_revision, 'cycles ran ' + current_revision)

    receipt_bytes = read(profile / 'v102-receipt.json')
    require(receipt_bytes == read(results / 'released-receipt.json'), 'receipt copy differs from the profile receipt')
    require(digest(receipt_bytes) == final['ReceiptSha256'].lower(), 'receipt digest mismatch')
    receipt = json.loads(receipt_bytes, object_pairs_hook=unique)
    require(receipt['Synthetic'] is True and receipt['SourceRevision'] == REVISION, 'receipt provenance mismatch')
    files = receipt['Files']

    released_archive, current_archive = archive_schema(REVISION), archive_schema(current_revision)
    require(released_archive < current_archive, 'no archive migration expected between these revisions')
    for suffix in ('-journal', '-wal'):
        require(not (profile / ('archive.db' + suffix)).exists(), 'archive.db' + suffix + ' left behind')
    require(sqlite_user_version(profile / 'archive.db') == current_archive, 'archive user_version is not the current schema')
    require(digest(read(profile / 'archive.db')) != files['archive.db'].lower(), 'archive.db was not migrated')
    released_journal, current_journal = journal_schema(REVISION), journal_schema(current_revision)
    require(sqlite_user_version(profile / 'work-state.sqlite') == current_journal, 'journal user_version mismatch')
    forbidden = forbidden_files(profile)
    require(not forbidden, 'opening created tag/Danbooru state: ' + ', '.join(forbidden))

    # Originals and exports are immutable through all three cycles (also asserted by the C# test).
    for name, sha in files.items():
        parts = PurePosixPath(name).parts
        if parts[0] == 'exports' or (parts[0] == 'media' and not parts[-1].startswith('.')):
            require(digest(read(profile.joinpath(*parts))) == sha.lower(), 'original bytes changed: ' + name)

    manifests = []
    for name in sorted(n for n in files if n.startswith('media/') and n.endswith('/.meowchan-manifest.json')):
        data = read(profile.joinpath(*PurePosixPath(name).parts))
        version = json.loads(data, object_pairs_hook=unique)['Version']
        manifests.append(f"{name.split('/')[1]}:v{version}:{'unchanged' if digest(data) == files[name].lower() else 'rewritten'}")
    changed = sorted(n for n, sha in files.items()
                     if not profile.joinpath(*PurePosixPath(n).parts).is_file()
                     or digest(read(profile.joinpath(*PurePosixPath(n).parts))) != sha.lower())
    new = sorted(p.relative_to(profile).as_posix() for p in profile.rglob('*')
                 if p.is_file() and p.relative_to(profile).as_posix() not in files and p.name != 'v102-receipt.json'
                 and not p.relative_to(profile).as_posix().startswith('plugin-cache/'))
    released_journal_text = 'none' if released_journal == 0 else str(released_journal)
    revision_binding = 'expected' if args.expect_revision else 'unbound'
    print(f'MIGRATED OK synthetic=true current={current_revision} revision={revision_binding} source={source} '
          'cycles=3 restarts=2 '
          f'archive={released_archive}->{current_archive} journal={released_journal_text}->{current_journal} '
          'private-tags=absent booru-catalog=absent')
    print('OBSERVED manifests=' + ','.join(manifests) + ' changed=' + ','.join(changed) + ' new=' + ','.join(new) +
          ' prune-backup=' + ('present' if (profile / 'work-state.before-prune.sqlite').exists() else 'absent'))
    print('NOT AN AUTHENTIC USER PROFILE; NOT A TAG-WRITE/UI/PACKAGE/PLATFORM/RELEASE ACCEPTANCE')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print('CHECK FAILED: ' + str(error), file=sys.stderr)
        sys.exit(1)
