"""Current-source prerelease/stable assembly; synthetic native evidence is never native proof."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('alpha_assembly', ROOT / 'scripts/assemble-prerelease.py')
A = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(A)
VERSION = '1.0.0-alpha.2'


def workflow_bash():
    """Use native Bash on POSIX and Git for Windows Bash, never the WSL shim."""
    if os.name != 'nt':
        bash = shutil.which('bash')
        if bash:
            return bash
    else:
        candidates = []
        git = shutil.which('git')
        if git:
            # git.exe is normally in Git/cmd or Git/bin (also Git/mingw64/bin).
            for parent in Path(git).resolve().parents:
                candidates.extend((parent / 'bin/bash.exe', parent / 'usr/bin/bash.exe'))
        for variable in ('ProgramW6432', 'ProgramFiles', 'ProgramFiles(x86)', 'LOCALAPPDATA'):
            base = os.environ.get(variable)
            if base:
                root = Path(base) / ('Programs/Git' if variable == 'LOCALAPPDATA' else 'Git')
                candidates.extend((root / 'bin/bash.exe', root / 'usr/bin/bash.exe'))
        for candidate in candidates:
            if candidate.is_file():
                return str(candidate)
    raise RuntimeError('Workflow guard tests require Bash (Git for Windows Bash on Windows)')


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


class WorkflowBashResolutionTests(unittest.TestCase):
    def test_windows_uses_git_installation_not_path_wsl_bash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'Git with spaces'
            git = write(root / 'cmd/git.exe', b'')
            bash = write(root / 'bin/bash.exe', b'')
            with patch.dict(globals(), os=SimpleNamespace(name='nt', environ={})), \
                    patch.object(shutil, 'which', side_effect=lambda name: str(git) if name == 'git' else 'WSL/bash.exe'):
                self.assertEqual(str(bash), workflow_bash())

    def test_windows_missing_git_bash_fails_instead_of_using_wsl(self):
        with patch.dict(globals(), os=SimpleNamespace(name='nt', environ={})), \
                patch.object(shutil, 'which', side_effect=lambda name: None if name == 'git' else 'WSL/bash.exe'):
            with self.assertRaisesRegex(RuntimeError, 'Git for Windows Bash'):
                workflow_bash()

    def test_posix_uses_path_bash(self):
        with patch.dict(globals(), os=SimpleNamespace(name='posix')), \
                patch.object(shutil, 'which', return_value='/bin/bash'):
            self.assertEqual('/bin/bash', workflow_bash())


class PrereleaseFixtures(unittest.TestCase):
    version = VERSION
    channel = 'prerelease'

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
        write(self.repo / 'Directory.Build.props', f'<Project><PropertyGroup><Version>{self.version}</Version></PropertyGroup></Project>')
        for project in A.public_api.PROJECTS:
            for kind in ('Shipped', 'Unshipped'):
                write(self.repo / project / ('PublicAPI.' + kind + '.txt'), '#nullable enable\n')
        # Apply the real shipping byte policy BEFORE the first git add. Otherwise
        # inherited Windows autocrlf normalizes the mixed-ending legal fixture.
        write(self.repo / '.gitattributes', (ROOT / '.gitattributes').read_bytes())
        self.git('init', '--quiet')
        self.git('add', '.')
        self.git('-c', 'user.name=Alpha fixture', '-c', 'user.email=alpha@example.invalid', 'commit', '--quiet', '-m', 'fixture')
        self.source = self.git('rev-parse', 'HEAD').strip()
        for name, expected in self.legal.items():
            self.assertEqual(expected, subprocess.check_output(
                ['git', '-C', str(self.repo), 'show', f'{self.source}:{name}']), name)
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
                    {'phase': phase, 'passed': True, 'assemblyInformationalVersion': self.version + '+' + self.source}
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
        write(root / 'version.txt', self.version)
        base = {'file': A.retained.BASE_NAME, 'bytes': A.retained.BASE_BYTES, 'sha256': A.retained.BASE_SHA}
        record(root / 'source-base.json', base)
        architectures = {}
        for rid in ('osx-arm64', 'osx-x64'):
            dmg = write(root / f'MeowChan-{self.version}-{rid}.dmg', (rid + ' DMG').encode())
            archive = root / 'evidence' / rid / f'MeowChan-{self.version}-{rid}.zip'
            archive.parent.mkdir(parents=True)
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('MeowChan.app/Contents/Info.plist', plistlib.dumps({
                    'CFBundleShortVersionString': self.version.split('-')[0], 'CFBundleVersion': self.version.split('-')[0], 'MeowChanProductVersion': self.version}))
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
        tar(root / f'MeowChan-{self.version}-macos-video-source-overlay.tar.gz', {'manifest.json': json.dumps({
            'schema': 'meowchan.macos-source-overlay.v1', 'version': self.version,
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
            path = write(packages / f'MeowChan-{self.version}-{rid}{suffix}', (rid + flavor).encode())
            prefix = 'usr/lib/meowchan/' if flavor == 'appimage' else ''
            artifacts.append({'file': path.name, 'flavor': flavor, 'bytes': path.stat().st_size,
                              'sha256': A.digest(path), 'payload_files': {prefix + name: hashlib.sha256(data).hexdigest() for name, data in self.legal.items()}})
        manifest = record(packages / (rid + '-package-manifest.json'), {
            'schema': 'meowchan.linux-packages.v1', 'version': self.version, 'source_commit': self.source,
            'runtime': rid, 'runtime_manifest_sha256': native_hash, 'artifacts': artifacts})
        source_archive = tar(packages / f'MeowChan-{self.version}-{rid}-video-sources.tar.gz', {
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
            path = self.root / 'library-package-candidate' / f'{package_id}.{self.version}.nupkg'
            path.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr(package_id + '.nuspec', f'<package><metadata><id>{package_id}</id><version>{self.version}</version><repository commit="{self.source}" /></metadata></package>')
                archive.writestr('LICENSE', self.legal['LICENSE'])
                archive.writestr(f'lib/net10.0/{package_id}.dll', b'fixture assembly')

    def assemble(self):
        A.assemble(self.root, self.source, self.version, self.base, self.output, self.repo, '123', self.channel)

    def reject(self, message):
        with self.assertRaisesRegex(ValueError, message):
            self.assemble()
        self.assertFalse(self.output.exists())

    def test_complete_inventory_preserves_source_base_identity_and_checksums(self):
        self.assemble()
        inventory = json.loads((self.output / 'candidate-inventory.json').read_text())
        self.assertEqual('meowchan.' + self.channel + '-candidate.v1', inventory['schema'])
        self.assertEqual(self.channel, inventory['channel'])
        self.assertEqual(16, len(inventory['assets']))
        self.assertEqual(self.version, inventory['version'])
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
        value['workflow']['scales'][0]['phases'][2]['assemblyInformationalVersion'] = self.version + '+' + 'b' * 40
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

    def test_committed_pending_api_cannot_be_hidden_by_clean_working_copy(self):
        pending = self.repo / 'libmeowchan/PublicAPI.Unshipped.txt'
        pending.write_text('#nullable enable\nUnreviewed.Contract\n')
        self.git('add', '.')
        self.git('-c', 'user.name=Alpha fixture', '-c', 'user.email=alpha@example.invalid', 'commit', '--quiet', '-m', 'pending API')
        self.source = self.git('rev-parse', 'HEAD').strip()
        pending.write_text('#nullable enable\n')
        self.reject('Unshipped public API review is incomplete')

    def test_other_release_kinds_are_rejected(self):
        for version in ['0.9.0', '1.0.0', '1.0.0-alpha.02', '1.0.0-alpha.2+local', '../1.0.0-alpha.2']:
            with self.subTest(version=version), self.assertRaises(ValueError):
                A.validate_version(version)

    def test_nuget_old_source_is_rejected(self):
        path = self.root / 'library-package-candidate' / f'libmeowchan.{self.version}.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('libmeowchan.nuspec', f'<package><metadata><id>libmeowchan</id><version>{self.version}</version><repository commit="{"b" * 40}" /></metadata></package>')
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
        (self.root / 'library-package-candidate' / f'MeowChan.PluginHost.{self.version}.nupkg').unlink()
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
        self.assertEqual(3, len(A.packages(downloaded, self.source, self.version, self.legal)))
        self.assertFalse((candidate / sidecar.name).exists())
        evidence = re.search(r'name: library-package-consumer-evidence\s+path: ([^\n]+)', workflow)
        self.assertIsNotNone(evidence)
        core_evidence = record(runner / 'artifacts/plugin-package-consumers/core-consumer-evidence.json',
                               {'packages': {'libmeowchan': {'exact_package_match': True}}})
        self.assertEqual([core_evidence], list(runner.glob(evidence.group(1).strip())))

    def test_asset_hash_change_fails_before_output(self):
        path = self.root / 'native-media-candidate-win-x64/media-packages/MeowChan-win-x64.zip'
        with zipfile.ZipFile(path, 'a') as package:
            package.writestr('tampered.txt', b'changed archive')
        self.reject('SHA-256 mismatch')


class StableFixtures(PrereleaseFixtures):
    # Re-run the complete synthetic evidence suite on the same shared guards.
    version = '1.0.0'
    channel = 'stable'

    def test_stable_channel_accepts_only_stable_1x(self):
        for version in ('1.0.0', '1.12.34'):
            A.validate_version(version, 'stable')
        for version in ('0.9.0', '2.0.0', '1.0.0-alpha.2', '1.0.0+local', '1.00.0', '../1.0.0'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                A.validate_version(version, 'stable')
        with self.assertRaises(ValueError):
            A.validate_version('1.0.0', 'unexpected')

    def test_stable_requires_explicit_channel_before_any_staging(self):
        with self.assertRaisesRegex(ValueError, 'Expected a 1.x prerelease'):
            A.assemble(self.root, self.source, self.version, self.base, self.output, self.repo, '123')
        self.assertFalse(self.output.exists())

    def test_stable_rejects_prerelease_identity_before_any_staging(self):
        with self.assertRaisesRegex(ValueError, 'Expected a 1.x stable'):
            A.assemble(self.root, self.source, VERSION, self.base, self.output, self.repo, '123', 'stable')
        self.assertFalse(self.output.exists())

    def test_stable_rejects_mismatching_committed_identity(self):
        with self.assertRaisesRegex(ValueError, 'Committed product version'):
            A.assemble(self.root, self.source, '1.0.1', self.base, self.output, self.repo, '123', 'stable')
        self.assertFalse(self.output.exists())

    def test_stable_rejects_unbound_run_and_abbreviated_source(self):
        for source, run_id in ((self.source, ''), (self.source, '0'), (self.source[:7], '123')):
            with self.subTest(source=source, run_id=run_id), self.assertRaises(ValueError):
                A.assemble(self.root, source, self.version, self.base, self.output, self.repo, run_id, 'stable')
        self.assertFalse(self.output.exists())

    def test_stable_rejects_changed_historical_source_base(self):
        self.base.write_bytes(b'different historical sources')
        self.reject('source base')


class CandidateWorkflowFixtures(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = (ROOT / '.github/workflows/dotnet-desktop.yml').read_text()
        cls.jobs = dict(re.findall(r'^  ([a-z][a-z-]+):\n(.*?)(?=^  [a-z][a-z-]+:|\Z)',
                                   cls.workflow.split('\njobs:\n', 1)[1], re.M | re.S))

    def condition(self, job, mode, scenario='all', platform='all', event='workflow_dispatch'):
        expression = re.search(r'^    if: (.+)$', self.jobs[job], re.M).group(1)
        return self.evaluate(expression, mode, scenario, platform, event)

    def evaluate(self, expression, mode='validate', scenario='all', platform='all',
                 event='workflow_dispatch'):
        for key, value in {'github.event_name': event, 'inputs.mode': mode,
                           'inputs.scenario': scenario, 'inputs.platform': platform}.items():
            expression = expression.replace(key, repr(value))
        expression = expression.replace('&&', ' and ').replace('||', ' or ')
        expression = re.sub(r'!(?!=)', ' not ', expression)
        return eval(expression, {'__builtins__': {}}, {})

    def test_both_candidate_modes_reach_every_same_run_dependency(self):
        required = ('validate', 'library-package', 'macos-package', 'package-media',
                    'linux-distribution', 'macos-source-candidate', 'current-source-inventory')
        for mode in ('prerelease-candidate', 'stable-candidate'):
            for job in required:
                with self.subTest(mode=mode, job=job):
                    self.assertTrue(self.condition(job, mode))
        self.assertIn('needs: [validate, library-package, macos-source-candidate]',
                      self.jobs['current-source-inventory'])
        self.assertIn('needs: [macos-package, package-media, linux-distribution]',
                      self.jobs['macos-source-candidate'])
        self.assertIn('needs: validate', self.jobs['library-package'])

    def test_diagnostic_and_partial_modes_cannot_stage_candidates(self):
        for mode in ('validate', 'package-media', 'linux-distribution', 'macos-package',
                     'macos-diagnose', 'homebrew-macos'):
            self.assertFalse(self.condition('current-source-inventory', mode), mode)
        for mode in ('prerelease-candidate', 'stable-candidate'):
            self.assertFalse(self.condition('library-package', mode, scenario='selection'))
            self.assertFalse(self.condition('current-source-inventory', mode, event='push'))
        guard = re.search(r'name: Require the complete current-source candidate matrix\n'
                          r'        if: [^\n]+\n        shell: bash\n        run: \|\n(.*?)'
                          r'(?=      -)', self.jobs['validate'], re.S).group(1)
        bash = workflow_bash()
        for platform, scenario, expected in (('all', 'all', 0), ('windows-2022', 'all', 1),
                                               ('all', 'selection', 1), ('all', 'gallery-errors', 1),
                                               ('windows-2022', 'gallery-errors', 1),
                                               ('all', 'gallery-features', 1),
                                               ('windows-2022', 'gallery-features', 1),
                                               ('all', 'gif-playback', 1),
                                               ('macos-15', 'gif-playback', 1),
                                               ('all', 'media-input', 1),
                                               ('windows-2022', 'media-input', 1)):
            command = guard.replace('${{ inputs.platform }}', platform).replace('${{ inputs.scenario }}', scenario)
            with self.subTest(platform=platform, scenario=scenario):
                result = subprocess.run([bash, '-e', '-c', command], capture_output=True, text=True)
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    def test_package_fixture_step_stops_at_first_failure_on_every_platform(self):
        body = re.search(r'^      - name: Check package layout fixtures\n(.*?)(?=^      - )',
                         self.jobs['validate'], re.M | re.S).group(1)
        self.assertIn('        shell: bash\n', body)
        condition = re.search(r'^        if: (.+)$', body, re.M).group(1)
        for scenario, expected in (('all', True), ('media-input', True), ('gui-only', False)):
            self.assertEqual(expected, self.evaluate(condition, scenario=scenario))
        command = body.split('        run: |\n', 1)[1]
        self.assertIn('set -euo pipefail', command)
        # Run the real extracted multi-command shape with a fake failing Python.
        # A late successful suite must not mask the first suite's nonzero status.
        script = 'python() { echo attempted; return 17; }\n' + command
        result = subprocess.run([workflow_bash(), '-c', script], capture_output=True, text=True)
        self.assertEqual(17, result.returncode, result.stdout + result.stderr)
        self.assertEqual(['attempted'], result.stdout.splitlines())

    def test_gallery_selectors_isolate_existing_scenes_but_retain_preparation_and_evidence(self):
        steps = dict(re.findall(r'^      - name: ([^\n]+)\n(.*?)(?=^      - |\Z)',
                                self.jobs['validate'], re.M | re.S))
        selectors = {
            'gallery-errors': 'Exercise gallery error interaction on the actual platform',
            'gallery-features': 'Exercise changed gallery features on the actual platform',
            'gif-playback': 'Exercise GIF playback and ownership on the actual platform',
            'media-input': 'Exercise real OS media input (not synthetic routed keys)',
        }
        gallery = selectors['gallery-errors']
        scenes = {name: body for name, body in steps.items()
                  if 'scripts/run-desktop-smoke.py' in body
                  or 'scripts/test-media-interaction-smoke.py' in body
                  or name.startswith('Replay genuine')}
        self.assertGreater(len(scenes), 10)
        startup = 'Exercise native graphical startup and fallback paths'
        full_only = {
            'Replay genuine 1.0.0 and 1.0.1 profiles with confirmed new selections',
            'Exercise real OS media input (not synthetic routed keys)',
            'Exercise tray lifecycle separately on the disposable runner',
            'Check native Linux tray host lifecycle on private DBus',
        }
        for os in ('Windows', 'Linux', 'macOS'):
            for scenario in ('all', 'gui-only', 'selection', *selectors):
                for name, body in scenes.items():
                    condition = re.search(r'^        if: (.+)$', body, re.M)
                    expression = condition.group(1) if condition else 'True'
                    expression = expression.replace('runner.os', repr(os))
                    if scenario in selectors:
                        expected = name == selectors[scenario]
                    elif scenario == 'selection':
                        expected = name == startup
                    else:
                        expected = scenario == 'all' or name not in full_only
                        if name == 'Check native Linux tray host lifecycle on private DBus':
                            expected = expected and os == 'Linux'
                    with self.subTest(os=os, scenario=scenario, scene=name):
                        self.assertEqual(expected, self.evaluate(expression, scenario=scenario))
                        self.assertEqual(
                            name != 'Check native Linux tray host lifecycle on private DBus' or os == 'Linux',
                            self.evaluate(expression, event='push'))
        for name in ('Record SDK and source commit',
                     'Restore graphical harness with reviewed dependencies',
                     'Retain exact graphical harness dependency graph',
                     'Build graphical harness',
                     'Verify plugin delivery in desktop and transitive harness outputs'):
            self.assertNotRegex(steps[name], r'^        if:', name)
        self.assertIn("MEOWCHAN_GALLERY_ERROR_ONLY: '1'", steps[gallery])
        self.assertIn('--mode normal --timeout 300 --output artifacts/closure-gallery-errors',
                      steps[gallery])
        self.assertIn('artifacts/closure-gallery-errors/**', self.jobs['validate'])
        self.assertIn('artifacts/build-evidence/**', self.jobs['validate'])
        self.assertIn("if: always()", self.jobs['validate'])
        features = steps[selectors['gallery-features']]
        self.assertIn("MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY: '1'", features)
        self.assertIn('if [[ "$RUNNER_OS" == Linux ]]; then', features)
        self.assertIn('scripts/with-private-null-audio.py --report artifacts/private-audio/gallery-features.json --', features)
        self.assertIn('env MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY=1 MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1', features)
        self.assertEqual(2, features.count('--mode normal --timeout 300 --output artifacts/gallery-features'))
        self.assertIn('--allow-native-desktop --mode normal', features)
        self.assertIn('artifacts/gallery-features/**', self.jobs['validate'])
        self.assertIn('artifacts/private-audio/**', self.jobs['validate'])
        gif = steps[selectors['gif-playback']]
        self.assertIn("MEOWCHAN_GIF_PLAYBACK_ONLY: '1'", gif)
        self.assertIn('--mode normal --timeout 300 --output artifacts/closure-gif-playback', gif)
        self.assertIn('artifacts/closure-gif-playback/**', self.jobs['validate'])
        for scenario in selectors:
            self.assertIn(f'          - {scenario}\n', self.workflow)
        self.assertIn('default: all', self.workflow.split('      scenario:', 1)[1].split('permissions:', 1)[0])
        matrix = re.search(r'^        os: (.+)$', self.jobs['validate'], re.M).group(1)
        self.assertIn("format('[\"{0}\"]', inputs.platform)", matrix)
        self.assertNotIn('inputs.scenario', matrix)
        for scenario in selectors:
            for platform in ('all', 'ubuntu-24.04', 'windows-2022', 'macos-15'):
                self.assertTrue(self.condition('validate', 'validate', scenario, platform))
                for job in self.jobs:
                    if job != 'validate':
                        with self.subTest(scenario=scenario, platform=platform, job=job):
                            self.assertFalse(self.condition(job, 'validate', scenario, platform))
            for mode in ('prerelease-candidate', 'stable-candidate'):
                self.assertFalse(self.condition('library-package', mode, scenario=scenario))

    def test_downloads_stay_in_current_run_and_candidate_channel_is_explicit(self):
        job = self.jobs['current-source-inventory']
        download = re.search(r'uses: actions/download-artifact@[^\n]+\n(.*?)(?=      -)', job, re.S).group(1)
        self.assertIn("pattern: '*candidate*'", download)
        for cross_run_option in ('run-id:', 'github-token:', 'repository:'):
            self.assertNotIn(cross_run_option, download)
        self.assertNotIn('always()', job)
        self.assertIn('--run-id "$GITHUB_RUN_ID"', job)
        self.assertIn('--source-commit "$GITHUB_SHA"', job)
        self.assertIn('channel=prerelease', job)
        self.assertIn('== stable-candidate ]]; then channel=stable', job)
        self.assertIn('--channel "$channel"', job)
        self.assertIn('name: ${{ inputs.mode }}-${{ github.sha }}', job)
        api = re.search(r'name: Check declared public APIs.*?run: \|\n(.*?)(?=      -)',
                        self.jobs['validate'], re.S).group(1)
        self.assertIn('== prerelease-candidate || "${{ inputs.mode }}" == stable-candidate', api)
        self.assertIn('api_args+=(--release)', api)


if __name__ == '__main__':
    unittest.main()
