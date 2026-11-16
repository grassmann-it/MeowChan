#!/usr/bin/env python3
"""Stage strict AP04 inputs from same-run downloaded artifacts; check complete replay evidence.

The workflow must download inventory and Linux artifacts from the requested Actions run.
This checks their recorded identity and bytes, not GitHub signatures or package execution.
No restore, build, download, repair, or diagnostic-only acceptance is performed here.
"""
import argparse
import json
from pathlib import Path
import shutil

import ap04_candidate as candidate
import ap04_run as runner


def read(path):
    return json.loads(candidate.ordinary_path(path, 'receipt').read_text(encoding='utf-8'))


def write_new(path, data):
    path = Path(path)
    candidate.require(path.parent.absolute() == path.parent.resolve(), 'symlinked output parent')
    with path.open('x', encoding='utf-8') as stream:
        stream.write(json.dumps(data, indent=2, sort_keys=True) + '\n')


def binding(inventory, sums, sha, run_id, package):
    candidate.require(candidate.SHA1.fullmatch(sha), 'full candidate SHA required')
    candidate.require(str(run_id).isdigit() and int(run_id) > 0, 'positive Actions run ID required')
    info = read(inventory)
    candidate.require(info.get('schema') in ('meowchan.stable-candidate.v1', 'meowchan.prerelease-candidate.v1'),
                      'unsupported inventory schema')
    candidate.require(info.get('source_commit') == sha and info.get('version') == runner.EXPECTED_VERSION
                      and str(info.get('actions_run_id')) == str(run_id),
                      'inventory run/source mismatch')
    sums_map = candidate.parse_sha256sums(candidate.ordinary_path(sums, 'checksums'))
    candidate.require(sums_map.get('candidate-inventory.json') == candidate.digest_file(inventory),
                      'SHA256SUMS does not bind candidate inventory')
    for asset in package['artifacts']:
        rows = [r for r in info.get('assets', []) if r.get('file') == asset['file']]
        candidate.require(len(rows) == 1, 'inventory missing/duplicate asset: ' + asset['file'])
        row = rows[0]
        candidate.require(row.get('source_commit') == sha and row.get('bytes') == asset['bytes']
                          and row.get('sha256') == asset['sha256'] == sums_map.get(asset['file']),
                          'inventory/checksum asset mismatch: ' + asset['file'])


def stage(args):
    source = candidate.ordinary_path(args.linux_input, 'directory')
    if (source / 'linux-packages').is_dir():
        source = candidate.ordinary_path(source / 'linux-packages', 'directory')
    output = Path(args.output).absolute()
    candidate.require(not output.exists() and not output.is_symlink(), 'staging output must be fresh')
    candidate.require(output.parent == output.parent.resolve(), 'symlinked staging parent')
    sums = candidate.ordinary_path(args.sha256sums, 'checksums')
    candidate.require(output not in sums.parents, 'checksums must stay outside package root')
    receipt_path = Path(args.receipt).absolute()
    candidate.require(receipt_path != output and output not in receipt_path.parents,
                      'stage receipt must stay outside package root')
    manifest = candidate.ordinary_path(source / 'linux-x64-package-manifest.json', 'manifest')
    # Filenames are derived, never accepted as paths from untrusted JSON.
    names = [f'MeowChan-{runner.EXPECTED_VERSION}-linux-x64{suffix}' for _, suffix in candidate.FLAVORS]
    inputs = [candidate.ordinary_path(source / name, 'asset') for name in names] + [manifest]
    output.mkdir()
    for path in inputs:
        shutil.copyfile(path, output / path.name)
    package = candidate.package_set(output, args.candidate_sha, runner.EXPECTED_VERSION, sha256sums=sums)
    binding(args.inventory, sums, args.candidate_sha, args.run_id, package)
    receipt = {'schema': 'meowchan.ap04-stage.v1', 'source_commit': args.candidate_sha,
               'actions_run_id': str(args.run_id), 'package': package,
               'inventory': str(Path(args.inventory).absolute()),
               'inventory_sha256': candidate.digest_file(args.inventory)}
    write_new(args.receipt, receipt)
    return receipt


def check(args):
    receipt = read(args.stage_receipt)
    candidate.require(receipt.get('schema') == 'meowchan.ap04-stage.v1'
                      and receipt.get('source_commit') == args.candidate_sha
                      and receipt.get('actions_run_id') == str(args.run_id), 'stage run/source mismatch')
    package = receipt['package']
    current = candidate.package_set(package['directory'], args.candidate_sha, runner.EXPECTED_VERSION,
                                    sha256sums=package['sha256sums'])
    candidate.require(current == package, 'staged package or checksums changed')
    candidate.require(candidate.digest_file(candidate.ordinary_path(receipt['inventory'], 'inventory'))
                      == receipt['inventory_sha256'], 'inventory changed')
    binding(receipt['inventory'], package['sha256sums'], args.candidate_sha, args.run_id, current)
    out = candidate.ordinary_path(args.out, 'directory')
    report = read(out / 'report.json')
    frozen = read(out / 'candidate.json')
    candidate.require(frozen.get('schema') == candidate.SCHEMA and frozen.get('commit') == args.candidate_sha and frozen.get('branch') is None
                      and frozen.get('clean') is True and frozen.get('version') == runner.EXPECTED_VERSION
                      and set(frozen.get('trees', {})) == {'consumer', 'tests'}
                      and frozen.get('package') == package, 'AP04 candidate/package mismatch')
    candidate.require(report.get('candidate') == candidate.tokens(frozen), 'report candidate tokens mismatch')
    candidate.require(report.get('verdict') == 'PASS' and report.get('final_problems') == []
                      and runner.verdict(report['steps'], report['final_problems'], True, True) == 'PASS',
                      'AP04 did not complete every acceptance step')
    execution = report.get('execution', {})
    candidate.require(execution.get('netns') is True and execution.get('namespace_backend') == 'bwrap-root'
                      and execution.get('namespace_verified') is True,
                      'AP04 requires bwrap-root network isolation')
    candidate.require(not candidate.verify(execution['root'], frozen), 'candidate changed after AP04 report')
    candidate.require(report.get('source_upgrade') == {'synthetic_profile': True,
                      'released_revision': runner.RELEASE_REVISION, 'executed': True}, 'missing source upgrade proof')
    candidate.require(report.get('packaged_release') == {'verification': 'not-executed',
                      'artifact_root_bound': True, 'sha256sums_bound': True}, 'invalid package scope')
    result = {'schema': 'meowchan.ap04-check.v1', 'source_commit': args.candidate_sha,
              'actions_run_id': str(args.run_id), 'passed': True,
              'report_sha256': candidate.digest_file(out / 'report.json'),
              'stage_receipt_sha256': candidate.digest_file(args.stage_receipt),
              'scope': 'synthetic source upgrade; packages provenance-bound, not executed'}
    report_path = Path(args.report).absolute()
    candidate.require(Path(package['directory']) not in report_path.parents,
                      'check receipt must stay outside package root')
    write_new(args.report, result)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    staging = commands.add_parser('stage')
    checking = commands.add_parser('check')
    for sub in (staging, checking):
        sub.add_argument('--candidate-sha', required=True)
        sub.add_argument('--run-id', required=True)
    for name in ('linux-input', 'inventory', 'sha256sums', 'output', 'receipt'):
        staging.add_argument('--' + name, type=Path, required=True)
    for name in ('out', 'stage-receipt', 'report'):
        checking.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        result = stage(args) if args.command == 'stage' else check(args)
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(1, 'AP04 refused: ' + str(error) + '\n')
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
