"""Synthetic privacy regressions for desktop payload aliases; no package is built."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('privacy_desktop_fixtures', ROOT / 'tests/test_desktop_package.py')
FIXTURES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURES)
PACKAGE = FIXTURES.package


class PublicPackageAliasPrivacyTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='public-alias-privacy-')
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name).resolve()

    def bundle(self):
        contents = self.base / 'MeowChan.app/Contents'
        application = FIXTURES.payload(contents / 'Resources/application', 'osx-arm64')
        macos = contents / 'MacOS'
        macos.mkdir()
        (application / 'MeowChan').rename(macos / 'MeowChan')
        (application / 'MeowChan').symlink_to('../../MacOS/MeowChan')
        for entry in application.iterdir():
            if entry.name != 'MeowChan':
                (macos / entry.name).symlink_to(Path('../Resources/application') / entry.name,
                                              target_is_directory=entry.is_dir())
        return application, macos

    def test_generated_macos_alias_view_and_physical_payload_remain_valid(self):
        application, macos = self.bundle()
        before = {path: os.readlink(path) for path in macos.iterdir() if path.is_symlink()}
        for root in (macos, application):
            self.assertEqual(len(PACKAGE.LEGAL_DOCS),
                             PACKAGE.validate(root, 'osx-arm64')['documentation_files'])
        self.assertEqual(before, {path: os.readlink(path) for path in before})

    def test_flat_publish_cannot_alias_an_external_source_or_dll_tree(self):
        for name in ('privatePlugins', 'extra.dll', 'extra.cs', 'absent'):
            with self.subTest(name=name):
                root = FIXTURES.payload(self.base / name)
                external = self.base / ('external-' + name)
                if name == 'privatePlugins':
                    external.mkdir()
                    (external / 'SyntheticPrivate.cs').write_bytes(b'// synthetic only')
                elif name != 'absent':
                    external.write_bytes(b'synthetic only')
                (root / name).symlink_to(external, target_is_directory=name == 'privatePlugins')
                with self.assertRaisesRegex(ValueError, 'symlink'):
                    PACKAGE.validate_payload(root)

    def test_macos_alias_cannot_be_retargeted_outside_the_bundle(self):
        application, macos = self.bundle()
        external = FIXTURES.payload(self.base / 'external')
        link = macos / 'Assets'
        link.unlink()
        link.symlink_to(external / 'Assets', target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            PACKAGE.validate_payload(macos)
        self.assertTrue((application / 'Assets/meowchan.png').is_file())

    def test_macos_resource_parent_cannot_redirect_to_an_external_tree(self):
        application, macos = self.bundle()
        resources = application.parent
        retained = self.base / 'external-resources'
        resources.rename(retained)
        resources.symlink_to(retained, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            PACKAGE.validate_payload(macos)

    def test_nested_links_below_a_legitimate_directory_alias_are_rejected(self):
        application, macos = self.bundle()
        external = self.base / 'external.dll'
        external.write_bytes(b'synthetic only')
        for parent in ('Assets', 'plugins/assemblies'):
            with self.subTest(parent=parent):
                directory = application / parent
                directory.mkdir(parents=True, exist_ok=True)
                if parent.startswith('plugins') and not (macos / 'plugins').exists():
                    (macos / 'plugins').symlink_to('../Resources/application/plugins', target_is_directory=True)
                link = directory / 'unlisted.dll'
                link.symlink_to(external)
                with self.assertRaisesRegex(ValueError, 'Nested.*symlink'):
                    PACKAGE.validate_payload(macos)
                link.unlink()

    def test_build_and_profile_components_below_aliases_are_not_invisible(self):
        application, macos = self.bundle()
        for relative in ('Assets/profiles/local/state.json', 'Assets/obj/private.dll', 'Assets/.git/config'):
            with self.subTest(relative=relative):
                path = application / relative
                path.parent.mkdir(parents=True)
                path.write_bytes(b'synthetic only')
                with self.assertRaisesRegex(ValueError, 'artifact'):
                    PACKAGE.validate_payload(macos)
                path.unlink()
                # Remove empty forbidden directories before the next independent case.
                while path.parent != application / 'Assets':
                    path = path.parent
                    path.rmdir()

    def test_hardlinks_below_directory_aliases_are_rejected(self):
        application, macos = self.bundle()
        external = self.base / 'external.dll'
        external.write_bytes(b'synthetic only')
        os.link(external, application / 'Assets/unlisted.dll')
        with self.assertRaisesRegex(ValueError, 'hardlink'):
            PACKAGE.validate_payload(macos)

    def test_physical_apphost_backlink_cannot_escape_the_bundle(self):
        application, _macos = self.bundle()
        external = self.base / 'external-apphost'
        external.write_bytes(b'synthetic only')
        link = application / 'MeowChan'
        link.unlink()
        link.symlink_to(external)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            PACKAGE.validate_payload(application)


if __name__ == '__main__':
    unittest.main()
