#!/usr/bin/env python3
"""Describe delivered plugins and exercise source operations with an SDK-inaccessible Linux worker.

An isolated packaging probe, not the production session/HTTP/filter acceptance suite.
"""
import base64
import hashlib
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


def write_frame(process, value):
    data = json.dumps(value).encode()
    process.stdin.write(struct.pack('<i', len(data)) + data); process.stdin.flush()


def send(process, value, http_fixture=None):
    write_frame(process, value)
    response = receive(process)
    if http_fixture is not None:
        source_id, url, body = http_fixture
        assert response['direction'] == 'service' and response['operation'] == 'http', response
        assert response['parentRequestId'] == value['requestId'], response
        request = response['payload']
        assert (request['sourceId'], request['method'], request['url']) == (source_id, 'Get', url), request
        payload = {'kind': 'response', 'statusCode': 200, 'finalUri': url,
                   'headers': {'Content-Type': 'application/json'},
                   'body': base64.b64encode(body.encode()).decode('ascii')}
        write_frame(process, dict(response, direction='serviceResult', payload=payload))
        response = receive(process)
    assert response['requestId'] == value['requestId'] and response['operation'] == value['operation'], response
    assert response['direction'] == 'response', response
    assert response['version'] == value['version'] and response['workerInstance'] == value['workerInstance'], response
    return response['payload']


source_probes = {
    'meowchan.official.4chan': (
        '4chan', 'catalog', {'board': {'sourceId': '4chan', 'boardId': 'g'}},
        'https://a.4cdn.org/g/catalog.json', '[{"threads":[{"no":42}]}]'),
    'meowchan.official.danbooru': (
        'danbooru', 'booru.search', {'sourceId': 'danbooru', 'query': 'cat', 'page': 1, 'limit': 20, 'beforePostId': '1004'},
        'https://danbooru.donmai.us/posts.json?tags=cat&page=b1004&limit=20', '[{"id":1003,"rating":"g"}]'),
}


def assert_source_result(operation, result):
    assert result['kind'] == 'snapshot', result
    if operation == 'catalog':
        threads = result['snapshot']['threads']
        assert [thread['id'] for thread in threads] == [{'sourceId': '4chan', 'boardId': 'g', 'threadId': '42'}], result
    elif operation == 'booru.search':
        posts = result['page']['posts']
        assert [post['id'] for post in posts] == [{'sourceId': 'danbooru', 'postId': '1003'}], result


def main():
    bundle = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not shutil.which('bwrap'):
        raise SystemExit('bubblewrap is required for an SDK-inaccessible worker proof')
    results = []
    expected_ids = {'MeowChan.Plugin.FourChan': 'meowchan.official.4chan', 'MeowChan.Plugin.EightKun': 'meowchan.official.8kun',
                    'MeowChan.Plugin.Danbooru': 'meowchan.official.danbooru', 'MeowChan.Plugin.VichanFamily': 'meowchan.source.vichan', 'MeowChan.Plugin.Kohlchan': 'meowchan.source.kohlchan',
                    'MeowChan.ImageDimensionsFilter': 'meowchan.example.image-dimensions',
                    'MeowChan.Plugin.Endchan': 'meowchan.source.endchan',
                    'MeowChan.FileRulesFilter': 'meowchan.filter.file-rules',
                    'MeowChan.HashBlocklistFilter': 'meowchan.filter.hash-blocklist',
                    'MeowChan.AnimationFormatFilter': 'meowchan.filter.animation-format',
                    'MeowChan.PngMetadataFilter': 'meowchan.filter.png-metadata',
                    'MeowChan.Plugin.Dvach': 'meowchan.source.2ch',
                    'MeowChan.Plugin.JschanFamily': 'meowchan.source.jschan',
                    'MeowChan.Plugin.SevenChan': 'meowchan.source.sevenchan',
                    'MeowChan.Plugin.BbwChan': 'meowchan.source.bbwchan',
                    'MeowChan.Plugin.CapybaraChan': 'meowchan.source.capybarachan',
                    'MeowChan.Plugin.EightchanMoe': 'meowchan.source.eightchanmoe',
                    'MeowChan.Plugin.Fchan': 'meowchan.source.fchan',
                    'MeowChan.Plugin.E621': 'meowchan.official.e621'}
    blocked_ids = {'meowchan.source.anonimageboards', 'meowchan.source.gurocx'}
    manifests = sorted((bundle / 'plugins').glob('*.plugin.json'))
    assert {path.stem.removesuffix(".plugin") for path in manifests} == set(expected_ids)
    for index, manifest_path in enumerate(manifests):
        manifest = json.loads(manifest_path.read_text())
        plugin_id = expected_ids[manifest_path.name.removesuffix('.plugin.json')]
        source_probe = source_probes.get(plugin_id)
        required = ['describe'] if source_probe is None else ['describe', 'http', source_probe[1]]
        relative_assembly = manifest['AssemblyRelativePath']
        assembly_sha = hashlib.sha256((bundle / 'plugins' / relative_assembly).read_bytes()).hexdigest()
        assert assembly_sha == manifest['AssemblySha256'], manifest
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
                hello = send(process, envelope('hello', 'hello-1', {'requiredOperations': required}))
                assert set(required).issubset(hello['operations']), hello
                source_settings = {} if source_probe is None else {source_probe[0]: {'schemaVersion': '1', 'values': []}}
                description = send(process, envelope('describe', 'describe-1', {
                    'assemblyPath': '/application/plugins/' + relative_assembly, 'resourcePaths': {},
                    'configuration': {'revision': 'delivery-config', 'pluginSettings': {'schemaVersion': '1', 'values': []},
                                      'sourceSettings': source_settings},
                    'validateConfiguration': source_probe is not None}))
                assert description['pluginId'] == plugin_id, description
                if plugin_id in blocked_ids:
                    # Documented technical blocks register no route; they must say so explicitly.
                    assert not description['sources'] and not description['filters'], description
                    assert any(item['key'] == 'block' for item in description['information']), description
                else:
                    assert description['sources'] or description['filters'], description
                operation = None
                if source_probe is not None:
                    source_id, operation, request, url, body = source_probe
                    result = send(process, envelope(operation, 'source-1', request), (source_id, url, body))
                    assert_source_result(operation, result)
                process.stdin.close()
                assert process.wait(timeout=5) == 0
                results.append({'manifest': manifest_path.name, 'pluginId': plugin_id,
                                'assemblySha256': assembly_sha, 'sdkFingerprint': manifest['SdkFingerprint'],
                                'sdkVersion': description['sdkVersion'], 'operation': operation,
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
