#!/usr/bin/env python3
"""Drive the source-stability run of docs/plugins/source-stability.md section 3.

The default is a DRY RUN: it verifies the delivered plugin payload, starts a test process that has no network
client at all and prints the planned request list. Nothing is sent. Sending needs --live.

A live run is: verify the payload, print the plan, run the online phase in one OS process (the only process that
gets a network client), then run the offline phase in a NEW OS process over the same throwaway profile with the
network blocked (bubblewrap --unshare-net), and write one evidence file per chan next to the plugin sources.

  dry run:  run-stability-live.py --manifest run.json
  live:     run-stability-live.py --manifest run.json --live
  self test (stub site, no network, two real processes): run-stability-live.py --selftest
  replay:   run-stability-live.py --replay artifacts/stability/<run> --only 2ch [--board mu]

A replay feeds the metadata bodies that an earlier run saved (evidence/bodies/<sha256>.body, referenced by the rows of
evidence/audit-<chan>.json) through the production host and parser, in a process without network, to reproduce a failure.
Every run also keeps the host and worker logs (evidence/logs) and, per failure, the host's view of the thread.

The manifest names every approved chan, its boards and optional redirects; see docs/plugins/source-stability-harness.md.
Profiles and downloaded media live in <run>/work and are deleted at the end, also after a failure.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
CONTRACT = 'docs/plugins/source-stability.md'
TEST_NAME = 'MeowChan.DesktopTests.StabilityRunLiveTests.RunTheRequestedPhase'
PREFIX = 'MEOWCHAN_STABILITY_RUN_'
EXIT_FAIL, EXIT_USAGE, EXIT_INFRA = 1, 2, 3

SELFTEST_MANIFEST = {
    'schemaVersion': 1,
    'sources': [
        {'pluginId': 'meowchan.source.vichan', 'sourceId': 'lainchan', 'approval': '2026-11-11, chat',
         'boards': [{'board': 'demo'}, {'board': 'demo2'}, {'board': 'demo3'}]},
        {'pluginId': 'meowchan.source.kohlchan', 'sourceId': 'kohlchan', 'approval': '2026-11-11, chat',
         'boards': [{'board': 'demo'}, {'board': 'demo2'}, {'board': 'demo3'}]},
    ],
}


class DriverError(Exception):
    def __init__(self, message, code=EXIT_INFRA):
        super().__init__(message)
        self.code = code


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def runtime_identifier() -> str:
    machine = platform.machine().lower()
    if sys.platform.startswith('linux'):
        return 'linux-arm64' if machine in ('aarch64', 'arm64') else 'linux-x64'
    raise DriverError('The stability run is supported on Linux only', EXIT_USAGE)


def contract_reference(repository: Path, override: str | None) -> str:
    """docs/plugins/source-stability.md@<commit of the last change to that file>; refuses an uncommitted contract."""
    if override:
        commit = override
    else:
        status = subprocess.run(['git', '-C', str(repository), 'status', '--porcelain', '--', CONTRACT],
                                capture_output=True, text=True, check=False)
        if status.returncode != 0 or status.stdout.strip():
            raise DriverError('The contract file is missing from git or has uncommitted changes; commit it first '
                              '(or pass --contract-commit)', EXIT_USAGE)
        log = subprocess.run(['git', '-C', str(repository), 'log', '-1', '--format=%H', '--', CONTRACT],
                             capture_output=True, text=True, check=False)
        commit = log.stdout.strip()
    if not commit or any(c not in '0123456789abcdef' for c in commit) or not 7 <= len(commit) <= 40:
        raise DriverError('No contract commit found: ' + repr(commit), EXIT_USAGE)
    return f'{CONTRACT}@{commit}'


def load_manifest(path: Path) -> dict:
    """Light pre-check only. The test process re-validates every field and fails closed on anything unknown."""
    try:
        data = json.loads(path.read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise DriverError(f'Cannot read manifest {path}: {error}', EXIT_USAGE)
    sources = data.get('sources') if isinstance(data, dict) else None
    if not isinstance(sources, list) or not sources:
        raise DriverError('Manifest has no sources', EXIT_USAGE)
    return data


def source_ids(manifest: dict) -> list[str]:
    return [row.get('sourceId') for row in manifest['sources']]


def select_only(manifest: dict, only: str | None) -> list[str]:
    ids = source_ids(manifest)
    if only is None:
        return ids
    wanted = [part.strip() for part in only.split(',') if part.strip()]
    unknown = [part for part in wanted if part not in ids]
    if not wanted or unknown or len(set(wanted)) != len(wanted):
        raise DriverError('--only must name unique chans of the manifest; unknown: ' + ', '.join(unknown), EXIT_USAGE)
    return wanted


def phase_environment(args, mode: str, manifest: Path, work: Path, evidence: Path, contract: str, report: Path | None) -> dict:
    """Only the online phase of --live (or --selftest) gets a network mode; plan and offline get none."""
    env = {key: value for key, value in os.environ.items() if not key.startswith(PREFIX) and key != 'WAYLAND_DISPLAY'}
    env.update(DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    env[PREFIX + 'MANIFEST'] = str(manifest)
    env[PREFIX + 'MODE'] = mode
    env[PREFIX + 'WORK'] = str(work)
    env[PREFIX + 'EVIDENCE'] = str(evidence)
    env[PREFIX + 'REPO'] = str(args.evidence_repo)
    env[PREFIX + 'CONTRACT'] = contract
    env[PREFIX + 'CONFIGURATION'] = args.configuration
    if args.only:
        env[PREFIX + 'ONLY'] = args.only
    if report is not None:
        env[PREFIX + 'PACKAGE_REPORT'] = str(report)
    if getattr(args, 'capture_seconds', None):
        env[PREFIX + 'CAPTURE_SECONDS'] = str(args.capture_seconds)
    if mode == 'replay':
        env[PREFIX + 'REPLAY_DIR'] = str(args.replay_evidence)
        if args.board:
            env[PREFIX + 'REPLAY_BOARD'] = args.board
    if mode == 'online':
        if args.selftest:
            env[PREFIX + 'NETWORK'] = 'stub'
        elif args.live:
            env[PREFIX + 'NETWORK'] = 'live'
            env[PREFIX + 'LIVE'] = '1'
        else:
            raise DriverError('Refusing to build an online phase without --live or --selftest', EXIT_USAGE)
    return env


def phase_command(args, mode: str, trx_dir: Path, block_network: bool) -> list[str]:
    inner = []
    if args.xvfb:
        inner += [args.xvfb, '-a', '-s', '-screen 0 1920x1080x24']
    inner += ['nice', '-n', '19', args.dotnet, 'vstest', str(args.test_assembly),
              '/TestCaseFilter:FullyQualifiedName=' + TEST_NAME,
              f'/Logger:trx;LogFileName={mode}.trx', '/ResultsDirectory:' + str(trx_dir)]
    if block_network:
        return [args.bwrap, '--die-with-parent', '--unshare-net', '--bind', '/', '/', '--proc', '/proc', '--dev-bind', '/dev', '/dev', '--'] + inner
    return inner


def check_trx(path: Path) -> None:
    try:
        counters = ET.parse(path).find('.//{*}Counters')
    except (OSError, ET.ParseError) as error:
        raise DriverError(f'No test result {path}: {error}')
    if counters is None or counters.get('total') != '1' or counters.get('passed') != '1':
        raise DriverError(f'The phase test did not pass (an opt-in test that is skipped or failed): {path}')


def run_phase(args, mode: str, manifest: Path, work: Path, evidence: Path, contract: str, report: Path | None, log: Path) -> None:
    blocked_mode = mode in ('offline', 'replay')
    block = blocked_mode and args.net_block != 'off' and args.bwrap is not None
    if blocked_mode and args.net_block == 'require' and args.bwrap is None:
        raise DriverError(f'The {mode} phase must run with the network blocked, but bwrap is not available', EXIT_USAGE)
    trx_dir = evidence / 'trx'
    trx_dir.mkdir(parents=True, exist_ok=True)
    command = phase_command(args, mode, trx_dir, block)
    env = phase_environment(args, mode, manifest, work, evidence, contract, report)
    print(f'[{mode}] ' + ' '.join(command), flush=True)
    with log.open('ab') as out:
        result = subprocess.run(command, env=env, stdout=out, stderr=subprocess.STDOUT, timeout=args.timeout, check=False)
    # A passing xUnit result means the phase itself completed. Chan outcomes live in the evidence files.
    if result.returncode != 0:
        raise DriverError(f'The {mode} phase failed with exit code {result.returncode}; see {log}')
    check_trx(trx_dir / f'{mode}.trx')


def verify_package(args, evidence: Path) -> Path:
    report = evidence / 'package-report.json'
    command = [sys.executable, '-I', str(args.package_script), '--verify', str(args.test_assembly.parent),
               '--runtime', runtime_identifier(), '--development', '--report', str(report)]
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise DriverError('The delivered plugin payload does not verify: ' + (result.stderr or result.stdout).strip())
    print('[package] payload verified: ' + str(args.test_assembly.parent), flush=True)
    return report


EVIDENCE_KEYS = {'kind': str, 'contract': str, 'pluginId': str, 'sourceId': str, 'executionRevision': str,
                 'assemblySha256': str, 'descriptionSha256': str, 'package': str, 'approvedByUser': str,
                 'windowUtc': list, 'boards': list, 'integrityExceptionUsed': bool, 'failuresObserved': list,
                 'receipts': list, 'outcome': str}


def validate_evidence(document) -> list[str]:
    """Shape check against section 4 of the contract; returns the problems found."""
    problems = []
    if not isinstance(document, dict):
        return ['root is not an object']
    for key, kind in EVIDENCE_KEYS.items():
        if key not in document:
            problems.append(key + ' missing')
        elif document[key] is None or not isinstance(document[key], kind):
            problems.append(f'{key} has the wrong type')
    if document.get('kind') != 'source-stability-run':
        problems.append('kind differs')
    contract = document.get('contract')
    if isinstance(contract, str):
        head, _, commit = contract.partition('@')
        if head != CONTRACT or not 7 <= len(commit) <= 40 or any(c not in '0123456789abcdef' for c in commit):
            problems.append('contract is not path@commit')
    if document.get('outcome') not in ('pass', 'fail'):
        problems.append('outcome is neither pass nor fail')
    window = document.get('windowUtc')
    if isinstance(window, list) and (len(window) != 2 or not all(isinstance(v, str) and v.endswith('Z') for v in window)):
        problems.append('windowUtc is not two UTC timestamps')
    for board in document.get('boards') or []:
        for key in ('board', 'requests', 'original', 'archive', 'reader', 'offlineAfterRestart'):
            if key not in board:
                problems.append(f'board.{key} missing')
        for request in board.get('requests') or []:
            if not {'url', 'status', 'bytes'} <= set(request):
                problems.append('request lacks url, status or bytes')
        original = board.get('original')
        if original is not None and not {'expectedBytes', 'receivedBytes', 'providerHash', 'hashMatched', 'sha256'} <= set(original):
            problems.append('original lacks a contract field')
    for receipt in document.get('receipts') or []:
        if not {'path', 'sha256'} <= set(receipt):
            problems.append('receipt lacks path or sha256')
    return problems


def summarize(evidence: Path) -> tuple[list[dict], int]:
    phase2 = json.loads((evidence / 'phase2.json').read_text(encoding='utf-8'))
    rows, exit_code = [], 0
    for chan in phase2['chans']:
        path = Path(chan['evidenceFile'])
        document = json.loads(path.read_text(encoding='utf-8'))
        problems = validate_evidence(document) + list(chan.get('problems') or [])
        sends = sum(1 for board in document['boards'] for request in board['requests'])
        rows.append({'sourceId': chan['sourceId'], 'outcome': document['outcome'], 'file': str(path), 'requests': sends,
                     'failures': [f"{row.get('board')}/{row.get('step')}: {row.get('mode')}" for row in document['failuresObserved']],
                     'problems': problems, 'profileDeleted': chan['profileDeleted']})
        if document['outcome'] != 'pass' or problems or not chan['profileDeleted']:
            exit_code = EXIT_FAIL
    return rows, exit_code


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--manifest', type=Path, help='Approved chans, boards and redirects (JSON)')
    parser.add_argument('--replay', type=Path, metavar='RUN_DIR', help='Reproduce one chan of an earlier run from its saved metadata bodies; no network')
    parser.add_argument('--board', help='With --replay: replay only this board')
    parser.add_argument('--capture-seconds', type=int, help='How long to wait for a fetched thread to reach the archive (10 to 900, default 120)')
    parser.add_argument('--live', action='store_true', help='Send the planned requests. Without it the run is a dry run.')
    parser.add_argument('--selftest', action='store_true', help='Run both phases against the in-memory stub site; no network')
    parser.add_argument('--only', help='Comma-separated source IDs; can only narrow the manifest')
    parser.add_argument('--configuration', default='Debug')
    parser.add_argument('--artifacts-dir', type=Path, default=REPOSITORY / 'artifacts' / 'stability',
                        help='Parent of the per-run directory holding raw audits (outside Git) and the throwaway work directory')
    parser.add_argument('--evidence-repo', type=Path, default=REPOSITORY, help='Repository whose plugins/*/Evidence directories receive the run files')
    parser.add_argument('--test-assembly', type=Path)
    parser.add_argument('--package-script', type=Path, default=REPOSITORY / 'scripts' / 'package-plugins.py')
    parser.add_argument('--contract-commit', help='Override the commit of the contract file (tests)')
    parser.add_argument('--dotnet', default=shutil.which('dotnet'))
    parser.add_argument('--no-xvfb', action='store_true', help='Do not wrap the test process in xvfb-run')
    parser.add_argument('--net-block', choices=('auto', 'require', 'off'), default=None,
                        help='Block the network of the offline phase with bwrap --unshare-net (default: require for --live, else auto)')
    parser.add_argument('--timeout', type=float, default=1800, help='Seconds per phase')
    args = parser.parse_args(argv)
    if args.live and args.selftest:
        parser.error('--live and --selftest are exclusive')
    if args.replay and (args.live or args.selftest):
        parser.error('--replay sends nothing and excludes --live and --selftest')
    if args.board and not args.replay:
        parser.error('--board needs --replay')
    if args.capture_seconds is not None and not 10 <= args.capture_seconds <= 900:
        parser.error('--capture-seconds must be from 10 to 900')
    if not args.selftest and not args.replay and args.manifest is None:
        parser.error('--manifest is required')
    if args.test_assembly is None:
        args.test_assembly = REPOSITORY / 'tests' / 'MeowChan.DesktopTests' / 'bin' / args.configuration / 'net10.0' / 'MeowChan.DesktopTests.dll'
    args.test_assembly = args.test_assembly.resolve()
    args.artifacts_dir = args.artifacts_dir.resolve()
    args.evidence_repo = args.evidence_repo.resolve()
    args.package_script = args.package_script.resolve()
    args.xvfb = None if args.no_xvfb else shutil.which('xvfb-run')
    args.bwrap = shutil.which('bwrap')
    if args.net_block is None:
        args.net_block = 'require' if args.live else 'auto'
    if not args.dotnet:
        parser.error('dotnet was not found')
    if not args.test_assembly.is_file():
        parser.error(f'{args.test_assembly} does not exist; build tests/MeowChan.DesktopTests first (nice -n 10 dotnet build ... -c {args.configuration})')
    return args


def replay(args) -> int:
    """Runs the saved metadata of one chan through the production host in a process without network and prints what it saw."""
    run_dir = args.replay.resolve()
    source = run_dir / 'evidence' if (run_dir / 'evidence').is_dir() else run_dir
    manifest_path = source / 'manifest.json'
    if not manifest_path.is_file():
        raise DriverError(f'{source} holds no manifest.json; it is not the evidence directory of a run', EXIT_USAGE)
    chosen = select_only(load_manifest(manifest_path), args.only)
    if len(chosen) != 1 or args.only is None:
        raise DriverError('--replay needs --only with exactly one chan', EXIT_USAGE)
    if not (source / f'audit-{chosen[0]}.json').is_file():
        raise DriverError(f'{source} has no audit-{chosen[0]}.json', EXIT_USAGE)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out = run_dir / f'replay-{stamp}'
    evidence, work = out / 'evidence', out / 'work'
    args.replay_evidence = source
    try:
        evidence.mkdir(parents=True, exist_ok=False)
        work.mkdir()
        report = verify_package(args, evidence)
        run_phase(args, 'replay', manifest_path, work, evidence, '-', report, evidence / 'driver-phases.log')
        document = json.loads((evidence / 'replay.json').read_text(encoding='utf-8'))
        print(f"replay of {document['sourceId']} from {source}: {document['savedResponses']} saved responses, "
              f"{len(document['requests'])} replayed requests, no network")
        for row in document['missing']:
            print('  not saved: ' + row)
        for board in document['boards']:
            print(f"  board {board['board']}: thread {board['threadId']} archive={board['archive']} posts={board['postCount']} "
                  f"waited {board['captureWaitedMs']} ms of {board['captureLimitMs']} ms")
            for thread in (board.get('hostView') or {}).get('threads', []):
                print(f"    host: activity={thread['activity']} source={thread['sourceState']} lastCheckFailure={thread['lastCheckFailure']}")
            for worker in (board.get('hostView') or {}).get('workers', []):
                print(f"    worker: {worker['state']} {worker['failureCode']} {worker['failureMessage']}")
        for failure in document['failures']:
            print(f"  failure: {failure['board']}/{failure['step']}: {failure['mode']} {failure.get('detail') or ''}")
        print('  fatal: ' + str(document['fatal']))
        print('Replay output (outside Git):', out)
        return EXIT_FAIL if document['failures'] or document['fatal'] else 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv=None) -> int:
    args = parse_args(argv)
    if args.replay:
        try:
            return replay(args)
        except DriverError as error:
            print('ERROR: ' + str(error), file=sys.stderr)
            return error.code
        except subprocess.TimeoutExpired as error:
            print('ERROR: a phase exceeded its timeout: ' + str(error), file=sys.stderr)
            return EXIT_INFRA
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    run_dir = args.artifacts_dir / ('stability-' + stamp + ('-selftest' if args.selftest else ''))
    evidence, work = run_dir / 'evidence', run_dir / 'work'
    selftest_repo = None
    try:
        if args.selftest:
            args.artifacts_dir.mkdir(parents=True, exist_ok=True)
            selftest_repo = Path(tempfile.mkdtemp(prefix='stability-selftest-', dir=args.artifacts_dir))
            for name in ('VichanFamily/Sites/Lainchan', 'Kohlchan'):
                (selftest_repo / 'plugins' / 'Examples' / name).mkdir(parents=True)
            args.evidence_repo = selftest_repo
            manifest_path = run_dir / 'selftest-manifest.json'
            run_dir.mkdir(parents=True, exist_ok=False)
            manifest_path.write_text(json.dumps(SELFTEST_MANIFEST, indent=2) + '\n', encoding='utf-8')
            contract = f'{CONTRACT}@{"0" * 40}'
        else:
            args.evidence_repo = args.evidence_repo.resolve()
            manifest_path = args.manifest.resolve()
            contract = contract_reference(REPOSITORY, args.contract_commit)
            run_dir.mkdir(parents=True, exist_ok=False)
        manifest = load_manifest(manifest_path)
        chosen = select_only(manifest, args.only)
        evidence.mkdir(parents=True, exist_ok=False)
        work.mkdir(parents=True, exist_ok=False)
        shutil.copy2(manifest_path, evidence / 'manifest.json')
        log = evidence / 'driver-phases.log'
        report = verify_package(args, evidence)
        mode_label = 'LIVE' if args.live else 'SELFTEST (stub site, no network)' if args.selftest else 'DRY RUN (nothing is sent)'
        print(f'{mode_label}: {len(chosen)} chan(s): ' + ', '.join(chosen), flush=True)

        # The plan always runs first, in a process that has no network client. It is the dry run's whole output.
        plan_evidence = evidence / 'plan'
        plan_evidence.mkdir()
        run_phase(args, 'plan', manifest_path, work / 'plan', plan_evidence, contract, report, log)
        print((plan_evidence / 'plan.txt').read_text(encoding='utf-8'), flush=True)
        if not (args.live or args.selftest):
            print('Dry run complete. To send these requests, add --live (explicit approval per chan is in the manifest).')
            return 0

        # Online: one process. Offline: a different process, network blocked, same profile.
        run_phase(args, 'online', manifest_path, work, evidence, contract, report, log)
        run_phase(args, 'offline', manifest_path, work, evidence, contract, report, log)
        rows, code = summarize(evidence)
        for row in rows:
            print(f"{row['sourceId']}: {row['outcome']}  {row['requests']} requests  {row['file']}"
                  + ('  failures: ' + '; '.join(row['failures']) if row['failures'] else '')
                  + ('  PROBLEMS: ' + '; '.join(row['problems']) if row['problems'] else ''))
        print('Raw HTTP audits and saved metadata bodies (outside Git):', evidence)
        if args.selftest:
            # The self test also proves the replay path: the saved bodies of the first chan go through the host again without network.
            again = argparse.Namespace(**vars(args))
            again.replay, again.only, again.board = run_dir, source_ids(manifest)[0], 'demo'
            code = max(code, replay(again))
        return code
    except DriverError as error:
        print('ERROR: ' + str(error), file=sys.stderr)
        return error.code
    except subprocess.TimeoutExpired as error:
        print('ERROR: a phase exceeded its timeout: ' + str(error), file=sys.stderr)
        return EXIT_INFRA
    finally:
        # Profiles and downloaded media never outlive the run, also after a failure.
        shutil.rmtree(work, ignore_errors=True)
        if selftest_repo is not None:
            if (selftest_repo / 'plugins').is_dir() and run_dir.is_dir():
                shutil.copytree(selftest_repo / 'plugins', run_dir / 'selftest-evidence', dirs_exist_ok=True)
            shutil.rmtree(selftest_repo, ignore_errors=True)


if __name__ == '__main__':
    raise SystemExit(main())
