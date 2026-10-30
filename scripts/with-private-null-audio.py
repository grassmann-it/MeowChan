#!/usr/bin/env python3
"""Linux CI: --report PATH -- COMMAND [ARG ...].

Requires dbus-daemon, pulseaudio and pulseaudio-utils (Ubuntu 24.04 stock).
No PipeWire or WirePlumber services are started. Runs real software audio, NOT a mocked availability flag.
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
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

SINK = 'ci_private_null'
MODULES = {'module-native-protocol-unix', 'module-null-sink'}


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
               XDG_RUNTIME_DIR=str(base/'runtime'), XDG_CONFIG_DIRS=str(base/'config'),
               PIPEWIRE_RUNTIME_DIR=str(base/'runtime'), PIPEWIRE_REMOTE='disabled-private-no-server',
               PULSE_CONFIG_PATH=str(base/'config/pulse'),
               PULSE_SERVER='unix:'+str(base/'runtime/pulse/native'),
               PULSE_SOCKET=str(base/'runtime/pulse/native'),
               PULSE_RUNTIME_PATH=str(base/'runtime/pulse'),
               PULSE_COOKIE=str(base/'config/pulse/cookie'), PULSE_SINK=SINK,
               ALSA_CONFIG_PATH=str(base/'config/alsa-null.conf'))
    config = base/'config'
    (config/'alsa-null.conf').write_text('pcm.!default { type null }\nctl.!default { type null }\n')
    (config/'pulse').mkdir()
    cookie = config/'pulse/cookie'
    cookie.write_bytes(os.urandom(256))
    cookie.chmod(0o600)
    (config/'pulse/client.conf').write_text('autospawn = no\n')
    (config/'pulse/daemon.conf').write_text(
        'daemonize = no\nuse-pid-file = no\nexit-idle-time = -1\n')
    # -n suppresses the system/user default.pa. Only these modules may load.
    (config/'pulse/private.pa').write_text(
        '.fail\n'
        'load-module module-native-protocol-unix socket='+env['PULSE_SOCKET']+
        ' auth-cookie='+str(cookie)+' auth-cookie-enabled=yes auth-anonymous=no\n'
        'load-module module-null-sink sink_name='+SINK+' channels=2 rate=48000\n'
        'set-default-sink '+SINK+'\nset-sink-mute '+SINK+' yes\n')
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
    modules = json.loads(checked(env, ['pactl', '--format=json', 'list', 'modules']))
    cards = json.loads(checked(env, ['pactl', '--format=json', 'list', 'cards']))
    default = checked(env, ['pactl', 'get-default-sink']).strip()
    if len(sinks) != 1 or sinks[0]['name'] != SINK or sinks[0]['mute'] is not True:
        raise RuntimeError('unsafe sink count/name/mute')
    if default != SINK:
        raise RuntimeError('unexpected default sink')
    if len(sources) != 1 or sources[0]['name'] != SINK+'.monitor':
        raise RuntimeError('unexpected source')
    if cards:
        raise RuntimeError('hardware card found')
    if len(modules) != 2 or {m['name'] for m in modules} != MODULES:
        raise RuntimeError('unexpected module (only private protocol and null sink allowed)')
    null_module = next(m for m in modules if m['name'] == 'module-null-sink')
    if any(str(obj.get('owner_module')) != str(null_module['index'])
           for obj in sinks+sources):
        raise RuntimeError('sink/source not owned by null module')
    if sinks[0].get('driver') != 'module-null-sink.c':
        raise RuntimeError('unexpected sink driver')
    return {'sink_count': 1, 'sink': SINK, 'muted': True, 'default_sink': default,
            'source_count': 1, 'hardware_cards': 0,
            'modules': sorted(MODULES)}


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
    report = {'status': 'starting', 'readiness': {'before': None, 'after': None, 'during_checks': 0},
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
                             stdout=None if name == 'child' else subprocess.DEVNULL,
                             stderr=None if name == 'child' else subprocess.DEVNULL)
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
                                '<allow send_destination="*"/><allow receive_sender="*"/>'
                                '<allow own="*"/>'
                                '</policy></busconfig>')
                env['DBUS_SESSION_BUS_ADDRESS'] = 'unix:path='+str(bus_path)
                # Never let rtkit/system-bus discovery reach the host bus.
                env['DBUS_SYSTEM_BUS_ADDRESS'] = env['DBUS_SESSION_BUS_ADDRESS']
                spawn('dbus', ['dbus-daemon', '--nofork', '--config-file='+str(conf)], env)
                deadline = time.monotonic()+25
                while not bus_path.exists():
                    alive()
                    if time.monotonic() >= deadline:
                        raise RuntimeError('private bus readiness timeout')
                    time.sleep(.1)
                spawn('pulseaudio', ['pulseaudio', '-n',
                    '--file='+str(base/'config/pulse/private.pa'),
                    '--daemonize=no', '--use-pid-file=no', '--exit-idle-time=-1',
                    '--system=no', '--fail=yes', '--disallow-module-loading=yes'], env)
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
                        report['readiness']['during_checks'] += 1
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
