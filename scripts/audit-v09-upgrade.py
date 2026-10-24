#!/usr/bin/env python3
"""Read retained synthetic-upgrade evidence without starting the application."""
from pathlib import Path
import argparse
import base64
import hashlib
import json
import sqlite3
import xml.etree.ElementTree as ET


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def confined(root, relative):
    path = (root / relative).resolve(strict=True)
    if not path.is_relative_to(root) or not path.is_file():
        raise ValueError('Evidence file is outside the explicit root: ' + str(path))
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--private-root', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--receipt-sha256', required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--source-manifest', type=Path, required=True)
    parser.add_argument('--trx', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    private = args.private_root.resolve(strict=True)
    profile = args.profile.resolve(strict=True)
    assert profile != private and profile.is_relative_to(private)
    source = args.source.resolve(strict=True)
    receipt_path = confined(profile, 'v09-receipt.json')
    assert sha(receipt_path) == args.receipt_sha256.lower()
    receipt = json.loads(receipt_path.read_text())
    assert receipt['SourceRevision'] == '8ec0cf50b270e0551a06d3ad61eff0f448bd70ff'
    assert len(receipt['Threads']) == 2 and len(receipt['Files']) == 14 and receipt['LegacyProfileLock']
    manifest = json.loads(args.source_manifest.read_text())
    expected_sources = {name: entry['sha256'] if isinstance(entry, dict) else entry
                        for name, entry in manifest['files'].items()}
    checked_sources = {name: sha(confined(source, name)) for name in expected_sources}
    assert checked_sources == expected_sources

    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    tree = ET.parse(args.trx)
    results = tree.findall('.//t:UnitTestResult', ns)
    assert len(results) == 1
    result = results[0]
    assert result.attrib['testName'].endswith('DesktopUpgradeIntegrationTests.Genuine09ProfileMigratesToDeliveredPluginsAndRestartsTwiceOffline')
    assert result.attrib['outcome'] == 'Passed'
    counters = tree.find('.//t:Counters', ns).attrib
    assert counters['total'] == counters['executed'] == counters['passed'] == '1'
    assert counters['failed'] == counters['notExecuted'] == counters['timeout'] == '0'
    assembly = confined(source, 'tests/MeowChan.DesktopTests/bin/Release/net10.0/MeowChan.DesktopTests.dll')
    assert {row.attrib['codeBase'] for row in tree.findall('.//t:TestMethod', ns)} == {str(assembly)}

    originals = {name: sha(confined(profile, name)) for name in receipt['Files']}
    assert {k: v.upper() for k, v in originals.items()} == {k: v.upper() for k, v in receipt['Files'].items()}
    reexports = []
    for startup in range(3):
        for old in receipt['Threads']:
            name = old['Id']['Source']
            directory = (profile / f'reexport-{startup}' / name).resolve(strict=True)
            assert directory.is_relative_to(profile)
            metadata_path = confined(directory, 'meowchan-export.json')
            metadata = json.loads(metadata_path.read_text())
            assert metadata['Thread'] == old['Id'] and metadata['PostCount'] == 2
            for item in metadata['Files']:
                path = confined(directory, item['Path'])
                assert path.stat().st_size == item['Length'] and sha(path).lower() == item['Sha256'].lower()
            media = [item for item in metadata['Files'] if item['Path'].startswith('media/')]
            assert len(media) == 1 and media[0]['Sha256'].lower() == old['Asset']['Sha256'].lower()
            reexports.append({'startup': startup, 'source': name, 'metadataSha256': sha(metadata_path), 'originalSha256': media[0]['Sha256']})
    favorites = json.loads(confined(profile, 'media-favorites.json').read_text())['Entries']
    assert sorted(favorites, key=lambda row: row['Key']['Thread']['Source']) == sorted([row['Favorite'] for row in receipt['Threads']], key=lambda row: row['Key']['Thread']['Source'])
    history = json.loads(confined(profile, 'export-history.json').read_text())['Entries']
    for old in receipt['Threads']:
        saved = next(row for row in history if row['Thread'] == old['Id'])
        assert saved['ExportId'] == old['Export']['ExportId'] and saved['Generation'] == old['Export']['Generation']

    state_path = confined(profile, 'plugin-state/state.json')
    envelope = json.loads(state_path.read_text())
    payload = base64.b64decode(envelope['payload'], validate=True)
    assert hashlib.sha256(payload).hexdigest().lower() == envelope['sha256'].lower()
    state = json.loads(payload)
    expected_plugins = {
        'meowchan.official.4chan', 'meowchan.official.8kun',
        'meowchan.source.lainchan', 'meowchan.source.kohlchan',
        'meowchan.source.wizchan', 'meowchan.source.endchan',
        'meowchan.example.image-dimensions',
    }
    assert len(state['plugins']) == len(expected_plugins)
    assert {plugin['pluginId'] for plugin in state['plugins']} == expected_plugins
    plugins = []
    for plugin in state['plugins']:
        assert plugin['enabled'] and plugin['active'] and plugin['problem'] is None
        assert plugin['lifecycleRevision'] == 3
        artifact = plugin['active']['artifact']
        path = Path(artifact['assemblyPath']).resolve(strict=True)
        assert path.is_relative_to(profile) and sha(path) == artifact['assemblySha256'].lower()
        plugins.append({'pluginId': plugin['pluginId'], 'lifecycleRevision': plugin['lifecycleRevision'], 'executionRevision': artifact['executionRevision'], 'assemblySha256': sha(path)})
    # Observe the migrated catalog without opening a writer or checkpointing it.
    catalog = confined(profile, 'archive.db')
    with sqlite3.connect(catalog.as_uri() + '?mode=ro', uri=True) as database:
        schema_version = database.execute('PRAGMA user_version').fetchone()[0]
        assert schema_version == 4
        assert database.execute('PRAGMA application_id').fetchone()[0] == 0x4F43484E
        assert database.execute('PRAGMA journal_mode').fetchone()[0] == 'delete'
        assert database.execute('PRAGMA quick_check').fetchall() == [('ok',)]
        integrity_columns = {
            'archive_attachments': {'expected_sha256', 'attachment_revision_key'},
            'media_decisions': {'expected_sha256', 'acquisition_revision_key', 'attachment_references_json'},
        }
        for table, required in integrity_columns.items():
            assert required <= {row[1] for row in database.execute('PRAGMA table_info(' + table + ')')}
        assert database.execute('SELECT count(*) FROM archive_threads').fetchone()[0] == 2
    legacy_manifests = []
    for old in receipt['Threads']:
        directory = Path(old['MediaDirectory']).resolve(strict=True)
        assert directory.is_relative_to(profile)
        path = confined(directory, '.meowchan-manifest.json')
        relative = path.relative_to(profile).as_posix()
        assert relative in receipt['Files']
        assert sha(path).upper() == receipt['Files'][relative].upper()
        media_manifest = json.loads(path.read_text())
        assert media_manifest['Version'] in (1, 2, 3)
        if media_manifest['Version'] == 3:
            assert isinstance(media_manifest['AttachmentBindings'], dict)
        legacy_manifests.append({'path': relative, 'wireVersion': media_manifest['Version'],
                                 'sha256': sha(path), 'originalBytesPreserved': True})
    # New acquisition/binding V3 behavior is exercised separately by the production
    # package process fixture. Passive reading must not rewrite genuine old manifests.
    report = {'passed': True, 'scope': 'One genuine synthetic 0.9 migration and three in-process paused DesktopSession lifetimes; no GUI/installer/OS-matrix or independent-process upgrade claim.',
              'applicationStartedByAudit': False, 'profile': str(profile), 'sourceManifest': str(args.source_manifest),
              'sourceManifestSha256': sha(args.source_manifest), 'sourceFilesVerified': len(checked_sources),
              'trx': str(args.trx), 'trxSha256': sha(args.trx), 'testResult': result.attrib,
              'testAssembly': str(assembly), 'testAssemblySha256': sha(assembly),
              'assemblyBindingLimit': 'TRX CodeBase and retained output hash; no loaded-memory hash claim.',
              'oldReceiptSha256': sha(receipt_path), 'originalFilesVerified': originals, 'reexports': reexports,
              'favoritesVerified': len(favorites), 'exportIdentitiesVerified': len(receipt['Threads']),
              'pluginStateSha256': sha(state_path), 'plugins': plugins,
              'archiveCatalog': {'sha256': sha(catalog), 'schemaVersion': schema_version, 'integrityColumnsVerified': {name: sorted(columns) for name, columns in integrity_columns.items()}},
              'legacyManifestCompatibility': legacy_manifests,
              'manifestV3BindingLimit': 'Original legacy manifests stay byte-identical; fresh V3 writes and exact bindings require separate package process evidence.'}
    with args.output.open('x') as output:
        output.write(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': True, 'originalFiles': len(originals), 'reexports': len(reexports), 'report': str(args.output)}))


if __name__ == '__main__':
    main()
