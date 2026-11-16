#!/usr/bin/env python3
"""Regression tests for the final validation gate's required-step selection."""
import importlib.util
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOW = ROOT / ".github/workflows/dotnet-desktop.yml"


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


compute = load("compute_required", "compute-validation-required.py")
aggregate = load("aggregate_gates", "aggregate-release-gates.py")
SCENARIOS = list(compute.TARGETED_STEPS)
PLATFORMS = [("Linux", "lane-a", "X64"), ("Windows", "lane-a", "X64"), ("macOS", "native", "ARM64")]


class Ctx:
    def __init__(self, **values):
        self.__dict__.update(values)

    def __getattr__(self, name):
        return ""


def workflow_steps():
    """Return {id: if-expression or None} for the validate job's steps."""
    steps, current = {}, None
    for line in WORKFLOW.read_text(encoding="utf-8").splitlines():
        m = re.match(r"^      - (?:name:.*|uses:.*)$", line)
        if m:
            current = {"id": None, "if": None}
            continue
        if current is None:
            continue
        m = re.match(r"^\s+id: (\w+)\s*$", line)
        if m:
            current["id"] = m.group(1)
            steps[current["id"]] = current
        m = re.match(r"^        if: (.*)$", line)
        if m:
            current["if"] = m.group(1).strip()
    return {k: v["if"] for k, v in steps.items()}


def predicate(expr, event, mode, scenario, full, runner_os, arch):
    if expr is None:
        return True
    py = re.sub(r"!(?!=)", " not ", expr).replace("&&", " and ").replace("||", " or ")
    py = py.replace("always()", "True").replace("true", "True")
    env = {
        "github": Ctx(event_name=event), "inputs": Ctx(mode=mode, scenario=scenario, platform="all"),
        "matrix": Ctx(full=full), "runner": Ctx(os=runner_os, arch=arch),
    }
    return bool(eval(py, {"__builtins__": {}}, env))


def runs(step_if, scenario, runner_os, arch="X64", event="workflow_dispatch", mode="validate"):
    return predicate(step_if, event, mode, scenario, True, runner_os, arch)


def outcomes(required, skipped=(), failed=()):
    return {n: {"outcome": "skipped" if n in skipped else "failure" if n in failed else "success"} for n in required}


class RequiredSelectionTests(unittest.TestCase):
    def req(self, scenario, os_="Linux", lane="lane-a", event="workflow_dispatch", mode="validate", arch="X64"):
        return compute.required_steps(event, mode, scenario, lane, os_, arch)

    def test_empty_scenario_on_push_and_pr_uses_full_requirements(self):
        for event in ("push", "pull_request"):
            required = self.req("", event=event)
            self.assertIn("build_all", required)
            self.assertIn("core_tests", required)
            self.assertEqual(required, self.req("all"))

    def test_selected_scenario_requires_its_steps_and_build_prerequisites(self):
        required = self.req("gallery-features")
        for step in ("record_source", "smoke_restore", "smoke_assets", "smoke_build", "plugin_delivery", "linux_setup", "gallery_features"):
            self.assertIn(step, required)
        self.assertNotIn("media_input", required)
        self.assertNotIn("build_all", required)
        self.assertNotIn("core_tests", required)
        self.assertIn("prepare_desktop", self.req("gif-playback", "Windows"))
        self.assertIn("prepare_desktop", self.req("gif-playback", "macOS", "native", arch="ARM64"))

    def test_nonfull_lanes_only_require_record_source_when_targeted(self):
        self.assertEqual(self.req("media-input", "Linux", "lane-b"), ["record_source"])
        self.assertEqual(self.req("all", "Linux", "lane-b"), ["record_source", "core_tests"])

    def test_skipped_selected_scenario_fails_gate(self):
        for scenario in SCENARIOS:
            required = self.req(scenario)
            step = compute.TARGETED_STEPS[scenario][-1]
            result = aggregate.step_gate(outcomes(required, skipped={step}), required)
            self.assertFalse(result["passed"], scenario)
            self.assertIn(step, result["requiredNotSuccessful"])
        required = self.req("gif-playback")
        self.assertFalse(aggregate.step_gate(outcomes(required, skipped={"smoke_build"}), required)["passed"])
        self.assertFalse(aggregate.step_gate({"record_source": {"outcome": "success"}}, required)["passed"])

    def test_successful_targeted_scenario_passes_gate(self):
        for scenario in SCENARIOS:
            required = self.req(scenario)
            observed = outcomes(required, skipped={"build_all", "core_tests", "dev_build"})
            self.assertTrue(aggregate.step_gate(observed, required)["passed"], scenario)

    def test_failed_targeted_step_fails_gate(self):
        required = self.req("media-input")
        self.assertFalse(aggregate.step_gate(outcomes(required, failed={"media_input"}), required)["passed"])


