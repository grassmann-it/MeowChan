"""Full-sweep orchestration fixtures; no product, desktop or network access."""
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runner = load("sweep_runner", "scripts/run-desktop-smoke.py")
gates = load("sweep_gates", "scripts/aggregate-release-gates.py")


class FullSweepTests(unittest.TestCase):
    def test_modes_continue_after_fake_failure_and_stay_red(self):
        with tempfile.TemporaryDirectory() as tmp:
            assembly = Path(tmp) / "fake.dll"
            assembly.touch()
            output = Path(tmp) / "output"
            def fake(mode, *args):
                return dict(mode=mode, passed=mode != "normal", actual_render_scaling=1)
            with patch.object(runner.shutil, "which", return_value="fake"), \
                    patch.object(runner, "run_mode", side_effect=fake) as run:
                self.assertEqual(1, runner.main(["--assembly", str(assembly), "--output", str(output)]))
            report = json.loads((output / "results.json").read_text())
            self.assertFalse(report["passed"])
            self.assertEqual(run.call_count, len(report["selected_modes"]))
            self.assertFalse(report["results"][0]["passed"])
            self.assertTrue(all(row["passed"] for row in report["results"][1:]))

    def test_cleanup_failure_and_interruption_still_block_unsafe_followups(self):
        for failure in (dict(error="Process cleanup failed: fixture"), dict(interrupted=True)):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
                assembly = Path(tmp) / "fake.dll"
                assembly.touch()
                output = Path(tmp) / "output"
                with patch.object(runner.shutil, "which", return_value="fake"), \
                        patch.object(runner, "run_mode", return_value=dict(
                            mode="normal", passed=False, actual_render_scaling=1, **failure)) as run:
                    self.assertEqual(1, runner.main(["--assembly", str(assembly), "--output", str(output)]))
                self.assertEqual(run.call_count, 1)
                report = json.loads((output / "results.json").read_text())
                self.assertTrue(all(row["status"] == "skipped" for row in report["results"][1:]))
                self.assertFalse(report["passed"])

    def test_registry_launch_failure_and_timeout_do_not_abort_next_scene(self):
        for failure in (OSError("fake scene launch denied"), subprocess.TimeoutExpired("fake", 1)):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
                assembly = Path(tmp) / "fake.dll"
                assembly.touch()
                entries = [dict(id=name, mode="normal", scenario="normal+" + name,
                                environment=[], error=None, skip=None) for name in ("red", "green")]
                args = SimpleNamespace(assembly=str(assembly), output=str(Path(tmp) / "output"),
                                       only=[], timeout=1, window_manager="openbox")
                with patch.object(runner, "registry_entries", return_value=entries), \
                        patch.object(runner, "registry_command", return_value=["fake"]), \
                        patch.object(runner.subprocess, "run", side_effect=[failure,
                            SimpleNamespace(returncode=0, stdout="", stderr="")]) as run, \
                        patch.object(runner, "child_result", return_value=dict(child_passed=True)):
                    self.assertEqual(1, runner.run_registry(args))
                report = json.loads((Path(args.output) / "registry.json").read_text())
                self.assertEqual(run.call_count, 2)
                self.assertEqual(report["failed"], ["red"])
                self.assertEqual(report["counts"]["passed"], 1)
                self.assertTrue(report["complete"])

    def test_continue_on_error_conclusion_never_masks_original_failure(self):
        report = gates.step_gate({"red": dict(outcome="failure", conclusion="success"),
                                  "green": dict(outcome="success")}, ["red", "green"])
        self.assertFalse(report["passed"])
        self.assertEqual(report["failures"], dict(red="failure"))

    def test_rollup_allows_only_mode_excluded_jobs_not_failure_or_missing(self):
        for result, passed in (("success", True), ("skipped", True), ("failure", False),
                               ("cancelled", False), (None, False)):
            with self.subTest(result=result):
                report = gates.needs_gate({"job": dict(result=result)}, ["job"], allow_skipped=True)
                self.assertEqual(report["passed"], passed)
        self.assertFalse(gates.needs_gate({}, ["missing"], allow_skipped=True)["passed"])
        self.assertFalse(gates.needs_gate({"job": dict(result="skipped")}, ["job"])["passed"])

    def test_final_cli_status_and_summary_report_all_failures_together(self):
        with tempfile.TemporaryDirectory() as tmp:
            summary = Path(tmp) / "summary.md"
            report = Path(tmp) / "report.json"
            payload = dict(a=dict(result="failure"), b=dict(result="cancelled"), c=dict(result="success"))
            with patch.dict(os.environ, SWEEP_NEEDS=json.dumps(payload), GITHUB_STEP_SUMMARY=str(summary)):
                self.assertEqual(1, gates.main(["needs", "--json-env", "SWEEP_NEEDS", "--required", "a,b,c",
                                               "--report", str(report)]))
            self.assertEqual(json.loads(report.read_text())["failures"], dict(a="failure", b="cancelled"))
            text = summary.read_text()
            self.assertIn("RED", text)
            self.assertIn('"a": "failure"', text)
            self.assertIn('"b": "cancelled"', text)
            self.assertIn('"c": "success"', text)

    def test_workflow_rollup_waits_for_every_other_job(self):
        workflow = (ROOT / ".github/workflows/dotnet-desktop.yml").read_text()
        jobs = set(re.findall(r"^  ([\w-]+):$", workflow.split("\njobs:\n", 1)[1], re.M))
        aggregate = workflow.split("  full-sweep-aggregate:\n", 1)[1].split("\n  # Diagnostic", 1)[0]
        needs = set(re.search(r"needs: \[([^\]]+)\]", aggregate)[1].replace(" ", "").split(","))
        self.assertEqual(needs, jobs - {"full-sweep-aggregate"})
        self.assertIn("if: always()", aggregate)
        self.assertNotIn("continue-on-error", aggregate)
        self.assertEqual(len(re.findall(r"^    strategy:$", workflow, re.M)),
                         len(re.findall(r"^      fail-fast: false$", workflow, re.M)))

    def test_single_smoke_guard_rejects_empty_multiple_and_shell_inputs(self):
        workflow = (ROOT / ".github/workflows/desktop-smoke-registry.yml").read_text()
        guard = workflow.split("          python3 - <<'PY'\n", 1)[1].split("          PY\n", 1)[0]
        guard = "\n".join(line[10:] for line in guard.splitlines())
        for value in ("", "reader-navigation reader-stability", "$(touch injected)", "--registry", "reader-navigation\n"):
            with self.subTest(value=value), patch.dict(os.environ, ONLY=value), self.assertRaises(SystemExit):
                exec(guard, {})
        with patch.dict(os.environ, ONLY="reader-navigation"):
            exec(guard, {})

    def test_existing_single_smoke_and_single_shard_dispatch_are_preserved(self):
        registry = (ROOT / ".github/workflows/desktop-smoke-registry.yml").read_text()
        desktop = (ROOT / ".github/workflows/dotnet-desktop.yml").read_text()
        self.assertIn("      only:\n", registry)
        self.assertIn("ONLY: ${{ inputs.only }}", registry)
        self.assertIn("group: desktop-smoke-registry-${{ github.ref }}-${{ inputs.only || 'all' }}", registry)
        self.assertIn("inputs.mode == 'smoke-retest' && inputs.smoke", desktop)
        self.assertIn('only+=(--only "$id")', registry)
        self.assertIn("      offline_lane:\n", desktop)
        self.assertIn("- offline-retest", desktop)
        self.assertIn("  aggregate:\n", registry)
        self.assertIn("--required plugin_delivery,runner_contract,registry_smokes", registry)
        self.assertNotIn("continue-on-error: true\n    runs-on", registry)


if __name__ == "__main__":
    unittest.main()
