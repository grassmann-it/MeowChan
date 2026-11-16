#!/usr/bin/env python3
"""Collect independent source-built GUI scenes; never admit incomplete evidence."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

NORMAL = ['normal-core', 'normal-grid', 'normal-settings-page', 'normal-diagnostics',
          'sources-page', 'normal-board-admission', 'pause-controls', 'rules-page', 'settings-core']
READER = ['gallery-close', 'reader-media-input', 'reader-shortcut-contexts', 'reader-groups',
          'reader-returns', 'reader-ideas', 'reader-replies', 'reader-post-filter',
          'reader-stability', 'reader-post-width', 'reader-navigation', 'reader-view',
          'reader-quick-settings', 'reader-thread-menu', 'reader-toolbar',
          'reader-conversation', 'reader-display', 'reader-media-sidebar', 'reader-inline-video']
MODES = ['locked-profile', 'no-tray', 'tray-failure', 'download-session', 'reader-ux', 'selection']


def collect(entries, required, assembly, output, timeout, native=False, run=subprocess.run):
    output.mkdir(parents=True, exist_ok=True)
    report = output / 'collection.json'
    if report.exists():
        raise ValueError('Choose a fresh output directory; prior evidence is preserved')
    rows = []
    def write(complete):
        passed = complete and len(rows) == len(required) and all(r['passed'] for r in rows)
        report.write_text(json.dumps(dict(required=required, results=rows, complete=complete,
                                         passed=passed), indent=2) + '\n')
        return passed
    write(False)
    for identifier in required:
        row = dict(id=identifier, passed=False)
        try:
            matches = [e for e in entries if e['id'] == identifier]
            if len(matches) != 1:
                raise ValueError('Required registry entry missing or duplicated')
            entry = matches[0]
            if entry.get('error') or entry.get('skip'):
                raise ValueError(str(entry.get('error') or entry.get('skip')))
            environment = {k: v for k, v in os.environ.items()
                           if not k.startswith('MEOWCHAN_') or not (k.endswith('_ONLY') or k in SELECTORS)}
            for pair in entry['environment']:
                key, value = pair.split('=', 1)
                environment[key] = value.replace('{repo}', str(Path.cwd()))
            destination = output / identifier
            command = [sys.executable, 'scripts/run-desktop-smoke.py', '--assembly', str(assembly),
                       '--mode', entry['mode'], '--expect-scenario', entry['scenario'],
                       '--timeout', str(timeout), '--output', str(destination)]
            if native:
                command.append('--allow-native-desktop')
            result = run(command, env=environment)
            evidence = json.loads((destination / 'results.json').read_text())
            row.update(exit_code=result.returncode, passed=result.returncode == 0 and
                       evidence.get('passed') is True and evidence.get('selected_modes') == [entry['mode']]
                       and len(evidence.get('results', [])) == 1
                       and evidence['results'][0].get('scenario') == entry['scenario'])
        except Exception as error:
            row['error'] = str(error)
        rows.append(row)
        write(False)
        print(f"GUI {identifier}: {'PASS' if row['passed'] else 'FAIL'}", flush=True)
    return 0 if write(True) else 1


SELECTORS = set()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', required=True, type=int)
    parser.add_argument('--selection-only', action='store_true')
    parser.add_argument('--allow-native-desktop', action='store_true')
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('smoke', Path(__file__).with_name('run-desktop-smoke.py'))
    smoke = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(smoke)
    entries = smoke.registry_entries(args.assembly.resolve())
    # Clear every registered selector, including branch selectors without _ONLY.
    SELECTORS.update(pair.split('=', 1)[0] for e in entries for pair in e['environment'])
    return collect(entries, ['selection'] if args.selection_only else NORMAL + MODES + READER,
                   args.assembly, args.output, args.timeout, args.allow_native_desktop)


if __name__ == '__main__':
    sys.exit(main())
