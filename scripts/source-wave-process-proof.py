#!/usr/bin/env python3
"""Run frozen delivered-source Desktop fixtures in two real offline OS processes.

No build, restore, GUI or live-source transport. Uses an already-built test output.
"""
from __future__ import annotations
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

FIXTURE = 'MeowChan.DesktopTests.SourceWaveProcessRestartTests.DeliveredSha1AndSizeHintBindingsSurviveAnActualOfflineProcessRestart'
LOCK = Path('/tmp/meowchan-ui-1.0-build.lock')
_spec = importlib.util.spec_from_file_location('wave_process_helpers', Path(__file__).with_name('apphost-integrity-process-smoke.py'))
helpers = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(helpers)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def closure(directory):
    """Freeze the entire existing test output, including real worker/plugin payloads."""
    rows = []
    total = 0
    for path in sorted(directory.rglob('*')):
        require(len(rows) < 20000, 'Unbounded test output inventory')
        if path.is_symlink():
            require(path.resolve(strict=True).is_relative_to(directory), 'Test payload link escapes output')
            rows.append(dict(path=path.relative_to(directory).as_posix(), link=os.readlink(path)))
        elif path.is_file():
            size = path.stat().st_size
            total += size
            require(size <= 1024 * 1024 * 1024 and total <= 4 * 1024 * 1024 * 1024, 'Test payload exceeds bound')
            rows.append(dict(path=path.relative_to(directory).as_posix(), bytes=size, sha256=sha256(path)))
    return rows


def check_trx(path):
    tree = ET.parse(path)
    counters = tree.find('.//{*}Counters')
    require(counters is not None and counters.get('total') == '1' and counters.get('passed') == '1'
            and counters.get('failed') == '0', 'Expected exactly one passed opt-in test')
    results = tree.findall('.//{*}UnitTestResult')
    require(len(results) == 1 and results[0].get('outcome') == 'Passed'
            and results[0].get('testName') == FIXTURE, 'Unexpected or skipped process fixture')


def read_receipt(path, phase, root, token, source_commit, assembly_sha, parent_network):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 2 * 1024 * 1024, 'Missing/linked/oversized receipt')
    data = json.loads(path.read_text())
    require(data.get('schemaVersion') == 1 and data.get('completed') is True and data.get('phase') == phase
            and data.get('token') == token and data.get('profile') == str(root / 'profile')
            and data.get('declaredSourceCommit') == source_commit and data.get('testAssemblySha256') == assembly_sha
            and type(data.get('processId')) is int and data['processId'] > 0
            and data.get('profileLeaseReleased') is True and data.get('actualNetworkSends') == 0
            and data.get('unexpected') == 0, 'Receipt identity/completion/transport/lease differs')
    network = data.get('networkNamespace')
    require(isinstance(network, str) and network.startswith('net:[') and network != parent_network, 'Network namespace not isolated')
    attempts = data.get('fixtureRequests')
    require(isinstance(attempts, list) and len(attempts) == (8 if phase == 'acquire' else 0), 'Acquisition/zero offline attempt inventory differs')
    require(not Path('/proc', str(data['processId'])).exists(), 'Test OS process has not exited')
    require(isinstance(data.get('workerIdentities'), list), 'Missing observed worker identity inventory')
    if phase == 'acquire':
        require(len(data['workerIdentities']) >= 2, 'Real delivered source worker processes not observed')
    require([row.get('source') for row in data.get('plugins', [])] == ['2ch', 'uboachan'], 'Delivered-source receipt differs')
    proofs = data.get('proofs', [])
    require([row.get('source') for row in proofs] == ['2ch', 'uboachan'], 'Missing source proofs')
    extended = 0
    for proof in proofs:
        require(len(proof.get('attachments', [])) == 2, 'Multi-attachment proof missing')
        for row in proof['attachments']:
            binding = row['binding']
            attachment = row['attachment']
            provider = binding['provider']
            require(row['restoredAssetId'] == binding['assetId'] and row['bytesSha256'] == binding['actual']['sha256']
                    and binding['acceptance']['kind'] == 0, 'Restore/byte/strict acceptance proof differs')
            is_extended = proof['source'] == '2ch' or attachment['position'] == 1
            require(binding['acquisitionRevisionKey'].startswith('acq-v2:' if is_extended else 'acq-v1:')
                    and attachment['attachmentRevisionKey'].startswith('att-v2:' if is_extended else 'att-v1:'), 'Revision version differs')
            if proof['source'] == '2ch':
                hint = provider.get('providerSizeHint')
                require(provider.get('expectedSize') is None and hint == dict(rawValue='24.50' if attachment['position'] == 0 else '0', unitLabel='Кб', precision=0), 'Raw provider hint changed')
            elif attachment['position'] == 1:
                require(provider.get('expectedMd5') is None and provider.get('expectedSha1') == binding['actual'].get('sha1')
                        and isinstance(provider.get('expectedSha1'), str), 'Provider SHA1 changed')
            extended += is_extended
    require(extended == 3, 'Expected three extended v2 bindings')
    return data


