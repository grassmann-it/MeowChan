#!/usr/bin/env python3
"""Read-only profile state capture and upgrade assertions for 1.0.2 -> 1.1.0.

Subcommands, all offline and read-only:

  snapshot PROFILE OUT.json      one complete observation of a profile directory
  check --states A.json B.json [...] [--results cycle-*.json] [--v1-tag-copy FILE]
                                 the upgrade assertions over those observations

A snapshot records every ordinary file with its size and SHA-256, the parsed
content of the stores that must survive the upgrade, the logical content of
archive.db / work-state.sqlite and the effective settings. Nothing is written
into the profile. The cycle results are the JSON files the upgrade consumer
(current/Program.cs) writes; they carry the settings, store readbacks, tag
state and media index observations of the running 1.1.0 build.
"""
import argparse
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

MAX_FILE = 64 * 1024 * 1024
MAX_FILES = 8192

# label -> (file name, JSON key holding the user-visible list)
JSON_STORES = {
    'favorites': ('media-favorites.json', 'Entries'),
    'thread_favorites': ('thread-favorites.json', 'Threads'),
    'ratings': ('media-ratings.json', 'Entries'),
    'bookmarks': ('post-bookmarks.json', 'bookmarks'),
    'saved_searches': ('archive-searches.json', 'searches'),
    'reader_groups': ('reader-groups.json', 'groups'),
    'watch_urls': ('saved_urls.json', 'ThreadUrls'),
    'private_tags': ('private-tags.json', 'Tags'),
}

# Lock, protocol and temporary files. Their content is per run, never user data.
VOLATILE = ('.meowchan-profile.lock', '.ochan-profile.lock', '.meowchan-profile.protocol',
            '.meowchan-store.lock', '.meowchan-export-lock-', 'plugin-state/state.lock')

# Files every start writes again by design, so their bytes cannot be compared for idempotency.
# What they must preserve is checked separately: the startup preview through its normalized
# content, the plugin state through the plugin set each start reports.
REGENERATED = ('startup-preview.json', 'plugin-state/state.json')

# One row per profile that marks the running host instance and start generation; it changes
# with every start by design and carries no user data.
PER_START_TABLES = ('archive_import_fence',)


def volatile(name):
    return any(part in name for part in VOLATILE)


def digest_file(path):
    hasher = hashlib.sha256()
    read = 0
    with path.open('rb') as stream:
        while chunk := stream.read(1024 * 1024):
            read += len(chunk)
            if read > MAX_FILE:
                raise ValueError('file too large: ' + str(path))
            hasher.update(chunk)
    return hasher.hexdigest(), read


def walk(profile):
    files = {}
    pending = [profile]
    while pending:
        for path in sorted(pending.pop().iterdir()):
            if path.is_symlink():
                raise ValueError('link in profile: ' + str(path))
            if path.is_dir():
                pending.append(path)
                continue
            if not path.is_file():
                raise ValueError('special file in profile: ' + str(path))
            if len(files) >= MAX_FILES:
                raise ValueError('too many files')
            sha, size = digest_file(path)
            files[path.relative_to(profile).as_posix()] = {'bytes': size, 'sha256': sha}
    return files


def sqlite_state(path):
    """Schema identity plus a content digest per table, so a rewritten file whose
    rows are unchanged is distinguishable from changed data."""
    if not path.is_file():
        return None
    connection = sqlite3.connect('file:' + str(path) + '?mode=ro', uri=True)
    try:
        state = {
            'user_version': connection.execute('pragma user_version').fetchone()[0],
            'application_id': connection.execute('pragma application_id').fetchone()[0],
            'tables': sorted(row[0] for row in connection.execute(
                "select name from sqlite_master where type='table'")),
            'counts': {}, 'digests': {},
        }
        for table in state['tables']:
            if table.startswith('sqlite_') or '_search' in table:
                continue
            if table in PER_START_TABLES:
                state['counts'][table] = connection.execute(
                    'select count(*) from "%s"' % table).fetchone()[0]
                continue
            rows = connection.execute('select * from "%s"' % table).fetchall()
            state['counts'][table] = len(rows)
            state['digests'][table] = hashlib.sha256(
                repr(sorted(repr(row) for row in rows)).encode()).hexdigest()
        return state
    finally:
        connection.close()


