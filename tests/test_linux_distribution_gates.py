"""Tiny fixtures only: runner safety tests are not distribution acceptance proof."""
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('linux_distribution_gates', ROOT / 'scripts/linux-distribution-gates.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)
SOURCE = 'a' * 40


def digest(data):
    return hashlib.sha256(data).hexdigest()


def elf(runtime='linux-x64'):
    header = bytearray(64)
    header[:6] = b'\x7fELF\x02\x01'
    struct.pack_into('<H', header, 18, runner.MACHINES[runtime][1])
    return bytes(header)


def write_tar(path, files, extra=()):
    with tarfile.open(path, 'w:gz') as archive:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size, info.mode = len(data), 0o755 if name == 'MeowChan' else 0o644
            archive.addfile(info, io.BytesIO(data))
        for entry, content in extra:
            archive.addfile(entry, io.BytesIO(content) if content is not None else None)


def fixture_packages(directory, runtime='linux-x64'):
    files = {'MeowChan': elf(runtime), 'linux-video-runtime.json': b'{}',
             f'libvlc/{runtime}/lib/libvlc.so.5': b'fixture library'}
    artifacts = []
    for flavor in ('bundled-vlc', 'system-vlc', 'appimage'):
        appimage = flavor == 'appimage'
        payload = {('usr/lib/meowchan/' + key if appimage else key): value for key, value in files.items()}
        if flavor == 'system-vlc':
            payload = {key: value for key, value in payload.items() if not key.startswith('libvlc/')}
        if appimage:
            payload['AppRun'] = b'#!/bin/sh\nexec "$(dirname "$0")/usr/lib/meowchan/MeowChan" "$@"\n'
        name = 'MeowChan-0.9.0-' + runtime + ('.AppImage' if appimage else '-' + flavor + '.tar.gz')
        path = directory / name
        if appimage:
            path.write_bytes(elf(runtime) + b'tiny nonlaunchable image fixture')
        else:
            write_tar(path, payload)
        path.chmod(0o755)
        artifacts.append({'file': name, 'flavor': flavor, 'bundled': flavor != 'system-vlc',
                          'apphost_member': 'usr/lib/meowchan/MeowChan' if appimage else 'MeowChan',
                          'payload_files': {key: digest(value) for key, value in payload.items()},
                          'sha256': runner.sha256(path), 'bytes': path.stat().st_size})
    value = {'schema': 'meowchan.linux-packages.v1', 'runtime': runtime, 'source_commit': SOURCE,
             'version': '0.9.0', 'apphost_sha256': digest(elf(runtime)), 'artifacts': artifacts}
    path = directory / (runtime + '-package-manifest.json')
    runner.write_json(path, value)
    return path, value


def workflow_report():
    return {'passed': True, 'completed': True, 'isolatedDesktop': True, 'apphostSha256': 'b' * 64,
            'declaredRuntime': 'linux-x64', 'declaredSourceCommit': SOURCE, 'selectedScales': [1, 2],
            'fullScaleCoverage': True, 'scales': [
                {'scale': scale, 'passed': True, 'phases': [
                    {'phase': phase, 'passed': True, 'assemblyInformationalVersion': '0.9.0+' + SOURCE}
                    for phase in ('download', 'restore', 'removed', 'shutdown')]} for scale in (1, 2)]}


class ManifestTests(unittest.TestCase):
    def test_binds_source_runtime_three_flavors_and_every_artifact_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path, value = fixture_packages(root)
            self.assertEqual(runner.load_manifest(path, root, 'linux-x64', SOURCE), value)
            for field, bad in [('schema', 'other'), ('source_commit', 'b' * 40), ('runtime', 'linux-arm64'),
                               ('apphost_sha256', 'b' * 64), ('version', '../../escape')]:
                mutated = copy.deepcopy(value)
                mutated[field] = bad
                runner.write_json(path, mutated)
                with self.subTest(field=field), self.assertRaises(ValueError):
                    runner.load_manifest(path, root, 'linux-x64', SOURCE)
            runner.write_json(path, value)
            archive = root / value['artifacts'][0]['file']
            archive.write_bytes(archive.read_bytes() + b'changed')
            with self.assertRaisesRegex(ValueError, 'hash/size'):
                runner.load_manifest(path, root, 'linux-x64', SOURCE)

    def test_rejects_duplicate_flavors_noncanonical_paths_and_apphost_mismatch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path, good = fixture_packages(root)
            mutations = []
            value = copy.deepcopy(good)
            value['artifacts'][1] = value['artifacts'][0]
            mutations.append(value)
            for name in ('../escape', '/absolute', './alias', 'bad\\path', 'a:b'):
                value = copy.deepcopy(good)
                value['artifacts'][0]['payload_files'][name] = 'b' * 64
                mutations.append(value)
            value = copy.deepcopy(good)
            value['artifacts'][0]['payload_files']['MeowChan'] = 'b' * 64
            mutations.append(value)
            value = copy.deepcopy(good)
            value['artifacts'][0]['payload_files']['MeowChan/child'] = 'b' * 64
            mutations.append(value)
            for value in mutations:
                runner.write_json(path, value)
                with self.subTest(value=value), self.assertRaises(ValueError):
                    runner.load_manifest(path, root, 'linux-x64', SOURCE)

    def test_duplicate_json_keys_and_artifact_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path, good = fixture_packages(root)
            path.write_text('{"schema":1,"schema":2}')
            with self.assertRaisesRegex(ValueError, 'Duplicate JSON'):
                runner.read_json(path)
            runner.write_json(path, good)
            archive = root / good['artifacts'][0]['file']
            target = archive.with_suffix('.actual')
            archive.rename(target)
            archive.symlink_to(target)
            with self.assertRaisesRegex(ValueError, 'hash/size'):
                runner.load_manifest(path, root, 'linux-x64', SOURCE)

    def test_native_and_elf_architectures_must_match(self):
        runner.native_runtime('linux-x64', 'x86_64')
        runner.native_runtime('linux-arm64', 'aarch64')
        with self.assertRaisesRegex(ValueError, 'native'):
            runner.native_runtime('linux-arm64', 'x86_64')
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'MeowChan'
            path.write_bytes(elf())
            self.assertEqual(runner.elf_machine(path, 'linux-x64'), 62)
            with self.assertRaisesRegex(ValueError, 'architecture'):
                runner.elf_machine(path, 'linux-arm64')
            path.write_bytes(b'#!not-ELF')
            with self.assertRaisesRegex(ValueError, 'ELF64'):
                runner.elf_machine(path, 'linux-x64')


class ExtractionTests(unittest.TestCase):
    def test_closed_preflight_then_safe_extraction_and_exact_hash_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _, value = fixture_packages(root)
            artifact = value['artifacts'][0]
            payload = root / 'payload'
            runner.extract_tar(root / artifact['file'], artifact, payload)
            proof = runner.verify_payload(payload, artifact, 'linux-x64')
            self.assertEqual(proof['files'], artifact['payload_files'])
            self.assertTrue(os.access(payload / 'MeowChan', os.X_OK))
            (payload / 'unrelated').write_text('unexpected')
            with self.assertRaisesRegex(ValueError, 'differs'):
                runner.verify_payload(payload, artifact, 'linux-x64')

    def test_rejects_traversal_links_special_entries_duplicates_and_nested_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files = {'MeowChan': elf()}
            artifact = {'payload_files': {key: digest(value) for key, value in files.items()}}
            entries = []
            for name in ('../escape', '/escape', 'bad\\name', 'MeowChan/child', './MeowChan'):
                info = tarfile.TarInfo(name)
                info.size = 1
                entries.append((info, b'x'))
            for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE, tarfile.CHRTYPE):
                info = tarfile.TarInfo('unsafe')
                info.type, info.linkname = kind, '../outside'
                entries.append((info, None))
            for index, bad in enumerate(entries):
                path = root / f'bad-{index}.tar.gz'
                write_tar(path, files, [bad])
                destination = root / f'payload-{index}'
                with self.subTest(name=bad[0].name, kind=bad[0].type), self.assertRaises(ValueError):
                    runner.extract_tar(path, artifact, destination)
                self.assertFalse(destination.exists(), 'preflight must happen before extraction')
            self.assertFalse((root.parent / 'escape').exists())

    def test_rejects_limits_and_second_pass_mutation_before_unbounded_write(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _, value = fixture_packages(root)
            artifact = value['artifacts'][0]
            archive = root / artifact['file']
            with mock.patch.object(runner, 'MAX_FILE', 20), self.assertRaisesRegex(ValueError, 'byte limit'):
                runner.inspect_tar(archive, artifact)
            expected = runner.inspect_tar(archive, artifact)
            malicious = {'MeowChan': elf() + b'grown'}
            write_tar(archive, malicious)
            with mock.patch.object(runner, 'inspect_tar', return_value=expected), self.assertRaisesRegex(ValueError, 'changed'):
                runner.extract_tar(archive, artifact, root / 'payload')
            self.assertFalse((root / 'payload/MeowChan').exists())

    def test_never_follows_links_in_extracted_payload_or_reuses_destination(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _, value = fixture_packages(root)
            artifact = value['artifacts'][0]
            payload = root / 'payload'
            runner.extract_tar(root / artifact['file'], artifact, payload)
            with self.assertRaisesRegex(ValueError, 'fresh'):
                runner.extract_tar(root / artifact['file'], artifact, payload)
            (payload / 'linked').symlink_to('/etc/passwd')
            with self.assertRaisesRegex(ValueError, 'links'):
                runner.payload_inventory(payload)

    def test_damaged_bundle_removes_exactly_one_declared_library(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _, value = fixture_packages(root)
            artifact = value['artifacts'][0]
            payload = root / 'payload'
            runner.extract_tar(root / artifact['file'], artifact, payload)
            damage = runner.damage_bundle(payload, artifact, 'linux-x64')
            self.assertEqual(damage['path'], 'libvlc/linux-x64/lib/libvlc.so.5')
            self.assertEqual(runner.sha256(payload / 'MeowChan'), artifact['payload_files']['MeowChan'])
            self.assertEqual(len(runner.payload_inventory(payload)['files']), len(artifact['payload_files']) - 1)


class ContainerTests(unittest.TestCase):
    def test_docker_command_has_only_owned_writable_mounts_and_no_host_session(self):
        args = SimpleNamespace(package_dir=Path('/candidate/packages'), manifest=Path('/candidate/packages/linux-x64-package-manifest.json'),
                               source_commit=SOURCE, runtime='linux-x64', scenario='appimage')
        command = runner.docker_command(args, 'sha256:' + 'b' * 64, 'owned-name', 'c' * 32,
                                        Path('/owned/work'), Path('/owned/evidence'), Path('/candidate/scripts'), Path('/candidate/fixtures'))
        for flag in ('--pull=never', '--network=none', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges'):
            self.assertIn(flag, command)
        mounts = [command[index + 1] for index, value in enumerate(command) if value == '--mount']
        self.assertEqual(len(mounts), 5)
        self.assertEqual([mount for mount in mounts if not mount.endswith(',readonly')],
                         ['type=bind,source=/owned/work,target=/work', 'type=bind,source=/owned/evidence,target=/evidence'])
        text = ' '.join(command)
        for forbidden in ('/var/run/docker.sock', '/home/', 'DISPLAY=', 'DBUS_SESSION_BUS_ADDRESS=', '--privileged', '--network=host'):
            self.assertNotIn(forbidden, text)
        self.assertIn('HOME=/work/home', command)
        self.assertIn('TMPDIR=/work/tmp', command)
        self.assertEqual(command[command.index('--user') + 1], f'{os.getuid()}:{os.getgid()}')

    def test_docker_requires_local_engine_and_native_image_and_records_immutable_id(self):
        responses = [[{'Endpoints': {'docker': {'Host': 'unix:///run/docker.sock'}}}],
                     {'Architecture': 'x86_64'},
                     [{'Os': 'linux', 'Architecture': 'amd64', 'Id': 'sha256:' + 'b' * 64}]]
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(runner, 'docker_json', side_effect=responses):
            value = runner.local_docker('linux-x64', {'no-vlc': 'candidate:no-vlc'})
        self.assertEqual(value['images']['no-vlc']['id'], 'sha256:' + 'b' * 64)
        with mock.patch.dict(os.environ, {'DOCKER_HOST': 'tcp://remote:2375'}, clear=True), self.assertRaisesRegex(ValueError, 'local'):
            runner.local_docker('linux-x64', {})
        with (mock.patch.dict(os.environ, {'DOCKER_CONTEXT': 'remote', 'DOCKER_HOST': 'unix:///run/docker.sock'}, clear=True),
              mock.patch.object(runner, 'docker_json', return_value=[{'Endpoints': {'docker': {'Host': 'tcp://remote:2375'}}}]) as query,
              self.assertRaisesRegex(ValueError, 'local')):
            runner.local_docker('linux-x64', {})
        query.assert_called_once_with(['context', 'inspect', 'remote'])
        responses[-1][0]['Architecture'] = 'arm64'
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(runner, 'docker_json', side_effect=responses), self.assertRaisesRegex(ValueError, 'native'):
            runner.local_docker('linux-x64', {'no-vlc': 'candidate:no-vlc'})

    def test_cleanup_checks_ownership_and_does_not_treat_engine_failure_as_absence(self):
        other = SimpleNamespace(returncode=0, stdout=json.dumps({'meowchan.owner': 'other'}))
        with mock.patch.object(runner.subprocess, 'run', return_value=other) as calls, self.assertRaisesRegex(ValueError, 'ownership|owner token'):
            runner.remove_owned_container('owned-name', 'a' * 32)
        self.assertEqual(calls.call_count, 1)
        failed = SimpleNamespace(returncode=1, stdout='')
        with mock.patch.object(runner.subprocess, 'run', side_effect=[failed, subprocess.CalledProcessError(1, 'docker ps')]), self.assertRaises(subprocess.CalledProcessError):
            runner.remove_owned_container('owned-name', 'a' * 32)
        with mock.patch.object(runner.subprocess, 'run', side_effect=[failed, SimpleNamespace(stdout='')]):
            self.assertTrue(runner.remove_owned_container('owned-name', 'a' * 32))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'work'
            root.mkdir()
            (root / '.owner').write_text('a' * 32)
            for token, removed in [('a' * 32, False), ('b' * 32, True)]:
                with self.assertRaisesRegex(ValueError, 'retaining'):
                    runner.cleanup_work(root, token, removed)
                self.assertTrue(root.exists())
            runner.cleanup_work(root, 'a' * 32, True)
            self.assertFalse(root.exists())

    def test_system_vlc_assertions_are_not_satisfied_by_wrong_container(self):
        empty = {'packages': [], 'loaderCache': [], 'physicalLibraries': []}
        runner.assert_vlc(empty, False)
        with self.assertRaisesRegex(ValueError, 'no installed'):
            runner.assert_vlc(empty, True)
        for key in empty:
            value = copy.deepcopy(empty)
            value[key] = ['libvlc.so.5']
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'contains'):
                runner.assert_vlc(value, False)

    def test_no_evidence_written_after_environment_ownership_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            evidence = Path(temporary)
            args = SimpleNamespace(scenario='bundled-vlc', source_commit=SOURCE, runtime='linux-x64', owner='a' * 32,
                                   work=evidence, evidence=evidence)
            with (mock.patch.object(runner, 'validate_container_environment', side_effect=ValueError('unowned')),
                    mock.patch.object(runner, 'system_vlc_inventory', return_value={'packages': [], 'loaderCache': [], 'physicalLibraries': []}),
                    contextlib.redirect_stdout(io.StringIO())):
                self.assertEqual(runner.container_main(args), 1)
            self.assertFalse((evidence / 'scenario.json').exists())


class ReportAndProcessTests(unittest.TestCase):
    def test_requires_every_scale_phase_and_actual_build_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'workflow.json'
            good = workflow_report()
            runner.write_json(path, good)
            runner.validate_positive_report(path, 'workflow', 'linux-x64', SOURCE, 'b' * 64, '0.9.0')
            mutations = []
            for field, value in [('passed', False), ('isolatedDesktop', False), ('fullScaleCoverage', False),
                                 ('selectedScales', [1]), ('apphostSha256', 'c' * 64), ('declaredSourceCommit', 'b' * 40)]:
                report = copy.deepcopy(good)
                report[field] = value
                mutations.append(report)
            report = copy.deepcopy(good)
            report['scales'][1]['phases'].pop()
            mutations.append(report)
            report = copy.deepcopy(good)
            report['scales'][0]['phases'][0]['assemblyInformationalVersion'] = '0.9.0+' + 'b' * 40
            mutations.append(report)
            report = copy.deepcopy(good)
            report['scales'][0]['phases'][0]['assemblyInformationalVersion'] = '0.8.0+' + SOURCE
            mutations.append(report)
            for report in mutations:
                runner.write_json(path, report)
                with self.subTest(report=report), self.assertRaises(ValueError):
                    runner.validate_positive_report(path, 'workflow', 'linux-x64', SOURCE, 'b' * 64, '0.9.0')

    def test_command_timeout_failure_and_bounded_output_never_count_as_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            good = runner.run_command([sys.executable, '-c', 'print("fixture")'], root / 'good.log', timeout=5)
            self.assertTrue(good['passed'])
            timed = runner.run_command([sys.executable, '-c', 'import time; time.sleep(20)'], root / 'timeout.log', timeout=.1)
            self.assertTrue(timed['timedOut'])
            self.assertFalse(timed['passed'])
            failed = runner.run_command([sys.executable, '-c', 'raise SystemExit(2)'], root / 'exit.log', timeout=5)
            self.assertEqual(failed['exitCode'], 2)
            with mock.patch.object(runner, 'MAX_LOG', 10):
                output = runner.run_command([sys.executable, '-c', 'import time; print("x"*100, flush=True); time.sleep(10)'],
                                            root / 'output.log', timeout=5)
                fast = runner.run_command([sys.executable, '-c', 'print("x"*100)'], root / 'fast.log', timeout=5)
            self.assertFalse(output['passed'])
            self.assertIn('output', output['error'])
            self.assertFalse(fast['passed'])
            self.assertIn('output', fast['error'])
            self.assertEqual((root / 'fast.log').stat().st_size, 10)
            self.assertEqual((root / 'output.log').stat().st_size, 10)

    @unittest.skipUnless(sys.platform == 'linux' and platform.machine().lower() in ('x86_64', 'aarch64', 'amd64', 'arm64'), 'Linux /proc proof')
    def test_observes_real_owned_executable_bytes_not_just_launcher_string(self):
        runtime = 'linux-x64' if platform.machine().lower() in ('x86_64', 'amd64') else 'linux-arm64'
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            root = work / 'payload'
            root.mkdir()
            apphost = root / 'MeowChan'
            shutil.copy2(shutil.which('sleep'), apphost)
            artifact = {'apphost_member': 'MeowChan', 'payload_files': {'MeowChan': runner.sha256(apphost)}}
            process = subprocess.Popen([str(apphost), '5'], start_new_session=True)
            observed = {}
            try:
                runner.observe_apphosts(process.pid, observed, artifact, runtime, work, root)
                self.assertEqual(observed[process.pid]['exeSha256'], artifact['payload_files']['MeowChan'])
                self.assertTrue(observed[process.pid]['brieflyStoppedForPayloadHash'])
                self.assertEqual(observed[process.pid]['payloadRoot'], str(root))
            finally:
                runner.stop_process(process)

    def test_disk_reserve_fails_before_spending_reserved_bytes(self):
        with mock.patch.object(runner.shutil, 'disk_usage', return_value=SimpleNamespace(free=runner.RESERVE + 10)):
            runner.disk_reserve('/owned', additional=10)
            with self.assertRaisesRegex(ValueError, '2 GiB'):
                runner.disk_reserve('/owned', additional=11)


if __name__ == '__main__':
    unittest.main()
