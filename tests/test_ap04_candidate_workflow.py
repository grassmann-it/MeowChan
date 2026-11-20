"""AP04 workflow wiring only: no runner tests, native execution or builds."""
import importlib.util
from pathlib import Path
import re
import subprocess
import unittest

from workflow_shell import workflow_bash

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = (ROOT/'.github/workflows/dotnet-desktop.yml').read_text()
JOBS = dict(re.findall(r'^  ([a-z][a-z0-9-]+):\n(.*?)(?=^  [a-z][a-z0-9-]+:|\Z)',
                       WORKFLOW.split('\njobs:\n', 1)[1], re.M | re.S))
SPEC = importlib.util.spec_from_file_location('ap04_workflow_gate', ROOT/'scripts/aggregate-release-gates.py')
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


def dependencies(job):
    return re.search(r'^    needs: \[(.+)\]$', JOBS[job], re.M)[1].split(', ')


def blocks(job):
    return dict(re.findall(r'^      - name: ([^\n]+)\n(.*?)(?=^      - |\Z)', JOBS[job], re.M | re.S))


def check_shell_syntax(script):
    shell = workflow_bash()
    # subprocess text-mode stdin becomes CRLF on Windows. Bash must parse the
    # actual LF workflow script, not a host-newline-translated variant.
    result = subprocess.run([shell, "--noprofile", "--norc", "-n"],
                            input=script.encode("utf-8"), capture_output=True, timeout=30)
    if result.returncode:
        raise AssertionError(f"Bash syntax check failed: shell={shell!r}, exit={result.returncode}"
                             f"\nstdout: {result.stdout.decode('utf-8', errors='replace')}"
                             f"\nstderr: {result.stderr.decode('utf-8', errors='replace')}")