def snapshot(profile):
    profile = profile.absolute()
    if profile.is_symlink() or not profile.is_dir():
        raise ValueError('profile must be an ordinary directory')
    files = walk(profile)
    stores = {}
    for label, (name, key) in JSON_STORES.items():
        path = profile / name
        if not path.is_file():
            stores[label] = None
            continue
        document = json.loads(path.read_bytes())
        value = document.get(key)
        stores[label] = {'file': name, 'count': len(value) if isinstance(value, list) else None,
                         'document': document}
    settings_path = profile / 'settings.json'
    media = sorted(path.relative_to(profile).as_posix() for path in profile.glob('media/*/*/*/*')
                   if path.is_file() and not path.name.startswith('.'))
    manifests = {path.relative_to(profile).as_posix(): json.loads(path.read_bytes())
                 for path in sorted(profile.glob('media/*/*/*/.meowchan-manifest.json'))}
    cache = profile / 'cache' / 'media-index-v1.json'
    preview = profile / 'startup-preview.json'
    preview_state = None
    if preview.is_file():
        preview_state = json.loads(preview.read_bytes())
        preview_state.pop('capturedAtUtc', None)
    return {
        'schema': 'meowchan.upgrade-102-state.v1',
        'profile': str(profile),
        'files': files,
        'file_count': len(files),
        'stores': stores,
        'settings': json.loads(settings_path.read_bytes()) if settings_path.is_file() else None,
        'media_files': media,
        'media_manifests': manifests,
        'media_index_cache': ({'version': json.loads(cache.read_bytes()).get('Version'),
                               'bytes': cache.stat().st_size} if cache.is_file() else None),
        'startup_preview': preview_state,
        'archive_db': sqlite_state(profile / 'archive.db'),
        'work_state_db': sqlite_state(profile / 'work-state.sqlite'),
    }


# --- assertions -------------------------------------------------------------

class Report:
    def __init__(self):
        self.lines = []
        self.failed = 0

    def check(self, name, condition, detail=''):
        if not condition:
            self.failed += 1
        self.lines.append('%-52s %s %s' % (name, 'PASS' if condition else 'FAIL', detail))
        return condition

    def note(self, name, detail):
        self.lines.append('%-52s NOTE %s' % (name, detail))


def counts(state):
    return {label: (None if store is None else store['count'])
            for label, store in state['stores'].items()}


def short(names, limit=4):
    names = list(names)
    if not names:
        return '-'
    head = ', '.join(names[:limit])
    return head + ('' if len(names) <= limit else ' (+%d more)' % (len(names) - limit))


def file_checks(before, after, report):
    missing = sorted(name for name in before['files']
                     if name not in after['files'] and not volatile(name))
    report.check('no profile file deleted', not missing, short(missing))
    shrunk = sorted(name for name, record in before['files'].items()
                    if name in after['files'] and not volatile(name)
                    and after['files'][name]['bytes'] < record['bytes'])
    report.check('no profile file truncated', not shrunk, short(shrunk))
    changed = sorted(name for name, record in before['files'].items()
                     if name in after['files'] and not volatile(name)
                     and after['files'][name]['sha256'] != record['sha256'])
    # Only the two databases may legitimately be rewritten in place by a migration.
    unexpected = [name for name in changed if name not in ('archive.db', 'work-state.sqlite')]
    report.check('only the databases were rewritten', not unexpected, short(unexpected))
    report.note('rewritten by the upgrade', short(changed, 8))
    added = sorted(name for name in after['files']
                   if name not in before['files'] and not volatile(name))
    report.note('added by the upgrade', short(added, 6) + ' [%d files]' % len(added))


def store_checks(before, after, report):
    before_counts, after_counts = counts(before), counts(after)
    for label in sorted(before_counts):
        if before_counts[label] is None:
            continue
        report.check('count kept: ' + label, before_counts[label] == after_counts[label],
                     '%s -> %s' % (before_counts[label], after_counts[label]))
    for label, store in before['stores'].items():
        if store is None or after['stores'].get(label) is None:
            continue
        same = store['document'] == after['stores'][label]['document']
        report.check('content kept: ' + label, same, 'identical' if same else 'CHANGED')


