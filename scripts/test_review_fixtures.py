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

    def test_screenshot_without_dump_rejected(self):
        with tempfile.TemporaryDirectory(dir=os.environ['COMMANDCODE_SCRATCHPAD']) as path:
            root = Path(path)
            scene = {'id': 'media-760-dark-de', 'status': 'captured', 'files': {'screenshot.png': {'path': 'missing.png', 'sha256': '0'}}}
            (root / 'scenes.json').write_text(json.dumps({'scenes': [scene]}))
            cells = runner.inventory(root, scene['id'])
            self.assertEqual('missing', cells[0]['status'])
            self.assertIn('error', cells[0])

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
