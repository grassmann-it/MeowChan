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

    def test_matrix_has_four_non_macos_lanes_and_eight_native_shards_per_macos(self):
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
        self.assertEqual(3, len(literals))
        self.assertTrue(all("{{" in literal and "}}" in literal for literal in literals))
        self.assertNotIn("format('{\"include\"", matrix_line)

        def render(literal, platform):
            return literal.replace("{{", "{").replace("}}", "}").replace("{0}", platform)

        macos = json.loads(render(literals[0], "macos-15"))
        self.assertEqual(list(range(1, 9)), [row["mac_shard"] for row in macos["include"]])
        self.assertEqual(1, sum(row["full"] for row in macos["include"]))
        focused = json.loads(render(literals[1], "macos-15"))
        self.assertEqual([{"os": "macos-15", "lane": "native", "full": True, "mac_shard": 1}], focused["include"])
        linux = json.loads(render(literals[2], "ubuntu-24.04"))
        self.assertEqual(4, len(linux["include"]))
        self.assertEqual("ubuntu-24.04", linux["include"][0]["os"])
        self.assertEqual("lane-d", linux["include"][-1]["lane"])


    def matrix(self, *, event="workflow_dispatch", platform="all", scenario="all", mode="validate"):
        from types import SimpleNamespace
        line = next(l for l in WORKFLOW.read_text().splitlines() if l.strip().startswith("matrix: ${{ fromJSON("))
        expression = line.split("${{", 1)[1].rsplit("}}", 1)[0].strip()
        strings = {}
        def literal(match):
            key = "literal_" + str(len(strings))
            strings[key] = match[0][1:-1].replace("''", "'")
            return key
        expression = re.sub(r"'(?:[^']|'')*'", literal, expression)
        expression = re.sub(r"!(?!=)", " not ", expression).replace("&&", " and ").replace("||", " or ")
        return eval(expression, {"__builtins__": {}}, {
            **strings, "fromJSON": json.loads, "format": lambda text, *args: text.format(*args),
            "github": SimpleNamespace(event_name=event),
            "inputs": SimpleNamespace(platform=platform, scenario=scenario, mode=mode, offline_lane="lane-c"),
        })["include"]

    def test_evaluated_full_matrix_has_eight_native_workers_and_gui_once_per_platform(self):
        for event, mode, scenario in (("push", "", ""), ("pull_request", "", ""),
                                      ("workflow_dispatch", "validate", "all"),
                                      ("workflow_dispatch", "prerelease-candidate", "all"),
                                      ("workflow_dispatch", "stable-candidate", "all")):
            rows = self.matrix(event=event, mode=mode, scenario=scenario)
            self.assertEqual(28, len(rows))
            for os_name in ("macos-15", "macos-15-intel"):
                native = [r for r in rows if r["os"] == os_name]
                self.assertEqual(list(range(1, 9)), [r["mac_shard"] for r in native])
                self.assertEqual(["native"], [r["lane"] for r in native if r["full"]])
                single = self.matrix(platform=os_name, mode=mode, scenario="all")
                self.assertEqual(native, single)

    def test_focused_native_input_scenarios_do_not_expand_mac_shards(self):
        for scenario in ("media-input", "gallery-features", "gallery-errors", "gif-playback", "selection"):
            for platform in ("all", "macos-15", "macos-15-intel"):
                rows = self.matrix(platform=platform, scenario=scenario)
                native = [r for r in rows if r["os"].startswith("macos-")]
                self.assertEqual(2 if platform == "all" else 1, len(native))
                self.assertTrue(all(r["full"] and r["mac_shard"] == 1 for r in native))

    def test_gui_only_uses_existing_native_eight_and_other_four_workers(self):
        rows = self.matrix(scenario="gui-only")
        self.assertEqual(28, len(rows))
        for platform in ("macos-15", "macos-15-intel"):
            native = [row for row in rows if row["os"] == platform]
            self.assertEqual(list(range(1, 9)), [row["mac_shard"] for row in native])
            self.assertEqual(native, self.matrix(platform=platform, scenario="gui-only"))
        for platform in ("ubuntu-24.04", "ubuntu-24.04-arm", "windows-2022"):
            lanes = self.matrix(platform=platform, scenario="gui-only")
            self.assertEqual(["lane-a", "lane-b", "lane-c", "lane-d"], [row["lane"] for row in lanes])

    def test_candidate_gate_explicitly_requires_cross_worker_coverage_gate(self):
        workflow = WORKFLOW.read_text()
        gate = workflow.split("  release-candidate-gate:\n")[1].split("  macos-diagnose:")[0]
        self.assertIn("needs: [validate, macos-offline-gate,", gate)
        self.assertIn("--required validate,macos-offline-gate,", gate)
        offline_gate = workflow.split("  macos-offline-gate:\n")[1].split("  library-package:")[0]
        self.assertIn("needs: validate", offline_gate)
        self.assertIn("always()", offline_gate)
        self.assertIn("inputs.scenario == 'all'", offline_gate)
        self.assertIn("pattern: macos-offline-macos-*", offline_gate)
        self.assertIn("scripts/run-macos-test-shards.py aggregate", offline_gate)
        self.assertIn("--platform macos-15 --platform macos-15-intel", offline_gate)
        self.assertNotIn("continue-on-error", offline_gate)

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
