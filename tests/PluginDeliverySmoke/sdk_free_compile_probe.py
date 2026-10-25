#!/usr/bin/env python3
"""Compile every delivered editable file and describe it with SDK-inaccessible helpers.

Requires a self-contained Linux bundle. No network, developer SDK, project build,
GUI or acquisition acceptance is implied. The installation is read-only.
"""
import base64
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys

from sdk_free_worker_probe import envelope, send

SOURCE_PLUGIN_IDS = {
    'Lainchan.cs': 'meowchan.source.lainchan',
    'Kohlchan.cs': 'meowchan.source.kohlchan',
    'Wizchan.cs': 'meowchan.source.wizchan',
    'Endchan.cs': 'meowchan.source.endchan',
    'ImageDimensionsFilter.cs': 'meowchan.example.image-dimensions',
    'FileRulesFilter.cs': 'meowchan.filter.file-rules',
    'HashBlocklistFilter.cs': 'meowchan.filter.hash-blocklist',
    'AnimationFormatFilter.cs': 'meowchan.filter.animation-format',
    'PngMetadataFilter.cs': 'meowchan.filter.png-metadata',
}


def hash_parts(*parts):
    result = hashlib.sha256()
    for part in parts:
        encoded = part.encode('utf-8')
        result.update(struct.pack('!i', len(encoded)))
        result.update(encoded)
    return result.hexdigest()


def isolated(bundle, work, executable, *arguments):
    return ['bwrap', '--die-with-parent', '--unshare-net', '--unshare-pid', '--clearenv',
            '--ro-bind', '/usr', '/usr', '--ro-bind', '/lib', '/lib', '--ro-bind', '/lib64', '/lib64',
            '--tmpfs', '/usr/share/dotnet', '--proc', '/proc', '--dev', '/dev', '--tmpfs', '/tmp',
            '--ro-bind', str(bundle), '/application', '--bind', str(work), '/work', '--chdir', '/work',
            '--setenv', 'PATH', '/no-sdk', '--setenv', 'HOME', '/no-home', '--setenv', 'DOTNET_ROOT', '/no-sdk',
            '/application/' + executable, *arguments]


def main():
    bundle = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not shutil.which('bwrap'):
        raise SystemExit('bubblewrap is required for the SDK-inaccessible compiler proof')
    sources = bundle / 'plugins/sources'
    assert {path.name for path in sources.iterdir()} == set(SOURCE_PLUGIN_IDS)
    manifest = json.loads((bundle / 'plugin-compiler/toolchain.json').read_text())
    toolchain = hash_parts(*(manifest[key] for key in (
        'TargetFramework', 'Options', 'CompilerFingerprint', 'SdkFingerprint', 'ReferenceFingerprint')))
    results = []
    for name, expected_id in SOURCE_PLUGIN_IDS.items():
        work = output / name.removesuffix('.cs')
        work.mkdir()
        source = (sources / name).read_bytes()
        source_hash = hashlib.sha256(source).hexdigest()
        fingerprint = hash_parts(source_hash, toolchain)
        request = dict(Source=base64.b64encode(source).decode(), OriginalPath='/application/plugins/sources/' + name,
                       CompilationFingerprint=fingerprint, ToolchainFingerprint=toolchain, OutputPath='/work/compiled.dll')
        completed = subprocess.run(isolated(bundle, work, 'plugin-compiler/MeowChan.PluginCompiler',
                                            '--compile', '/application/plugin-compiler'),
                                   input=json.dumps(request).encode(), capture_output=True, timeout=30)
        (work / 'compiler-stderr.txt').write_bytes(completed.stderr)
        (work / 'compiler-response.json').write_bytes(completed.stdout)
        response = json.loads(completed.stdout)
        assert completed.returncode == 0 and response['Success'], response
        compiled = work / 'compiled.dll'
        assert compiled.is_file() and compiled.stat().st_size > 0
        with (work / 'worker-stderr.txt').open('wb') as stderr:
            worker = subprocess.Popen(isolated(bundle, work, 'plugin-worker/MeowChan.PluginWorker'),
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, start_new_session=True)
            try:
                hello = send(worker, envelope('hello', 'hello', {'requiredOperations': ['describe']}))
                assert 'describe' in hello['operations']
                description = send(worker, envelope('describe', 'compiled', {
                    'assemblyPath': '/work/compiled.dll', 'resourcePaths': {},
                    'configuration': {'revision': 'delivery-config', 'pluginSettings': {'schemaVersion': '1', 'values': []},
                                      'sourceSettings': {}}, 'validateConfiguration': False}))
                assert description['pluginId'] == expected_id, description
                worker.stdin.close()
                assert worker.wait(timeout=5) == 0
                results.append(dict(source=name, sourceSha256=source_hash, compilationFingerprint=fingerprint,
                                    assemblySha256=hashlib.sha256(compiled.read_bytes()).hexdigest(), pluginId=expected_id,
                                    diagnostics=response['Diagnostics'], sourceIds=[row['sourceId'] for row in description['sources']],
                                    filterKeys=[row['key'] for row in description['filters']]))
            finally:
                if worker.poll() is None:
                    worker.kill(); worker.wait(timeout=5)
                if not worker.stdin.closed: worker.stdin.close()
                worker.stdout.close()
    report = dict(sdkAccessible=False, networkAvailable=False, installationReadOnly=True,
                  nativePlatform='linux-x64', toolchainFingerprint=toolchain, results=results)
    (output / 'RESULT.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(passed=len(results), report=str(output / 'RESULT.json'))))


if __name__ == '__main__':
    main()
