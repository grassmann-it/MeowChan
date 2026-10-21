#!/usr/bin/env python3
"""Verify one final package-media candidate set and stage v0.9.0 release assets.

Download the four named candidate artifacts from one GitHub Actions run into
--artifacts-root first. This offline command does not create a tag or release.
The caller must independently confirm that all four downloads came from that
same run; source SHA and binary/evidence bindings are checked here.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import tarfile
import tempfile


VERSION = '0.9.0'
BASE_NAME = f'MeowChan-{VERSION}-video-sources.tar.gz'
BASE_BYTES = 725131331
BASE_SHA = 'bf556da3dd1fb511dafb81f684a0bb6fc38e7cb7b91081e9ac0c307bd0771bfa'
SHA = re.compile(r'[0-9a-f]{64}\Z')
COMMIT = re.compile(r'[0-9a-f]{40}\Z')


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def file(path: Path) -> Path:
    require(not path.is_symlink() and path.is_file(), f'Missing regular file: {path}')
    return path


def digest(path: Path) -> str:
    with file(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f'Duplicate JSON key: {key}')
        result[key] = value
    return result


def read_json(path: Path) -> dict:
    path = file(path)
    require(path.stat().st_size <= 16 * 1024 * 1024, f'Oversize JSON: {path}')
    value = json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)
    require(isinstance(value, dict), f'Expected JSON object: {path}')
    return value


def check_hash(path: Path, expected: str, label: str) -> None:
    require(isinstance(expected, str) and SHA.fullmatch(expected) is not None,
            f'Invalid SHA-256 for {label}')
    require(digest(path) == expected, f'SHA-256 mismatch: {label}')


def check_source(path: Path, source: str) -> None:
    require(file(path).read_text(encoding='utf-8').strip() == source,
            f'Source commit mismatch: {path}')


def windows(root: Path, source: str) -> dict[str, Path]:
    candidate = root / 'native-media-candidate-win-x64'
    evidence = candidate / 'media-evidence'
    archive = file(candidate / 'media-packages/MeowChan-win-x64.zip')
    check_source(evidence / 'source.txt', source)
    line = file(evidence / 'archive.sha256').read_text(encoding='utf-8-sig').strip().split()
    require(len(line) == 2 and line[1] == archive.name, 'Windows checksum record is invalid')
    check_hash(archive, line[0].lower(), 'Windows ZIP')
    playback = read_json(evidence / 'playback/playback.json')
    workflow = read_json(evidence / 'workflow/workflow.json')
    require(playback.get('passed') is True and workflow.get('passed') is True
            and workflow.get('completed') is True and workflow.get('declaredSourceCommit') == source
            and workflow.get('declaredRuntime') == 'win-x64', 'Windows native playback/workflow proof failed')
    binding = workflow.get('archiveBinding')
    require(isinstance(binding, dict) and binding.get('member') == 'MeowChan.exe'
            and binding.get('sha256') == digest(archive), 'Windows workflow does not bind the ZIP')
    return {f'MeowChan-{VERSION}-win-x64.zip': archive}


def linux(root: Path, source: str, rid: str) -> dict[str, Path]:
    candidate = root / f'linux-distribution-candidate-{rid}'
    packages = candidate / 'linux-packages'
    evidence = candidate / 'linux-evidence'
    check_source(evidence / 'source.txt', source)
    manifest_path = packages / f'{rid}-package-manifest.json'
    manifest = read_json(manifest_path)
    require(manifest.get('schema') == 'meowchan.linux-packages.v1'
            and manifest.get('source_commit') == source and manifest.get('runtime') == rid
            and manifest.get('version') == VERSION, f'Linux package manifest mismatch: {rid}')
    gates = read_json(evidence / 'distributions/linux-distribution-gates.json')
    require(gates.get('passed') is True and gates.get('completed') is True
            and gates.get('sourceCommit') == source and gates.get('runtime') == rid
            and gates.get('manifestSha256') == digest(manifest_path), f'Linux native gates mismatch: {rid}')
    scenarios = gates.get('scenarios')
    require(isinstance(scenarios, list) and len(scenarios) == 4
            and {row.get('scenario') for row in scenarios if isinstance(row, dict)}
            == {'bundled-vlc', 'system-vlc', 'missing-system-vlc', 'appimage'}
            and all(row.get('passed') is True for row in scenarios), f'Linux scenarios incomplete: {rid}')
    artifacts = manifest.get('artifacts')
    require(isinstance(artifacts, list) and len(artifacts) == 3, f'Linux formats incomplete: {rid}')
    outputs = {}
    for flavor, suffix in [('system-vlc', '-system-vlc.tar.gz'),
                           ('bundled-vlc', '-bundled-vlc.tar.gz'), ('appimage', '.AppImage')]:
        rows = [row for row in artifacts if isinstance(row, dict) and row.get('flavor') == flavor]
        name = f'MeowChan-{VERSION}-{rid}{suffix}'
        require(len(rows) == 1 and rows[0].get('file') == name, f'Linux asset name mismatch: {name}')
        path = file(packages / name)
        require(type(rows[0].get('bytes')) is int and path.stat().st_size == rows[0]['bytes'],
                f'Linux asset length mismatch: {name}')
        check_hash(path, rows[0].get('sha256'), name)
        outputs[name] = path
    source_name = f'MeowChan-{VERSION}-{rid}-video-sources.tar.gz'
    source_path = file(packages / source_name)
    sidecar = read_json(packages / (source_name + '.json'))
    require(sidecar.get('schema') == 'meowchan.linux-source-artifact.v1'
            and sidecar.get('source_commit') == source and sidecar.get('file') == source_name
            and sidecar.get('runtimes') == [rid] and sidecar.get('bytes') == source_path.stat().st_size,
            f'Linux corresponding-source record mismatch: {rid}')
    check_hash(source_path, sidecar.get('sha256'), source_name)
    outputs[source_name] = source_path
    return outputs


def mac(root: Path, source: str) -> dict[str, Path]:
    candidate = root / f'macos-paired-candidate-{source}'
    check_source(candidate / 'source.txt', source)
    require(file(candidate / 'version.txt').read_text(encoding='utf-8').strip() == VERSION,
            'Mac paired candidate version mismatch')
    checksums = {}
    for line in file(candidate / 'SHA256SUMS').read_text(encoding='utf-8').splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  ([^/][^\n]*)', line)
        require(match is not None, 'Malformed Mac candidate checksum line')
        name = match.group(2)
        require(name not in checksums and '..' not in Path(name).parts and '\\' not in name,
                'Unsafe or duplicate Mac candidate checksum path')
        checksums[name] = match.group(1)
    require(bool(checksums), 'Mac candidate checksum inventory is empty')
    for name, expected in checksums.items():
        check_hash(candidate / name, expected, f'Mac candidate {name}')
    outputs = {}
    dmgs = {}
    for rid in ('osx-arm64', 'osx-x64'):
        name = f'MeowChan-{VERSION}-{rid}.dmg'
        path = file(candidate / name)
        check_hash(path, checksums.get(name), name)
        report = read_json(candidate / f'{rid}-native-validation.json')
        dmg_report = report.get('dmg_validation')
        startup, playback, workflow = (report.get(key) for key in ('startup', 'playback', 'workflow'))
        signatures = report.get('signatures')
        require(report.get('source_commit') == source and report.get('rid') == rid
                and report.get('dmg_sha256') == digest(path)
                and isinstance(dmg_report, dict) and dmg_report.get('dmgSha256') == digest(path)
                and dmg_report.get('mountedAndCopied') is True
                and dmg_report.get('applicationsShortcut') == '/Applications'
                and isinstance(signatures, dict) and signatures.get('runtime') == rid
                and all(signatures.get(key) is True for key in
                        ('signatures_verified', 'dependency_closure', 'coreclr_entitlements_verified'))
                and isinstance(startup, dict) and startup.get('passed') is True
                and isinstance(playback, dict) and playback.get('passed') is True
                and isinstance(workflow, dict) and workflow.get('passed') is True
                and workflow.get('completed') is True and workflow.get('declaredSourceCommit') == source
                and workflow.get('declaredRuntime') == rid,
                f'Mac native validation mismatch: {rid}')
        outputs[name] = path
        dmgs[rid] = digest(path)
    overlay_name = f'MeowChan-{VERSION}-macos-video-source-overlay.tar.gz'
    overlay = file(candidate / overlay_name)
    check_hash(overlay, checksums.get(overlay_name), overlay_name)
    with tarfile.open(overlay, 'r:gz') as archive:
        member = archive.getmember('manifest.json')
        require(member.isfile() and member.size <= 16 * 1024 * 1024, 'Invalid Mac overlay manifest')
        manifest = json.load(archive.extractfile(member), object_pairs_hook=unique_object)
    require(manifest.get('schema') == 'meowchan.macos-source-overlay.v1'
            and manifest.get('version') == VERSION and manifest.get('application_source_commit') == source,
            'Mac source overlay does not match final source')
    architectures = manifest.get('architectures')
    require(isinstance(architectures, dict) and set(architectures) == set(dmgs),
            'Mac source overlay architecture set mismatch')
    for rid, sha in dmgs.items():
        record = architectures[rid].get('signed_dmg')
        require(isinstance(record, dict) and record.get('file') == f'MeowChan-{VERSION}-{rid}.dmg'
                and record.get('sha256') == sha, f'Mac source overlay DMG binding mismatch: {rid}')
    base = read_json(candidate / 'source-base.json')
    source_base = manifest.get('source_base')
    require(isinstance(source_base, dict) and source_base.get('file') == BASE_NAME
            and source_base.get('bytes') == BASE_BYTES and source_base.get('sha256') == BASE_SHA
            and all(base.get(key) == source_base[key] for key in ('file', 'bytes', 'sha256')),
            'Mac source base binding mismatch')
    outputs[overlay_name] = overlay
    return outputs


def assemble(root: Path, source: str, source_base: Path, output: Path) -> None:
    require(COMMIT.fullmatch(source) is not None, 'Expected full lowercase source commit SHA')
    root = root.resolve(strict=True)
    require(root.is_dir() and not root.is_symlink(), 'Artifact root must be a directory')
    require(not output.exists() and not output.is_symlink(), 'Output already exists')
    source_base = file(source_base)
    require(source_base.stat().st_size == BASE_BYTES, 'Immutable source base length mismatch')
    check_hash(source_base, BASE_SHA, BASE_NAME)
    assets = {}
    for group in (windows(root, source), linux(root, source, 'linux-x64'),
                  linux(root, source, 'linux-arm64'), mac(root, source)):
        require(not (assets.keys() & group.keys()), 'Duplicate public release asset')
        assets.update(group)
    assets[BASE_NAME] = source_base
    require(len(assets) == 13, 'Expected exactly 13 public binary/source assets')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.meowchan-release-', dir=output.parent) as temporary:
        stage = Path(temporary) / 'assets'
        stage.mkdir()
        for name, origin in sorted(assets.items()):
            shutil.copyfile(origin, stage / name)
            require(digest(stage / name) == digest(origin), f'Changed while staging: {name}')
        lines = [f'{digest(stage / name)}  {name}\n' for name in sorted(assets)]
        (stage / 'SHA256SUMS').write_text(''.join(lines), encoding='utf-8')
        require({path.name for path in stage.iterdir()} == set(assets) | {'SHA256SUMS'},
                'Staged asset set differs from public inventory')
        stage.rename(output)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts-root', required=True, type=Path)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--source-base', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        assemble(args.artifacts_root, args.source_commit, args.source_base, args.output)
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError, json.JSONDecodeError) as error:
        parser.exit(1, f'Release assembly rejected: {error}\n')
    print(f'Staged 13 release assets and SHA256SUMS in {args.output}')


if __name__ == '__main__':
    main()
