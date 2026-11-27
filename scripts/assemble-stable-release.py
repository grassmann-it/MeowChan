#!/usr/bin/env python3
"""Verify one final package-media candidate set and stage release assets.

The release version defaults to 0.9.0 (the original contract) and is selected
with --version. From 1.2 onward, current-source candidates combine the pinned
base and matching platform inputs into one public source asset. Release upload
stages the exact bytes from a successful same-source stable candidate.

Download the four named candidate artifacts into --artifacts-root first. This
offline command does not create a tag or release. Platform source overrides
allow an explicitly recorded Linux-only fix to retain accepted Windows/macOS
packages; each platform still checks its exact source and binary/evidence binding.
The caller must independently verify the originating successful Actions runs.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


VERSION = '0.9.0'  # default release version; override with --version
BASE_NAME = f'MeowChan-{VERSION}-video-sources.tar.gz'
BASE_BYTES = 725131331
BASE_SHA = 'bf556da3dd1fb511dafb81f684a0bb6fc38e7cb7b91081e9ac0c307bd0771bfa'
RELEASE_VERSION = re.compile(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.]+)?\Z')
SHA = re.compile(r'[0-9a-f]{64}\Z')
COMMIT = re.compile(r'[0-9a-f]{40}\Z')


VIDEO_SPEC = importlib.util.spec_from_file_location('video_sources', Path(__file__).with_name('assemble-video-sources.py'))
video = importlib.util.module_from_spec(VIDEO_SPEC)
VIDEO_SPEC.loader.exec_module(video)
PACKAGE_IDS = ('libmeowchan', 'MeowChan.PluginSdk', 'MeowChan.PluginHost')


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


def windows(root: Path, source: str, version: str = VERSION) -> dict[str, Path]:
    candidate = root / 'native-media-candidate-win-x64'
    evidence = candidate / 'media-evidence'
    archive = file(candidate / 'media-packages/MeowChan-win-x64.zip')
    check_source(evidence / 'source.txt', source)
    line = file(evidence / 'archive.sha256').read_text(encoding='utf-8-sig').strip().split()
    require(len(line) == 2 and line[1] == archive.name, 'Windows checksum record is invalid')
    check_hash(archive, line[0].lower(), 'Windows ZIP')
    # Retained 0.9 inputs keep their original contract. New source candidates
    # must provide split evidence (enforced by assemble-prerelease.py).
    execution_evidence = candidate / 'acceptance-media-evidence'
    execution_archive = candidate / 'acceptance-media-packages/MeowChan-win-x64.zip'
    split = execution_evidence.is_dir()
    if not split:
        execution_evidence, execution_archive = evidence, archive
    playback = read_json(execution_evidence / 'playback/playback.json')
    workflow = read_json(execution_evidence / 'workflow/workflow.json')
    require(playback.get('passed') is True and workflow.get('passed') is True
            and workflow.get('completed') is True and workflow.get('declaredSourceCommit') == source
            and workflow.get('declaredRuntime') == 'win-x64', 'Windows native playback/workflow proof failed')
    binding = workflow.get('archiveBinding')
    require(isinstance(binding, dict) and binding.get('member') == 'MeowChan.exe'
            and binding.get('sha256') == digest(execution_archive), 'Windows workflow does not bind the ZIP')
    if split:
        require(all(report.get('acceptanceConfiguration') == 'Debug' and report.get('shippingArtifactExecuted') is False
                    for report in (playback, workflow)), 'Windows Debug acceptance boundary mismatch')
    return {f'MeowChan-{version}-win-x64.zip': archive}


def linux(root: Path, source: str, rid: str, version: str = VERSION) -> dict[str, Path]:
    candidate = root / f'linux-distribution-candidate-{rid}'
    packages = candidate / 'linux-packages'
    evidence = candidate / 'linux-evidence'
    check_source(evidence / 'source.txt', source)
    manifest_path = packages / f'{rid}-package-manifest.json'
    manifest = read_json(manifest_path)
    require(manifest.get('schema') == 'meowchan.linux-packages.v1'
            and manifest.get('source_commit') == source and manifest.get('runtime') == rid
            and manifest.get('version') == version, f'Linux package manifest mismatch: {rid}')
    acceptance_manifest = candidate / 'acceptance-linux-packages' / manifest_path.name
    split = acceptance_manifest.is_file()
    if split:
        layouts = read_json(evidence / 'release-layouts/linux-distribution-gates.json')
        require(layouts.get('passed') is True and layouts.get('completed') is True
                and layouts.get('configuration') == 'Release' and layouts.get('layoutOnly') is True
                and layouts.get('sourceCommit') == source and layouts.get('runtime') == rid
                and layouts.get('manifestSha256') == digest(manifest_path)
                and len(layouts.get('scenarios', [])) == 4
                and {row.get('scenario') for row in layouts['scenarios']}
                    == {'bundled-vlc', 'system-vlc', 'missing-system-vlc', 'appimage'}
                and all(row.get('passed') is True for row in layouts['scenarios']),
                'Linux Release layout gates mismatch: ' + rid)
    gates = read_json(evidence / 'distributions/linux-distribution-gates.json')
    require(gates.get('passed') is True and gates.get('completed') is True
            and gates.get('sourceCommit') == source and gates.get('runtime') == rid
            and gates.get('manifestSha256') == digest(acceptance_manifest if split else manifest_path), f'Linux native gates mismatch: {rid}')
    if split:
        require(gates.get('configuration') == 'Debug' and gates.get('layoutOnly') is False,
                'Linux Debug acceptance boundary mismatch: ' + rid)
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
        name = f'MeowChan-{version}-{rid}{suffix}'
        require(len(rows) == 1 and rows[0].get('file') == name, f'Linux asset name mismatch: {name}')
        path = file(packages / name)
        require(type(rows[0].get('bytes')) is int and path.stat().st_size == rows[0]['bytes'],
                f'Linux asset length mismatch: {name}')
        check_hash(path, rows[0].get('sha256'), name)
        outputs[name] = path
    source_name = f'MeowChan-{version}-{rid}-video-sources.tar.gz'
    source_path = file(packages / source_name)
    sidecar = read_json(packages / (source_name + '.json'))
    require(sidecar.get('schema') == 'meowchan.linux-source-artifact.v1'
            and sidecar.get('source_commit') == source and sidecar.get('file') == source_name
            and sidecar.get('runtimes') == [rid] and sidecar.get('bytes') == source_path.stat().st_size,
            f'Linux corresponding-source record mismatch: {rid}')
    check_hash(source_path, sidecar.get('sha256'), source_name)
    outputs[source_name] = source_path
    return outputs


def mac(root: Path, source: str, version: str = VERSION) -> dict[str, Path]:
    candidate = root / f'macos-paired-candidate-{source}'
    check_source(candidate / 'source.txt', source)
    require(file(candidate / 'version.txt').read_text(encoding='utf-8').strip() == version,
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
        name = f'MeowChan-{version}-{rid}.dmg'
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
    overlay_name = f'MeowChan-{version}-macos-video-source-overlay.tar.gz'
    overlay = file(candidate / overlay_name)
    check_hash(overlay, checksums.get(overlay_name), overlay_name)
    with tarfile.open(overlay, 'r:gz') as archive:
        member = archive.getmember('manifest.json')
        require(member.isfile() and member.size <= 16 * 1024 * 1024, 'Invalid Mac overlay manifest')
        manifest = json.load(archive.extractfile(member), object_pairs_hook=unique_object)
    require(manifest.get('schema') == 'meowchan.macos-source-overlay.v1'
            and manifest.get('version') == version and manifest.get('application_source_commit') == source,
            'Mac source overlay does not match final source')
    architectures = manifest.get('architectures')
    require(isinstance(architectures, dict) and set(architectures) == set(dmgs),
            'Mac source overlay architecture set mismatch')
    for rid, sha in dmgs.items():
        architecture = architectures[rid]
        record = architecture.get('signed_dmg')
        require(isinstance(record, dict) and record.get('file') == f'MeowChan-{version}-{rid}.dmg'
                and record.get('sha256') == sha, f'Mac source overlay DMG binding mismatch: {rid}')
        if video.current_layout(version):
            name = f'MeowChan-{version}-{rid}.zip'
            archive = candidate / 'evidence' / rid / name
            require(architecture.get('inventory_zip') == video.binary_record(archive, name),
                    f'Mac source overlay does not bind the final ZIP inventory: {rid}')
    base = read_json(candidate / 'source-base.json')
    source_base = manifest.get('source_base')
    require(isinstance(source_base, dict) and source_base.get('file') == BASE_NAME
            and source_base.get('bytes') == BASE_BYTES and source_base.get('sha256') == BASE_SHA
            and all(base.get(key) == source_base[key] for key in ('file', 'bytes', 'sha256')),
            'Mac source base binding mismatch')
    outputs[overlay_name] = overlay
    return outputs


def assemble(root: Path, source: str, source_base: Path, output: Path, *,
             windows_source: str | None = None, macos_source: str | None = None,
             version: str = VERSION, repository: Path = Path(__file__).resolve().parents[1]) -> None:
    require(RELEASE_VERSION.fullmatch(version) is not None, f'Invalid release version: {version}')
    require(COMMIT.fullmatch(source) is not None, 'Expected full lowercase source commit SHA')
    windows_source = source if windows_source is None else windows_source
    macos_source = source if macos_source is None else macos_source
    require(COMMIT.fullmatch(windows_source) is not None, 'Invalid Windows source commit SHA')
    require(COMMIT.fullmatch(macos_source) is not None, 'Invalid macOS source commit SHA')
    current = video.current_layout(version)
    if current:
        require(windows_source == macos_source == source, 'Current video sources require one binary source commit')
    root = root.resolve(strict=True)
    require(root.is_dir() and not root.is_symlink(), 'Artifact root must be a directory')
    require(not output.exists() and not output.is_symlink(), 'Output already exists')
    source_base = file(source_base)
    require(source_base.stat().st_size == BASE_BYTES, 'Immutable source base length mismatch')
    check_hash(source_base, BASE_SHA, BASE_NAME)
    assets = {}
    for group in (windows(root, windows_source, version), linux(root, source, 'linux-x64', version),
                  linux(root, source, 'linux-arm64', version), mac(root, macos_source, version)):
        require(not (assets.keys() & group.keys()), 'Duplicate public release asset')
        assets.update(group)
    require(len(assets) == 12, 'Expected nine binaries and three platform source inputs')
    if current:
        require(not any(root.rglob('*-current-video-sources.7z')), 'Duplicate current video source input')
        inputs = {'base': source_base}
        package_manifests = {}
        for rid in video.LINUX_RIDS:
            name = f'MeowChan-{version}-{rid}-video-sources.tar.gz'
            inputs[rid] = assets.pop(name)
            package_manifests[rid] = inputs[rid].with_name(f'{rid}-package-manifest.json')
        inputs['macos'] = assets.pop(f'MeowChan-{version}-macos-video-source-overlay.tar.gz')
    else:
        assets[BASE_NAME] = source_base
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.meowchan-release-', dir=output.parent) as temporary:
        stage = Path(temporary) / 'assets'
        stage.mkdir()
        for name, origin in sorted(assets.items()):
            shutil.copyfile(origin, stage / name)
            require(digest(stage / name) == digest(origin), f'Changed while staging: {name}')
        names = set(assets)
        if current:
            source_name = video.archive_name(version)
            video.assemble(inputs, package_manifests, {name: stage / name for name in assets}, stage / source_name,
                           version, source, video.approved_windows(repository, source))
            names.add(source_name)
            require(names == set(video.public_names(version)), 'Current public asset inventory mismatch')
        write_checksums(stage, names)
        require({path.name for path in stage.iterdir()} == names | {'SHA256SUMS'},
                'Staged asset set differs from public inventory')
        stage.rename(output)


def write_checksums(directory, names):
    lines = [f'{digest(directory / name)}  {name}\n' for name in sorted(names)]
    (directory / 'SHA256SUMS').write_text(''.join(lines), encoding='utf-8')


def stage_candidate(candidate, source, version, run_id, output, repository):
    """Verify the gated candidate and stage its public desktop assets byte-for-byte."""
    require(RELEASE_VERSION.fullmatch(version) and '-' not in version and video.current_layout(version),
            'Expected stable version 1.2 or later')
    require(COMMIT.fullmatch(source) and re.fullmatch(r'[1-9][0-9]*', run_id),
            'Expected exact source commit and candidate Actions run ID')
    require(not output.exists() and not output.is_symlink(), 'Output already exists')
    inventory = read_json(candidate / 'candidate-inventory.json')
    require(inventory['schema'] == 'meowchan.stable-candidate.v1' and inventory['channel'] == 'stable'
            and inventory['version'] == version and inventory['source_commit'] == source
            and inventory['actions_run_id'] == run_id, 'Wrong stable candidate version, run, or source')
    public = set(video.public_names(version))
    names = public | {f'{package}.{version}.nupkg' for package in PACKAGE_IDS}
    records = inventory['assets']
    require(len(records) == len(names) and {row['file'] for row in records} == names
            and {path.name for path in candidate.iterdir()} == names | {'candidate-inventory.json', 'SHA256SUMS'},
            'Candidate asset inventory mismatch (missing, extra, or duplicate asset)')
    checksums = {}
    for row in records:
        name = row['file']
        path = file(candidate / name)
        require(row['source_commit'] == source and type(row['bytes']) is int and path.stat().st_size == row['bytes'],
                'Candidate asset length or source mismatch: ' + name)
        check_hash(path, row['sha256'], name)
        checksums[name] = row['sha256']
    require(inventory['current_video_sources'] == next(row for row in records if row['file'] == video.archive_name(version)),
            'Candidate source asset differs from its inventory row')
    checksums['candidate-inventory.json'] = digest(candidate / 'candidate-inventory.json')
    expected = ''.join(f'{sha}  {name}\n' for name, sha in sorted(checksums.items()))
    require(file(candidate / 'SHA256SUMS').read_text(encoding='utf-8') == expected,
            'Candidate SHA256SUMS differs from its assets')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.meowchan-release-', dir=output.parent) as temporary:
        stage = Path(temporary) / 'assets'
        stage.mkdir()
        for name in sorted(public):
            shutil.copyfile(candidate / name, stage / name)
            check_hash(stage / name, checksums[name], name)
        source_name = video.archive_name(version)
        video.verify(stage / source_name, {name: stage / name for name in public - {source_name}},
                     version, source, video.approved_windows(repository, source))
        write_checksums(stage, public)
        stage.rename(output)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument('--artifacts-root', type=Path)
    inputs.add_argument('--candidate-dir', type=Path, help='Complete gated stable-candidate artifact')
    parser.add_argument('--candidate-run-id')
    parser.add_argument('--repository', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--windows-source-commit', help='Explicit accepted Windows source; defaults to --source-commit')
    parser.add_argument('--macos-source-commit', help='Explicit accepted macOS source; defaults to --source-commit')
    parser.add_argument('--version', default=VERSION, help='Release version used in asset names and manifests (default 0.9.0)')
    parser.add_argument('--source-base', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.candidate_dir is not None:
            require(args.candidate_run_id and not any((args.source_base, args.windows_source_commit,
                                                      args.macos_source_commit)), 'Expected a candidate run without platform overrides')
            stage_candidate(args.candidate_dir, args.source_commit, args.version, args.candidate_run_id, args.output,
                            args.repository)
        else:
            require(args.source_base is not None and args.candidate_run_id is None, 'Expected the pinned source base')
            assemble(args.artifacts_root, args.source_commit, args.source_base, args.output,
                     windows_source=args.windows_source_commit, macos_source=args.macos_source_commit,
                     version=args.version, repository=args.repository)
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError, zipfile.BadZipFile, ET.ParseError,
            subprocess.SubprocessError) as error:
        parser.exit(1, f'Release assembly rejected: {error}\n')
    print(f'Staged release assets and SHA256SUMS in {args.output}')


if __name__ == '__main__':
    main()
