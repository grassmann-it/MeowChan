import re
import shlex
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "dotnet-desktop.yml"

EXPECTED_SHARDS = {
    "shard-01": ["tests/MeowChan.Tests/MeowChan.Tests.csproj"],
    "shard-02": ["tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj"],
    "shard-03": ["tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj"],
    "shard-04": ["tests/MeowChan.PluginFilters.Tests/MeowChan.PluginFilters.Tests.csproj"],
    "shard-05": ["tests/MeowChan.SourceAdapter.Tests/MeowChan.SourceAdapter.Tests.csproj"],
    "shard-06": [
        "tests/MeowChan.OfficialPlugins.Tests/MeowChan.OfficialPlugins.Tests.csproj",
        "tests/MeowChan.ExamplePlugins.Tests/MeowChan.ExamplePlugins.Tests.csproj",
    ],
    "shard-07": [
        "tests/MeowChan.PluginCompiler.Tests/MeowChan.PluginCompiler.Tests.csproj",
        "tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj",
        "tests/MeowChan.PluginSdk.Tests/MeowChan.PluginSdk.Tests.csproj",
    ],
    "shard-08": [
        "tests/MeowChan.ImageHeader.Tests/MeowChan.ImageHeader.Tests.csproj",
        "tests/MeowChan.MetadataFilters.Tests/MeowChan.MetadataFilters.Tests.csproj",
        "tests/MeowChan.OriginalMediaHttp.Tests/MeowChan.OriginalMediaHttp.Tests.csproj",
    ],
}


class DotnetDesktopWorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index("      - name: Run core and desktop offline test shards")
        end = workflow.index("      - name: Run native macOS offline test shard", start)
        cls.step = workflow[start:end]

    def test_non_macos_step_uses_selected_two_shard_lane_runner(self):
        self.assertIn("python scripts/run-dotnet-shards.py --lane \"${{ matrix.lane }}\"", self.step)
        self.assertIn("--results-root test-results/offline-shards", self.step)
        self.assertEqual(self.step.count("python scripts/run-dotnet-shards.py"), 1)
        self.assertNotIn("dotnet test MeowChan.sln", self.step)
        self.assertNotIn("timeout --", self.step)

    def test_runner_declares_exactly_eight_disjoint_shards(self):
        runner = (WORKFLOW.parents[2] / "scripts" / "run-dotnet-shards.py").read_text(encoding="utf-8")
        self.assertIn('"shard-01"', runner)
        self.assertIn('"shard-08"', runner)
        shard_keys = set(re.findall(r'"(shard-\d{2})":\s*\(', runner))
        self.assertEqual(shard_keys, set(EXPECTED_SHARDS))
        projects = re.findall(r'"(tests/[^"\n]+\.csproj)"', runner)
        self.assertEqual(len(projects), len(set(projects)))
        self.assertEqual(set(projects), {project for shard in EXPECTED_SHARDS.values() for project in shard})

    def test_each_project_has_private_trx_directory_and_deadline(self):
        self.assertIn("--results-root test-results/offline-shards", self.step)
        self.assertIn("timeout-minutes: 35", self.step)
        self.assertIn("scripts/run-dotnet-shards.py", self.step)

    def test_matrix_expression_has_balanced_parentheses_outside_strings(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        expression = next(line for line in workflow.splitlines()
                          if line.strip().startswith("matrix: ${{ fromJSON("))
        expression = re.sub(r"'(?:[^']|'')*'", "", expression)
        depth = 0
        for char in expression:
            depth += (char == "(") - (char == ")")
            self.assertGreaterEqual(depth, 0, "Unexpected closing parenthesis in matrix expression")
        self.assertEqual(0, depth)

    def test_workflow_uses_the_checked_in_planner(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn('matrix: ${{ fromJSON(needs.validate-plan.outputs.matrix) }}', workflow)
        self.assertIn('scripts/balance-validation.py matrix', workflow)
        self.assertIn('name: validation-${{ matrix.os }}-${{ matrix.lane }}-${{ runner.arch }}', workflow)

    def test_mac_step_is_outside_shard_step_and_non_macos_guard_remains(self):
        self.assertIn("if: runner.os != 'macOS'", self.step)
        self.assertNotIn("macos-test-watchdog.py", self.step)

    def matrix(self, *, event="workflow_dispatch", platform="all", scenario="all", mode="validate",
               portable_shards=5, macos_shards=9, offline_lane="lane-c"):
        from tests.test_validation_gate import balance
        return balance.matrix(event, mode, scenario, platform, offline_lane, portable_shards, macos_shards)["include"]

    def test_evaluated_full_matrix_has_expected_workers_per_platform(self):
        for event, mode in (("push", ""), ("pull_request", ""), ("workflow_dispatch", "validate"),
                            ("workflow_dispatch", "prerelease-candidate"), ("workflow_dispatch", "stable-candidate")):
            rows = self.matrix(event=event, mode=mode)
            self.assertEqual(33, len(rows))
            for platform in ("macos-15", "macos-15-intel"):
                native = [row for row in rows if row["os"] == platform]
                self.assertEqual(list(range(1, 10)), [row["mac_shard"] for row in native])
                self.assertEqual(["native"], [row["lane"] for row in native if row["full"]])
                if mode in ("prerelease-candidate", "stable-candidate"):
                    with self.assertRaisesRegex(ValueError, "Candidates require all"):
                        self.matrix(platform=platform, mode=mode)
                else:
                    self.assertEqual(native, self.matrix(platform=platform, mode=mode))

    def test_explicit_counts_size_the_matrix_without_changing_scope(self):
        for platform, options, count in (("windows-2022", dict(portable_shards=7), 7),
                                         ("macos-15-intel", dict(macos_shards=10), 10)):
            rows = self.matrix(platform=platform, **options)
            self.assertEqual(count, len(rows))
            self.assertTrue(all(r["os"] == platform and r["workers"] == count for r in rows))
            self.assertEqual(1, sum(r["full"] for r in rows))
            self.assertCountEqual(["upgrade_replay", "upgrade_102_replay"],
                                  [task for r in rows for task in r["tasks"] if task.startswith("upgrade")])
        for options in (dict(portable_shards=3), dict(macos_shards=13)):
            with self.assertRaises(ValueError):
                self.matrix(**options)

    def test_focused_native_input_scenarios_do_not_expand_mac_shards(self):
        for scenario in ("media-input", "gallery-features", "gallery-errors", "gif-playback", "selection"):
            for platform in ("all", "macos-15", "macos-15-intel"):
                rows = self.matrix(platform=platform, scenario=scenario)
                native = [r for r in rows if r["os"].startswith("macos-")]
                self.assertEqual(2 if platform == "all" else 1, len(native))
                self.assertTrue(all(r["full"] and r["mac_shard"] == 1 for r in native))

    def test_gui_only_uses_default_workers_without_offline_work(self):
        rows = self.matrix(scenario="gui-only")
        self.assertEqual(33, len(rows))
        for platform in ("macos-15", "macos-15-intel"):
            native = [row for row in rows if row["os"] == platform]
            self.assertEqual(list(range(1, 10)), [row["mac_shard"] for row in native])
            self.assertEqual(native, self.matrix(platform=platform, scenario="gui-only"))
        for platform in ("ubuntu-24.04", "ubuntu-24.04-arm", "windows-2022"):
            lanes = self.matrix(platform=platform, scenario="gui-only")
            self.assertEqual(["lane-a", "lane-b", "lane-c", "lane-d", "lane-e"], [row["lane"] for row in lanes])

    def test_candidate_gate_explicitly_requires_cross_worker_coverage_gate(self):
        workflow = WORKFLOW.read_text()
        gate = workflow.split("  release-candidate-gate:\n")[1].split("  macos-diagnose:")[0]
        self.assertIn("needs: [validate, macos-offline-gate,", gate)
        self.assertIn("--required validate,macos-offline-gate,", gate)
        offline_gate = workflow.split("  macos-offline-gate:\n")[1].split("  library-package:")[0]
        self.assertIn("needs: [validate, validate-plan]", offline_gate)
        self.assertIn("always()", offline_gate)
        self.assertIn("inputs.scenario == 'all'", offline_gate)
        self.assertIn("pattern: macos-offline-macos-*", offline_gate)
        self.assertIn("scripts/run-macos-test-shards.py aggregate", offline_gate)
        self.assertIn("--platform macos-15 --platform macos-15-intel", offline_gate)
        self.assertNotIn("continue-on-error", offline_gate)

    def test_offline_retest_matrix_is_one_nonfull_lane_on_nonmacos(self):
        for lane in ("lane-a", "lane-b", "lane-c", "lane-d", "lane-e"):
            rows = self.matrix(mode="offline-retest", offline_lane=lane)
            self.assertEqual(["ubuntu-24.04", "ubuntu-24.04-arm", "windows-2022"], [r["os"] for r in rows])
            self.assertTrue(all(r["lane"] == lane and r["workers"] == 5 and not r["full"] for r in rows))
        single = self.matrix(mode="offline-retest", platform="windows-2022", offline_lane="lane-d")
        self.assertEqual(["windows-2022"], [row["os"] for row in single])
        for invalid in (dict(offline_lane="lane-l"), dict(platform="macos-15"), dict(scenario="selection")):
            with self.assertRaises(ValueError):
                self.matrix(mode="offline-retest", **invalid)

    def test_offline_retest_input_defaults_to_lane_c(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        block = workflow[workflow.index("      offline_lane:"):workflow.index("      scenario:")]
        self.assertIn("default: lane-c", block)
        self.assertEqual([f"lane-{letter}" for letter in "abcdefghijkl"], re.findall(r"- (lane-\w)", block))
        self.assertIn("          - offline-retest\n", workflow)

    def test_candidate_and_existing_matrix_branches_never_use_retest_lane(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        for mode in ("validate", "prerelease-candidate", "stable-candidate"):
            self.assertEqual(self.matrix(mode=mode, offline_lane="lane-a"),
                             self.matrix(mode=mode, offline_lane="lane-l"))
        candidate = workflow[workflow.index("id: candidate_matrix"):]
        candidate = candidate[:candidate.index("      - uses: actions/setup-dotnet")]
        self.assertNotIn("offline", candidate)
        self.assertIn('test "${{ inputs.platform }}" = all', candidate)

    def test_guard_is_first_step_and_rejects_invalid_scope(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index("      - name: Reject offline retest outside its bounded scope")
        self.assertLess(workflow.index("      - uses: actions/checkout@v7"), start)
        self.assertLess(start, workflow.index("      - uses: actions/setup-dotnet@v6"))
        guard = workflow[start:workflow.index("      - name: Require the complete current-source candidate matrix")]
        self.assertNotIn("continue-on-error", guard)
        self.assertIn("lane-[a-l]", guard)
        self.assertIn("macos-*", guard)
        self.assertIn('"$RUNNER_OS" != macOS', guard)
        self.assertIn('"$RETEST_SCENARIO" = all', guard)


if __name__ == "__main__":
    unittest.main()
