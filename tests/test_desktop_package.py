"""Read-only desktop distribution inventory checks; no publish/build is performed."""
import importlib.util
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('desktop_package', ROOT / 'scripts/check-desktop-package.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def payload(root, runtime='linux-x64'):
    root.mkdir(parents=True)
    for relative in (*package.LEGAL_FILES, 'Assets/meowchan.png'):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'retained ' + relative.encode('utf-8'))
    (root / ('MeowChan.exe' if runtime.startswith('win-') else 'MeowChan')).write_bytes(b'apphost')
    return root


class DesktopPackageTests(unittest.TestCase):
    def bundled_linux(self, root):
        payload(root)
        marker = {'SchemaVersion': 1, 'RuntimeIdentifier': 'linux-x64', 'Mode': 'bundled', 'Files': {}}
        (root / 'linux-video-runtime.json').write_text(json.dumps(marker))
        files = {}
        for name in ('copyright/libvlc5', 'AppImage-runtime-LICENSE'):
            path = root / 'linux-video-licenses' / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'unchanged upstream legal text')
            files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
        (root / 'linux-video-runtime-inventory.json').write_text(json.dumps({'license_files': files}))
        return root

    def test_bundled_linux_notices_are_hash_bound_outside_the_six_document_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundled_linux(Path(temporary) / 'publish')
            result = package.validate(root, 'linux-x64')
            self.assertEqual(result['linux_video_mode'], 'bundled')
            self.assertEqual(result['linux_license_files'], 2)
            self.assertEqual(result['documentation_files'], len(package.LEGAL_DOCS))
            for mutation in ('tampered', 'missing', 'extra', 'escape'):
                with self.subTest(mutation=mutation):
                    changed = self.bundled_linux(Path(temporary) / mutation)
                    notice = changed / 'linux-video-licenses/copyright/libvlc5'
                    if mutation == 'tampered':
                        notice.write_bytes(b'modified')
                    elif mutation == 'missing':
                        notice.unlink()
                    elif mutation == 'extra':
                        (changed / 'linux-video-licenses/unlisted').write_bytes(b'unlisted')
                    else:
                        (changed / 'linux-video-runtime-inventory.json').write_text(json.dumps({'license_files': {'../LICENSE': '0' * 64}}))
                    with self.assertRaises(ValueError):
                        package.validate(changed, 'linux-x64')

    def test_system_linux_has_no_private_runtime_notices_and_marker_matches_runtime(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = payload(Path(temporary) / 'system')
            marker = {'SchemaVersion': 1, 'RuntimeIdentifier': 'linux-x64', 'Mode': 'system'}
            path = root / 'linux-video-runtime.json'
            path.write_text(json.dumps(marker))
            self.assertEqual(package.validate(root, 'linux-x64')['linux_video_mode'], 'system')
            for field, value in (('RuntimeIdentifier', 'linux-arm64'), ('Mode', 'unexpected'), ('SchemaVersion', True)):
                path.write_text(json.dumps(dict(marker, **{field: value})))
                with self.subTest(field=field), self.assertRaises(ValueError):
                    package.validate(root, 'linux-x64')
            path.write_text(json.dumps(marker))
            (root / 'linux-video-licenses').mkdir()
            with self.assertRaisesRegex(ValueError, 'unexpectedly'):
                package.validate(root, 'linux-x64')
            path.unlink()
            with self.assertRaisesRegex(ValueError, 'require package metadata'):
                package.validate(root, 'linux-x64')

    @unittest.skipIf(os.name == 'nt', 'POSIX legal-link fixture')
    def test_linux_license_symlinks_are_rejected_without_following_external_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundled_linux(Path(temporary) / 'publish')
            notice = root / 'linux-video-licenses/copyright/libvlc5'
            notice.unlink()
            notice.symlink_to(root / 'LICENSE')
            with self.assertRaisesRegex(ValueError, 'symlink'):
                package.validate(root, 'linux-x64')

    @unittest.skipIf(os.name == 'nt', 'POSIX dangling-link fixture')
    def test_system_linux_rejects_dangling_bundled_notice_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            for mode in ('source', 'system'):
                for name in ('linux-video-runtime-inventory.json', 'linux-video-licenses'):
                    with self.subTest(mode=mode, name=name):
                        root = payload(Path(temporary) / mode / name)
                        if mode == 'system':
                            (root / 'linux-video-runtime.json').write_text(json.dumps({
                                'SchemaVersion': 1, 'RuntimeIdentifier': 'linux-x64', 'Mode': 'system'}))
                        (root / name).symlink_to(root / 'absent')
                        with self.assertRaises(ValueError):
                            package.validate(root, 'linux-x64')

    def test_minimal_payload_has_legal_files_icon_and_correct_apphost(self):
        with tempfile.TemporaryDirectory() as temporary:
            for runtime in package.RUNTIMES:
                with self.subTest(runtime=runtime):
                    root = payload(Path(temporary) / runtime, runtime)
                    result = package.validate(root, runtime)
                    self.assertEqual(result['rid'], runtime)
                    self.assertEqual(result['legal_files'], len(package.LEGAL_FILES))
                    self.assertEqual(result['documentation_files'], len(package.LEGAL_DOCS))
                    self.assertGreater(result['legal_bytes'], 0)
                    self.assertGreater(result['icon_bytes'], 0)
                    (root / result['apphost']).unlink()
                    package.validate_payload(root)  # RID-independent check intentionally does not need the host.
                    with self.assertRaisesRegex(ValueError, 'MeowChan'):
                        package.validate(root, runtime)

    def test_every_required_legal_file_and_retained_icon_must_be_nonempty(self):
        for relative in (*package.LEGAL_FILES, 'Assets/meowchan.png'):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as temporary:
                root = payload(Path(temporary) / 'publish')
                path = root / relative
                path.write_bytes(b'')
                with self.assertRaisesRegex(ValueError, 'missing or empty'):
                    package.validate_payload(root)
                path.unlink()
                with self.assertRaisesRegex(ValueError, 'missing or empty'):
                    package.validate_payload(root)

    def test_repository_docs_and_developer_outputs_are_rejected(self):
        files = ('README.md', 'CHANGELOG.md', 'MeowChan.pdb', 'MeowChan.xml', 'libmeowchan.pdb', 'libmeowchan.xml',
                 'docs/Help.md', 'docs/macos-signing.md', 'docs/releases/v0.9.0.md', 'docs/screenshots/desktop.png')
        directories = ('Desktop', 'libmeowchan', 'tests', 'samples')
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            for index, relative in enumerate((*files, *directories)):
                with self.subTest(relative=relative):
                    root = payload(base / str(index))
                    path = root / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    if relative in directories:
                        path.mkdir()
                    else:
                        path.write_text('repository-only material')
                    with self.assertRaises(ValueError):
                        package.validate_payload(root)

    def test_native_runtime_and_upstream_legal_trees_are_preserved_without_recursion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = payload(Path(temporary) / 'publish')
            extra = ('libvlc/osx-arm64/README.md', 'libvlc/osx-arm64/LICENSE', 'libvlc/osx-arm64/docs/README.md',
                     'libvlc/osx-arm64/plugins/native.dylib', 'libvlc/win-x64/UPSTREAM.pdb', 'Avalonia.dll',
                     'MeowChan.dll', 'libmeowchan.dll', 'MeowChan.deps.json', 'MeowChan.runtimeconfig.json')
            for relative in extra:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'unchanged runtime/legal data')
            before = {path.relative_to(root): path.read_bytes() for path in root.rglob('*') if path.is_file()}
            package.validate(root, 'osx-arm64')
            after = {path.relative_to(root): path.read_bytes() for path in root.rglob('*') if path.is_file()}
            self.assertEqual(after, before)

    @unittest.skipIf(os.name == 'nt', 'Mac alias fixture needs POSIX symlink privileges')
    def test_macos_payload_alias_view_is_valid(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / 'MeowChan.app/Contents'
            physical = payload(bundle / 'Resources/application', 'osx-arm64')
            aliases = bundle / 'MacOS'
            aliases.mkdir()
            (physical / 'MeowChan').rename(aliases / 'MeowChan')
            (physical / 'MeowChan').symlink_to('../../MacOS/MeowChan')
            for entry in physical.iterdir():
                if entry.name != 'MeowChan':
                    (aliases / entry.name).symlink_to(Path('../Resources/application') / entry.name,
                                                   target_is_directory=entry.is_dir())
            self.assertEqual(package.validate(aliases, 'osx-arm64')['documentation_files'], len(package.LEGAL_DOCS))
            (physical / 'docs/Help.md').write_text('must stay in source repository')
            with self.assertRaisesRegex(ValueError, 'only the approved legal files'):
                package.validate_payload(aliases)

    def test_project_publish_inventory_keeps_only_the_legal_documentation(self):
        project = ET.parse(ROOT / 'MeowChan.csproj').getroot()
        published = set()
        for item in project.iter():
            mode = item.get('CopyToPublishDirectory') or item.findtext('CopyToPublishDirectory')
            if mode and mode.casefold() != 'never':
                for name in (item.get('Include') or item.get('Update') or '').split(';'):
                    if name:
                        published.add(name.replace('\\', '/'))
        self.assertEqual({name for name in published if name.startswith('docs/')},
                         {f'docs/{name}' for name in package.LEGAL_DOCS})
        self.assertTrue(set(package.LEGAL_FILES).issubset(published))
        self.assertIn('Assets/meowchan.png', published)
        self.assertFalse({'README.md', 'CHANGELOG.md'} & published)
        self.assertFalse(any(name.split('/')[0].casefold() in package.FORBIDDEN_REPOSITORY_ROOTS for name in published))


if __name__ == '__main__':
    unittest.main()
