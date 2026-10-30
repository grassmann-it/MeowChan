#!/usr/bin/env python3
"""Linux CI: --report PATH -- COMMAND [ARG ...].

Requires dbus-daemon, PipeWire, WirePlumber >= 0.5, pipewire-pulse,
pactl and pw-dump. Runs real software audio, NOT a mocked availability flag.
Only a muted null sink and its monitor are allowed. No inherited session,
display or audio environment is used. The child may start its own GUI bus/Xvfb;
its absolute Pulse socket/cookie remain usable across that bus change.
Readiness is bounded to 25s plus 8s for default metadata. Child execution has
no arbitrary deadline; CI may SIGTERM this wrapper for bounded owned cleanup.
The report contains summaries only (no environment, cookies or raw graphs).
"""
import argparse
import json
import os
import re
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

SINK = 'ci_private_null'
SERVICES = ('pipewire', 'wireplumber', 'pipewire-pulse')


def cli(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True, type=Path)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    if not args.command or args.command[0] != '--' or len(args.command) < 2:
        parser.error('expected -- followed by a child command')
    args.command = args.command[1:]
    return args


def environment(base):
    # Allowlist avoids indirect session/device/plugin overrides and user HOME.
    env = {k: os.environ[k] for k in ('PATH', 'LANG', 'LC_ALL', 'TZ') if k in os.environ}
    for name in ('home', 'config', 'cache', 'state', 'data', 'runtime', 'tmp'):
        (base / name).mkdir(mode=0o700)
    env.update(HOME=str(base/'home'), TMPDIR=str(base/'tmp'),
               XDG_CONFIG_HOME=str(base/'config'), XDG_CACHE_HOME=str(base/'cache'),
               XDG_STATE_HOME=str(base/'state'), XDG_DATA_HOME=str(base/'data'),
               XDG_RUNTIME_DIR=str(base/'runtime'), XDG_CONFIG_DIRS='/etc/xdg',
               PIPEWIRE_RUNTIME_DIR=str(base/'runtime'), PIPEWIRE_REMOTE='pipewire-0',
               PULSE_SERVER='unix:'+str(base/'runtime/pulse/native'),
               PULSE_SOCKET=str(base/'runtime/pulse/native'),
               PULSE_RUNTIME_PATH=str(base/'runtime/pulse'),
               PULSE_COOKIE=str(base/'config/pulse/cookie'), PULSE_SINK=SINK,
               ALSA_CONFIG_PATH=str(base/'config/alsa-null.conf'))
    config = base/'config'
    (config/'pipewire/pipewire.conf.d').mkdir(parents=True)
    (config/'pipewire/pipewire.conf.d/null.conf').write_text('''context.objects = [
{ factory = adapter args = { factory.name = support.null-audio-sink
 node.name = ci_private_null media.class = "Audio/Sink" audio.position = [ FL FR ] } }
]
''')
    (config/'wireplumber/wireplumber.conf.d').mkdir(parents=True)
    (config/'wireplumber/wireplumber.conf.d/no-hardware.conf').write_text('''wireplumber.profiles = {
 main = { monitor.alsa = disabled monitor.bluez = disabled
 monitor.bluez-midi = disabled monitor.v4l2 = disabled monitor.libcamera = disabled }
}
''')
    (config/'alsa-null.conf').write_text('pcm.!default { type null }\nctl.!default { type null }\n')
    (config/'pulse').mkdir()
    cookie = config/'pulse/cookie'
    cookie.write_bytes(os.urandom(256))
    cookie.chmod(0o600)
    (config/'pulse/client.conf').write_text('autospawn = no\n')
    return env


def run(env, command, timeout=3):
    return subprocess.run(command, env=env, capture_output=True, text=True, timeout=timeout)


def checked(env, command):
    result = run(env, command)
    if result.returncode:
        raise RuntimeError('command failed: '+command[0])
    return result.stdout


def proof(env):
    sinks = json.loads(checked(env, ['pactl', '--format=json', 'list', 'sinks']))
    sources = json.loads(checked(env, ['pactl', '--format=json', 'list', 'sources']))
    graph = json.loads(checked(env, ['pw-dump']))
    if len(sinks) != 1 or sinks[0]['name'] != SINK or sinks[0]['mute'] is not True:
        raise RuntimeError('unsafe sink count/name/mute')
    if any(s['name'] != SINK+'.monitor' for s in sources):
        raise RuntimeError('unexpected source')
    for item in graph:
        props = item.get('info', {}).get('props', {})
        # Reject every device, not just ALSA/BlueZ; also catch hardware nodes.
        if item.get('type', '').endswith(':Device') or any(
            k.startswith(('api.alsa.', 'api.bluez', 'api.v4l2.', 'api.libcamera.'))
            or k == 'device.api' for k in props
        ):
            raise RuntimeError('hardware/device found')
        if props.get('media.class') == 'Audio/Sink' and props.get('node.name') != SINK:
            raise RuntimeError('extra graph sink')
    return {'sink_count': len(sinks), 'sink': SINK, 'muted': True,
            'source_count': len(sources), 'hardware_devices': 0, 'graph_objects': len(graph)}


