import importlib.util
import ast
import json
import os
import re
from pathlib import Path
import struct
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/package-plugins.py'
SPEC = importlib.util.spec_from_file_location('plugin_package', SCRIPT)
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)


class PluginPackagingTests(unittest.TestCase):
    def test_build_graph_and_sdk_free_probe_match_required_payload(self):
        repository = SCRIPT.parents[1]
        delivery = ET.parse(repository / 'packaging/PluginDelivery.targets').getroot()
        prefix = '$(_PluginRepositoryRoot)'
        def registered_paths(item):
            return [repository / path.removeprefix(prefix)
                    for node in delivery.iter(item)
                    for path in node.attrib['Include'].split(';')]
        projects = registered_paths('_ShippedPluginProject')
        assemblies = []
        for project in projects:
            graph = ET.parse(project).getroot()
            assemblies.append(graph.findtext('.//AssemblyName'))
            self.assertFalse(list(graph.iter('PackageReference')), project)
            self.assertEqual(['../../../MeowChan.PluginSdk/MeowChan.PluginSdk.csproj'],
                             [node.attrib['Include'] for node in graph.iter('ProjectReference')], project)
        self.assertEqual(set(PACKAGE.PLUGINS), set(assemblies))
        self.assertEqual(len(PACKAGE.PLUGINS), len(assemblies))
        sources = registered_paths('_PluginExampleSource')
        self.assertTrue(all(path.is_file() for path in sources))
        self.assertEqual(set(PACKAGE.SOURCES), {path.name for path in sources})
        self.assertEqual(len(PACKAGE.SOURCES), len(sources))
        probe = ast.parse((repository / 'tests/PluginDeliverySmoke/sdk_free_worker_probe.py').read_text())
        expected_ids = [ast.literal_eval(node.value) for node in ast.walk(probe)
                        if isinstance(node, ast.Assign)
                        and any(isinstance(target, ast.Name) and target.id == 'expected_ids' for target in node.targets)]
        self.assertEqual(1, len(expected_ids))
        self.assertEqual(set(PACKAGE.PLUGINS), set(expected_ids[0]))
        compile_probe = ast.parse((repository / 'tests/PluginDeliverySmoke/sdk_free_compile_probe.py').read_text())
        compile_ids = [ast.literal_eval(node.value) for node in ast.walk(compile_probe)
                       if isinstance(node, ast.Assign)
                       and any(isinstance(target, ast.Name) and target.id == 'SOURCE_PLUGIN_IDS' for target in node.targets)]
        self.assertEqual(1, len(compile_ids))
        self.assertEqual(set(PACKAGE.SOURCES), set(compile_ids[0]))
        self.assertTrue(set(compile_ids[0].values()).issubset(set(expected_ids[0].values())))

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='meowchan-plugin-package-')
        # Python 3.12 on Windows may return the 8.3 TEMP spelling. The verifier
        # resolves its root, so register synthetic modes with that same spelling.
        self.root = Path(self.temporary.name).resolve()
        self.unix_helpers = set()
        if os.name == 'nt':
            # NTFS does not represent Unix execute bits. Emulate them only for
            # synthetic cross-target helpers; all other stat values stay real.
            original_stat = Path.stat
            def fixture_stat(path, *args, **kwargs):
                value = original_stat(path, *args, **kwargs)
                if path in self.unix_helpers:
                    fields = list(value)
                    fields[0] |= 0o111
                    return os.stat_result(fields)
                return value
            patch = mock.patch.object(Path, 'stat', autospec=True, side_effect=fixture_stat)
            patch.start()
            self.addCleanup(patch.stop)

    def tearDown(self):
        self.temporary.cleanup()

    def put(self, relative, content=b'fixture bytes'):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return path

    def json(self, relative, value):
        return self.put(relative, json.dumps(value).encode())

    def fixture(self, runtime='linux-x64', self_contained=True):
        header = bytearray(256)
        if runtime.startswith('win-'):
            header[:2] = b'MZ'; struct.pack_into('<I', header, 0x3c, 128)
            header[128:132] = b'PE\0\0'; struct.pack_into('<H', header, 132, 0x8664)
        elif runtime.startswith('linux-'):
            header[:6] = b'\x7fELF\x02\x01'
            struct.pack_into('<H', header, 18, 62 if runtime.endswith('x64') else 183)
        else:
            header[:4] = b'\xcf\xfa\xed\xfe'
            struct.pack_into('<I', header, 4, 0x01000007 if runtime.endswith('x64') else 0x0100000c)
        for directory, name in (('plugin-worker', 'MeowChan.PluginWorker'), ('plugin-compiler', 'MeowChan.PluginCompiler')):
            path = self.put(directory + '/' + name + ('.exe' if runtime.startswith('win-') else ''), header)
            path.chmod(0o755)
            if not runtime.startswith('win-'):
                self.unix_helpers.add(path)
            self.put(directory + '/' + name + '.dll')
            self.json(directory + '/' + name + '.deps.json', {'libraries': {}})
            config = {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.11'}]} if self_contained else {'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}
            self.json(directory + '/' + name + '.runtimeconfig.json', {'runtimeOptions': config})
            natives = ('coreclr.dll', 'hostpolicy.dll', 'hostfxr.dll') if runtime.startswith('win-') else (('libcoreclr.dylib', 'libhostpolicy.dylib', 'libhostfxr.dylib') if runtime.startswith('osx-') else ('libcoreclr.so', 'libhostpolicy.so', 'libhostfxr.so'))
            if self_contained:
                for native in natives:
                    self.put(directory + '/' + native)
        rows = []
        for path, role in (('MeowChan.PluginCompiler.dll', 'compiler'), ('Microsoft.CodeAnalysis.dll', 'compiler'),
                           ('Microsoft.CodeAnalysis.CSharp.dll', 'compiler'), ('MeowChan.PluginSdk.dll', 'sdk'),
                           ('references/System.Runtime.dll', 'framework')):
            file = self.put('plugin-compiler/' + path, (path + role).encode())
            rows.append({'Path': path, 'Role': role, 'Sha256': PACKAGE.digest(file)})
        sdk = next(row['Sha256'] for row in rows if row['Role'] == 'sdk')
        self.put('plugin-worker/MeowChan.PluginSdk.dll', (self.root / 'plugin-compiler/MeowChan.PluginSdk.dll').read_bytes())
        compiler = PACKAGE.file_set_hash([row for row in rows if row['Role'] == 'compiler'])
        self.json('plugin-compiler/toolchain.json', {'SchemaVersion': 1, 'TargetFramework': 'net10.0', 'Options': PACKAGE.OPTIONS,
                  'Files': rows, 'SdkFingerprint': sdk, 'CompilerFingerprint': compiler,
                  'ReferenceFingerprint': PACKAGE.file_set_hash([row for row in rows if row['Role'] != 'compiler'])})
        for license in ('framework/LICENSE.TXT', 'framework/THIRD-PARTY-NOTICES.TXT', 'roslyn/LICENSE-Roslyn.txt', 'roslyn/ThirdPartyNotices.rtf', 'roslyn/provenance.json'):
            self.put('plugin-compiler/licenses/' + license)
        for name in PACKAGE.PLUGINS:
            file = self.put('plugins/assemblies/' + name + '.dll', name.encode())
            self.json('plugins/' + name + '.plugin.json', {'SchemaVersion': 1, 'AssemblyRelativePath': 'assemblies/' + name + '.dll',
                      'AssemblySha256': PACKAGE.digest(file), 'SdkFingerprint': sdk, 'CompilerFingerprint': compiler, 'Resources': []})
        for source in PACKAGE.SOURCES:
            self.put('plugins/sources/' + source, b'// Editable source fixture')

    def test_all_five_native_layouts(self):
        for runtime in PACKAGE.RUNTIMES:
            with self.subTest(runtime=runtime), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture(runtime)
                    report = PACKAGE.verify(self.root, runtime)
                    self.assertEqual(24, report['plugins'])
                    self.assertEqual(21, report['editable_sources'])
                finally:
                    self.root = outer

    def test_framework_dependent_helpers_are_only_allowed_for_developer_outputs(self):
        self.fixture(self_contained=False)
        with self.assertRaisesRegex(ValueError, 'external .NET runtime'):
            PACKAGE.verify(self.root, 'linux-x64')
        self.assertTrue(PACKAGE.verify(self.root, 'linux-x64', development=True)['passed'])

    def test_exact_bundled_inventory_includes_filters(self):
        self.assertEqual({'MeowChan.Plugin.FourChan', 'MeowChan.Plugin.EightKun', 'MeowChan.Plugin.Danbooru', 'MeowChan.Plugin.Lainchan',
                          'MeowChan.Plugin.Kohlchan', 'MeowChan.ImageDimensionsFilter',
                          'MeowChan.Plugin.Wizchan', 'MeowChan.Plugin.Endchan',
                          'MeowChan.FileRulesFilter', 'MeowChan.HashBlocklistFilter', 'MeowChan.AnimationFormatFilter', 'MeowChan.PngMetadataFilter',
                          'MeowChan.Plugin.Leftypol', 'MeowChan.Plugin.Dvach', 'MeowChan.Plugin.Uboachan', 'MeowChan.Plugin.Zzzchan', 'MeowChan.Plugin.SushiChan',
                          'MeowChan.Plugin.SevenChan', 'MeowChan.Plugin.BbwChan', 'MeowChan.Plugin.AnonImageboards',
                          'MeowChan.Plugin.CapybaraChan', 'MeowChan.Plugin.EightchanMoe', 'MeowChan.Plugin.Fchan', 'MeowChan.Plugin.GuroCx'}, set(PACKAGE.PLUGINS))
        self.assertEqual({'Lainchan.cs', 'Kohlchan.cs', 'ImageDimensionsFilter.cs', 'Wizchan.cs', 'Endchan.cs',
                          'FileRulesFilter.cs', 'HashBlocklistFilter.cs', 'AnimationFormatFilter.cs', 'PngMetadataFilter.cs',
                          'Leftypol.cs', 'Dvach.cs', 'Uboachan.cs', 'Zzzchan.cs', 'SushiChan.cs',
                          'SevenChan.cs', 'BbwChan.cs', 'AnonImageboards.cs', 'CapybaraChan.cs', 'EightchanMoe.cs',
                          'Fchan.cs', 'GuroCx.cs'}, set(PACKAGE.SOURCES))
        self.assertEqual(5, len(PACKAGE.RUNTIMES))
        self.assertNotIn('Danbooru.cs', PACKAGE.SOURCES)
        self.assertIn('MeowChan.Plugin.Danbooru', PACKAGE.PLUGINS)
        self.fixture()
        for assembly_name, source in ([('MeowChan.Plugin.Wizchan', 'Wizchan'), ('MeowChan.Plugin.Endchan', 'Endchan')] +
                [('MeowChan.FileRulesFilter', 'FileRulesFilter'), ('MeowChan.HashBlocklistFilter', 'HashBlocklistFilter'), ('MeowChan.AnimationFormatFilter', 'AnimationFormatFilter'), ('MeowChan.PngMetadataFilter', 'PngMetadataFilter'),
                 ('MeowChan.Plugin.Leftypol', 'Leftypol'), ('MeowChan.Plugin.Dvach', 'Dvach'), ('MeowChan.Plugin.Uboachan', 'Uboachan'), ('MeowChan.Plugin.Zzzchan', 'Zzzchan'), ('MeowChan.Plugin.SushiChan', 'SushiChan'),
                 ('MeowChan.Plugin.SevenChan', 'SevenChan'), ('MeowChan.Plugin.BbwChan', 'BbwChan'), ('MeowChan.Plugin.AnonImageboards', 'AnonImageboards'),
                 ('MeowChan.Plugin.CapybaraChan', 'CapybaraChan'), ('MeowChan.Plugin.EightchanMoe', 'EightchanMoe'), ('MeowChan.Plugin.Fchan', 'Fchan'),
                 ('MeowChan.Plugin.GuroCx', 'GuroCx')]):
            with self.subTest(source=source):
                manifest = self.root / ('plugins/' + assembly_name + '.plugin.json')
                original = manifest.read_bytes()
                manifest.unlink()
                with self.assertRaisesRegex(ValueError, 'required plugin inventory'):
                    PACKAGE.verify(self.root, 'linux-x64')
                manifest.write_bytes(original)
                editable = self.root / ('plugins/sources/' + source + '.cs')
                original = editable.read_bytes()
                editable.unlink()
                with self.assertRaisesRegex(ValueError, 'Missing'):
                    PACKAGE.verify(self.root, 'linux-x64')
                editable.write_bytes(original)
                assembly = self.root / ('plugins/assemblies/' + assembly_name + '.dll')
                original = assembly.read_bytes()
                assembly.write_bytes(b'modified')
                with self.assertRaisesRegex(ValueError, 'assembly hash'):
                    PACKAGE.verify(self.root, 'linux-x64')
                assembly.write_bytes(original)

    def test_wrong_native_architecture_is_rejected(self):
        self.fixture('linux-arm64')
        with self.assertRaisesRegex(ValueError, 'architecture'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_missing_self_contained_host_resolver_is_rejected(self):
        self.fixture()
        (self.root / 'plugin-worker/libhostfxr.so').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_changed_plugin_bytes_are_rejected(self):
        self.fixture()
        self.put('plugins/assemblies/MeowChan.Plugin.Lainchan.dll', b'modified')
        with self.assertRaisesRegex(ValueError, 'assembly hash'):
            PACKAGE.verify(self.root, 'linux-x64')

    def rewrite_manifest(self, **changes):
        name = PACKAGE.PLUGINS[0]
        path = self.root / 'plugins' / (name + '.plugin.json')
        data = json.loads(path.read_text())
        data.update(changes)
        path.write_text(json.dumps(data))

    def test_manifest_binding_mismatches_are_rejected(self):
        cases = (
            ({'SdkFingerprint': '0' * 64}, 'build/SDK fingerprint'),
            ({'CompilerFingerprint': '1' * 64}, 'build/SDK fingerprint'),
            ({'AssemblyRelativePath': 'assemblies/Other.dll'}, 'declared package identity'),
            ({'Resources': ['x']}, 'schema/resources'),
            ({'SchemaVersion': 2}, 'schema/resources'),
        )
        for changes, message in cases:
            with self.subTest(changes=changes), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture()
                    self.rewrite_manifest(**changes)
                    with self.assertRaisesRegex(ValueError, message):
                        PACKAGE.verify(self.root, 'linux-x64')
                finally:
                    self.root = outer

    def test_missing_manifest_and_worker_sdk_drift_are_rejected(self):
        self.fixture()
        (self.root / 'plugins' / (PACKAGE.PLUGINS[0] + '.plugin.json')).unlink()
        with self.assertRaisesRegex(ValueError, 'manifests differ'):
            PACKAGE.verify(self.root, 'linux-x64')
        self.fixture()
        self.put('plugin-worker/MeowChan.PluginSdk.dll', b'drifted sdk')
        with self.assertRaisesRegex(ValueError, 'Worker SDK'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_changed_compiler_references_are_rejected(self):
        self.fixture()
        self.put('plugin-compiler/references/System.Runtime.dll', b'modified')
        with self.assertRaisesRegex(ValueError, 'toolchain hash'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_path_traversal_is_rejected(self):
        self.fixture()
        file = self.root / 'plugin-compiler/toolchain.json'
        data = json.loads(file.read_text()); data['Files'][0]['Path'] = '../external.dll'
        file.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'escapes'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_symlink_cannot_substitute_compiler_file(self):
        self.fixture()
        path = self.root / 'plugin-compiler/references/System.Runtime.dll'
        original = path.read_bytes(); path.unlink()
        target = self.put('outside-reference.dll', original)
        try:
            path.symlink_to(target)
        except (OSError, NotImplementedError):
            self.skipTest('Platform does not permit fixture symlinks')
        with self.assertRaisesRegex(ValueError, 'link'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_missing_example_does_not_pass(self):
        self.fixture()
        (self.root / 'plugins/sources/Kohlchan.cs').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_extra_top_level_source_would_be_an_unintended_installation_candidate(self):
        self.fixture()
        self.put('plugins/Lainchan.cs', b'// duplicate active plugin')
        with self.assertRaisesRegex(ValueError, 'unintended installation candidate'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_roslyn_must_not_be_in_main_process_dependency_graph(self):
        self.fixture()
        self.json('MeowChan.deps.json', {'libraries': {'Microsoft.CodeAnalysis.CSharp/5.0.0': {}}})
        with self.assertRaisesRegex(ValueError, 'main process'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_non_executable_unix_helper_is_rejected(self):
        self.fixture()
        helper = self.root / 'plugin-worker/MeowChan.PluginWorker'
        helper.chmod(0o644)
        self.unix_helpers.discard(helper)
        with self.assertRaisesRegex(ValueError, 'not executable'):
            PACKAGE.verify(self.root, 'linux-x64')

    # ---- Packaging-freshness slice: 24-bundle contract (16 Chan, 1 Danbooru, 5 filters, 2 blocked scaffolds) ----
    REPOSITORY = SCRIPT.parents[1]
    BLOCKED_SCAFFOLDS = {'MeowChan.Plugin.AnonImageboards', 'MeowChan.Plugin.GuroCx'}
    BOORU = {'MeowChan.Plugin.Danbooru'}
    FILTERS = {'MeowChan.ImageDimensionsFilter', 'MeowChan.FileRulesFilter', 'MeowChan.HashBlocklistFilter',
               'MeowChan.AnimationFormatFilter', 'MeowChan.PngMetadataFilter'}

    def test_bundle_contract_counts_and_types(self):
        plugins = set(PACKAGE.PLUGINS)
        self.assertEqual(24, len(PACKAGE.PLUGINS), 'bundle count drifted from the 24-bundle contract')
        self.assertEqual(len(PACKAGE.PLUGINS), len(plugins), 'duplicate bundle name in PLUGINS')
        self.assertEqual(21, len(PACKAGE.SOURCES))
        self.assertEqual(len(PACKAGE.SOURCES), len(set(PACKAGE.SOURCES)), 'duplicate editable source')
        self.assertTrue(self.BLOCKED_SCAFFOLDS | self.BOORU | self.FILTERS <= plugins)
        chan = plugins - self.BLOCKED_SCAFFOLDS - self.BOORU - self.FILTERS
        self.assertEqual((16, 1, 5, 2), (len(chan), len(self.BOORU), len(self.FILTERS), len(self.BLOCKED_SCAFFOLDS)))
        self.assertTrue(all(name.startswith('MeowChan.Plugin.') for name in chan | self.BOORU | self.BLOCKED_SCAFFOLDS))
        self.assertTrue(all(name.endswith('Filter') for name in self.FILTERS))
        # Compiled-only official plugins (Chan official sources and Danbooru) have no editable example.
        official = {'MeowChan.Plugin.FourChan', 'MeowChan.Plugin.EightKun'} | self.BOORU
        self.assertEqual(plugins - official, {n for n in plugins if n.rsplit('.', 1)[1] + '.cs' in PACKAGE.SOURCES})

    def test_blocked_scaffolds_are_exactly_the_sources_declaring_blocked_status(self):
        blocked = {path.stem for path in (self.REPOSITORY / 'plugins/Examples').glob('*/*.cs')
                   if 'PluginDeclarationStatus.Blocked' in path.read_text(encoding='utf-8')}
        self.assertEqual({name.rsplit('.', 1)[1] for name in self.BLOCKED_SCAFFOLDS}, blocked)
        for name in self.BLOCKED_SCAFFOLDS:
            self.assertIn(name, PACKAGE.PLUGINS)
            self.assertIn(name.rsplit('.', 1)[1] + '.cs', PACKAGE.SOURCES)

    def test_example_sources_on_disk_match_the_shipped_inventory_without_stale_extras(self):
        on_disk = {path.name for path in (self.REPOSITORY / 'plugins/Examples').glob('*/*.cs')}
        self.assertEqual(set(PACKAGE.SOURCES), on_disk)

    def test_plugin_and_product_versions_stay_distinct(self):
        props = ET.parse(self.REPOSITORY / 'Directory.Build.props').getroot().findtext('.//Version')
        sdk = re.search(r'const string Version = "([^"]+)"', (self.REPOSITORY / 'MeowChan.PluginSdk/SdkContract.cs').read_text())
        self.assertRegex(props, r'\d+\.\d+\.\d+\Z')
        self.assertIsNotNone(sdk)
        self.assertRegex(sdk.group(1), r'\d+\.\d+\.\d+\Z')
        # The manifest SchemaVersion is its own integer; neither product nor SDK version may stand in for it.
        for value in (props, sdk.group(1), '1', '1.0'):
            with self.subTest(schema=value), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture()
                    self.rewrite_manifest(SchemaVersion=value)
                    with self.assertRaisesRegex(ValueError, 'schema/resources'):
                        PACKAGE.verify(self.root, 'linux-x64')
                finally:
                    self.root = outer
        # Verifier output carries no product version: it reports the SDK fingerprint only.
        self.fixture()
        report = PACKAGE.verify(self.root, 'linux-x64')
        self.assertFalse({'version', 'product_version', 'sdk_version'} & set(report))

    def test_report_matches_fixture_manifest_and_hash_output(self):
        self.fixture()
        report = PACKAGE.verify(self.root, 'linux-x64')
        toolchain = json.loads((self.root / 'plugin-compiler/toolchain.json').read_text())
        self.assertEqual(24, report['plugins'])
        self.assertEqual(21, report['editable_sources'])
        self.assertEqual(len(toolchain['Files']), report['compiler_files_verified'])
        self.assertEqual(toolchain['SdkFingerprint'], report['sdk_sha256'])
        self.assertEqual(PACKAGE.digest(self.root / 'plugin-worker/MeowChan.PluginSdk.dll'), report['sdk_sha256'])
        self.assertRegex(report['sdk_sha256'], PACKAGE.HASH)
        self.assertTrue(report['passed'] and report['self_contained_required'])
        self.assertEqual(sorted(report), sorted(['runtime', 'self_contained_required', 'plugins', 'editable_sources',
                                                 'compiler_files_verified', 'sdk_sha256', 'root', 'passed']))
        manifests = sorted(path.name for path in (self.root / 'plugins').glob('*.plugin.json'))
        self.assertEqual(sorted(name + '.plugin.json' for name in PACKAGE.PLUGINS), manifests)
        for name in PACKAGE.PLUGINS:
            manifest = json.loads((self.root / 'plugins' / (name + '.plugin.json')).read_text())
            self.assertEqual(PACKAGE.digest(self.root / 'plugins' / manifest['AssemblyRelativePath']), manifest['AssemblySha256'])
            self.assertRegex(manifest['AssemblySha256'], PACKAGE.HASH)

    def test_stale_extra_or_surplus_bundles_are_rejected(self):
        self.fixture()
        # A 25th manifest (stale bundle left over from an older build) must not be tolerated.
        self.json('plugins/MeowChan.Plugin.Stale.plugin.json', {'SchemaVersion': 1})
        with self.assertRaisesRegex(ValueError, 'required plugin inventory'):
            PACKAGE.verify(self.root, 'linux-x64')
        (self.root / 'plugins/MeowChan.Plugin.Stale.plugin.json').unlink()
        self.assertEqual(24, PACKAGE.verify(self.root, 'linux-x64')['plugins'])
        # A stale assembly without manifest is not verified by hash, but a stale top-level file is rejected.
        self.put('plugins/MeowChan.Plugin.Stale.dll', b'stale')
        with self.assertRaisesRegex(ValueError, 'unintended installation candidate'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_bundle_count_reductions_are_rejected_for_each_type(self):
        groups = {'chan': set(PACKAGE.PLUGINS) - self.BLOCKED_SCAFFOLDS - self.BOORU - self.FILTERS,
                  'booru': self.BOORU, 'filter': self.FILTERS, 'blocked': self.BLOCKED_SCAFFOLDS}
        for label, names in groups.items():
            name = sorted(names)[0]
            with self.subTest(group=label, removed=name), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture()
                    (self.root / 'plugins' / (name + '.plugin.json')).unlink()
                    with self.assertRaisesRegex(ValueError, 'required plugin inventory'):
                        PACKAGE.verify(self.root, 'linux-x64')
                finally:
                    self.root = outer

    def test_stale_toolchain_fingerprints_and_duplicate_entries_are_rejected(self):
        for field in ('SdkFingerprint', 'CompilerFingerprint', 'ReferenceFingerprint'):
            with self.subTest(field=field), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture()
                    file = self.root / 'plugin-compiler/toolchain.json'
                    data = json.loads(file.read_text()); data[field] = '2' * 64
                    file.write_text(json.dumps(data))
                    with self.assertRaisesRegex(ValueError, 'fingerprints differ'):
                        PACKAGE.verify(self.root, 'linux-x64')
                finally:
                    self.root = outer
        self.fixture()
        file = self.root / 'plugin-compiler/toolchain.json'
        data = json.loads(file.read_text()); data['Files'].append(dict(data['Files'][0], Path=data['Files'][0]['Path'].upper()))
        file.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_malformed_or_stale_hash_values_are_rejected(self):
        for bad in ('', 'A' * 64, 'f' * 63, 'g' * 64):
            with self.subTest(bad=bad), tempfile.TemporaryDirectory(dir=self.root) as directory:
                outer = self.root; self.root = Path(directory).resolve()
                try:
                    self.fixture()
                    file = self.root / 'plugin-compiler/toolchain.json'
                    data = json.loads(file.read_text()); data['Files'][0]['Sha256'] = bad
                    file.write_text(json.dumps(data))
                    with self.assertRaisesRegex(ValueError, 'Invalid compiler toolchain entry'):
                        PACKAGE.verify(self.root, 'linux-x64')
                finally:
                    self.root = outer
        self.fixture()
        self.rewrite_manifest(AssemblySha256='0' * 64)
        with self.assertRaisesRegex(ValueError, 'assembly hash'):
            PACKAGE.verify(self.root, 'linux-x64')

    def test_hash_helpers_are_deterministic_and_order_independent(self):
        rows = [{'Role': 'compiler', 'Path': 'b.dll', 'Sha256': 'a' * 64}, {'Role': 'sdk', 'Path': 'a.dll', 'Sha256': 'b' * 64}]
        self.assertEqual(PACKAGE.file_set_hash(rows), PACKAGE.file_set_hash(list(reversed(rows))))
        self.assertNotEqual(PACKAGE.file_set_hash(rows), PACKAGE.file_set_hash([dict(rows[0], Sha256='c' * 64), rows[1]]))
        self.assertNotEqual(PACKAGE.file_set_hash(rows), PACKAGE.file_set_hash([dict(rows[0], Role='framework'), rows[1]]))
        self.assertNotEqual(PACKAGE.hash_parts(['ab', 'c']), PACKAGE.hash_parts(['a', 'bc']))

    def test_cli_report_file_is_deterministic_and_failure_writes_none(self):
        self.fixture()
        first, second = self.root.parent / (self.root.name + '-r1.json'), self.root.parent / (self.root.name + '-r2.json')
        self.addCleanup(lambda: [path.unlink(missing_ok=True) for path in (first, second)])
        with mock.patch('builtins.print'):
            for target in (first, second):
                self.assertEqual(0, PACKAGE.main(['--verify', str(self.root), '--runtime', 'linux-x64', '--report', str(target)]))
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(24, json.loads(first.read_text())['plugins'])
        (self.root / 'plugins/MeowChan.Plugin.Kohlchan.plugin.json').unlink()
        first.unlink()
        with self.assertRaises(SystemExit):
            PACKAGE.main(['--verify', str(self.root), '--runtime', 'linux-x64', '--report', str(first)])
        self.assertFalse(first.exists())

    def test_cli_reports_failure_without_repairing_package(self):
        self.fixture()
        target = self.root / 'plugins/assemblies/MeowChan.Plugin.Kohlchan.dll'
        target.write_bytes(b'bad')
        with self.assertRaises(SystemExit) as error:
            PACKAGE.main(['--verify', str(self.root), '--runtime', 'linux-x64'])
        self.assertEqual(1, error.exception.code)
        self.assertEqual(b'bad', target.read_bytes())


if __name__ == '__main__':
    unittest.main()
