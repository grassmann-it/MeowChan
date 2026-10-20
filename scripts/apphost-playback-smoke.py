#!/usr/bin/env python3
"""Exercise actual apphost gallery playback. Linux always uses a private desktop."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

spec = importlib.util.spec_from_file_location('desktop_runner', Path(__file__).with_name('run-desktop-smoke.py'))
desktop = importlib.util.module_from_spec(spec)
spec.loader.exec_module(desktop)
CHECKS = {'initial-image', 'native-time-and-frame-progress', 'native-output-snapshot', 'pause-clock-held', 'play-resumed',
          'video-image-stream-released', 'back-to-video', 'gallery-close-disposed', 'playing-shutdown-disposed'}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def default_fixtures():
    directory = Path(__file__).resolve().parents[1] / 'tests/Fixtures'
    manifest = json.loads((directory / 'gallery-playback-manifest.json').read_text(encoding='utf-8'))
    records = manifest['files']
    if len(records) != 2 or {record['codec'] for record in records} != {'vp8', 'vp9'}:
        raise ValueError('Expected pinned VP8 and VP9 fixtures')
    paths = []
    for record in records:
        path = directory / record['file']
        if path.parent != directory or path.stat().st_size != record['bytes'] or sha256(path) != record['sha256']:
            raise ValueError('Pinned fixture manifest mismatch: ' + str(path))
        paths.append(path)
    return paths


def validate_png(path):
    data = path.read_bytes()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('Native snapshot is not PNG')
    offset, chunks = 8, []
    while offset < len(data):
        if len(data) - offset < 12:
            raise ValueError('Truncated PNG chunk')
        length = struct.unpack('>I', data[offset:offset + 4])[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data) or zlib.crc32(data[offset + 4:end - 4]) != struct.unpack('>I', data[end - 4:end])[0]:
            raise ValueError('Invalid PNG chunk/CRC')
        if not chunks:
            if kind != b'IHDR' or length != 13:
                raise ValueError('Missing PNG header')
            width, height = struct.unpack('>II', data[offset + 8:offset + 16])
            if not width or not height:
                raise ValueError('Empty native snapshot')
        chunks.append(kind)
        offset = end
        if kind == b'IEND':
            break
    if offset != len(data) or not chunks or chunks[-1] != b'IEND' or b'IDAT' not in chunks:
        raise ValueError('Incomplete native snapshot')
    return {'width': width, 'height': height, 'sha256': sha256(path)}


def validate_metrics(metrics, fixture_hash):
    if metrics.get('schemaVersion') != 1 or metrics.get('completed') is not True or metrics.get('failure') is not None:
        raise ValueError('Product did not complete playback checks')
    if str(metrics.get('fixtureSha256', '')).lower() != fixture_hash.lower():
        raise ValueError('Product fixture hash mismatch')
    if not CHECKS.issubset(set(metrics.get('checks', []))):
        raise ValueError('Product omitted required playback checks')
    samples = metrics.get('samples', [])
    if len(samples) < 2:
        raise ValueError('Missing native playback progress samples')
    first, second = samples[:2]
    if first['State'] != 'Playing' or first['Time'] < 300 or first['DecodedFrames'] < 3 or first['DisplayedFrames'] < 2:
        raise ValueError('Insufficient initial native frames/time')
    if second['State'] != 'Playing' or second['Time'] < first['Time'] + 250 or any(second[k] <= first[k] for k in ('DecodedFrames', 'DisplayedFrames')):
        raise ValueError('Native frames/time did not advance')
    for name, minimum, disposed in [('imageCleanup', 1, False), ('closeCleanup', 2, True), ('shutdownCleanup', 1, True)]:
        evidence = metrics[name]
        if evidence['HasPlayback'] is not False or evidence['ReleasedStreams'] < minimum or evidence['CleanupFailures'] != 0:
            raise ValueError(name + ' did not release playback streams cleanly')
        if disposed and (evidence['Disposed'] is not True or evidence['HasEngine'] is not False):
            raise ValueError(name + ' did not dispose the native engine')


def run_fixture(apphost, fixture, destination, timeout, isolated):
    destination.mkdir()
    result = {'fixture': str(fixture), 'fixtureSha256': sha256(fixture), 'passed': False, 'timedOut': False}
    try:
        with tempfile.TemporaryDirectory(prefix='meowchan-playback-') as working:
            directory = Path(working)
            environment = desktop.prepare_environment(dict(os.environ), isolated, directory)
            profile = Path(environment['MEOWCHAN_PROFILE'])
            command = [str(apphost), '--smoke-playback-webm', str(fixture)]
            if isolated:
                command = ['dbus-run-session', '--config-file=' + str(directory / 'bus.conf'), '--',
                           'xvfb-run', '--auto-servernum', '--server-args=-screen 0 3200x2000x24 -nolisten tcp',
                           sys.executable, str(Path(__file__).with_name('with-isolated-window-manager.py').resolve()), '--', *command]
            result['command'] = command
            process = None
            with (destination / 'output.log').open('w', encoding='utf-8') as log:
                try:
                    process = subprocess.Popen(command, cwd=directory, env=environment, stdout=log,
                                               stderr=subprocess.STDOUT, start_new_session=os.name != 'nt')
                    try:
                        result['exitCode'] = process.wait(timeout=timeout)
                    except subprocess.TimeoutExpired:
                        result['timedOut'] = True
                        desktop.sample_timeout(process, destination)
                finally:
                    try:
                        if process is not None:
                            desktop.stop_owned_processes(process)
                    finally:
                        for source, target in [('apphost-playback.json', 'metrics.json'), ('apphost-playback-frame.png', 'frame.png')]:
                            if (profile / source).is_file():
                                shutil.copy2(profile / source, destination / target)
        if result['timedOut'] or result.get('exitCode') != 0:
            raise ValueError('Apphost timed out or exited unsuccessfully')
        if 'APPHOST PLAYBACK PASS' not in (destination / 'output.log').read_text(encoding='utf-8', errors='replace'):
            raise ValueError('Apphost completion marker missing')
        validate_metrics(json.loads((destination / 'metrics.json').read_text(encoding='utf-8')), result['fixtureSha256'])
        result['snapshot'] = validate_png(destination / 'frame.png')
        result['passed'] = True
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
    (destination / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apphost', type=Path)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, action='append')
    parser.add_argument('--timeout', type=float, default=120)
    parser.add_argument('--allow-native-desktop', action='store_true')
    args = parser.parse_args(argv)
    isolated = sys.platform.startswith('linux')
    if not isolated and not args.allow_native_desktop:
        parser.error('Native playback requires --allow-native-desktop on a disposable desktop/CI')
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error('--timeout must be finite and positive')
    if isolated:
        missing = [tool for tool in desktop.ISOLATED_TOOLS if not shutil.which(tool)]
        if missing:
            parser.error('Private desktop dependencies missing: ' + ', '.join(missing))
    apphost = args.apphost.resolve(strict=True)
    fixtures = [path.resolve(strict=True) for path in (args.fixture or default_fixtures())]
    if not fixtures:
        parser.error('No playback fixtures')
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    results = [run_fixture(apphost, fixture, args.evidence_dir / (str(index) + '-' + fixture.stem), args.timeout, isolated)
               for index, fixture in enumerate(fixtures)]
    report = {'schemaVersion': 1, 'apphost': str(apphost), 'apphostSha256': sha256(apphost),
              'host': platform.platform(), 'machine': platform.machine(), 'isolatedDesktop': isolated,
              'scope': 'Actual apphost, native VLC decoded/displayed frames and snapshot; silent fixtures, no physical audio/hardware claim.',
              'passed': all(item['passed'] for item in results), 'fixtures': results}
    (args.evidence_dir / 'playback.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
