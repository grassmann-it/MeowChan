"""Regressions for the 1.0.2 -> 1.1.0 upgrade state capture and its assertions.

No product, build or database is opened: assertion inputs exercise upgrade_state.py
and check_migrated.py, including AP04's three-cycle effective-settings gate.
These helper tests are not evidence of an executed product upgrade.
"""
import copy
import importlib.util
import json
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

MODULE = Path(__file__).resolve().parents[1] / 'tests' / 'fixtures' / 'upgrade-v1.0.2' / 'upgrade_state.py'
_spec = importlib.util.spec_from_file_location('upgrade_state', MODULE)
state = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(state)

_check_spec = importlib.util.spec_from_file_location('check_migrated', MODULE.with_name('check_migrated.py'))
migrated = importlib.util.module_from_spec(_check_spec)
_check_spec.loader.exec_module(migrated)


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


class AP04EffectiveSettingsTests(unittest.TestCase):
    """Assertion inputs only: no generated predecessor profile or product execution."""

    def setUp(self):
        self.released = {'VideoVolume': 31, 'MaxParallelDownloads': 2, 'CompactDensity': True,
                         'LayoutProfiles': {'Barebones': {'CompactDensity': True}}}
        self.defaults = {'VideoVolume': 70, 'MaxParallelDownloads': 5, 'CompactDensity': False,
                         'TagQueueUndoDepth': 20, 'TaggingCardCount': 5, 'ReaderInlineVideo': False,
                         'ReaderInlineVideoMaxPlayers': 2, 'GalleryMaxZoomPercent': 1600,
                         'WorkViewNoticePending': False}
        effective = {**self.defaults, **{k: v for k, v in self.released.items() if k != 'LayoutProfiles'},
                     'WorkViewNoticePending': True}
        self.cycles = [{'Persisted': {'AllSettings': copy.deepcopy(effective),
                                     'DefaultSettings': copy.deepcopy(self.defaults)}} for _ in range(3)]
        self.released_bytes = json.dumps(self.released).encode()
        self.released_sha = migrated.digest(self.released_bytes)

    def check(self, cycles=None, data=None):
        migrated.check_effective_settings(self.released_bytes if data is None else data,
                                          self.released_sha, self.cycles if cycles is None else cycles)

    def test_all_three_correct_cycles_pass_including_the_legacy_notice(self):
        self.check()

    def test_each_cycle_rejects_wrong_new_defaults_and_old_values(self):
        # Late-cycle drift must fail too, not just an invalid first open.
        for index in range(3):
            for key, value in [('TagQueueUndoDepth', 0), ('TaggingCardCount', 2),
                               ('ReaderInlineVideo', True), ('ReaderInlineVideoMaxPlayers', 1),
                               ('GalleryMaxZoomPercent', 0), ('VideoVolume', 70),
                               ('MaxParallelDownloads', 5), ('CompactDensity', 1),
                               ('WorkViewNoticePending', False)]:
                with self.subTest(cycle=index, key=key):
                    cycles = copy.deepcopy(self.cycles)
                    cycles[index]['Persisted']['AllSettings'][key] = value
                    with self.assertRaisesRegex(ValueError, key):
                        self.check(cycles)

    def test_missing_or_empty_settings_maps_never_count_as_success(self):
        for index in range(3):
            for key in ('AllSettings', 'DefaultSettings'):
                for replacement in (None, {}):
                    with self.subTest(cycle=index, key=key, value=replacement):
                        cycles = copy.deepcopy(self.cycles)
                        if replacement is None:
                            del cycles[index]['Persisted'][key]
                        else:
                            cycles[index]['Persisted'][key] = replacement
                        with self.assertRaises(ValueError):
                            self.check(cycles)

    def test_a_missing_effective_key_or_old_scalar_is_rejected(self):
        for both in (False, True):
            cycles = copy.deepcopy(self.cycles)
            del cycles[2]['Persisted']['AllSettings']['MaxParallelDownloads']
            if both:
                del cycles[2]['Persisted']['DefaultSettings']['MaxParallelDownloads']
            with self.assertRaises(ValueError):
                self.check(cycles)

    def test_defaults_cannot_drift_between_same_build_cycles(self):
        cycles = copy.deepcopy(self.cycles)
        for key in ('AllSettings', 'DefaultSettings'):
            cycles[2]['Persisted'][key]['TagQueueUndoDepth'] = 0
        with self.assertRaises(ValueError):
            self.check(cycles)

    def test_missing_cycles_and_unbound_or_duplicate_settings_are_rejected(self):
        with self.assertRaises(ValueError):
            self.check(self.cycles[:2])
        with self.assertRaisesRegex(ValueError, 'digest'):
            self.check(data=self.released_bytes + b' ')
        duplicate = b'{"VideoVolume":31,"VideoVolume":70}'
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            migrated.check_effective_settings(duplicate, migrated.digest(duplicate), self.cycles)

    def test_migrated_entrypoint_rejects_late_drift_before_reading_database_markers(self):
        # Minimal oracle-protocol inputs, NOT a generated old profile or runtime proof.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            revision = 'e' * 40
            cycles = copy.deepcopy(self.cycles)
            cycles[2]['Persisted']['AllSettings']['TaggingCardCount'] = 2
            files = {}
            rows = []
            for index, cycle in enumerate(cycles):
                cycle.update(Schema='meowchan.released-102-current-cycle.v1', HostStopped=True,
                             NetworkRequests=[], Profile=str(root), CurrentVersion='1.1.0+' + revision)
                name = f'cycle-{index}.json'
                data = json.dumps(cycle).encode()
                files[name] = data
                rows.append({'Cycle': index, 'Result': name, 'Sha256': migrated.digest(data)})
            receipt = json.dumps({'Synthetic': True, 'SourceRevision': migrated.REVISION,
                                  'Files': {'settings.json': self.released_sha}}).encode()
            files.update({'v102-receipt.json': receipt, 'released-receipt.json': receipt,
                          'released-settings.json': self.released_bytes})
            final = {'Schema': 'meowchan.released-102-rest-package-result.v1', 'Synthetic': True,
                     'SourceRevision': migrated.REVISION, 'SubsequentOfflineProcessRestarts': 2,
                     'Cycles': rows, 'ReceiptSha256': migrated.digest(receipt)}
            argv = ['check_migrated.py', '--profile', str(root), '--results', str(root),
                    '--expect-revision', revision]
            with patch('sys.argv', argv), patch.object(migrated, 'document', return_value=final), \
                    patch.object(migrated, 'read', side_effect=lambda path: files[path.name]), \
                    patch.object(migrated, 'archive_schema', side_effect=AssertionError('must not read a DB marker')):
                with self.assertRaisesRegex(ValueError, 'cycle-2.*TaggingCardCount'):
                    migrated.main()

    def test_explicit_legacy_notice_value_is_preserved(self):
        # The exception is only for a missing old key, not a blanket waiver.
        released = json.dumps({**self.released, 'WorkViewNoticePending': False}).encode()
        with self.assertRaisesRegex(ValueError, 'WorkViewNoticePending'):
            migrated.check_effective_settings(released, migrated.digest(released), self.cycles)
        cycles = copy.deepcopy(self.cycles)
        for cycle in cycles:
            cycle['Persisted']['AllSettings']['WorkViewNoticePending'] = False
        migrated.check_effective_settings(released, migrated.digest(released), cycles)


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
