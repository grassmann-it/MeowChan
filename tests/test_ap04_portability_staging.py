"""Small local file/receipt tests; never build, restore or execute the AP04 profile."""
import copy
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent / 'fixtures/upgrade-v1.0.2'))
import ap04_candidate as candidate
import ap04_run as runner
import ap04_stage as stage

SHA = 'a' * 40


def dump(path, value):
    path.write_text(json.dumps(value))


class PortabilityTests(unittest.TestCase):
    def test_configured_paths_reach_restore_environment_lock_and_namespace(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            args = SimpleNamespace(root=base / 'candidate', candidate_sha=SHA, out=base / 'run',
                                   out_root=base, offline_feed=base / 'feed', heavy_lock=base / 'heavy.lock',
                                   no_netns=False, namespace_backend='bwrap-root', lock_wait=13)
            ctx = runner.Context(args)
            runner.guard_out(ctx.out, ctx.out_root)
            self.assertEqual(ctx.env()['NUGET_PACKAGES'], str(args.offline_feed))
            self.assertIn(str(args.heavy_lock), ctx.heavy(['true']))
            restore = ctx.restore('fixture.csproj')
            self.assertEqual(restore[restore.index('--source') + 1], str(args.offline_feed))
            self.assertIn('--locked-mode', restore)
            command = ctx.isolated(['true'])
            self.assertIn(['--ro-bind', str(args.offline_feed), str(args.offline_feed)],
                          [command[i:i+3] for i in range(len(command))])
            self.assertIn('--net', command)
            self.assertIn('--bounding-set=-all', command)
            self.assertNotIn('DISPLAY', ctx.env())

    def test_no_network_namespace_can_never_produce_acceptance(self):
        records = [{'name': name, 'status': 'PASS'} for name, _ in runner.STEPS]
        self.assertEqual(runner.verdict(records, [], True, True, network_isolated=False), 'SOURCE-ONLY')
        self.assertEqual(runner.verdict(records, [], True, True, network_isolated=True), 'PASS')

    def test_explicit_environment_survives_sudo_without_forwarding_desktop(self):
        args = SimpleNamespace(root='/tmp/candidate', candidate_sha=SHA, out='/tmp/ap04/run',
                               no_netns=False, namespace_backend='bwrap-root', lock_wait=1)
        ctx = runner.Context(args)
        env = ctx.env()
        env.update(MEOWCHAN_HOST_NETNS='net:[123]', MEOWCHAN_EXPECTED_UID='1000',
                   DISPLAY=':99', SECRET='not-forwarded')
        command = ctx._bwrap(['true'], False, environment=env)
        forwarded = {command[i+1]: command[i+2] for i, value in enumerate(command) if value == '--setenv'}
        self.assertEqual(forwarded['MEOWCHAN_HOST_NETNS'], 'net:[123]')
        self.assertEqual(forwarded['MEOWCHAN_EXPECTED_UID'], '1000')
        self.assertEqual(forwarded['HOME'], '/tmp/ap04/run/home')
        self.assertEqual(forwarded['TMPDIR'], '/tmp/ap04/run/tmp')
        self.assertEqual(forwarded['MEOWCHAN_DISABLE_UPDATE_CHECK'], '1')
        self.assertNotIn('DISPLAY', forwarded)
        self.assertNotIn('SECRET', forwarded)
        self.assertNotIn('-E', command)

    def test_fresh_short_bounded_output_and_symlink_refusal(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runner.guard_out(root / 'fresh', root)
            for bad in (root, root.parent / 'escape', root / ('x' * 65)):
                with self.subTest(bad=bad), self.assertRaises(runner.StepFailed):
                    runner.guard_out(bad, root)
            (root / 'existing').mkdir()
            with self.assertRaises(runner.StepFailed):
                runner.guard_out(root / 'existing', root)
            (root / 'link').symlink_to(root, target_is_directory=True)
            with self.assertRaises(runner.StepFailed):
                runner.guard_out(root / 'link/fresh', root / 'link')


class StagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'download/linux-packages'
        self.source.mkdir(parents=True)
        rows = []
        for flavor, suffix in candidate.FLAVORS:
            name = f'MeowChan-1.1.0-linux-x64{suffix}'
            data = flavor.encode()
            (self.source / name).write_bytes(data)
            rows.append({'file': name, 'flavor': flavor, 'bytes': len(data),
                         'sha256': hashlib.sha256(data).hexdigest()})
        self.rows = rows
        dump(self.source / 'linux-x64-package-manifest.json', {
            'schema': candidate.PACKAGE_SCHEMA, 'runtime': 'linux-x64', 'source_commit': SHA,
            'version': '1.1.0', 'apphost_sha256': 'b' * 64, 'artifacts': rows})
        (self.source / 'source-sidecar.txt').write_text('not a package')
        (self.source / 'sources').mkdir()
        self.inventory = self.root / 'candidate-inventory.json'
        dump(self.inventory, {'schema': 'meowchan.stable-candidate.v1', 'source_commit': SHA,
                             'version': '1.1.0', 'actions_run_id': '123',
                             'assets': [dict(row, source_commit=SHA) for row in rows]})
        self.sums = self.root / 'SHA256SUMS'
        self.sums.write_text(''.join(f"{r['sha256']}  {r['file']}\n" for r in rows)
                             + candidate.digest_file(self.inventory) + '  candidate-inventory.json\n')
        self.args = SimpleNamespace(linux_input=self.source.parent, inventory=self.inventory,
                                    sha256sums=self.sums, candidate_sha=SHA, run_id='123',
                                    output=self.root / 'packages', receipt=self.root / 'stage.json')

    def replay(self):
        receipt = stage.stage(self.args)
        out = self.root / 'run'
        out.mkdir()
        frozen = {'schema': candidate.SCHEMA, 'commit': SHA, 'branch': None, 'clean': True, 'version': '1.1.0',
                  'package': receipt['package'], 'trees': {
                      'consumer': {'sha256': 'c' * 64}, 'tests': {'sha256': 'd' * 64}}}
        dump(out / 'candidate.json', frozen)
        report = {'candidate': candidate.tokens(frozen), 'verdict': 'PASS', 'final_problems': [],
                  'steps': [{'name': name, 'status': 'PASS'} for name, _ in runner.STEPS],
                  'execution': {'root': str(self.root / 'candidate'), 'netns': True, 'namespace_backend': 'bwrap-root', 'namespace_verified': True},
                  'source_upgrade': {'synthetic_profile': True, 'released_revision': runner.RELEASE_REVISION,
                                     'executed': True},
                  'packaged_release': {'verification': 'not-executed', 'artifact_root_bound': True,
                                       'sha256sums_bound': True}}
        dump(out / 'report.json', report)
        return SimpleNamespace(out=out, stage_receipt=self.args.receipt, candidate_sha=SHA,
                               run_id='123', report=self.root / 'check.json'), report

    def test_stage_exact_four_copies_and_external_checksums(self):
        receipt = stage.stage(self.args)
        self.assertEqual(set(p.name for p in self.args.output.iterdir()),
                         {r['file'] for r in self.rows} | {'linux-x64-package-manifest.json'})
        self.assertEqual(receipt['actions_run_id'], '123')
        self.assertNotEqual((self.args.output / self.rows[0]['file']).stat().st_ino,
                            (self.source / self.rows[0]['file']).stat().st_ino)
        with self.assertRaisesRegex(ValueError, 'fresh'):
            stage.stage(self.args)

    def test_stage_rejects_foreign_run_source_version_and_asset_identity(self):
        original = stage.read(self.inventory)
        variants = [dict(original, actions_run_id='456'), dict(original, source_commit='e' * 40),
                    dict(original, version='1.0.2')]
        for key, value in [('sha256', '0'*64), ('bytes', 999), ('source_commit', 'f'*40)]:
            bad = copy.deepcopy(original)
            bad['assets'][0][key] = value
            variants.append(bad)
        bad = copy.deepcopy(original)
        bad['assets'].append(bad['assets'][0])
        variants.append(bad)
        for i, value in enumerate(variants):
            with self.subTest(i=i):
                dump(self.inventory, value)
                self.sums.write_text(''.join(f"{r['sha256']}  {r['file']}\n" for r in self.rows)
                                     + candidate.digest_file(self.inventory) + '  candidate-inventory.json\n')
                self.args.output = self.root / f'bad{i}'
                with self.assertRaises(ValueError):
                    stage.stage(self.args)
                self.assertFalse(self.args.receipt.exists())

    def test_receipts_cannot_pollute_strict_package_root(self):
        self.args.receipt = self.args.output / 'receipt.json'
        with self.assertRaisesRegex(ValueError, 'outside package root'):
            stage.stage(self.args)
        self.assertFalse(self.args.output.exists())

    def test_inventory_must_itself_be_checksum_bound(self):
        self.sums.write_text(''.join(f"{r['sha256']}  {r['file']}\n" for r in self.rows))
        with self.assertRaisesRegex(ValueError, 'bind candidate inventory'):
            stage.stage(self.args)
        self.assertFalse(self.args.receipt.exists())

    def test_stage_refuses_symlink_and_hardlink_assets(self):
        path = self.source / self.rows[0]['file']
        actual = self.root / 'actual'
        path.rename(actual)
        path.symlink_to(actual)
        with self.assertRaises(ValueError):
            stage.stage(self.args)
        path.unlink()
        os.link(actual, path)
        with self.assertRaises(ValueError):
            stage.stage(self.args)

    def test_check_requires_complete_bound_isolated_receipt(self):
        args, report = self.replay()
        with patch.object(candidate, 'verify', return_value=[]) as verify:
            self.assertTrue(stage.check(args)['passed'])
            verify.assert_called_once()
        args.report.unlink()
        variants = []
        for verdict in ('SOURCE-ONLY', 'FAIL', 'INCOMPLETE'):
            variants.append(dict(report, verdict=verdict))
        variants += [dict(report, final_problems=['changed']), dict(report, steps=report['steps'][:-1]),
                     dict(report, execution={'netns': False, 'namespace_backend': 'bwrap-root'}),
                     dict(report, execution={'netns': True, 'namespace_backend': 'unshare'}),
                     dict(report, packaged_release={'verification': 'executed'})]
        for index in range(len(report['steps'])):
            bad = copy.deepcopy(report)
            bad['steps'][index]['status'] = 'SKIP'
            variants.append(bad)
        missing_probe = copy.deepcopy(report)
        del missing_probe['execution']['namespace_verified']
        variants.append(missing_probe)
        for i, bad in enumerate(variants):
            with self.subTest(i=i), patch.object(candidate, 'verify', return_value=[]):
                dump(args.out / 'report.json', bad)
                with self.assertRaises(ValueError):
                    stage.check(args)
                self.assertFalse(args.report.exists())

    def test_check_rejects_changed_package_sums_inventory_and_candidate(self):
        args, _ = self.replay()
        with patch.object(candidate, 'verify', return_value=['changed sealed tree']):
            with self.assertRaisesRegex(ValueError, 'changed after'):
                stage.check(args)
        for path in (self.args.output / self.rows[0]['file'], self.sums, self.inventory):
            data = path.read_bytes()
            path.write_bytes(data + b'\n')
            with self.subTest(path=path), self.assertRaises(ValueError):
                stage.check(args)
            path.write_bytes(data)
        args.run_id = '456'
        with self.assertRaisesRegex(ValueError, 'run/source'):
            stage.check(args)
        self.assertFalse(args.report.exists())


if __name__ == '__main__':
    unittest.main()