def run_phase(assembly, root, evidence, token, phase, timeout, source_commit, parent_network):
    destination = evidence / phase
    destination.mkdir()
    results = root / ('results-' + phase)
    results.mkdir()
    home = root / ('home-' + phase)
    home.mkdir()
    temporary = root / ('tmp-' + phase)
    temporary.mkdir()
    environment = {key: value for key, value in os.environ.items() if key in ('PATH', 'LANG', 'LC_ALL', 'TZ')}
    environment.update(HOME=str(home), TMPDIR=str(temporary), XDG_CONFIG_HOME=str(home / 'config'),
        XDG_CACHE_HOME=str(home / 'cache'), XDG_DATA_HOME=str(home / 'data'), DOTNET_CLI_HOME=str(home),
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
        NUGET_PACKAGES=str(home / 'nuget'), MEOWCHAN_WAVE_PROCESS_PROOF='1', MEOWCHAN_WAVE_PROCESS_ROOT=str(root),
        MEOWCHAN_WAVE_PROCESS_TOKEN=token, MEOWCHAN_WAVE_PROCESS_PHASE=phase, MEOWCHAN_WAVE_PROCESS_SOURCE_COMMIT=source_commit)
    # Existing Core proof's bwrap path: read-only host filesystem, private writable root,
    # fresh /tmp and a distinct network namespace in BOTH synthetic phases.
    command = ['bwrap', '--die-with-parent', '--unshare-net', '--ro-bind', '/', '/', '--tmpfs', '/tmp',
        '--bind', str(root), str(root), '--proc', '/proc', '--dev', '/dev', '--chdir', str(root), '--',
        shutil.which('dotnet'), 'vstest', str(assembly), '/TestCaseFilter:FullyQualifiedName=' + FIXTURE,
        '/Logger:trx;LogFileName=fixture.trx', '/ResultsDirectory:' + str(results)]
    result = dict(phase=phase, passed=False, command=command)
    try:
        with (destination / 'output.log').open('wb') as log:
            deadline = time.monotonic() + timeout
            process = subprocess.Popen(command, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            supervisor = helpers.process_stat(process.pid)
            require(supervisor is not None and supervisor['processGroupId'] == process.pid and supervisor['sessionId'] == process.pid, 'Supervisor identity missing')
            identities = {process.pid: supervisor['startTimeTicks']}
            try:
                result['exitCode'] = process.wait(timeout=max(0, deadline - time.monotonic()))
            finally:
                normal = helpers.observe_normal_group_exit(supervisor, deadline, result, identities)
                if not normal:
                    result['normalGroupExitDeadlineExceeded'] = True
                    helpers.cleanup_bound_group(process, supervisor, identities, result)
        require(result['exitCode'] == 0 and not result.get('forcedCleanup') and not result.get('normalGroupExitDeadlineExceeded'), 'Process failed or needed forced cleanup')
        check_trx(results / 'fixture.trx')
        receipt = read_receipt(root / (phase + '.json'), phase, root, token, source_commit, sha256(assembly), parent_network)
        shutil.copy2(root / (phase + '.json'), destination / 'metrics.json')
        result.update(passed=True, processId=receipt['processId'], networkNamespace=receipt['networkNamespace'], metricsSha256=sha256(destination / 'metrics.json'))
        return result, receipt
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        raise
    finally:
        metrics = root / (phase + '.json')
        if metrics.is_file() and not metrics.is_symlink() and metrics.stat().st_size <= 2 * 1024 * 1024:
            shutil.copy2(metrics, destination / 'metrics.json')
        trx = results / 'fixture.trx'
        if trx.is_file(): shutil.copy2(trx, destination / 'fixture.trx')
        (destination / 'result.json').write_text(json.dumps(result, indent=2) + '\n')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('test_assembly', type=Path)
    parser.add_argument('--evidence-dir', required=True, type=Path)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--timeout', type=float, default=150)
    args = parser.parse_args(argv)
    if not sys.platform.startswith('linux') or not 0 < args.timeout <= 240 or not re.fullmatch('[0-9a-f]{40}', args.source_commit):
        parser.error('Linux, timeout in (0,240], and full source commit required')
    if not all(shutil.which(name) for name in ('bwrap', 'dotnet')): parser.error('Existing bwrap and dotnet required; nothing is installed/built')
    assembly = args.test_assembly.resolve(strict=True)
    if not assembly.is_file() or assembly.name != 'MeowChan.DesktopTests.dll' or assembly.is_relative_to('/tmp'):
        parser.error('Frozen MeowChan.DesktopTests.dll outside masked /tmp required')
    args.evidence_dir.mkdir(parents=True, exist_ok=False, mode=0o700)
    evidence = args.evidence_dir.resolve(strict=True)
    report = dict(schemaVersion=1, passed=False, declaredSourceCommit=args.source_commit, testAssembly=str(assembly),
        testAssemblySha256=sha256(assembly), driverSha256=sha256(Path(__file__)), phases=[], actualNetworkSends=0,
        scope='Two actual production DesktopHost OS processes, delivered Uboachan/Dvach workers, terminal synthetic metadata/PNG bytes, offline SHA1/hint/v2 persistence. No live source, package or GUI rendering claim.')
    try:
        with LOCK.open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            before = closure(assembly.parent)
            (evidence / 'closure.json').write_text(json.dumps(before, indent=2) + '\n')
            report['closureSha256'] = sha256(evidence / 'closure.json')
            with tempfile.TemporaryDirectory(prefix='mcwave-', dir='/tmp') as directory:
                root = Path(directory)
                root.chmod(0o700)
                token = os.urandom(32).hex()
                (root / '.wave-process-owner').write_text(token)
                parent_network = os.readlink('/proc/self/ns/net')
                receipts = []
                for phase in ('acquire', 'offline'):
                    result, receipt = run_phase(assembly, root, evidence, token, phase, args.timeout, args.source_commit, parent_network)
                    report['phases'].append(result)
                    receipts.append(receipt)
                first, second = receipts
                require(first['processId'] != second['processId'], 'Same test OS process reused')
                for key in ('testAssemblySha256', 'applicationAssemblySha256', 'coreAssemblySha256', 'plugins', 'proofs'):
                    require(first[key] == second[key], 'Exact cross-process evidence differs: ' + key)
                require(before == closure(assembly.parent), 'Frozen test/worker/plugin output changed')
            require(not root.exists(), 'Private profile cleanup did not complete')
            report.update(passed=True, exactV2EvidencePreserved=True, distinctActualProcessIds=True, privateProfileRemovedAfterProof=True)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError) as error:
        report['passed'] = False
        report['error'] = str(error)
    (evidence / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(passed=report['passed'], evidence=str(evidence), error=report.get('error'))))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