def cleanup(owned):
    # Each service/child has an owned process group, never signal a user service.
    for _, p in reversed(owned):
        try:
            os.killpg(p.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    deadline = time.monotonic()+5
    for _, p in reversed(owned):
        try:
            p.wait(timeout=max(.01, deadline-time.monotonic()))
        except subprocess.TimeoutExpired:
            pass
    for _, p in reversed(owned):
        try:
            os.killpg(p.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        try:
            p.wait(timeout=2)
        except subprocess.TimeoutExpired:
            return False
    # Include descendants left in a group after the direct child exited.
    for _, p in owned:
        try:
            os.killpg(p.pid, 0)
        except ProcessLookupError:
            continue
        return False
    return True


def execute(command, report_path):
    report = {'status': 'starting', 'readiness': {'before': None, 'after': None},
              'services': {}, 'child_exit_code': None, 'cleanup': {'complete': False}}
    owned = []
    code = 1
    def save():
        report_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = report_path.with_name(report_path.name+'.tmp')
        temporary.write_text(json.dumps(report, indent=2)+'\n')
        temporary.replace(report_path)
    def spawn(name, args, env):
        p = subprocess.Popen(args, env=env, start_new_session=True,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        owned.append((name, p))
        return p
    def alive():
        if any(p.poll() is not None for n, p in owned if n != 'child'):
            raise RuntimeError('owned service exited')
    def stop(signum, _frame):
        raise InterruptedError(signum)
    old = {s: signal.signal(s, stop) for s in (signal.SIGTERM, signal.SIGINT)}
    try:
        with tempfile.TemporaryDirectory(prefix='ci-audio-', dir='/tmp') as directory:
            try:
                base = Path(directory)
                env = environment(base)
                bus_path = base/'runtime/bus'
                conf = base/'bus.conf'
                conf.write_text('<busconfig><type>session</type><listen>unix:path='+str(bus_path)+
                                '</listen><auth>EXTERNAL</auth><policy context="default">'
                                '<allow send_destination="*"/><allow own="*"/>'
                                '</policy></busconfig>')
                env['DBUS_SESSION_BUS_ADDRESS'] = 'unix:path='+str(bus_path)
                spawn('dbus', ['dbus-daemon', '--nofork', '--config-file='+str(conf)], env)
                deadline = time.monotonic()+25
                while not bus_path.exists():
                    alive()
                    if time.monotonic() >= deadline:
                        raise RuntimeError('private bus readiness timeout')
                    time.sleep(.1)
                version = checked(env, ['wireplumber', '--version'])
                match = re.search(r'(?<![0-9])(\d+)\.(\d+)\.', version)
                if not match or tuple(map(int, match.groups())) < (0, 5):
                    raise RuntimeError('WirePlumber >= 0.5 required for disabled monitors')
                for service in SERVICES:
                    spawn(service, [service], env)
                while True:
                    alive()
                    result = run(env, ['pactl', '--format=json', 'list', 'sinks'])
                    if result.returncode == 0 and json.loads(result.stdout):
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError('audio readiness timeout (25s)')
                    time.sleep(.2)
                deadline = time.monotonic()+8
                while run(env, ['pactl', 'set-default-sink', SINK]).returncode:
                    alive()
                    if time.monotonic() >= deadline:
                        raise RuntimeError('default metadata timeout (8s)')
                    time.sleep(.2)
                checked(env, ['pactl', 'set-sink-mute', SINK, '1'])
                report['readiness']['before'] = proof(env)
                report['services'] = {n: {'pid': p.pid, 'status': 'running'} for n, p in owned}
                report['status'] = 'ready'
                save()
                child = spawn('child', command, env)
                next_proof = time.monotonic()+.5
                while child.poll() is None:
                    alive()
                    if time.monotonic() >= next_proof:
                        proof(env)
                        next_proof = time.monotonic()+.5
                    time.sleep(.1)
                code = child.returncode if child.returncode >= 0 else 128-child.returncode
                report['child_exit_code'] = code
                alive()
                report['readiness']['after'] = proof(env)
                report['status'] = 'passed' if code == 0 else 'child_failed'
            finally:
                # Ignore repeated termination while bounded cleanup is in progress.
                for s in old:
                    signal.signal(s, signal.SIG_IGN)
                complete = cleanup(owned)
                report['services'] = {n: {'pid': p.pid,
                    'status': 'exited' if p.poll() is not None else 'still_running'}
                    for n, p in owned if n != 'child'}
                report['cleanup'] = {'complete': complete,
                    'processes': {n: {'pid': p.pid, 'exit_code': p.poll()} for n, p in owned}}
                if not complete:
                    code = 1
                    report['status'] = 'cleanup_failed'
    except (Exception, KeyboardInterrupt) as exc:
        code = 128+exc.args[0] if isinstance(exc, InterruptedError) else 1
        report['status'] = 'failed'
        # Deliberately omit exception content (could include raw service output).
        report['error_type'] = type(exc).__name__
    finally:
        for s, handler in old.items():
            signal.signal(s, handler)
        save()
    return code


def main(argv=None):
    args = cli(sys.argv[1:] if argv is None else argv)
    if sys.platform != 'linux':
        raise SystemExit('private null audio requires Linux')
    return execute(args.command, args.report.resolve())


if __name__ == '__main__':
    sys.exit(main())
