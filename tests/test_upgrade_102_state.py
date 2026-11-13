"""Regressions for the 1.0.2 -> 1.1.0 upgrade state capture and its assertions.

These do not open a profile or run a build: they check that the assertions in
tests/fixtures/upgrade-v1.0.2/upgrade_state.py actually fail when something is
lost, and pass on an untouched upgrade.
"""
import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[1] / 'tests' / 'fixtures' / 'upgrade-v1.0.2' / 'upgrade_state.py'
_spec = importlib.util.spec_from_file_location('upgrade_state', MODULE)
state = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(state)


def profile(files, stores, settings, user_version, extra=None):
    document = {
        'schema': 'meowchan.upgrade-102-state.v1', 'profile': '/synthetic',
        'files': {name: {'bytes': len(body), 'sha256': body} for name, body in files.items()},
        'file_count': len(files),
        'stores': {label: None for label in state.JSON_STORES},
        'settings': settings, 'media_files': [name for name in files if name.startswith('media/')],
        'media_manifests': {}, 'media_index_cache': None, 'startup_preview': None,
        'archive_db': {'user_version': user_version, 'application_id': 1, 'tables': ['archive_threads'],
                       'counts': {'archive_threads': 2}, 'digests': {'archive_threads': 'd'}},
        'work_state_db': None,
    }
    for label, count in stores.items():
        name, key = state.JSON_STORES[label]
        document['stores'][label] = {'file': name, 'count': count,
                                     'document': {key: list(range(count))}}
    document.update(extra or {})
    return document


class UpgradeAssertionTests(unittest.TestCase):
    def setUp(self):
        self.before = profile(
            {'settings.json': 'a', 'media-favorites.json': 'b', 'media/4chan/g/123/456.png': 'c'},
            {'favorites': 2}, {'VideoVolume': 31}, 5)
        self.after = profile(
            {'settings.json': 'a', 'media-favorites.json': 'b', 'media/4chan/g/123/456.png': 'c',
             'startup-preview.json': 'new'},
            {'favorites': 2}, {'VideoVolume': 31}, 12)

    def run_checks(self, before, after, states=None, results=(), read_only=None):
        states = states or [before, after]
        report = state.Report()
        state.file_checks(before, after, report)
        state.store_checks(before, after, report)
        state.archive_checks(before, after, report)
        state.settings_checks(before, after, states, list(results), report)
        state.idempotency_checks(states, [str(i) for i in range(len(states))],
                                 read_only or len(states), report)
        return report

    def failures(self, report):
        return [line for line in report.lines if ' FAIL ' in line]

    def test_an_untouched_upgrade_passes(self):
        self.assertEqual(self.failures(self.run_checks(self.before, self.after)), [])

    def test_a_deleted_store_fails(self):
        after = copy.deepcopy(self.after)
        del after['files']['media-favorites.json']
        self.assertTrue(any('no profile file deleted' in line
                            for line in self.failures(self.run_checks(self.before, after))))

    def test_a_truncated_file_fails(self):
        after = copy.deepcopy(self.after)
        after['files']['media-favorites.json'] = {'bytes': 0, 'sha256': 'empty'}
        failures = self.failures(self.run_checks(self.before, after))
        self.assertTrue(any('truncated' in line for line in failures))

    def test_a_lost_entry_fails_even_with_the_same_file_count(self):
        after = copy.deepcopy(self.after)
        after['stores']['favorites'] = {'file': 'media-favorites.json', 'count': 1, 'document': {'Entries': [0]}}
        failures = self.failures(self.run_checks(self.before, after))
        self.assertTrue(any('count kept: favorites' in line for line in failures))

    def test_a_changed_old_setting_fails(self):
        after = copy.deepcopy(self.after)
        after['settings']['VideoVolume'] = 50
        self.assertTrue(any('keeps every old value' in line
                            for line in self.failures(self.run_checks(self.before, after))))

    def test_a_new_setting_that_is_not_its_default_fails(self):
        result = {'Persisted': {'AllSettings': {'VideoVolume': 31, 'PrivateTagLimit': 0},
                                'DefaultSettings': {'VideoVolume': 100, 'PrivateTagLimit': 2000}}}
        failures = self.failures(self.run_checks(self.before, self.after, results=[result]))
        self.assertTrue(any('documented default' in line for line in failures))

    def test_matching_defaults_pass(self):
        result = {'Persisted': {'AllSettings': {'VideoVolume': 31, 'PrivateTagLimit': 2000},
                                'DefaultSettings': {'VideoVolume': 100, 'PrivateTagLimit': 2000}}}
        self.assertEqual(self.failures(self.run_checks(self.before, self.after, results=[result])), [])

    def test_a_missing_archive_migration_fails(self):
        after = copy.deepcopy(self.after)
        after['archive_db']['user_version'] = 5
        self.assertTrue(any('archive schema migrated' in line
                            for line in self.failures(self.run_checks(self.before, after))))

    def test_lost_archive_rows_fail(self):
        after = copy.deepcopy(self.after)
        after['archive_db']['counts']['archive_threads'] = 1
        self.assertTrue(any('archive rows kept' in line
                            for line in self.failures(self.run_checks(self.before, after))))

    def test_a_second_start_that_rewrites_a_store_is_not_idempotent(self):
        third = copy.deepcopy(self.after)
        third['files']['media-favorites.json'] = {'bytes': 1, 'sha256': 'other'}
        failures = self.failures(self.run_checks(self.before, self.after,
                                                 states=[self.before, self.after, third]))
        self.assertTrue(any('idempotent' in line for line in failures))

    def test_a_rewritten_database_with_equal_rows_is_idempotent(self):
        third = copy.deepcopy(self.after)
        third['files']['archive.db'] = {'bytes': 9, 'sha256': 'rewritten'}
        second = copy.deepcopy(self.after)
        second['files']['archive.db'] = {'bytes': 9, 'sha256': 'original'}
        self.assertEqual(self.failures(self.run_checks(self.before, second,
                                                       states=[self.before, second, third])), [])

    def test_the_regenerated_files_do_not_count_as_drift(self):
        third = copy.deepcopy(self.after)
        third['files']['startup-preview.json'] = {'bytes': 3, 'sha256': 'later'}
        self.assertEqual(self.failures(self.run_checks(self.before, self.after,
                                                       states=[self.before, self.after, third])), [])