def archive_checks(before, after, report):
    old, new = before['archive_db'], after['archive_db']
    if old and new:
        report.check('archive schema migrated', new['user_version'] > old['user_version'],
                     'user_version %s -> %s' % (old['user_version'], new['user_version']))
        for table, value in old['counts'].items():
            if table in new['counts']:
                report.check('archive rows kept: ' + table, new['counts'][table] >= value,
                             '%s -> %s' % (value, new['counts'][table]))
        lost = sorted(set(old['counts']) - set(new['counts']))
        report.check('no archive table dropped', not lost, short(lost))
    old_work, new_work = before['work_state_db'], after['work_state_db']
    if old_work and new_work:
        for table, value in old_work['counts'].items():
            report.check('work state rows kept: ' + table,
                         new_work['counts'].get(table, -1) >= value,
                         '%s -> %s' % (value, new_work['counts'].get(table)))
    report.check('no media file missing', set(before['media_files']) <= set(after['media_files']),
                 '%d -> %d' % (len(before['media_files']), len(after['media_files'])))
    report.check('media bytes unchanged',
                 all(before['files'][name]['sha256'] == after['files'][name]['sha256']
                     for name in before['media_files'] if name in after['files']))
    report.check('media manifests kept',
                 before['media_manifests'].keys() <= after['media_manifests'].keys(),
                 '%d manifests' % len(before['media_manifests']))


def settings_checks(before, after, states, results, report):
    old, new = before['settings'] or {}, after['settings'] or {}
    kept = sorted(key for key, value in old.items() if new.get(key) != value)
    report.check('settings file keeps every old value', not kept, short(kept))
    # The file is not rewritten on a read-only start, so the effective values come from the
    # running build: the consumer reports them next to this build's documented defaults.
    cycle = next((result for result in results if 'AllSettings' in result.get('Persisted', {})), None)
    if cycle is None:
        report.note('effective settings', 'no consumer result with AllSettings')
        return
    effective = cycle['Persisted']['AllSettings']
    defaults = cycle['Persisted']['DefaultSettings']
    wrong_old = sorted(key for key, value in old.items()
                       if key in effective and effective[key] != value)
    report.check('1.1.0 reads every old setting unchanged', not wrong_old, short(wrong_old))
    new_keys = sorted(key for key in effective if key not in old)
    report.check('new settings exist', bool(new_keys), '%d new: %s' % (len(new_keys), short(new_keys, 6)))
    # Until the user saves settings, the file keeps the old keys only. The first save must write
    # the new ones with their defaults, not with 0.
    written = next((state for state in states
                    if state['settings'] and set(state['settings']) - set(old)), None)
    if written is None:
        report.note('new settings in the file', 'no start saved settings in this run')
    else:
        added = sorted(set(written['settings']) - set(old))
        wrong = sorted(key for key in added
                       if key in defaults and written['settings'][key] != defaults[key])
        report.check('a settings save writes the new keys with their defaults', not wrong,
                     '%d keys written, %s' % (len(added), short(wrong)))
    off_default = sorted(key for key in new_keys if effective[key] != defaults[key])
    report.check('every new setting has its documented default', not off_default, short(off_default))
    zeroed = sorted(key for key in new_keys
                    if isinstance(effective[key], int) and not isinstance(effective[key], bool)
                    and effective[key] == 0 and defaults[key] == 0)
    report.check('no new numeric setting is zero', not zeroed, short(zeroed))


def same_rows(left, right, name):
    """True when a rewritten database still holds exactly the same rows and schema."""
    key = {'archive.db': 'archive_db', 'work-state.sqlite': 'work_state_db'}.get(name)
    return bool(key and left[key] and right[key] and left[key]['digests'] == right[key]['digests']
                and left[key]['user_version'] == right[key]['user_version'])


def drift_between(earlier, later):
    changed = [name for name, record in earlier['files'].items()
               if not volatile(name) and name not in REGENERATED and
               (name not in later['files'] or later['files'][name]['sha256'] != record['sha256'])]
    added = sorted(name for name in later['files']
                   if name not in earlier['files'] and not volatile(name))
    return [name for name in sorted(changed) if not same_rows(earlier, later, name)], added