class WorkflowConsistencyTests(unittest.TestCase):
    def test_required_steps_exist_and_actually_run_per_workflow_predicates(self):
        steps = workflow_steps()
        for scenario in SCENARIOS:
            for os_, lane, arch in PLATFORMS:
                for step in compute.required_steps("workflow_dispatch", "validate", scenario, lane, os_, arch):
                    self.assertIn(step, steps, step)
                    self.assertTrue(runs(steps[step], scenario, os_, arch), f"{step} skipped for {scenario} on {os_}")

    def test_scenario_steps_are_not_forced_when_workflow_skips_them(self):
        steps = workflow_steps()
        for scenario in ("selection", "gallery-errors", "gif-playback", "gallery-features", "media-input"):
            required = compute.required_steps("workflow_dispatch", "validate", scenario, "lane-a", "Linux", "X64")
            for step in ("smoke_normal", "first_run", "media_input", "gif_playback", "gallery_errors", "gallery_features"):
                if step not in required:
                    continue
                self.assertTrue(runs(steps[step], scenario, "Linux"))

    def test_full_requirements_run_on_push_and_pull_request(self):
        steps = workflow_steps()
        for event in ("push", "pull_request"):
            for os_, lane, arch in PLATFORMS:
                for step in compute.required_steps(event, "", "", lane, os_, arch):
                    self.assertTrue(
                        predicate(steps[step], event, "", "", True, os_, arch), f"{step} skipped on {event}/{os_}")

    def test_workflow_keeps_continue_on_error_fail_fast_false_and_script_gate(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("fail-fast: false", text)
        self.assertIn("scripts/compute-validation-required.py", text)
        self.assertNotIn("required=(record_source)", text)
        for step in ("smoke_build", "gallery_features", "media_input"):
            block = text[text.index(f"id: {step}\n"):]
            self.assertIn("continue-on-error: true", block.split("      - ")[0])


def job_conditions():
    """Return {job: if-expression} for the workflow's top-level jobs."""
    jobs, current = {}, None
    in_jobs = False
    for line in WORKFLOW.read_text(encoding="utf-8").splitlines():
        if line == "jobs:":
            in_jobs = True
        elif in_jobs and (m := re.match(r"^  ([a-z][\w-]*):\s*$", line)):
            current = m.group(1)
            jobs[current] = None
        elif in_jobs and current and (m := re.match(r"^    if: (.*)$", line)):
            expr = m.group(1).strip()
            jobs[current] = expr[3:-2].strip() if expr.startswith("${{") else expr
    return jobs


class OfflineRetestTests(unittest.TestCase):
    def req(self, lane="lane-c", os_="Linux", scenario="all"):
        return compute.required_steps("workflow_dispatch", "offline-retest", scenario, lane, os_, "X64")

    def test_guard_rejects_invalid_scope(self):
        for lane in ("lane-e", "native", ""):
            with self.assertRaises(ValueError):
                self.req(lane=lane)
        with self.assertRaises(ValueError):
            self.req(os_="macOS")
        for scenario in ("selection", "gui-only", "media-input"):
            with self.assertRaises(ValueError):
                self.req(scenario=scenario)

    def test_required_steps_are_build_and_core_tests_only_for_every_lane(self):
        steps = workflow_steps()
        for lane in ("lane-a", "lane-b", "lane-c", "lane-d"):
            for os_ in ("Linux", "Windows"):
                required = self.req(lane=lane, os_=os_)
                self.assertEqual(["offline_retest_guard", "record_source", "restore_release", "build_all",
                                  "process_probe", "core_tests"], required)
                for step in required:
                    self.assertIn(step, steps)
                    self.assertTrue(
                        predicate(steps[step], "workflow_dispatch", "offline-retest", "all", False, os_, "X64"), step)

    def test_only_build_and_core_test_steps_run_on_a_nonfull_lane(self):
        expected = {"checkout_source", "offline_retest_guard", "setup_dotnet", "record_source", "dev_build",
                    "restore_release", "retain_release_assets", "build_all", "process_probe", "core_tests",
                    "validation_summary", "metadata"}
        for os_ in ("Linux", "Windows"):
            ran = {step for step, expr in workflow_steps().items()
                   if predicate(expr, "workflow_dispatch", "offline-retest", "all", False, os_, "X64")}
            self.assertEqual(expected, ran)

    def test_mode_cannot_run_any_staging_release_or_packaging_job(self):
        for job, expr in job_conditions().items():
            ran = predicate(expr, "workflow_dispatch", "offline-retest", "all", False, "Linux", "X64")
            self.assertEqual(job == "validate", ran, job)

    def test_candidate_modes_keep_complete_requirements(self):
        for mode in ("prerelease-candidate", "stable-candidate"):
            required = compute.required_steps("workflow_dispatch", mode, "all", "lane-a", "Linux", "X64")
            self.assertIn("candidate_matrix", required)
            self.assertIn("smoke_normal", required)
            self.assertNotIn("offline_retest_guard", required)
            self.assertEqual(compute.required_steps("workflow_dispatch", mode, "all", "lane-a", "Linux", "X64"),
                             compute.required_steps("workflow_dispatch", mode, "", "lane-a", "Linux", "X64"))


if __name__ == "__main__":
    unittest.main()
