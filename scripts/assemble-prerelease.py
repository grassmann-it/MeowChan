#!/usr/bin/env python3
"""Offline current-source prerelease candidate inventory; never publishes anything.

Inputs must come from ONE successful prerelease-candidate Actions run. Native
execution reports identify the actual managed version/commit, not just filenames.
The fixed 0.9 retained-release assembler/upload contract remains independent.
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


def helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# This module instance belongs only to this command. Do not change the stable
# script's defaults or offer retained platform source overrides for prereleases.
retained = helper('prerelease_package_contracts', 'assemble-stable-release.py')
layout = helper('prerelease_legal_contract', 'check-desktop-package.py')
mac_sources = helper('prerelease_mac_sources', 'package-macos-sources.py')
public_api = helper('prerelease_public_api', 'check-public-api.py')
require, digest, read_json = retained.require, retained.digest, retained.read_json
PACKAGE_IDS = ('libmeowchan', 'MeowChan.PluginSdk', 'MeowChan.PluginHost')
PHASES = ['download', 'restore', 'removed', 'shutdown']


def committed_file(repository: Path, source: str, name: str) -> bytes:
    return subprocess.check_output(['git', '-C', str(repository), 'show', source + ':' + name])


def validate_version(version: str) -> None:
    mac_sources.METADATA.bundle_metadata(version)
    require(version.startswith('1.') and '-' in version and '+' not in version,
            'Expected a 1.x prerelease without build metadata')


def committed_legal(repository: Path, source: str, version: str) -> dict[str, bytes]:
    props = ET.fromstring(committed_file(repository, source, 'Directory.Build.props'))
    require([node.text for node in props.iter('Version')] == [version],
            'Committed product version differs from requested prerelease')
    return {name: committed_file(repository, source, name) for name in layout.LEGAL_FILES}


def check_legal_zip(path: Path, prefix: str, legal: dict[str, bytes]) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), 'Duplicate ZIP member')
        for name, expected in legal.items():
            info = archive.getinfo(prefix + name)
            require(info.file_size == len(expected) and archive.read(info) == expected,
                    'Packaged legal file differs from committed source: ' + name)


def workflow_identity(report: dict, version: str, source: str, rid: str) -> None:
    require(report.get('passed') is True and report.get('completed') is True
            and report.get('declaredSourceCommit') == source and report.get('declaredRuntime') == rid,
            'Native workflow source/runtime mismatch: ' + rid)
    scales = report.get('scales')
    require(isinstance(scales, list) and bool(scales), 'Native workflow has no executed scales: ' + rid)
    for scale in scales:
        phases = scale.get('phases', [])
        require(scale.get('passed') is True and [phase.get('phase') for phase in phases] == PHASES
                and all(phase.get('passed') is True and phase.get('assemblyInformationalVersion') == version + '+' + source
                        for phase in phases),
                'Executed managed version/source mismatch: ' + rid)


def check_windows(root: Path, source: str, version: str, legal: dict) -> None:
    candidate = root / 'native-media-candidate-win-x64'
    workflow = read_json(candidate / 'media-evidence/workflow/workflow.json')
    workflow_identity(workflow, version, source, 'win-x64')
    archive = candidate / 'media-packages/MeowChan-win-x64.zip'
    binding = workflow['archiveBinding']
    with zipfile.ZipFile(archive) as package:
        apphost = hashlib.sha256(package.read('MeowChan.exe')).hexdigest()
    require(binding.get('memberSha256') == apphost == workflow.get('apphostSha256'),
            'Executed Windows apphost differs from ZIP')
    check_legal_zip(archive, '', legal)


def check_linux(root: Path, source: str, version: str, legal: dict, rid: str) -> None:
    candidate = root / ('linux-distribution-candidate-' + rid)
    packages, evidence = candidate / 'linux-packages', candidate / 'linux-evidence/distributions'
    manifest = read_json(packages / (rid + '-package-manifest.json'))
    runtime_hash = manifest.get('runtime_manifest_sha256')
    require(isinstance(runtime_hash, str) and retained.SHA.fullmatch(runtime_hash), 'Missing Linux native inventory binding')
    source_archive = packages / f'MeowChan-{version}-{rid}-video-sources.tar.gz'
    with tarfile.open(source_archive) as archive:
        member = archive.getmember('manifest.json')
        require(member.isfile() and member.size <= 16 * 1024 * 1024, 'Invalid Linux source manifest')
        sources = json.load(archive.extractfile(member), object_pairs_hook=retained.unique_object)
        require(sources.get('source_commit') == source and sources.get('schema') == 'meowchan.linux-sources.v1'
                and set(sources.get('runtimes', {})) == {rid}
                and sources['runtimes'][rid].get('sha256') == runtime_hash,
                'Linux corresponding sources differ from packaged native inventory: ' + rid)
        native = archive.getmember('inventories/' + rid + '-runtime-manifest.json')
        require(native.isfile() and native.size <= 16 * 1024 * 1024
                and hashlib.sha256(archive.extractfile(native).read()).hexdigest() == runtime_hash,
                'Linux source archive lacks its exact native runtime manifest')
    gates = read_json(evidence / 'linux-distribution-gates.json')
    for row in gates['scenarios']:
        scenario = row['scenario']
        proof_path = evidence / scenario / 'scenario.json'
        retained.check_hash(proof_path, row.get('reportSha256'), 'Linux scenario ' + scenario)
        proof = read_json(proof_path)
        require(proof.get('passed') is True and proof.get('completed') is True
                and proof.get('sourceCommit') == source and proof.get('runtime') == rid
                and proof.get('manifestSha256') == digest(packages / (rid + '-package-manifest.json')),
                'Linux native scenario binding mismatch: ' + scenario)
        if scenario == 'missing-system-vlc':
            continue
        for label in (('extracted', 'extract-and-run') if scenario == 'appimage' else ('extracted',)):
            workflow_path = evidence / scenario / label / 'workflow/workflow.json'
            retained.check_hash(workflow_path, proof['positive' if label == 'extracted' else 'extractAndRun']['workflow']['reportSha256'],
                                'Linux native managed workflow')
            workflow_identity(read_json(workflow_path), version, source, rid)
    for artifact in manifest['artifacts']:
        prefix = 'usr/lib/meowchan/' if artifact['flavor'] == 'appimage' else ''
        expected = {prefix + name: hashlib.sha256(data).hexdigest() for name, data in legal.items()}
        require(all(artifact.get('payload_files', {}).get(name) == sha for name, sha in expected.items()),
                'Linux packaged legal files differ from source: ' + rid)


def check_mac(root: Path, source: str, version: str, legal: dict) -> None:
    candidate = root / ('macos-paired-candidate-' + source)
    for rid in ('osx-arm64', 'osx-x64'):
        report = read_json(candidate / (rid + '-native-validation.json'))
        workflow_identity(report['workflow'], version, source, rid)
        archive = candidate / 'evidence' / rid / f'MeowChan-{version}-{rid}.zip'
        retained.check_hash(archive, report.get('inventory_zip_sha256'), 'Mac inventory ZIP')
        require(report['workflow'].get('archiveBinding', {}).get('sha256') == digest(archive),
                'Mac native workflow differs from inventory ZIP')
        check_legal_zip(archive, 'MeowChan.app/Contents/Resources/application/', legal)
        with zipfile.ZipFile(archive) as package:
            import plistlib
            metadata = plistlib.loads(package.read('MeowChan.app/Contents/Info.plist'))
        require(metadata.get('MeowChanProductVersion') == version, 'Mac full prerelease identity missing')
        require(metadata.get('CFBundleShortVersionString') == metadata.get('CFBundleVersion') == version.split('-')[0],
                'Mac numeric bundle version mismatch')


def packages(root: Path, source: str, version: str, legal: dict) -> dict[str, Path]:
    feed = root / 'library-package-candidate'
    expected = {package_id + '.' + version + '.nupkg' for package_id in PACKAGE_IDS}
    require({p.name for p in feed.iterdir()} == expected, 'Library/SDK/Host candidate inventory mismatch')
    result = {}
    for package_id in PACKAGE_IDS:
        path = feed / (package_id + '.' + version + '.nupkg')
        with zipfile.ZipFile(retained.file(path)) as archive:
            specs = [name for name in archive.namelist() if name.endswith('.nuspec')]
            require(len(specs) == 1, 'NuGet package must have one nuspec')
            metadata = ET.fromstring(archive.read(specs[0]))
            def nodes(name):
                return [node for node in metadata.iter() if node.tag.rsplit('}', 1)[-1] == name]
            require(len(nodes('id')) == 1 and nodes('id')[0].text == package_id
                    and len(nodes('version')) == 1 and nodes('version')[0].text == version
                    and len(nodes('repository')) == 1 and nodes('repository')[0].get('commit') == source,
                    'NuGet package version/source mismatch: ' + package_id)
            require(archive.read('LICENSE') == legal['LICENSE'], 'NuGet license differs from source')
            require('lib/net10.0/' + package_id + '.dll' in archive.namelist(), 'NuGet managed assembly missing')
        result[path.name] = path
    return result


def assemble(root: Path, source: str, version: str, source_base: Path, output: Path,
             repository: Path, run_id: str) -> None:
    validate_version(version)
    require(retained.COMMIT.fullmatch(source) is not None, 'Expected full lowercase source commit SHA')
    require(re.fullmatch(r'[1-9][0-9]*', run_id) is not None, 'Expected originating Actions run ID')
    require(not output.exists() and not output.is_symlink(), 'Output already exists')
    api_review = public_api.audit(repository, source)
    public_api.require_release(api_review)
    legal = committed_legal(repository, source, version)
    retained.VERSION = version
    # All five platforms must bind to the same exact source; there are no overrides.
    check_windows(root, source, version, legal)
    for rid in ('linux-x64', 'linux-arm64'):
        check_linux(root, source, version, legal, rid)
    check_mac(root, source, version, legal)
    nuget = packages(root, source, version, legal)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.meowchan-prerelease-', dir=output.parent) as temporary:
        staged = Path(temporary) / 'candidate'
        retained.assemble(root, source, source_base, staged)
        for name, origin in nuget.items():
            shutil.copyfile(origin, staged / name)
            require(digest(staged / name) == digest(origin), 'NuGet changed while staging')
        records = []
        for path in sorted(staged.iterdir()):
            if path.name == 'SHA256SUMS':
                continue
            records.append({'file': path.name, 'bytes': path.stat().st_size, 'sha256': digest(path),
                            'source_commit': None if path.name == retained.BASE_NAME else source})
        require(len(records) == 16, 'Expected 13 desktop/source and three developer package assets')
        inventory = {'schema': 'meowchan.prerelease-candidate.v1', 'version': version,
                     'source_commit': source, 'actions_run_id': run_id, 'assets': records,
                     'scope': 'Current-source CI candidate inventory. No publication or complete R10-12/native user acceptance claim.',
                     'native_signing': 'macOS ad-hoc; no Developer ID, notarization or Gatekeeper acceptance claim',
                     'source_base': {'file': retained.BASE_NAME, 'bytes': retained.BASE_BYTES, 'sha256': retained.BASE_SHA},
                     'public_api_review': api_review,
                     'legal_sha256': {name: hashlib.sha256(data).hexdigest() for name, data in legal.items()}}
        (staged / 'candidate-inventory.json').write_text(json.dumps(inventory, indent=2, sort_keys=True) + '\n')
        (staged / 'SHA256SUMS').write_text(''.join(f'{digest(path)}  {path.name}\n' for path in sorted(staged.iterdir())
                                                  if path.name != 'SHA256SUMS'))
        staged.rename(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts-root', required=True, type=Path)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--source-base', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--repository', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--run-id', required=True)
    args = parser.parse_args()
    try:
        assemble(args.artifacts_root, args.source_commit, args.version, args.source_base, args.output, args.repository, args.run_id)
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError, zipfile.BadZipFile, ET.ParseError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Prerelease inventory rejected: {error}\n')
    print(f'Staged current-source prerelease candidate and checksums in {args.output}')


if __name__ == '__main__':
    main()
