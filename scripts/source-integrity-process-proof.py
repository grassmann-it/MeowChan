#!/usr/bin/env python3
"""Run one Core acquisition fixture, then a distinct offline OS process; no build/live/GUI."""
from __future__ import annotations
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

FIXTURE = 'MeowChan.Tests.SourceIntegrityProcessProofTests.ExactAcceptedRevisionSurvivesARealOfflineProcessRestart'
LOCK = Path('/tmp/meowchan-ui-1.0-build.lock')
MAX_RECEIPT = 1024 * 1024


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_receipt(path, phase, root, token, assembly_sha, parent_network):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAX_RECEIPT,
            'Missing, linked or oversized fixture receipt')
    data = json.loads(path.read_text())
    require(data.get('schemaVersion') == 1 and data.get('completed') is True
            and data.get('phase') == phase and data.get('token') == token
            and data.get('profile') == str(root / 'profile')
            and data.get('testAssemblySha256') == assembly_sha
            and isinstance(data.get('coreAssemblySha256'), str) and len(data['coreAssemblySha256']) == 64
            and type(data.get('processId')) is int and data['processId'] > 0
            and data.get('workerProcessIds') == []
            and data.get('actualNetworkSends') == 0 and data.get('httpAttempts') == 0
            and data.get('profileLeaseReleased') is True and data.get('freshPolicyAllowsMismatch') is False,
            'Fixture receipt identity/completion/transport/lease differs')
    network = data.get('networkNamespace')
    require(isinstance(network, str) and network.startswith('net:[') and network != parent_network,
            'Fixture was not in a distinct network namespace')
    require(not Path('/proc', str(data['processId'])).exists(),
            'Actual test process has not exited; offline restart cannot begin')
    proof = data.get('proof')
    require(isinstance(proof, dict) and proof.get('newUnboundRevisionRejected') is True
            and proof.get('retainedRevisionSelected') is True
            and proof.get('galleryAssetId') == proof.get('binding', {}).get('assetId')
            and proof.get('exportSha256') == proof.get('restoredSha256')
            and proof.get('exportSha256') == proof.get('binding', {}).get('actual', {}).get('sha256'),
            'Exact resolver/Gallery/export byte proof differs')
    return data


def check_trx(path):
    tree = ET.parse(path)
    counters = tree.find('.//{*}Counters')
    require(counters is not None and counters.get('total') == '1' and counters.get('passed') == '1'
            and counters.get('failed') == '0', 'Expected exactly one passed fixture test')
    results = tree.findall('.//{*}UnitTestResult')
    require(len(results) == 1 and results[0].get('outcome') == 'Passed'
            and results[0].get('testName') == FIXTURE, 'Unexpected fixture result or skipped test')


def owned_group_exists(pid):
    try:
        os.killpg(pid, 0)
        return True
    except ProcessLookupError:
        return False


