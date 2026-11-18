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
REQ_SPEC = importlib.util.spec_from_file_location("compute_validation_required", ROOT / "scripts/compute-validation-required.py")
GATE_REQUIRED = importlib.util.module_from_spec(REQ_SPEC)
assert REQ_SPEC.loader is not None
REQ_SPEC.loader.exec_module(GATE_REQUIRED)


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

    def test_release_upload_takes_version_from_input_or_product(self):
        self.assertIn("RELEASE_VERSION", self.upload)
        self.assertIn("Directory.Build.props", self.upload)
        self.assertIn("--version \"$RELEASE_VERSION\"", self.upload)
        self.assertNotIn("'v0.9.0'", self.upload)
        self.assertNotIn("upload-verified-v0.9.0", self.upload)

    def test_validation_keeps_parallel_diagnostics_and_uses_aggregator(self):
        self.assertIn("continue-on-error: true", self.workflow)
        self.assertIn("python3 scripts/aggregate-release-gates.py steps", self.workflow)
        self.assertIn("scripts/compute-validation-required.py", self.workflow)
        full = GATE_REQUIRED.required_steps("push", "", "", "lane-a", "Linux", "X64")
        self.assertTrue({"linux_setup", "tray_host", "core_tests", "smoke_plugins"} <= set(full))
        mac = GATE_REQUIRED.required_steps("push", "", "", "native", "macOS", "ARM64")
        self.assertTrue({"prepare_desktop", "mac_tests"} <= set(mac))
        gate = self.workflow.index("      - name: Fail validation after collecting all evidence")
        upload = self.workflow.index("      - name: Upload regression and graphical evidence")
        self.assertLess(gate, upload)
        self.assertIn("artifacts/build-evidence/release-validation-gate.json", self.workflow[upload:])

    def test_candidate_1_1_registry_smoke_is_x64_only_and_required_when_run(self):
        start = self.workflow.index("      - name: Exercise selected 1.1 registry workflows on Linux x64")
        end = self.workflow.index("      - name: Exercise the released 1.0.2 fixture against the current source", start)
        section = self.workflow[start:end]
        self.assertIn("id: smoke_1_1_registry", section)
        self.assertIn("continue-on-error: true", section)
        self.assertIn("inputs.mode == 'prerelease-candidate'", section)
        self.assertIn("inputs.mode == 'stable-candidate'", section)
        self.assertIn("runner.os == 'Linux'", section)
        self.assertIn("runner.arch == 'X64'", section)
        self.assertIn("--registry --only booru-workbench", section)
        for identifier in (
            "booru-composition",
            "booru-media",
            "tagging-page",
            "tagging-mode",
            "media-page",
            "media-page-booru-captures",
            "media-menu-surfaces",
            "settings-reader-display",
        ):
            self.assertIn("--only " + identifier, section)

        candidate = ("workflow_dispatch", "prerelease-candidate", "all")
        self.assertIn("smoke_1_1_registry", GATE_REQUIRED.required_steps(*candidate, "lane-a", "Linux", "X64"))
        self.assertNotIn("smoke_1_1_registry", GATE_REQUIRED.required_steps(*candidate, "lane-a", "Linux", "ARM64"))
        self.assertNotIn("smoke_1_1_registry", GATE_REQUIRED.required_steps("push", "", "", "lane-a", "Linux", "X64"))
        self.assertIn("artifacts/desktop-smoke-1.1-registry/**", self.workflow)

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
        self.assertEqual(2, section.count("':(exclude)test-results'"))
        self.assertIn("matches = [item for item in rows if item.get('testName') == expected]", section)
        self.assertIn("assert len(matches) == 1 and matches[0].get('outcome') == 'Passed'", section)
        self.assertNotIn("assert len(rows) == 1", section)
        self.assertIn("candidate_matrix", GATE_REQUIRED.required_steps("workflow_dispatch", "stable-candidate", "all", "lane-a", "Linux", "X64"))
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
