#!/usr/bin/env python3
"""Fast failure-contract checks; these do not execute an apphost or desktop."""
import importlib.util
from pathlib import Path
import tempfile
import struct
import uuid
import zipfile
import zlib
import subprocess
import shutil
import json
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('plugin_workflow', Path(__file__).with_name('apphost-plugin-workflow-smoke.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class PluginWorkflowRunnerTests(unittest.TestCase):
    def metrics(self, phase='download'):
        checks = runner.COMMON | ({'delivered-compiler-uncached', 'real-source-and-filter-workers',
                                   'delivered-plugin-source-described', 'delivered-plugin-source-cache-hit'}
                                  if phase == 'download' else {'paused-offline-restore', 'delivered-plugin-source-offline-cache-hit'})
        return dict(schemaVersion=1, completed=True, failure=None, phase=phase, checks=list(checks),
                    profile='/profile', workingDirectory='/work', renderScaling=1, unexpectedRequests=0,
                    revisions={plugin_id: str(i) for i, plugin_id in enumerate(sorted(runner.PLUGIN_IDS))}, processId=123,
                    requests=sorted(runner.REQUESTS) if phase == 'download' else [],
                    sourceCompilation=dict(source='plugins/sources/Lainchan.cs', pluginId='meowchan.source.lainchan',
                                           sdkVersion='1.0.0', sourceSha256='a' * 64, executionRevision='b' * 64,
                                           assemblySha256='c' * 64, uncachedBuild=True, describedInWorker=True, cachedBuild=True))

    def validate(self, metrics):
        with patch.object(runner, 'validate_assembly_bindings', return_value=['bound']):
            return runner.validate_metrics(metrics, metrics['phase'], Path('/profile'), Path('/work'), 1,
                                           dict(apphostMember='MeowChan', entries=[dict(path='plugins/sources/Lainchan.cs', sha256='a' * 64)]))

    def test_valid_download_and_restore(self):
        self.assertEqual(['bound'], self.validate(self.metrics()))
        self.assertEqual(['bound'], self.validate(self.metrics('restore')))

    def test_seven_exact_plugin_ids_and_twelve_exact_requests_are_required(self):
        self.assertEqual(7, len(runner.PLUGIN_IDS))
        self.assertEqual(12, len(runner.REQUESTS))
        for source in ('wizchan', 'endchan'):
            metrics = self.metrics()
            metrics['revisions']['foreign'] = metrics['revisions'].pop('meowchan.source.' + source)
            with self.assertRaises(ValueError): self.validate(metrics)
            metrics = self.metrics()
            metrics['requests'] = [uri for uri in metrics['requests'] if source not in uri]
            with self.assertRaises(ValueError): self.validate(metrics)

    def test_missing_compiler_proof_fails(self):
        metrics = self.metrics()
        metrics['checks'].remove('delivered-compiler-uncached')
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_plain_class_compile_cannot_replace_plugin_description_and_cache_proof(self):
        for required in ('delivered-plugin-source-described', 'delivered-plugin-source-cache-hit'):
            metrics = self.metrics()
            metrics['checks'].remove(required)
            with self.assertRaises(ValueError): self.validate(metrics)

    def test_compiled_source_must_match_the_actual_delivered_source_bytes(self):
        metrics = self.metrics()
        metrics['sourceCompilation']['sourceSha256'] = 'd' * 64
        with self.assertRaisesRegex(ValueError, 'delivered source'): self.validate(metrics)

    def test_missing_compiled_plugin_evidence_fails(self):
        metrics = self.metrics()
        metrics['sourceCompilation'] = None
        with self.assertRaisesRegex(ValueError, 'delivered source'): self.validate(metrics)

    def test_restore_must_reuse_compiled_source_cache(self):
        metrics = self.metrics('restore')
        metrics['sourceCompilation']['cachedBuild'] = False
        with self.assertRaisesRegex(ValueError, 'delivered source'): self.validate(metrics)
        metrics = self.metrics('restore')
        metrics['checks'].remove('delivered-plugin-source-offline-cache-hit')
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_restore_network_attempt_fails(self):
        metrics = self.metrics('restore')
        metrics['requests'] = [next(iter(runner.REQUESTS))]
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_unknown_or_duplicate_download_request_fails(self):
        for replacement in ('https://example.org/live', sorted(runner.REQUESTS)[0]):
            metrics = self.metrics()
            metrics['requests'][-1] = replacement
            with self.assertRaises(ValueError): self.validate(metrics)

    def test_product_failure_cannot_pass(self):
        metrics = self.metrics()
        metrics['failure'] = 'shutdown failed'
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_namespace_mounts_do_not_expose_home_or_original_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            working = root / 'cwd'
            working.mkdir()
            home = runner.prepare_private_home(root, working)
            command = runner.sandbox(root / 'package/MeowChan', root, working)
            self.assertEqual(0o700, home.stat().st_mode & 0o777)
            self.assertIn(str(home), command)
            self.assertNotIn('/no-home', command)
            self.assertFalse((home / '.dotnet/sdk').exists())
            self.assertFalse((home / '.nuget/packages').exists())
            self.assertIn('--unshare-net', command)
            self.assertIn('/no-sdk', command)
            self.assertIn('/no-nuget', command)
            self.assertNotIn('/home', command)
            self.assertNotIn('/opt', command)
            self.assertNotIn('/', command)
            index = command.index(str(root / 'package'))
            self.assertEqual('--ro-bind', command[index - 1])
            self.assertEqual('--smoke-plugin-workflow', command[-1])


    def test_sandbox_rejects_missing_or_shared_home(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            working = root / 'cwd'
            working.mkdir()
            with self.assertRaises((ValueError, OSError)): runner.sandbox(root / 'package/MeowChan', root, working)
            home = runner.prepare_private_home(root, working)
            home.chmod(0o755)
            with self.assertRaises((ValueError, OSError)): runner.sandbox(root / 'package/MeowChan', root, working)

    @unittest.skipUnless(shutil.which('bwrap'), 'bubblewrap unavailable')
    def test_private_home_is_visible_but_host_home_and_package_stores_are_not(self):
        # Pure Python namespace proof; no desktop, runtime, profile or network access.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / 'package').mkdir()
            working = root / 'cwd'
            working.mkdir()
            home = runner.prepare_private_home(root, working)
            command = runner.sandbox(root / 'package/MeowChan', root, working)[:-2]
            socket = command.index('/tmp/.X11-unix')
            del command[socket - 1:socket + 2]  # This non-GUI probe requires no X11 socket.
            code = """import json, os
from pathlib import Path
home = Path(os.environ['HOME'])
print(json.dumps({'homeExists': home.is_dir(), 'mode': home.stat().st_mode & 511,
                  'home': str(home), 'hostHomeAbsent': not Path('/home').exists(),
                  'sdkAbsent': not Path(os.environ['DOTNET_ROOT']).exists() and not (home / '.dotnet/sdk').exists(),
                  'nugetAbsent': not Path(os.environ['NUGET_PACKAGES']).exists() and not (home / '.nuget/packages').exists()}))
"""
            result = subprocess.run([*command, '/usr/bin/python3', '-c', code], check=True, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
            self.assertEqual(dict(homeExists=True, mode=0o700, home=str(home), hostHomeAbsent=True,
                                  sdkAbsent=True, nugetAbsent=True), json.loads(result.stdout))



class PluginTemporaryRootTests(unittest.TestCase):
    def test_both_phase_children_use_owned_root_parent_without_changing_profiles(self):
        # Mock the process boundary: no DBus/GUI/apphost process is started.
        with tempfile.TemporaryDirectory(prefix='meowchan-workflow-tmpdir-test-') as temporary:
            root = Path(temporary).resolve()
            (root / '.workflow-owner').write_text('a' * 32)
            def phase_environment(root, phase, profile, isolated, scale):
                working = root / ('cwd-' + phase)
                working.mkdir()
                return working, {'TMPDIR': '/wrong-parent', 'MEOWCHAN_PROFILE': str(profile), 'XDG_RUNTIME_DIR': str(working / 'runtime')}
            process = Mock()
            process.wait.return_value = 2
            with patch.object(runner.workflow, 'phase_environment', side_effect=phase_environment), \
                    patch.object(runner, 'sandbox', side_effect=lambda *args: ['bwrap', '--smoke-plugin-workflow']), \
                    patch.object(runner.subprocess, 'Popen', return_value=process) as popen, \
                    patch.object(runner.desktop, 'stop_owned_processes'):
                for phase in ('download', 'restore'):
                    runner.run_phase(root / 'package/MeowChan', root, phase, root / ('evidence-' + phase), 1, 1, {})
                    if phase == 'download':
                        (root / 'profile-download').mkdir()  # Stand in for the mocked app's first profile.
            self.assertEqual(2, popen.call_count)
            for call, phase in zip(popen.call_args_list, ('download', 'restore')):
                environment = call.kwargs['env']
                self.assertEqual(str(root.parent), environment['TMPDIR'])
                self.assertEqual(str(root / ('profile-' + phase)), environment['MEOWCHAN_PROFILE'])
                self.assertEqual(str(root / ('cwd-' + phase) / 'runtime'), environment['XDG_RUNTIME_DIR'])
                self.assertIn('--smoke-plugin-workflow', call.args[0])
                self.assertEqual(str(root / (phase + '.json')), call.args[0][-1])


def managed_pe(mvid):
    # Minimal deterministic PE/CLI metadata image, never executed by these tests.
    data = bytearray(1024)
    def pack(fmt, offset, *values): struct.pack_into(fmt, data, offset, *values)
    data[:2] = b'MZ'
    pack('<I', 0x3c, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    pack('<H', 0x86, 1)
    pack('<H', 0x94, 224)
    pack('<H', 0x98, 0x10b)
    pack('<II', 0x98 + 96 + 14 * 8, 0x2000, 72)
    pack('<IIII', 0x98 + 224 + 8, 512, 0x2000, 512, 512)
    pack('<II', 512 + 8, 0x2080, 256)
    metadata = 640
    data[metadata:metadata + 4] = b'BSJB'
    pack('<I', metadata + 12, 4)
    data[metadata + 16:metadata + 20] = b'v4\0\0'
    pack('<HH', metadata + 20, 0, 2)
    pack('<II', metadata + 24, 64, 40)
    data[metadata + 32:metadata + 36] = b'#~\0\0'
    pack('<II', metadata + 36, 112, 16)
    data[metadata + 44:metadata + 52] = b'#GUID\0\0\0'
    pack('<Q', metadata + 64 + 8, 1)
    pack('<I', metadata + 64 + 24, 1)
    pack('<HHHHH', metadata + 64 + 28, 0, 0, 1, 0, 0)
    data[metadata + 112:metadata + 128] = uuid.UUID(mvid).bytes_le
    return bytes(data)


def bundle_bytes(entries, compressed=False):
    def text(value):
        value = value.encode()
        return bytes([len(value)]) + value  # all fixture names are shorter than 128 bytes
    data = bytearray(b'HOST1234' + bytes(8) + runner.BUNDLE_SIGNATURE)
    records = []
    for name, kind, raw in entries:
        payload = zlib.compress(raw)[2:-4] if compressed and kind == 1 else raw
        records.append((name, kind, len(data), len(raw), len(payload) if compressed and kind == 1 else 0))
        data += payload
    header = len(data)
    struct.pack_into('<q', data, 8, header)
    data += struct.pack('<IIi', 6, 0, len(records)) + text('fixture-id')
    deps = next((record for record in records if record[1] == 3), ('', 0, 0, 0, 0))
    config = next((record for record in records if record[1] == 4), ('', 0, 0, 0, 0))
    data += struct.pack('<qqqqQ', deps[2], deps[3], config[2], config[3], 0)
    for name, kind, offset, size, packed in records:
        data += struct.pack('<qqqB', offset, size, packed, kind) + text(name)
    return bytes(data)


class PackageAssemblyBindingTests(unittest.TestCase):
    MVID = '12ab34cd-1234-4567-8901-123456789abc'

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'package'
        self.root.mkdir()
        self.host = self.root / 'MeowChan'
        self.pe = managed_pe(self.MVID)
        self.entries = [('MeowChan.dll', 1, self.pe), ('libmeowchan.dll', 1, self.pe),
                        ('MeowChan.deps.json', 3, b'{}'), ('MeowChan.runtimeconfig.json', 4, b'{}')]

    def closure(self, bundled=True, compressed=False):
        self.host.write_bytes(bundle_bytes(self.entries, compressed) if bundled else b'unbundled apphost')
        if not bundled:
            for name, _, payload in self.entries: (self.root / name).write_bytes(payload)
        archive = self.root.parent / 'candidate.zip'
        with zipfile.ZipFile(archive, 'w') as target:
            for path in self.root.iterdir(): target.write(path, path.name)
        return runner.plugin_archive_closure(archive, self.root, 'MeowChan', self.host)

    def metrics(self, bundled=True):
        return dict(processPath=str(self.host), processSha256=runner.workflow.sha256(self.host), appBaseDirectory=str(self.root),
                    managedAssemblies=[dict(name=name, path='' if bundled else str(self.root / (name + '.dll')),
                                            sha256=None if bundled else runner.hashlib.sha256(self.pe).hexdigest(), moduleVersionId=self.MVID)
                                       for name in sorted(runner.REQUIRED_ASSEMBLIES)])

    def test_actual_module_mvid_is_decoded(self):
        self.assertEqual(self.MVID, runner.module_version_id(self.pe))
        with self.assertRaises(ValueError): runner.module_version_id(self.pe[:660])

    def test_singlefile_complete_closure_and_loaded_bundle_identity(self):
        closure = self.closure()
        bindings = runner.validate_assembly_bindings(self.metrics(), closure)
        self.assertEqual({'bundle-entry-mvid-and-archived-apphost'}, {row['binding'] for row in bindings})
        self.assertEqual(4, len(closure['bundle']['entries']))
        # Auxiliary payload mutation is rejected by the full archive closure too.
        (self.root / 'unexpected.dll').write_bytes(b'foreign')
        with self.assertRaises(ValueError): runner.workflow.verify_extracted_closure(self.root, closure)

    def test_compressed_bundle_assemblies_are_bound(self):
        closure = self.closure(compressed=True)
        self.assertEqual(2, len(runner.validate_assembly_bindings(self.metrics(), closure)))
        self.assertTrue(all(row['compressedBytes'] for row in closure['bundle']['entries'] if row['type'] == 1))

    def test_unbundled_delivery_still_requires_and_binds_external_dlls(self):
        closure = self.closure(bundled=False)
        self.assertIsNone(closure['bundle'])
        self.assertEqual(2, len(runner.validate_assembly_bindings(self.metrics(bundled=False), closure)))
        with self.assertRaises(ValueError): runner.validate_assembly_bindings(self.metrics(), closure)

    def test_missing_bundle_assembly_or_runtime_metadata_fails(self):
        for name in ('libmeowchan.dll', 'MeowChan.runtimeconfig.json'):
            self.host.write_bytes(bundle_bytes([row for row in self.entries if row[0] != name]))
            with self.assertRaises(ValueError): runner.bundle_inventory(self.host)

    def test_corrupt_populated_bundle_never_falls_back(self):
        raw = bytearray(bundle_bytes(self.entries))
        header, = struct.unpack_from('<q', raw, 8)
        struct.pack_into('<I', raw, header, 999)
        self.host.write_bytes(raw)
        with self.assertRaises(ValueError): runner.bundle_inventory(self.host)
        self.host.write_bytes(raw[:header + 7])
        with self.assertRaises(ValueError): runner.bundle_inventory(self.host)

    def test_loaded_mvid_location_hosthash_or_processpath_mismatch_fails(self):
        closure = self.closure()
        for change in ('moduleVersionId', 'path', 'processSha256', 'processPath'):
            metrics = self.metrics()
            if change in ('moduleVersionId', 'path'): metrics['managedAssemblies'][0][change] = 'foreign'
            else: metrics[change] = 'foreign'
            with self.assertRaises(ValueError): runner.validate_assembly_bindings(metrics, closure)
        metrics = self.metrics()
        metrics['managedAssemblies'] *= 2
        with self.assertRaises(ValueError): runner.validate_assembly_bindings(metrics, closure)

    def test_compressed_payload_corruption_fails(self):
        raw = bytearray(bundle_bytes(self.entries, compressed=True))
        raw[48:52] = b'FAIL'
        self.host.write_bytes(raw)
        with self.assertRaises((ValueError, zlib.error)): runner.bundle_inventory(self.host)

    def test_unbundled_archive_missing_runtimeconfig_fails(self):
        self.closure(bundled=False)
        (self.root / 'MeowChan.runtimeconfig.json').unlink()
        archive = self.root.parent / 'incomplete.zip'
        with zipfile.ZipFile(archive, 'w') as target:
            for path in self.root.iterdir(): target.write(path, path.name)
        with self.assertRaises(ValueError): runner.plugin_archive_closure(archive, self.root, 'MeowChan', self.host)


if __name__ == '__main__': unittest.main()
