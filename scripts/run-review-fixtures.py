#!/usr/bin/env python3
"""Private synthetic stage-0 matrix, using run-desktop-smoke's exact isolated run/capture mechanism."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import itertools
import json
import os
from pathlib import Path
import sys

SURFACES = ('downloads', 'archive', 'archive-reader', 'archive-large-reader', 'media', 'media-panel', 'tagging', 'tagging-stamp', 'booru', 'booru-workbench', 'rules', 'settings', 'gallery')
WIDTHS = (760, 900, 1099, 1100, 1440)
ROOT = Path(__file__).resolve().parents[1]

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

def atomic_json(path, doc):
    temporary = path.with_suffix(path.suffix + '.pending')
    temporary.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + '\n')
    temporary.replace(path)

def inventory(evidence, selected=None):
    source = evidence / 'scenes.json'
    captured = json.loads(source.read_text())['scenes'] if source.exists() else []
    by_id = {c['id']: c for c in captured}
    scenes = []
    for lang, theme, width, surface in itertools.product(('de', 'en'), ('Light', 'Dark'), WIDTHS, SURFACES):
        scene_id = f'{surface}-{width}-{theme.lower()}-{lang}'
        if selected and scene_id != selected: continue
        cell = by_id.get(scene_id, {'id': scene_id, 'surface': surface, 'status': 'missing', 'theme': theme, 'language': lang, 'geometry': {'width': width, 'height': 920}, 'error': 'No scene bundle produced (see run/output.log or review-artifacts/fatal.json)'})
        if cell['status'] == 'captured':
            try:
                files = cell['files']
                for name in ('bundle.json', 'screenshot.png', 'control-tree.json', 'timing.json', 'state.json', 'grayscale.png'):
                    item = files[name]
                    path = (evidence / item['path']).resolve()
                    if not path.is_relative_to(evidence) or hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
                        raise ValueError('Missing or checksum-mismatched ' + name)
                tree = json.loads((evidence / files['control-tree.json']['path']).read_text())
                if not tree['elements']: raise ValueError('Empty control dump')
                for control in tree['elements']:
                    for key in ('name', 'bounds', 'visible', 'is_enabled', 'is_focused', 'text'):
                        if key not in control: raise ValueError('Incomplete control dump: ' + key)
                marker = json.loads((evidence / files['bundle.json']['path']).read_text())
                if not marker.get('complete'): raise ValueError('Uncommitted bundle')
                cell['checksum'] = hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()
                for item in files.values(): item['path'] = 'run/review-artifacts/' + item['path']
            except (OSError, ValueError, KeyError) as exc:
                cell['status'], cell['error'] = 'missing', str(exc)
        scenes.append(cell)
    return scenes

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path, help='New run directory, never a real profile')
    parser.add_argument('--seed', type=int, default=20261117)
    parser.add_argument('--media', type=int, choices=(0, 1, 100, 1000, 10000), default=100)
    parser.add_argument('--timeout', type=float, default=1800)
    parser.add_argument('--scene', help='One exact scene ID for harness debugging; omit for 260 scenes')
    parser.add_argument('--generate-only', action='store_true')
    args = parser.parse_args(argv)
    if args.scene and args.scene not in {f'{s}-{w}-{t}-{l}' for s, w, t, l in itertools.product(SURFACES, WIDTHS, ('light', 'dark'), ('de', 'en'))}:
        parser.error('unknown scene ID')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    output.chmod(0o700)
    smoke = load('review_smoke_runner', ROOT / 'scripts/run-desktop-smoke.py')
    # Drop inherited smoke selectors, profile pointers and capture destinations; keep tool/runtime paths.
    environment = {k: v for k, v in os.environ.items() if not k.startswith(('MEOWCHAN_', 'OCHAN_'))}
    environment.update(MEOWCHAN_REVIEW_ARTIFACTS='1', MEOWCHAN_REVIEW_SEED=str(args.seed), MEOWCHAN_REVIEW_MEDIA=str(args.media))
    if args.scene: environment['MEOWCHAN_REVIEW_SCENE'] = args.scene
    result = smoke.run_mode('review-fixture-generate' if args.generate_only else 'review-fixtures', args.assembly.resolve(), output / 'run', environment, True, args.timeout)
    evidence = output / 'run/review-artifacts'
    prechecks = load('review_prechecks', ROOT / 'scripts/review-prechecks.py').run(ROOT, evidence)
    atomic_json(output / 'prechecks.json', prechecks)
    scenes = [] if args.generate_only else inventory(evidence, args.scene)
    clean = sum(s['status'] == 'captured' for s in scenes)
    manifest = {'schema_version': 1, 'seed': args.seed, 'media': args.media, 'expected': len(scenes), 'captured': clean,
                'failed': len(scenes) - clean, 'reviewable': bool(scenes) and clean == len(scenes) and result['passed'], 'runner': result,
                'assembly_sha256': hashlib.sha256(args.assembly.read_bytes()).hexdigest(), 'prechecks': 'prechecks.json', 'scenes': scenes,
                'limitations': ['grayscale is not forced-colors', 'scene setup timings are not input-to-photon latency', 'native detached popup pixels are not composited',
                    'full interaction, heartbeat, persistence and focus-walk protocol are not asserted by the static matrix', 'private tag vocabulary capped by product; 4096-word probe exported separately']}
    atomic_json(output / 'captures.json', manifest)
    print(json.dumps({'expected': len(scenes), 'captured': clean, 'failed': len(scenes)-clean, 'prechecks': prechecks['counts'], 'incomplete_checks': prechecks['incomplete']}))
    return 0 if result['passed'] and clean == len(scenes) else 1

if __name__ == '__main__':
    sys.exit(main())
