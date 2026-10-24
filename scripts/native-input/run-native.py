"""W20 only: source-bound private Linux X11/AT-SPI/Orca; no desktop fallback."""
from pathlib import Path
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--source-manifest', required=True, type=Path)
    parser.add_argument('--assembly', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--suite', choices=('plugins', 'sources', 'saved-searches', 'shortcuts'), required=True)
    parser.add_argument('--language', choices=('en', 'de'), default='en')
    parser.add_argument('--scale', choices=('1', '1.5', '2'), default='1')
    parser.add_argument('--bundle', required=True, type=Path,
                        help='Retained task-private Orca tool root, not the personal session')
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    repo = args.source.resolve(strict=True)
    assembly = args.assembly.resolve(strict=True)
    bundle = args.bundle.resolve(strict=True)
    assembly.relative_to(repo)
    manifest = json.loads(args.source_manifest.read_text())
    source_digest = manifest.get('sourceDigest', manifest.get('source_digest', ''))
    if re.fullmatch('[0-9a-f]{64}', source_digest) is None:
        raise ValueError('Manifest requires a full sourceDigest/source_digest')
    files = manifest['files']
    if not isinstance(files, dict) or not files:
        raise ValueError('Expected nonempty source manifest file map')
    for relative, record in files.items():
        path = Path(relative)
        if path.is_absolute() or '..' in path.parts:
            raise ValueError('Unsafe source manifest path')
        expected = record.get('sha256') if isinstance(record, dict) else record
        if re.fullmatch('[0-9a-f]{64}', expected or '') is None or digest(repo / path) != expected:
            raise ValueError('Source differs from manifest: ' + relative)
    for path in (bundle / 'usr/bin/orca', Path('/usr/lib/at-spi-bus-launcher'),
                 Path('/usr/lib/at-spi2-registryd')):
        if not path.is_file():
            raise FileNotFoundError(path)
    expected = {
        'plugins': ['plugin-overview', 'plugin-local-error', 'source-worker-error'],
        'sources': ['source-policy', 'source-plugin-link'],
        'shortcuts': ['shortcuts', 'shortcut-text', 'shortcut-normal'],
        'saved-searches': ['archive-search', 'saved-search-name', 'saved-search-manager'],
    }[args.suite]
    languages = ('en', 'de') if args.suite in ('sources', 'shortcuts') else (args.language,)
    spec = importlib.util.spec_from_file_location('private_desktop', repo / 'scripts/run-desktop-smoke.py')
    desktop = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(desktop)
    missing = [name for name in (*desktop.ISOLATED_TOOLS, 'gdbus', 'dotnet') if not shutil.which(name)]
    if missing:
        raise ValueError('Missing tools: ' + ', '.join(missing))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / 'exchange').mkdir()
    receipt = {
        'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'source': str(repo), 'sourceDigest': source_digest,
        'sourceManifestSha256': digest(args.source_manifest), 'sourceFilesVerified': len(files),
        'suite': args.suite, 'language': args.language, 'observedLanguages': list(languages), 'scale': args.scale,
        'assemblies': {str(path): digest(path) for path in
                       (assembly, assembly.with_name('MeowChan.dll'), assembly.with_name('libmeowchan.dll'))},
        'helpers': {path.name: digest(path) for path in here.glob('*.py')},
        'toolFiles': {str(path): digest(path) for path in
                      (bundle / 'usr/bin/orca', Path('/usr/lib/at-spi-bus-launcher'), Path('/usr/lib/at-spi2-registryd'))},
        'scope': 'Source-built fixtures, actual native AT-SPI/focus/XTest Tab and Orca debug utterances. No delivered-package, IME, human or audible-audio claim.',
    }
    (output / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    with tempfile.TemporaryDirectory(prefix='meowchan-a11y-') as temporary:
        work = Path(temporary).resolve(strict=True)
        token = os.urandom(32).hex()
        (work / '.native-input-owner').write_text(token)
        (work / '.native-input-owner').chmod(0o600)
        base = {key: value for key, value in os.environ.items() if not key.startswith(
            ('MEOWCHAN_', 'OCHAN_', 'GTK', 'GIO', 'GDK', 'GI_', 'LD_', 'GST', 'PYTHON',
             'SNAP', 'QT_', 'XDG_DATA_DIRS', 'PULSE', 'PIPEWIRE', 'SPEECHD', 'BRLAPI'))}
        environment = desktop.prepare_environment(base, True, work)
        private_home = work / 'home'
        private_state = work / 'state'
        private_home.mkdir(mode=0o700)
        private_state.mkdir(mode=0o700)
        environment.update(HOME=str(private_home), XDG_STATE_HOME=str(private_state),
                           MEOWCHAN_NATIVE_PRIVATE_ROOT=str(work), MEOWCHAN_NATIVE_PRIVATE_TOKEN=token)
        environment.update({
            'PYTHONPATH': str(bundle / ('usr/lib/python' + str(sys.version_info.major) + '.' + str(sys.version_info.minor) + '/site-packages')),
            'LD_LIBRARY_PATH': str(bundle / 'usr/lib'), 'GI_TYPELIB_PATH': str(bundle / 'usr/lib/girepository-1.0'),
            'GSETTINGS_SCHEMA_DIR': str(bundle / 'usr/share/glib-2.0/schemas'), 'GSETTINGS_BACKEND': 'memory',
            'XDG_DATA_DIRS': str(bundle / 'usr/share') + ':/usr/share',
            'PULSE_SERVER': 'unix:' + str(work / 'no-personal-audio'),
            'PIPEWIRE_REMOTE': 'meowchan-isolated-no-audio',
            'SPEECHD_ADDRESS': 'unix_socket:' + str(work / 'no-speech-server'), 'SPEECHD_CMD': '/bin/false',
            'NO_AT_BRIDGE': '0', 'MEOWCHAN_ACCESSIBILITY_EXCHANGE': str(output / 'exchange'),
            'AVALONIA_GLOBAL_SCALE_FACTOR': args.scale,
            'MEOWCHAN_NATIVE_EXPECTED_FAMILIES': json.dumps(expected), 'MEOWCHAN_URSA_NATIVE_SUITE': args.suite,
            'MEOWCHAN_NATIVE_EXPECTED_STAGES': json.dumps([family + '-' + language for family in expected for language in languages]),
            'MEOWCHAN_NATIVE_INPUT_LANGUAGE': args.language,
            'MEOWCHAN_NATIVE_SOURCE_DIGEST': source_digest, 'MEOWCHAN_SOURCE_DIGEST': source_digest,
            'MEOWCHAN_SAVED_SEARCH_SOURCE_DIGEST': source_digest,
        })
        if args.suite == 'shortcuts':
            environment.update(MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY='1')
        if args.suite == 'sources':
            environment.update(MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY='1', MEOWCHAN_SOURCE_INTEGRITY_EXPECT_SCALE=args.scale)
        if args.suite == 'saved-searches':
            environment.update(MEOWCHAN_SAVED_SEARCHES_ONLY='1', MEOWCHAN_SAVED_SEARCH_STAGE='save',
                               MEOWCHAN_SAVED_SEARCH_LANGUAGE=args.language,
                               MEOWCHAN_SAVED_SEARCH_TEST_PROFILE=str(work / 'saved-search-profile'))
        custom = work / 'data/orca'
        custom.mkdir(parents=True)
        (custom / 'orca-customizations.py').write_text(
            "from orca import debug, gsettings_registry\n"
            "if debug.debugFile is None:\n"
            "    raise RuntimeError('Native observer requires the real Orca debug file')\n"
            "debug.debugFile.reconfigure(line_buffering=True)\n"
            "debug.debugFile.flush()\n"
            "debug.print_message(debug.LEVEL_INFO, 'MEOWCHAN NATIVE DEBUG FILE: line buffering enabled', True)\n"
            "gsettings_registry.get_registry().set_runtime_value('sound', 'enabled', False)\n"
            "gsettings_registry.get_registry().set_runtime_value('braille', 'enabled', False)\n")
        command = ['dbus-run-session', '--config-file=' + str(work / 'bus.conf'), '--',
                   'xvfb-run', '--auto-servernum', '--server-args=-screen 0 3200x2000x24 -nolisten tcp',
                   sys.executable, str(repo / 'scripts/with-isolated-window-manager.py'), '--', sys.executable,
                   str(here / 'native-session.py'), str(output), str(bundle), str(assembly), args.suite]
        started = time.time_ns()
        with (output / 'session.log').open('w') as log:
            process = subprocess.Popen(command, cwd=work, env=environment, stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            try:
                status = process.wait(timeout=930)
            except subprocess.TimeoutExpired:
                status = 124
                (output / 'timeout.json').write_text(json.dumps({'passed': False, 'timeoutSeconds': 930}) + '\n')
            finally:
                desktop.stop_owned_processes(process)
        captures = output / 'product-evidence'
        captures.mkdir()
        for path in assembly.parent.glob('smoke*'):
            if path.is_file() and path.suffix in ('.png', '.json') and path.stat().st_mtime_ns >= started:
                shutil.copy2(path, captures / path.name)
    print(json.dumps({'output': str(output), 'exit_status': status}))
    return status


if __name__ == '__main__':
    raise SystemExit(main())
