#!/usr/bin/env python3
"""Read-only, offline synthetic source/receipt validation; not an upgrade test."""
import argparse
import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import xml.etree.ElementTree as ET

REVISION = 'eec6c0169f8d52f7481ea0bf7febfd20981e97c3'
OVERLAY = 'tests/Upgrade102FixtureGenerator/'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nAAAAABJRU5ErkJggg==')
PNG_SHA1 = base64.b64encode(hashlib.sha1(PNG).digest()).decode('ascii')
URLS = {'https://boards.4chan.org/g/thread/123', 'https://8kun.top/g/res/123.html'}
LEGACY_CHAN_STATES = {
    'meowchan.source.lainchan': False,
    'meowchan.source.leftypol': False,
    'meowchan.source.sushichan': False,
    'meowchan.source.uboachan': False,
    'meowchan.source.wizchan': True,
    'meowchan.source.zzzchan': True,
}


def require(value, message):
    if not value:
        raise ValueError(message)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON key: ' + key)
        result[key] = value
    return result


def read(path, limit=32 * 1024 * 1024):
    require(not path.is_symlink() and path.is_file(), 'not an ordinary file: ' + str(path))
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    require(len(data) <= limit, 'file budget: ' + str(path))
    return data


def document(path):
    return json.loads(read(path), object_pairs_hook=unique)


