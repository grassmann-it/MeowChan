"""Helper regressions for the AP04 post-migration checker (check_migrated.py).

No dotnet, no profile, no hand-authored receipt: only a stdlib-created SQLite file,
empty marker files in a temporary directory and local Git objects. A full check needs a
real three-process attempt; see tests/fixtures/upgrade-v1.0.2/README.md. Run:
    python3 -m unittest discover -s tests -p test_released102_migrated_check.py -v
"""
import importlib.util
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    'check_migrated', ROOT / 'tests/fixtures/upgrade-v1.0.2/check_migrated.py')
check = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check)


def has_object(revision):
    return subprocess.run(['git', 'cat-file', '-e', revision + '^{commit}'], cwd=ROOT,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


class MigratedCheckHelperTests(unittest.TestCase):
    def test_user_version_is_read_from_the_header_without_opening_the_database(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'archive.db'
            with sqlite3.connect(path) as db:
                db.execute('PRAGMA user_version=12')
                db.execute('CREATE TABLE t(x)')
            db.close()
            self.assertEqual(12, check.sqlite_user_version(path))
            other = Path(temp) / 'other.db'
            other.write_bytes(b'not a database' * 10)
            with self.assertRaises(ValueError):
                check.sqlite_user_version(other)

    def test_schema_constant_is_single_or_absent(self):
        pattern = r'const int SchemaVersion = (\d+);'
        self.assertEqual(5, check.schema_constant('internal const int SchemaVersion = 5;', pattern))
        self.assertIsNone(check.schema_constant('no marker', pattern))
        with self.assertRaises(ValueError):
            check.schema_constant('const int SchemaVersion = 1; const int SchemaVersion = 2;', pattern)

    @unittest.skipUnless(has_object(check.REVISION), 'pinned 1.0.2 objects not available locally')
    def test_released_expectations_come_from_the_pinned_release(self):
        self.assertEqual(5, check.archive_schema(check.REVISION))
        self.assertEqual(0, check.journal_schema(check.REVISION))  # 1.0.2 has no journal marker.

    def test_current_expectations_match_this_checkout_at_head(self):
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        source = (ROOT / check.ARCHIVE_SQL).read_text(encoding='utf-8')
        if source != check.git_text(head, check.ARCHIVE_SQL):
            self.skipTest('archive schema source has uncommitted changes')
        self.assertEqual(check.schema_constant(source, r'internal const int SchemaVersion = (\d+);'),
                         check.archive_schema(head))

    def test_tag_and_danbooru_files_including_backups_are_reported(self):
        with tempfile.TemporaryDirectory() as temp:
            tag_files = ['.booru-catalog.json.0123abcd.tmp', '.private-tags.json.0123abcd.tmp',
                         '.private-tags.v1.bak.0123abcd.tmp', 'booru-catalog.json', 'private-tags.json',
                         'private-tags.json.bak', 'private-tags.v1.bak']
            for name in tag_files + ['settings.json', 'media-favorites.json', '.settings.json.0123abcd.tmp']:
                (Path(temp) / name).touch()
            self.assertEqual(tag_files, check.forbidden_files(Path(temp)))

    def test_writer_file_names_are_covered(self):
        # Names the current tag and Danbooru writers use; a renamed writer must update the guard.
        names = [('libmeowchan/Tags/PrivateTagStore.cs', r'const string FileName = "([^"]+)";'),
                 ('libmeowchan/Tags/PrivateTagStore.cs', r'const string V1BackupFileName = "([^"]+)";'),
                 ('libmeowchan/Archive/BooruCatalog.cs', r'const string FileName = "([^"]+)";')]
        for path, pattern in names:
            found = re.findall(pattern, (ROOT / path).read_text(encoding='utf-8'))
            self.assertEqual(1, len(found), path)
            self.assertTrue(found[0].startswith(check.FORBIDDEN_PREFIXES), found[0])

    def test_source_is_clean_only_with_empty_status_captures(self):
        with tempfile.TemporaryDirectory() as temp:
            empty, dirty = Path(temp) / 'empty.txt', Path(temp) / 'dirty.txt'
            empty.write_bytes(b'')
            dirty.write_bytes(b' M Program.cs\n')
            self.assertEqual('unverified', check.clean_status([]))
            self.assertEqual('clean', check.clean_status([empty, empty]))
            for paths in ([dirty], [empty, dirty], [Path(temp) / 'missing.txt']):
                with self.assertRaises(ValueError):
                    check.clean_status(paths)

    def test_cycle_revision_requires_a_full_commit_id(self):
        sha = 'e3569eae4988f4f08a15778d62d88ae7be0e0c2f'
        self.assertEqual(sha, check.cycle_revision('1.0.2+' + sha))
        for version in ('1.0.2', '1.0.2+e3569eae', '1.0.2+' + sha.upper(), None):
            with self.assertRaises(ValueError):
                check.cycle_revision(version)


if __name__ == '__main__':
    unittest.main()
