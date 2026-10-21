"""Offline release assembly fixtures matching the package-media output schemas."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/assemble-stable-release.py'
SPEC = importlib.util.spec_from_file_location('stable_release', SCRIPT)
ASSEMBLY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ASSEMBLY)
SOURCE = 'a' * 40


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else data.encode('utf-8'))
    return path


def write_json(path, value):
    return write(path, json.dumps(value, sort_keys=True))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class StableReleaseFixtures(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'candidates'
        self.root.mkdir()
        self.output = Path(self.temporary.name) / 'release'
        self.base = write(Path(self.temporary.name) / ASSEMBLY.BASE_NAME, b'fixture source base')
        self.base_patch = patch.multiple(ASSEMBLY, BASE_BYTES=self.base.stat().st_size, BASE_SHA=sha(self.base))
        self.base_patch.start()
        self.addCleanup(self.base_patch.stop)
        self.make_windows()
        for rid in ('linux-x64', 'linux-arm64'):
            self.make_linux(rid)
        self.make_mac()

    def make_windows(self):
        root = self.root / 'native-media-candidate-win-x64'
        archive = write(root / 'media-packages/MeowChan-win-x64.zip', b'Windows ZIP fixture')
        evidence = root / 'media-evidence'
        write(evidence / 'source.txt', SOURCE)
        write(evidence / 'archive.sha256', f'{sha(archive)}  {archive.name}\n')
        write_json(evidence / 'playback/playback.json', {'passed': True})
        write_json(evidence / 'workflow/workflow.json', {
            'passed': True, 'completed': True, 'declaredSourceCommit': SOURCE,
            'declaredRuntime': 'win-x64',
            'archiveBinding': {'member': 'MeowChan.exe', 'sha256': sha(archive)},
        })

    def make_linux(self, rid):
        root = self.root / f'linux-distribution-candidate-{rid}'
        packages = root / 'linux-packages'
        write(root / 'linux-evidence/source.txt', SOURCE)
        artifacts = []
        for flavor, suffix in (('system-vlc', '-system-vlc.tar.gz'),
                               ('bundled-vlc', '-bundled-vlc.tar.gz'), ('appimage', '.AppImage')):
            name = f'MeowChan-0.9.0-{rid}{suffix}'
            archive = write(packages / name, (rid + flavor).encode())
            artifacts.append({'flavor': flavor, 'file': name,
                              'bytes': archive.stat().st_size, 'sha256': sha(archive)})
        manifest = write_json(packages / f'{rid}-package-manifest.json', {
            'schema': 'meowchan.linux-packages.v1', 'source_commit': SOURCE,
            'runtime': rid, 'version': '0.9.0', 'artifacts': artifacts,
        })
        source_name = f'MeowChan-0.9.0-{rid}-video-sources.tar.gz'
        sources = write(packages / source_name, b'Linux source fixture')
        write_json(packages / (source_name + '.json'), {
            'schema': 'meowchan.linux-source-artifact.v1', 'source_commit': SOURCE,
            'file': source_name, 'bytes': sources.stat().st_size, 'sha256': sha(sources),
            'runtimes': [rid],
        })
        write_json(root / 'linux-evidence/distributions/linux-distribution-gates.json', {
            'passed': True, 'completed': True, 'sourceCommit': SOURCE,
            'runtime': rid, 'manifestSha256': sha(manifest),
            'scenarios': [{'scenario': scenario, 'passed': True} for scenario in
                          ('bundled-vlc', 'system-vlc', 'missing-system-vlc', 'appimage')],
        })

    def make_mac(self):
        root = self.root / f'macos-paired-candidate-{SOURCE}'
        write(root / 'source.txt', SOURCE)
        write(root / 'version.txt', '0.9.0')
        base = {'file': ASSEMBLY.BASE_NAME, 'bytes': ASSEMBLY.BASE_BYTES,
                'sha256': ASSEMBLY.BASE_SHA}
        write_json(root / 'source-base.json', base)
        architectures = {}
        for rid in ('osx-arm64', 'osx-x64'):
            name = f'MeowChan-0.9.0-{rid}.dmg'
            dmg = write(root / name, (rid + ' DMG').encode())
            architectures[rid] = {'signed_dmg': {'file': name, 'sha256': sha(dmg)}}
            write_json(root / f'{rid}-native-validation.json', {
                'source_commit': SOURCE, 'rid': rid, 'dmg_sha256': sha(dmg),
                'dmg_validation': {'dmgSha256': sha(dmg), 'mountedAndCopied': True,
                                   'applicationsShortcut': '/Applications'},
                'signatures': {'runtime': rid, 'signatures_verified': True,
                               'dependency_closure': True, 'coreclr_entitlements_verified': True},
                'startup': {'passed': True}, 'playback': {'passed': True},
                'workflow': {'passed': True, 'completed': True,
                             'declaredSourceCommit': SOURCE, 'declaredRuntime': rid},
            })
        overlay = root / 'MeowChan-0.9.0-macos-video-source-overlay.tar.gz'
        manifest = json.dumps({
            'schema': 'meowchan.macos-source-overlay.v1', 'version': '0.9.0',
            'application_source_commit': SOURCE, 'source_base': base,
            'architectures': architectures,
        }).encode()
        with tarfile.open(overlay, 'w:gz') as archive:
            info = tarfile.TarInfo('manifest.json')
            info.size = len(manifest)
            archive.addfile(info, io.BytesIO(manifest))
        files = sorted(path for path in root.rglob('*') if path.is_file())
        write(root / 'SHA256SUMS', ''.join(
            f'{sha(path)}  {path.relative_to(root).as_posix()}\n' for path in files))

    def test_stages_exact_public_set_and_checksums(self):
        ASSEMBLY.assemble(self.root, SOURCE, self.base, self.output)
        expected = {'MeowChan-0.9.0-win-x64.zip', 'MeowChan-0.9.0-osx-arm64.dmg',
                    'MeowChan-0.9.0-osx-x64.dmg', ASSEMBLY.BASE_NAME,
                    'MeowChan-0.9.0-macos-video-source-overlay.tar.gz'}
        for rid in ('linux-x64', 'linux-arm64'):
            expected.update({f'MeowChan-0.9.0-{rid}-{mode}.tar.gz'
                             for mode in ('system-vlc', 'bundled-vlc')})
            expected.update({f'MeowChan-0.9.0-{rid}.AppImage',
                             f'MeowChan-0.9.0-{rid}-video-sources.tar.gz'})
        self.assertEqual(expected | {'SHA256SUMS'}, {path.name for path in self.output.iterdir()})
        lines = (self.output / 'SHA256SUMS').read_text().splitlines()
        self.assertEqual(13, len(lines))
        self.assertEqual({f'{sha(self.output / name)}  {name}' for name in expected}, set(lines))

    def test_rejects_wrong_source_without_output(self):
        with self.assertRaisesRegex(ValueError, 'Source commit mismatch'):
            ASSEMBLY.assemble(self.root, 'b' * 40, self.base, self.output)
        self.assertFalse(self.output.exists())

    def test_rejects_changed_archive_hash_without_output(self):
        path = self.root / 'linux-distribution-candidate-linux-x64/linux-packages/MeowChan-0.9.0-linux-x64.AppImage'
        write(path, b'changed artifact bytes')
        with self.assertRaisesRegex(ValueError, '(length|SHA-256) mismatch'):
            ASSEMBLY.assemble(self.root, SOURCE, self.base, self.output)
        self.assertFalse(self.output.exists())

    def test_rejects_wrong_public_asset_name_without_output(self):
        rid = 'linux-x64'
        root = self.root / f'linux-distribution-candidate-{rid}'
        path = root / 'linux-packages' / f'{rid}-package-manifest.json'
        manifest = json.loads(path.read_text())
        manifest['artifacts'][0]['file'] = 'wrong-name.tar.gz'
        write_json(path, manifest)
        gate = root / 'linux-evidence/distributions/linux-distribution-gates.json'
        proof = json.loads(gate.read_text())
        proof['manifestSha256'] = sha(path)
        write_json(gate, proof)
        with self.assertRaisesRegex(ValueError, 'Linux asset name mismatch'):
            ASSEMBLY.assemble(self.root, SOURCE, self.base, self.output)
        self.assertFalse(self.output.exists())


if __name__ == '__main__':
    unittest.main()
