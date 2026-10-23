"""Current-source alpha assembly; synthetic native evidence is never native proof."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('alpha_assembly', ROOT / 'scripts/assemble-prerelease.py')
A = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(A)
VERSION = '1.0.0-alpha.2'


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else data.encode())
    return path


def record(path, value):
    return write(path, json.dumps(value, sort_keys=True))


def tar(path, values):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(path, 'w:gz') as archive:
        for name, data in values.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
    return path


class PrereleaseFixtures(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.directory = Path(self.tmp.name)
        self.root = self.directory / 'candidates'
        self.root.mkdir()
        self.repo = self.directory / 'repo'
        self.repo.mkdir()
        self.legal = {name: (name + ' legal fixture\nsecond line\n').encode() for name in A.layout.LEGAL_FILES}
        self.legal['docs/native-video-licenses.txt'] += b'upstream mixed endings\r\nretained verbatim\n'
        for name, data in self.legal.items():
            write(self.repo / name, data)
        write(self.repo / 'Directory.Build.props', f'<Project><PropertyGroup><Version>{VERSION}</Version></PropertyGroup></Project>')
        self.git('init', '--quiet')
        self.git('add', '.')
        self.git('-c', 'user.name=Alpha fixture', '-c', 'user.email=alpha@example.invalid', 'commit', '--quiet', '-m', 'fixture')
        self.source = self.git('rev-parse', 'HEAD').strip()
        self.base = write(self.directory / A.retained.BASE_NAME, b'fixture native corresponding-source base')
        self.patch = patch.multiple(A.retained, BASE_BYTES=self.base.stat().st_size, BASE_SHA=A.digest(self.base))
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.output = self.directory / 'output'
        self.make_windows()
        self.make_mac()
        for rid in ('linux-x64', 'linux-arm64'):
            self.make_linux(rid)
        self.make_nuget()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.repo), *args], text=True)

    def workflow(self, rid):
        return {'passed': True, 'completed': True, 'declaredSourceCommit': self.source,
                'declaredRuntime': rid, 'scales': [{'passed': True, 'phases': [
                    {'phase': phase, 'passed': True, 'assemblyInformationalVersion': VERSION + '+' + self.source}
                    for phase in A.PHASES]}]}

    def make_windows(self):
        root = self.root / 'native-media-candidate-win-x64'
        archive = root / 'media-packages/MeowChan-win-x64.zip'
        archive.parent.mkdir(parents=True)
        with zipfile.ZipFile(archive, 'w') as package:
            package.writestr('MeowChan.exe', b'actual fixture apphost')
            for name, data in self.legal.items():
                package.writestr(name, data)
        apphost_hash = hashlib.sha256(b'actual fixture apphost').hexdigest()
        workflow = self.workflow('win-x64')
        workflow.update(apphostSha256=apphost_hash, archiveBinding={
            'sha256': A.digest(archive), 'member': 'MeowChan.exe', 'memberSha256': apphost_hash})
        evidence = root / 'media-evidence'
        write(evidence / 'source.txt', self.source)
        write(evidence / 'archive.sha256', A.digest(archive) + '  ' + archive.name)
        record(evidence / 'playback/playback.json', {'passed': True})
        record(evidence / 'workflow/workflow.json', workflow)

    def make_mac(self):
        root = self.root / ('macos-paired-candidate-' + self.source)
        write(root / 'source.txt', self.source)
        write(root / 'version.txt', VERSION)
        base = {'file': A.retained.BASE_NAME, 'bytes': A.retained.BASE_BYTES, 'sha256': A.retained.BASE_SHA}
        record(root / 'source-base.json', base)
        architectures = {}
        for rid in ('osx-arm64', 'osx-x64'):
            dmg = write(root / f'MeowChan-{VERSION}-{rid}.dmg', (rid + ' DMG').encode())
            archive = root / 'evidence' / rid / f'MeowChan-{VERSION}-{rid}.zip'
            archive.parent.mkdir(parents=True)
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('MeowChan.app/Contents/Info.plist', plistlib.dumps({
                    'CFBundleShortVersionString': '1.0.0', 'CFBundleVersion': '1.0.0', 'MeowChanProductVersion': VERSION}))
                for name, data in self.legal.items():
                    package.writestr('MeowChan.app/Contents/Resources/application/' + name, data)
            workflow = self.workflow(rid)
            workflow['archiveBinding'] = {'sha256': A.digest(archive)}
            record(root / f'{rid}-native-validation.json', {
                'source_commit': self.source, 'rid': rid, 'dmg_sha256': A.digest(dmg),
                'inventory_zip_sha256': A.digest(archive), 'workflow': workflow,
                'dmg_validation': {'dmgSha256': A.digest(dmg), 'mountedAndCopied': True, 'applicationsShortcut': '/Applications'},
                'signatures': {'runtime': rid, 'signatures_verified': True, 'dependency_closure': True, 'coreclr_entitlements_verified': True},
                'startup': {'passed': True}, 'playback': {'passed': True}})
            architectures[rid] = {'signed_dmg': {'file': dmg.name, 'sha256': A.digest(dmg)}}
        tar(root / f'MeowChan-{VERSION}-macos-video-source-overlay.tar.gz', {'manifest.json': json.dumps({
            'schema': 'meowchan.macos-source-overlay.v1', 'version': VERSION,
            'application_source_commit': self.source, 'source_base': base, 'architectures': architectures}).encode()})
        self.mac_checksums()

    def mac_checksums(self):
        root = self.root / ('macos-paired-candidate-' + self.source)
        write(root / 'SHA256SUMS', ''.join(A.digest(path) + '  ' + path.relative_to(root).as_posix() + '\n'
              for path in sorted(root.rglob('*')) if path.is_file() and path.name != 'SHA256SUMS'))

    def make_linux(self, rid):
        root = self.root / ('linux-distribution-candidate-' + rid)
        packages = root / 'linux-packages'
        write(root / 'linux-evidence/source.txt', self.source)
        artifacts = []
        native = b'fixture exact native runtime inventory'
        native_hash = hashlib.sha256(native).hexdigest()
        for flavor, suffix in [('system-vlc', '-system-vlc.tar.gz'), ('bundled-vlc', '-bundled-vlc.tar.gz'), ('appimage', '.AppImage')]:
            path = write(packages / f'MeowChan-{VERSION}-{rid}{suffix}', (rid + flavor).encode())
            prefix = 'usr/lib/meowchan/' if flavor == 'appimage' else ''
            artifacts.append({'file': path.name, 'flavor': flavor, 'bytes': path.stat().st_size,
                              'sha256': A.digest(path), 'payload_files': {prefix + name: hashlib.sha256(data).hexdigest() for name, data in self.legal.items()}})
        manifest = record(packages / (rid + '-package-manifest.json'), {
            'schema': 'meowchan.linux-packages.v1', 'version': VERSION, 'source_commit': self.source,
            'runtime': rid, 'runtime_manifest_sha256': native_hash, 'artifacts': artifacts})
        source_archive = tar(packages / f'MeowChan-{VERSION}-{rid}-video-sources.tar.gz', {
            'manifest.json': json.dumps({'schema': 'meowchan.linux-sources.v1', 'source_commit': self.source,
                'runtimes': {rid: {'sha256': native_hash}}}).encode(),
            'inventories/' + rid + '-runtime-manifest.json': native})
        record(source_archive.with_name(source_archive.name + '.json'), {
            'schema': 'meowchan.linux-source-artifact.v1', 'source_commit': self.source, 'runtimes': [rid],
            'file': source_archive.name, 'bytes': source_archive.stat().st_size, 'sha256': A.digest(source_archive)})
        rows = []
        for scenario in ['bundled-vlc', 'system-vlc', 'missing-system-vlc', 'appimage']:
            evidence = root / 'linux-evidence/distributions' / scenario
            proof = {'passed': True, 'completed': True, 'sourceCommit': self.source,
                     'runtime': rid, 'manifestSha256': A.digest(manifest)}
            if scenario != 'missing-system-vlc':
                for label in (['extracted', 'extract-and-run'] if scenario == 'appimage' else ['extracted']):
                    workflow = record(evidence / label / 'workflow/workflow.json', self.workflow(rid))
                    proof['positive' if label == 'extracted' else 'extractAndRun'] = {'workflow': {'reportSha256': A.digest(workflow)}}
            path = record(evidence / 'scenario.json', proof)
            rows.append({'scenario': scenario, 'passed': True, 'reportSha256': A.digest(path)})
        record(root / 'linux-evidence/distributions/linux-distribution-gates.json', {
            'passed': True, 'completed': True, 'sourceCommit': self.source, 'runtime': rid,
            'manifestSha256': A.digest(manifest), 'scenarios': rows})

    def make_nuget(self):
        for package_id in A.PACKAGE_IDS:
            path = self.root / 'library-package-candidate' / f'{package_id}.{VERSION}.nupkg'
            path.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr(package_id + '.nuspec', f'<package><metadata><id>{package_id}</id><version>{VERSION}</version><repository commit="{self.source}" /></metadata></package>')
                archive.writestr('LICENSE', self.legal['LICENSE'])
                archive.writestr(f'lib/net10.0/{package_id}.dll', b'fixture assembly')

    def assemble(self):
        A.assemble(self.root, self.source, VERSION, self.base, self.output, self.repo, '123')

    def reject(self, message):
        with self.assertRaisesRegex(ValueError, message):
            self.assemble()
        self.assertFalse(self.output.exists())

    def test_complete_inventory_preserves_source_base_identity_and_checksums(self):
        self.assemble()
        inventory = json.loads((self.output / 'candidate-inventory.json').read_text())
        self.assertEqual(16, len(inventory['assets']))
        self.assertEqual(VERSION, inventory['version'])
        self.assertEqual('123', inventory['actions_run_id'])
        self.assertEqual(18, len(list(self.output.iterdir())))
        self.assertEqual(17, len((self.output / 'SHA256SUMS').read_text().splitlines()))
        for row in inventory['assets']:
            self.assertEqual(A.digest(self.output / row['file']), row['sha256'])
            self.assertEqual(None if row['file'] == A.retained.BASE_NAME else self.source, row['source_commit'])

    def test_old_binary_cannot_be_renamed_with_current_declarations(self):
        path = self.root / 'native-media-candidate-win-x64/media-evidence/workflow/workflow.json'
        value = json.loads(path.read_text())
        value['scales'][0]['phases'][0]['assemblyInformationalVersion'] = '0.9.0+' + self.source
        record(path, value)
        self.reject('Executed managed version/source')

    def test_stale_mac_managed_commit_is_rejected(self):
        path = self.root / ('macos-paired-candidate-' + self.source) / 'osx-arm64-native-validation.json'
        value = json.loads(path.read_text())
        value['workflow']['scales'][0]['phases'][2]['assemblyInformationalVersion'] = VERSION + '+' + 'b' * 40
        record(path, value)
        self.reject('Executed managed version/source')

    def test_linux_runtime_source_pairing_is_required(self):
        path = self.root / 'linux-distribution-candidate-linux-x64/linux-packages/linux-x64-package-manifest.json'
        value = json.loads(path.read_text())
        value['runtime_manifest_sha256'] = 'f' * 64
        record(path, value)
        self.reject('corresponding sources differ')

    def test_changed_native_evidence_is_rejected(self):
        path = self.root / 'linux-distribution-candidate-linux-x64/linux-evidence/distributions/system-vlc/scenario.json'
        write(path, '{}')
        self.reject('SHA-256 mismatch')

    def test_committed_version_is_checked(self):
        with self.assertRaisesRegex(ValueError, 'Committed product version'):
            A.committed_legal(self.repo, self.source, '1.0.0-alpha.3')

    def test_uncommitted_legal_files_do_not_change_source_binding(self):
        write(self.repo / 'LICENSE', b'local uncommitted notice')
        self.assemble()
        self.assertEqual(hashlib.sha256(self.legal['LICENSE']).hexdigest(), json.loads((self.output / 'candidate-inventory.json').read_text())['legal_sha256']['LICENSE'])

    def test_autocrlf_checkout_preserves_exact_shipped_legal_and_nuget_license_bytes(self):
        write(self.repo / '.gitattributes', (ROOT / '.gitattributes').read_bytes())
        write(self.repo / 'unprotected.txt', b'control first line\ncontrol second line\n')
        self.git('add', '.')
        self.git('-c', 'user.name=Alpha fixture', '-c', 'user.email=alpha@example.invalid',
                 'commit', '--quiet', '-m', 'shipping byte policy')
        checkout = self.directory / 'windows-checkout'
        # A Git checkout with Windows conversion enabled must still give the
        # archive packers and NuGet LICENSE the exact bytes offered as source.
        self.git('-c', 'core.autocrlf=true', 'checkout-index', '--prefix=' + str(checkout) + '/', '-a')
        self.assertEqual(b'control first line\r\ncontrol second line\r\n', (checkout / 'unprotected.txt').read_bytes())
        archive_path = self.directory / 'windows-legal.zip'
        with zipfile.ZipFile(archive_path, 'w') as archive:
            for name, expected in self.legal.items():
                actual = (checkout / name).read_bytes()
                self.assertEqual(expected, actual, name)
                archive.writestr(name, actual)
        A.check_legal_zip(archive_path, '', self.legal)
        with zipfile.ZipFile(archive_path) as archive:
            self.assertEqual(self.legal['LICENSE'], archive.read('LICENSE'))

    def test_other_release_kinds_are_rejected(self):
        for version in ['0.9.0', '1.0.0', '1.0.0-alpha.02', '1.0.0-alpha.2+local', '../1.0.0-alpha.2']:
            with self.subTest(version=version), self.assertRaises(ValueError):
                A.validate_version(version)

    def test_nuget_old_source_is_rejected(self):
        path = self.root / 'library-package-candidate' / f'libmeowchan.{VERSION}.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('libmeowchan.nuspec', f'<package><metadata><id>libmeowchan</id><version>{VERSION}</version><repository commit="{"b" * 40}" /></metadata></package>')
        self.reject('NuGet package version/source mismatch')

    def test_stale_binary_notices_are_rejected_even_with_updated_archive_hash(self):
        path = self.root / 'native-media-candidate-win-x64/media-packages/MeowChan-win-x64.zip'
        with zipfile.ZipFile(path) as archive:
            contents = {name: archive.read(name) for name in archive.namelist()}
        contents['THIRD-PARTY-NOTICES.md'] = b'previous release notices'
        with zipfile.ZipFile(path, 'w') as archive:
            for name, data in contents.items():
                archive.writestr(name, data)
        workflow_path = path.parents[1] / 'media-evidence/workflow/workflow.json'
        workflow = json.loads(workflow_path.read_text())
        workflow['archiveBinding']['sha256'] = A.digest(path)
        record(workflow_path, workflow)
        self.reject('Packaged legal file differs')

    def test_missing_developer_package_fails_without_partial_output(self):
        (self.root / 'library-package-candidate' / f'MeowChan.PluginHost.{VERSION}.nupkg').unlink()
        self.reject('Library/SDK/Host candidate inventory mismatch')

    def test_workflow_upload_glob_keeps_consumer_sidecar_out_of_strict_package_inventory(self):
        workflow = (ROOT / '.github/workflows/dotnet-desktop.yml').read_text()
        match = re.search(r'name: library-package-candidate\s+path: ([^\n]+)', workflow)
        self.assertIsNotNone(match)
        runner = self.directory / 'runner-workspace'
        feed = runner / 'artifacts/packages'
        feed.mkdir(parents=True)
        for path in (self.root / 'library-package-candidate').iterdir():
            shutil.copyfile(path, feed / path.name)
        # test-local-package.py intentionally writes this beside the nupkgs.
        sidecar = record(feed / 'consumer-evidence.json', {'exact_package_match': True})
        downloaded = self.directory / 'downloaded-artifacts'
        candidate = downloaded / 'library-package-candidate'
        candidate.mkdir(parents=True)
        for path in runner.glob(match.group(1).strip()):
            shutil.copyfile(path, candidate / path.name)
        self.assertEqual(3, len(A.packages(downloaded, self.source, VERSION, self.legal)))
        self.assertFalse((candidate / sidecar.name).exists())
        evidence = re.search(r'name: library-package-consumer-evidence\s+path: ([^\n]+)', workflow)
        self.assertIsNotNone(evidence)
        self.assertEqual([sidecar], list(runner.glob(evidence.group(1).strip())))

    def test_asset_hash_change_fails_before_output(self):
        path = self.root / 'native-media-candidate-win-x64/media-packages/MeowChan-win-x64.zip'
        with zipfile.ZipFile(path, 'a') as package:
            package.writestr('tampered.txt', b'changed archive')
        self.reject('SHA-256 mismatch')


if __name__ == '__main__':
    unittest.main()
