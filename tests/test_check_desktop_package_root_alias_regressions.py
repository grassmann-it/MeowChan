"""Compose the macOS parent-alias fix with real desktop layout/hash guards.

Only synthetic files and symlinks are used, including the macOS bundle layout (no
native executable is run). Existing plain-root cases live in tests/. Run with
TMPDIR pointing at a private external evidence directory; no package is built.
"""
import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('desktop_alias_fixtures', ROOT / 'tests/test_desktop_package.py')
FIXTURES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURES)
PACKAGE = FIXTURES.package


def snapshot(root):
    return {path.relative_to(root).as_posix():
            ('link', os.readlink(path)) if path.is_symlink() else
            ('file', hashlib.sha256(path.read_bytes()).hexdigest())
            for path in root.rglob('*') if path.is_symlink() or path.is_file()}


@unittest.skipIf(os.name == 'nt', 'Synthetic POSIX parent aliases; not a native macOS test')
class DesktopRootAliasCompositionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='f04-desktop-')
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name).resolve()
        self.physical = self.base / 'private/tmp'
        self.physical.mkdir(parents=True)
        self.alias = self.base / 'tmp'
        self.alias.symlink_to(self.physical, target_is_directory=True)

    def allow(self, extra=None):
        aliases = {str(self.alias): str(self.physical)}
        aliases.update(extra or {})
        return mock.patch.object(PACKAGE, 'SYSTEM_PARENT_ALIASES', aliases)

    def test_system_parent_alias_composes_with_the_macos_bundle_alias_view(self):
        for runtime in ('osx-x64', 'osx-arm64'):
            with self.subTest(runtime=runtime):
                contents = self.physical / runtime / 'MeowChan.app/Contents'
                application = FIXTURES.payload(contents / 'Resources/application', runtime)
                macos = contents / 'MacOS'
                macos.mkdir()
                (application / 'MeowChan').rename(macos / 'MeowChan')
                (application / 'MeowChan').symlink_to('../../MacOS/MeowChan')
                for entry in application.iterdir():
                    if entry.name != 'MeowChan':
                        (macos / entry.name).symlink_to(Path('../Resources/application') / entry.name,
                                                      target_is_directory=entry.is_dir())
                alias_root = self.alias / macos.relative_to(self.physical)
                before = snapshot(contents)
                with self.allow():
                    report = PACKAGE.validate(alias_root, runtime)
                self.assertEqual(str(macos), report['root'])
                self.assertEqual(len(PACKAGE.LEGAL_DOCS), report['documentation_files'])
                self.assertEqual(snapshot(contents), before)
                # The two kinds of trusted alias must not hide extra documentation.
                (application / 'docs/unlisted.md').write_bytes(b'synthetic extra document')
                with self.allow(), self.assertRaisesRegex(ValueError, 'only the approved legal files'):
                    PACKAGE.validate(alias_root, runtime)

    def test_bundled_notice_hash_and_inventory_are_rechecked_through_the_alias(self):
        # Reuse the existing data factory, not its plain-root tests.
        root = FIXTURES.DesktopPackageTests('runTest').bundled_linux(self.physical / 'publish')
        alias_root = self.alias / 'publish'
        notice = root / 'linux-video-licenses/copyright/libvlc5'
        original, metadata = notice.read_bytes(), notice.stat()
        with self.allow():
            self.assertEqual(2, PACKAGE.validate(alias_root, 'linux-x64')['linux_license_files'])
            # Same size and timestamps: only rehashing the current bytes catches this.
            notice.write_bytes(bytes([original[0] ^ 1]) + original[1:])
            os.utime(notice, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
            with self.assertRaisesRegex(ValueError, 'checksum differs'):
                PACKAGE.validate(alias_root, 'linux-x64')
            notice.write_bytes(original)
            extra = root / 'linux-video-licenses/copyright/unlisted.log'
            extra.write_bytes(b'not exempt from the legal inventory')
            with self.assertRaisesRegex(ValueError, 'differ from their inventory'):
                PACKAGE.validate(alias_root, 'linux-x64')
            extra.unlink()
            self.assertEqual(2, PACKAGE.validate(alias_root, 'linux-x64')['linux_license_files'])

    def test_parent_alias_allowlist_is_never_applied_to_nested_payload_links(self):
        root = FIXTURES.payload(self.physical / 'publish')
        notice = root / PACKAGE.LEGAL_FILES[2]
        target = self.base / 'external-notice'
        target.write_bytes(notice.read_bytes())
        notice.unlink()
        notice.symlink_to(target)
        with self.allow({str(notice): str(target)}), self.assertRaisesRegex(ValueError, 'Nested.*symlink'):
            PACKAGE.validate(self.alias / 'publish', 'linux-x64')

    def test_successful_scan_does_not_cache_a_parent_alias_after_retargeting(self):
        FIXTURES.payload(self.physical / 'publish')
        other = self.base / 'other'
        FIXTURES.payload(other / 'publish')
        with self.allow():
            PACKAGE.validate(self.alias / 'publish', 'linux-x64')
            self.alias.unlink()
            self.alias.symlink_to(other, target_is_directory=True)
            # Fail at root validation, before inspecting the redirected payload.
            with mock.patch.object(PACKAGE, 'require_file', side_effect=AssertionError('payload was read')), \
                    self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
                PACKAGE.validate(self.alias / 'publish', 'linux-x64')


if __name__ == '__main__':
    unittest.main()
