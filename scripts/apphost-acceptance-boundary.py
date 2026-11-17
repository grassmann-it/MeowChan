#!/usr/bin/env python3
"""Fail-closed routing guard for NON-SHIPPING Debug apphost acceptance.

This is a capability/configuration preflight, not an acceptance scene or a PE
security oracle (ReleaseAssemblyBoundaryTests is the metadata oracle). Publishing
uses uncompressed bundles so the managed metadata/user strings remain visible.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

SWITCHES = {
    'macos-apphost-smoke.py': '--smoke-startup-seconds',
    'apphost-playback-smoke.py': '--smoke-playback-webm',
    'apphost-workflow-smoke.py': '--smoke-workflow',
    'apphost-plugin-workflow-smoke.py': '--smoke-plugin-workflow',
    'apphost-source-activation-smoke.py': '--smoke-source-activation',
    'apphost-integrity-process-smoke.py': '--smoke-source-policy-process',
    'apphost-video-error-smoke.py': '--smoke-video-error',
}


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def managed_payload(apphost):
    """Read product, not adjacent test DLLs. Support thin and single-file hosts."""
    apphost = Path(apphost).resolve(strict=True)
    if apphost.name == 'AppRun':
        return managed_payload(apphost.parent / 'usr/lib/meowchan/MeowChan')
    if apphost.suffix == '.AppImage':
        # No FUSE/desktop/network; extract only the product for capability inspection.
        with tempfile.TemporaryDirectory(prefix='meowchan-debug-preflight-') as temporary:
            subprocess.run([str(apphost), '--appimage-extract', 'usr/lib/meowchan/MeowChan'],
                           cwd=temporary, check=True, stdout=subprocess.DEVNULL, timeout=120)
            return managed_payload(Path(temporary) / 'squashfs-root/usr/lib/meowchan/MeowChan')
    candidates = [apphost.with_name('MeowChan.dll')]
    if apphost.parent.name == 'MacOS':
        candidates.insert(0, apphost.parent.parent / 'Resources/application/MeowChan.dll')
    for path in candidates:
        if path.is_file():
            return path.read_bytes()
    return apphost.read_bytes()  # uncompressed self-contained single-file bundle


def require_debug(apphost, runner):
    switch = SWITCHES[Path(runner).name]
    payload = managed_payload(apphost)
    if (b'RunDevelopmentHost' not in payload
            or b'\x01\x00\x05Debug\x00\x00' not in payload
            or switch.encode('utf-16-le') not in payload):
        raise ValueError('In-app acceptance requires a separate uncompressed Debug apphost containing '
                         + switch + '; never pass a shipping Release archive. See docs/smoke-release-boundary.md')
    return {'acceptanceConfiguration': 'Debug', 'shippingArtifactExecuted': False,
            'switch': switch, 'apphostSha256': sha256(apphost)}


def require_release_payload(payload):
    if (b'\x01\x00\x07Release\x00\x00' not in payload or b'RunDevelopmentHost' in payload
            or '--smoke-'.encode('utf-16-le') in payload):
        raise ValueError('Shipping artifacts must be Release without in-app acceptance switches')


def require_release(apphost):
    require_release_payload(managed_payload(apphost))


def dry_run(argv, runner):
    """--dry-run APPHOST validates routing without starting a UI or running scenes.

    It intentionally needs no archive, provenance, fixture or desktop arguments;
    real runs still validate all of those with the original parsers/assertions.
    """
    argv = list(sys.argv[1:] if argv is None else argv)
    if '--dry-run' not in argv:
        return False
    parser = argparse.ArgumentParser(description='Debug acceptance routing preflight only; not an acceptance pass')
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args(argv)
    proof = require_debug(args.apphost, runner)
    print(json.dumps(dict(proof, dryRun=True, acceptanceExecuted=False,
                          command=[str(args.apphost.resolve()), proof['switch'], '<scene arguments>']), indent=2))
    return True


def validate_macos_release(packages, evidence, version, runtime, commit):
    """Retain every Release DMG/ZIP/signature assertion separately from Debug UI proof."""
    packages, evidence = Path(packages), Path(evidence)
    if (evidence / 'source.txt').read_text().strip() != commit:
        raise ValueError('Release package source mismatch')
    if (evidence / 'version.txt').read_text().strip() != version:
        raise ValueError('Release package version mismatch')
    dmg = packages / f'MeowChan-{version}-{runtime}.dmg'
    archive = packages / f'MeowChan-{version}-{runtime}.zip'
    digest = sha256(dmg)
    report = json.loads((evidence / 'dmg.json').read_text())
    signatures = json.loads((evidence / 'signatures.json').read_text())
    if (report['runtime'] != runtime or report['dmg'] != dmg.name or report['dmgSha256'] != digest
            or (evidence / 'archive.sha256').read_text().split()[0] != digest
            or report['mountedAndCopied'] is not True or report['applicationsShortcut'] != '/Applications'
            or report['sourceSignatures']['files'] != report['copiedSignatures']['files']
            or signatures['runtime'] != runtime or signatures['files'] != report['copiedSignatures']['files']
            or not all(signatures[key] is True for key in ('signatures_verified', 'dependency_closure', 'coreclr_entitlements_verified'))):
        raise ValueError('Release DMG/signature/closure evidence mismatch')
    with zipfile.ZipFile(archive) as package:
        for name, expected in signatures['files'].items():
            if hashlib.sha256(package.read('MeowChan.app/' + name)).hexdigest() != expected:
                raise ValueError('Release ZIP/signature evidence mismatch: ' + name)
        host_hash = hashlib.sha256(package.read('MeowChan.app/Contents/MacOS/MeowChan')).hexdigest()
        if host_hash != report['apphostSha256']:
            raise ValueError('Release DMG apphost differs from inventory ZIP')
        product = package.read('MeowChan.app/Contents/Resources/application/MeowChan.dll')
        require_release_payload(product)
    return dmg, {'configuration': 'Release', 'executionClaim': False, 'dmgSha256': digest,
                 'inventoryZipSha256': sha256(archive), 'dmg': report, 'signatures': signatures}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--configuration', choices=('Debug', 'Release'), required=True)
    args = parser.parse_args()
    if args.configuration == 'Release':
        require_release(args.apphost)
    else:
        for runner in SWITCHES:
            require_debug(args.apphost, runner)
    print(json.dumps({'configuration': args.configuration, 'apphostSha256': sha256(args.apphost),
                      'boundaryVerified': True, 'acceptanceExecuted': False}))
