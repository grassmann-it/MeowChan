"""Mock-only tests: never contact audio services or the user's session."""
from contextlib import contextmanager
import importlib.util
import json
from pathlib import Path
import signal
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('private_audio', Path(__file__).resolve().parents[1]/'scripts/with-private-null-audio.py')
audio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audio)


class MockOnlyTestCase(unittest.TestCase):
    def setUp(self):
        # A forgotten mock must fail the test, never start a native process or
        # signal a real group. Nested scenario mocks replace these guards.
        for owner, name in ((audio.subprocess, 'Popen'),
                            (audio.subprocess, 'run'), (audio.os, 'killpg')):
            guard = patch.object(owner, name, side_effect=AssertionError(
                'unmocked process boundary: '+name))
            mocked = guard.start()
            self.addCleanup(guard.stop)
            self.addCleanup(mocked.assert_not_called)


class PrivateAudioTests(MockOnlyTestCase):
    def test_backend_detection_is_explicit_and_prefers_pulseaudio(self):
        available = {'dbus-daemon', 'pactl', 'pulseaudio', 'pipewire', 'pipewire-pulse', 'pw-dump'}
        with patch.object(audio.shutil, 'which',
                          side_effect=lambda name: '/usr/bin/'+name if name in available else None):
            self.assertEqual(audio.detect_backend(), audio.BACKEND_PULSEAUDIO)
        available.remove('pulseaudio')
        with patch.object(audio.shutil, 'which',
                          side_effect=lambda name: '/usr/bin/'+name if name in available else None):
            self.assertEqual(audio.detect_backend(), audio.BACKEND_PIPEWIRE)
        for required in ('dbus-daemon', 'pactl', 'pipewire', 'pipewire-pulse', 'pw-dump'):
            with self.subTest(missing=required), patch.object(
                    audio.shutil, 'which', side_effect=lambda name:
                    '/usr/bin/'+name if name in available and name != required else None):
                with self.assertRaises(RuntimeError):
                    audio.detect_backend()
        available.remove('pipewire-pulse')
        with patch.object(audio.shutil, 'which', return_value=None), self.assertRaises(RuntimeError):
            audio.detect_backend()

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

    def test_pipewire_environment_is_closed_and_never_loads_a_session_manager(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = audio.environment(Path(tmp), audio.BACKEND_PIPEWIRE)
            pipewire = (Path(tmp)/'pipewire.conf').read_text()
            pulse = (Path(tmp)/'pipewire-pulse.conf').read_text()
            self.assertEqual(env['PIPEWIRE_REMOTE'], 'disabled-private-no-server')
            self.assertIn('support.null-audio-sink', pipewire)
            # No session manager exists to configure ports on our behalf.
            self.assertIn('audio.adapt = audioconvert/libspa-audioconvert', pipewire)
            self.assertIn('adapter.auto-port-config = {', pipewire)
            self.assertIn('mode = dsp', pipewire)
            self.assertIn('monitor = true', pipewire)
            self.assertIn('position = preserve', pipewire)
            self.assertIn('libpipewire-module-access', pipewire)
            self.assertNotIn('session-manager', pipewire)
            self.assertNotIn('alsa', pipewire.lower())
            self.assertNotIn('wireplumber', pipewire.lower())
            self.assertIn('pulse.allow-module-loading = false', pulse)
            self.assertIn('unix:'+str(Path(tmp)/'runtime/pulse/native'), pulse)


    def test_socket_parent_exists_before_either_backend_starts(self):
        for backend in (audio.BACKEND_PULSEAUDIO, audio.BACKEND_PIPEWIRE):
            with self.subTest(backend=backend), tempfile.TemporaryDirectory() as tmp:
                env = audio.environment(Path(tmp), backend)
                parent = Path(env['PULSE_SOCKET']).parent
                self.assertEqual(parent, Path(tmp)/'runtime/pulse')
                self.assertTrue(parent.is_dir())
                self.assertFalse(parent.is_symlink())
                self.assertEqual(parent.stat().st_mode & 0o777, 0o700)

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

    def test_valid_pipewire_proof(self):
        graph = [
            {'type': 'PipeWire:Interface:Module',
             'info': {'props': {'module.name': name}}}
            for name in audio.PIPEWIRE_MODULES
        ] + [
            {'type': 'PipeWire:Interface:Node',
             'info': {'props': {'node.name': 'Dummy-Driver',
                                'factory.name': 'support.node.driver'}}},
            {'type': 'PipeWire:Interface:Node',
             'info': {'props': {'node.name': audio.SINK,
                                'factory.name': 'support.null-audio-sink',
                                'media.class': 'Audio/Sink'}}},
        ]
        with patch.object(audio, 'checked', side_effect=[
                json.dumps([{'name': audio.SINK, 'mute': True}]),
                json.dumps([{'name': audio.SINK+'.monitor'}]),
                '[]', audio.SINK, json.dumps(graph)]):
            self.assertTrue(audio.pipewire_proof({})['muted'])

    def test_pipewire_proof_rejects_device_backed_graph(self):
        graph = [
            {'type': 'PipeWire:Interface:Module',
             'info': {'props': {'module.name': name}}}
            for name in audio.PIPEWIRE_MODULES
        ] + [
            {'type': 'PipeWire:Interface:Node',
             'info': {'props': {'node.name': 'Dummy-Driver'}}},
            {'type': 'PipeWire:Interface:Node',
             'info': {'props': {'node.name': audio.SINK,
                                'factory.name': 'support.null-audio-sink',
                                'media.class': 'Audio/Sink',
                                'api.alsa.path': 'hw:0'}}},
        ]
        with patch.object(audio, 'checked', side_effect=[
                json.dumps([{'name': audio.SINK, 'mute': True}]),
                json.dumps([{'name': audio.SINK+'.monitor'}]),
                '[]', audio.SINK, json.dumps(graph)]), self.assertRaises(RuntimeError):
            audio.pipewire_proof({})

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

    def test_pipewire_execute_owns_both_daemons(self):
        self.execute_mock(0, True, 0, audio.BACKEND_PIPEWIRE)

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

    def execute_mock(self, child_code, clean, expected, backend=audio.BACKEND_PULSEAUDIO):
        with tempfile.TemporaryDirectory() as tmp:
            report = Path(tmp)/'report.json'
            service_count = 2 if backend == audio.BACKEND_PULSEAUDIO else 3
            services = [Mock(pid=100+i) for i in range(service_count)]
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
                self.assertEqual(audio.execute(['child'], report, backend), expected)
            import xml.etree.ElementTree as ET
            bus = ET.fromstring(bus_configs[0])
            rules = [node.attrib for node in bus.findall('policy/allow')]
            self.assertIn({'receive_sender': '*'}, rules)
            self.assertIn({'send_destination': '*'}, rules)
            self.assertIn({'own': '*'}, rules)
            self.assertTrue(bus.find('listen').text.startswith(
                'unix:path='+str(Path(tempfile.gettempdir()))+'/ci-audio-'))
            self.assertEqual(bus.find('auth').text, 'EXTERNAL')
            bus_env = spawn.call_args_list[0].kwargs['env']
            self.assertEqual(bus_env['DBUS_SESSION_BUS_ADDRESS'],
                             bus_env['DBUS_SYSTEM_BUS_ADDRESS'])
            self.assertTrue(bus_env['DBUS_SYSTEM_BUS_ADDRESS'].startswith(
                'unix:path='+str(Path(tempfile.gettempdir()))+'/ci-audio-'))
            if backend == audio.BACKEND_PULSEAUDIO:
                pulse_args = spawn.call_args_list[1].args[0]
                self.assertEqual(pulse_args[:2], ['pulseaudio', '-n'])
                for flag in ('--daemonize=no', '--use-pid-file=no', '--exit-idle-time=-1', '--disallow-module-loading=yes'):
                    self.assertIn(flag, pulse_args)
                self.assertTrue(any(a.startswith('--file=/') for a in pulse_args))
                expected_services = {'dbus', 'pulseaudio'}
            else:
                self.assertEqual(spawn.call_args_list[1].args[0][:2], ['pipewire', '-c'])
                self.assertEqual(spawn.call_args_list[2].args[0][:2], ['pipewire-pulse', '-c'])
                self.assertEqual(spawn.call_args_list[1].kwargs['env']['PIPEWIRE_REMOTE'], 'pipewire-0')
                self.assertEqual(spawn.call_args_list[2].kwargs['env']['PIPEWIRE_REMOTE'], 'pipewire-0')
                expected_services = {'dbus', 'pipewire', 'pipewire-pulse'}
            data = json.loads(report.read_text())
            self.assertEqual(data['child_exit_code'], child_code)
            self.assertEqual(data['cleanup']['complete'], clean)
            self.assertEqual(len(cleanup.call_args.args[0]), service_count + 1)
            self.assertNotIn('cookie', report.read_text())
            self.assertEqual(set(data['services']), expected_services)


class PipeWireContractTests(MockOnlyTestCase):
    @contextmanager
    def scenario(self, child_code=0):
        """Exercise execute/proof/cleanup; only OS process boundaries are fake.

        Socket stand-ins are ordinary files inside the test directory. No
        native executable, socket connection, process or signal is involved.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            foreign = root/'foreign-session'
            foreign.mkdir()
            sentinel = foreign/'native'
            sentinel.write_text('untouched foreign socket stand-in')
            case = SimpleNamespace()
            case.envs = []
            case.processes = {}
            case.base = None
            case.hook = lambda command: None
            case.bus_ready = True
            case.child_polls = 0
            case.stuck = set()
            case.graph = [
                {'type': 'PipeWire:Interface:Module',
                 'info': {'props': {'module.name': name}}}
                for name in sorted(audio.PIPEWIRE_MODULES)
            ] + [
                {'type': 'PipeWire:Interface:Node',
                 'info': {'props': {'node.name': 'Dummy-Driver',
                                    'factory.name': 'support.node.driver'}}},
                {'type': 'PipeWire:Interface:Node',
                 'info': {'props': {'node.name': audio.SINK,
                                    'factory.name': 'support.null-audio-sink',
                                    'media.class': 'Audio/Sink'}}},
            ]
            case.sinks = [{'name': audio.SINK, 'mute': True}]
            inherited = {key: str(foreign) for key in (
                'HOME', 'TMPDIR', 'XDG_RUNTIME_DIR', 'XDG_CONFIG_HOME',
                'XDG_CONFIG_DIRS', 'XDG_CACHE_HOME', 'XDG_STATE_HOME',
                'XDG_DATA_HOME', 'PIPEWIRE_RUNTIME_DIR', 'PIPEWIRE_REMOTE',
                'PIPEWIRE_CONFIG_DIR', 'PIPEWIRE_CONFIG_NAME', 'PULSE_SERVER',
                'PULSE_RUNTIME_PATH', 'PULSE_COOKIE', 'PULSE_CLIENTCONFIG',
                'PULSE_SINK', 'DBUS_SESSION_BUS_ADDRESS', 'DBUS_SYSTEM_BUS_ADDRESS')}

            def spawn(command, *, env, start_new_session, **kwargs):
                self.assertTrue(start_new_session)
                case.envs.append((command[0], env.copy()))
                case.hook(command)
                name = command[0]
                self.assertIn(name, ('dbus-daemon', 'pipewire', 'pipewire-pulse', 'contract-child'))
                case.base = Path(env['HOME']).parent
                if name == 'dbus-daemon' and case.bus_ready:
                    (case.base/'runtime/bus').touch()
                elif name == 'pipewire':
                    (case.base/'runtime/pipewire-0').touch()
                elif name == 'pipewire-pulse':
                    Path(env['PULSE_SOCKET']).parent.mkdir(exist_ok=True)
                    Path(env['PULSE_SOCKET']).touch()
                process = Mock(pid=100000+len(case.processes), returncode=None)
                def poll():
                    if name == 'contract-child' and process.returncode is None:
                        if case.child_polls:
                            case.child_polls -= 1
                            return None
                        process.returncode = child_code
                    return process.returncode
                def wait(timeout):
                    if name in case.stuck:
                        raise subprocess.TimeoutExpired(name, timeout)
                    if process.returncode is None:
                        process.returncode = -signal.SIGTERM
                    return process.returncode
                process.poll.side_effect = poll
                process.wait.side_effect = wait
                case.processes[name] = process
                return process

            def run(command, *, env, capture_output, text, timeout):
                self.assertTrue(capture_output and text)
                self.assertEqual(timeout, 3)
                case.envs.append((command[0], env.copy()))
                case.hook(command)
                outputs = {
                    ('pactl', '--format=json', 'list', 'sinks'): json.dumps(case.sinks),
                    ('pactl', '--format=json', 'list', 'sources'):
                        json.dumps([{'name': audio.SINK+'.monitor'}]),
                    ('pactl', '--format=json', 'list', 'cards'): '[]',
                    ('pactl', 'get-default-sink'): audio.SINK,
                    ('pactl', 'set-default-sink', audio.SINK): '',
                    ('pactl', 'set-sink-mute', audio.SINK, '1'): '',
                    ('pw-dump', '-N'): json.dumps(case.graph),
                }
                self.assertIn(tuple(command), outputs)
                return subprocess.CompletedProcess(command, 0, outputs[tuple(command)], '')

            def killpg(pid, sig):
                process = next(p for p in case.processes.values() if p.pid == pid)
                if sig == 0 and process.returncode is not None:
                    raise ProcessLookupError()

            case.report_path = root/'report.json'
            case.execute = lambda: audio.execute(
                ['contract-child'], case.report_path, audio.BACKEND_PIPEWIRE)
            handlers = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
            # The clock advances deterministically; timeout tests never sleep.
            with patch.dict(audio.os.environ, inherited), \
                 patch.object(audio.subprocess, 'Popen', side_effect=spawn) as popen, \
                 patch.object(audio.subprocess, 'run', side_effect=run), \
                 patch.object(audio.os, 'killpg', side_effect=killpg) as kill, \
                 patch.object(audio.time, 'sleep'), \
                 patch.object(audio.time, 'monotonic', side_effect=iter(range(10000))):
                case.spawn = popen
                case.kill = kill
                yield case
            for sig, handler in handlers.items():
                self.assertEqual(signal.getsignal(sig), handler)
            if case.base is not None:
                self.assertFalse(case.base.exists(), 'private socket/config tree leaked')
            self.assertEqual(sentinel.read_text(), 'untouched foreign socket stand-in')
            self.assertEqual(list(foreign.iterdir()), [sentinel])

    def assert_report(self, case, status, child_code=None, error=None, clean=True):
        report = json.loads(case.report_path.read_text())
        self.assertEqual(report['backend'], audio.BACKEND_PIPEWIRE)
        self.assertEqual(report['status'], status)
        self.assertEqual(report['child_exit_code'], child_code)
        self.assertEqual(report['cleanup']['complete'], clean)
        if error:
            self.assertEqual(report['error_type'], error)
        self.assertFalse(case.report_path.with_suffix('.json.tmp').exists())
        self.assertNotIn('secret raw output', case.report_path.read_text())
        self.assertEqual({c.args[0] for c in case.kill.call_args_list},
                         {p.pid for p in case.processes.values()})
        names = {'dbus-daemon': 'dbus', 'contract-child': 'child'}
        expected = {names.get(name, name): process.pid
                    for name, process in case.processes.items()}
        self.assertEqual({name: data['pid'] for name, data in
                          report['cleanup']['processes'].items()}, expected)
        self.assertEqual(set(report['services']), set(expected)-{'child'})
        return report

    def test_inherited_session_is_replaced_at_every_process_boundary(self):
        with self.scenario() as case:
            self.assertEqual(case.execute(), 0)
            report = self.assert_report(case, 'passed', 0)
            self.assertIsNotNone(report['readiness']['before'])
            self.assertIsNotNone(report['readiness']['after'])
            self.assertEqual({name for name, _ in case.envs},
                             {'dbus-daemon', 'pipewire', 'pipewire-pulse',
                              'contract-child', 'pactl', 'pw-dump'})
            for name, env in case.envs:
                with self.subTest(process=name):
                    self.assertFalse(any('foreign-session' in v for v in env.values()))
                    runtime = str(case.base/'runtime')
                    self.assertEqual(env['XDG_RUNTIME_DIR'], runtime)
                    self.assertEqual(env['PIPEWIRE_RUNTIME_DIR'], runtime)
                    self.assertEqual(env['PULSE_SERVER'], 'unix:'+runtime+'/pulse/native')
                    self.assertEqual(env['DBUS_SESSION_BUS_ADDRESS'], 'unix:path='+runtime+'/bus')
                    self.assertEqual(env['DBUS_SYSTEM_BUS_ADDRESS'], env['DBUS_SESSION_BUS_ADDRESS'])
                    self.assertEqual(env['PIPEWIRE_REMOTE'], 'pipewire-0' if name in
                                     ('pipewire', 'pipewire-pulse', 'pw-dump') else
                                     'disabled-private-no-server')

    def test_missing_pw_dump_preflight_starts_nothing(self):
        available = {'dbus-daemon', 'pactl', 'pipewire', 'pipewire-pulse'}
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(audio.sys, 'platform', 'linux'), \
             patch.object(audio.shutil, 'which', side_effect=lambda name:
                          '/mock/'+name if name in available else None):
            report = Path(directory)/'report.json'
            with self.assertRaisesRegex(RuntimeError, 'no supported private'):
                audio.main(['--report', str(report), '--', 'contract-child'])
            self.assertFalse(report.exists())  # Preflight has not entered execute.

    def test_pw_dump_disappearing_before_graph_probe_fails_closed(self):
        with self.scenario() as case:
            def missing(command):
                if command[0] == 'pw-dump':
                    raise FileNotFoundError('secret raw output')
            case.hook = missing
            self.assertEqual(case.execute(), 1)
            self.assertNotIn('contract-child', case.processes)
            self.assert_report(case, 'failed', error='FileNotFoundError')

    def test_missing_private_sink_never_starts_child(self):
        for missing in ('pulse-view', 'native-graph'):
            with self.subTest(missing=missing), self.scenario() as case:
                if missing == 'pulse-view':
                    case.sinks = []
                else:
                    case.graph.pop()
                self.assertEqual(case.execute(), 1)
                self.assertNotIn('contract-child', case.processes)
                report = self.assert_report(case, 'failed', error='RuntimeError')
                self.assertIsNone(report['readiness']['before'])

    def test_bus_and_server_readiness_timeouts_clean_only_started_services(self):
        for stage in ('bus', 'audio', 'default'):
            with self.subTest(stage=stage), self.scenario() as case:
                case.bus_ready = stage != 'bus'
                if stage == 'audio':
                    case.sinks = []
                if stage == 'default':
                    def timeout(command):
                        if command[:2] == ['pactl', 'set-default-sink']:
                            # Nonzero result, not an exception: exhaust 8s loop.
                            return subprocess.CompletedProcess(command, 1, '', '')
                    # Override only this response, retaining the boundary fake.
                    normal_run = audio.subprocess.run.side_effect
                    audio.subprocess.run.side_effect = lambda command, **kw: (
                        timeout(command) or normal_run(command, **kw))
                self.assertEqual(case.execute(), 1)
                self.assertNotIn('contract-child', case.processes)
                self.assertEqual(len(case.processes), 1 if stage == 'bus' else 3)
                self.assert_report(case, 'failed', error='RuntimeError')

    def test_partial_server_start_failure_cleans_only_owned_groups(self):
        with self.scenario() as case:
            def fail(command):
                if command[0] == 'pipewire-pulse':
                    raise OSError('secret raw output')
            case.hook = fail
            self.assertEqual(case.execute(), 1)
            self.assertEqual(set(case.processes), {'dbus-daemon', 'pipewire'})
            self.assert_report(case, 'failed', error='OSError')

    def test_server_exit_before_readiness_fails_closed(self):
        with self.scenario() as case:
            def exit_core(command):
                if command[0] == 'pipewire-pulse':
                    case.processes['pipewire'].returncode = 17
            case.hook = exit_core
            self.assertEqual(case.execute(), 1)
            self.assertNotIn('contract-child', case.processes)
            report = self.assert_report(case, 'failed', error='RuntimeError')
            self.assertEqual(report['cleanup']['processes']['pipewire']['exit_code'], 17)

    def test_pulse_readiness_command_timeout_fails_closed(self):
        with self.scenario() as case:
            def timeout(command):
                if command[0] == 'pactl':
                    raise subprocess.TimeoutExpired(command, 3, output='secret raw output')
            case.hook = timeout
            self.assertEqual(case.execute(), 1)
            self.assertNotIn('contract-child', case.processes)
            self.assert_report(case, 'failed', error='TimeoutExpired')

    def test_graph_probe_timeout_before_during_and_after_child(self):
        for phase in ('before', 'during', 'after'):
            with self.subTest(phase=phase), self.scenario() as case:
                case.child_polls = 1 if phase == 'during' else 0
                def timeout(command):
                    if command[0] == 'pw-dump' and (
                            phase == 'before' or 'contract-child' in case.processes):
                        raise subprocess.TimeoutExpired(command, 3, output='secret raw output')
                case.hook = timeout
                self.assertEqual(case.execute(), 1)
                report = self.assert_report(case, 'failed', 0 if phase == 'after' else None,
                                            error='TimeoutExpired')
                self.assertEqual('contract-child' in case.processes, phase != 'before')
                self.assertIsNone(report['readiness']['after'])

    def test_child_error_and_signal_exit_codes_are_preserved(self):
        for code, expected in ((23, 23), (-signal.SIGTERM, 143), (-signal.SIGINT, 130)):
            with self.subTest(code=code), self.scenario(code) as case:
                self.assertEqual(case.execute(), expected)
                report = self.assert_report(case, 'child_failed', expected)
                self.assertEqual(report['cleanup']['processes']['child']['exit_code'], code)

    def test_child_exec_failure_cleans_servers_and_reports_no_exit_code(self):
        with self.scenario() as case:
            def fail(command):
                if command[0] == 'contract-child':
                    raise OSError('secret raw output')
            case.hook = fail
            self.assertEqual(case.execute(), 1)
            self.assertNotIn('contract-child', case.processes)
            self.assert_report(case, 'failed', error='OSError')

    def test_wrapper_signals_during_startup_and_child_restore_handlers(self):
        for stage in ('startup', 'child'):
            for sig in (signal.SIGTERM, signal.SIGINT):
                with self.subTest(stage=stage, sig=sig), self.scenario() as case:
                    case.child_polls = 1
                    def interrupt(command):
                        if ((stage == 'startup' and command[0] == 'pipewire-pulse') or
                            (stage == 'child' and command[0] == 'pw-dump' and
                             'contract-child' in case.processes)):
                            signal.getsignal(sig)(sig, None)
                    case.hook = interrupt
                    self.assertEqual(case.execute(), 128+sig)
                    self.assert_report(case, 'failed', error='InterruptedError')
                    self.assertEqual('contract-child' in case.processes, stage == 'child')

    def test_cleanup_timeout_still_escalates_all_other_owned_groups(self):
        with self.scenario() as case:
            case.stuck = {'pipewire-pulse'}
            self.assertEqual(case.execute(), 1)
            report = self.assert_report(case, 'cleanup_failed', 0, clean=False)
            self.assertEqual(report['services']['pipewire-pulse']['status'], 'still_running')
            self.assertIsNone(report['cleanup']['processes']['pipewire-pulse']['exit_code'])
            for process in case.processes.values():
                self.assertEqual(process.wait.call_count, 2)
            killed = {call.args[0] for call in case.kill.call_args_list
                      if call.args[1] == signal.SIGKILL}
            self.assertEqual(killed, {p.pid for p in case.processes.values()})


if __name__ == '__main__':
    unittest.main()
