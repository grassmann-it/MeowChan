"""Offline alias/freeze composition regressions, not an AP04 acceptance run.

Reuse the existing synthetic payload factories and adjudication harness without
inheriting/re-running their tests. Heavy runner steps are substitutes; real
identity/seal/hash/inventory/final-verdict code must reject the late mutations.
Set TMPDIR to a private external evidence directory. No dotnet or product runs.
"""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PLUGIN_FIXTURES = load('f04_plugin_fixtures', 'tests/test_plugin_packaging.py')
FROZEN_FIXTURES = load('f04_frozen_fixtures', 'tests/test_ap04_frozen_provenance.py')
PACKAGE = PLUGIN_FIXTURES.PACKAGE
CANDIDATE = FROZEN_FIXTURES.candidate


@unittest.skipIf(os.name == 'nt', 'Synthetic POSIX aliases, not native macOS execution')
class PluginRootAliasCompositionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='f04-plugin-')
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name).resolve()
        self.physical = self.base / 'private/tmp'
        self.physical.mkdir(parents=True)
        self.alias = self.base / 'tmp'
        self.alias.symlink_to(self.physical, target_is_directory=True)

    def payload(self, name='publish', runtime='linux-x64'):
        # fixture() only writes synthetic headers/JSON/bytes; no setUp/test method.
        factory = PLUGIN_FIXTURES.PluginPackagingTests('runTest')
        factory.root = self.physical / name
        factory.unix_helpers = set()
        factory.fixture(runtime)
        return factory.root, self.alias / name

    def allow(self, extra=None):
        aliases = {str(self.alias): str(self.physical)}
        aliases.update(extra or {})
        return mock.patch.object(PACKAGE, 'SYSTEM_PARENT_ALIASES', aliases)

    def test_complete_verification_through_alias_for_every_synthetic_native_header(self):
        for runtime in PACKAGE.RUNTIMES:
            with self.subTest(runtime=runtime):
                root, alias_root = self.payload(runtime, runtime)
                before = CANDIDATE.artifact_files(root)
                with self.allow():
                    direct = PACKAGE.verify(root, runtime)
                    aliased = PACKAGE.verify(alias_root, runtime)
                self.assertEqual(direct, aliased)
                self.assertTrue(aliased['passed'])
                self.assertTrue(aliased['self_contained_required'])
                self.assertEqual(str(root), aliased['root'])
                self.assertEqual(len(PACKAGE.PLUGINS), aliased['plugins'])
                self.assertEqual(len(PACKAGE.SOURCES), aliased['editable_sources'])
                self.assertEqual(before, CANDIDATE.artifact_files(root))

    def test_successful_alias_scan_never_caches_plugin_or_compiler_hashes(self):
        root, alias_root = self.payload()
        changes = (('plugins/assemblies/' + PACKAGE.PLUGINS[0] + '.dll', 'assembly hash'),
                   ('plugin-compiler/references/System.Runtime.dll', 'toolchain hash'))
        with self.allow():
            PACKAGE.verify(alias_root, 'linux-x64')
            for relative, message in changes:
                with self.subTest(relative=relative):
                    path = root / relative
                    original, metadata = path.read_bytes(), path.stat()
                    path.write_bytes(bytes([original[0] ^ 1]) + original[1:])
                    os.utime(path, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
                    with self.assertRaisesRegex(ValueError, message):
                        PACKAGE.verify(alias_root, 'linux-x64')
                    path.write_bytes(original)
                    self.assertTrue(PACKAGE.verify(alias_root, 'linux-x64')['passed'])

    def test_alias_inventory_is_rechecked_for_added_removed_and_empty_paths(self):
        root, alias_root = self.payload()
        with self.allow():
            PACKAGE.verify(alias_root, 'linux-x64')
            extra = root / 'plugins/sources/unlisted.cs'
            extra.write_bytes(b'// synthetic extra payload')
            with self.assertRaisesRegex(ValueError, 'plugin files differ'):
                PACKAGE.verify(alias_root, 'linux-x64')
            extra.unlink()
            extra = root / 'plugins/unlisted'
            extra.mkdir()
            with self.assertRaisesRegex(ValueError, 'directory inventory'):
                PACKAGE.verify(alias_root, 'linux-x64')
            extra.rmdir()
            source = root / 'plugins/sources' / PACKAGE.SOURCES[0]
            original = source.read_bytes()
            source.unlink()
            with self.assertRaisesRegex(ValueError, 'Missing.*plugin files'):
                PACKAGE.verify(alias_root, 'linux-x64')
            source.write_bytes(original)
            self.assertTrue(PACKAGE.verify(alias_root, 'linux-x64')['passed'])

    def test_parent_alias_exception_does_not_allow_payload_links_even_with_identical_bytes(self):
        for kind in ('symlink', 'hardlink'):
            with self.subTest(kind=kind):
                root, alias_root = self.payload(kind)
                with self.allow():
                    PACKAGE.verify(alias_root, 'linux-x64')
                assembly = root / 'plugins/assemblies' / (PACKAGE.PLUGINS[0] + '.dll')
                external = self.base / (kind + '-external.dll')
                external.write_bytes(assembly.read_bytes())
                assembly.unlink()
                if kind == 'symlink':
                    assembly.symlink_to(external)
                else:
                    os.link(external, assembly)
                # Even explicitly listing a payload link cannot turn it into a root alias.
                with self.allow({str(assembly): str(external)}), self.assertRaisesRegex(ValueError, 'link'):
                    PACKAGE.verify(alias_root, 'linux-x64')

    def test_alias_target_is_revalidated_before_a_second_payload_scan(self):
        _root, alias_root = self.payload()
        other, _unused = self.payload('other/publish')
        with self.allow():
            PACKAGE.verify(alias_root, 'linux-x64')
            self.alias.unlink()
            self.alias.symlink_to(other.parent, target_is_directory=True)
            with mock.patch.object(PACKAGE, 'native_architecture', side_effect=AssertionError('payload was read')), \
                    self.assertRaisesRegex(ValueError, 'root path contains a symlink'):
                PACKAGE.verify(alias_root, 'linux-x64')


class FrozenFinalInventoryCompositionTests(unittest.TestCase):
    def setUp(self):
        # The existing fixture forces /tmp to meet preflight's 64-character bound.
        # Redirect only its temp allocation so *all* synthetic data obeys TMPDIR.
        # This class uses the adjudicator, not that preflight/capacity/tool probe.
        temporary = tempfile.TemporaryDirectory(prefix='f04-freeze-', dir=Path(tempfile.gettempdir()).resolve())
        self.addCleanup(temporary.cleanup)
        self.harness = FROZEN_FIXTURES.FrozenProvenanceTests('runTest')
        self.addCleanup(self.harness.doCleanups)
        with mock.patch.object(FROZEN_FIXTURES.tempfile, 'TemporaryDirectory', return_value=temporary):
            self.harness.setUp()

    def adjudicate_failure(self, mutation, problem):
        result = self.harness.run_adjudication(mutation)
        self.harness.assert_failed_report(result)
        _, report, _ = result
        # Every substituted step succeeded. Only the real final freeze gate fails.
        self.assertTrue(all(step['status'] == 'PASS' for step in report['steps']))
        self.assertTrue(any(problem in item for item in report['final_problems']), report['final_problems'])
        self.assertEqual(self.harness.sha, report['candidate']['commit'])

    def test_build_skip_suffixes_are_never_exempt_from_the_frozen_package_inventory(self):
        for index, suffix in enumerate(CANDIDATE.SKIP_SUFFIXES):
            with self.subTest(suffix=suffix):
                extra = self.harness.packages / ('unlisted' + suffix)
                self.harness.out = self.harness.base / f'out-{index}'
                self.adjudicate_failure(lambda _ctx: extra.write_bytes(b'late extra payload'), 'root inventory')
                extra.unlink()

    def test_late_empty_directory_and_nested_payload_fail_final_adjudication(self):
        extra = self.harness.packages / 'late-directory'
        self.adjudicate_failure(lambda _ctx: extra.mkdir(), 'unexpected directory')
        extra.rmdir()
        self.harness.out = self.harness.base / 'out-nested'

        def nested(_ctx):
            extra.mkdir()
            (extra / 'runtime.dll').write_bytes(b'late nested payload')

        self.adjudicate_failure(nested, 'root inventory')

    def test_sealed_runtime_additions_deletions_and_renames_fail_final_adjudication(self):
        built = self.harness.base / 'synthetic-built'
        built.mkdir()
        library = built / 'MeowChan.dll'
        library.write_bytes(b'synthetic bytes, not an executable assembly')
        (built / 'MeowChan.runtimeconfig.json').write_text('{}', encoding='utf-8')
        original_identity = CANDIDATE.identity

        def sealed_identity(*args, **kwargs):
            identity = original_identity(*args, **kwargs)
            CANDIDATE.seal(identity, 'synthetic-consumer', built)
            self.assertEqual([], CANDIDATE.verify(self.harness.root, identity))
            return identity

        with mock.patch.object(CANDIDATE, 'identity', side_effect=sealed_identity):
            for index, mutation in enumerate(('addition', 'deletion', 'rename')):
                with self.subTest(mutation=mutation):
                    extra = built / 'Worker.dll'
                    self.harness.out = self.harness.base / f'out-{index}'

                    def change(_ctx):
                        if mutation == 'addition':
                            extra.write_bytes(b'late runtime payload')
                        elif mutation == 'deletion':
                            library.unlink()
                        else:
                            library.rename(extra)

                    try:
                        self.adjudicate_failure(change, 'synthetic-consumer changed after sealing')
                    finally:
                        if extra.exists():
                            extra.unlink()
                        library.write_bytes(b'synthetic bytes, not an executable assembly')

    @unittest.skipIf(os.name == 'nt', 'POSIX same-byte hardlink substitutions')
    def test_same_byte_hardlink_replacements_fail_after_identity_not_just_on_initial_scan(self):
        paths = (self.harness.packages / 'MeowChan-1.1.0-linux-x64.AppImage',
                 self.harness.packages / 'linux-x64-package-manifest.json', self.harness.sums)
        for index, path in enumerate(paths):
            with self.subTest(path=path.name):
                original = path.read_bytes()
                external = self.harness.base / f'external-{index}'
                external.write_bytes(original)
                self.harness.out = self.harness.base / f'out-{index}'

                def replace(_ctx):
                    path.unlink()
                    os.link(external, path)
                    self.assertEqual(original, path.read_bytes())

                self.adjudicate_failure(replace, 'single-link regular file')
                path.unlink()
                path.write_bytes(original)

    @unittest.skipIf(os.name == 'nt', 'POSIX artifact-root substitution')
    def test_same_byte_package_root_replaced_by_a_symlink_fails_final_adjudication(self):
        def redirect(_ctx):
            retained = self.harness.base / 'retained-packages'
            self.harness.packages.rename(retained)
            self.harness.packages.symlink_to(retained, target_is_directory=True)

        self.adjudicate_failure(redirect, 'package directory path contains a symlink')


if __name__ == '__main__':
    unittest.main()
