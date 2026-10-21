#!/usr/bin/env python3
"""Pack an exact commit in a detached checkout and consume it with fresh NuGet caches."""
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--output', required=True, type=Path, help='New evidence directory; never reused')
    args = parser.parse_args()
    repo = Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip())
    commit = subprocess.check_output(['git', 'rev-parse', '--verify', '--end-of-options', args.commit + '^{commit}'], cwd=repo, text=True).strip()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    environment = dict(os.environ)
    for key, folder in [('TMPDIR', 'tmp'), ('NUGET_PACKAGES', 'packages'), ('NUGET_HTTP_CACHE_PATH', 'http-cache')]:
        path = out / folder
        path.mkdir()
        environment[key] = str(path)
    source, build, feed = (out / name for name in ('source', 'build', 'feed'))
    result = {'source_commit': commit, 'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'commands': [], 'minimum_reserve_bytes': RESERVE}
    def save():
        (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    def run(name, argv, cwd):
        if shutil.disk_usage(out).free < RESERVE + 512 * 1024**2:
            raise RuntimeError('Insufficient storage above 2 GiB reserve plus 512 MiB working allowance.')
        print('START ' + name, flush=True)
        with (out / (name + '.log')).open('w') as log:
            log.write('CWD ' + str(cwd) + '\nCOMMAND ' + json.dumps(argv) + '\n'); log.flush()
            step = subprocess.Popen(argv, cwd=cwd, env=environment,
                                    stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            deadline = time.monotonic() + 600
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
        result['commands'].append({'name': name, 'argv': argv, 'cwd': str(cwd), 'exit_code': step.returncode})
        save()
        if step.returncode:
            raise subprocess.CalledProcessError(step.returncode, argv)
    result['passed'] = False
    try:
        run('checkout', ['git', 'worktree', 'add', '--detach', str(source), commit], repo)
        run('locked-release-restore', ['dotnet', 'restore', 'libmeowchan/libmeowchan.csproj', '--locked-mode', '-p:Configuration=Release', '--artifacts-path', str(build)], source)
        run('pack', ['dotnet', 'pack', 'libmeowchan/libmeowchan.csproj', '-c', 'Release', '--no-restore', '--artifacts-path', str(build), '--output', str(feed), '-p:RepositoryCommit=' + commit], source)
        run('fresh-cache-consumer', ['python3', 'scripts/test-local-package.py', '--feed', str(feed), '--consumer', 'samples/LibMeowChan.Consumer/LibMeowChan.Consumer.csproj'], source)
        result['actual_checkout_head'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip()
        status = subprocess.check_output(['git', 'status', '--porcelain'], cwd=source, text=True)
        (out / 'source-status.txt').write_text(status)
        (out / 'sdk-info.txt').write_text(subprocess.check_output(['dotnet', '--info'], cwd=source, env=environment, text=True))
        if status or result['actual_checkout_head'] != commit:
            raise RuntimeError('Frozen tracked checkout changed.')
        result['consumer_evidence'] = json.loads((feed / 'consumer-evidence.json').read_text())
        shutil.copy2(source / 'samples/LibMeowChan.Consumer/obj/project.assets.json', out / 'consumer-project.assets.json')
        result['packages'] = [{'name': p.name, 'bytes': p.stat().st_size, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in feed.glob('*.nupkg')]
        result['passed'] = True
    finally:
        result['disk_free_after_bytes'] = shutil.disk_usage(out).free
        result['finished_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
