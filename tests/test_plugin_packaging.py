import importlib.util
import ast
import json
import os
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
                    self.assertEqual(11, report['plugins'])
                    self.assertEqual(9, report['editable_sources'])
                finally:
                    self.root = outer

    def test_framework_dependent_helpers_are_only_allowed_for_developer_outputs(self):
        self.fixture(self_contained=False)
        with self.assertRaisesRegex(ValueError, 'external .NET runtime'):
            PACKAGE.verify(self.root, 'linux-x64')
        self.assertTrue(PACKAGE.verify(self.root, 'linux-x64', development=True)['passed'])

    def test_exact_bundled_inventory_includes_filters(self):
        self.assertEqual({'MeowChan.Plugin.FourChan', 'MeowChan.Plugin.EightKun', 'MeowChan.Plugin.Lainchan',
                          'MeowChan.Plugin.Kohlchan', 'MeowChan.ImageDimensionsFilter',
                          'MeowChan.Plugin.Wizchan', 'MeowChan.Plugin.Endchan',
                          'MeowChan.FileRulesFilter', 'MeowChan.HashBlocklistFilter', 'MeowChan.AnimationFormatFilter', 'MeowChan.PngMetadataFilter'}, set(PACKAGE.PLUGINS))
        self.assertEqual({'Lainchan.cs', 'Kohlchan.cs', 'ImageDimensionsFilter.cs', 'Wizchan.cs', 'Endchan.cs',
                          'FileRulesFilter.cs', 'HashBlocklistFilter.cs', 'AnimationFormatFilter.cs', 'PngMetadataFilter.cs'}, set(PACKAGE.SOURCES))
        self.assertEqual(5, len(PACKAGE.RUNTIMES))
        self.fixture()
        for assembly_name, source in ([('MeowChan.Plugin.Wizchan', 'Wizchan'), ('MeowChan.Plugin.Endchan', 'Endchan')] +
                [('MeowChan.FileRulesFilter', 'FileRulesFilter'), ('MeowChan.HashBlocklistFilter', 'HashBlocklistFilter'), ('MeowChan.AnimationFormatFilter', 'AnimationFormatFilter'), ('MeowChan.PngMetadataFilter', 'PngMetadataFilter')]):
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
