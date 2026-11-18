"""Offline timing-profile generation; timings never count as coverage evidence."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('timings', ROOT / 'scripts/update-macos-test-timings.py')
timings = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(timings)


class TimingTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)

    def trx(self, name, durations):
        root = ET.Element('TestRun')
        definitions = ET.SubElement(root, 'TestDefinitions')
        test = ET.SubElement(definitions, 'UnitTest', id='case')
        ET.SubElement(test, 'TestMethod', className='N.Class', name='Theory')
        results = ET.SubElement(root, 'Results')
        for duration in durations:
            ET.SubElement(results, 'UnitTestResult', testId='case', duration=duration, outcome='Failed')
        path = self.root / name
        ET.ElementTree(root).write(path)
        return path

    def test_durations_sum_deferred_rows_but_count_distinct_case_ids(self):
        elapsed, counts = timings.trx_observations(self.trx('a.trx', ['00:00:01.250', '00:00:02.500']))
        self.assertEqual({'N.Class': 3750}, elapsed)
        self.assertEqual({'N.Class': 1}, counts)

    def test_profile_takes_max_observed_class_cost_and_is_order_independent(self):
        a = self.trx('a.trx', ['00:00:01'])
        b = self.trx('b.trx', ['00:00:03'])
        profile = timings.generate([('a', a), ('b', b)], [])
        self.assertEqual(profile, timings.generate([('b', b), ('a', a)], []))
        self.assertEqual(dict(milliseconds=3000, cases=1), profile['classes']['N.Class'])
        self.assertEqual(3000, profile['fallback_case_ms'])
        self.assertEqual(64, len(profile['sources'][0]['sha256']))

    def test_partial_console_is_only_a_lower_bound_not_extrapolated_or_identity(self):
        trx = self.trx('a.trx', ['00:00:01'])
        console = self.root / 'partial.log'
        console.write_text('  Passed N.Class.Theory(arg: "(), odd, [42 s]") [750 ms]\n'
                           '  Passed N.Class.Theory(arg: "random") [2 s]\n')
        profile = timings.generate([('a', trx)], [('partial', console)])
        self.assertEqual(dict(milliseconds=2750, cases=1), profile['classes']['N.Class'])
        self.assertEqual('partial-console', profile['sources'][1]['kind'])
        with self.assertRaises(ValueError):
            timings.generate([], [('partial', console)])

    def test_empty_input_rejected_and_zero_durations_have_positive_weight(self):
        with self.assertRaises(ValueError):
            timings.generate([], [])
        profile = timings.generate([('skipped', self.trx('a.trx', ['00:00:00']))], [])
        self.assertEqual(1, profile['classes']['N.Class']['milliseconds'])
        self.assertEqual(1, profile['fallback_case_ms'])


if __name__ == '__main__':
    unittest.main()
