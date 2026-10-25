#!/usr/bin/env python3
"""Describe all delivered plugins through the actual SDK-inaccessible Linux worker.

An isolated packaging probe, not the production session/HTTP/filter acceptance suite.
"""
import json
import os
from pathlib import Path
import select
import shutil
import struct
import subprocess
import sys
import time


def exact(process, count, deadline):
    chunks = []
    while count:
        remaining = deadline - time.monotonic()
        if remaining <= 0 or not select.select([process.stdout], [], [], remaining)[0]:
            raise TimeoutError('Worker protocol response timed out')
        chunk = os.read(process.stdout.fileno(), count)
        if not chunk:
            raise EOFError('Worker exited before completing its response')
        chunks.append(chunk); count -= len(chunk)
    return b''.join(chunks)


def receive(process):
    deadline = time.monotonic() + 10
    size = struct.unpack('<i', exact(process, 4, deadline))[0]
    if not 0 < size <= 16 * 1024 * 1024:
        raise ValueError('Worker response exceeded protocol size')
    return json.loads(exact(process, size, deadline))


def envelope(operation, request, payload):
    return {'version': 1, 'direction': 'request', 'operation': operation, 'requestId': request,
            'workerInstance': 'delivery-worker', 'buildRevision': 'delivery-build', 'configurationRevision': 'delivery-config',
            'resourceId': None, 'generation': None, 'ruleRevision': None, 'grantRevision': None,
            'parentRequestId': None, 'payload': payload}


def send(process, value):
    data = json.dumps(value).encode()
    process.stdin.write(struct.pack('<i', len(data)) + data); process.stdin.flush()
    response = receive(process)
    assert response['requestId'] == value['requestId'] and response['operation'] == value['operation'], response
    assert response['direction'] == 'response', response
    return response['payload']


def main():
    bundle = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not shutil.which('bwrap'):
        raise SystemExit('bubblewrap is required for an SDK-inaccessible worker proof')
    results = []
    expected_ids = {'MeowChan.Plugin.FourChan': 'meowchan.official.4chan', 'MeowChan.Plugin.EightKun': 'meowchan.official.8kun',
                    'MeowChan.Plugin.Lainchan': 'meowchan.source.lainchan', 'MeowChan.Plugin.Kohlchan': 'meowchan.source.kohlchan',
                    'MeowChan.ImageDimensionsFilter': 'meowchan.example.image-dimensions',
                    'MeowChan.Plugin.Wizchan': 'meowchan.source.wizchan', 'MeowChan.Plugin.Endchan': 'meowchan.source.endchan',
                    'MeowChan.FileRulesFilter': 'meowchan.filter.file-rules',
                    'MeowChan.HashBlocklistFilter': 'meowchan.filter.hash-blocklist',
                    'MeowChan.AnimationFormatFilter': 'meowchan.filter.animation-format',
                    'MeowChan.PngMetadataFilter': 'meowchan.filter.png-metadata',
                    'MeowChan.Plugin.Leftypol': 'meowchan.source.leftypol',
                    'MeowChan.Plugin.Dvach': 'meowchan.source.2ch',
                    'MeowChan.Plugin.Uboachan': 'meowchan.source.uboachan',
                    'MeowChan.Plugin.Zzzchan': 'meowchan.source.zzzchan',
                    'MeowChan.Plugin.SushiChan': 'meowchan.source.sushichan'}
    manifests = sorted((bundle / 'plugins').glob('*.plugin.json'))
    assert {path.stem.removesuffix(".plugin") for path in manifests} == set(expected_ids)
    for index, manifest_path in enumerate(manifests):
        manifest = json.loads(manifest_path.read_text())
        work = output / (str(index) + ' isolated work')
        work.mkdir()
        args = ['bwrap', '--die-with-parent', '--unshare-net', '--unshare-pid', '--clearenv',
                '--ro-bind', '/usr', '/usr', '--ro-bind', '/lib', '/lib', '--ro-bind', '/lib64', '/lib64',
                '--tmpfs', '/usr/share/dotnet', '--proc', '/proc', '--dev', '/dev', '--tmpfs', '/tmp',
                '--ro-bind', str(bundle), '/application', '--bind', str(work), '/work', '--chdir', '/work',
                '--setenv', 'PATH', '/no-sdk', '--setenv', 'HOME', '/no-home', '--setenv', 'DOTNET_ROOT', '/no-sdk',
                '/application/plugin-worker/MeowChan.PluginWorker']
        with (work / 'stderr.txt').open('wb') as stderr:
            process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, start_new_session=True)
            try:
                hello = send(process, envelope('hello', 'hello-1', {'requiredOperations': ['describe']}))
                assert 'describe' in hello['operations']
                description = send(process, envelope('describe', 'describe-1', {
                    'assemblyPath': '/application/plugins/' + manifest['AssemblyRelativePath'], 'resourcePaths': {},
                    'configuration': {'revision': 'delivery-config', 'pluginSettings': {'schemaVersion': '1', 'values': []}, 'sourceSettings': {}},
                    'validateConfiguration': False}))
                assert description['pluginId'] == expected_ids[manifest_path.name.removesuffix('.plugin.json')], description
                assert description['sources'] or description['filters']
                process.stdin.close()
                assert process.wait(timeout=5) == 0
                results.append({'manifest': manifest_path.name, 'pluginId': description['pluginId'],
                                'sourceIds': [source['sourceId'] for source in description['sources']],
                                'filterKeys': [item['key'] for item in description['filters']], 'exitCode': process.returncode})
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=5)
                if not process.stdin.closed: process.stdin.close()
                process.stdout.close()
    report = {'sdkAccessible': False, 'networkAvailable': False, 'installationReadOnly': True,
              'foreignWorkingDirectory': True, 'nativePlatform': 'linux-x64', 'results': results}
    (output / 'RESULT.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': len(results), 'report': str(output / 'RESULT.json')}))


if __name__ == '__main__':
    main()
