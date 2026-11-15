"""Focused fail-closed tests for the GitHub candidate/release gates."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/aggregate-release-gates.py"
SPEC = importlib.util.spec_from_file_location("aggregate_release_gates", SCRIPT)
GATES = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(GATES)


class AggregationTests(unittest.TestCase):
    def test_step_failure_is_not_hidden_by_later_success(self):
        report = GATES.step_gate({
            "startup": {"outcome": "failure"},
            "packaging": {"outcome": "success"},
        }, ["startup", "packaging"])
        self.assertFalse(report["passed"])
        self.assertEqual("failure", report["failures"]["startup"])

    def test_missing_or_skipped_required_step_fails_closed(self):
        report = GATES.step_gate({"startup": {"outcome": "skipped"}}, ["startup", "packaging"])
        self.assertFalse(report["passed"])
        self.assertEqual("skipped", report["requiredNotSuccessful"]["startup"])
        self.assertEqual("missing", report["requiredNotSuccessful"]["packaging"])

    def test_optional_skipped_diagnostic_step_does_not_block_ordinary_run(self):
        report = GATES.step_gate({
            "startup": {"outcome": "success"},
            "platform_specific_diagnostic": {"outcome": "skipped"},
        }, ["startup"])
        self.assertTrue(report["passed"])

    def test_candidate_needs_rejects_failed_and_skipped_jobs(self):
        needs = {
            "validate": {"result": "failure"},
            "package-media": {"result": "skipped"},
            "current-source-inventory": {"result": "success"},
        }
        report = GATES.needs_gate(needs, ["validate", "package-media", "current-source-inventory"])
        self.assertFalse(report["passed"])
        self.assertEqual("failure", report["failures"]["validate"])
        self.assertEqual("skipped", report["failures"]["package-media"])

    def test_run_gate_requires_successful_named_jobs_and_exact_source(self):
        run = {
            "headSha": "a" * 40,
            "status": "completed",
            "conclusion": "success",
            "jobs": [
                {"name": "Release candidate gate (fail-closed)", "status": "completed", "conclusion": "success"},
                {"name": "linux-distribution (linux-x64)", "status": "completed", "conclusion": "success"},
            ],
        }
        self.assertTrue(GATES.run_gate(run, "a" * 40, ["Release candidate gate"])["passed"])
        self.assertTrue(GATES.run_gate(run, "a" * 40, ["linux-distribution"])["passed"])
        self.assertFalse(GATES.run_gate(run, "b" * 40, ["Release candidate gate"])["passed"])

    def test_run_gate_rejects_missing_gate_even_when_run_is_green(self):
        run = {"headSha": "a" * 40, "status": "completed", "conclusion": "success", "jobs": []}
        report = GATES.run_gate(run, "a" * 40, ["Release candidate gate"])
        self.assertFalse(report["passed"])
        self.assertTrue(any("missing" in failure for failure in report["failures"]))


class WorkflowContractTests(unittest.TestCase):
    workflow = (ROOT / ".github/workflows/dotnet-desktop.yml").read_text(encoding="utf-8")
    upload = (ROOT / ".github/workflows/upload-verified-release.yml").read_text(encoding="utf-8")

    def test_validation_keeps_parallel_diagnostics_and_uses_aggregator(self):
        self.assertIn("continue-on-error: true", self.workflow)
        self.assertIn("python3 scripts/aggregate-release-gates.py steps", self.workflow)
        self.assertIn("required+=(linux_setup tray_host core_tests smoke_plugins)", self.workflow)
        self.assertIn("required+=(prepare_desktop mac_tests)", self.workflow)
        gate = self.workflow.index("      - name: Fail validation after collecting all evidence")
        upload = self.workflow.index("      - name: Upload regression and graphical evidence")
        self.assertLess(gate, upload)
        self.assertIn("artifacts/build-evidence/release-validation-gate.json", self.workflow[upload:])

    def test_upgrade_gate_runs_the_released_102_fixture_and_binds_current_source(self):
        start = self.workflow.index("      - name: Exercise the released 1.0.2 fixture against the current source")
        end = self.workflow.index("      - name: Exercise FirstRunSetup", start)
        section = self.workflow[start:end]
        for marker in (
            "tests/fixtures/upgrade-v1.0.2/prepare.py",
            "tests/fixtures/upgrade-v1.0.2/check.py",
            "tests/fixtures/upgrade-v1.0.2/check_migrated.py",
            "Released102RestPackageUpgradeTests",
            "MEOWCHAN_RELEASED102_SOURCE_MANIFEST",
            "source-binding.json",
            "--expect-revision \"$current_revision\"",
            "test ! -s \"$fixture/status-after.txt\"",
        ):
            self.assertIn(marker, section)
        self.assertNotIn("upgrade-v1x", section)
        self.assertNotIn("OldReleaseExplicitNewBundledSelectionPreservesHistoryAcrossTwoOfflineRestarts", section)
        self.assertIn("required=(candidate_matrix", self.workflow)
        self.assertIn("upgrade_replay", self.workflow)

    def test_candidate_gate_waits_for_every_release_input_and_is_always_evaluated(self):
        section = self.workflow.split("  release-candidate-gate:\n", 1)[1].split(
            "\n  # Diagnostic evidence only", 1)[0]
        self.assertIn("if: ${{ always()", section)
        for job in ("validate", "library-package", "macos-package", "package-media",
                    "linux-distribution", "macos-source-candidate", "current-source-inventory"):
            self.assertIn(job, section)
        self.assertIn("aggregate-release-gates.py needs", section)

    def test_upload_requires_candidate_and_linux_gate_jobs(self):
        self.assertIn("aggregate-release-gates.py run", self.upload)
        self.assertIn('--required-job \"Release candidate gate\"', self.upload)
        self.assertIn("--required-job linux-distribution", self.upload)
        self.assertIn("--json headSha,status,conclusion,jobs", self.upload)

    def test_cli_fails_when_json_is_missing(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "needs", "--json-env", "NOT_PRESENT",
             "--required", "validate"],
            check=False, capture_output=True, text=True,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn('"passed": false', result.stdout)

    def test_cli_writes_a_machine_readable_report(self):
        with tempfile.TemporaryDirectory() as directory:
            input_file = Path(directory) / "steps.json"
            report = Path(directory) / "gate.json"
            input_file.write_text(json.dumps({"startup": {"outcome": "success"}}))
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "steps", "--json-file", str(input_file),
                 "--required", "startup", "--report", str(report)],
                check=False, capture_output=True, text=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue(report.exists())
            self.assertTrue(json.loads(report.read_text())["passed"])


if __name__ == "__main__":
    unittest.main()
