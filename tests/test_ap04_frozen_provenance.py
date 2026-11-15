"""Offline freeze regressions; synthetic Git/package/TRX inputs, never product execution.

Run: python3 -m unittest discover -s tests -p test_ap04_frozen_provenance.py -v
The main() harness substitutes heavy steps only; its green control is NOT AP04 evidence.
"""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

FIXTURE = Path(__file__).resolve().parent / 'fixtures/upgrade-v1.0.2'
sys.path.insert(0, str(FIXTURE))
import ap04_candidate as candidate  # noqa: E402
import ap04_run as runner  # noqa: E402
import check as source_check  # noqa: E402


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True, stderr=subprocess.PIPE).strip()


def digest(data):
    return hashlib.sha256(data).hexdigest()


class FrozenProvenanceTests(unittest.TestCase):
    def setUp(self):
        # The production guard intentionally bounds --out to 64 characters.
        # Use a fresh private short root even when the caller has a long TMPDIR.
        self.temp = tempfile.TemporaryDirectory(prefix='ap04-', dir='/tmp')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / 'repo'
        self.root.mkdir()
        git(self.root, '-c', 'init.defaultBranch=fixture', 'init', '-q')
        git(self.root, 'config', 'user.name', 'Offline fixture')
        git(self.root, 'config', 'user.email', 'fixture@example.invalid')
        (self.root / 'Directory.Build.props').write_text('<Project><Version>1.1.0</Version></Project>')
        (self.root / 'tracked.txt').write_text('candidate source')
        git(self.root, 'add', '--', 'Directory.Build.props', 'tracked.txt')
        git(self.root, '-c', 'commit.gpgsign=false', 'commit', '-qm', 'synthetic candidate')
        self.sha = git(self.root, 'rev-parse', 'HEAD')
        git(self.root, 'checkout', '--detach', '-q', self.sha)
        self.packages = self.base / 'packages'
        self.packages.mkdir()
        self.sums = self.base / 'SHA256SUMS'
        self.write_packages(self.sha)
        self.out = self.base / 'out'

    def write_packages(self, sha, payload=b'original'):
        rows = []
        for flavor, suffix in candidate.FLAVORS:
            name = f'MeowChan-1.1.0-linux-x64{suffix}'
            data = payload + flavor.encode()
            (self.packages / name).write_bytes(data)
            rows.append({'file': name, 'flavor': flavor, 'sha256': digest(data), 'bytes': len(data)})
        manifest = {'schema': candidate.PACKAGE_SCHEMA, 'runtime': 'linux-x64', 'version': '1.1.0',
                    'source_commit': sha, 'apphost_sha256': digest(payload), 'artifacts': rows}
        (self.packages / 'linux-x64-package-manifest.json').write_text(json.dumps(manifest))
        self.sums.write_text(''.join(f"{row['sha256']}  {row['file']}\n" for row in rows))

    def context(self):
        return runner.Context(SimpleNamespace(root=self.root, candidate_sha=self.sha, out=self.out,
                                             no_netns=True, lock_wait=1, linux_package_dir=self.packages,
                                             sha256sums=self.sums, rid='linux-x64', allow_no_package=False))

    def identity(self):
        return candidate.identity(self.root, self.sha, self.packages, sha256sums=self.sums)

    def preflight(self, ctx):
        # Real preflight guards and Git plumbing; only host capability/capacity checks are substituted.
        with mock.patch.object(runner, 'ALLOWED_OUT', self.base), \
                mock.patch.object(runner, 'ROOT', self.root), \
                mock.patch.object(runner, 'FEED', self.base), \
                mock.patch.object(runner.shutil, 'which', return_value='/synthetic/tool'), \
                mock.patch.object(runner.shutil, 'disk_usage', return_value=SimpleNamespace(free=runner.MIN_FREE_BYTES)):
            return runner.step_preflight(ctx)

    def test_preflight_requires_a_detached_candidate_before_creating_evidence(self):
        git(self.root, 'checkout', '-q', 'fixture')
        # Substitute only the release object id for this preflight-order test; the
        # separate missing-object test keeps the real pinned 1.0.2 requirement.
        with mock.patch.object(runner, 'RELEASE_REVISION', self.sha):
            with self.assertRaisesRegex(runner.StepFailed, 'detached'):
                self.preflight(self.context())
        self.assertFalse(self.out.exists())

    def test_detached_preflight_control(self):
        with mock.patch.object(runner, 'RELEASE_REVISION', self.sha):
            self.assertIn('clean=yes fresh=yes', self.preflight(self.context()))
        self.assertTrue((self.out / 'evidence/status-before.txt').is_file())

    def test_preflight_refuses_a_root_other_than_the_checkout_of_the_runner(self):
        # The helpers imported by the runner must be the candidate's own files: another --root would
        # stamp the candidate SHA onto a run judged by code of a different checkout.
        ctx = self.context()
        with mock.patch.object(runner, 'RELEASE_REVISION', self.sha), \
                mock.patch.object(runner, 'ALLOWED_OUT', self.base), \
                mock.patch.object(runner, 'FEED', self.base), \
                mock.patch.object(runner.shutil, 'which', return_value='/synthetic/tool'), \
                mock.patch.object(runner.shutil, 'disk_usage', return_value=SimpleNamespace(free=runner.MIN_FREE_BYTES)):
            with self.assertRaisesRegex(runner.StepFailed, 'not the checkout of this runner'):
                runner.step_preflight(ctx)
        self.assertFalse(self.out.exists())

    def test_candidate_root_must_be_the_git_toplevel_not_a_subdirectory(self):
        sub = self.root / 'nested'
        sub.mkdir()
        (sub / 'Directory.Build.props').write_text('<Project><Version>1.1.0</Version></Project>')
        # Untracked file makes the tree dirty as well; the toplevel rule must name the real cause first.
        with self.assertRaisesRegex(ValueError, 'not the top level of its Git worktree'):
            candidate.require_frozen(sub, self.sha)
        (sub / 'Directory.Build.props').unlink()
        sub.rmdir()
        self.assertEqual(self.sha, candidate.require_frozen(self.root, self.sha)['commit'])

    def release_context(self, artifact, sums):
        args = SimpleNamespace(release_artifact=artifact, release_sums=sums, linux_package_dir=self.packages,
                               sha256sums=self.sums, rid='linux-x64')
        ctx = self.context()
        ctx.args = args
        ctx.candidate = self.identity()
        ctx.evidence = self.base / 'evidence'
        ctx.evidence.mkdir(exist_ok=True)
        return ctx

    def release_inputs(self, name='04-MeowChan-1.0.2-linux-x64.AppImage', data=b'published 1.0.2 asset'):
        directory = self.base / 'published'
        directory.mkdir(exist_ok=True)
        (directory / name).write_bytes(data)
        sums = directory / 'SHA256SUMS'
        sums.write_text(f'{digest(data)}  {name}\n')
        return directory / name, sums

    def test_release_artifact_control_is_checksum_only_and_records_not_executed(self):
        artifact, sums = self.release_inputs()
        ctx = self.release_context(artifact, sums)
        self.assertIn('not executed', runner.step_release_artifact(ctx))
        record = json.loads((ctx.evidence / 'release-artifact.json').read_text())
        self.assertEqual((artifact.name, digest(artifact.read_bytes()), False),
                         (record['file'], record['sha256'], record['executed']))

    def test_release_artifact_cannot_be_a_candidate_package_or_foreign_name(self):
        copy_name = 'MeowChan-1.0.2-linux-x64.AppImage'
        # A 1.1.0 package renamed to look like 1.0.2 (content equals a candidate asset).
        renamed, sums = self.release_inputs(copy_name, (self.packages / 'MeowChan-1.1.0-linux-x64.AppImage').read_bytes())
        with self.assertRaisesRegex(runner.StepFailed, 'is a 1.1.0 candidate package'):
            runner.step_release_artifact(self.release_context(renamed, sums))
        # The 1.1.0 package itself, with the 1.1.0 SHA256SUMS.
        with self.assertRaisesRegex(runner.StepFailed, 'not a 1.0.2 release asset name'):
            runner.step_release_artifact(self.release_context(self.packages / 'MeowChan-1.1.0-linux-x64.AppImage', self.sums))
        # A genuine-looking name but the candidate's checksum file.
        artifact, _ = self.release_inputs()
        with self.assertRaisesRegex(runner.StepFailed, 'SHA256SUMS of the 1.1.0 candidate'):
            runner.step_release_artifact(self.release_context(artifact, self.sums))
        # A 1.1.0 file name never becomes evidence by listing it in a sums file.
        other, other_sums = self.release_inputs('MeowChan-1.1.0-linux-x64.AppImage', b'x')
        with self.assertRaisesRegex(runner.StepFailed, 'not a 1.0.2 release asset name'):
            runner.step_release_artifact(self.release_context(other, other_sums))

    def test_release_artifact_rejects_symlinked_inputs_and_mismatched_checksum(self):
        artifact, sums = self.release_inputs()
        link = self.base / 'link.AppImage'
        link.symlink_to(artifact)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            runner.step_release_artifact(self.release_context(link, sums))
        artifact.write_bytes(b'tampered')
        with self.assertRaisesRegex(runner.StepFailed, 'does not match its published checksum'):
            runner.step_release_artifact(self.release_context(artifact, sums))

    def test_release_artifact_options_must_come_as_a_pair(self):
        for extra in (('--release-artifact', '/x/MeowChan-1.0.2-linux-x64.AppImage'), ('--release-sums', '/x/SHA256SUMS')):
            with self.assertRaises(SystemExit) as raised, contextlib.redirect_stderr(io.StringIO()):
                runner.main(['--candidate-sha', self.sha, '--out', str(self.out), '--allow-no-package', *extra])
            self.assertEqual(2, raised.exception.code)
        self.assertFalse(self.out.exists())

    def test_preflight_refuses_missing_local_102_object_without_fetch_or_output(self):
        # This tiny repository deliberately has no released source object, even though the
        # candidate and package inputs are consistent. No dotnet/tool probe is actually run.
        with self.assertRaisesRegex(runner.StepFailed, 'pinned 1.0.2 commit.*local Git object'):
            self.preflight(self.context())
        self.assertFalse(self.out.exists())

    def test_stale_package_root_is_refused_even_with_self_consistent_sums(self):
        (self.root / 'tracked.txt').write_text('later candidate source')
        git(self.root, '-c', 'commit.gpgsign=false', 'commit', '-qam', 'synthetic final freeze')
        final_sha = git(self.root, 'rev-parse', 'HEAD')
        self.assertNotEqual(self.sha, final_sha)
        ctx = self.context()
        ctx.sha = final_sha
        self.out.mkdir()
        with self.assertRaisesRegex(ValueError, 'source_commit.*is not the candidate'):
            runner.step_candidate(ctx)
        self.assertFalse(ctx.candidate_path.exists())

    def test_candidate_step_refuses_a_different_worktree_commit(self):
        (self.root / 'tracked.txt').write_text('later source')
        git(self.root, '-c', 'commit.gpgsign=false', 'commit', '-qam', 'another freeze')
        self.out.mkdir()
        ctx = self.context()
        with self.assertRaisesRegex(ValueError, 'worktree HEAD.*is not the candidate'):
            runner.step_candidate(ctx)
        self.assertFalse(ctx.candidate_path.exists())

    def test_final_verify_detects_coherent_repack_not_just_bad_checksums(self):
        frozen = self.identity()
        self.assertEqual([], candidate.verify(self.root, frozen))
        self.write_packages(self.sha, b'repacked after inventory')
        # The replacement would be a valid NEW identity, but cannot replace the frozen one.
        self.assertEqual([], candidate.verify(self.root, self.identity()))
        self.assertIn('package files changed after recording', candidate.verify(self.root, frozen))

    def test_final_verify_rejects_package_inventory_mutations(self):
        for mutation in ('bytes', 'missing', 'extra', 'symlink', 'fifo', 'manifest', 'sums'):
            with self.subTest(mutation=mutation):
                self.write_packages(self.sha)
                frozen = self.identity()
                asset = self.packages / 'MeowChan-1.1.0-linux-x64.AppImage'
                extra = self.packages / 'unlisted'
                if mutation == 'bytes':
                    asset.write_bytes(b'tampered')
                elif mutation == 'missing':
                    asset.unlink()
                elif mutation == 'extra':
                    extra.write_bytes(b'not inventoried')
                elif mutation == 'symlink':
                    asset.unlink()
                    asset.symlink_to(self.sums)
                elif mutation == 'fifo':
                    os.mkfifo(extra)
                elif mutation == 'manifest':
                    manifest = self.packages / 'linux-x64-package-manifest.json'
                    manifest.write_text(manifest.read_text() + '\n')
                else:
                    self.sums.write_text(self.sums.read_text() + '\n')
                self.assertTrue(candidate.verify(self.root, frozen), mutation)
                if asset.is_symlink():
                    asset.unlink()
                if extra.exists():
                    extra.unlink()

    def released_input(self, ctx):
        """Minimal synthetic source/receipt accepted by the real offline checker, not a build."""
        ctx.fix = FIXTURE
        ctx.src.mkdir(parents=True)
        data = b'<Project><Version>1.0.2</Version></Project>'
        (ctx.src / 'Directory.Build.props').write_bytes(data)
        overlays = {}
        for name in ('Program.cs', 'UpgradeFixtureGenerator.csproj'):
            relative = source_check.OVERLAY + name
            overlay = (FIXTURE / name).read_bytes()
            target = ctx.src / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(overlay)
            overlays[relative] = {'bytes': len(overlay), 'sha256': digest(overlay)}
        manifest = {'schema': 'meowchan.synthetic-upgrade-102-source.v2', 'synthetic': True,
                    'source_commit': runner.RELEASE_REVISION, 'overlay': overlays,
                    'prepare_sha256': digest((FIXTURE / 'prepare.py').read_bytes()),
                    'files': {'Directory.Build.props': {'bytes': len(data), 'sha256': digest(data),
                              'git_mode': '100644',
                              'git_blob': hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()}}}
        manifest_path = ctx.src / 'UPGRADE-102-SOURCE.json'
        manifest_path.write_text(json.dumps(manifest))
        ids = [{'Source': source, 'Board': 'g', 'Thread': '123'} for source in ('4chan', '8kun')]
        asset = {'Length': len(source_check.PNG), 'Sha256': digest(source_check.PNG), 'Sha1': source_check.PNG_SHA1}
        settings = {'DownloadsPaused': True, 'VideoVolume': 31, 'VideoStartMuted': True,
                    'GalleryContextOpen': True, 'AutomaticUpdateChecks': False,
                    'ArchiveThreadLayout': 'Table', 'ArchiveSplitWidth': 431, 'WindowWidth': 1123}
        return {'Schema': 'meowchan.synthetic-upgrade-102.v1', 'Synthetic': True,
                'SourceRevision': runner.RELEASE_REVISION, 'ProductVersion': '1.0.2+' + runner.RELEASE_REVISION,
                'SourceManifestSha256': digest(manifest_path.read_bytes()),
                'Files': {'synthetic-input.txt': digest(b'synthetic input, no user data')},
                'Threads': [{'Id': item, 'Posts': [{'PostId': '123'}, {'PostId': '124'}], 'Asset': asset} for item in ids],
                'Inventory': {
                    'MediaFavorites': [{'Length': asset['Length'], 'Key': {'Thread': item, 'Sha256': asset['Sha256']}} for item in ids],
                    'Ratings': [{'Key': {'Thread': item}, 'Value': rating} for item, rating in zip(ids, (5, 2))],
                    'ThreadFavorites': {'Threads': ids},
                    'Bookmarks': {'Bookmarks': [{'Key': {'Thread': item, 'Post': '124'},
                                                'Note': 'SYNTHETIC 1.0.2 retained reply\nNo personal content.'} for item in ids]},
                    'Watch': {'ThreadUrls': sorted(source_check.URLS)}, 'Settings': settings},
                'Requests': ['https://a.4cdn.org/boards.json'],
                'LegacyChanPlugins': source_check.LEGACY_CHAN_STATES}

    def test_released_input_must_bind_pinned_102_receipt_and_source_before_current_open(self):
        ctx = self.context()
        receipt = self.released_input(ctx)
        ctx.evidence.mkdir()
        generator = self.base / 'synthetic_generator.py'
        # Execute only a Python stand-in for the generator. Receipt/source validation and
        # subprocess exit handling remain the runner's real public path through check.py.
        for field, bad in ((None, None), ('SourceRevision', self.sha),
                           ('ProductVersion', '1.1.0+' + self.sha), ('SourceManifestSha256', '0' * 64)):
            with self.subTest(field=field):
                document = dict(receipt)
                if field:
                    document[field] = bad
                generator.write_text('from pathlib import Path\n'
                                     f'p = Path({str(ctx.profile)!r})\n'
                                     'p.mkdir(parents=True, exist_ok=True)\n'
                                     '(p / "synthetic-input.txt").write_bytes(b"synthetic input, no user data")\n'
                                     f'(p / "v102-receipt.json").write_text({json.dumps(document)!r})\n')
                with mock.patch.object(ctx, 'heavy', return_value=[sys.executable, '-I', str(generator)]):
                    if field:
                        with self.assertRaisesRegex(runner.StepFailed, 'released-receipt-check: exit 1'):
                            runner.step_released_generate(ctx)
                        self.assertIn('CHECK FAILED:', (ctx.logs / 'released-receipt-check.log').read_text())
                    else:
                        self.assertTrue(runner.step_released_generate(ctx).startswith('RECEIPT OK synthetic=true'))
                        self.assertEqual(document, json.loads((ctx.evidence / 'released-receipt.json').read_text()))

    def cycle_command(self, ctx, exit_code):
        # A tiny Python child stands in for the testhost, including a realistic late failure:
        # passing TRX/results already published, but the process exits nonzero at teardown.
        script = self.base / 'synthetic_testhost.py'
        script.write_text(
            'import json\nfrom pathlib import Path\n'
            f'out = Path({str(ctx.out)!r})\n'
            '(out / "test-results").mkdir(parents=True, exist_ok=True)\n'
            f'(out / "test-results/ap04.trx").write_text({self.trx()!r})\n'
            '(out / "cycles").mkdir(parents=True, exist_ok=True)\n'
            'for i in range(3):\n'
            f'    (out / "cycles" / f"cycle-{{i}}.json").write_text(json.dumps({{"Mode": "observe", '
            f'"CurrentVersion": "1.1.0+{self.sha}", "Profile": {str(ctx.profile)!r}}}))\n'
            'for name in ("current-result.json", "released-receipt.json"):\n'
            '    (out / "cycles" / name).write_text("{}")\n'
            f'raise SystemExit({exit_code})\n')
        return [sys.executable, '-I', str(script)]

    @staticmethod
    def trx():
        return ('<TestRun><Results><UnitTestResult testName="' + runner.TEST_NAME
                + '" outcome="Passed"/></Results></TestRun>')

    def test_cycles_refuses_failed_testhost_even_with_passing_trx_and_complete_results(self):
        ctx = self.context()
        argv = self.cycle_command(ctx, 3)
        with mock.patch.object(ctx, 'test_command', return_value=argv):
            with self.assertRaisesRegex(runner.StepFailed, 'cycles-test: exit 3'):
                runner.step_cycles(ctx)
        self.assertEqual('Passed', runner.trx_outcome(ctx.out / 'test-results/ap04.trx')[runner.TEST_NAME])
        self.assertIn('EXIT 3', (ctx.logs / 'cycles-test.log').read_text())

    def run_adjudication(self, mutation=None, cycle_exit=None):
        """Exercise real identity, final verify, verdict and serialized reports, not dotnet.

        Other steps return synthetic success so that ONLY the tested gate determines the
        result. In particular, the control's PASS does not assert an actual 1.0.2 upgrade.
        """
        def prepare(ctx):
            ctx.out.mkdir()
            return 'synthetic harness setup (no preflight/product execution)'

        def synthetic(_ctx):
            return 'synthetic heavy-step substitute, not upgrade evidence'

        def cycles(ctx):
            with mock.patch.object(ctx, 'test_command', return_value=self.cycle_command(ctx, cycle_exit)):
                return runner.step_cycles(ctx)

        def last(ctx):
            if mutation:
                mutation(ctx)
            return synthetic(ctx)

        overrides = {'preflight': prepare, 'candidate': runner.step_candidate, 'downgrade-notes': last}
        if cycle_exit is not None:
            overrides['cycles'] = cycles
        steps = tuple((name, overrides.get(name, synthetic)) for name, _ in runner.STEPS)
        with mock.patch.object(runner, 'STEPS', steps), contextlib.redirect_stdout(io.StringIO()):
            code = runner.main(['--root', str(self.root), '--candidate-sha', self.sha, '--out', str(self.out),
                                '--linux-package-dir', str(self.packages), '--sha256sums', str(self.sums), '--no-netns'])
        report = json.loads((self.out / 'report.json').read_text())
        return code, report, (self.out / 'report.txt').read_text()

    def assert_failed_report(self, result):
        code, report, text = result
        self.assertEqual(1, code)
        self.assertEqual('FAIL', report['verdict'])
        self.assertTrue(report['final_problems'])
        self.assertIn('VERDICT=FAIL', text)
        self.assertNotIn('VERDICT=PASS', text)
        self.assertEqual('not-executed', report['packaged_release']['verification'])

    def test_adjudication_control_records_only_source_evidence_and_bound_provenance(self):
        code, report, text = self.run_adjudication(cycle_exit=0)
        self.assertEqual(0, code)
        self.assertEqual('PASS', report['verdict'])  # synthetic adjudication control, NOT a release run
        self.assertEqual(self.sha, report['candidate']['commit'])
        self.assertEqual([], report['final_problems'])
        self.assertEqual(runner.RELEASE_REVISION, report['source_upgrade']['released_revision'])
        self.assertTrue(report['source_upgrade']['synthetic_profile'])
        self.assertEqual({'verification': 'not-executed', 'artifact_root_bound': True, 'sha256sums_bound': True},
                         report['packaged_release'])
        self.assertIn('NOT a real-profile', text)

    def test_main_never_claims_upgrade_for_packages_replaced_after_identity(self):
        self.assert_failed_report(self.run_adjudication(lambda _ctx: self.write_packages(self.sha, b'new package')))

    def test_main_never_claims_upgrade_for_worktree_advanced_after_identity(self):
        def advance(_ctx):
            (self.root / 'tracked.txt').write_text('changed after freeze')
            git(self.root, '-c', 'commit.gpgsign=false', 'commit', '-qam', 'later candidate')
        self.assert_failed_report(self.run_adjudication(advance))

    def test_main_retains_testhost_failure_instead_of_green_upgrade_claim(self):
        result = self.run_adjudication(cycle_exit=3)
        self.assert_failed_report(result)
        _, report, _ = result
        self.assertEqual('FAIL', next(step['status'] for step in report['steps'] if step['name'] == 'cycles'))
        self.assertFalse(report['source_upgrade']['executed'])


if __name__ == '__main__':
    unittest.main()
