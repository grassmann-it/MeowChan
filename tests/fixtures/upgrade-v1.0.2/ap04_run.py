#!/usr/bin/env python3
"""AP04 final run: upgrade from the released 1.0.2 to the frozen 1.1.0 candidate, one command.

    python3 tests/fixtures/upgrade-v1.0.2/ap04_run.py --candidate-sha <40-hex> \\
        --out /home/tomka/.cache/claude-ssd-tmp/ap4/r1 --linux-package-dir <stable-candidate linux-packages> \\
        --sha256sums <SHA256SUMS of the same stable-candidate run>

Run it from a CLEAN, FRESH detached worktree of exactly the candidate commit (never the live checkout). It
refuses to run on a dirty tree, another commit or a tree with ignored build outputs (bin/, obj/, *.dll ...:
stale binaries), and it refuses to write a PASS if the tree is dirty or the commit differs at any later
point (builds included). PASS needs the 1.1.0 package set: the Linux package manifest (source_commit = the
candidate) and the SHA256SUMS of the same stable-candidate run. `--allow-no-package` runs a source-only
diagnosis whose verdict is SOURCE-ONLY, never PASS. Every report line names the commit and the
checksums of what was run. Only a fresh temporary profile below --out is ever opened; no GUI, no network
(private network namespace for every current process), no download. `--dry-run` prints the plan only.
Waiting for heavy.lock is bounded by --lock-wait and reported apart from command timeouts.

Steps: preflight, candidate, release-artifact (optional checksum of an owner-supplied 1.0.2 download),
released-source/-build/-generate (the exact 1.0.2 code, built from the local pinned Git object, writes the
profile), current-build (+ seal of the built bytes), cycles (upgrade + two offline restarts, existing xunit
test), migrated-check, writes (settings, queue options, pause state, tag, v1 -> v2 with v1 backup), two
read-back restarts, post-check (plugins active, writes readable, v1 backup byte-identical), downgrade-notes,
final-verify. Evidence of the earlier steps is never reused: a failed attempt is kept and a new --out is used.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / 'scripts'))
import ap04_candidate as candidate_lib  # noqa: E402
import check_writes  # noqa: E402
import legacy102_process as process_runner  # noqa: E402
from legacy102_process import CommandFailed, CommandTimedOut, CommandUnavailable  # noqa: E402
RELEASE_REVISION = 'eec6c0169f8d52f7481ea0bf7febfd20981e97c3'
EXPECTED_VERSION = '1.1.0'
ALLOWED_OUT = Path('/home/tomka/.cache/claude-ssd-tmp/ap4')
LOCK = Path('/home/tomka/.cache/claude-ssd-tmp/heavy.lock')
# flock --conflict-exit-code: heavy.lock was not acquired within --lock-wait, so the command never started.
LOCK_BUSY = 75
DEFAULT_LOCK_WAIT = 3600
FEED = Path('/home/tomka/.nuget/packages')
TEST_NAME = 'ReleasedProviderProfileSurvivesCurrentOpenAndTwoOfflineProcessRestarts'
TEST_FULL_NAME = 'MeowChan.DesktopTests.Released102RestPackageUpgradeTests.' + TEST_NAME
MIN_FREE_BYTES = 6 * 1024**3
# A display or desktop session of the person running this must never reach a child.
DROPPED = ('DISPLAY', 'WAYLAND_DISPLAY', 'XAUTHORITY', 'DBUS_SESSION_BUS_ADDRESS', 'SESSION_MANAGER', 'XDG_RUNTIME_DIR',
           'MEOWCHAN_PROFILE', 'OCHAN_PROFILE', 'DOTNET_HOST_PATH')
REAL_PROFILES = ('.oChan', '.ochan', '.meowchan', '.MeowChan')
REQUIRED_NOTES = (
    ('no supported downgrade', 'There is no supported downgrade'),
    ('v1 backup file named', 'private-tags.v1.bak'),
    ('way back', 'restore `private-tags.v1.bak` as `private-tags.json`'),
    ('old build refuses archive', 'A 1.0.2 build refuses it as an unsupported schema'),
    ('full backup advice', 'back up your entire profile and media folders'),
)
STEP_NAMES = ('preflight', 'candidate', 'release-artifact', 'released-source', 'released-build', 'released-generate',
              'current-build', 'cycles', 'migrated-check', 'writes', 'readbacks', 'post-check', 'downgrade-notes')


class Context:
    def __init__(self, args):
        self.root = Path(args.root).resolve()
        self.sha = args.candidate_sha
        self.out = Path(args.out).absolute()
        self.args = args
        self.netns = not args.no_netns
        self.lock_wait = args.lock_wait
        self.fix = self.root / 'tests/fixtures/upgrade-v1.0.2'
        self.src, self.profile = self.out / 'rel', self.out / 'p'
        self.results, self.writes, self.logs = self.out / 'cycles', self.out / 'w', self.out / 'logs'
        self.evidence = self.out / 'evidence'
        self.consumer_dir = self.fix / 'current/bin/Debug/net10.0'
        self.consumer_dll = self.consumer_dir / 'MeowChan.DesktopTests.dll'
        self.test_project = 'tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj'
        self.consumer_project = 'tests/fixtures/upgrade-v1.0.2/current/UpgradeConsumer.csproj'
        self.generator_project = self.src / 'tests/Upgrade102FixtureGenerator/UpgradeFixtureGenerator.csproj'
        self.generator_dll = self.src / 'tests/Upgrade102FixtureGenerator/bin/Debug/net10.0/MeowChan.DesktopTests.dll'
        self.nuget_config = self.out / 'offline.nuget.config'
        self.candidate_path = self.out / 'candidate.json'
        self.candidate = None

    def env(self):
        env = {k: v for k, v in os.environ.items() if k not in DROPPED}
        env.update(HOME=str(self.out / 'home'), TMPDIR=str(self.out / 'tmp'), DOTNET_CLI_HOME=str(self.out / 'dotnet-cli'),
                   XDG_CONFIG_HOME=str(self.out / 'home/.config'), XDG_STATE_HOME=str(self.out / 'home/.local/state'),
                   XDG_CACHE_HOME=str(self.out / 'home/.cache'), NUGET_PACKAGES=str(FEED), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                   DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', MEOWCHAN_DISABLE_UPDATE_CHECK='1')
        return env

    def heavy(self, argv):
        return ['flock', '--close', '--wait', str(self.lock_wait), '--conflict-exit-code', str(LOCK_BUSY), str(LOCK),
                'nice', '-n', '19', *argv]

    def isolated(self, argv):
        return ['unshare', '-Urn', '--', *argv] if self.netns else list(argv)

    def dotnet(self, verb, project, *extra, timeout=900):
        return self.heavy(['timeout', f'{timeout}s', 'dotnet', verb, str(project), *extra])

    def restore(self, project):
        return self.dotnet('restore', project, '--configfile', str(self.nuget_config), '--source', str(FEED),
                           '--locked-mode', '-p:NuGetAudit=false', '-p:Configuration=Debug',
                           '-p:UseSharedCompilation=false', '-m:1', '-nodeReuse:false')

    def build(self, project, revision):
        return self.dotnet('build', project, '--no-restore', '--configuration', 'Debug', f'-p:SourceRevisionId={revision}',
                           '-p:UseSharedCompilation=false', '-m:1', '-nodeReuse:false')

    def test_command(self):
        return self.heavy(['timeout', '1200s', 'dotnet', 'test', self.test_project, '--no-build', '--no-restore', '--configuration', 'Debug',
                           '--filter', 'FullyQualifiedName~MeowChan.DesktopTests.Released102RestPackageUpgradeTests',
                           '--results-directory', str(self.out / 'test-results'), '--logger', 'trx;LogFileName=ap04.trx',
                           '-p:UseSharedCompilation=false', '-m:1', '-nodeReuse:false'])

    def test_env(self):
        env = self.env()
        env.update(MEOWCHAN_RELEASED102_PROFILE=str(self.profile),
                   MEOWCHAN_RELEASED102_SOURCE_MANIFEST=str(self.src / 'UPGRADE-102-SOURCE.json'),
                   MEOWCHAN_RELEASED102_CONSUMER_DLL=str(self.consumer_dll), MEOWCHAN_RELEASED102_RESULTS=str(self.results))
        if self.netns:
            env['MEOWCHAN_RELEASED102_NETWORK_NAMESPACE'] = '1'
        return env

    def consumer_command(self, result, mode):
        # Consumer starts are native smoke processes.  They are serialized just like restore,
        # build and test; otherwise a write/read-back could race a separate heavy build.
        return self.heavy(self.isolated(['dotnet', str(self.consumer_dll), str(self.profile), str(result), mode]))

    def tokens(self):
        if self.candidate:
            return candidate_lib.tokens(self.candidate)
        return {'commit': self.sha, 'bin': 'unsealed', 'pkg': 'none'}


class StepFailed(Exception):
    pass


def require(value, message):
    if not value:
        raise StepFailed(message)


def _finalize_run_log(log, argv, outcome):
    """Keep the command header, bounded helper tail, and a deterministic outcome line.

    ``run_bounded_command`` has already limited the captured output in its
    file. Reading that file here is bounded too, and avoids expanding arbitrary child bytes
    through a Unicode re-encode while restoring AP04's historical log shape.
    """
    try:
        captured = log.read_bytes() if log.exists() else b''
        with log.open('wb') as stream:
            stream.write(('COMMAND ' + shlex.join(map(str, argv)) + '\n').encode('utf-8'))
            stream.write(captured)
            stream.write(('\n' + outcome.rstrip('\n') + '\n').encode('utf-8'))
        return log.read_text(encoding='utf-8', errors='replace')
    except OSError:
        raise


def _best_effort_log(log, argv, outcome):
    try:
        _finalize_run_log(log, argv, outcome)
    except BaseException:
        # Preserve KeyboardInterrupt/BaseException and the helper's cleanup
        # result even if the diagnostic log itself cannot be finalized.
        pass


def run(ctx, name, argv, env=None, cwd=None, timeout=1500, check=True):
    """Run one owned process group with a separate heavy.lock allowance.

    ``flock --wait`` is part of the owned process group.  Its lock wait is
    added to the command budget so exit 75 remains a distinct lock-busy result,
    while the helper kills flock and all descendants on command timeout or any
    exceptional interruption.  Output/logs retain only the helper's bounded
    tail; required first-line scanners fail closed if that tail no longer
    contains their sentinel.
    """
    ctx.logs.mkdir(parents=True, exist_ok=True)
    log = ctx.logs / f'{name}.log'
    arguments = list(map(str, argv))
    locked = bool(arguments) and arguments[0] == 'flock'
    budget = timeout + (ctx.lock_wait if locked else 0)
    environment = env or ctx.env()
    working_directory = Path(cwd or ctx.root)
    try:
        process_runner.run_bounded_command(
            arguments,
            working_directory,
            environment,
            log,
            budget,
            name,
        )
    except CommandTimedOut:
        _finalize_run_log(log, arguments, f'TIMEOUT after {budget}s')
        raise StepFailed(f'{name}: timeout after {budget}s (see {log})') from None
    except CommandFailed as error:
        text = _finalize_run_log(log, arguments, f'EXIT {error.returncode}')
        if locked and error.returncode == LOCK_BUSY:
            raise StepFailed(f'{name}: {LOCK.name} not acquired within {ctx.lock_wait}s (another build holds it); '
                             f'nothing ran, retry with a new --out (see {log})')
        if check:
            raise StepFailed(f'{name}: exit {error.returncode} (see {log})')
        return text
    except CommandUnavailable as error:
        _finalize_run_log(log, arguments, f'START-FAILED {error}')
        raise OSError(f'{name}: {error}') from error
    except BaseException as error:
        _best_effort_log(log, arguments, f'ABORTED {type(error).__name__}')
        raise
    return _finalize_run_log(log, arguments, 'EXIT 0')


def first_line(text, prefix):
    for line in text.splitlines():
        if line.startswith(prefix):
            return line.strip()
    raise StepFailed('expected output line starting with ' + repr(prefix))


def guard_out(out):
    """Fresh path below the allowed temp root; never a real profile and never anything that exists."""
    resolved = Path(os.path.abspath(out))
    require(not resolved.exists(), f'--out exists (a failed attempt is kept, use a new one): {resolved}')
    # A lexical prefix is not enough: an existing symlink component could redirect all evidence
    # outside the bounded AP04 root while the path still appears to be below it.
    allowed = ALLOWED_OUT.resolve()
    parent = resolved.parent.resolve()
    require(parent == allowed or allowed in parent.parents, f'--out must be below {ALLOWED_OUT}: {resolved}')
    home = Path.home()
    for name in REAL_PROFILES:
        real = home / name
        require(real != resolved and real not in resolved.parents and resolved not in real.parents, 'refusing a real profile path: ' + str(real))
    require(len(str(resolved)) <= 64, 'use a short --out path (sockets and package paths): ' + str(resolved))


def require_runner_in_candidate(root):
    """The code that judges the run (this runner, ap04_candidate, check_writes) must be the candidate's own files.

    `--root` may only name the worktree this script lives in: with another root the report would carry the
    candidate SHA while the helpers imported here come from a different (possibly dirty or older) checkout."""
    runner_root = Path(ROOT).resolve()
    require(Path(root).resolve() == runner_root,
            f'--root {root} is not the checkout of this runner ({runner_root}): run ap04_run.py from the detached candidate worktree itself')


# 1.0.2 release assets are named MeowChan-1.0.2-<platform>... (the published AppImage carries a numeric prefix).
RELEASE_ASSET_NAME = re.compile(r'(?:[0-9]+-)?MeowChan-1\.0\.2-[A-Za-z0-9._-]+')


# ---- steps -------------------------------------------------------------------------------------------------

def step_preflight(ctx):
    guard_out(ctx.out)
    for tool in ('git', 'python3', 'dotnet', 'flock', 'nice', 'timeout') + (('unshare',) if ctx.netns else ()):
        require(shutil.which(tool), 'missing tool: ' + tool)
    if ctx.netns:
        probe = subprocess.run(['unshare', '-Urn', '--', 'true'], capture_output=True)
        require(probe.returncode == 0, 'unshare -Urn is not available; use --no-netns only if the owner accepts handler-only blocking')
    require(FEED.is_dir(), f'offline package feed missing: {FEED}')
    ALLOWED_OUT.mkdir(parents=True, exist_ok=True)
    require(shutil.disk_usage(ALLOWED_OUT).free >= MIN_FREE_BYTES, 'less than 6 GiB free for the build and the profile')
    require(candidate_lib.product_version(ctx.root) == EXPECTED_VERSION, f'product version is not {EXPECTED_VERSION}')
    state = candidate_lib.require_frozen(ctx.root, ctx.sha)
    candidate_lib.require_fresh_outputs(ctx.root)
    require_runner_in_candidate(ctx.root)
    require(state['branch'] is None, 'the candidate must be a fresh detached worktree (use git worktree add --detach)')
    commit = subprocess.run(['git', 'cat-file', '-e', RELEASE_REVISION + '^{commit}'], cwd=ctx.root).returncode == 0
    require(commit, 'pinned 1.0.2 commit is not available as a local Git object (no fetch is attempted)')
    tag = subprocess.run(['git', 'rev-parse', '--verify', '-q', 'v1.0.2^{commit}'], cwd=ctx.root, capture_output=True, text=True)
    note = 'tag-v1.0.2=same' if tag.stdout.strip() == RELEASE_REVISION else 'tag-v1.0.2=not-local'
    for path in (ctx.out, ctx.out / 'home', ctx.out / 'tmp', ctx.out / 'dotnet-cli', ctx.logs, ctx.evidence):
        path.mkdir(parents=True, exist_ok=True)
    capture_status(ctx, 'status-before.txt')
    ctx.nuget_config.write_text('<configuration><packageSources><clear /><add key="local-cache" value="' + str(FEED)
                                + '" /></packageSources></configuration>\n', encoding='utf-8')
    return (f"commit={ctx.sha} clean=yes fresh=yes version={EXPECTED_VERSION} "
            f"network={'namespace' if ctx.netns else 'handler-only'} lock-wait={ctx.lock_wait}s {note}")


def step_candidate(ctx):
    ctx.candidate = candidate_lib.identity(ctx.root, ctx.sha, ctx.args.linux_package_dir, ctx.args.rid, ctx.args.sha256sums)
    ctx.candidate_path.write_text(json.dumps(ctx.candidate, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    package = ctx.candidate['package']
    if package is None:
        require(ctx.args.allow_no_package, 'no --linux-package-dir/--sha256sums: the 1.1.0 package set is part of the candidate '
                '(--allow-no-package runs a source-only diagnosis that can never PASS)')
        return 'package=none (source-only diagnosis: the verdict cannot be PASS)'
    require(package['sha256sums_checked'], 'the package set needs --sha256sums of the same stable-candidate run')
    return ('package=' + ','.join(f"{a['flavor']}:{a['sha256'][:16]}" for a in package['artifacts'])
            + f" manifest={package['manifest_sha256'][:16]} sha256sums={package['sha256sums_sha256'][:16]}")


def step_release_artifact(ctx):
    if not ctx.args.release_artifact:
        return 'real-artifact=not supplied (release code is the pinned Git object eec6c016, built locally)'
    require(ctx.args.release_sums, '--release-artifact needs --release-sums (the published 1.0.2 SHA256SUMS)')
    # Same fail-closed rule as the package root: no symlink component, single-link regular files only.
    artifact = candidate_lib.ordinary_path(ctx.args.release_artifact, '--release-artifact')
    sums = candidate_lib.ordinary_path(ctx.args.release_sums, '--release-sums')
    require(RELEASE_ASSET_NAME.fullmatch(artifact.name),
            f'{artifact.name} is not a 1.0.2 release asset name (MeowChan-1.0.2-...): a 1.1.0 or foreign file is not 1.0.2 evidence')
    for path in (artifact, sums):
        require(ctx.root != path and ctx.root not in path.parents, f'{path} lies inside the candidate source root: source and artifact roots must stay apart')
    package = (ctx.candidate or {}).get('package')
    if package:
        package_dir = Path(package['directory'])
        require(package_dir not in artifact.parents and package_dir not in sums.parents,
                f'{artifact.name}: the 1.0.2 inputs must not come from the 1.1.0 package root {package_dir}')
        require(not (package.get('sha256sums') and os.path.samefile(sums, package['sha256sums'])),
                '--release-sums is the SHA256SUMS of the 1.1.0 candidate, not of the published 1.0.2 release')
        require(artifact.name not in {a['file'] for a in package['artifacts']}
                and candidate_lib.digest_file(artifact) not in {a['sha256'] for a in package['artifacts']},
                f'{artifact.name} is a 1.1.0 candidate package, not a published 1.0.2 asset')
    listed = candidate_lib.parse_sha256sums(sums).get(artifact.name)
    actual = candidate_lib.digest_file(artifact)
    require(listed is not None, f'{artifact.name} is not listed in {sums.name}')
    require(listed == actual, f'{artifact.name} does not match its published checksum')
    (ctx.evidence / 'release-artifact.json').write_text(json.dumps({'file': artifact.name, 'sha256': actual,
                                                                    'sha256sums_sha256': candidate_lib.digest_file(sums),
                                                                    'executed': False}, indent=2) + '\n', encoding='utf-8')
    return f'real-artifact={artifact.name} sha256={actual[:16]} matches SHA256SUMS (not executed: release code is the pinned source build)'


def step_released_source(ctx):
    py = ['python3', '-I']
    out = run(ctx, 'released-prepare', ['nice', '-n', '19', 'timeout', '180s', *py, ctx.fix / 'prepare.py', ctx.src], timeout=240)
    first_line(out, 'SYNTHETIC SOURCE EXPORTED')
    text = run(ctx, 'released-source-check', [*py, ctx.fix / 'check.py', '--source', ctx.src], timeout=120)
    line = first_line(text, 'SOURCE OK')
    require(f'revision={RELEASE_REVISION}' in line, 'source export is not the pinned 1.0.2 revision')
    return line


def step_released_build(ctx):
    run(ctx, 'released-restore', ctx.restore(ctx.generator_project), timeout=1000)
    run(ctx, 'released-build', ctx.build(ctx.generator_project, RELEASE_REVISION), timeout=1000)
    require(ctx.generator_dll.is_file(), 'generator build output missing')
    return 'generator=' + candidate_lib.digest_file(ctx.generator_dll)[:16] + f' stamped=1.0.2+{RELEASE_REVISION[:8]}'


def step_released_generate(ctx):
    run(ctx, 'released-generate', ctx.heavy(ctx.isolated(
        ['dotnet', ctx.generator_dll, ctx.profile, ctx.src / 'UPGRADE-102-SOURCE.json'])), timeout=200)
    text = run(ctx, 'released-receipt-check', ['python3', '-I', ctx.fix / 'check.py', '--source', ctx.src, '--profile', ctx.profile], timeout=120)
    line = first_line(text, 'RECEIPT OK')
    shutil.copy2(ctx.profile / 'v102-receipt.json', ctx.evidence / 'released-receipt.json')
    shutil.copy2(ctx.src / 'UPGRADE-102-SOURCE.json', ctx.evidence / 'released-source-manifest.json')
    return line


def step_current_build(ctx):
    # Every file sealed and executed below must come from this build of the candidate, not from an earlier one.
    candidate_lib.require_fresh_outputs(ctx.root)
    for project in (ctx.consumer_project, ctx.test_project):
        name = Path(project).stem
        run(ctx, f'{name}-restore', ctx.restore(project), timeout=1500)
        run(ctx, f'{name}-build', ctx.build(project, ctx.sha), timeout=2400)
    require(ctx.consumer_dll.is_file(), 'consumer build output missing')
    sealed = candidate_lib.seal(ctx.candidate, 'consumer', ctx.consumer_dir)
    candidate_lib.seal(ctx.candidate, 'tests', ctx.root / 'tests/MeowChan.DesktopTests/bin/Debug/net10.0')
    ctx.candidate_path.write_text(json.dumps(ctx.candidate, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    problems = candidate_lib.verify(ctx.root, ctx.candidate)
    require(not problems, 'the build changed the candidate: ' + '; '.join(problems))
    files = ctx.candidate['trees']['consumer']['files']
    return f"consumer-bin={sealed[:16]} files={len(files)} MeowChan.dll={files['MeowChan.dll'][:16]} libmeowchan.dll={files['libmeowchan.dll'][:16]}"


def trx_outcome(path):
    outcomes = {}
    for element in ET.parse(path).getroot().iter():
        if element.tag.endswith('UnitTestResult'):
            name = element.get('testName')
            require(name and name not in outcomes, f'duplicate TRX result: {name or "<unnamed>"}')
            outcomes[name] = element.get('outcome')
    return outcomes


def trx_upgrade_outcome(outcomes):
    """Accept the two exact adapter names for the upgrade fact, never a suffix.

    VSTest/xUnit emits the fully-qualified name; older adapter fixtures use
    the bare method name. A result in another class is not upgrade evidence,
    and receiving both aliases is ambiguous even if both report Passed.
    """
    matches = [name for name in (TEST_FULL_NAME, TEST_NAME) if name in outcomes]
    require(len(matches) <= 1, 'ambiguous upgrade TRX results: ' + ', '.join(matches))
    return outcomes[matches[0]] if matches else None


def step_cycles(ctx):
    # Passing TRX files can precede a testhost/teardown failure. The process must
    # also succeed; an earlier failed step must never become green from its files.
    text = run(ctx, 'cycles-test', ctx.test_command(), env=ctx.test_env(), timeout=1500)
    trx = ctx.out / 'test-results/ap04.trx'
    require(trx.is_file(), 'no TRX produced (see logs/cycles-test.log)')
    outcomes = trx_outcome(trx)
    upgrade = trx_upgrade_outcome(outcomes)
    require(upgrade == 'Passed', f'the upgrade test did not pass or was skipped: {upgrade or "absent"}')
    require(all(v == 'Passed' for v in outcomes.values()), 'a test of the class failed: ' + ', '.join(k for k, v in outcomes.items() if v != 'Passed'))
    for name in ('current-result.json', 'cycle-0.json', 'cycle-1.json', 'cycle-2.json', 'released-receipt.json'):
        require((ctx.results / name).is_file(), 'missing ' + name)
    cycles = [json.loads((ctx.results / f'cycle-{index}.json').read_bytes()) for index in range(3)]
    require(all(cycle.get('Mode') == 'observe' for cycle in cycles), 'cycle result is not an observe restart')
    require(all(cycle.get('CurrentVersion') == f'{EXPECTED_VERSION}+{ctx.sha}' for cycle in cycles),
            'a restart result is not stamped with the candidate revision')
    require(len({cycle.get('Profile') for cycle in cycles}) == 1, 'restart results do not use one profile')
    summary = [line for line in text.splitlines() if line.startswith('Passed!')]
    return f"tests={len(outcomes)} upgrade=Passed cycles=3 {summary[0] if summary else ''}".strip()


def capture_status(ctx, name):
    """`git status --porcelain=v1` of the candidate worktree, kept as evidence; check_migrated.py demands them empty."""
    path = ctx.evidence / name
    path.write_bytes(subprocess.run(['git', 'status', '--porcelain=v1', '--untracked-files=all'], cwd=ctx.root, check=True,
                                    capture_output=True).stdout)
    return path


def step_migrated_check(ctx):
    statuses = [capture_status(ctx, 'status-after-test.txt'), ctx.evidence / 'status-before.txt']
    text = run(ctx, 'migrated-check', ['python3', '-I', ctx.fix / 'check_migrated.py', '--profile', ctx.profile, '--results', ctx.results,
                                       '--expect-revision', ctx.sha, *[a for path in statuses for a in ('--status', path)]], timeout=300)
    line = first_line(text, 'MIGRATED OK')
    require(f'current={ctx.sha} revision=expected source=clean' in line, 'migrated check did not bind the revision and a clean source tree')
    for index in range(3):
        cycle = json.loads((ctx.results / f'cycle-{index}.json').read_bytes())
        require(cycle['CurrentVersion'] == f'{EXPECTED_VERSION}+{ctx.sha}', f"cycle-{index} was built as {cycle['CurrentVersion']}")
    return line


def snapshot_before(ctx):
    directory = ctx.writes / 'snapshot-before'
    directory.mkdir(parents=True, exist_ok=True)
    for name in ('settings.json', 'saved_urls.json'):
        shutil.copy2(ctx.profile / name, directory / name)


def step_writes(ctx):
    ctx.writes.mkdir(parents=True, exist_ok=True)
    snapshot_before(ctx)
    require(not (ctx.profile / 'private-tags.json').exists() and not (ctx.profile / check_writes.V1_BACKUP).exists(),
            'tag files exist before the first write')
    env = ctx.env()
    run(ctx, 'write-host', ctx.consumer_command(ctx.writes / 'write-host.json', 'write-host'), env=env, cwd=ctx.consumer_dir, timeout=300)
    tags = ctx.profile / 'private-tags.json'
    require(tags.is_file(), 'the thread tag write did not create private-tags.json')
    data = tags.read_bytes()
    require(json.loads(data)['Version'] == 1, 'the first tag write did not create a version 1 file')
    require(not (ctx.profile / check_writes.V1_BACKUP).exists(), 'a v1 backup exists before the conversion')
    snapshot = ctx.writes / 'snapshot-v1'
    snapshot.mkdir()
    (snapshot / 'private-tags.json').write_bytes(data)
    (snapshot / 'meta.json').write_text(json.dumps({'sha256': candidate_lib.digest_file(snapshot / 'private-tags.json'),
                                                    'bytes': len(data), 'v1_backup_existed_before': False}) + '\n', encoding='utf-8')
    run(ctx, 'write-local-tag', ctx.consumer_command(ctx.writes / 'write-local-tag.json', 'write-local-tag'), env=env,
        cwd=ctx.consumer_dir, timeout=300)
    backup = ctx.profile / check_writes.V1_BACKUP
    require(backup.is_file() and not backup.is_symlink(), 'the conversion did not create the permanent v1 backup')
    (ctx.writes / 'backup-observations.json').write_text(json.dumps([{
        'stage': 'after-conversion', 'bytes': backup.stat().st_size,
        'sha256': candidate_lib.digest_file(backup),
    }], sort_keys=True) + '\n', encoding='utf-8')
    return f"settings+queue+pause+thread-tag written; v1 file {len(data)} bytes sha256={candidate_lib.digest_file(snapshot / 'private-tags.json')[:16]}; conversion done"


def step_readbacks(ctx):
    observations = json.loads((ctx.writes / 'backup-observations.json').read_bytes())
    for index in (1, 2):
        run(ctx, f'readback-{index}', ctx.consumer_command(ctx.writes / f'readback-{index}.json', 'readback'), env=ctx.env(),
            cwd=ctx.consumer_dir, timeout=300)
        result = ctx.writes / f'readback-{index}.json'
        require(result.is_file(), f'missing readback-{index}.json')
        backup = ctx.profile / check_writes.V1_BACKUP
        require(backup.is_file() and not backup.is_symlink(), 'permanent v1 backup disappeared during read-back')
        observations.append({'stage': f'readback-{index}', 'bytes': backup.stat().st_size,
                             'sha256': candidate_lib.digest_file(backup)})
    (ctx.writes / 'backup-observations.json').write_text(json.dumps(observations, sort_keys=True) + '\n', encoding='utf-8')
    return 'offline restarts after the writes=2'


def step_post_check(ctx):
    lines = check_writes.check_writes(ctx.profile, ctx.results, ctx.writes, ctx.sha, EXPECTED_VERSION)
    (ctx.evidence / 'post-check.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    return ' | '.join(lines)


def step_downgrade_notes(ctx):
    text = (ctx.root / 'docs/releases/v1.1.0.md').read_text(encoding='utf-8')
    flat = ' '.join(text.split())
    missing = [label for label, phrase in REQUIRED_NOTES if ' '.join(phrase.split()) not in flat]
    require(not missing, 'docs/releases/v1.1.0.md lacks: ' + ', '.join(missing))
    # The way back, executed on bytes only: restoring the backup yields the version 1 document the tags were created with.
    restored = (ctx.profile / check_writes.V1_BACKUP).read_bytes()
    require(restored == (ctx.writes / 'snapshot-v1/private-tags.json').read_bytes() and json.loads(restored)['Version'] == 1,
            'restoring the backup would not give the original version 1 file')
    return f'notes={len(REQUIRED_NOTES)}/{len(REQUIRED_NOTES)} present; restore-of-backup=version 1 bytes'


STEPS = (('preflight', step_preflight), ('candidate', step_candidate), ('release-artifact', step_release_artifact),
         ('released-source', step_released_source), ('released-build', step_released_build),
         ('released-generate', step_released_generate), ('current-build', step_current_build), ('cycles', step_cycles),
         ('migrated-check', step_migrated_check), ('writes', step_writes), ('readbacks', step_readbacks),
         ('post-check', step_post_check), ('downgrade-notes', step_downgrade_notes))


# ---- report ------------------------------------------------------------------------------------------------

def verdict(records, final_problems, package_bound, sums_checked):
    """PASS needs every step PASS, an unchanged frozen candidate and the bound 1.1.0 package set: the Linux package
    manifest whose source_commit is the candidate AND the SHA256SUMS of the same stable-candidate run. A complete run
    without them is SOURCE-ONLY, a diagnosis and never an acceptance; --allow-no-package only permits running it."""
    if final_problems or any(r['status'] == 'FAIL' for r in records):
        return 'FAIL'
    expected = tuple(name for name, _ in STEPS)
    observed = tuple(r.get('name') for r in records)
    if observed != expected or any(r.get('status') != 'PASS' for r in records):
        return 'INCOMPLETE'
    if not (package_bound and sums_checked):
        return 'SOURCE-ONLY'
    return 'PASS'


def prefix(tokens):
    return f"AP04 candidate={tokens['commit']} bin={tokens['bin']} pkg={tokens['pkg']}"


def render(tokens, records, final_problems, result, out):
    lines = [f"{prefix(tokens)} out={out}"]
    for record in records:
        lines.append(f"{prefix(tokens)} step={record['name']} status={record['status']} seconds={record['seconds']} {record['detail']}".rstrip())
    for problem in final_problems:
        lines.append(f'{prefix(tokens)} step=final-verify status=FAIL {problem}')
    complete = tuple(record.get('name') for record in records) == tuple(name for name, _ in STEPS) \
        and all(record.get('status') == 'PASS' for record in records)
    if not final_problems and complete:
        lines.append(f'{prefix(tokens)} step=final-verify status=PASS same commit, clean worktree, built files and package root unchanged')
    elif not final_problems:
        lines.append(f'{prefix(tokens)} step=final-verify status=NOT-RUN run was incomplete; acceptance was not verified')
    if result == 'SOURCE-ONLY':
        lines.append(f'{prefix(tokens)} NOTE no 1.1.0 package manifest and SHA256SUMS bound: source-only diagnosis, NOT an AP04 acceptance')
    lines.append(f'{prefix(tokens)} VERDICT={result}')
    lines.append(f'{prefix(tokens)} SCOPE source-upgrade evidence only: synthetic profile written by the released 1.0.2 code from the pinned Git object; '
                 'DesktopSessionHost and PluginSession processes, no window; loose-file tag conversion at store level; packaged-release verification '
                 'was not executed (package root/checksums are provenance only); NOT a real-profile, UI, platform, Danbooru-network or release acceptance')
    return lines


def dry_run(ctx):
    print('AP04 DRY RUN: nothing is executed, nothing is written, no evidence is produced.')
    print(f'candidate={ctx.sha} out={ctx.out} network={"unshare -Urn" if ctx.netns else "handler-only"} lock={LOCK} '
          f'lock-wait<={ctx.lock_wait}s per heavy command')
    package = (f'package set of {ctx.args.linux_package_dir} + {ctx.args.sha256sums}' if ctx.args.linux_package_dir
               else 'NO package: source-only diagnosis, the verdict cannot be PASS')
    show = [
        ('preflight', 'git status/rev-parse, no ignored build outputs (fresh worktree), tool + `unshare -Urn true` probe, '
                      'disk, offline feed, pinned 1.0.2 object'),
        ('candidate', 'ap04_candidate.identity (commit, tree, version, ' + package + ')'),
        ('release-artifact', 'SHA-256 of --release-artifact against --release-sums (optional)'),
        ('released-source', shlex.join(['python3', '-I', str(ctx.fix / 'prepare.py'), str(ctx.src)])),
        ('released-build', shlex.join(map(str, ctx.restore(ctx.generator_project))) + '\n      ' + shlex.join(map(str, ctx.build(ctx.generator_project, RELEASE_REVISION)))),
        ('released-generate', shlex.join(map(str, ctx.heavy(ctx.isolated(
            ['dotnet', str(ctx.generator_dll), str(ctx.profile), str(ctx.src / 'UPGRADE-102-SOURCE.json')]))))),
        ('current-build', shlex.join(map(str, ctx.restore(ctx.consumer_project))) + '\n      ' + shlex.join(map(str, ctx.build(ctx.consumer_project, ctx.sha)))
         + '\n      (same for ' + ctx.test_project + ', then seal of both bin directories)'),
        ('cycles', shlex.join(map(str, ctx.test_command())) + '\n      env MEOWCHAN_RELEASED102_* -> ' + str(ctx.profile)),
        ('migrated-check', 'python3 -I check_migrated.py --profile ... --results ... --expect-revision ' + ctx.sha),
        ('writes', shlex.join(map(str, ctx.consumer_command(ctx.writes / 'write-host.json', 'write-host'))) + '\n      copy private-tags.json (version 1), then mode write-local-tag'),
        ('readbacks', shlex.join(map(str, ctx.consumer_command(ctx.writes / 'readback-N.json', 'readback'))) + '  (N = 1, 2)'),
        ('post-check', 'check_writes.check_writes (plugins active, writes readable, private-tags.v1.bak byte-identical)'),
        ('downgrade-notes', 'docs/releases/v1.1.0.md phrases + restore-of-backup on bytes'),
    ]
    for name, text in show:
        print(f'  {name}: {text}')
    print('  final-verify: ap04_candidate.verify (same commit, clean, sealed files and package unchanged), report.txt/report.json')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--candidate-sha', required=True, help='the frozen full commit id the worktree must be at')
    parser.add_argument('--out', type=Path, required=True, help=f'new directory below {ALLOWED_OUT} (short path)')
    parser.add_argument('--root', type=Path, default=ROOT, help='clean detached worktree of the candidate (default: this checkout)')
    parser.add_argument('--linux-package-dir', type=Path, help='stable-candidate `linux-packages` directory (manifest source_commit must equal the SHA)')
    parser.add_argument('--rid', default='linux-x64')
    parser.add_argument('--sha256sums', type=Path,
                        help='SHA256SUMS of the same stable-candidate run (required with --linux-package-dir; must list the same checksums)')
    parser.add_argument('--allow-no-package', action='store_true',
                        help='run without the package set as a source-only diagnosis: the verdict is then SOURCE-ONLY, never PASS')
    parser.add_argument('--lock-wait', type=int, default=DEFAULT_LOCK_WAIT,
                        help=f'seconds each restore/build/test may wait for {LOCK.name} on top of its own timeout (default {DEFAULT_LOCK_WAIT})')
    parser.add_argument('--release-artifact', type=Path, help='owner-supplied published 1.0.2 asset (checksum only)')
    parser.add_argument('--release-sums', type=Path, help='the published 1.0.2 SHA256SUMS')
    parser.add_argument('--no-netns', action='store_true', help='skip the private network namespace (handler-only blocking)')
    parser.add_argument('--dry-run', action='store_true', help='print the plan; execute and write nothing')
    args = parser.parse_args(argv)
    if not candidate_lib.SHA1.fullmatch(args.candidate_sha):
        parser.error('--candidate-sha must be a full lowercase 40-hex commit id')
    if args.lock_wait < 1:
        parser.error('--lock-wait must be at least 1 second')
    if args.allow_no_package and (args.linux_package_dir or args.sha256sums):
        parser.error('--allow-no-package contradicts --linux-package-dir/--sha256sums')
    if bool(args.release_artifact) != bool(args.release_sums):
        parser.error('--release-artifact and --release-sums belong together: the published 1.0.2 asset and its SHA256SUMS')
    if bool(args.linux_package_dir) != bool(args.sha256sums):
        parser.error('--linux-package-dir and --sha256sums belong together: the package manifest and SHA256SUMS of one stable-candidate run')
    if not args.dry_run and not args.linux_package_dir and not args.allow_no_package:
        parser.error('no 1.1.0 package set: pass --linux-package-dir and --sha256sums of the stable-candidate run, '
                     'or --allow-no-package for a source-only diagnosis that can never PASS')
    ctx = Context(args)
    if args.dry_run:
        dry_run(ctx)
        return 0
    records, stopped = [], False
    for name, function in STEPS:
        started = time.monotonic()
        if stopped:
            records.append({'name': name, 'status': 'SKIP', 'seconds': 0, 'detail': 'not run after an earlier failure'})
            continue
        try:
            detail, status = function(ctx), 'PASS'
        except (StepFailed, ValueError, OSError, KeyError, IndexError, TypeError, json.JSONDecodeError, subprocess.SubprocessError) as error:
            detail, status, stopped = 'REASON ' + str(error).replace('\n', ' '), 'FAIL', True
        records.append({'name': name, 'status': status, 'seconds': round(time.monotonic() - started), 'detail': detail})
        print(f"{name}: {status} {detail}", flush=True)
        if name == 'preflight' and status == 'FAIL':
            print('AP04 refused: nothing was created.', file=sys.stderr)
            return 2
    final_problems = []
    stopped_at = next((record['name'] for record in records if record['status'] == 'FAIL'), None)
    if ctx.candidate is not None:
        final_problems = candidate_lib.verify(ctx.root, ctx.candidate)
    else:
        try:
            candidate_lib.require_frozen(ctx.root, ctx.sha)
        except ValueError as error:
            final_problems.append(str(error))
    if stopped_at:
        final_problems.append(f'run stopped after {stopped_at}; final acceptance was not verified')
    package = ctx.candidate.get('package') if ctx.candidate else None
    result = verdict(records, final_problems, package is not None, bool(package and package.get('sha256sums_checked')))
    lines = render(ctx.tokens(), records, final_problems, result, str(ctx.out))
    (ctx.out / 'report.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    (ctx.out / 'report.json').write_text(json.dumps({'candidate': ctx.tokens(), 'verdict': result, 'steps': records,
                                                     'final_problems': final_problems,
                                                     'source_upgrade': {'synthetic_profile': True, 'released_revision': RELEASE_REVISION,
                                                                       'executed': bool(ctx.candidate and not stopped_at)},
                                                     'packaged_release': {'verification': 'not-executed',
                                                                          'artifact_root_bound': bool(package),
                                                                          'sha256sums_bound': bool(package and package.get('sha256sums_checked'))},
                                                     'finished_utc': datetime.datetime.now(datetime.timezone.utc).isoformat()}, indent=2) + '\n',
                                         encoding='utf-8')
    print('\n'.join(lines))
    return 0 if result == 'PASS' else 1


if __name__ == '__main__':
    sys.exit(main())
