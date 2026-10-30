"""Mock-only tests: never contact audio services or the user's session."""
import importlib.util
import json
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('private_audio', Path(__file__).resolve().parents[1]/'scripts/with-private-null-audio.py')
audio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audio)


class PrivateAudioTests(unittest.TestCase):
    def test_cli(self):
        args = audio.cli(['--report', 'out.json', '--', 'test', '--flag'])
        self.assertEqual(args.command, ['test', '--flag'])
        with self.assertRaises(SystemExit):
            audio.cli(['--report', 'out.json'])

    def test_isolation_absolute_pulse(self):
        with tempfile.TemporaryDirectory() as tmp, patch.dict(audio.os.environ, {
            'HOME': '/user', 'DISPLAY': ':0', 'DBUS_SESSION_BUS_ADDRESS': 'user',
            'PULSE_SERVER': 'user', 'ALSA_CARD': '0', 'LD_PRELOAD': 'bad',
            'DBUS_SYSTEM_BUS_ADDRESS': 'unix:path=/host/system_bus_socket'}):
            env = audio.environment(Path(tmp))
            for key in ('DISPLAY', 'DBUS_SESSION_BUS_ADDRESS', 'DBUS_SYSTEM_BUS_ADDRESS',
                        'ALSA_CARD', 'LD_PRELOAD'):
                self.assertNotIn(key, env)
            self.assertTrue(env['PULSE_SERVER'].startswith('unix:/'))
            self.assertEqual(env['PULSE_SOCKET'], str(Path(tmp)/'runtime/pulse/native'))
            self.assertEqual(Path(env['PULSE_COOKIE']).stat().st_mode & 0o777, 0o600)
            config = (Path(tmp)/'config/pulse/private.pa').read_text()
            self.assertEqual(config.count('load-module '), 2)
            self.assertIn('auth-anonymous=no', config)
            self.assertNotIn('udev', config)
            self.assertNotIn('alsa', config)
            self.assertEqual(env['PIPEWIRE_REMOTE'], 'disabled-private-no-server')
            self.assertEqual(Path(env['PIPEWIRE_RUNTIME_DIR']), Path(tmp)/'runtime')
            self.assertEqual((Path(tmp)/'config/pulse/client.conf').read_text(), 'autospawn = no\n')

    def check_proof(self, sinks=None, modules=None, sources=None, cards=None, default=None, short_modules=None):
        values = [sinks if sinks is not None else [
                      {'name': audio.SINK, 'mute': True, 'owner_module': 1,
                       'driver': 'module-null-sink.c'}],
                  sources if sources is not None else [
                      {'name': audio.SINK+'.monitor', 'owner_module': 1}],
                  modules if modules is not None else [
                      {'name': 'module-null-sink', 'argument': 'sink_name='+audio.SINK,
                       'usage_counter': 0, 'properties': {}},
                      {'name': 'module-native-protocol-unix', 'argument': '',
                       'usage_counter': 0, 'properties': {}}],
                  cards if cards is not None else []]
        with patch.object(audio, 'checked', side_effect=[json.dumps(v) for v in values[:3]]+[
                '0\tmodule-native-protocol-unix\targs\n1\tmodule-null-sink\targs\n'
                if short_modules is None else short_modules, json.dumps(values[3]),
                audio.SINK if default is None else default]):
            return audio.proof({})

    def test_valid_proof(self):
        self.assertTrue(self.check_proof()['muted'])

    def test_reject_hardware(self):
        for module in ('module-udev-detect', 'module-alsa-sink',
                       'module-bluetooth-discover', 'module-detect'):
            with self.subTest(module=module), self.assertRaises(RuntimeError):
                self.check_proof(modules=[{'name': module, 'index': 2}])
        with self.assertRaises(RuntimeError):
            self.check_proof(cards=[{'name': 'hardware'}])

    def test_reject_default_driver_and_ownership(self):
        with self.assertRaises(RuntimeError):
            self.check_proof(default='other')
        with self.assertRaises(RuntimeError):
            self.check_proof(sinks=[{'name': audio.SINK, 'mute': True,
                                    'owner_module': 1, 'driver': 'module-alsa-sink.c'}])
        with self.assertRaises(RuntimeError):
            self.check_proof(sources=[{'name': audio.SINK+'.monitor', 'owner_module': 5}])

    def test_reject_short_module_names_ids_and_owner(self):
        for listing in (
            '0\tmodule-native-protocol-unix\n1\tmodule-alsa-sink\n',
            '0\tmodule-native-protocol-unix\n0\tmodule-null-sink\n',
            '0\tmodule-native-protocol-unix\nno-id\tmodule-null-sink\n',
            '0\tmodule-native-protocol-unix\n1\tmodule-null-sink\n2\tmodule-null-sink\n',
            '0\tmodule-native-protocol-unix\n',
            '0 module-native-protocol-unix\n1 module-null-sink\n',
        ):
            with self.subTest(listing=listing), self.assertRaises(RuntimeError):
                self.check_proof(short_modules=listing)
        with self.assertRaises(RuntimeError):
            self.check_proof(short_modules='0\tmodule-native-protocol-unix\n7\tmodule-null-sink\n')
        with self.assertRaises(RuntimeError):
            self.check_proof(modules=[{'name': 'module-null-sink', 'index': 7},
                                      {'name': 'module-native-protocol-unix', 'index': 0}])

    def test_reject_sink_and_source(self):
        for sinks in ([], [{'name': audio.SINK, 'mute': False}],
                      [{'name': audio.SINK, 'mute': True}]*2):
            with self.assertRaises(RuntimeError):
                self.check_proof(sinks=sinks)
        with self.assertRaises(RuntimeError):
            self.check_proof(sources=[{'name': 'hardware'}])

    def test_cleanup_only_owned_groups(self):
        p = Mock(pid=123456)
        p.wait.return_value = 0
        with patch.object(audio.os, 'killpg', side_effect=[None, None, ProcessLookupError()]) as kill:
            self.assertTrue(audio.cleanup([('owned', p)]))
        self.assertEqual([c.args for c in kill.call_args_list],
                         [(123456, signal.SIGTERM), (123456, signal.SIGKILL), (123456, 0)])

    def test_cleanup_leftover_fails(self):
        p = Mock(pid=123456)
        with patch.object(audio.os, 'killpg'):
            self.assertFalse(audio.cleanup([('owned', p)]))

    def test_child_failure_preserved_and_reported(self):
        self.execute_mock(23, True, 23)

    def test_cleanup_failure_overrides_success(self):
        self.execute_mock(0, False, 1)

    def test_sigterm_and_startup_timeout_cleanup(self):
        for error, expected in ((InterruptedError(signal.SIGTERM), 143),
                                (subprocess.TimeoutExpired('pactl', 3), 1)):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as tmp:
                report = Path(tmp)/'report.json'
                service = Mock(pid=123456)
                service.poll.return_value = None
                with patch.object(audio.subprocess, 'Popen', return_value=service), \
                     patch.object(audio.Path, 'exists', return_value=True), \
                     patch.object(audio, 'run', side_effect=error), \
                     patch.object(audio, 'cleanup', return_value=True) as cleanup:
                    self.assertEqual(audio.execute(['child'], report), expected)
                cleanup.assert_called_once()
                data = json.loads(report.read_text())
                self.assertTrue(data['cleanup']['complete'])
                self.assertIsNone(data['child_exit_code'])
                self.assertEqual(data['status'], 'failed')

    def execute_mock(self, child_code, clean, expected):
        with tempfile.TemporaryDirectory() as tmp:
            report = Path(tmp)/'report.json'
            services = [Mock(pid=100+i) for i in range(2)]
            for service in services:
                service.poll.return_value = None
            child = Mock(pid=105, returncode=child_code)
            child.poll.return_value = child_code
            bus_configs = []
            def fake_spawn(args, **kwargs):
                if args[0] == 'dbus-daemon':
                    config_path = args[2].split('=', 1)[1]
                    bus_configs.append(Path(config_path).read_text())
                return next(processes)
            processes = iter(services+[child])
            with patch.object(audio.subprocess, 'Popen', side_effect=fake_spawn) as spawn, \
                 patch.object(audio.Path, 'exists', return_value=True), \
                 patch.object(audio, 'run', return_value=subprocess.CompletedProcess([], 0, '[{}]', '')), \
                 patch.object(audio, 'checked', return_value=''), \
                 patch.object(audio, 'proof', return_value={'muted': True}), \
                 patch.object(audio, 'cleanup', return_value=clean) as cleanup:
                self.assertEqual(audio.execute(['child'], report), expected)
            import xml.etree.ElementTree as ET
            bus = ET.fromstring(bus_configs[0])
            rules = [node.attrib for node in bus.findall('policy/allow')]
            self.assertIn({'receive_sender': '*'}, rules)
            self.assertIn({'send_destination': '*'}, rules)
            self.assertIn({'own': '*'}, rules)
            self.assertTrue(bus.find('listen').text.startswith('unix:path=/tmp/ci-audio-'))
            self.assertEqual(bus.find('auth').text, 'EXTERNAL')
            bus_env = spawn.call_args_list[0].kwargs['env']
            self.assertEqual(bus_env['DBUS_SESSION_BUS_ADDRESS'],
                             bus_env['DBUS_SYSTEM_BUS_ADDRESS'])
            self.assertTrue(bus_env['DBUS_SYSTEM_BUS_ADDRESS'].startswith('unix:path=/tmp/ci-audio-'))
            pulse_args = spawn.call_args_list[1].args[0]
            self.assertEqual(pulse_args[:2], ['pulseaudio', '-n'])
            for flag in ('--daemonize=no', '--use-pid-file=no', '--exit-idle-time=-1', '--disallow-module-loading=yes'):
                self.assertIn(flag, pulse_args)
            self.assertTrue(any(a.startswith('--file=/') for a in pulse_args))
            data = json.loads(report.read_text())
            self.assertEqual(data['child_exit_code'], child_code)
            self.assertEqual(data['cleanup']['complete'], clean)
            self.assertEqual(len(cleanup.call_args.args[0]), 3)
            self.assertNotIn('cookie', report.read_text())
            self.assertEqual(set(data['services']), {'dbus', 'pulseaudio'})


if __name__ == '__main__':
    unittest.main()