class AP04CandidateWorkflowTests(unittest.TestCase):
    def test_independent_job_has_no_inventory_cycle_and_is_required_for_release(self):
        self.assertEqual(['current-source-inventory', 'linux-distribution'], dependencies('ap04-upgrade'))
        self.assertNotIn('ap04-upgrade', dependencies('current-source-inventory'))
        self.assertEqual(['validate', 'library-package', 'linux-distribution', 'macos-source-candidate'],
                         dependencies('current-source-inventory'))
        expected = ['validate', 'macos-offline-gate', 'desktop-offline-gate', 'library-package', 'macos-package',
                    'package-media', 'linux-distribution', 'macos-source-candidate', 'current-source-inventory', 'ap04-upgrade']
        self.assertEqual(expected, dependencies('release-candidate-gate'))
        required = re.search(r'--required ([a-z0-9,-]+)', JOBS['release-candidate-gate'])[1].split(',')
        self.assertEqual(expected, required)
        ok = {name: dict(result='success') for name in expected}
        self.assertTrue(GATE.needs_gate(ok, required)['passed'])
        for status in ('failure', 'skipped', 'cancelled', 'missing'):
            values = dict(ok)
            if status == 'missing': values.pop('ap04-upgrade')
            else: values['ap04-upgrade'] = dict(result=status)
            self.assertFalse(GATE.needs_gate(values, required)['passed'])

    def test_candidate_all_all_only_ubuntu_x64_and_bounded_budget(self):
        from tests import test_prerelease as prerelease
        # Reuse the existing workflow expression interpreter, not YAML's ambiguous "on" key.
        cls = next(cls for cls in vars(prerelease).values() if isinstance(cls, type) and hasattr(cls, 'condition') and hasattr(cls, 'evaluate'))
        cls.setUpClass(); contract = cls()
        for mode in ('prerelease-candidate', 'stable-candidate'):
            self.assertTrue(contract.condition('ap04-upgrade', mode))
            for scenario in ('selection', 'gui-only', 'tray-lifecycle', 'gallery-errors', 'media-input'):
                self.assertFalse(contract.condition('ap04-upgrade', mode, scenario=scenario))
            for platform in ('macos-15', 'macos-15-intel', 'windows-2022', 'ubuntu-24.04'):
                self.assertFalse(contract.condition('ap04-upgrade', mode, platform=platform))
            for event in ('push', 'pull_request'):
                self.assertFalse(contract.condition('ap04-upgrade', mode, event=event))
        for mode in ('validate', 'offline-retest', 'linux-distribution', 'package-media'):
            self.assertFalse(contract.condition('ap04-upgrade', mode))
        self.assertIn('runs-on: ubuntu-24.04\n', JOBS['ap04-upgrade'])
        self.assertIn('timeout-minutes: 60\n', JOBS['ap04-upgrade'])
        self.assertIn('timeout-minutes: 40\n', JOBS['ap04-upgrade'])
        self.assertIn('timeout-minutes: 75\n', JOBS['validate'])

    def test_inventory_rejects_failed_producers_before_download(self):
        job = JOBS['current-source-inventory']
        self.assertIn('id: current_source_inputs', job)
        self.assertIn('CURRENT_SOURCE_NEEDS: ${{ toJSON(needs) }}', job)
        self.assertIn('--required validate,library-package,linux-distribution,macos-source-candidate', job)
        self.assertIn('--report artifacts/current-source/upstream-gate.json', job)
        self.assertIn('current-source-inventory-evidence-${{ github.sha }}', job)
        self.assertIn('path: artifacts/current-source/**', job)
        self.assertIn('if-no-files-found: error', job)

    def test_exact_same_run_downloads_and_strict_package_binding(self):
        job = JOBS['ap04-upgrade']
        self.assertIn('name: candidate-binding-${{ github.sha }}', job)
        self.assertIn('name: linux-distribution-candidate-linux-x64', job)
        self.assertNotIn('run-id:', job)  # download-artifact defaults to THIS run, never a prior run.
        inventory = JOBS['current-source-inventory']
        self.assertIn('artifacts/${{ inputs.mode }}/candidate-inventory.json', inventory)
        self.assertIn('artifacts/${{ inputs.mode }}/SHA256SUMS', inventory)
        stage = blocks('ap04-upgrade')['Stage exactly the source-bound AP04 Linux package set']
        for value in ('ap04_stage.py stage', '--linux-input artifacts/ap04-linux',
                      '--inventory artifacts/ap04-binding/candidate-inventory.json',
                      '--sha256sums artifacts/ap04-binding/SHA256SUMS',
                      '--candidate-sha "$GITHUB_SHA" --run-id "$GITHUB_RUN_ID"',
                      '--output "$RUNNER_TEMP/a4/packages" --receipt artifacts/ap04-stage.json'):
            self.assertIn(value, stage)
        self.assertNotIn('--allow-no-package', job)
        self.assertNotIn('--no-netns', job)

    def test_fresh_detached_acceptance_is_separate_from_warm_restores(self):
        job = JOBS['ap04-upgrade']; steps = blocks('ap04-upgrade')
        self.assertIn('fetch-depth: 0', job)
        self.assertIn('persist-credentials: false', job)
        self.assertIn('case "$(realpath "$RUNNER_TEMP")/" in /tmp/*)', job)
        self.assertIn('AP04 SDK cannot live under /tmp', job)
        warm = steps['Warm only the separate AP04 offline feed outside acceptance checkout']
        run = steps['Run the full AP04 replay from a fresh detached same-source checkout']
        self.assertIn('git worktree add --detach "$RUNNER_TEMP/a4/warm-current" "$GITHUB_SHA"', warm)
        self.assertIn('prepare.py "$RUNNER_TEMP/a4/warm-old"', warm)
        self.assertIn('--packages "$RUNNER_TEMP/a4/feed" --locked-mode', warm)
        self.assertEqual(3, warm.count('.csproj"'))
        self.assertNotIn('dotnet build', warm)
        self.assertNotIn('dotnet publish', warm)
        self.assertIn('git worktree add --detach "$candidate" "$GITHUB_SHA"', run)
        self.assertIn('cd "$candidate"', run)
        self.assertIn('test "$(git rev-parse HEAD)" = "$GITHUB_SHA"', run)
        self.assertIn('--candidate-sha "$GITHUB_SHA" --root "$candidate"', run)
        self.assertIn('--namespace-backend bwrap-root --lock-wait 60', run)
        self.assertIn('--offline-feed "$RUNNER_TEMP/a4/feed" --heavy-lock "$RUNNER_TEMP/a4/heavy.lock"', run)
        self.assertIn('set -euo pipefail', run)
        self.assertNotIn('dotnet restore', run)
        self.assertNotIn('actions/cache', job)
        self.assertLess(job.index('id: ap04_warm'), job.index('id: ap04_run'))

    def test_failed_run_cannot_be_hidden_by_checker_or_upload_and_evidence_is_always(self):
        job = JOBS['ap04-upgrade']; steps = blocks('ap04-upgrade')
        self.assertNotIn('continue-on-error:', job)
        check = steps['Verify full AP04 PASS and same-run package/source evidence']
        self.assertIn("if: ${{ always() && steps.ap04_stage.outcome == 'success' && steps.ap04_run.outcome == 'success' }}", check)
        self.assertIn('ap04_stage.py check', check)
        self.assertIn('--stage-receipt artifacts/ap04-stage.json', check)
        self.assertIn('--run-id "$GITHUB_RUN_ID"', check)
        self.assertIn('--candidate-sha "$GITHUB_SHA"', check)
        final = steps['Reject any missing or incomplete AP04 phase']
        required = re.search(r'--required ([a-z0-9_,]+)', final)[1].split(',')
        self.assertEqual(['ap04_inputs', 'ap04_tools', 'ap04_stage', 'ap04_warm', 'ap04_run', 'ap04_check'], required)
        self.assertIn('if: always()', final)
        self.assertIn('AP04_STEPS: ${{ toJSON(steps) }}', final)
        for name in required:
            for status in ('failure', 'skipped', 'cancelled', 'missing'):
                observed = {step: dict(outcome='success') for step in required}
                if status == 'missing': observed.pop(name)
                else: observed[name] = dict(outcome=status, conclusion='success')
                self.assertFalse(GATE.step_gate(observed, required)['passed'])
        upload = steps['Retain AP04 decision and bounded attempt evidence even on failure']
        self.assertIn('if: always()', upload)
        for path in ('artifacts/ap04/**', 'artifacts/ap04-stage.json', '/a4/run/report.json',
                     '/a4/run/candidate.json', '/a4/run/evidence/**', '/a4/run/logs/**', '/a4/run/test-results/**', '/a4/run/cycles/**', '/a4/run/w/**'):
            self.assertIn(path, upload)
        self.assertIn('if-no-files-found: error', upload)

    def test_validation_step_fixture_does_not_mix_independent_job_steps(self):
        from tests.test_validation_gate import workflow_steps, job_conditions
        jobs = job_conditions()
        self.assertTrue({'validate', 'ap04-upgrade', 'release-candidate-gate', 'current-source-inventory'} <= set(jobs))
        self.assertIn("inputs.scenario == 'all'", jobs['ap04-upgrade'])
        steps = workflow_steps()
        self.assertIn('upgrade_102_replay', steps)
        for name in ('ap04_inputs', 'ap04_stage', 'ap04_run', 'ap04_check', 'metadata'):
            self.assertNotIn(name, steps)

    def test_existing_native_upgrade_steps_retained_and_new_shell_blocks_parse(self):
        self.assertIn('id: upgrade_102_replay', JOBS['validate'])
        self.assertIn('python -m unittest discover -s tests -p test_ap04_candidate_workflow.py -v || fixture_status=$?', JOBS['validate'])
        self.assertIn('check_migrated.py', JOBS['validate'])
        for name, body in blocks('ap04-upgrade').items():
            match = re.search(r'^        run: \|\n(.*)', body, re.M | re.S)
            if match:
                script = '\n'.join(line[10:] if line.startswith('          ') else line for line in match[1].splitlines())
                with self.subTest(step=name):
                    check_shell_syntax(script)


if __name__ == '__main__': unittest.main()
