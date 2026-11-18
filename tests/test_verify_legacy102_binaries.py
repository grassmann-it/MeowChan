"""Offline safety and evidence tests for the legacy 1.0.2 binary runner.

These tests never invoke dotnet, restore, a provider, a GUI, or a subprocess
from the runner.  Child-process behavior is covered with mocks only; the real
four-project gate belongs to the parent heavy.lock lane.
"""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-legacy102-binaries.py"
SPEC = importlib.util.spec_from_file_location("verify_legacy102_binaries", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(RUNNER)


SHA = "a" * 40


def make_runtime(root: Path, marker: bytes = b"runtime") -> None:
    root.mkdir()
    (root / "MeowChan.PluginSdk.dll").write_bytes(marker + b" sdk")
    (root / "libmeowchan.dll").write_bytes(marker + b" core")
    (root / "runtimeconfig.json").write_text("{}\n", encoding="utf-8")


class Legacy102RunnerSafetyTests(unittest.TestCase):
    def test_inventory_is_sorted_and_hashes_are_recorded(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "runtime"
            make_runtime(root)
            (root / "nested").mkdir()
            (root / "nested" / "dependency.dll").write_bytes(b"dependency")
            inventory = RUNNER.inventory_tree(root, "fixture")
            self.assertEqual(
                ["MeowChan.PluginSdk.dll", "libmeowchan.dll", "nested/dependency.dll", "runtimeconfig.json"],
                [item["path"] for item in inventory["files"]],
            )
            self.assertEqual(4, inventory["fileCount"])
            self.assertEqual(64, len(inventory["treeSha256"]))

    @unittest.skipUnless(hasattr(os, "symlink"), "symlinks are unavailable")
    def test_input_root_and_nested_symlinks_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            actual = parent / "actual"
            make_runtime(actual)
            alias = parent / "alias"
            alias.symlink_to(actual, target_is_directory=True)
            with self.assertRaisesRegex(RUNNER.ProbeError, "symlink"):
                RUNNER.validate_input_dir(alias, "legacy input")
            nested = actual / "nested-link"
            nested.symlink_to(parent, target_is_directory=True)
            with self.assertRaisesRegex(RUNNER.ProbeError, "symlink"):
                RUNNER.validate_input_dir(actual, "legacy input")

    @unittest.skipUnless(hasattr(os, "mkfifo"), "FIFO is unavailable")
    def test_special_input_files_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "runtime"
            make_runtime(root)
            os.mkfifo(root / "unexpected.pipe")
            with self.assertRaisesRegex(RUNNER.ProbeError, "special file"):
                RUNNER.validate_input_dir(root, "legacy input")

    def test_required_assemblies_and_forbidden_user_store_paths_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            missing = base / "missing"
            missing.mkdir()
            (missing / "libmeowchan.dll").write_bytes(b"core")
            with self.assertRaisesRegex(RUNNER.ProbeError, "MeowChan.PluginSdk.dll"):
                RUNNER.validate_input_dir(missing, "legacy input")
            forbidden = base / ".oChan" / "authstore"
            forbidden.mkdir(parents=True)
            (forbidden / "MeowChan.PluginSdk.dll").write_bytes(b"sdk")
            (forbidden / "libmeowchan.dll").write_bytes(b"core")
            with self.assertRaisesRegex(RUNNER.ProbeError, "database|oChan|authstore"):
                RUNNER.validate_input_dir(forbidden, "legacy input")

    def test_output_must_be_new_and_disjoint_from_both_inputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            legacy, candidate = base / "legacy", base / "candidate"
            make_runtime(legacy, b"legacy")
            make_runtime(candidate, b"candidate")
            with self.assertRaisesRegex(RUNNER.ProbeError, "legacy-dir"):
                RUNNER._ensure_input_roots(legacy, candidate, legacy / "evidence")
            existing = base / "already"
            existing.mkdir()
            with self.assertRaisesRegex(RUNNER.ProbeError, "existing collision"):
                RUNNER._ensure_input_roots(legacy, candidate, existing)

    def test_source_pin_requires_exact_clean_checkout_without_running_shell(self):
        with mock.patch.object(RUNNER, "_run_git", side_effect=[str(ROOT), SHA, ""]) as git:
            self.assertEqual({"commit": SHA, "headExact": True, "clean": True}, RUNNER.verify_source_pin(SHA))
            self.assertEqual([mock.call(["rev-parse", "--show-toplevel"]),
                              mock.call(["rev-parse", "HEAD"]),
                              mock.call(["status", "--porcelain=v1", "--untracked-files=all"])], git.call_args_list)
        with self.assertRaisesRegex(RUNNER.ProbeError, "full lowercase"):
            RUNNER.verify_source_pin("A" * 40)

    def test_source_pin_rejects_dirty_or_different_head(self):
        with mock.patch.object(RUNNER, "_run_git", side_effect=[str(ROOT), "b" * 40]):
            with self.assertRaisesRegex(RUNNER.ProbeError, "not the requested"):
                RUNNER.verify_source_pin(SHA)
        with mock.patch.object(RUNNER, "_run_git", side_effect=[str(ROOT), SHA, " M script.py"]):
            with self.assertRaisesRegex(RUNNER.ProbeError, "dirty"):
                RUNNER.verify_source_pin(SHA)

    def test_private_environment_isolated_and_removes_profile_overrides(self):
        with tempfile.TemporaryDirectory() as temporary:
            with mock.patch.dict(os.environ, {
                "HOME": "/personal", "MEOWCHAN_PROFILE": "/personal/profile",
                "OCHAN_PROFILE": "/personal/profile", "NUGET_FALLBACK_PACKAGES": "/personal/packages",
            }):
                environment = RUNNER._private_environment(Path(temporary) / "sandbox")
            for key in ("HOME", "TMPDIR", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME"):
                self.assertTrue(Path(environment[key]).is_relative_to(Path(temporary)))
            self.assertNotIn("MEOWCHAN_PROFILE", environment)
            self.assertNotIn("OCHAN_PROFILE", environment)
            self.assertNotIn("NUGET_FALLBACK_PACKAGES", environment)

    def test_probe_validation_requires_documented_observations(self):
        plugin_hash = "b" * 64
        sdk = {"probe": "sdk102", "legacySdkVersion": "1.0.0", "candidateSdkVersion": "1.1.0",
               "legacyAssemblySha256": plugin_hash, "legacyLoaded": True,
               "sourceContractOk": True, "jsonRoundTripOk": True}
        self.assertEqual(sdk, RUNNER.validate_sdk_probe(sdk, plugin_hash))
        with self.assertRaisesRegex(RUNNER.ProbeError, "embedded 1.0.0"):
            RUNNER.validate_sdk_probe({**sdk, "legacySdkVersion": "1.1.0"}, plugin_hash)
        rejected = {"probe": "core102", "started": False, "startupAdmissionRejected": True,
                    "exceptionType": "System.InvalidOperationException"}
        self.assertEqual(rejected, RUNNER.validate_core_probe(rejected, "core102", rejected=True, label="mock"))
        with self.assertRaisesRegex(RUNNER.ProbeError, "documented old"):
            RUNNER.validate_core_probe({**rejected, "exceptionType": "Exception"}, "core102", rejected=True, label="mock")

    def test_probe_json_uses_only_final_line_and_rejects_non_object(self):
        self.assertEqual({"probe": "x"}, RUNNER._probe_json("diagnostic\n{\"probe\": \"x\"}\n", "mock"))
        with self.assertRaisesRegex(RUNNER.ProbeError, "not JSON"):
            RUNNER._probe_json("diagnostic\n", "mock")
        with self.assertRaisesRegex(RUNNER.ProbeError, "not an object"):
            RUNNER._probe_json("[]\n", "mock")

    def test_child_command_forwards_argument_list_and_remaining_budget_to_bounded_helper(self):
        with tempfile.TemporaryDirectory() as temporary, \
             mock.patch.object(RUNNER, "run_bounded_command", return_value='{"ok":true}\n') as command:
            root = Path(temporary)
            argv = ["dotnet", "probe.dll", "--profile", "/private/profile"]
            environment = {"HOME": str(root / "home")}
            output = RUNNER.run_command(argv, root, environment, root / "run.log", RUNNER.Deadline(5), "mock")
            self.assertIn("ok", output)
            self.assertEqual(argv, command.call_args.args[0])
            self.assertEqual(environment, command.call_args.args[2])
            self.assertGreater(command.call_args.args[4], 0)
            self.assertLessEqual(command.call_args.args[4], 5)

    def test_bounded_child_failures_map_to_runner_errors(self):
        for child_error, runner_error in (
            (RUNNER.CommandFailed("probe", 7, Path("probe.log"), "tail"), RUNNER.ChildFailed),
            (RUNNER.CommandTimedOut("probe", 1, Path("probe.log"), "tail"), RUNNER.DeadlineExceeded),
            (RUNNER.CommandUnavailable("missing"), RUNNER.UnsupportedEnvironment),
        ):
            with self.subTest(error=type(child_error).__name__), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=child_error):
                with self.assertRaises(runner_error):
                    RUNNER.run_command(["dotnet"], ROOT, {}, Path("unused.log"), RUNNER.Deadline(5), "mock")

    def test_sdk_probe_run_has_no_profile_flag_and_core_run_gets_fresh_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            runtime = base / "runtime"
            build = base / "build"
            make_runtime(runtime)
            build.mkdir()
            (build / "Probe.dll").write_bytes(b"probe")
            (build / "Probe.deps.json").write_text("{}")
            (build / "Probe.runtimeconfig.json").write_text("{}")
            run = base / "run"
            RUNNER._runtime_run_directory(runtime, build, run, "mock runtime", executable_name="Probe.dll")
            fake = {"probe": "mock"}
            with mock.patch.object(RUNNER, "run_command", return_value='{"probe":"mock"}\n') as command:
                result, metadata = RUNNER._run_private_probe(
                    "dotnet", run / "Probe.dll", ["--plugin", "plugin.dll"],
                    run, RUNNER.Deadline(5), "mock SDK", with_profile=False)
            self.assertEqual(fake, result)
            self.assertIsNone(metadata["profileRelative"])
            self.assertNotIn("--profile", command.call_args.args[0])
            self.assertFalse((run / "profile").exists())
            with mock.patch.object(RUNNER, "run_command", return_value='{"probe":"mock"}\n'):
                _, core_metadata = RUNNER._run_private_probe(
                    "dotnet", run / "Probe.dll", [], run, RUNNER.Deadline(5),
                    "mock Core", with_profile=True)
            self.assertEqual("profile", core_metadata["profileRelative"])
            self.assertTrue((run / "profile").is_dir())

    def test_runtime_selects_candidate_not_old_copy_local_references(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            candidate, build, run = base / "candidate", base / "build", base / "run"
            make_runtime(candidate, b"candidate")
            make_runtime(build, b"old-copy-local")
            for name in ("Probe.dll", "Probe.deps.json", "Probe.runtimeconfig.json"):
                (build / name).write_bytes(b"old-executable")
            RUNNER._runtime_run_directory(candidate, build, run, "mixed version", executable_name="Probe.dll")
            self.assertEqual(b"candidate core", (run / "libmeowchan.dll").read_bytes())
            self.assertEqual(b"candidate sdk", (run / "MeowChan.PluginSdk.dll").read_bytes())
            self.assertEqual(b"old-executable", (run / "Probe.dll").read_bytes())
            (candidate / "Probe.dll").write_bytes(b"untrusted collision")
            with self.assertRaisesRegex(RUNNER.ProbeError, "Runtime/probe copy collision"):
                RUNNER._runtime_run_directory(candidate, build, base / "second", "collision", executable_name="Probe.dll")

    def test_actual_probe_projects_match_the_runner_contract(self):
        for spec in RUNNER._stage_specs(Path("/legacy"), Path("/candidate")):
            with self.subTest(project=spec["project"]):
                result = RUNNER.inspect_project(spec["project"], spec["property"], spec["assembly"], spec["name"])
                self.assertEqual(spec["assembly"], result["output"])

    def test_full_mock_orchestration_creates_work_and_checks_versions_and_hashes(self):
        self._exercise_mock_execution()

    def test_mutated_legacy_consumer_does_not_pass(self):
        with self.assertRaisesRegex(RUNNER.ProbeError, "executable bytes changed"):
            self._exercise_mock_execution(mutate="executable")

    def test_mutated_runtime_input_does_not_pass(self):
        with self.assertRaisesRegex(RUNNER.ProbeError, "runtime inputs changed"):
            self._exercise_mock_execution(mutate="input")

    def _exercise_mock_execution(self, mutate=None):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            legacy, candidate, output = base / "legacy", base / "candidate", base / "output"
            make_runtime(legacy, b"legacy")
            make_runtime(candidate, b"candidate")
            output.mkdir()
            args = SimpleNamespace(timeout_seconds=5, heavy_lock=base / "heavy.lock", source_commit=SHA)
            pin = {"commit": SHA, "headExact": True, "clean": True}
            observed = []

            def build(spec, source, unused, build_root, dependencies, deadline, dotnet, label):
                stage = build_root / spec["name"] / "output"
                stage.mkdir(parents=True)
                stem = spec["assembly"][:-4]
                for name in (spec["assembly"], stem + ".deps.json", stem + ".runtimeconfig.json"):
                    (stage / name).write_bytes(spec["name"].encode())
                # The old reference would overwrite the candidate without the
                # executable-only overlay rule.
                (stage / "libmeowchan.dll").write_bytes((source / "libmeowchan.dll").read_bytes())
                return {"assemblyPath": str(stage / spec["assembly"])}

            def run(dotnet, executable, arguments, run_root, deadline, label, *, with_profile):
                observed.append(executable.name)
                expected = b"legacy core" if run_root.name == "core-legacy-baseline" else b"candidate core"
                self.assertEqual(expected, (run_root / "libmeowchan.dll").read_bytes())
                if executable.name == RUNNER.SDK_CANDIDATE_ASSEMBLY:
                    self.assertFalse(with_profile)
                    value = {"probe": "sdk102", "legacySdkVersion": "1.0.0", "candidateSdkVersion": "1.1.0",
                             "legacyAssemblySha256": RUNNER.sha256_file(Path(arguments[1])),
                             "legacyLoaded": True, "sourceContractOk": True, "jsonRoundTripOk": True}
                else:
                    self.assertTrue(with_profile)
                    rejected = "--expect-startup-admission-rejection" in arguments
                    value = {"probe": "core110" if executable.name == RUNNER.CORE_MIGRATED_ASSEMBLY else "core102",
                             "started": not rejected, "startupAdmissionRejected": rejected,
                             "exceptionType": "System.InvalidOperationException" if rejected else None}
                    if executable.name == RUNNER.CORE_MIGRATED_ASSEMBLY:
                        if mutate == "executable":
                            (run_root.parent / "core-legacy-candidate" / RUNNER.CORE_LEGACY_ASSEMBLY).write_bytes(b"changed")
                        elif mutate == "input":
                            (candidate / "libmeowchan.dll").write_bytes(b"changed")
                return value, {"command": [dotnet, str(executable)]}

            with mock.patch.object(RUNNER, "_build_stage", side_effect=build), \
                 mock.patch.object(RUNNER, "_run_private_probe", side_effect=run), \
                 mock.patch.object(RUNNER.shutil, "which", return_value="/usr/bin/dotnet"), \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value=pin):
                report = RUNNER.execute(args, legacy, candidate, output, pin)
            self.assertTrue(report["passed"])
            self.assertTrue(report["inputHashesUnchanged"])
            self.assertTrue(report["observations"]["core"]["sameLegacyExecutableBytes"])
            self.assertEqual([RUNNER.SDK_CANDIDATE_ASSEMBLY, RUNNER.CORE_LEGACY_ASSEMBLY,
                              RUNNER.CORE_LEGACY_ASSEMBLY, RUNNER.CORE_MIGRATED_ASSEMBLY], observed)

    def test_atomic_result_write_never_leaves_temporary_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "RESULT.json"
            RUNNER._atomic_json(path, {"passed": False, "scope": "not release"})
            self.assertEqual({"passed": False, "scope": "not release"}, json.loads(path.read_text()))
            self.assertEqual([], list(Path(temporary).glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
