import importlib.util
import json
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).parents[1]
RUNNER_PATH = ROOT / "scripts" / "run-dotnet-shards.py"
SPEC = importlib.util.spec_from_file_location("dotnet_shard_runner", RUNNER_PATH)
runner = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = runner
assert SPEC.loader is not None
SPEC.loader.exec_module(runner)


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


class FakeProcess:
    def __init__(self, *, timeout_first=False):
        self.pid = 4242
        self.returncode = None
        self.timeout_first = timeout_first
        self.wait_calls = 0
        self.signals = []
        self.killed = False

    def wait(self, timeout=None):
        self.wait_calls += 1
        if self.timeout_first and self.wait_calls == 1:
            raise subprocess.TimeoutExpired("fake", timeout)
        self.returncode = 124 if self.killed else 0
        return self.returncode

    def send_signal(self, value):
        self.signals.append(value)

    def terminate(self):
        self.signals.append("terminate")

    def kill(self):
        self.killed = True


class DotnetShardRunnerTests(unittest.TestCase):
    def test_eight_shards_are_disjoint_and_cover_expected_projects(self):
        self.assertEqual({key: list(value) for key, value in runner.SHARDS.items()}, EXPECTED_SHARDS)
        projects = [project for shard in runner.SHARDS.values() for project in shard]
        self.assertEqual(len(projects), len(set(projects)))
        self.assertEqual(len(runner.SHARDS), 8)

    def test_command_has_private_results_directory_and_hang_diagnostics(self):
        results = Path("test-results/offline-shards/shard-01/MeowChan.Tests")
        command = runner.build_test_command("dotnet", EXPECTED_SHARDS["shard-01"][0], results)
        self.assertEqual(command[0:3], ["dotnet", "test", EXPECTED_SHARDS["shard-01"][0]])
        self.assertIn("--no-build", command)
        self.assertIn("--no-restore", command)
        self.assertIn("--blame-hang", command)
        self.assertIn("--blame-hang-timeout", command)
        self.assertIn("--results-directory", command)
        self.assertIn(str(results), command)
        self.assertIn("--logger", command)
        self.assertIn("trx;LogFileName=MeowChan.Tests.trx", command)

    def test_process_isolation_uses_new_session_or_windows_process_group(self):
        self.assertEqual(runner.process_creation_kwargs(False), {"start_new_session": True})
        self.assertIn("creationflags", runner.process_creation_kwargs(True))

    def test_posix_timeout_escalates_the_entire_process_group(self):
        process = FakeProcess(timeout_first=True)
        with mock.patch.object(runner.os, "getpgid", return_value=process.pid), mock.patch.object(
            runner.os, "killpg"
        ) as killpg:
            runner.terminate_process_tree(process, cleanup_grace_seconds=0, is_windows=False)
        self.assertEqual(killpg.call_args_list[0].args, (process.pid, signal.SIGTERM))
        self.assertEqual(killpg.call_args_list[1].args, (process.pid, signal.SIGKILL))

    def test_windows_timeout_uses_taskkill_tree_fallback(self):
        process = FakeProcess(timeout_first=True)
        with mock.patch.object(runner.subprocess, "run") as taskkill:
            runner.terminate_process_tree(process, cleanup_grace_seconds=0, is_windows=True)
        taskkill.assert_called_once()
        self.assertEqual(taskkill.call_args.args[0], ["taskkill", "/PID", "4242", "/T", "/F"])

    def test_project_deadline_records_timeout_evidence(self):
        process = FakeProcess(timeout_first=True)
        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(
            runner.os, "getpgid", return_value=process.pid
        ), mock.patch.object(runner.os, "killpg") as killpg:
            result = runner.run_project(
                EXPECTED_SHARDS["shard-01"][0],
                shard_directory=Path(temporary),
                repo_root=ROOT,
                dotnet="dotnet",
                timeout_seconds=0.001,
                cleanup_grace_seconds=0,
                popen_factory=lambda *args, **kwargs: process,
                is_windows=False,
            )
            evidence = json.loads(
                (Path(temporary) / "MeowChan.Tests" / "status.json").read_text()
            )
        self.assertEqual(result.status, "timed_out")
        self.assertEqual(evidence["status"], "timed_out")
        self.assertEqual(evidence["exit_code"], 124)
        self.assertEqual(killpg.call_count, 1)

    def test_run_shard_keeps_projects_sequential_and_records_each_status(self):
        calls = []

        def fake_project(project, **kwargs):
            calls.append(project)
            directory = kwargs["shard_directory"] / Path(project).stem
            result = runner.ProjectResult(
                project=project,
                project_name=Path(project).stem,
                status="passed",
                exit_code=0,
                duration_seconds=0.0,
                timeout_seconds=kwargs["timeout_seconds"],
                results_directory=str(directory),
                log_file=str(directory / "project.log"),
                command=[],
            )
            runner._write_json(directory / "status.json", runner.asdict(result))
            return result

        with tempfile.TemporaryDirectory() as temporary:
            result = runner.run_shard(
                "shard-07",
                EXPECTED_SHARDS["shard-07"],
                results_root=Path(temporary),
                repo_root=ROOT,
                dotnet="dotnet",
                shard_timeout_seconds=10,
                project_timeout_seconds=5,
                cleanup_grace_seconds=0,
                project_runner=fake_project,
                is_windows=False,
            )
            self.assertEqual(calls, EXPECTED_SHARDS["shard-07"])
            self.assertEqual(result.status, "passed")
            self.assertEqual(result.exit_code, 0)
            self.assertTrue((Path(temporary) / "shard-07" / "status.json").is_file())

    def test_aggregate_collects_other_shards_after_one_runner_error(self):
        called = []

        def fake_shard(shard, projects, **kwargs):
            called.append(shard)
            if shard == "shard-03":
                raise RuntimeError("synthetic shard failure")
            return runner.ShardResult(
                shard=shard,
                status="passed",
                exit_code=0,
                duration_seconds=0.0,
                timeout_seconds=kwargs["shard_timeout_seconds"],
            )

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            exit_code = runner.run_all(
                results_root=root,
                repo_root=ROOT,
                dotnet="dotnet",
                shard_timeout_seconds=10,
                light_shard_timeout_seconds=5,
                project_timeout_seconds=10,
                light_project_timeout_seconds=5,
                cleanup_grace_seconds=0,
                shard_runner=fake_shard,
                is_windows=False,
            )
            self.assertEqual(exit_code, 1)
            self.assertEqual(set(called), set(runner.SHARDS))
            aggregate = json.loads((root / "aggregate-status.json").read_text())
            self.assertEqual(aggregate["aggregate_status"], "failed")
            self.assertEqual(len(aggregate["shards"]), 8)
            self.assertEqual(
                next(item for item in aggregate["shards"] if item["shard"] == "shard-03")["status"],
                "runner_error",
            )
            self.assertIn("aggregate_status=failed", (root / "aggregate-status.txt").read_text())

    def test_workflow_uses_python_runner_and_keeps_macos_watchdog_separate(self):
        workflow = (ROOT / ".github" / "workflows" / "dotnet-desktop.yml").read_text()
        start = workflow.index("      - name: Run core and desktop offline test shards")
        end = workflow.index("      - name: Run both complete macOS offline suites", start)
        shard_step = workflow[start:end]
        self.assertIn("python scripts/run-dotnet-shards.py", shard_step)
        self.assertIn("timeout-minutes: 30", shard_step)
        self.assertNotIn("timeout --", shard_step)
        self.assertNotIn("dotnet test MeowChan.sln", shard_step)
        self.assertNotIn("macos-test-watchdog.py", shard_step)
        mac_step = workflow[end : workflow.index("      - name: Check package layout fixtures", end)]
        self.assertIn("python3 scripts/macos-test-watchdog.py", mac_step)
        self.assertIn("--timeout 300 --desktop-timeout 600", mac_step)
        self.assertNotIn("run-dotnet-shards.py", mac_step)


if __name__ == "__main__":
    unittest.main()