def idempotency_checks(states, labels, read_only, report):
    after = states[1]
    for index in range(2, read_only):
        later = states[index]
        # A rewritten database is only drift when its rows changed.
        real, added = drift_between(after, later)
        report.check('idempotent: %s vs %s' % (labels[index], labels[1]),
                     not real and not added, short(real + ['+' + name for name in added]))
    if len(states) > read_only:
        # The last start opens a profile that is already upgraded and has since been written to.
        real, added = drift_between(states[-2], states[-1])
        report.check('start on an already upgraded profile changes nothing',
                     not real and not added, short(real + ['+' + name for name in added]))
    previews = [state['startup_preview'] for state in states if state.get('startup_preview')]
    if previews:
        report.check('the startup preview keeps the same content every start',
                     all(preview == previews[0] for preview in previews),
                     '%d threads, %d starts' % (previews[0].get('threadCount'), len(previews)))
    report.check('counts unchanged after the last start',
                 all(counts(states[-1])[label] >= (counts(states[1])[label] or 0)
                     for label in counts(states[1]) if counts(states[1])[label] is not None),
                 json.dumps(counts(states[-1]), sort_keys=True))


def tag_checks(states, results, v1_copy, report):
    v1 = next((state for state in states
               if state['stores']['private_tags'] and
               state['stores']['private_tags']['document'].get('Version') == 1), None)
    v2 = next((state for state in states
               if state['stores']['private_tags'] and
               state['stores']['private_tags']['document'].get('Version') == 2), None)
    report.check('a v1 private tag store was created', v1 is not None,
                 'none' if v1 is None else 'revision %s' % v1['stores']['private_tags']['document'].get('Revision'))
    report.check('the v1 store was upgraded to v2', v2 is not None)
    if v2 is None:
        return
    backup = 'private-tags.v1.bak'
    report.check('permanent v1 backup exists', backup in v2['files'])
    if v1 is not None and backup in v2['files'] and 'private-tags.json' in v1['files']:
        report.check('v1 backup is byte-identical to the v1 file',
                     v2['files'][backup] == v1['files']['private-tags.json'],
                     v2['files'][backup]['sha256'][:16])
    if v1_copy is not None:
        sha, size = digest_file(v1_copy)
        report.check('v1 backup equals the copy taken before the conversion',
                     backup in v2['files'] and v2['files'][backup] == {'bytes': size, 'sha256': sha},
                     sha[:16])
    for state in states[states.index(v2):]:
        if backup in state['files'] and state['files'][backup] != v2['files'][backup]:
            report.check('v1 backup is never rewritten', False, 'changed after the conversion')
            break
    else:
        report.check('v1 backup is never rewritten', True, 'stable in every later start')
    tags = next((result['PersistedTags'] for result in results
                 if result.get('Mode') == 'readback' and result.get('PersistedTags')), None)
    if tags:
        report.check('the tags are readable again after a restart',
                     tags['FormatVersion'] == 2 and len(tags['Tags']) == 2,
                     'v%s %s' % (tags['FormatVersion'], ','.join(tags['Tags'])))


def media_index_checks(states, results, report):
    index = next((result['Writes'] for result in results
                  if result.get('Mode') == 'media-index' and result.get('Writes')), None)
    if index is None:
        report.note('media index cache', 'no media-index cycle in this run')
        return
    report.check('the 1.0.2 profile had no media index cache', index['ExistedBefore'] is False)
    report.check('the rebuilt cache is written at version 4', index['WrittenVersion'] == 4,
                 'version %s, %s bytes' % (index['WrittenVersion'], index['Bytes']))
    report.check('the rebuilt index holds every medium',
                 index['BuiltEntries'] == index['ReadBackEntries'] == 2 and
                 index['BuiltThreads'] == index['ReadBackThreads'] == 2,
                 '%s entries / %s threads, read back %s / %s' %
                 (index['BuiltEntries'], index['BuiltThreads'],
                  index['ReadBackEntries'], index['ReadBackThreads']))
    report.check('no medium is missing from the index',
                 len(index['Files']) == len(states[-1]['media_files']),
                 '%d indexed / %d media files' % (len(index['Files']), len(states[-1]['media_files'])))


