#!/usr/bin/env python3
"""Offline assertions for the AP04 phases after the migration (plugins, writes, read-back).

Runs AFTER check_migrated.py. It starts no product code, opens no database and writes nothing. It reads

  <results>/cycle-0..2.json          the three current-side processes of the upgrade test (plugin state only)
  <writes>/write-host.json           consumer mode write-host (settings, queue options, pause state, thread tag)
  <writes>/write-local-tag.json      consumer mode write-local-tag (store-level loose-file tag, v1 -> v2)
  <writes>/readback-1/2.json         consumer mode readback: two more offline restarts after the writes
  <writes>/snapshot-before.json      runner-made copies of settings.json / saved_urls.json before the first write
  <writes>/snapshot-v1/              runner-made copy of private-tags.json after write-host (still version 1)

and the profile. The v1 backup is compared with the runner's own copy, never with a value the product reports.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

REQUIRED_PLUGINS = ('meowchan.official.4chan', 'meowchan.official.8kun', 'meowchan.official.danbooru')
THREAD_TAG, LOCAL_TAG = 'ap04-thread-tag', 'ap04-local-tag'
V1_BACKUP = 'private-tags.v1.bak'
FORBIDDEN_PREFIXES = ('booru-catalog.',)
CHANGED_SETTINGS = ('VideoVolume', 'MaxParallelDownloads')


def require(value, message):
    if not value:
        raise ValueError(message)


def load(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink(), 'missing result: ' + path.name)
    return json.loads(path.read_bytes())


def digest(data):
    return hashlib.sha256(data).hexdigest()


def stable_plugins(cycle, name):
    """(id, enabled, active, active execution revision) of every plugin, after the activation rules of the start."""
    plugins = cycle.get('Plugins')
    require(isinstance(plugins, dict) and plugins.get('Plugins'), name + ': no plugin state reported')
    return [(p['PluginId'], p['Enabled'], p['Active'], p['ActiveExecutionRevision']) for p in plugins['Plugins']]


def check_plugins(cycles, required=REQUIRED_PLUGINS):
    """Every start reports the same plugin state; the required plugins are enabled, active and without a problem."""
    require(cycles, 'no cycles')
    first_name, first = cycles[0]
    reference = stable_plugins(first, first_name)
    for name, cycle in cycles:
        require(stable_plugins(cycle, name) == reference, f'{name}: plugin state differs from {first_name}')
        by_id = {p['PluginId']: p for p in cycle['Plugins']['Plugins']}
        for plugin_id in required:
            plugin = by_id.get(plugin_id)
            require(plugin is not None, f'{name}: plugin missing: {plugin_id}')
            require(plugin['Enabled'] and plugin['Active'] and plugin['Problem'] is None,
                    f"{name}: plugin not active: {plugin_id} ({plugin['Problem']})")
        for plugin in by_id.values():
            require(not plugin['Enabled'] or (plugin['Active'] and plugin['Problem'] is None),
                    f"{name}: enabled plugin without a working build: {plugin['PluginId']} ({plugin['Problem']})")
    enabled = [p for p in reference if p[1]]
    return f'starts={len(cycles)} plugins={len(reference)} enabled-active={len(enabled)} required={len(required)}'


def check_cycle_shape(name, cycle, profile, revision, version, mode):
    require(cycle.get('Schema') == 'meowchan.released-102-current-cycle.v1' and cycle.get('Mode', 'observe') == mode,
            f'{name}: shape/mode mismatch')
    require(cycle['HostStopped'] is True and cycle['NetworkRequests'] == [] and cycle['Profile'] == str(profile),
            f'{name}: host not stopped, network attempted or other profile')
    require(cycle['CurrentVersion'] == f'{version}+{revision}', f"{name}: built as {cycle['CurrentVersion']}")


def without(settings, names):
    return {k: v for k, v in settings.items() if k not in names}


def parse_json_file(path):
    return json.loads(Path(path).read_bytes())


def check_writes(profile, results, writes, revision, version):
    """The write and read-back phases. Returns (summary lines, observed notes)."""
    profile, results, writes = Path(profile), Path(results), Path(writes)
    cycle2 = load(results / 'cycle-2.json')
    host = load(writes / 'write-host.json')
    local = load(writes / 'write-local-tag.json')
    readbacks = [load(writes / f'readback-{i}.json') for i in (1, 2)]
    check_cycle_shape('write-host', host, profile, revision, version, 'write-host')
    check_cycle_shape('write-local-tag', local, profile, revision, version, 'write-local-tag')
    for index, readback in enumerate(readbacks, 1):
        check_cycle_shape(f'readback-{index}', readback, profile, revision, version, 'readback')

    # Settings, queue options and pause state (host level), persisted and read again.
    w = host['Writes']
    require(w['Settings']['VolumeAfter'] == 47 and w['Settings']['VolumeAfter'] != w['Settings']['VolumeBefore'],
            'settings write not visible in the running host')
    require(w['Settings']['ParallelAfter'] == w['Settings']['ParallelBefore'] + 1, 'queue option write not visible in the running host')
    require(w['IsPaused'] is True, 'pause state was not kept')
    tag = w['Tag']
    require(tag['Name'] == THREAD_TAG and tag['DefinedChanged'] and tag['AssignedChanged'] and tag['FormatVersion'] == 1,
            'thread tag write did not create a version 1 file')
    tags = host['HostTags']
    require(tags['State'] == 'Ready' and tags['Tags'] == [THREAD_TAG] and tags['FormatVersion'] == 1, 'host does not read its own tag write')
    require(host['Persisted']['Settings']['VideoVolume'] == 47, 'settings write not persisted at shutdown')

    # Files on disk: settings and queue options persisted, the existing values kept.
    before = parse_json_file(writes / 'snapshot-before' / 'settings.json')
    after = parse_json_file(profile / 'settings.json')
    require(after['VideoVolume'] == 47 and after['MaxParallelDownloads'] == w['Settings']['ParallelAfter'], 'settings.json lacks the written values')
    require(all(after.get(k) == v for k, v in without(before, CHANGED_SETTINGS).items()), 'settings.json lost or changed unrelated values')
    urls_before = parse_json_file(writes / 'snapshot-before' / 'saved_urls.json')
    urls_after = parse_json_file(profile / 'saved_urls.json')
    for key in ('ThreadUrls', 'BoardUrls', 'DownloadDirectories', 'SuppressedThreadUrls'):
        require(urls_after.get(key) == urls_before.get(key), 'saved_urls.json changed: ' + key)

    # v1 tag file -> v2 with the permanent v1 backup, byte-identical.
    v1_dir = writes / 'snapshot-v1'
    meta = load(v1_dir / 'meta.json')
    require(meta['v1_backup_existed_before'] is False, 'a v1 backup existed before the conversion')
    v1_bytes = (v1_dir / 'private-tags.json').read_bytes()
    require(parse_json_file(v1_dir / 'private-tags.json')['Version'] == 1 and digest(v1_bytes) == meta['sha256'], 'snapshot is not the version 1 file')
    require(local['Writes']['Level'] == 'store' and local['Writes']['Changed'] and local['Writes']['FormatVersion'] == 2
            and local['Writes']['LocalAssignments'] == 1 and local['PersistedTags']['FormatVersion'] == 2, 'conversion write did not produce version 2')
    backup = profile / V1_BACKUP
    require(backup.is_file() and not backup.is_symlink(), V1_BACKUP + ' does not exist')
    require(backup.read_bytes() == v1_bytes, V1_BACKUP + ' is not byte-identical to the version 1 file')
    current = (profile / 'private-tags.json').read_bytes()
    require(current != v1_bytes and parse_json_file(profile / 'private-tags.json')['Version'] == 2, 'private-tags.json is not the version 2 file')
    require(not [p.name for p in profile.iterdir() if p.name.startswith('.' + V1_BACKUP)], 'temporary backup file left behind')
    # Downgrade way back as documented: the backup restored as private-tags.json is a valid version 1 document (checked on bytes only).
    require(json.loads(backup.read_bytes())['Version'] == 1, V1_BACKUP + ' is not a version 1 document')
    rotating = profile / 'private-tags.json.bak'
    rotating_note = 'rotating-bak=v1' if rotating.is_file() and rotating.read_bytes() == v1_bytes else 'rotating-bak=other'

    # Read-back restarts: everything written is readable, nothing else moved.
    expected_tags = sorted([THREAD_TAG, LOCAL_TAG])
    for index, readback in enumerate(readbacks, 1):
        name = f'readback-{index}'
        state = readback['HostTags']
        require(state['State'] == 'Ready' and state['FormatVersion'] == 2 and state['Tags'] == expected_tags
                and state['ThreadAssignments'] == 1 and state['LocalAssignments'] == 1 and state['MediaAssignments'] == 0,
                f'{name}: host tag state differs from what was written')
        thread_tags = {json.dumps(t['Thread'], sort_keys=True): t['Tags'] for t in state['ThreadTags']}
        require(thread_tags[json.dumps(tag['Thread'], sort_keys=True)] == [THREAD_TAG], f'{name}: the thread tag is not on its thread')
        require(readback['PersistedTags']['FormatVersion'] == 2 and readback['PersistedTags']['Tags'] == expected_tags, f'{name}: store state differs')
        observed, reference = readback['Observed'], cycle2['Observed']
        require(observed['Settings']['VideoVolume'] == 47 and without(observed['Settings'], ('VideoVolume',)) ==
                without(reference['Settings'], ('VideoVolume',)), f'{name}: settings differ beyond the written value')
        for key in observed:
            require(key == 'Settings' or observed[key] == reference[key], f'{name}: {key} differs from the last migration cycle')
        require(readback['Persisted']['Watch'] == cycle2['Persisted']['Watch'], f'{name}: persisted watch snapshot differs')
        require(readback['Persisted']['Settings']['VideoVolume'] == 47, f'{name}: persisted setting differs')
    plugin_line = check_plugins([('cycle-0', load(results / 'cycle-0.json')), ('cycle-1', load(results / 'cycle-1.json')),
                                 ('cycle-2', cycle2), ('write-host', host), ('write-local-tag', local),
                                 ('readback-1', readbacks[0]), ('readback-2', readbacks[1])])
    # Unchanged until the very end, after all read-backs.
    require(backup.read_bytes() == v1_bytes, V1_BACKUP + ' changed during the read-back restarts')
    leftovers = sorted(p.name for p in profile.iterdir() if p.name.removeprefix('.').startswith(FORBIDDEN_PREFIXES))
    require(not leftovers, 'Danbooru state appeared without a Danbooru write: ' + ', '.join(leftovers))
    lines = [
        f"WRITES OK settings=volume {w['Settings']['VolumeBefore']}->47,parallel {w['Settings']['ParallelBefore']}->{w['Settings']['ParallelAfter']} "
        f"pause=kept thread-tag=created,v1 conversion=store-level,v2 backup={V1_BACKUP},byte-identical,sha256={digest(v1_bytes)[:16]} {rotating_note}",
        f'READBACK OK restarts=2 tags={",".join(expected_tags)} settings=47 archive/favorites/ratings/bookmarks/watch=unchanged',
        'PLUGINS OK ' + plugin_line,
    ]
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--results', type=Path, required=True)
    parser.add_argument('--writes', type=Path, required=True)
    parser.add_argument('--expect-revision', required=True)
    parser.add_argument('--expect-version', required=True)
    args = parser.parse_args()
    require(re.fullmatch(r'[0-9a-f]{40}', args.expect_revision), 'expected revision must be a full commit id')
    for line in check_writes(args.profile, args.results, args.writes, args.expect_revision, args.expect_version):
        print(line)
    print('NOT AN AUTHENTIC USER PROFILE; NOT A UI/PACKAGE/PLATFORM/RELEASE ACCEPTANCE')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, IndexError, json.JSONDecodeError) as error:
        print('CHECK FAILED: ' + str(error), file=sys.stderr)
        sys.exit(1)
