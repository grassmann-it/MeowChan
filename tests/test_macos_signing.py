"""Portable failure/closure contracts; native codesign fixture is in packaging tests."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('macos_signing', Path(__file__).resolve().parents[1] / 'scripts/macos-signing.py')
signing = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(signing)


class SigningContracts(unittest.TestCase):
    def test_load_commands_omit_id_and_preserve_spaces(self):
        deps, paths = signing.load_commands('''Load command 0
          cmd LC_ID_DYLIB
          name @rpath/identity.dylib (offset 24)
        Load command 1
          cmd LC_LOAD_DYLIB
          name @loader_path/a space.dylib (offset 24)
        Load command 2
          cmd LC_RPATH
          path @executable_path/native dir (offset 12)
        ''')
        self.assertEqual(['@loader_path/a space.dylib'], deps)
        self.assertEqual(['@executable_path/native dir'], paths)

    def test_system_allowlist_is_narrow(self):
        self.assertTrue(signing.system_dependency('/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit'))
        for value in ('/opt/homebrew/lib/a.dylib', '/usr/local/lib/a.dylib', '/usr/lib/../../tmp/a.dylib', '@rpath/libSystem.dylib'):
            self.assertFalse(signing.system_dependency(value))

    def test_inventory_rejects_external_and_dangling_symlinks(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            apphost = root / 'Contents/MacOS/MeowChan'
            apphost.parent.mkdir(parents=True)
            apphost.write_bytes(bytes.fromhex('cffaedfe') + b'fixture')
            link = root / 'unsafe'
            try:
                link.symlink_to('../outside')
            except OSError:
                self.skipTest('Symlinks unavailable')
            with self.assertRaises((ValueError, OSError)):
                signing.inventory(root)
            link.unlink()
            link.symlink_to(apphost)
            with self.assertRaisesRegex(ValueError, 'Unsafe'):
                signing.inventory(root)

    def test_executable_path_uses_canonical_main_and_internal_resource_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            apphost = root / 'Contents/MacOS/MeowChan'
            library = root / 'Contents/Resources/application/native.dylib'
            for path in (apphost, library):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(bytes.fromhex('cffaedfe'))
            link = apphost.parent / 'native.dylib'
            try:
                link.symlink_to('../Resources/application/native.dylib')
            except OSError:
                self.skipTest('Symlinks unavailable')
            def commands(*args):
                if '-archs' in args:
                    return 'arm64'
                return 'cmd LC_LOAD_DYLIB\nname @executable_path/native.dylib (offset 24)' if args[-1] == apphost else ''
            with mock.patch.object(signing.sys, 'platform', 'darwin'), mock.patch.object(signing.platform, 'machine', return_value='arm64'), mock.patch.object(signing, 'run', side_effect=commands):
                result = signing.validate(root, 'osx-arm64', signatures=False)
                self.assertEqual('Contents/MacOS/MeowChan', result['apphost'])
                self.assertEqual(2, result['macho_files'])
                link.unlink()
                with self.assertRaisesRegex(ValueError, 'Unresolved'):
                    signing.validate(root, 'osx-arm64', signatures=False)

    def test_apphost_sealing_waits_for_sibling_and_nested_native_code(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            apphost = root / 'Contents/MacOS/MeowChan'
            sibling = apphost.parent / 'libAvaloniaNative.dylib'
            framework = apphost.parent / 'Nested.framework'
            nested = framework / 'Nested'
            nested.parent.mkdir(parents=True)
            for path in (apphost, sibling, nested):
                path.write_bytes(bytes.fromhex('cffaedfe'))
            signed = set()
            def command(*args):
                if '--sign' in args:
                    target = args[-1]
                    if target == apphost:
                        # Apple's signer can inspect the enclosing bundle while
                        # sealing its main executable; unsealed siblings fail.
                        self.assertTrue({sibling, nested, framework}.issubset(signed))
                    signed.add(target)
                return ''
            with mock.patch.object(signing.sys, 'platform', 'darwin'), mock.patch.object(signing, 'repair_vlc_references'), mock.patch.object(signing, 'validate', return_value={'passed': True}), mock.patch.object(signing, 'run', side_effect=command):
                self.assertTrue(signing.sign(root, 'osx-arm64')['passed'])
            self.assertIn(root, signed)

    def test_closure_rejects_external_dependency_and_wrong_architecture(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            apphost = root / 'Contents/MacOS/MeowChan'
            apphost.parent.mkdir(parents=True)
            apphost.write_bytes(bytes.fromhex('cffaedfe'))
            def commands(*args):
                return 'arm64' if '-archs' in args else 'cmd LC_LOAD_DYLIB\nname /opt/homebrew/lib/foreign.dylib (offset 24)'
            with mock.patch.object(signing.sys, 'platform', 'darwin'), mock.patch.object(signing.platform, 'machine', return_value='arm64'), mock.patch.object(signing, 'run', side_effect=commands):
                with self.assertRaisesRegex(ValueError, 'Non-relocatable'):
                    signing.validate(root, 'osx-arm64', signatures=False)
            with mock.patch.object(signing.sys, 'platform', 'darwin'), mock.patch.object(signing.platform, 'machine', return_value='arm64'), mock.patch.object(signing, 'run', return_value='x86_64'):
                with self.assertRaisesRegex(ValueError, 'Architecture mismatch'):
                    signing.validate(root, 'osx-arm64', signatures=False)

    def test_closure_resolves_loader_path_and_rejects_missing_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            apphost = root / 'Contents/MacOS/MeowChan'
            apphost.parent.mkdir(parents=True)
            library = apphost.parent / 'native.dylib'
            for p in (apphost, library):
                p.write_bytes(bytes.fromhex('cffaedfe'))
            def commands(*args):
                if '-archs' in args:
                    return 'arm64'
                return 'cmd LC_LOAD_DYLIB\nname @loader_path/native.dylib (offset 24)' if args[-1] == apphost else ''
            with mock.patch.object(signing.sys, 'platform', 'darwin'), mock.patch.object(signing.platform, 'machine', return_value='arm64'), mock.patch.object(signing, 'run', side_effect=commands):
                self.assertTrue(signing.validate(root, 'osx-arm64', signatures=False)['dependency_closure'])
                library.unlink()
                with self.assertRaisesRegex(ValueError, 'Unresolved'):
                    signing.validate(root, 'osx-arm64', signatures=False)


if __name__ == '__main__':
    unittest.main()
