"""Regressions for the AP04 final-run tooling: candidate binding, runner refusals, post-migration checks.

No dotnet, no profile and no network: temporary Git repositories and synthetic JSON/files only. Run:
    python3 -m unittest discover -s tests -p test_ap04_run.py -v
"""
import contextlib
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import socket
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import unittest.mock

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / 'tests/fixtures/upgrade-v1.0.2'
sys.path.insert(0, str(FIXTURE))
import ap04_candidate as cand  # noqa: E402
import ap04_run as run  # noqa: E402
import check_writes as cw  # noqa: E402

SHA = 'e' * 40


def git(root, *args):
    subprocess.run(['git', *args], cwd=root, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def make_repo(directory, version='1.1.0', ignore=()):
    root = Path(directory)
    git(root, 'init', '-q')
    git(root, 'config', 'user.email', 't@example.invalid')
    git(root, 'config', 'user.name', 't')
    (root / 'Directory.Build.props').write_text(f'<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>')
    (root / 'a.txt').write_text('a')
    if ignore:
        (root / '.gitignore').write_text(''.join(pattern + '\n' for pattern in ignore))
    git(root, 'add', '-A')
    git(root, 'commit', '-q', '-m', 'x')
    return subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class CandidateTests(unittest.TestCase):
    def test_clean_candidate_records_commit_and_tokens(self):
        with tempfile.TemporaryDirectory() as temp:
            sha = make_repo(temp)
            candidate = cand.identity(temp, sha)
            self.assertEqual((sha, '1.1.0', True), (candidate['commit'], candidate['version'], candidate['clean']))
            self.assertEqual({'commit': sha, 'bin': 'unsealed', 'pkg': 'none'}, cand.tokens(candidate))

    def test_dirty_untracked_other_or_short_sha_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            sha = make_repo(temp)
            with self.assertRaisesRegex(ValueError, 'not the candidate'):
                cand.require_frozen(temp, '0' * 40)
            with self.assertRaisesRegex(ValueError, 'full lowercase'):
                cand.require_frozen(temp, sha[:12])
            (Path(temp) / 'untracked.txt').write_text('x')
            with self.assertRaisesRegex(ValueError, 'dirty'):
                cand.require_frozen(temp, sha)
            (Path(temp) / 'untracked.txt').unlink()
            (Path(temp) / 'a.txt').write_text('changed')
            with self.assertRaisesRegex(ValueError, 'dirty'):
                cand.require_frozen(temp, sha)

    def test_seal_and_verify_detect_changed_bytes_new_commit_and_dirt(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as built:
            sha = make_repo(temp)
            (Path(built) / 'MeowChan.dll').write_bytes(b'one')
            (Path(built) / 'run.log').write_text('ignored')
            candidate = cand.identity(temp, sha)
            digest = cand.seal(candidate, 'consumer', built)
            self.assertEqual(['MeowChan.dll'], sorted(candidate['trees']['consumer']['files']))
            self.assertEqual(digest[:16], candidate['trees']['consumer']['sha256'][:16])
            self.assertEqual([], cand.verify(temp, candidate))
            (Path(built) / 'run.log').write_text('still ignored')
            self.assertEqual([], cand.verify(temp, candidate))
            (Path(built) / 'MeowChan.dll').write_bytes(b'two')
            self.assertTrue(any('changed after sealing' in p for p in cand.verify(temp, candidate)))
            (Path(built) / 'MeowChan.dll').write_bytes(b'one')
            (Path(temp) / 'a.txt').write_text('dirty')
            self.assertTrue(any('dirty' in p for p in cand.verify(temp, candidate)))
            git(temp, 'commit', '-q', '-am', 'later')
            self.assertTrue(any('not the candidate' in p for p in cand.verify(temp, candidate)))

    def test_ignored_build_outputs_are_refused_but_bytecode_is_allowed(self):
        # `git status` (require_frozen) does not see ignored files: a reused bin/ or obj/ looks clean.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sha = make_repo(temp, ignore=('bin/', 'obj/', '*.dll', '*.user', '__pycache__/'))
            (root / 'tests/__pycache__').mkdir(parents=True)
            (root / 'tests/__pycache__/m.cpython-314.pyc').write_bytes(b'')
            self.assertEqual([], cand.ignored_entries(temp))
            cand.require_fresh_outputs(temp)
            self.assertEqual([], cand.identity(temp, sha)['ignored_at_identity'])
            for stale in ('current/bin/Debug/net10.0/MeowChan.dll', 'current/obj/project.assets.json', 'old.dll',
                          'MeowChan.csproj.user'):
                path = root / stale
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'built from another commit')
                cand.require_frozen(temp, sha)
                with self.assertRaisesRegex(ValueError, 'ignored build output', msg=stale):
                    cand.require_fresh_outputs(temp)
                if stale.startswith('current/'):
                    shutil.rmtree(root / 'current')
                else:
                    path.unlink()
            cand.require_fresh_outputs(temp)

    def make_packages(self, directory, commit, version='1.1.0', rid='linux-x64'):
        base = Path(directory)
        rows = []
        for flavor, suffix in cand.FLAVORS:
            name = f'MeowChan-{version}-{rid}{suffix}'
            data = (flavor + name).encode()
            (base / name).write_bytes(data)
            rows.append({'flavor': flavor, 'file': name, 'sha256': sha256(data), 'bytes': len(data)})
        manifest = {'schema': cand.PACKAGE_SCHEMA, 'runtime': rid, 'version': version, 'source_commit': commit,
                    'apphost_sha256': 'a' * 64, 'artifacts': rows}
        (base / f'{rid}-package-manifest.json').write_text(json.dumps(manifest))
        return rows

    def test_package_set_binds_commit_version_and_checksums(self):
        with tempfile.TemporaryDirectory() as temp:
            rows = self.make_packages(temp, SHA)
            sums = Path(temp) / 'SHA256SUMS'
            sums.write_text(''.join(f"{row['sha256']} *{row['file']}\n" for row in rows))
            record = cand.package_set(temp, SHA, '1.1.0', sha256sums=sums)
            self.assertTrue(record['sha256sums_checked'])
            self.assertEqual(3, len(record['artifacts']))
            with self.assertRaisesRegex(ValueError, 'is not the candidate'):
                cand.package_set(temp, 'f' * 40, '1.1.0')
            with self.assertRaisesRegex(ValueError, 'version'):
                cand.package_set(temp, SHA, '1.1.1')
            sums.write_text(f"{'0' * 64} *{rows[0]['file']}\n" + ''.join(f"{r['sha256']} *{r['file']}\n" for r in rows[1:]))
            with self.assertRaisesRegex(ValueError, 'SHA256SUMS'):
                cand.package_set(temp, SHA, '1.1.0', sha256sums=sums)
            (Path(temp) / rows[1]['file']).write_bytes(b'tampered')
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                cand.package_set(temp, SHA, '1.1.0')

    def test_sha256sums_is_recorded_and_reverified(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as packages:
            sha = make_repo(temp)
            rows = self.make_packages(packages, sha)
            sums = Path(packages) / 'SHA256SUMS'
            # Two-space format, as scripts/assemble-prerelease.py writes it for the stable-candidate run.
            sums.write_text(''.join(f"{row['sha256']}  {row['file']}\n" for row in rows))
            candidate = cand.identity(temp, sha, packages, sha256sums=sums)
            package = candidate['package']
            self.assertTrue(package['sha256sums_checked'])
            self.assertEqual((str(sums.absolute()), sha256(sums.read_bytes())), (package['sha256sums'], package['sha256sums_sha256']))
            self.assertEqual([], cand.verify(temp, candidate))
            sums.write_text(sums.read_text() + f"{'1' * 64}  MeowChan-1.1.0-win-x64.zip\n")
            self.assertIn('package files changed after recording', cand.verify(temp, candidate))
            sums.unlink()
            self.assertTrue(cand.verify(temp, candidate), 'a vanished SHA256SUMS is a problem, not a crash')

    def test_package_root_inventory_detects_unlisted_file_and_manifest_mutation(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as packages:
            sha = make_repo(temp)
            self.make_packages(packages, sha)
            candidate = cand.identity(temp, sha, packages)
            (Path(packages) / 'unlisted-dependency-artifact').write_bytes(b'not in the manifest')
            self.assertIn('package files changed after recording', cand.verify(temp, candidate))
            (Path(packages) / 'unlisted-dependency-artifact').unlink()
            manifest = Path(packages) / 'linux-x64-package-manifest.json'
            manifest.write_text(manifest.read_text().replace('"version": "1.1.0"', '"version": "1.1.0"\n'))
            self.assertIn('package files changed after recording', cand.verify(temp, candidate))

    def test_package_and_build_roots_reject_symlink_components(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp)
            actual = parent / 'actual'
            actual.mkdir()
            self.make_packages(actual, SHA)
            (actual / 'SHA256SUMS').write_text('')
            link = parent / 'alias'
            link.symlink_to(actual, target_is_directory=True)
            for probe in (lambda: cand.package_set(link, SHA, '1.1.0'),
                          lambda: cand.tree_files(link),
                          lambda: cand.parse_sha256sums(link / 'SHA256SUMS')):
                with self.subTest(probe=probe), self.assertRaisesRegex(ValueError, 'symlink'):
                    probe()

    @unittest.skipUnless(hasattr(os, 'mkfifo'), 'FIFO mutation requires POSIX')
    def test_fifo_is_not_silently_omitted_from_artifact_or_build_inventory(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as packages:
            sha = make_repo(temp)
            self.make_packages(packages, sha)
            candidate = cand.identity(temp, sha, packages)
            os.mkfifo(Path(packages) / 'unexpected.pipe')
            self.assertTrue(any('special file' in problem for problem in cand.verify(temp, candidate)))
            with self.assertRaisesRegex(ValueError, 'special file'):
                cand.tree_files(packages)

    @unittest.skipUnless(hasattr(socket, 'AF_UNIX'), 'Unix socket mutation requires AF_UNIX')
    def test_socket_is_not_silently_omitted_from_package_inventory(self):
        with tempfile.TemporaryDirectory() as temp, socket.socket(socket.AF_UNIX) as server:
            self.make_packages(temp, SHA)
            server.bind(str(Path(temp) / 'unexpected.sock'))
            with self.assertRaisesRegex(ValueError, 'special file'):
                cand.artifact_files(temp)

    def test_package_checksum_reaches_the_report_prefix(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as packages:
            sha = make_repo(temp)
            self.make_packages(packages, sha)
            candidate = cand.identity(temp, sha, packages)
            tokens = cand.tokens(candidate)
            self.assertEqual(candidate['package']['manifest_sha256'][:16], tokens['pkg'])
            self.assertIn(sha, run.prefix(tokens))
            self.assertIn(tokens['pkg'], run.prefix(tokens))


class RunnerTests(unittest.TestCase):
    def records(self, status='PASS'):
        return [{'name': name, 'status': status, 'seconds': 1, 'detail': 'd'} for name, _ in run.STEPS]

    def test_verdict_needs_every_step_a_frozen_candidate_and_a_package(self):
        # PASS only with the package manifest AND the SHA256SUMS of the stable-candidate run.
        self.assertEqual('PASS', run.verdict(self.records(), [], True, True))
        # --allow-no-package (no package) or a package without SHA256SUMS: never PASS.
        self.assertEqual('SOURCE-ONLY', run.verdict(self.records(), [], False, False))
        self.assertEqual('SOURCE-ONLY', run.verdict(self.records(), [], True, False))
        self.assertEqual('FAIL', run.verdict(self.records(), ['worktree is dirty'], True, True))
        failed = self.records()
        failed[3]['status'] = 'FAIL'
        self.assertEqual('FAIL', run.verdict(failed, [], True, True))
        skipped = self.records()
        skipped[-1]['status'] = 'SKIP'
        self.assertEqual('INCOMPLETE', run.verdict(skipped, [], True, True))
        self.assertEqual('INCOMPLETE', run.verdict(skipped, [], False, False))

    def test_verdict_rejects_missing_or_reordered_steps_even_when_all_are_green(self):
        records = self.records()
        self.assertEqual('INCOMPLETE', run.verdict(records[:-1], [], True, True))
        reordered = list(records)
        reordered[0], reordered[1] = reordered[1], reordered[0]
        self.assertEqual('INCOMPLETE', run.verdict(reordered, [], True, True))

    def test_source_only_report_is_never_a_pass(self):
        tokens = {'commit': SHA, 'bin': 'b' * 16, 'pkg': 'none'}
        result = run.verdict(self.records(), [], False, False)
        lines = run.render(tokens, self.records(), [], result, '/x')
        self.assertEqual('SOURCE-ONLY', result)
        self.assertTrue(lines[-2].endswith('VERDICT=SOURCE-ONLY'))
        self.assertIn('NOT an AP04 acceptance', lines[-3])
        self.assertNotIn('VERDICT=PASS', '\n'.join(lines))
        self.assertTrue(all(f'candidate={SHA} bin={"b" * 16} pkg=none' in line for line in lines))

    def test_every_report_line_names_the_commit_and_checksums(self):
        tokens = {'commit': SHA, 'bin': 'b' * 16, 'pkg': 'c' * 16}
        lines = run.render(tokens, self.records(), [], 'PASS', '/x')
        self.assertTrue(lines and all(f'candidate={SHA} bin={"b" * 16} pkg={"c" * 16}' in line for line in lines))
        self.assertTrue(lines[-2].endswith('VERDICT=PASS'))
        failing = run.render(tokens, self.records(), ['worktree is dirty (1 entries)'], 'FAIL', '/x')
        self.assertTrue(all('candidate=' in line for line in failing))
        self.assertIn('step=final-verify status=FAIL', '\n'.join(failing))

    def test_report_separates_source_upgrade_from_unexecuted_package_start(self):
        tokens = {'commit': SHA, 'bin': 'b' * 16, 'pkg': 'c' * 16}
        lines = run.render(tokens, self.records(), [], 'PASS', '/x')
        joined = '\n'.join(lines)
        self.assertIn('source-upgrade evidence only', joined)
        self.assertIn('packaged-release verification was not executed', joined)
        incomplete = run.render(tokens, self.records()[:-1], [], 'INCOMPLETE', '/x')
        self.assertIn('status=NOT-RUN', '\n'.join(incomplete))
        self.assertNotIn('step=final-verify status=PASS', '\n'.join(incomplete))

    def test_trx_outcomes_are_read_by_test_name(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'a.trx'
            path.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
                            '<UnitTestResult testName="N.C.A" outcome="Passed"/><UnitTestResult testName="N.C.B" outcome="NotExecuted"/>'
                            '</Results></TestRun>')
            self.assertEqual({'N.C.A': 'Passed', 'N.C.B': 'NotExecuted'}, run.trx_outcome(path))

    def test_output_directory_guard(self):
        allowed = run.ALLOWED_OUT / 'ut-guard'
        run.guard_out(allowed)
        for bad in (Path('/tmp/ap4x'), Path.home() / '.oChan', Path.home() / '.oChan' / 'x', run.ALLOWED_OUT,
                    run.ALLOWED_OUT / ('x' * 40)):
            with self.assertRaises(run.StepFailed, msg=str(bad)):
                run.guard_out(bad)
        with tempfile.TemporaryDirectory() as temp, unittest.mock.patch.object(run, 'ALLOWED_OUT', Path(temp)):
            (Path(temp) / 'exists').mkdir()
            with self.assertRaises(run.StepFailed):
                run.guard_out(Path(temp) / 'exists')

    def invoke(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = run.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_dirty_or_other_commit_is_refused_before_anything_is_created(self):
        with tempfile.TemporaryDirectory() as temp:
            sha = make_repo(temp)
            target = run.ALLOWED_OUT / f'ut-ref-{sha[:8]}'
            self.assertFalse(target.exists())
            (Path(temp) / 'a.txt').write_text('dirty')
            code, out, _ = self.invoke('--candidate-sha', sha, '--out', str(target), '--root', temp, '--allow-no-package')
            self.assertEqual(2, code)
            self.assertIn('dirty', out)
            self.assertFalse(target.exists())
            git(temp, 'checkout', '-q', '--', 'a.txt')
            code, out, _ = self.invoke('--candidate-sha', '1' * 40, '--out', str(target), '--root', temp, '--allow-no-package')
            self.assertEqual(2, code)
            self.assertIn('is not the candidate', out)
            self.assertFalse(target.exists())

    def test_stale_build_outputs_are_refused_before_anything_is_created(self):
        with tempfile.TemporaryDirectory() as temp:
            sha = make_repo(temp, ignore=('bin/', 'obj/'))
            stale = Path(temp) / 'tests/fixtures/upgrade-v1.0.2/current/bin/Debug/net10.0'
            stale.mkdir(parents=True)
            (stale / 'MeowChan.dll').write_bytes(b'built from another commit')
            target = run.ALLOWED_OUT / f'ut-stale-{sha[:8]}'
            self.assertFalse(target.exists())
            code, out, _ = self.invoke('--candidate-sha', sha, '--out', str(target), '--root', temp, '--allow-no-package')
            self.assertEqual(2, code)
            self.assertIn('ignored build output', out)
            self.assertFalse(target.exists())

    def test_package_options_are_validated_before_anything_runs(self):
        target = run.ALLOWED_OUT / 'ut-options'
        base = ('--candidate-sha', SHA, '--out', str(target))
        for extra in ((), ('--linux-package-dir', '/x'), ('--sha256sums', '/x/SHA256SUMS'),
                      ('--allow-no-package', '--linux-package-dir', '/x', '--sha256sums', '/x/SHA256SUMS'),
                      ('--allow-no-package', '--sha256sums', '/x/SHA256SUMS'), ('--allow-no-package', '--lock-wait', '0')):
            with self.assertRaises(SystemExit, msg=str(extra)) as raised:
                self.invoke(*base, *extra)
            self.assertEqual(2, raised.exception.code, str(extra))
        self.assertFalse(target.exists())

    def test_candidate_step_requires_sha256sums_with_a_package(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as packages, \
                tempfile.TemporaryDirectory() as results:
            sha = make_repo(temp)
            rows = CandidateTests.make_packages(self, packages, sha)

            class Args:
                root, candidate_sha, out, no_netns, lock_wait = Path(temp), sha, Path(results), True, 1
                linux_package_dir, rid, sha256sums, allow_no_package = Path(packages), 'linux-x64', None, False
            ctx = run.Context(Args)
            with self.assertRaisesRegex(run.StepFailed, '--sha256sums'):
                run.step_candidate(ctx)
            Args.sha256sums = Path(packages) / 'SHA256SUMS'
            Args.sha256sums.write_text(''.join(f"{row['sha256']}  {row['file']}\n" for row in rows))
            detail = run.step_candidate(ctx)
            self.assertIn('sha256sums=' + sha256(Args.sha256sums.read_bytes())[:16], detail)
            Args.linux_package_dir = Args.sha256sums = None
            with self.assertRaisesRegex(run.StepFailed, 'never PASS'):
                run.step_candidate(ctx)

    @unittest.skipUnless(shutil.which('flock'), 'flock not available')
    def test_heavy_lock_wait_is_bounded_and_reported_apart_from_command_failures(self):
        with tempfile.TemporaryDirectory() as temp:
            lock = Path(temp) / 'heavy.lock'
            lock.write_bytes(b'')

            class Args:
                root, candidate_sha, out, no_netns, lock_wait = ROOT, SHA, Path(temp) / 'o', True, 1
            ctx = run.Context(Args)
            with unittest.mock.patch.object(run, 'LOCK', lock):
                with lock.open('rb') as held:
                    fcntl.flock(held, fcntl.LOCK_EX)
                    # check=False as for the test step: a lock not acquired must still fail as such.
                    with self.assertRaisesRegex(run.StepFailed, 'heavy.lock not acquired within 1s'):
                        run.run(ctx, 'busy', ctx.heavy(['true']), timeout=30, check=False)
                log = (ctx.logs / 'busy.log').read_text()
                self.assertTrue(log.startswith('COMMAND flock --close --wait 1 --conflict-exit-code 75 ') and 'EXIT 75' in log)
                with self.assertRaisesRegex(run.StepFailed, 'busy-free: exit 3'):
                    run.run(ctx, 'busy-free', ctx.heavy(['sh', '-c', 'exit 3']), timeout=30)
                self.assertIn('EXIT 0', run.run(ctx, 'free', ctx.heavy(['true']), timeout=30))

    def test_dotnet_restore_is_offline_locked_and_smokes_are_serialized(self):
        class Args:
            root, candidate_sha, out, no_netns, lock_wait = ROOT, SHA, run.ALLOWED_OUT / 'r-cmd', True, 1
        ctx = run.Context(Args())
        restore = list(map(str, ctx.restore('fixture.csproj')))
        self.assertIn('--locked-mode', restore)
        self.assertIn('-p:UseSharedCompilation=false', restore)
        self.assertEqual('flock', ctx.consumer_command(Path('/tmp/result.json'), 'readback')[0])
        self.assertEqual('flock', ctx.test_command()[0])

    def test_dry_run_prints_every_step_and_writes_nothing(self):
        target = run.ALLOWED_OUT / 'ut-dry'
        code, out, _ = self.invoke('--candidate-sha', SHA, '--out', str(target), '--dry-run')
        self.assertEqual(0, code)
        for name, _ in run.STEPS:
            self.assertIn(f'  {name}:', out)
        self.assertNotIn('VERDICT', out)
        self.assertFalse(target.exists())

    def test_every_child_runs_without_a_desktop_or_real_profile_variable(self):
        class Args:
            root, candidate_sha, out, no_netns, lock_wait = ROOT, SHA, run.ALLOWED_OUT / 'r', False, run.DEFAULT_LOCK_WAIT
        env = run.Context(Args).env()
        for name in run.DROPPED:
            self.assertNotIn(name, env)
        self.assertTrue(env['HOME'].startswith(str(run.ALLOWED_OUT)))
        self.assertEqual('1', env['MEOWCHAN_DISABLE_UPDATE_CHECK'])

    def test_downgrade_notes_phrases_exist_in_the_release_notes(self):
        flat = ' '.join((ROOT / 'docs/releases/v1.1.0.md').read_text(encoding='utf-8').split())
        for label, phrase in run.REQUIRED_NOTES:
            self.assertIn(' '.join(phrase.split()), flat, label)

    def test_consumer_and_test_keep_their_contracts(self):
        program = (FIXTURE / 'current/Program.cs').read_text(encoding='utf-8')
        for mode in ('"observe"', '"write-host"', '"write-local-tag"', '"readback"'):
            self.assertIn(mode, program)
        # The upgrade test uses observe only: that mode must not read or write the tag store.
        self.assertIn('if (mode != "observe") persistedTags', program)
        self.assertIn('if (mode != "observe") hostTags', program)
        self.assertNotIn('SetPausedAsync(false)', program, 'unpausing would start the watch producers (network)')
        test = (ROOT / 'tests/MeowChan.DesktopTests/Released102RestPackageUpgradeTests.cs').read_text(encoding='utf-8')
        self.assertIn('MEOWCHAN_RELEASED102_NETWORK_NAMESPACE', test)
        self.assertIn('"-Urn"', test)


def plugin_state(active=True):
    required = [{'PluginId': pid, 'Enabled': True, 'Active': active, 'ActiveExecutionRevision': 'r-' + pid,
                 'Problem': None, 'SdkTransition': None} for pid in cw.REQUIRED_PLUGINS]
    required.append({'PluginId': 'meowchan.example.off', 'Enabled': False, 'Active': False, 'ActiveExecutionRevision': None,
                     'Problem': None, 'SdkTransition': None})
    return {'Revision': 1, 'Warnings': [], 'Sources': [], 'Plugins': required}


class PostCheckTests(unittest.TestCase):
    """A synthetic profile and results that satisfy check_writes; each test breaks one fact."""
    THREAD = {'Source': 's', 'Board': 'g', 'Thread': '123'}

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name)
        self.profile, self.results, self.writes = base / 'p', base / 'c', base / 'w'
        for directory in (self.profile, self.results, self.writes / 'snapshot-before', self.writes / 'snapshot-v1'):
            directory.mkdir(parents=True)
        version = '1.1.0+' + SHA

        def cycle(mode, **extra):
            data = {'Schema': 'meowchan.released-102-current-cycle.v1', 'Mode': mode, 'HostStopped': True, 'NetworkRequests': [],
                    'Profile': str(self.profile), 'CurrentVersion': version, 'Plugins': plugin_state(),
                    'Observed': {'IsPaused': True, 'Settings': {'VideoVolume': 31, 'DownloadPath': 'd'}, 'Threads': [1], 'Work': [2]},
                    'Persisted': {'Watch': {'u': 1}, 'Settings': {'VideoVolume': 31}}}
            data.update(extra)
            return data
        for index in range(3):
            (self.results / f'cycle-{index}.json').write_text(json.dumps(cycle('observe')))
        self.v1 = json.dumps({'Version': 1, 'Revision': 2, 'Tags': [], 'Threads': [], 'Media': []}).encode()
        v2 = json.dumps({'Version': 2, 'Revision': 3}).encode()
        settings_before = {'VideoVolume': 31, 'MaxParallelDownloads': 2, 'DownloadPath': 'd'}
        (self.writes / 'snapshot-before/settings.json').write_text(json.dumps(settings_before))
        (self.profile / 'settings.json').write_text(json.dumps(dict(settings_before, VideoVolume=47, MaxParallelDownloads=3, New=1)))
        urls = {'ThreadUrls': ['u'], 'BoardUrls': [], 'DownloadDirectories': {'u': 'd'}, 'SuppressedThreadUrls': []}
        (self.writes / 'snapshot-before/saved_urls.json').write_text(json.dumps(urls))
        (self.profile / 'saved_urls.json').write_text(json.dumps(urls))
        (self.writes / 'snapshot-v1/private-tags.json').write_bytes(self.v1)
        (self.writes / 'snapshot-v1/meta.json').write_text(json.dumps({'sha256': sha256(self.v1), 'bytes': len(self.v1),
                                                                         'v1_backup_existed_before': False}))
        (self.profile / 'private-tags.json').write_bytes(v2)
        (self.profile / 'private-tags.json.bak').write_bytes(self.v1)
        (self.profile / cw.V1_BACKUP).write_bytes(self.v1)
        (self.writes / 'backup-observations.json').write_text(json.dumps([
            {'stage': 'after-conversion', 'bytes': len(self.v1), 'sha256': sha256(self.v1)},
            {'stage': 'readback-1', 'bytes': len(self.v1), 'sha256': sha256(self.v1)},
            {'stage': 'readback-2', 'bytes': len(self.v1), 'sha256': sha256(self.v1)},
        ]))
        tag = {'Name': cw.THREAD_TAG, 'Id': 'x', 'Thread': self.THREAD, 'DefinedChanged': True, 'AssignedChanged': True,
               'Revision': 2, 'FormatVersion': 1}
        (self.writes / 'write-host.json').write_text(json.dumps(cycle(
            'write-host', Writes={'Settings': {'VolumeBefore': 31, 'VolumeAfter': 47, 'ParallelBefore': 2, 'ParallelAfter': 3},
                                  'IsPaused': True, 'Tag': tag},
            HostTags={'State': 'Ready', 'Tags': [cw.THREAD_TAG], 'FormatVersion': 1}, Persisted={'Watch': {'u': 1}, 'Settings': {'VideoVolume': 47}})))
        (self.writes / 'write-local-tag.json').write_text(json.dumps(cycle(
            'write-local-tag', Writes={'Level': 'store', 'Changed': True, 'FormatVersion': 2, 'LocalAssignments': 1},
            PersistedTags={'FormatVersion': 2})))
        for index in (1, 2):
            (self.writes / f'readback-{index}.json').write_text(json.dumps(cycle(
                'readback', Observed={'IsPaused': True, 'Settings': {'VideoVolume': 47, 'DownloadPath': 'd'}, 'Threads': [1], 'Work': [2]},
                Persisted={'Watch': {'u': 1}, 'Settings': {'VideoVolume': 47}},
                HostTags={'State': 'Ready', 'FormatVersion': 2, 'Tags': sorted([cw.THREAD_TAG, cw.LOCAL_TAG]), 'ThreadAssignments': 1,
                          'LocalAssignments': 1, 'MediaAssignments': 0, 'ThreadTags': [{'Thread': self.THREAD, 'Tags': [cw.THREAD_TAG]}]},
                PersistedTags={'FormatVersion': 2, 'Tags': sorted([cw.THREAD_TAG, cw.LOCAL_TAG])})))

    def tearDown(self):
        self.temp.cleanup()

    def check(self):
        return cw.check_writes(self.profile, self.results, self.writes, SHA, '1.1.0')

    def edit(self, path, change):
        data = json.loads(path.read_text())
        change(data)
        path.write_text(json.dumps(data))

    def test_consistent_evidence_passes_and_names_the_backup(self):
        lines = self.check()
        self.assertTrue(lines[0].startswith('WRITES OK') and 'private-tags.v1.bak' in lines[0] and 'byte-identical' in lines[0])
        self.assertTrue(lines[2].startswith('PLUGINS OK') and 'starts=7' in lines[2])

    def test_backup_must_exist_and_be_byte_identical(self):
        (self.profile / cw.V1_BACKUP).write_bytes(self.v1 + b' ')
        with self.assertRaisesRegex(ValueError, 'byte-identical'):
            self.check()
        (self.profile / cw.V1_BACKUP).unlink()
        with self.assertRaisesRegex(ValueError, 'does not exist'):
            self.check()

    def test_backup_observations_must_cover_both_readbacks(self):
        observations = json.loads((self.writes / 'backup-observations.json').read_text())
        observations.pop()
        (self.writes / 'backup-observations.json').write_text(json.dumps(observations))
        with self.assertRaisesRegex(ValueError, 'observations do not cover'):
            self.check()

    def test_conversion_must_produce_version_two_and_leave_no_temporary_file(self):
        (self.profile / 'private-tags.json').write_bytes(self.v1)
        with self.assertRaisesRegex(ValueError, 'not the version 2'):
            self.check()
        (self.profile / 'private-tags.json').write_bytes(json.dumps({'Version': 2}).encode())
        (self.profile / ('.' + cw.V1_BACKUP + '.abc.tmp')).write_bytes(b'')
        with self.assertRaisesRegex(ValueError, 'temporary'):
            self.check()

    def test_inactive_or_missing_required_plugin_fails_in_any_start(self):
        path = self.writes / 'readback-2.json'
        self.edit(path, lambda d: d['Plugins']['Plugins'][0].update(Active=False))
        with self.assertRaises(ValueError):
            self.check()
        self.edit(path, lambda d: d['Plugins'].update(Plugins=d['Plugins']['Plugins'][1:]))
        with self.assertRaises(ValueError):
            self.check()

    def test_settings_must_persist_and_unrelated_values_must_survive(self):
        settings = self.profile / 'settings.json'
        self.edit(settings, lambda d: d.update(VideoVolume=31))
        with self.assertRaisesRegex(ValueError, 'lacks the written values'):
            self.check()
        self.edit(settings, lambda d: d.update(VideoVolume=47, DownloadPath='other'))
        with self.assertRaisesRegex(ValueError, 'unrelated'):
            self.check()

    def test_download_state_and_archive_data_must_not_move(self):
        self.edit(self.profile / 'saved_urls.json', lambda d: d.update(ThreadUrls=[]))
        with self.assertRaisesRegex(ValueError, 'saved_urls.json changed'):
            self.check()
        self.tearDown()
        self.setUp()
        self.edit(self.writes / 'readback-1.json', lambda d: d['Observed'].update(Threads=[9]))
        with self.assertRaisesRegex(ValueError, 'Threads differs'):
            self.check()

    def test_tag_state_must_be_readable_after_restart(self):
        self.edit(self.writes / 'readback-2.json', lambda d: d['HostTags'].update(LocalAssignments=0))
        with self.assertRaisesRegex(ValueError, 'differs from what was written'):
            self.check()

    def test_network_attempt_or_foreign_revision_fails(self):
        self.edit(self.writes / 'readback-1.json', lambda d: d.update(NetworkRequests=['GET x']))
        with self.assertRaisesRegex(ValueError, 'network'):
            self.check()
        self.tearDown()
        self.setUp()
        self.edit(self.writes / 'write-host.json', lambda d: d.update(CurrentVersion='1.1.0+' + 'f' * 40))
        with self.assertRaisesRegex(ValueError, 'built as'):
            self.check()

    def test_danbooru_file_without_a_danbooru_write_fails(self):
        (self.profile / 'booru-catalog.json').write_text('{}')
        with self.assertRaisesRegex(ValueError, 'Danbooru'):
            self.check()


if __name__ == '__main__':
    unittest.main()