def consumer_checks(results, report):
    report.check('every start reached a stopped host and wrote its result',
                 all(result.get('HostStopped') for result in results), '%d starts' % len(results))
    report.check('no start attempted a network request',
                 all(not result.get('NetworkRequests') for result in results))
    versions = {result.get('CurrentVersion', '').split('+')[0] for result in results}
    report.check('every start ran the 1.1.0 build', versions == {'1.1.0'}, ','.join(sorted(versions)))
    pids = [result.get('ProcessId') for result in results]
    report.check('every start was its own process', len(set(pids)) == len(pids))
    fleets = [tuple(sorted((plugin['PluginId'], plugin['Enabled'], plugin['Active'])
                           for plugin in (result.get('Plugins') or {}).get('Plugins', [])))
              for result in results]
    if fleets and fleets[0]:
        report.check('every start sees the same plugins, enabled and active',
                     all(fleet == fleets[0] for fleet in fleets),
                     '%d plugins in %d starts' % (len(fleets[0]), len(fleets)))
    searches = [result['Persisted'].get('SavedSearches') for result in results
                if 'SavedSearches' in result.get('Persisted', {})]
    if searches:
        report.check('1.1.0 reads the saved searches',
                     all(entry['State'] == 'Ready' and entry['Count'] == 2 and
                         all(search['CanApply'] for search in entry['Searches']) for entry in searches),
                     '%d of %d starts, 2 applicable searches' % (len(searches), len(results)))
    groups = [result['Persisted'].get('ReaderGroups') for result in results
              if 'ReaderGroups' in result.get('Persisted', {})]
    if groups:
        report.check('1.1.0 reads the reader groups',
                     all(entry['State'] == 'Ready' and entry['Count'] == 2 for entry in groups),
                     '%d of %d starts' % (len(groups), len(results)))


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    one = sub.add_parser('snapshot')
    one.add_argument('profile', type=Path)
    one.add_argument('output', type=Path)
    many = sub.add_parser('check')
    many.add_argument('--states', type=Path, nargs='+', required=True)
    many.add_argument('--results', type=Path, nargs='*', default=[])
    many.add_argument('--v1-tag-copy', type=Path)
    many.add_argument('--read-only-states', type=int, default=0,
                      help='how many leading --states came from read-only starts (default: all)')
    args = parser.parse_args()

    if args.command == 'snapshot':
        if args.output.exists():
            parser.error('output must not exist')
        state = snapshot(args.profile)
        args.output.write_text(json.dumps(state, indent=1, sort_keys=True) + '\n', encoding='utf-8')
        print('STATE CAPTURED files=%d archive=%s tags=%s' % (
            state['file_count'],
            state['archive_db']['user_version'] if state['archive_db'] else 'none',
            (state['stores']['private_tags'] or {}).get('document', {}).get('Version', 'none')))
        return 0

    states = [json.loads(path.read_bytes()) for path in args.states]
    labels = [path.stem for path in args.states]
    results = [json.loads(path.read_bytes()) for path in args.results]
    if len(states) < 2:
        parser.error('check needs at least a before and an after state')
    report = Report()
    before, after = states[0], states[1]
    file_checks(before, after, report)
    store_checks(before, after, report)
    archive_checks(before, after, report)
    settings_checks(before, after, states, results, report)
    idempotency_checks(states, labels, args.read_only_states or len(states), report)
    if results:
        consumer_checks(results, report)
        media_index_checks(states, results, report)
    tag_checks(states, results, args.v1_tag_copy, report)
    print('\n'.join(report.lines))
    checks = sum(1 for line in report.lines if ' NOTE ' not in line)
    print('UPGRADE CHECK %s checks=%d failed=%d' % (
        'FAILED' if report.failed else 'OK', checks, report.failed))
    return 1 if report.failed else 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError, IndexError, json.JSONDecodeError, sqlite3.Error) as error:
        print('STATE FAILED: ' + str(error), file=sys.stderr)
        sys.exit(2)
