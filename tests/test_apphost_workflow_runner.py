"""Workflow runner safety/verification tests; fake apphosts are not product proof."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import struct
import sys
import tempfile
import tarfile
from types import SimpleNamespace
import unittest
from unittest import mock
import warnings
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('workflow_runner', ROOT / 'scripts/apphost-workflow-smoke.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def metrics(phase='download', scale=1):
    return dict(schemaVersion=1, completed=True, failure=None, phase=phase, checks=sorted(runner.CHECKS[phase]),
                renderScaling=scale, processId=123, workingDirectory=str(Path('/owned/cwd')), profile=str(Path('/owned/profile')),
                assemblyInformationalVersion='0.9.0+0123456789abcdef',
                externalDispatch=dict(kind='intercepted-export-command', target='/owned/exports/result'),
                galleryLoad=dict(activeDownloadThroughout=True, navigationMilliseconds=[20] * 8,
                                 presentedPosts=['9901', '9900'] * 4,
                                 heartbeat=dict(Samples=20, HardStalls=0, MaxGapMilliseconds=25,
                                                OngoingSilenceMilliseconds=10)))


def png_bytes():
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(b'\x00\x00\x00\x00')) + chunk(b'IEND', b''))


@contextlib.contextmanager
def workflow_root():
    with tempfile.TemporaryDirectory(prefix='meowchan-workflow-test-') as temporary:
        root = Path(temporary).resolve(strict=True)
        (root / '.workflow-owner').write_text('a' * 32)
        yield root


def fake_apphost(directory, behavior='pass'):
    """A subprocess that follows the wire format, used only to test orchestration."""
    path = directory / 'fake-apphost'
    path.write_text('#!' + sys.executable + '\n' + '''import json, os, sys, time
from pathlib import Path
assert sys.argv[1] == '--smoke-workflow'
descriptor = Path(sys.argv[2])
d = json.loads(descriptor.read_text())
root = Path(d['root'])
assert descriptor == root / (d['phase'] + '.json')
assert (root / '.workflow-owner').read_text() == d['ownerToken']
assert 'OCHAN_PROFILE' not in os.environ
profile = Path(os.environ['MEOWCHAN_PROFILE'])
assert profile == root / ('profile-' + d['phase']) and not profile.exists()
assert Path.cwd() == root / ('cwd-' + d['phase'])
assert float(os.environ['AVALONIA_GLOBAL_SCALE_FACTOR']) == d['expectedScale']
prior = d['previousProfile']
if d['phase'] in ('restore', 'removed'):
    previous = Path(prior)
    assert previous.parent == root
    assert json.loads((previous / 'apphost-workflow.json').read_text())['completed']
else:
    assert prior is None
profile.mkdir()
''' + f'checks = {repr(runner.CHECKS)}\nbehavior = {behavior!r}\npng = {png_bytes()!r}\n' + '''
m = dict(schemaVersion=1, completed=True, failure=None, phase=d['phase'], checks=sorted(checks[d['phase']]),
         renderScaling=d['expectedScale'], processId=os.getpid(), workingDirectory=str(Path.cwd()), profile=str(profile),
         assemblyInformationalVersion='fake-runner-fixture',
         externalDispatch=dict(kind='intercepted-export-command', target=str(root / 'exports' / 'result')),
         galleryLoad=dict(activeDownloadThroughout=True, navigationMilliseconds=[20] * 8,
                          presentedPosts=['9901', '9900'] * 4,
                          heartbeat=dict(Samples=20, HardStalls=0, MaxGapMilliseconds=25,
                                         OngoingSilenceMilliseconds=10)))
if behavior == 'wrong-phase':
    m['phase'] = 'other'
(profile / 'apphost-workflow.json').write_text(json.dumps(m))
(profile / 'apphost-workflow-frame.png').write_bytes(png)
if d['phase'] == 'download' and behavior != 'no-screenshots':
    (profile / 'apphost-workflow-compact.png').write_bytes(png)
    (profile / 'apphost-workflow-export.png').write_bytes(png)
print(json.dumps(dict(profile=str(profile), pid=os.getpid())), flush=True)
if behavior == 'timeout':
    time.sleep(30)
elif behavior == 'nonzero':
    print('APPHOST WORKFLOW PASS', flush=True)
    sys.exit(1)
elif behavior != 'no-marker':
    print('APPHOST WORKFLOW PASS', flush=True)
''', encoding='utf-8')
    path.chmod(0o755)
    return path


class WorkflowMetricsTests(unittest.TestCase):
    def test_gallery_load_cannot_claim_success_without_active_download_and_distinct_targets(self):
        for field, value in [('activeDownloadThroughout', False), ('navigationMilliseconds', [1] * 7),
                             ('navigationMilliseconds', [float('nan')] * 8), ('presentedPosts', ['9900'] * 8),
                             ('heartbeat', dict(Samples=20, HardStalls=1, MaxGapMilliseconds=25)),
                             ('heartbeat', dict(Samples=20, HardStalls=0, MaxGapMilliseconds=25,
                                                OngoingSilenceMilliseconds=2000)),
                             ('heartbeat', dict(Samples=0, HardStalls=0, MaxGapMilliseconds=25))]:
            bad = metrics()
            bad['galleryLoad'][field] = value
            with self.subTest(field=field, value=value), self.assertRaisesRegex(ValueError, 'Gallery load'):
                runner.validate_metrics(bad, 'download', 1)

    def test_requires_every_phase_check_and_explicit_completion(self):
        for phase in runner.PHASES:
            good = metrics(phase)
            runner.validate_metrics(good, phase, 1)
            for check in runner.CHECKS[phase]:
                bad = metrics(phase)
                bad['checks'].remove(check)
                with self.subTest(phase=phase, check=check), self.assertRaisesRegex(ValueError, 'omitted'):
                    runner.validate_metrics(bad, phase, 1)
        for field, value in [('schemaVersion', True), ('completed', False), ('failure', 'failed'),
                             ('phase', 'restore'), ('checks', 'download-paused'), ('checks', [None]),
                             ('processId', True), ('processId', 0), ('assemblyInformationalVersion', '')]:
            bad = metrics()
            bad[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                runner.validate_metrics(bad, 'download', 1)
        bad = metrics()
        del bad['failure']
        with self.assertRaises(ValueError):
            runner.validate_metrics(bad, 'download', 1)

    def test_checks_scaling_and_native_observation(self):
        for value in (None, True, 0, -1, float('nan'), float('inf'), '1'):
            bad = metrics(scale=value)
            for isolated in (True, False):
                with self.subTest(value=value, isolated=isolated), self.assertRaises(ValueError):
                    runner.validate_metrics(bad, 'download', 1, isolated=isolated)
        with self.assertRaisesRegex(ValueError, 'Requested scale'):
            runner.validate_metrics(metrics(scale=1), 'download', 2)
        runner.validate_metrics(metrics(scale=2), 'download', 1, isolated=False)
        runner.validate_metrics(metrics(scale=1.5), 'download', 1.5)

    def test_checks_reported_process_context_and_external_boundary(self):
        good = metrics()
        runner.validate_metrics(good, 'download', 1, isolated=False, working=Path('/owned/cwd'),
                                profile=Path('/owned/profile'), process_id=123)
        for field, value in [('working', Path('/wrong')), ('profile', Path('/wrong')), ('process_id', 456)]:
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'mismatch'):
                runner.validate_metrics(good, 'download', 1, isolated=False, **{field: value})
        for dispatch in (None, {}, {'kind': 'native-open', 'target': '/result'},
                         {'kind': 'intercepted-export-command', 'target': ''}):
            bad = metrics()
            bad['externalDispatch'] = dispatch
            with self.subTest(dispatch=dispatch), self.assertRaisesRegex(ValueError, 'handoff'):
                runner.validate_metrics(bad, 'download', 1)

    def test_process_paths_use_platform_spelling_and_still_require_exact_matches(self):
        for root in (PurePosixPath('/owned'), PureWindowsPath('C:/owned')):
            working, profile = root / 'cwd', root / 'profile'
            good = metrics()
            good.update(workingDirectory=str(working), profile=str(profile))
            with self.subTest(path_type=type(root).__name__):
                runner.validate_metrics(good, 'download', 1, isolated=False, working=working,
                                        profile=profile, process_id=123)
                for field in ('workingDirectory', 'profile'):
                    bad = good.copy()
                    bad[field] = str(root / 'wrong')
                    with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'mismatch'):
                        runner.validate_metrics(bad, 'download', 1, isolated=False, working=working,
                                                profile=profile, process_id=123)
                bad = good.copy()
                bad['workingDirectory'] = str(working).replace('\\', '/') if isinstance(root, PureWindowsPath) else str(working).replace('/', '\\')
                with self.assertRaisesRegex(ValueError, 'mismatch'):
                    runner.validate_metrics(bad, 'download', 1, isolated=False, working=working, profile=profile)


class WorkflowSafetyTests(unittest.TestCase):
    def test_descriptor_owns_root_phase_profile_and_owner_token(self):
        with workflow_root() as root:
            descriptor, profile = runner.create_descriptor(root, 'download', 1, None)
            self.assertFalse(profile.exists())
            data = json.loads(descriptor.read_text())
            self.assertEqual(data, dict(schemaVersion=1, phase='download', root=str(root), previousProfile=None,
                                        expectedScale=1, ownerToken='a' * 32))
            profile.mkdir()
            restored, new_profile = runner.create_descriptor(root, 'restore', 1.5, profile)
            self.assertEqual(json.loads(restored.read_text())['previousProfile'], str(profile))
            self.assertFalse(new_profile.exists())
            with self.assertRaisesRegex(ValueError, 'preceding'):
                runner.create_descriptor(root, 'removed', 1, profile)
            with self.assertRaisesRegex(ValueError, 'nonexistent'):
                runner.create_descriptor(root, 'download', 1, None)

    def test_descriptor_rejects_unowned_roots_tokens_and_prior_profiles(self):
        with workflow_root() as root, tempfile.TemporaryDirectory() as outside:
            other = Path(outside).resolve()
            for phase, scale, previous in [('other', 1, None), ('download', True, None),
                                            ('download', 3, None), ('restore', 1, other), ('restore', 1, None)]:
                with self.subTest(phase=phase, scale=scale, previous=previous), self.assertRaises(ValueError):
                    runner.create_descriptor(root, phase, scale, previous)
            with self.assertRaisesRegex(ValueError, 'prefix'):
                runner.create_descriptor(other, 'download', 1, None)
            (root / '.workflow-owner').write_text('bad')
            with self.assertRaisesRegex(ValueError, 'token'):
                runner.create_descriptor(root, 'download', 1, None)

    @unittest.skipIf(os.name == 'nt', 'Symlink privilege differs on Windows')
    def test_no_symlink_or_parent_escape_in_paths_or_evidence(self):
        with workflow_root() as root, tempfile.TemporaryDirectory() as outside:
            other = Path(outside).resolve()
            (other / 'secret').write_text('not workflow evidence')
            for path in (other / 'secret', root / '..' / 'escape'):
                with self.assertRaises(ValueError):
                    runner.owned_path(path, root)
            (root / 'linked').symlink_to(other, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, 'symlinks'):
                runner.owned_path(root / 'linked' / 'secret', root, exists=True)
            profile = root / 'profile-download'
            profile.mkdir()
            (profile / 'apphost-workflow.json').symlink_to(other / 'secret')
            evidence = root / 'evidence'
            evidence.mkdir()
            with self.assertRaisesRegex(ValueError, 'symlinks'):
                runner.retain_evidence(profile, root, evidence)
            self.assertFalse((evidence / 'metrics.json').exists())

    def test_evidence_rechecks_root_ownership_before_copy(self):
        with workflow_root() as root:
            profile, evidence = root / 'profile-download', root / 'evidence'
            profile.mkdir()
            evidence.mkdir()
            (profile / 'apphost-workflow.json').write_text('{}')
            (root / '.workflow-owner').write_text('b' * 32)
            with self.assertRaisesRegex(ValueError, 'owner token'):
                runner.retain_evidence(profile, root, evidence, 'a' * 32)
            self.assertFalse((evidence / 'metrics.json').exists())

    def test_profile_and_private_desktop_environment_do_not_inherit_user_state(self):
        with workflow_root() as root:
            inherited = dict(MEOWCHAN_PROFILE='/user/meow', OCHAN_PROFILE='/user/old',
                             DISPLAY=':0', WAYLAND_DISPLAY='wayland-0', DBUS_SESSION_BUS_ADDRESS='user-bus')
            with mock.patch.dict(os.environ, inherited):
                cwd, env = runner.phase_environment(root, 'download', root / 'profile-download', True, 2)
            self.assertNotIn('OCHAN_PROFILE', env)
            for name in ('DISPLAY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS'):
                self.assertNotIn(name, env)
            self.assertEqual(env['MEOWCHAN_PROFILE'], str(root / 'profile-download'))
            self.assertEqual(env['AVALONIA_GLOBAL_SCALE_FACTOR'], '2')
            self.assertTrue((cwd / 'bus.conf').is_file())
            self.assertFalse((root / 'profile-download').exists())

    def test_evidence_limits_and_disk_reserve(self):
        with workflow_root() as root:
            profile, evidence = root / 'profile-download', root / 'evidence'
            profile.mkdir()
            evidence.mkdir()
            (profile / 'apphost-workflow.json').write_text('1234')
            with mock.patch.object(runner, 'MAX_METRICS_BYTES', 3), self.assertRaisesRegex(ValueError, 'bounded'):
                runner.retain_evidence(profile, root, evidence)
            with mock.patch.object(runner.shutil, 'disk_usage', return_value=mock.Mock(free=runner.RESERVE_BYTES - 1)):
                with self.assertRaisesRegex(ValueError, '2 GiB'):
                    runner.ensure_disk_reserve(root)
            with mock.patch.object(runner.shutil, 'disk_usage', return_value=mock.Mock(free=runner.RESERVE_BYTES + 3)):
                with self.assertRaisesRegex(ValueError, '2 GiB'):
                    runner.retain_evidence(profile, root, evidence)
            self.assertFalse((evidence / 'metrics.json').exists())

    def test_cli_requires_native_opt_in_and_has_bounded_deadlines(self):
        base = ['apphost', '--evidence-dir', 'evidence', '--source-commit', 'a' * 40, '--runtime', 'osx-x64']
        with mock.patch.object(runner.sys, 'platform', 'darwin'), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                runner.parse_args(base)
            args = runner.parse_args(base + ['--allow-native-desktop'])
            self.assertEqual(args.scales, [1])
            for value in ('nan', 'inf', '0', '-1', '121'):
                with self.subTest(timeout=value), self.assertRaises(SystemExit):
                    runner.parse_args(base + ['--allow-native-desktop', '--timeout', value])
            for extra in (['--archive', 'candidate.zip'], ['--archive-member', 'MeowChan.exe'],
                          ['--archive', 'candidate.zip', '--archive-member', '../MeowChan.exe'],
                          ['--archive-root', 'extracted']):
                with self.subTest(extra=extra), self.assertRaises(SystemExit):
                    runner.parse_args(base + ['--allow-native-desktop', *extra])
        with mock.patch.object(runner.sys, 'platform', 'linux'), mock.patch.object(runner.shutil, 'which', return_value='/tool'):
            self.assertEqual(runner.parse_args(base).scales, [1, 2])
            self.assertEqual(runner.parse_args(base + ['--scale', '1.5']).scales, [1.5])
        with mock.patch.object(runner.sys, 'platform', 'linux'), mock.patch.object(runner.shutil, 'which', return_value=None), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                runner.parse_args(base + ['--allow-native-desktop'])


class ArchiveBindingTests(unittest.TestCase):
    @staticmethod
    def create_archive(path, entries):
        if path.suffix == '.zip':
            with warnings.catch_warnings(), zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                warnings.simplefilter('ignore', UserWarning)
                for name, data in entries:
                    archive.writestr(name, data)
        else:
            with tarfile.open(path, 'w:gz') as archive:
                for name, data in entries:
                    member = tarfile.TarInfo(name)
                    member.size = len(data)
                    archive.addfile(member, io.BytesIO(data))

    def test_binds_actual_archive_and_member_bytes_for_supported_paths(self):
        payload = b'candidate executable bytes\x00'
        digest = hashlib.sha256(payload).hexdigest()
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            for extension, member, requested in [('.zip', 'MeowChan.exe', 'MeowChan.exe'),
                                                  ('.tar.gz', './MeowChan', 'MeowChan'),
                                                  ('.zip', 'MeowChan.app/Contents/MacOS/MeowChan', 'MeowChan.app/Contents/MacOS/MeowChan')]:
                with self.subTest(member=member):
                    path = base / ('candidate' + extension)
                    self.create_archive(path, [('before.txt', b'ignored before'), (member, payload), ('after.txt', b'ignored after')])
                    result = runner.archive_binding(path, requested, digest.upper())
                    self.assertEqual(result['sha256'], runner.sha256(path))
                    self.assertEqual(result['memberSha256'], digest)
                    self.assertEqual(result['memberBytes'], len(payload))
                    self.assertEqual(result['member'], requested)
                    self.assertEqual(set(base.iterdir()), {entry for entry in base.iterdir() if entry.name.startswith('candidate')})

    def test_rejects_missing_duplicate_and_mismatched_members_without_extracting(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            digest = hashlib.sha256(b'original').hexdigest()
            for extension in ('.zip', '.tar.gz'):
                for entries, expected in [([('other', b'original')], 'missing'),
                                          ([('MeowChan', b'modified')], 'differs'),
                                          ([('MeowChan', b'original'), ('./MeowChan', b'original')], 'Duplicate')]:
                    with self.subTest(extension=extension, expected=expected):
                        path = base / ('candidate' + extension)
                        self.create_archive(path, entries)
                        with self.assertRaisesRegex(ValueError, expected):
                            runner.archive_binding(path, 'MeowChan', digest)
                        self.assertFalse((base / 'MeowChan').exists())

    def test_rejects_oversized_members_and_path_escapes(self):
        for member in ('', '.', '/MeowChan', '../MeowChan', 'a/../MeowChan', 'C:/MeowChan.exe', './C:/MeowChan.exe', 'a\\MeowChan.exe'):
            with self.subTest(member=member), self.assertRaises(ValueError):
                runner.normalized_member(member)
        with tempfile.TemporaryDirectory() as temporary:
            for extension in ('.zip', '.tar.gz'):
                path = Path(temporary) / ('candidate' + extension)
                self.create_archive(path, [('MeowChan', b'1234')])
                with mock.patch.object(runner, 'MAX_ARCHIVE_MEMBER_BYTES', 3), self.assertRaisesRegex(ValueError, 'limit'):
                    runner.archive_binding(path, 'MeowChan', hashlib.sha256(b'1234').hexdigest())
        with self.assertRaisesRegex(ValueError, 'declared'):
            runner.member_sha256(io.BytesIO(b'1234'), 3)
        with self.assertRaisesRegex(ValueError, 'size mismatch'):
            runner.member_sha256(io.BytesIO(b'12'), 3)

    def test_rejects_link_members(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            zipped = base / 'candidate.zip'
            with zipfile.ZipFile(zipped, 'w') as archive:
                link = zipfile.ZipInfo('MeowChan')
                link.create_system = 3
                link.external_attr = (0o120777 << 16)
                archive.writestr(link, 'outside')
            tarred = base / 'candidate.tar.gz'
            with tarfile.open(tarred, 'w:gz') as archive:
                link = tarfile.TarInfo('MeowChan')
                link.type = tarfile.SYMTYPE
                link.linkname = '../outside'
                archive.addfile(link)
            for path in (zipped, tarred):
                with self.subTest(path=path), self.assertRaisesRegex(ValueError, 'regular file'):
                    runner.archive_binding(path, 'MeowChan', '0' * 64)


@unittest.skipIf(os.name == 'nt', 'macOS-style symlink fixtures need symlink privileges')
class ArchiveClosureTests(unittest.TestCase):
    @staticmethod
    def make_bundle(base):
        root = base / 'extracted'
        native = root / 'MeowChan.app/Contents/MacOS'
        payload = root / 'MeowChan.app/Contents/Resources/application'
        native.mkdir(parents=True)
        payload.mkdir(parents=True)
        app = native / 'MeowChan'
        app.write_bytes(b'thin native apphost unchanged')
        for name, data in [('MeowChan.dll', b'main managed implementation'), ('libmeowchan.dll', b'library implementation'),
                           ('MeowChan.deps.json', b'{"deps":true}'), ('MeowChan.runtimeconfig.json', b'{"runtime":true}')]:
            (payload / name).write_bytes(data)
            (native / name).symlink_to('../Resources/application/' + name)
        (payload / 'MeowChan').symlink_to('../../MacOS/MeowChan')
        (payload / 'docs').mkdir()
        (payload / 'docs/readme.txt').write_text('readme')
        (native / 'docs').symlink_to('../Resources/application/docs', target_is_directory=True)
        archive = base / 'candidate.zip'
        ArchiveClosureTests.write_bundle(root, archive)
        return root, app, payload, archive

    @staticmethod
    def write_bundle(root, archive):
        with zipfile.ZipFile(archive, 'w') as zipped:
            for path in sorted(root.rglob('*')):
                name = path.relative_to(root).as_posix()
                info = zipfile.ZipInfo(name + '/' if path.is_dir() and not path.is_symlink() else name)
                info.create_system = 3
                if path.is_symlink():
                    info.external_attr = 0o120777 << 16
                    zipped.writestr(info, os.readlink(path))
                elif path.is_dir():
                    info.external_attr = 0o40755 << 16
                    zipped.writestr(info, b'')
                else:
                    info.external_attr = 0o100644 << 16
                    zipped.writestr(info, path.read_bytes())

    @staticmethod
    def closure(archive, root, app):
        return runner.archive_closure(archive, root, 'MeowChan.app/Contents/MacOS/MeowChan', app)

    def test_full_managed_payload_and_confined_bundle_symlinks_are_bound(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, app, payload, archive = self.make_bundle(Path(temporary))
            closure = self.closure(archive, root, app)
            self.assertTrue(closure['verifiedBeforeRun'])
            self.assertFalse(closure['verifiedAfterRun'])
            self.assertEqual(closure['contentSha256'], runner.archive_entries(archive)['contentSha256'])
            self.assertEqual(runner.verify_extracted_closure(root, closure), closure['contentSha256'])
            self.assertEqual(closure['fileCount'], 6)
            rows = {entry['path']: entry for entry in closure['entries']}
            self.assertEqual(rows['MeowChan.app/Contents/Resources/application/MeowChan']['target'], '../../MacOS/MeowChan')
            self.assertEqual(rows['MeowChan.app/Contents/MacOS/docs']['kind'], 'symlink')
            self.assertEqual(rows['MeowChan.app/Contents/Resources/application/MeowChan.dll']['sha256'], runner.sha256(payload / 'MeowChan.dll'))

    def test_streamed_tar_closure_matches_zip_and_normalizes_dot_prefix(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root, app, payload, archive = self.make_bundle(base)
            expected = self.closure(archive, root, app)
            tarred = base / 'candidate.tar.gz'
            with tarfile.open(tarred, 'w:gz') as output:
                output.add(root, arcname='.')
            actual = self.closure(tarred, root, app)
            self.assertEqual(actual['entries'], expected['entries'])
            self.assertEqual(actual['contentSha256'], expected['contentSha256'])

    def test_unchanged_thin_apphost_cannot_hide_modified_managed_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, app, payload, archive = self.make_bundle(Path(temporary))
            original_app_hash = runner.sha256(app)
            before = self.closure(archive, root, app)
            (payload / 'MeowChan.dll').write_bytes(b'modified managed behavior')
            self.assertEqual(runner.sha256(app), original_app_hash)
            runner.archive_binding(archive, 'MeowChan.app/Contents/MacOS/MeowChan', original_app_hash)
            with self.assertRaisesRegex(ValueError, 'differs'):
                self.closure(archive, root, app)
            with self.assertRaisesRegex(ValueError, 'differs'):
                runner.verify_extracted_closure(root, before)

    def test_broken_external_changed_and_unexpected_links_are_rejected(self):
        for target, expected in [('missing', 'broken'), ('../../../../outside', 'escapes'),
                                 ('../Resources/application/libmeowchan.dll', 'differs')]:
            with self.subTest(target=target), tempfile.TemporaryDirectory() as temporary:
                base = Path(temporary)
                root, app, payload, archive = self.make_bundle(base)
                before = self.closure(archive, root, app)
                (base / 'outside').write_bytes(b'outside')
                link = app.parent / 'MeowChan.dll'
                link.unlink()
                link.symlink_to(target)
                with self.assertRaisesRegex(ValueError, expected):
                    runner.verify_extracted_closure(root, before)
        with tempfile.TemporaryDirectory() as temporary:
            root, app, payload, archive = self.make_bundle(Path(temporary))
            before = self.closure(archive, root, app)
            (root / 'unexpected').symlink_to(app.relative_to(root))
            with self.assertRaisesRegex(ValueError, 'Unexpected'):
                runner.verify_extracted_closure(root, before)

    def test_missing_required_managed_runtime_file_is_rejected_even_when_archive_matches(self):
        for name in ('MeowChan.deps.json', 'MeowChan.runtimeconfig.json', 'libmeowchan.dll'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root, app, payload, archive = self.make_bundle(Path(temporary))
                (app.parent / name).unlink()
                (payload / name).unlink()
                self.write_bundle(root, archive)
                with self.assertRaises(ValueError):
                    self.closure(archive, root, app)

    def test_loaded_managed_assembly_locations_and_hashes_must_match_closure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, app, payload, archive = self.make_bundle(Path(temporary))
            closure = self.closure(archive, root, app)
            evidence = {'appBaseDirectory': str(app.parent), 'managedAssemblies': [
                {'name': name, 'path': str(app.parent / (name + '.dll')), 'sha256': runner.sha256(payload / (name + '.dll')).upper()}
                for name in ('MeowChan', 'libmeowchan')]}
            bindings = runner.validate_loaded_assemblies(evidence, closure)
            self.assertEqual({row['name'] for row in bindings}, {'MeowChan', 'libmeowchan'})
            self.assertTrue(all('/Resources/application/' in row['archivePath'] for row in bindings))
            for mutation in ('missing', 'hash', 'outside', 'base', 'wrong-name'):
                bad = json.loads(json.dumps(evidence))
                if mutation == 'missing':
                    bad['managedAssemblies'].pop()
                elif mutation == 'hash':
                    bad['managedAssemblies'][0]['sha256'] = '0' * 64
                elif mutation == 'outside':
                    bad['managedAssemblies'][0]['path'] = str(root.parent / 'outside.dll')
                elif mutation == 'base':
                    bad['appBaseDirectory'] = str(root.parent)
                else:
                    bad['managedAssemblies'][0]['name'] = 'libmeowchan'
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    runner.validate_loaded_assemblies(bad, closure)

    def test_archive_paths_duplicates_and_closure_limits_are_enforced(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            for name in ('/evil', '../evil', 'a/../evil', 'a\\evil'):
                archive = base / 'unsafe.zip'
                ArchiveBindingTests.create_archive(archive, [('MeowChan', b'app'), (name, b'bad')])
                with self.subTest(name=name), self.assertRaises(ValueError):
                    runner.archive_entries(archive)
            archive = base / 'duplicate.zip'
            ArchiveBindingTests.create_archive(archive, [('MeowChan', b'app'), ('./MeowChan', b'app')])
            with self.assertRaisesRegex(ValueError, 'Duplicate'):
                runner.archive_entries(archive)
            root, app, payload, archive = self.make_bundle(base)
            for limit, value in [('MAX_CLOSURE_ENTRIES', 2), ('MAX_CLOSURE_BYTES', 3), ('MAX_CLOSURE_FILE_BYTES', 3)]:
                with self.subTest(limit=limit), mock.patch.object(runner, limit, value), self.assertRaisesRegex(ValueError, 'limit'):
                    runner.archive_entries(archive)

    def test_appledouble_metadata_is_separately_hashed_and_cannot_exclude_payload(self):
        for name, executable, allowed in [('__MACOSX/MeowChan.app/Contents/MacOS/._MeowChan', False, True),
                                          ('__MACOSX/hidden.dll', False, False),
                                          ('__MACOSX/._unknown', False, False),
                                          ('__MACOSX/MeowChan.app/Contents/MacOS/._MeowChan', True, True)]:
            with self.subTest(name=name, executable=executable), tempfile.TemporaryDirectory() as temporary:
                root, app, payload, archive = self.make_bundle(Path(temporary))
                with zipfile.ZipFile(archive, 'a') as zipped:
                    entry = zipfile.ZipInfo(name)
                    entry.create_system = 3
                    entry.external_attr = (0o100755 if executable else 0o100644) << 16
                    zipped.writestr(entry, b'AppleDouble metadata envelope')
                if allowed:
                    closure = self.closure(archive, root, app)
                    files = [entry for entry in closure['archiveMetadataEntries'] if entry['kind'] == 'file']
                    self.assertEqual(len(files), 1)
                    self.assertEqual(files[0]['sha256'], hashlib.sha256(b'AppleDouble metadata envelope').hexdigest())
                    self.assertFalse(any(row['path'].startswith('__MACOSX') for row in closure['entries']))
                else:
                    with self.assertRaises(ValueError):
                        self.closure(archive, root, app)


@unittest.skipIf(os.name == 'nt', 'POSIX executable fixtures; no actual GUI or packaged apphost')
class WorkflowProcessTests(unittest.TestCase):
    def test_closure_bound_workflow_verifies_loaded_assemblies_in_every_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root, app, payload, archive = ArchiveClosureTests.make_bundle(base)
            fake = fake_apphost(base)
            assemblies = [{'name': name, 'path': str(app.parent / (name + '.dll')), 'sha256': runner.sha256(payload / (name + '.dll'))}
                          for name in ('MeowChan', 'libmeowchan')]
            marker = "(profile / 'apphost-workflow.json').write_text(json.dumps(m))"
            script = fake.read_text().replace(marker, f"m['appBaseDirectory'] = {str(app.parent)!r}\nm['managedAssemblies'] = {assemblies!r}\n" + marker)
            app.write_text(script)
            app.chmod(0o755)
            ArchiveClosureTests.write_bundle(root, archive)
            args = SimpleNamespace(apphost=app, evidence_dir=base / 'evidence', source_commit='a' * 40,
                                   runtime='linux-x64', isolated=False, allow_native_desktop=True, timeout=5,
                                   scales=[1], archive=archive, archive_member='MeowChan.app/Contents/MacOS/MeowChan', archive_root=root)
            with mock.patch.object(runner, 'parse_args', return_value=args), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 0)
            report = json.loads((args.evidence_dir / 'workflow.json').read_text())
            closure = report['archiveClosure']
            self.assertTrue(closure['verifiedBeforeRun'])
            self.assertTrue(closure['verifiedAfterRun'])
            self.assertEqual(closure['afterContentSha256'], closure['contentSha256'])
            for phase in report['scales'][0]['phases']:
                self.assertTrue(phase['managedAssemblyBindingVerified'])
                self.assertEqual({item['name'] for item in phase['managedAssemblyBindings']}, {'MeowChan', 'libmeowchan'})

    def test_managed_payload_mutation_during_workflow_fails_final_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root, app, payload, archive = ArchiveClosureTests.make_bundle(base)
            args = SimpleNamespace(apphost=app, evidence_dir=base / 'evidence', source_commit='a' * 40,
                                   runtime='osx-x64', isolated=False, allow_native_desktop=True, timeout=5,
                                   scales=[1], archive=archive, archive_member='MeowChan.app/Contents/MacOS/MeowChan', archive_root=root)
            def mutate(*unused):
                (payload / 'MeowChan.dll').write_bytes(b'replaced after initial verification')
                return {'scale': 1, 'passed': True, 'phases': []}
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'run_scale', side_effect=mutate), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 1)
            report = json.loads((args.evidence_dir / 'workflow.json').read_text())
            self.assertFalse(report['passed'])
            self.assertFalse(report['archiveClosure']['verifiedAfterRun'])
            self.assertIn('changed during workflow', report['error'])

    def test_final_report_binds_apphost_hash_declared_provenance_and_fresh_cwds(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base)
            args = SimpleNamespace(apphost=app, evidence_dir=base / 'evidence', source_commit='A' * 40,
                                   runtime='linux-x64', isolated=False, allow_native_desktop=True,
                                   timeout=5, scales=[1], archive=None, archive_member=None, archive_root=None)
            with mock.patch.object(runner, 'parse_args', return_value=args), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 0)
            report = json.loads((base / 'evidence/workflow.json').read_text())
            self.assertEqual(report['apphostSha256'], runner.sha256(app))
            self.assertEqual(report['declaredSourceCommit'], 'a' * 40)
            self.assertEqual(report['declaredRuntime'], 'linux-x64')
            self.assertEqual(report['minimumDiskReserveBytes'], 2 * 1024 ** 3)
            self.assertFalse(report['isolatedDesktop'])
            self.assertTrue(report['nativeDesktopOptIn'])
            self.assertIn('Declared provenance', report['scope'])
            self.assertEqual(len({item['workingDirectory'] for item in report['scales'][0]['phases']}), 4)
            with mock.patch.object(runner, 'parse_args', return_value=args), self.assertRaises(FileExistsError):
                runner.main()

    def test_archive_binding_is_written_before_any_process_and_mismatch_prevents_runs(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base)
            archive = base / 'candidate.zip'
            ArchiveBindingTests.create_archive(archive, [('MeowChan', app.read_bytes())])
            args = SimpleNamespace(apphost=app, evidence_dir=base / 'evidence', source_commit='a' * 40,
                                   runtime='linux-x64', isolated=False, allow_native_desktop=True,
                                   timeout=5, scales=[1], archive=archive, archive_member='MeowChan', archive_root=None)
            def verify_before_run(*unused):
                report = json.loads((args.evidence_dir / 'workflow.json').read_text())
                self.assertFalse(report['completed'])
                self.assertFalse(report['passed'])
                self.assertEqual(report['archiveBinding']['memberSha256'], report['apphostSha256'])
                self.assertEqual(report['archiveBinding']['sha256'], runner.sha256(archive))
                return {'scale': 1, 'passed': True, 'phases': []}
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'run_scale', side_effect=verify_before_run) as run, contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 0)
                run.assert_called_once()
            args.evidence_dir = base / 'mismatch-evidence'
            ArchiveBindingTests.create_archive(archive, [('MeowChan', b'wrong apphost')])
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'run_scale') as run:
                with self.assertRaisesRegex(ValueError, 'differs'):
                    runner.main()
                run.assert_not_called()

    def test_phases_use_fresh_processes_and_keep_prior_profiles_until_sequence_ends(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base)
            result = runner.run_scale(app, 2, base / 'evidence', 5, False)
            self.assertTrue(result['passed'], result)
            phases = result['phases']
            self.assertEqual([item['phase'] for item in phases], list(runner.PHASES))
            self.assertEqual(len({item['productProcessId'] for item in phases}), 4)
            for item in phases:
                self.assertEqual(item['renderScaling'], 2)
                self.assertFalse(Path(item['profile']).parent.exists())
                location = base / 'evidence' / item['phase']
                self.assertTrue((location / 'descriptor.json').is_file())
                self.assertTrue((location / 'metrics.json').is_file())
                self.assertEqual(item['screenshots']['apphost-workflow-frame.png']['width'], 1)

    def test_timeout_retains_evidence_cleans_process_and_skips_dependent_phases(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base, 'timeout')
            result = runner.run_scale(app, 1, base / 'evidence', .4, False)
            first = result['phases'][0]
            self.assertFalse(result['passed'])
            self.assertTrue(first['timedOut'])
            self.assertTrue((base / 'evidence/download/metrics.json').is_file())
            self.assertTrue((base / 'evidence/download/apphost-workflow-frame.png').is_file())
            with self.assertRaises(ProcessLookupError):
                os.kill(first['runnerProcessId'], 0)
            self.assertFalse(Path(first['profile']).parent.exists())
            self.assertTrue(all(item['skipped'] for item in result['phases'][1:]))

    def test_marker_metrics_and_successful_exit_are_all_required(self):
        for behavior in ('no-marker', 'wrong-phase', 'nonzero', 'no-screenshots'):
            with self.subTest(behavior=behavior), tempfile.TemporaryDirectory() as temporary, workflow_root() as root:
                base = Path(temporary)
                app = fake_apphost(base, behavior)
                result = runner.run_phase(app, root, 'download', 1, None, base / 'evidence', 5, False)
                self.assertFalse(result['passed'])
                self.assertIn('error', result)

    def test_disk_reserve_failure_stops_owned_child(self):
        with tempfile.TemporaryDirectory() as temporary, workflow_root() as root:
            base = Path(temporary)
            app = fake_apphost(base, 'timeout')
            with mock.patch.object(runner, 'ensure_disk_reserve', side_effect=[None, ValueError('2 GiB reserve')]):
                result = runner.run_phase(app, root, 'download', 1, None, base / 'evidence', 5, False)
            self.assertFalse(result['passed'])
            self.assertIn('2 GiB', result['error'])
            with self.assertRaises(ProcessLookupError):
                os.kill(result['runnerProcessId'], 0)


if __name__ == '__main__':
    unittest.main()
