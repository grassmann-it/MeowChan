#!/usr/bin/env python3
"""Commit-bound identity of the AP04 release candidate (1.1.0).

A candidate is one exact commit of a clean worktree plus the checksums of what was built or
packaged from it. Nothing here builds, downloads or runs product code:

  identity   record commit, tree, version and the clean state; optionally verify and record the
             Linux package set of a stable-candidate run (manifest source_commit must be the commit)
             and the SHA256SUMS of the same run
  seal       add the SHA-256 of every file of built directories (the exact bytes the run executes)
  verify     re-check later: same commit, still clean, every recorded file and package unchanged

Clean (`git status`) does not see ignored files. `require_fresh_outputs` therefore also refuses ignored
build outputs and local MSBuild overrides (bin/, obj/, *.dll, *.user, ...) before anything is built:
stale binaries of an earlier build must never be sealed or executed as if built from the commit.

`ap04_run.py` calls these functions; the commands exist for manual inspection and for the tests.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys

SCHEMA = 'meowchan.ap04-candidate.v1'
PACKAGE_SCHEMA = 'meowchan.linux-packages.v1'
SHA1 = re.compile(r'[0-9a-f]{40}')
SHA256 = re.compile(r'[0-9a-f]{64}')
# Debug symbols, XML docs and run logs are not executed; everything else below a built directory is.
SKIP_SUFFIXES = ('.pdb', '.xml', '.log', '.trx')
FLAVORS = (('system-vlc', '-system-vlc.tar.gz'), ('bundled-vlc', '-bundled-vlc.tar.gz'), ('appimage', '.AppImage'))


def require(value, message):
    if not value:
        raise ValueError(message)


def git(root, *args):
    env = dict(os.environ, GIT_TERMINAL_PROMPT='0', GIT_NO_LAZY_FETCH='1')
    return subprocess.check_output(['git', *args], cwd=root, env=env, text=True, timeout=60, stderr=subprocess.PIPE)


def digest_file(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def product_version(root):
    text = (Path(root) / 'Directory.Build.props').read_text(encoding='utf-8')
    found = re.findall(r'<Version>([^<]+)</Version>', text)
    require(len(found) == 1, 'Directory.Build.props has no single <Version>')
    return found[0].strip()


def worktree_state(root):
    """HEAD, tree and every deviation from it. Untracked files count (ignored ones do not)."""
    commit = git(root, 'rev-parse', '--verify', 'HEAD').strip()
    require(SHA1.fullmatch(commit), 'HEAD is not a full commit id')
    porcelain = [line for line in git(root, 'status', '--porcelain=v1', '--untracked-files=all').splitlines() if line]
    branch = subprocess.run(['git', 'symbolic-ref', '--short', '-q', 'HEAD'], cwd=root, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout.strip() or None
    return {'commit': commit, 'tree': git(root, 'rev-parse', 'HEAD^{tree}').strip(), 'branch': branch,
            'dirty': porcelain}


def require_frozen(root, expected_sha):
    """The only gate for evidence: the worktree is exactly `expected_sha` and has no change at all."""
    require(SHA1.fullmatch(expected_sha or ''), 'the candidate SHA must be a full lowercase 40-hex commit id')
    state = worktree_state(root)
    require(state['commit'] == expected_sha, f"worktree HEAD {state['commit']} is not the candidate {expected_sha}")
    if state['dirty']:
        raise ValueError(f"worktree is dirty ({len(state['dirty'])} entries, first: {state['dirty'][0]})")
    return state


def ignored_entries(root):
    """Ignored paths `git status` does not report: build outputs (bin/, obj/, *.dll, ...) and local MSBuild
    overrides (*.user). A fresh `git worktree add --detach` has none. Python bytecode caches of these
    helpers (__pycache__/) are not product code and are allowed."""
    listed = git(root, 'status', '--porcelain=v1', '-z', '--ignored=matching', '--untracked-files=all').split('\0')
    return [item[3:] for item in listed
            if item.startswith('!! ') and '__pycache__' not in PurePosixPath(item[3:].rstrip('/')).parts]


def require_fresh_outputs(root):
    """No stale binaries: MSBuild reuses obj/ and the plugin copy does not prune bin/, so a reused tree could be
    sealed and executed as if it were built from the candidate. Only a fresh worktree is accepted."""
    found = ignored_entries(root)
    if found:
        raise ValueError(f'worktree has {len(found)} ignored build output or override entries (first: {found[0]}); '
                         'stale binaries could be sealed and run as the candidate: use a fresh `git worktree add --detach`')


def tree_files(directory):
    base = ordinary_path(directory, 'directory')
    require(base.is_dir(), 'not an ordinary directory: ' + str(directory))
    files = {}
    for path in sorted(base.rglob('*')):
        require(not path.is_symlink(), 'link in hashed tree: ' + str(path))
        require(path.is_file() or path.is_dir(), 'special file in hashed tree: ' + str(path))
        if path.is_file():
            metadata = path.stat()
            require(stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1,
                    'hardlink or non-regular file in hashed tree: ' + str(path))
            if path.suffix not in SKIP_SUFFIXES:
                files[path.relative_to(base).as_posix()] = digest_file(path)
    require(files, 'no files below ' + str(directory))
    return files


def ordinary_path(path, kind):
    """Return an absolute path whose existing components contain no symlink.

    Checking only the final path is insufficient for an artifact root: ``root/link/file``
    can otherwise move the evidence outside the root between identity and verification.
    The release inputs are immutable files/directories, so refusing symlink components is
    the deliberately boring, fail-closed rule here.
    """
    path = Path(path)
    absolute = Path(os.path.abspath(path))
    current = Path(absolute.root)
    for part in absolute.parts[1:]:
        current /= part
        require(not current.is_symlink(), f'{kind} path contains a symlink: {current}')
    if kind in ('directory', 'package directory', 'artifact root'):
        require(absolute.is_dir(), 'not an ordinary directory: ' + str(path))
    else:
        metadata = absolute.stat() if absolute.is_file() else None
        require(metadata is not None and stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1,
                f'{kind} must be a single-link regular file: {path}')
    return absolute


def artifact_files(directory):
    """Inventory every file in a package root, including files not named in its manifest."""
    base = ordinary_path(directory, 'artifact root')
    files = {}
    for path in sorted(base.rglob('*')):
        require(not path.is_symlink(), 'link in artifact root: ' + str(path))
        require(path.is_file() or path.is_dir(), 'special file in artifact root: ' + str(path))
        if path.is_file():
            metadata = path.stat()
            require(stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1,
                    'hardlink or non-regular file in artifact root: ' + str(path))
            files[path.relative_to(base).as_posix()] = digest_file(path)
    require(files, 'artifact root is empty: ' + str(directory))
    return files


def summary_digest(files):
    value = hashlib.sha256()
    for name in sorted(files):
        value.update(name.encode('utf-8') + b'\0' + files[name].encode('ascii') + b'\n')
    return value.hexdigest()


def parse_sha256sums(path):
    source = ordinary_path(path, 'SHA256SUMS')
    result = {}
    for line in source.read_text(encoding='utf-8').splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(r'([0-9a-fA-F]{64}) [ *](.+)', line)
        require(match, 'malformed SHA256SUMS line')
        name = match.group(2)
        parts = name.split('/')
        require(not name.startswith('/') and '\\' not in name and all(part not in ('', '.', '..') for part in parts),
                'unsafe SHA256SUMS path: ' + name)
        require(name not in result, 'duplicate SHA256SUMS entry: ' + name)
        result[name] = match.group(1).lower()
    return result


def package_set(directory, commit, version, rid='linux-x64', sha256sums=None):
    """Verify the Linux package manifest of a stable-candidate run against the commit and recompute every checksum."""
    base = ordinary_path(directory, 'package directory')
    manifest_path = base / f'{rid}-package-manifest.json'
    require(manifest_path.is_file() and not manifest_path.is_symlink(), 'package manifest missing: ' + manifest_path.name)
    manifest_metadata = manifest_path.stat()
    require(stat.S_ISREG(manifest_metadata.st_mode) and manifest_metadata.st_nlink == 1,
            'package manifest must be a single-link regular file: ' + manifest_path.name)
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    require(isinstance(manifest, dict), 'package manifest must be a JSON object')
    require(manifest.get('schema') == PACKAGE_SCHEMA and manifest.get('runtime') == rid, 'package manifest schema/runtime mismatch')
    require(manifest.get('source_commit') == commit,
            f"package manifest source_commit {manifest.get('source_commit')} is not the candidate {commit}")
    require(manifest.get('version') == version, f"package manifest version {manifest.get('version')} is not {version}")
    require(SHA256.fullmatch(str(manifest.get('apphost_sha256', ''))), 'package manifest apphost_sha256 is missing or malformed')
    rows = manifest.get('artifacts')
    require(isinstance(rows, list) and len(rows) == 3, 'package manifest must list three artifacts')
    sums_path = ordinary_path(sha256sums, 'SHA256SUMS') if sha256sums else None
    sums = parse_sha256sums(sums_path) if sums_path else None
    artifacts = []
    for flavor, suffix in FLAVORS:
        found = [row for row in rows if isinstance(row, dict) and row.get('flavor') == flavor]
        name = f'MeowChan-{version}-{rid}{suffix}'
        require(len(found) == 1 and found[0].get('file') == name, 'package artifact missing: ' + name)
        path = base / name
        require(path.is_file() and not path.is_symlink(), 'package file missing: ' + name)
        metadata = path.stat()
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1,
                'package file must be a single-link regular file: ' + name)
        actual = digest_file(path)
        require(SHA256.fullmatch(str(found[0].get('sha256'))) and actual == found[0]['sha256'], 'package checksum mismatch: ' + name)
        require(type(found[0].get('bytes')) is int and found[0]['bytes'] == path.stat().st_size,
                'package size mismatch: ' + name)
        if sums is not None:
            require(sums.get(name) == actual, 'SHA256SUMS does not list the same checksum for ' + name)
        artifacts.append({'file': name, 'flavor': flavor, 'sha256': actual, 'bytes': path.stat().st_size})
    root_files = artifact_files(base)
    expected_root_files = {manifest_path.name, *(artifact['file'] for artifact in artifacts)}
    if sums_path is not None and sums_path.parent == base:
        expected_root_files.add(sums_path.name)
    require(set(root_files) == expected_root_files,
            'package artifact root inventory differs from its manifest: '
            + f" missing={sorted(expected_root_files - set(root_files))} extra={sorted(set(root_files) - expected_root_files)}")
    require({path.relative_to(base).as_posix() for path in base.iterdir()} == expected_root_files,
            'package artifact root contains an unexpected directory or nested path')
    return {'directory': str(base), 'runtime': rid, 'manifest': manifest_path.name, 'manifest_sha256': digest_file(manifest_path),
            'apphost_sha256': manifest.get('apphost_sha256'), 'artifacts': artifacts,
            'artifact_root': {'files': root_files, 'sha256': summary_digest(root_files)},
            'sha256sums_checked': sums is not None,
            'sha256sums': str(sums_path) if sums is not None else None,
            'sha256sums_sha256': digest_file(sums_path) if sums is not None else None}


def identity(root, expected_sha, package_dir=None, rid='linux-x64', sha256sums=None):
    state = require_frozen(root, expected_sha)
    version = product_version(root)
    candidate = {'schema': SCHEMA, 'commit': state['commit'], 'tree': state['tree'], 'branch': state['branch'],
                 'version': version, 'clean': True, 'ignored_at_identity': ignored_entries(root), 'trees': {}, 'package': None}
    if package_dir:
        candidate['package'] = package_set(package_dir, state['commit'], version, rid, sha256sums)
    return candidate


def seal(candidate, label, directory):
    """Record the exact bytes below `directory` (a build output the run executes)."""
    files = tree_files(directory)
    candidate['trees'][label] = {'directory': str(Path(directory).absolute()), 'files': files, 'sha256': summary_digest(files)}
    return candidate['trees'][label]['sha256']


def tokens(candidate):
    """Short, stable identifiers that every report line carries next to the full commit id."""
    trees = candidate.get('trees') or {}
    bin_value = hashlib.sha256(json.dumps({k: v['sha256'] for k, v in sorted(trees.items())}, sort_keys=True).encode()).hexdigest()[:16] if trees else 'unsealed'
    package = candidate.get('package')
    pkg_value = package['manifest_sha256'][:16] if package else 'none'
    return {'commit': candidate['commit'], 'bin': bin_value, 'pkg': pkg_value}


def verify(root, candidate):
    """Problems since the candidate was recorded; empty means the same commit, still clean, bytes unchanged."""
    problems = []
    try:
        require_frozen(root, candidate['commit'])
    except ValueError as error:
        problems.append(str(error))
    for label, tree in (candidate.get('trees') or {}).items():
        try:
            if tree_files(tree['directory']) != tree['files']:
                problems.append(f'built files of {label} changed after sealing')
        except ValueError as error:
            problems.append(str(error))
    package = candidate.get('package')
    if package:
        try:
            current = package_set(package['directory'], candidate['commit'], candidate['version'], package['runtime'],
                                  package.get('sha256sums'))
            if (current['manifest_sha256'] != package['manifest_sha256'] or current['artifacts'] != package['artifacts']
                    or current.get('artifact_root') != package.get('artifact_root')
                    or current['sha256sums_sha256'] != package.get('sha256sums_sha256')):
                problems.append('package files changed after recording')
        except (ValueError, OSError) as error:
            problems.append(str(error))
    return problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    actions = parser.add_subparsers(dest='command', required=True)
    ident = actions.add_parser('identity', help='record commit/tree/version of a clean worktree (and the package set)')
    ident.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[3])
    ident.add_argument('--sha', required=True, help='the frozen full commit id')
    ident.add_argument('--out', type=Path, required=True)
    ident.add_argument('--linux-package-dir', type=Path)
    ident.add_argument('--rid', default='linux-x64')
    ident.add_argument('--sha256sums', type=Path)
    sealed = actions.add_parser('seal', help='add the checksums of built directories to a candidate file')
    sealed.add_argument('--candidate', type=Path, required=True)
    sealed.add_argument('--tree', action='append', required=True, metavar='LABEL=DIR')
    check = actions.add_parser('verify', help='check the candidate file against the worktree and the files on disk')
    check.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[3])
    check.add_argument('--candidate', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == 'identity':
            require(not args.out.exists(), 'refusing to overwrite ' + str(args.out))
            candidate = identity(args.root, args.sha, args.linux_package_dir, args.rid, args.sha256sums)
            args.out.write_text(json.dumps(candidate, indent=2, sort_keys=True) + '\n', encoding='utf-8')
            print('CANDIDATE', json.dumps(tokens(candidate), sort_keys=True))
        elif args.command == 'seal':
            candidate = json.loads(args.candidate.read_text(encoding='utf-8'))
            for item in args.tree:
                label, _, directory = item.partition('=')
                require(label and directory, 'expected LABEL=DIR')
                print('SEALED', label, seal(candidate, label, directory))
            args.candidate.write_text(json.dumps(candidate, indent=2, sort_keys=True) + '\n', encoding='utf-8')
        else:
            candidate = json.loads(args.candidate.read_text(encoding='utf-8'))
            problems = verify(args.root, candidate)
            for problem in problems:
                print('PROBLEM', problem, file=sys.stderr)
            print('CANDIDATE', 'UNCHANGED' if not problems else 'CHANGED', json.dumps(tokens(candidate), sort_keys=True))
            return 1 if problems else 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.SubprocessError) as error:
        print('CANDIDATE FAILED: ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
