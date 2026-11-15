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

    def test_non_macos_step_launches_exactly_eight_disjoint_shards(self):
        launches = {}
        for line in self.step.splitlines():
            match = re.match(r"\s*run_shard\s+(shard-\d{2})(.*?)\s+&$", line)
            if match:
                launches[match.group(1)] = shlex.split(match.group(2))

        self.assertEqual(set(launches), set(EXPECTED_SHARDS))
        self.assertEqual(launches, EXPECTED_SHARDS)
        projects = [project for shard in launches.values() for project in shard]
        self.assertEqual(len(projects), len(set(projects)))

    def test_each_project_has_private_trx_directory_and_deadline(self):
        self.assertIn("results_root=test-results/offline-shards", self.step)
        self.assertIn('timeout --signal=TERM --kill-after=30s "${timeout_seconds}s"', self.step)
        self.assertIn('shard-06|shard-07|shard-08', self.step)
        self.assertIn('dotnet test "$project"', self.step)
        self.assertIn('--logger "trx;LogFileName=${project_name}.trx"', self.step)
        self.assertIn('--results-directory "$project_results"', self.step)
        self.assertIn('run_shard shard-01', self.step)
        self.assertIn('run_shard shard-08', self.step)
        self.assertIn('"shard-01:$pid_01" "shard-02:$pid_02" "shard-03:$pid_03" "shard-04:$pid_04"', self.step)
        self.assertIn('"shard-05:$pid_05" "shard-06:$pid_06" "shard-07:$pid_07" "shard-08:$pid_08"', self.step)

    def test_mac_step_is_outside_shard_step_and_non_macos_guard_remains(self):
        self.assertIn("if: runner.os != 'macOS'", self.step)
        self.assertNotIn("macos-test-watchdog.py", self.step)


if __name__ == "__main__":
    unittest.main()
