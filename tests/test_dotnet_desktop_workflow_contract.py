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


if __name__ == "__main__":
    unittest.main()
