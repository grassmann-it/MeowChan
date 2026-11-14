"""Offline safety and mock-flow coverage for the legacy Host/Worker runner.

These tests never invoke dotnet, restore, a provider, a GUI, or a real worker.
The parent heavy.lock lane owns the actual compile and CLI execution.
"""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-legacy102-hostworker.py"
SPEC = importlib.util.spec_from_file_location("verify_legacy102_hostworker", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(RUNNER)

SHA = "a" * 40
PLUGIN_BYTES = b"unchanged-prebuilt-legacy-plugin"


def make_runtime(root: Path, marker: bytes = b"candidate") -> None:
    root.mkdir()
    (root / RUNNER.HOST_ASSEMBLY).write_bytes(marker + b" host")
    (root / RUNNER.SDK_ASSEMBLY).write_bytes(marker + b" sdk")
    (root / RUNNER.CORE_ASSEMBLY).write_bytes(marker + b" core")
    (root / "native-helper.so").write_bytes(marker + b" native")


def make_worker(root: Path) -> None:
    root.mkdir()
    (root / RUNNER.WORKER_ASSEMBLY).write_bytes(b"current worker")
    (root / "MeowChan.PluginSdk.dll").write_bytes(b"worker sdk")
    (root / "MeowChan.PluginWorker.runtimeconfig.json").write_text("{}\n", encoding="utf-8")


def valid_result(plugin_hash: str) -> dict:
    return {
        "scope": "legacy102-hostworker",
        "passed": True,
        "legacyPluginSha256": plugin_hash,
        "declaredSdkVersion": "1.0.0",
        "workerProcessObserved": True,
        "describeOk": True,
        "sourceCallsOk": True,
    }


class Legacy102HostWorkerRunnerTests(unittest.TestCase):
    def test_project_is_local_reference_only_and_uses_both_runtime_properties(self):
        info = RUNNER.inspect_project(RUNNER.PROBE_PROJECT)
        self.assertEqual(RUNNER.PROBE_ASSEMBLY, info["output"])
        self.assertFalse(info["projectReferences"])
        self.assertFalse(info["packageReferences"])
        self.assertTrue(info["frameworkRestore"])

    def test_hostworker_json_requires_exact_sdk_hash_and_worker_observation(self):
        plugin_hash = "b" * 64
        value = valid_result(plugin_hash)
        self.assertEqual(value, RUNNER.validate_hostworker_probe(value, plugin_hash))
        mutations = (
            ("declaredSdkVersion", "1.1.0", "declared SDK version"),
            ("legacyPluginSha256", "c" * 64, "legacy plugin hash"),
            ("workerProcessObserved", False, "workerProcessObserved"),
        )
        for field, replacement, message in mutations:
            with self.subTest(field=field):
                with self.assertRaisesRegex(RUNNER.ProbeError, message):
                    RUNNER.validate_hostworker_probe({**value, field: replacement}, plugin_hash)

    def test_input_and_runtime_copy_collisions_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            plugin = base / "legacy.dll"
            plugin.write_bytes(PLUGIN_BYTES)
            candidate = base / "candidate"
            worker = base / "worker"
            make_runtime(candidate)
            make_worker(worker)

            with self.assertRaisesRegex(RUNNER.ProbeError, "output.*overlaps"):
                RUNNER._ensure_input_roots(plugin, candidate, worker, candidate / "evidence")
            with self.assertRaisesRegex(RUNNER.ProbeError, "disjoint"):
                RUNNER._ensure_input_roots(plugin, candidate, candidate, base / "evidence")

            candidate_with_probe = base / "candidate-with-probe"
            make_runtime(candidate_with_probe)
            (candidate_with_probe / RUNNER.PROBE_ASSEMBLY).write_bytes(b"collision")
            build = base / "build"
            build.mkdir()
            for name in (
                RUNNER.PROBE_ASSEMBLY,
                "MeowChan.PluginHost.Legacy102Probe.deps.json",
                "MeowChan.PluginHost.Legacy102Probe.runtimeconfig.json",
            ):
                (build / name).write_bytes(b"probe")
            with self.assertRaisesRegex(RUNNER.ProbeError, "copy collision"):
                RUNNER._stage_probe_runtime(candidate_with_probe, build, base / "run")

    def test_full_mock_orchestration_stages_worker_and_uses_bounded_command(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            plugin = base / "legacy.dll"
            plugin.write_bytes(PLUGIN_BYTES)
            candidate = base / "candidate"
            worker_dir = base / "worker"
            make_runtime(candidate)
            make_worker(worker_dir)
            output = base / "evidence"
            plugin, candidate, worker_dir, output = RUNNER._ensure_input_roots(
                plugin, candidate, worker_dir, output)
            args = SimpleNamespace(timeout_seconds=30, source_commit=SHA)
            pin = {"commit": SHA, "headExact": True, "clean": True}
            observed = {}

            def fake_build(candidate_input, evidence, deadline, dotnet):
                self.assertEqual(candidate, candidate_input)
                self.assertEqual("/usr/bin/dotnet", dotnet)
                build_output = evidence / "mock-build-output"
                build_output.mkdir()
                for name in (
                    RUNNER.PROBE_ASSEMBLY,
                    "MeowChan.PluginHost.Legacy102Probe.deps.json",
                    "MeowChan.PluginHost.Legacy102Probe.runtimeconfig.json",
                ):
                    (build_output / name).write_bytes(b"probe artifact " + name.encode())
                return {"assemblyPath": str(build_output / RUNNER.PROBE_ASSEMBLY), "mock": True}

            def fake_bounded(command, cwd, environment, log, timeout, label):
                observed["command"] = command
                observed["cwd"] = cwd
                observed["environment"] = environment
                observed["log"] = log
                observed["timeout"] = timeout
                self.assertEqual("legacy102 Host/Worker probe", label)
                self.assertEqual(command[0], "/usr/bin/dotnet")
                worker_argument = Path(command[command.index("--worker") + 1])
                profile_argument = Path(command[command.index("--profile") + 1])
                self.assertEqual(b"current worker", worker_argument.read_bytes())
                self.assertNotEqual(worker_dir / RUNNER.WORKER_ASSEMBLY, worker_argument)
                self.assertTrue(profile_argument.is_relative_to(Path(tempfile.gettempdir())))
                self.assertFalse(profile_argument.is_relative_to(output))
                self.assertNotIn("MEOWCHAN_PROFILE", environment)
                self.assertNotIn("OCHAN_AUTHSTORE", environment)
                self.assertTrue(Path(environment["HOME"]).is_relative_to(Path(tempfile.gettempdir())))
                self.assertTrue(Path(environment["TMPDIR"]).is_relative_to(Path(tempfile.gettempdir())))
                self.assertTrue(Path(environment["XDG_CONFIG_HOME"]).is_relative_to(Path(tempfile.gettempdir())))
                log.write_text("diagnostic\n" + json.dumps(valid_result(RUNNER.sha256_file(plugin))) + "\n",
                               encoding="utf-8")
                return json.dumps(valid_result(RUNNER.sha256_file(plugin))) + "\n"

            with mock.patch.object(RUNNER, "_dotnet_executable", return_value="/usr/bin/dotnet"), \
                 mock.patch.object(RUNNER, "_build_probe", side_effect=fake_build), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=fake_bounded), \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value=pin):
                result = RUNNER.execute(args, plugin, candidate, worker_dir, output, pin)

            self.assertTrue(result["passed"])
            self.assertTrue(result["inputHashesUnchanged"])
            self.assertTrue(result["inputs"]["legacyPlugin"]["prebuilt"])
            self.assertEqual(candidate, Path(result["inputs"]["candidateRuntime"]["root"]))
            self.assertEqual(worker_dir, Path(result["inputs"]["workerDir"]["root"]))
            self.assertLess(observed["timeout"], args.timeout_seconds)
            self.assertTrue((output / "hostworker-probe.log").is_file())

    def test_mutating_candidate_input_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            plugin = base / "legacy.dll"
            plugin.write_bytes(PLUGIN_BYTES)
            candidate = base / "candidate"
            worker_dir = base / "worker"
            make_runtime(candidate)
            make_worker(worker_dir)
            output = base / "evidence"
            plugin, candidate, worker_dir, output = RUNNER._ensure_input_roots(
                plugin, candidate, worker_dir, output)
            args = SimpleNamespace(timeout_seconds=30, source_commit=SHA)
            pin = {"commit": SHA, "headExact": True, "clean": True}

            def fake_build(candidate_input, evidence, deadline, dotnet):
                build_output = evidence / "mock-build-output"
                build_output.mkdir()
                for name in (
                    RUNNER.PROBE_ASSEMBLY,
                    "MeowChan.PluginHost.Legacy102Probe.deps.json",
                    "MeowChan.PluginHost.Legacy102Probe.runtimeconfig.json",
                ):
                    (build_output / name).write_bytes(b"probe")
                return {"assemblyPath": str(build_output / RUNNER.PROBE_ASSEMBLY)}

            def fake_bounded(command, cwd, environment, log, timeout, label):
                (candidate / RUNNER.SDK_ASSEMBLY).write_bytes(b"mutated")
                return json.dumps(valid_result(RUNNER.sha256_file(plugin))) + "\n"

            with mock.patch.object(RUNNER, "_dotnet_executable", return_value="/usr/bin/dotnet"), \
                 mock.patch.object(RUNNER, "_build_probe", side_effect=fake_build), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=fake_bounded), \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value=pin):
                with self.assertRaisesRegex(RUNNER.ProbeError, "inputs changed"):
                    RUNNER.execute(args, plugin, candidate, worker_dir, output, pin)

    def test_private_environment_does_not_forward_profile_store_variables(self):
        with tempfile.TemporaryDirectory() as temporary:
            with mock.patch.dict(os.environ, {
                "HOME": "/personal", "MEOWCHAN_PROFILE": "/personal/profile",
                "OCHAN_PROFILE": "/personal/profile", "OCHAN_AUTHSTORE": "/personal/authstore",
                "NUGET_FALLBACK_PACKAGES": "/personal/packages",
            }):
                environment = RUNNER._private_environment(Path(temporary) / "sandbox")
            for key in ("HOME", "TMPDIR", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME"):
                self.assertTrue(Path(environment[key]).is_relative_to(Path(temporary)))
            self.assertNotIn("MEOWCHAN_PROFILE", environment)
            self.assertNotIn("OCHAN_PROFILE", environment)
            self.assertNotIn("OCHAN_AUTHSTORE", environment)
            self.assertNotIn("NUGET_FALLBACK_PACKAGES", environment)

    def test_timeout_parser_rejects_values_outside_parent_limit(self):
        arguments = [
            "--legacy-plugin", "/tmp/legacy.dll", "--candidate-runtime", "/tmp/candidate",
            "--worker-dir", "/tmp/worker", "--source-commit", SHA,
            "--output", "/tmp/output",
        ]
        for invalid in (0, RUNNER.MAX_TIMEOUT_SECONDS + 1):
            with self.subTest(timeout=invalid), self.assertRaises(SystemExit) as raised:
                RUNNER.main([*arguments, "--timeout-seconds", str(invalid)])
            self.assertEqual(2, raised.exception.code)


if __name__ == "__main__":
    unittest.main()
