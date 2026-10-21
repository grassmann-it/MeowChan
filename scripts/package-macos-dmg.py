#!/usr/bin/env python3
"""Create and verify the public macOS DMG from an already signed MeowChan.app."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def sibling_module(name: str):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def create_and_verify(bundle: Path, dmg: Path, copied_root: Path, runtime: str) -> dict:
    if sys.platform != 'darwin':
        raise ValueError('A native Mac and hdiutil are required for DMG packaging.')
    bundle = bundle.resolve(strict=True)
    if bundle.name != 'MeowChan.app' or not bundle.is_dir():
        raise ValueError('Expected a complete MeowChan.app bundle.')
    if dmg.exists() or dmg.is_symlink() or copied_root.exists() or copied_root.is_symlink():
        raise ValueError('Refusing to replace an existing DMG or copied app directory.')
    if dmg.resolve().is_relative_to(bundle) or copied_root.resolve().is_relative_to(bundle):
        raise ValueError('DMG and copied app must be outside the source bundle.')
    packaging = sibling_module('package-macos')
    signing = sibling_module('macos-signing')
    source_signatures = signing.validate(bundle, runtime)
    source_tree = packaging.tree_manifest(bundle)
    dmg.parent.mkdir(parents=True, exist_ok=True)
    copied_root.parent.mkdir(parents=True, exist_ok=True)
    created_dmg = False
    created_copy = False
    try:
        with tempfile.TemporaryDirectory(prefix='meowchan-dmg-') as scratch:
            scratch = Path(scratch)
            volume = scratch / 'volume'
            volume.mkdir()
            subprocess.run(['/usr/bin/ditto', str(bundle), str(volume / bundle.name)], check=True)
            (volume / 'Applications').symlink_to('/Applications')
            staged = scratch / 'candidate.dmg'
            subprocess.run(['/usr/bin/hdiutil', 'create', '-quiet', '-format', 'UDZO', '-fs', 'HFS+',
                            '-volname', 'MeowChan', '-srcfolder', str(volume), str(staged)], check=True)
            mount = scratch / 'mounted'
            mount.mkdir()
            attached = False
            try:
                subprocess.run(['/usr/bin/hdiutil', 'attach', '-quiet', '-readonly', '-nobrowse',
                                '-mountpoint', str(mount), str(staged)], check=True)
                attached = True
                link = mount / 'Applications'
                if not link.is_symlink() or os.readlink(link) != '/Applications':
                    raise ValueError('DMG lost the Applications install shortcut.')
                if {entry.name for entry in mount.iterdir()} != {'MeowChan.app', 'Applications'}:
                    raise ValueError('DMG contains unexpected root entries.')
                mounted = mount / bundle.name
                if packaging.tree_manifest(mounted) != source_tree:
                    raise ValueError('Mounted DMG changed signed app contents, modes or symlinks.')
                signing.validate(mounted, runtime)
                copied_root.mkdir()
                created_copy = True
                copied = copied_root / bundle.name
                subprocess.run(['/usr/bin/ditto', str(mounted), str(copied)], check=True)
                if packaging.tree_manifest(copied) != source_tree:
                    raise ValueError('DMG-installed app differs from the signed source bundle.')
                copied_signatures = signing.validate(copied, runtime)
            finally:
                if attached:
                    subprocess.run(['/usr/bin/hdiutil', 'detach', '-quiet', str(mount)], check=True)
            with dmg.open('xb') as target, staged.open('rb') as source:
                created_dmg = True
                shutil.copyfileobj(source, target)
        with dmg.open('rb') as stream:
            dmg_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
        return {'runtime': runtime, 'dmg': dmg.name,
                'dmgSha256': dmg_hash,
                'appTreeEntries': len(source_tree),
                'apphostSha256': hashlib.sha256((copied_root / 'MeowChan.app/Contents/MacOS/MeowChan').read_bytes()).hexdigest(),
                'mountedAndCopied': True, 'applicationsShortcut': '/Applications',
                'sourceSignatures': source_signatures, 'copiedSignatures': copied_signatures}
    except BaseException:
        if created_dmg:
            dmg.unlink()
        if created_copy:
            shutil.rmtree(copied_root)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', required=True, type=Path)
    parser.add_argument('--dmg', required=True, type=Path)
    parser.add_argument('--copied-root', required=True, type=Path)
    parser.add_argument('--runtime', required=True, choices=['osx-arm64', 'osx-x64'])
    args = parser.parse_args()
    try:
        print(json.dumps(create_and_verify(args.bundle, args.dmg, args.copied_root, args.runtime), indent=2))
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'DMG packaging failed: {error}\n')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
