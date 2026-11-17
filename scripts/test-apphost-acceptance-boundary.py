#!/usr/bin/env python3
"""Routing/configuration contracts using synthetic bytes; not platform acceptance."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

SCRIPTS = Path(__file__).resolve().parent


def load(filename):
    spec = importlib.util.spec_from_file_location(filename.replace('-', '_'), SCRIPTS / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


boundary = load('apphost-acceptance-boundary.py')
DEBUG = b'RunDevelopmentHost\x01\x00\x05Debug\x00\x00' + b''.join(
    value.encode('utf-16-le') for value in boundary.SWITCHES.values())
RELEASE = b'\x01\x00\x07Release\x00\x00'


class BoundaryTests(unittest.TestCase):
    def test_every_runner_dry_run_checks_its_switch_and_starts_no_ui(self):
        with tempfile.TemporaryDirectory() as temporary:
            apphost = Path(temporary) / 'MeowChan'
            apphost.write_bytes(DEBUG)
            for filename, switch in boundary.SWITCHES.items():
                with self.subTest(runner=filename):
                    runner = load(filename)
                    output = io.StringIO()
                    with mock.patch('subprocess.Popen', side_effect=AssertionError('No desktop in dry-run')), contextlib.redirect_stdout(output):
                        self.assertEqual(runner.main([str(apphost), '--dry-run']), 0)
                    proof = json.loads(output.getvalue())
                    self.assertEqual(proof['acceptanceConfiguration'], 'Debug')
                    self.assertEqual(proof['switch'], switch)
                    self.assertFalse(proof['acceptanceExecuted'])
                    self.assertFalse(proof['shippingArtifactExecuted'])
                    # Every runner must reject Release, not merely print a changed claim.
                    apphost.write_bytes(RELEASE)
                    with self.assertRaisesRegex(ValueError, 'Debug apphost'):
                        runner.main([str(apphost), '--dry-run'])
                    apphost.write_bytes(DEBUG)

    def test_configuration_and_hook_and_requested_switch_are_all_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            apphost = Path(temporary) / 'MeowChan'
            runner, switch = next(iter(boundary.SWITCHES.items()))
            for bad in (RELEASE, DEBUG.replace(b'RunDevelopmentHost', b'other'),
                        DEBUG.replace(b'\x05Debug', b'\x07Release'),
                        DEBUG.replace(switch.encode('utf-16-le'), b'other')):
                apphost.write_bytes(bad)
                with self.assertRaises(ValueError):
                    boundary.require_debug(apphost, runner)
            apphost.write_bytes(RELEASE)
            boundary.require_release(apphost)
            apphost.write_bytes(DEBUG)
            with self.assertRaises(ValueError):
                boundary.require_release(apphost)

    def test_release_rejects_even_unregistered_smoke_switches(self):
        for payload in (RELEASE + b'RunDevelopmentHost',
                        RELEASE + '--smoke-future-probe'.encode('utf-16-le')):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                boundary.require_release_payload(payload)

    def test_thin_mac_host_is_not_enough_product_payload_must_match(self):
        with tempfile.TemporaryDirectory() as temporary:
            contents = Path(temporary) / 'MeowChan.app/Contents'
            native = contents / 'MacOS/MeowChan'
            managed = contents / 'Resources/application/MeowChan.dll'
            native.parent.mkdir(parents=True)
            managed.parent.mkdir(parents=True)
            native.write_bytes(DEBUG)  # must not hide a Release managed payload
            managed.write_bytes(RELEASE)
            with self.assertRaises(ValueError):
                boundary.require_debug(native, 'macos-apphost-smoke.py')
            managed.write_bytes(DEBUG)
            boundary.require_debug(native, 'macos-apphost-smoke.py')

    def test_apprun_inspects_the_actual_product(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'AppRun').write_text('launcher')
            product = root / 'usr/lib/meowchan/MeowChan'
            product.parent.mkdir(parents=True)
            product.write_bytes(DEBUG)
            boundary.require_debug(root / 'AppRun', 'apphost-workflow-smoke.py')

    def test_workflow_routes_every_in_app_probe_to_acceptance_paths(self):
        text = (SCRIPTS.parent / '.github/workflows/dotnet-desktop.yml').read_text()
        direct = [line for line in text.splitlines() if 'run:' in line and any(
            filename in line for filename in boundary.SWITCHES)]
        self.assertEqual(len(direct), 5)  # macOS startup/playback/workflow; Windows playback/workflow
        for line in direct:
            self.assertIn('artifacts/acceptance-', line)
        for platform in ('macos', 'media', 'linux'):
            self.assertIn(f'artifacts/acceptance-{platform}-build', text)
            self.assertIn(f'artifacts/{platform}-publish', text)
        self.assertIn('--layout-only --package-dir artifacts/linux-packages', text)
        self.assertIn('--package-dir artifacts/acceptance-linux-packages', text)
        self.assertIn('boundary.validate_macos_release(', text)
        self.assertIn("output / 'evidence' / rid / 'debug-acceptance'", text)

    def test_linux_layout_only_is_forwarded_but_default_remains_full_acceptance(self):
        runner = load('linux-distribution-gates.py')
        args = mock.Mock(package_dir=Path('/packages'), manifest=Path('/packages/linux-x64-package-manifest.json'),
                         source_commit='a' * 40, runtime='linux-x64', scenario='bundled-vlc', layout_only=True)
        # This command-construction contract also runs in the Windows packaging
        # job, where POSIX identity APIs do not exist. No Docker is executed.
        with mock.patch.object(runner.os, 'getuid', return_value=1000, create=True), \
             mock.patch.object(runner.os, 'getgid', return_value=1000, create=True):
            cmd = runner.docker_command(args, 'sha256:' + 'b' * 64, 'owned', 'c' * 32,
                                        Path('/work'), Path('/evidence'), Path('/scripts'), Path('/fixtures'))
            self.assertIn('--layout-only', cmd)
            self.assertEqual('1000:1000', cmd[cmd.index('--user') + 1])
            args.layout_only = False
            self.assertNotIn('--layout-only', runner.docker_command(args, 'image', 'owned', 'c' * 32,
                             Path('/work'), Path('/evidence'), Path('/scripts'), Path('/fixtures')))

    def test_release_macos_binding_is_independent_of_debug_acceptance(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            packages, evidence = root / 'packages', root / 'evidence'
            packages.mkdir(); evidence.mkdir()
            dmg = packages / 'MeowChan-1.0-osx-arm64.dmg'
            archive = packages / 'MeowChan-1.0-osx-arm64.zip'
            dmg.write_bytes(b'release DMG')
            host = b'release native host'
            files = {'Contents/MacOS/MeowChan': __import__('hashlib').sha256(host).hexdigest(),
                     'Contents/Resources/application/MeowChan.dll': __import__('hashlib').sha256(RELEASE).hexdigest()}
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('MeowChan.app/Contents/MacOS/MeowChan', host)
                package.writestr('MeowChan.app/Contents/Resources/application/MeowChan.dll', RELEASE)
            signatures = dict(runtime='osx-arm64', files=files, signatures_verified=True,
                              dependency_closure=True, coreclr_entitlements_verified=True)
            report = dict(runtime='osx-arm64', dmg=dmg.name, dmgSha256=boundary.sha256(dmg),
                          mountedAndCopied=True, applicationsShortcut='/Applications',
                          sourceSignatures={'files': files}, copiedSignatures={'files': files},
                          apphostSha256=files['Contents/MacOS/MeowChan'])
            for name, value in [('source.txt', 'a' * 40), ('version.txt', '1.0'),
                                ('archive.sha256', boundary.sha256(dmg))]:
                (evidence / name).write_text(value)
            (evidence / 'signatures.json').write_text(json.dumps(signatures))
            (evidence / 'dmg.json').write_text(json.dumps(report))
            _, proof = boundary.validate_macos_release(packages, evidence, '1.0', 'osx-arm64', 'a' * 40)
            self.assertEqual(proof['configuration'], 'Release')
            self.assertFalse(proof['executionClaim'])
            for key, value in [('mountedAndCopied', False), ('apphostSha256', '0' * 64),
                               ('applicationsShortcut', '/wrong')]:
                with self.subTest(key=key):
                    (evidence / 'dmg.json').write_text(json.dumps(dict(report, **{key: value})))
                    with self.assertRaises(ValueError):
                        boundary.validate_macos_release(packages, evidence, '1.0', 'osx-arm64', 'a' * 40)


if __name__ == '__main__':
    unittest.main()
