#!/usr/bin/env python3
"""Run a bounded real-provider probe from a frozen detached release checkout."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time

RESERVE = 2 * 1024**3


def candidates(source, board, catalog):
    threads = [thread for page in catalog for thread in page['threads']]
    eligible = [t for t in threads if t.get('images') == 0 and t.get('ext', '').lower() in ('.jpg', '.png')
                and 0 < t.get('fsize', 0) <= 200_000 and not t.get('extra_files') and not t.get('filedeleted')]
    if not eligible:
        raise RuntimeError('No eligible tiny single-image thread for ' + source + '; no extra requests will be attempted.')
    t = min(eligible, key=lambda t: (t.get('replies', 0), t['fsize']))
    url = f'https://boards.4chan.org/{board}/thread/{t["no"]}' if source == '4chan' else f'https://8kun.top/{board}/res/{t["no"]}.html'
    return {'source': source, 'url': url, 'op_bytes': t['fsize'], 'replies': t.get('replies', 0)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--source', required=True, type=Path, help='Exact clean detached checkout; never the interactive app checkout')
    parser.add_argument('--output', required=True, type=Path, help='New evidence directory')
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    def git(*argv):
        return subprocess.check_output(['git', *argv], cwd=source, text=True).strip()
    commit = git('rev-parse', '--verify', '--end-of-options', args.commit + '^{commit}')
    if git('rev-parse', 'HEAD') != commit or git('status', '--porcelain'):
        raise RuntimeError('Expected a clean checkout of the explicit commit.')
    if subprocess.run(['git', 'symbolic-ref', '-q', 'HEAD'], cwd=source, stdout=subprocess.DEVNULL).returncode == 0:
        raise RuntimeError('Expected detached checkout.')
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    environment = dict(os.environ)
    for key, folder in [('TMPDIR', 'tmp'), ('NUGET_PACKAGES', 'packages'), ('NUGET_HTTP_CACHE_PATH', 'http-cache')]:
        path = out / folder; path.mkdir(); environment[key] = str(path)
    result = {'source_commit': commit, 'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'passed': False, 'commands': [], 'minimum_reserve_bytes': RESERVE}
    def save():
        (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    def run(name, argv):
        if shutil.disk_usage(out).free < RESERVE + 512 * 1024**2:
            raise RuntimeError('Insufficient storage above 2 GiB reserve plus working allowance.')
        print('START ' + name, flush=True)
        with (out / (name + '.log')).open('w') as log:
            log.write('COMMAND ' + json.dumps(argv) + '\n'); log.flush()
            step = subprocess.Popen(argv, cwd=source, env=environment,
                                    stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            deadline = time.monotonic() + 300
            try:
                while step.poll() is None:
                    if time.monotonic() >= deadline:
                        raise TimeoutError('Command deadline exceeded: ' + name)
                    if shutil.disk_usage(out).free < RESERVE:
                        raise RuntimeError('Command crossed the 2 GiB storage reserve: ' + name)
                    try:
                        step.wait(timeout=0.5)
                    except subprocess.TimeoutExpired:
                        pass
                if shutil.disk_usage(out).free < RESERVE:
                    raise RuntimeError('Command crossed the 2 GiB storage reserve: ' + name)
            except BaseException:
                # Kill the whole command group, including nested dotnet/MSBuild children.
                try:
                    os.killpg(step.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                step.wait()
                raise
        result['commands'].append({'name': name, 'argv': argv, 'exit_code': step.returncode}); save()
        if step.returncode:
            raise subprocess.CalledProcessError(step.returncode, argv)
    try:
        harness = out / 'harness'; harness.mkdir()
        originals = source / 'scripts/release-live-probe'
        result['harness_sha256'] = {}
        for name in ('Program.cs', 'RestoreProbe.cs', 'LiveCheck.csproj', 'RestoreProbe.csproj'):
            data = (originals / name).read_bytes()
            result['harness_sha256'][name] = hashlib.sha256(data).hexdigest()
            if name.endswith('.csproj'):
                import xml.etree.ElementTree as ET
                project = ET.fromstring(data)
                project.find('.//ProjectReference').set('Include', str(source / 'libmeowchan/libmeowchan.csproj'))
                ET.ElementTree(project).write(harness / name, encoding='utf-8', xml_declaration=True)
            else:
                (harness / name).write_bytes(data)
        build = out / 'build'
        for project in ('LiveCheck', 'RestoreProbe'):
            run('build-' + project, ['dotnet', 'build', str(harness / (project + '.csproj')), '-c', 'Release', '--artifacts-path', str(build)])
        selected = []
        for provider, board, url in [('4chan', 'g', 'https://a.4cdn.org/g/catalog.json'), ('8kun', 'tech', 'https://8kun.top/tech/catalog.json')]:
            catalog = out / (provider + '-catalog.json')
            run('discover-' + provider, ['curl', '--disable', '--fail', '--max-time', '30', '--max-filesize', '2097152', '--dump-header', str(out / (provider + '-catalog.headers')), '--output', str(catalog), url])
            selected.append(candidates(provider, board, json.loads(catalog.read_text())))
        result['selected'] = selected
        result['request_budget_per_provider'] = {'discovery_catalog': 1, 'board_metadata': 1, 'thread': 1, 'media': 1,
                                                 'maximum_media_response_bytes': 200_000, 'maximum_json_response_bytes': 2_097_152}
        save()
        state = out / 'current-run'
        def assembly(project):
            paths = list((build / 'bin' / project.lower()).glob('release*/' + project + '.dll'))
            if len(paths) != 1:
                # .NET artifacts layout preserves project name casing on some SDKs.
                paths = list((build / 'bin' / project).glob('release*/' + project + '.dll'))
            if len(paths) != 1:
                raise RuntimeError('Ambiguous probe assembly: ' + project)
            return str(paths[0])
        run('initial', ['dotnet', assembly('LiveCheck'), str(state), *[s['url'] for s in selected]])
        run('fresh-process-restore', ['dotnet', assembly('RestoreProbe'), str(state)])
        initial = json.loads((state / 'results.json').read_text())
        restored = json.loads((state / 'fresh-process-restore.json').read_text())
        if len(initial) != 2 or not all(r['Passed'] and r['Detail']['Files'] == 1 and r['Detail']['HTTPMediaRequests'] == 1
                and r['Detail']['HTTPThreadRequests'] == 1 and r['Detail']['HTTPBoardMetadataRequests'] <= 1 for r in initial):
            raise RuntimeError('Expected one verified original per provider.')
        if len(restored) != 2 or not all(r['Passed'] and r['Paused'] and r['Intent'] == 'Watching' and r['SavedMedia'] == 1
                and r['Requests'] == [] and r['MediaHTTPRequests'] == 0 for r in restored):
            raise RuntimeError('Fresh restore attempted HTTP.')
        if {r['Source'] for r in initial} != {'4chan', '8kun'} or {r['Source'] for r in restored} != {'4chan', '8kun'}:
            raise RuntimeError('Missing provider evidence.')
        initial_by_source = {r['Source']: r for r in initial}
        if not all(r['Files'] == 1 and r['CompletedMedia'] == 1 and 0 < r['Bytes'] <= 200_000
                   and r['Bytes'] == initial_by_source[r['Source']]['Detail']['Bytes'] for r in restored):
            raise RuntimeError('Fresh restore did not verify the same saved payload bytes.')
        if git('rev-parse', 'HEAD') != commit or git('status', '--porcelain'):
            raise RuntimeError('Frozen tracked source changed.')
        result.update(passed=True, initial=initial, fresh_process_restore=restored)
    finally:
        result['disk_free_after_bytes'] = shutil.disk_usage(out).free
        result['finished_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat(); save()
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