class TagAssertionTests(unittest.TestCase):
    def test_a_missing_or_changed_v1_backup_fails(self):
        v1 = profile({'private-tags.json': 'v1bytes'}, {'private_tags': 1}, {}, 12)
        v1['stores']['private_tags']['document'] = {'Version': 1, 'Revision': 2, 'Tags': [0]}
        v2 = profile({'private-tags.json': 'v2bytes', 'private-tags.v1.bak': 'something-else'},
                     {'private_tags': 1}, {}, 12)
        v2['stores']['private_tags']['document'] = {'Version': 2, 'Revision': 3, 'Tags': [0]}
        report = state.Report()
        state.tag_checks([v1, v2], [], None, report)
        self.assertTrue(any('byte-identical' in line and ' FAIL ' in line for line in report.lines))

        v2['files']['private-tags.v1.bak'] = v1['files']['private-tags.json']
        report = state.Report()
        state.tag_checks([v1, v2], [], None, report)
        self.assertEqual([line for line in report.lines if ' FAIL ' in line], [])


class SnapshotTests(unittest.TestCase):
    def test_a_snapshot_records_files_stores_and_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'profile'
            (root / 'media' / '4chan' / 'g' / '123').mkdir(parents=True)
            (root / 'media' / '4chan' / 'g' / '123' / '456.png').write_bytes(b'png')
            (root / 'settings.json').write_text('{"VideoVolume": 31}')
            (root / 'media-favorites.json').write_text('{"Version": 1, "Entries": [1, 2]}')
            captured = state.snapshot(root)
        self.assertEqual(captured['file_count'], 3)
        self.assertEqual(captured['stores']['favorites']['count'], 2)
        self.assertIsNone(captured['stores']['private_tags'])
        self.assertEqual(captured['media_files'], ['media/4chan/g/123/456.png'])
        self.assertEqual(captured['settings'], {'VideoVolume': 31})
        self.assertIsNone(captured['archive_db'])


if __name__ == '__main__':
    unittest.main()
