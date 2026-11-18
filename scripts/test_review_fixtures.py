#!/usr/bin/env python3
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent

def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

runner = module('run-review-fixtures')
checks = module('review-prechecks')

class ReviewContracts(unittest.TestCase):
    def test_full_matrix_missing_is_explicit(self):
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            cells = runner.inventory(Path(path))
            self.assertEqual(260, len(cells))
            self.assertEqual(260, sum(c['status'] == 'missing' for c in cells))
            self.assertEqual(260, len({c['id'] for c in cells}))
            self.assertEqual({760, 900, 1099, 1100, 1440}, {c['geometry']['width'] for c in cells})

    def test_single_debug_scene_accepts_non_matrix_width(self):
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            self.assertEqual(('tagging-stamp', 1920, 'Light', 'en'), runner.parse_scene('tagging-stamp-1920-light-en'))
            cells = runner.inventory(Path(path), 'tagging-870-dark-de')
            self.assertEqual(1, len(cells))
            self.assertEqual('tagging-870-dark-de', cells[0]['id'])
            self.assertEqual(870, cells[0]['geometry']['width'])

    def test_screenshot_without_dump_rejected(self):
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            root = Path(path)
            import hashlib
            (root / 'screenshot.png').write_bytes(b'synthetic screenshot bytes')
            (root / 'bundle.json').write_text('{"complete": true}')
            files = {name: {'path': name, 'sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()}
                     for name in ('bundle.json', 'screenshot.png')}
            scene = {'id': 'media-760-dark-de', 'status': 'captured', 'files': files}
            (root / 'scenes.json').write_text(json.dumps({'scenes': [scene]}))
            cells = runner.inventory(root, scene['id'])
            self.assertEqual('missing', cells[0]['status'])
            self.assertIn('control-tree.json', cells[0]['error'])

    def test_committed_bundle_and_tamper_detection(self):
        import hashlib
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            root = Path(path)
            control = dict(name='SyntheticControl', bounds=None, visible=False, is_enabled=True, is_focused=False, text='Synthetic')
            data = {'bundle.json': {'complete': True}, 'control-tree.json': {'elements': [control]}, 'state.json': {}, 'timing.json': {}}
            for name, doc in data.items(): (root / name).write_text(json.dumps(doc))
            for name in ('screenshot.png', 'grayscale.png'): (root / name).write_bytes(b'synthetic')
            files = {p.name: {'path': p.name, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in root.iterdir()}
            scene = {'id': 'media-760-dark-de', 'status': 'captured', 'files': files}
            (root / 'scenes.json').write_text(json.dumps({'scenes': [scene]}))
            captured = runner.inventory(root, scene['id'])[0]
            self.assertEqual('captured', captured['status'])
            self.assertEqual(hashlib.sha256(json.dumps(captured['files'], sort_keys=True).encode()).hexdigest(), captured['checksum'])
            (root / 'control-tree.json').write_text('{}')
            self.assertEqual('missing', runner.inventory(root, scene['id'])[0]['status'])
            control['name'] = ''
            (root / 'control-tree.json').write_text(json.dumps({'elements': [control]}))
            files['control-tree.json']['sha256'] = hashlib.sha256((root / 'control-tree.json').read_bytes()).hexdigest()
            (root / 'scenes.json').write_text(json.dumps({'scenes': [scene]}))
            invalid = runner.inventory(root, scene['id'])[0]
            self.assertEqual('missing', invalid['status'])
            self.assertIn('unnamed control', invalid['error'])

    def test_wcag_known_pairs(self):
        self.assertEqual(21, checks.ratio('#000000', '#ffffff'))
        self.assertEqual(1, checks.ratio('#123456', '#123456'))

    def test_prechecks_report_unavailable_exports(self):
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            report = checks.run(ROOT.parent, Path(path))
            self.assertEqual(2, len(report['incomplete']))
            self.assertGreater(len(report['measurements']), 50)
            self.assertTrue(any(m.get('tokens') for m in report['measurements']))

if __name__ == '__main__': unittest.main()