def run_phase(assembly, dotnet, root, evidence, token, phase, timeout, assembly_sha, parent_network):
    phase_dir = evidence / phase
    phase_dir.mkdir()
    result_dir = root / ('results-' + phase)
    result_dir.mkdir()
    environment = {key: value for key, value in os.environ.items()
                   if key in ('PATH', 'LANG', 'LC_ALL', 'TZ')}
    environment.update(HOME=str(root / 'home'), TMPDIR=str(root / 'tmp'),
        XDG_CONFIG_HOME=str(root / 'home/config'), XDG_CACHE_HOME=str(root / 'home/cache'),
        XDG_DATA_HOME=str(root / 'home/data'), DOTNET_CLI_HOME=str(root / 'home'),
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
        DOTNET_NOLOGO='1', NUGET_PACKAGES=str(root / 'home/nuget'),
        MEOWCHAN_INTEGRITY_PROCESS_PROOF='1', MEOWCHAN_INTEGRITY_PROCESS_ROOT=str(root),
        MEOWCHAN_INTEGRITY_PROCESS_TOKEN=token, MEOWCHAN_INTEGRITY_PROCESS_PHASE=phase)
    # Both phases are deterministic fixtures. Their terminal handler has no network leg.
    # Phase B additionally asserts zero handler attempts. There is no live mode here.
    command = ['bwrap', '--die-with-parent', '--unshare-net', '--ro-bind', '/', '/',
        '--tmpfs', '/tmp', '--bind', str(root), str(root), '--proc', '/proc', '--dev', '/dev', '--chdir', str(root),
        '--', dotnet, 'vstest', str(assembly), '/TestCaseFilter:FullyQualifiedName=' + FIXTURE,
        '/Logger:trx;LogFileName=fixture.trx', '/ResultsDirectory:' + str(result_dir)]
    result = {'phase': phase, 'passed': False, 'command': command}
    try:
        with (phase_dir / 'output.log').open('wb') as log:
            process = subprocess.Popen(command, env=environment, cwd=root, stdout=log,
                stderr=subprocess.STDOUT, start_new_session=True)
            try:
                result['exitCode'] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                result['timedOut'] = True
                raise
            finally:
                if owned_group_exists(process.pid):
                    # A leftover process is failure evidence; never clean it and claim normal exit.
                    result['forcedCleanup'] = True
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
        trx = result_dir / 'fixture.trx'
        if trx.is_file():
            shutil.copy2(trx, phase_dir / 'fixture.trx')
        require(result['exitCode'] == 0 and not result.get('forcedCleanup'),
                'Fixture failed or required forced process cleanup')
        check_trx(trx)
        receipt = root / (phase + '.json')
        data = read_receipt(receipt, phase, root, token, assembly_sha, parent_network)
        shutil.copy2(receipt, phase_dir / 'metrics.json')
        result.update(passed=True, processId=data['processId'], networkNamespace=data['networkNamespace'],
                      metricsSha256=sha256(receipt))
        return result, data
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        raise
    finally:
        trx = result_dir / 'fixture.trx'
        if trx.is_file():
            shutil.copy2(trx, phase_dir / 'fixture.trx')
        receipt = root / (phase + '.json')
        if receipt.is_file() and not receipt.is_symlink() and receipt.stat().st_size <= MAX_RECEIPT:
            shutil.copy2(receipt, phase_dir / 'metrics.json')
        (phase_dir / 'result.json').write_text(json.dumps(result, indent=2) + '\n')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('test_assembly', type=Path)
    parser.add_argument('--evidence-dir', required=True, type=Path)
    parser.add_argument('--timeout', type=float, default=120)
    args = parser.parse_args(argv)
    if not sys.platform.startswith('linux') or not 0 < args.timeout <= 180:
        parser.error('Linux and timeout in (0,180] required')
    if not all(shutil.which(name) for name in ('bwrap', 'dotnet')):
        parser.error('bwrap and dotnet must already be available; this driver installs/builds nothing')
    assembly = args.test_assembly.resolve(strict=True)
    if not assembly.is_file() or assembly.name != 'MeowChan.Tests.dll':
        parser.error('An already-built frozen MeowChan.Tests.dll is required')
    args.evidence_dir.mkdir(parents=True, exist_ok=False)
    evidence = args.evidence_dir.resolve(strict=True)
    report = {'schemaVersion': 1, 'passed': False, 'testAssembly': str(assembly),
        'testAssemblySha256': sha256(assembly), 'phases': [], 'liveRequests': 0,
        'scope': 'Two actual Core test OS processes; synthetic acquisition then offline namespace; no build, GUI or live proof.'}
    try:
        with LOCK.open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with tempfile.TemporaryDirectory(prefix='mcip-', dir='/tmp') as temporary:
                root = Path(temporary)
                for name in ('home', 'tmp'):
                    (root / name).mkdir()
                token = os.urandom(32).hex()
                (root / '.source-integrity-proof-owner').write_text(token)
                parent_network = os.readlink('/proc/self/ns/net')
                metrics = []
                for phase in ('acquire', 'offline'):
                    result, data = run_phase(assembly, shutil.which('dotnet'), root, evidence,
                        token, phase, args.timeout, report['testAssemblySha256'], parent_network)
                    report['phases'].append(result)
                    metrics.append(data)
                before, after = metrics
                require(before['processId'] != after['processId'], 'Offline phase reused the acquisition process')
                require(before['coreAssemblySha256'] == after['coreAssemblySha256'], 'Core assembly changed across phases')
                require(before['proof'] == after['proof'], 'Exact binding/asset/provider/actual bytes changed across restart')
                require(report['testAssemblySha256'] == sha256(assembly), 'Test assembly changed during proof')
                report.update(passed=True, exactProofPreserved=True, distinctActualProcessIds=True,
                              privateProfileRemovedAfterProof=True)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError) as error:
        report['error'] = str(error)
        report['phases'] = [json.loads(path.read_text()) for path in
                            (evidence / 'acquire/result.json', evidence / 'offline/result.json') if path.is_file()]
    (evidence / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': report['passed'], 'evidence': str(evidence), 'error': report.get('error')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
