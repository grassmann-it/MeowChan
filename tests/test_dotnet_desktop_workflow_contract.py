import json
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
        end = workflow.index("      - name: Run both complete macOS offline suites", start)
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

    def test_matrix_has_four_non_macos_lanes_and_one_native_lane_per_macos(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn('matrix: ${{ fromJSON(', workflow)
        for os_name in ("ubuntu-24.04", "ubuntu-24.04-arm", "windows-2022"):
            self.assertIn(f'"os":"{os_name}","lane":"lane-a"', workflow)
            self.assertIn(f'"os":"{os_name}","lane":"lane-d"', workflow)
        self.assertIn('"os":"macos-15","lane":"native"', workflow)
        self.assertIn('"os":"macos-15-intel","lane":"native"', workflow)
        self.assertIn('name: validation-${{ matrix.os }}-${{ matrix.lane }}-${{ runner.arch }}', workflow)

    def test_mac_step_is_outside_shard_step_and_non_macos_guard_remains(self):
        self.assertIn("if: runner.os != 'macOS'", self.step)
        self.assertNotIn("macos-test-watchdog.py", self.step)

    def test_single_platform_matrix_formats_escaped_json_and_keeps_jobs(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        matrix_line = next(
            line for line in workflow.splitlines()
            if line.strip().startswith("matrix: ${{ fromJSON(")
        )
        literals = re.findall(r"format\('([^']+)', inputs\.platform\)", matrix_line)
        self.assertEqual(2, len(literals))
        self.assertTrue(all("{{" in literal and "}}" in literal for literal in literals))
        self.assertNotIn("format('{\"include\"", matrix_line)

        def render(literal, platform):
            return literal.replace("{{", "{").replace("}}", "}").replace("{0}", platform)

        macos = json.loads(render(literals[0], "macos-15"))
        self.assertEqual([{"os": "macos-15", "lane": "native", "full": True}], macos["include"])
        linux = json.loads(render(literals[1], "ubuntu-24.04"))
        self.assertEqual(4, len(linux["include"]))
        self.assertEqual("ubuntu-24.04", linux["include"][0]["os"])
        self.assertEqual("lane-d", linux["include"][-1]["lane"])


    def retest_literals(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        line = next(l for l in workflow.splitlines() if l.strip().startswith("matrix: ${{ fromJSON("))
        head = line.split("|| (github.event_name != 'workflow_dispatch'")[0]
        return re.findall(r"format\('([^']+)', ([^)]*)\)", head)

    @staticmethod
    def render(literal, *args):
        text = literal.replace("{{", "{").replace("}}", "}")
        for index, arg in enumerate(args):
            text = text.replace("{%d}" % index, arg)
        return json.loads(text)

    def test_offline_retest_matrix_is_one_nonfull_lane_on_nonmacos(self):
        (all_literal, all_args), (one_literal, one_args) = self.retest_literals()
        self.assertEqual("inputs.offline_lane", all_args)
        self.assertEqual("inputs.platform, inputs.offline_lane", one_args)
        for lane in ("lane-a", "lane-b", "lane-c", "lane-d"):
            include = self.render(all_literal, lane)["include"]
            self.assertEqual(["ubuntu-24.04", "ubuntu-24.04-arm", "windows-2022"], [e["os"] for e in include])
            self.assertTrue(all(e["lane"] == lane and e["full"] is False for e in include))
        single = self.render(one_literal, "windows-2022", "lane-c")["include"]
        self.assertEqual([{"os": "windows-2022", "lane": "lane-c", "full": False}], single)

    def test_offline_retest_input_defaults_to_lane_c_with_four_choices(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        block = workflow[workflow.index("      offline_lane:"):workflow.index("      scenario:")]
        self.assertIn("default: lane-c", block)
        self.assertEqual(["lane-a", "lane-b", "lane-c", "lane-d"], re.findall(r"- (lane-\w)", block))
        self.assertIn("          - offline-retest\n", workflow)

    def test_candidate_and_existing_matrix_branches_never_use_retest_lane(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        line = next(l for l in workflow.splitlines() if l.strip().startswith("matrix: ${{ fromJSON("))
        existing = line.split("|| (github.event_name != 'workflow_dispatch'", 1)[1]
        self.assertNotIn("offline_lane", existing)
        self.assertIn('"os":"macos-15","lane":"native","full":true', existing)
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
        self.assertIn("lane-a|lane-b|lane-c|lane-d", guard)
        self.assertIn("macos-*", guard)
        self.assertIn('"$RUNNER_OS" != macOS', guard)
        self.assertIn('"$RETEST_SCENARIO" = all', guard)


if __name__ == "__main__":
    unittest.main()