def safe(root, name):
    require(isinstance(name, str) and name and '\\' not in name, 'invalid relative path')
    parts = PurePosixPath(name).parts
    require(not name.startswith('/') and '/'.join(parts) == name and
            all(p not in ('.', '..') for p in parts), 'unsafe relative path: ' + name)
    path = root
    for part in parts:
        path = path / part
        require(not path.is_symlink(), 'link in fixture: ' + name)
    return path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def thread_id(value):
    require(value['Board'] == 'g' and value['Thread'] == '123', 'unexpected thread identity')
    require(value['Source'] in ('4chan', '8kun'), 'unexpected source')
    return value['Source']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--profile', type=Path)
    args = parser.parse_args()
    source = args.source.absolute()
    require(not source.is_symlink() and source.is_dir(), 'source must be ordinary directory')
    manifest_path = source / 'UPGRADE-102-SOURCE.json'
    manifest = document(manifest_path)
    require(manifest['schema'] == 'meowchan.synthetic-upgrade-102-source.v2' and
            manifest['synthetic'] is True and manifest['source_commit'] == REVISION, 'source provenance mismatch')
    files, overlay = manifest['files'], manifest['overlay']
    require(0 < len(files) <= 20000 and set(overlay) ==
            {OVERLAY + n for n in ('Program.cs', 'UpgradeFixtureGenerator.csproj')}, 'source inventory shape')
    require(not set(files).intersection(overlay), 'overlay shadows release source')
    total = 0
    for name, record in sorted({**files, **overlay}.items()):
        data = read(safe(source, name))
        total += len(data)
        require(total <= 512 * 1024 * 1024 + 1024 * 1024, 'source total budget')
        require(len(data) == record['bytes'] and digest(data) == record['sha256'], 'source mismatch: ' + name)
        if name in files:
            require(record['git_mode'] in ('100644', '100755', '120000'), 'unsupported git mode')
            require(hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() ==
                    record['git_blob'], 'git blob mismatch: ' + name)
        else:
            require(data == read(Path(__file__).with_name(PurePosixPath(name).name)), 'overlay is not this reviewed generator')
    require(manifest['prepare_sha256'] == digest(read(Path(__file__).with_name('prepare.py'))), 'preparer changed')
    require(ET.fromstring(read(source / 'Directory.Build.props')).findtext('.//Version') == '1.0.2', 'release Version mismatch')
    print(f'SOURCE OK synthetic=true revision={REVISION} files={len(files)} overlay=2')
    if args.profile is None:
        return
    profile = args.profile.absolute()
    require(not profile.is_symlink() and profile.is_dir(), 'profile must be ordinary directory')
    receipt = document(profile / 'v102-receipt.json')
    require(receipt['Schema'] == 'meowchan.synthetic-upgrade-102.v1' and receipt['Synthetic'] is True and
            receipt['SourceRevision'] == REVISION and receipt['ProductVersion'] == '1.0.2+' + REVISION,
            'receipt provenance mismatch')
    require(receipt['SourceManifestSha256'].lower() == digest(read(manifest_path)), 'receipt/source binding mismatch')
    expected = receipt['Files']
    require(0 < len(expected) <= 4096 and 'v102-receipt.json' not in expected, 'receipt file inventory shape')
    actual = set()
    # Reject links (including directories), extra and missing files before hashing.
    pending = [profile]
    entries = 0
    while pending:
        for path in pending.pop().iterdir():
            entries += 1
            require(entries <= 8192 and not path.is_symlink(), 'profile traversal budget/link')
            if path.is_dir():
                pending.append(path)
            else:
                require(path.is_file(), 'special file in profile')
                actual.add(path.relative_to(profile).as_posix())
    require(actual == set(expected) | {'v102-receipt.json'}, 'extra/missing profile files')
    total = 0
    for name, sha in expected.items():
        data = read(safe(profile, name))
        total += len(data)
        require(total <= 128 * 1024 * 1024, 'profile total budget')
        require(digest(data) == sha.lower(), 'profile digest mismatch: ' + name)
    threads = receipt['Threads']
    require(len(threads) == 2 and {thread_id(t['Id']) for t in threads} == {'4chan', '8kun'}, 'thread set mismatch')
    for thread in threads:
        require(sorted(p['PostId'] for p in thread['Posts']) == ['123', '124'], 'post set mismatch')
        require(thread['Asset']['Length'] == len(PNG) and thread['Asset']['Sha256'].lower() == digest(PNG) and
                thread['Asset']['Sha1'] == PNG_SHA1, 'media integrity mismatch')
    legacy = receipt.get('LegacyChanPlugins')
    require(legacy == LEGACY_CHAN_STATES, 'legacy chan plugin state mismatch')
    inventory = receipt['Inventory']
    favorites = inventory['MediaFavorites']
    require(len(favorites) == 2 and {thread_id(f['Key']['Thread']) for f in favorites} == {'4chan', '8kun'}, 'favorites mismatch')
    for favorite in favorites:
        require(favorite['Length'] == len(PNG) and favorite['Key']['Sha256'].lower() == digest(PNG), 'favorite bytes mismatch')
    ratings = inventory['Ratings']
    require(len(ratings) == 2 and {thread_id(r['Key']['Thread']): r['Value'] for r in ratings} == {'4chan': 5, '8kun': 2}, 'ratings mismatch')
    marks = inventory['ThreadFavorites']['Threads']
    require(len(marks) == 2 and {thread_id(t) for t in marks} == {'4chan', '8kun'}, 'thread favorites mismatch')
    bookmarks = inventory['Bookmarks']['Bookmarks']
    require(len(bookmarks) == 2 and {thread_id(b['Key']['Thread']) for b in bookmarks} == {'4chan', '8kun'}, 'bookmarks mismatch')
    require(all(b['Key']['Post'] == '124' and b['Note'] == 'SYNTHETIC 1.0.2 retained reply\nNo personal content.' for b in bookmarks), 'bookmark content mismatch')
    urls = inventory['Watch']['ThreadUrls']
    require(len(urls) == 2 and set(urls) == URLS, 'watch set mismatch')
    settings = inventory['Settings']
    for key, expected_value in {'DownloadsPaused': True, 'VideoVolume': 31, 'VideoStartMuted': True,
                               'GalleryContextOpen': True, 'AutomaticUpdateChecks': False,
                               'ArchiveThreadLayout': 'Table', 'ArchiveSplitWidth': 431, 'WindowWidth': 1123}.items():
        require(settings[key] == expected_value, 'settings mismatch: ' + key)
    allowed = {'https://a.4cdn.org/boards.json', 'https://8kun.top/boards.json',
               'https://a.4cdn.org/g/thread/123.json', 'https://8kun.top/g/res/123.json',
               'https://i.4cdn.org/g/456.png', 'https://nerv.8kun.top/file_store/456.png'}
    require(0 < len(receipt['Requests']) <= 256 and set(receipt['Requests']) <= allowed, 'request inventory mismatch')
    print(f'RECEIPT OK synthetic=true threads=2 posts=4 favorites=2 ratings=2 bookmarks=2 files={len(expected)}')
    print('NOT AN AUTHENTIC USER PROFILE; NOT A MIGRATION/RESTART/RELEASE ACCEPTANCE')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        print('CHECK FAILED: ' + str(error), file=sys.stderr)
        sys.exit(1)
