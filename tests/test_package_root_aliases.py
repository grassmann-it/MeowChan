"""Root canonicalisation of the package checkers, reproduced with synthetic aliases.

macOS exposes /tmp, /var and /etc as symlinks into /private. A real /tmp alias cannot be
created on Linux, so each test points SYSTEM_PARENT_ALIASES at an alias inside a private
temporary directory. Only the exact listed alias is tolerated; the root itself, other
parent links and every payload link/hardlink stay rejected. No package is built.
"""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def load(name, script):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


DESKTOP = load('alias_desktop_package', 'check-desktop-package.py')
PLUGINS = load('alias_plugin_package', 'package-plugins.py')


def desktop_payload(root):
    root.mkdir(parents=True)
    for relative in (*DESKTOP.LEGAL_FILES, 'Assets/meowchan.png', 'MeowChan'):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'retained ' + relative.encode('utf-8'))
    return root


@unittest.skipIf(os.name == 'nt', 'POSIX symlink fixture')
class PackageRootAliasTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        # realpath: the temp directory itself may sit below a system alias on the host.
        self.base = Path(os.path.realpath(temporary.name))
        self.physical = self.base / 'private' / 'tmp'
        self.physical.mkdir(parents=True)
        self.alias = self.base / 'tmp'
        self.alias.symlink_to(self.physical, target_is_directory=True)

    def allow(self, module, alias=None, target=None):
        return mock.patch.object(module, 'SYSTEM_PARENT_ALIASES',
                                 {str(alias or self.alias): str(target or self.physical)})

    def test_default_contract_is_exactly_the_three_macos_aliases(self):
        for module in (DESKTOP, PLUGINS):
            self.assertEqual({'/tmp': '/private/tmp', '/var': '/private/var', '/etc': '/private/etc'},
                             module.SYSTEM_PARENT_ALIASES)

    def test_desktop_root_below_system_alias_is_canonicalised_to_the_physical_path(self):
        physical_root = desktop_payload(self.physical / 'build' / 'publish')
        with self.allow(DESKTOP):
            result = DESKTOP.validate(self.alias / 'build' / 'publish', 'linux-x64')
        self.assertEqual(str(physical_root), result['root'])
        self.assertEqual(len(DESKTOP.LEGAL_FILES), result['legal_files'])

    def test_unlisted_parent_alias_is_still_rejected(self):
        desktop_payload(self.physical / 'publish')
        with self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
            DESKTOP.validate_payload(self.alias / 'publish')
        with self.assertRaisesRegex(ValueError, 'package root path contains a symlink'):
            PLUGINS._ordinary_directory(self.alias / 'publish')

    def test_listed_alias_with_wrong_target_is_rejected(self):
        desktop_payload(self.physical / 'publish')
        elsewhere = self.base / 'elsewhere'
        elsewhere.mkdir()
        with self.allow(DESKTOP, target=elsewhere), self.assertRaisesRegex(ValueError, 'contains a symlink'):
            DESKTOP.validate_payload(self.alias / 'publish')
        with self.allow(PLUGINS, target=elsewhere), self.assertRaisesRegex(ValueError, 'contains a symlink'):
            PLUGINS._ordinary_directory(self.alias / 'publish')

    def test_listed_target_that_is_itself_a_link_is_rejected(self):
        # The physical target must be a real directory, not another redirect.
        real = self.base / 'real'
        desktop_payload(real / 'publish')
        target_link = self.base / 'target-link'
        target_link.symlink_to(real, target_is_directory=True)
        chained = self.base / 'chained'
        chained.symlink_to(target_link, target_is_directory=True)
        for module, call in ((DESKTOP, DESKTOP.validate_payload), (PLUGINS, PLUGINS._ordinary_directory)):
            with self.allow(module, alias=chained, target=target_link), \
                    self.assertRaisesRegex(ValueError, 'contains a symlink'):
                call(chained / 'publish')

    def test_root_itself_as_symlink_is_rejected_even_when_listed(self):
        real = desktop_payload(self.physical / 'publish')
        link = self.physical / 'publish-link'
        link.symlink_to(real, target_is_directory=True)
        with self.allow(DESKTOP, alias=link, target=real), self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
            DESKTOP.validate_payload(link)
        with self.allow(PLUGINS, alias=link, target=real), self.assertRaisesRegex(ValueError, 'contains a symlink'):
            PLUGINS._ordinary_directory(link)
        # The alias itself used as the root is the root, not a parent.
        with self.allow(DESKTOP), self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
            DESKTOP.validate_payload(self.alias)

    def test_symlink_below_the_alias_is_still_a_rejected_parent(self):
        real = desktop_payload(self.physical / 'real')
        (self.physical / 'inner').symlink_to(real, target_is_directory=True)
        with self.allow(DESKTOP), self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
            DESKTOP.validate_payload(self.alias / 'inner')
        with self.allow(PLUGINS), self.assertRaisesRegex(ValueError, 'contains a symlink'):
            PLUGINS._ordinary_directory(self.alias / 'inner')

    def test_payload_links_and_hardlinks_stay_rejected_below_an_allowed_alias(self):
        root = desktop_payload(self.physical / 'publish')
        (root / 'Assets' / 'link.png').symlink_to(root / 'Assets' / 'meowchan.png')
        with self.allow(DESKTOP), self.assertRaisesRegex(ValueError, 'symlink'):
            DESKTOP.validate_payload(self.alias / 'publish')
        (root / 'Assets' / 'link.png').unlink()
        os.link(root / 'LICENSE', root / 'copied-hardlink.bin')
        with self.allow(DESKTOP), self.assertRaisesRegex(ValueError, 'hardlink'):
            DESKTOP.validate_payload(self.alias / 'publish')

    def test_plugin_verify_below_alias_reaches_payload_checks_and_rejects_payload_links(self):
        root = self.physical / 'plugins-out'
        for name in ('plugin-worker', 'plugin-compiler', 'plugins'):
            (root / name).mkdir(parents=True)
        with self.allow(PLUGINS):
            self.assertEqual(root, PLUGINS._ordinary_directory(self.alias / 'plugins-out'))
            (root / 'plugins' / 'escape').symlink_to(self.base)
            with self.assertRaisesRegex(ValueError, 'Plugin payload contains a link'):
                PLUGINS.verify(self.alias / 'plugins-out', 'linux-x64', development=True)
            (root / 'plugins' / 'escape').unlink()
            # Past the root check, verification proceeds to toolchain inputs (absent here).
            with self.assertRaises((ValueError, OSError)) as caught:
                PLUGINS.verify(self.alias / 'plugins-out', 'linux-x64', development=True)
            self.assertNotIn('symlink', str(caught.exception))

    def test_plain_roots_behave_as_before(self):
        root = desktop_payload(self.physical / 'plain')
        self.assertEqual(root, DESKTOP._ordinary_directory(root))
        self.assertEqual(root, PLUGINS._ordinary_directory(root))
        with self.assertRaisesRegex(ValueError, 'must be a'):
            DESKTOP._ordinary_directory(self.physical / 'missing')
        with self.assertRaisesRegex(ValueError, 'must be a regular directory'):
            PLUGINS._ordinary_directory(self.physical / 'missing')


if __name__ == '__main__':
    unittest.main()
