"""Offline safety checks for the synthetic reauthorization matrix runner.

These tests use stdlib fixtures/fakes only.  They never invoke dotnet, bwrap,
restore, build, testhost, a worker, a provider, or a network namespace.
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-plugin-reauthorization.py"
SPEC = importlib.util.spec_from_file_location("verify_plugin_reauthorization", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(RUNNER)

SHA = "a" * 40
HASH = "b" * 64


class PluginReauthorizationRunnerTests(unittest.TestCase):
    def test_existing_project_contract_contains_real_matrix_dependencies(self):
        contract = RUNNER._project_contract(ROOT)
        self.assertTrue(contract["lockedRestore"])
        self.assertEqual(RUNNER.TEST_FILTER, contract["filter"])
        self.assertEqual(RUNNER.CONFIGURATION, contract["configuration"])
        self.assertIn("../../MeowChan.PluginWorker/MeowChan.PluginWorker.csproj", contract["projectReferences"])
        self.assertIn("../PluginReauthorizationProbe/PluginReauthorizationProbe.csproj", contract["projectReferences"])

    def test_trx_requires_all_fourteen_cases_without_skips(self):
        with tempfile.TemporaryDirectory() as temporary:
            trx = Path(temporary) / "matrix.trx"
            names = []
            for method, count in RUNNER.EXPECTED_METHODS.items():
                for index in range(count):
                    names.append(f"{RUNNER.TEST_CLASS}.{method}(case: {index})")
            results = "".join(
                f'<UnitTestResult testName="{name}" outcome="Passed" />' for name in names
            )
            trx.write_text(
                '<TestRun><ResultSummary><Counters total="14" passed="14" failed="0" skipped="0" /></ResultSummary>'
                f"<Results>{results}</Results></TestRun>",
                encoding="utf-8",
            )
            result = RUNNER.validate_trx(trx)
            self.assertEqual(14, result["total"])
            self.assertEqual(RUNNER.EXPECTED_METHODS, result["methods"])

            skipped = trx.read_text(encoding="utf-8").replace('skipped="0"', 'skipped="1"', 1)
            trx.write_text(skipped, encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.ProbeError, "skipped"):
                RUNNER.validate_trx(trx)

    def test_process_evidence_requires_all_persistent_phase_and_worker_records(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary) / "process-evidence"
            parent.mkdir()
            sequences = (
                [("seed", "stage-B", "review", "consent-no-rights", "start-B", "consent",
                  "start-B", "start-B", "stage-C", "consent", "start-C")] * 4
                + [("seed", "stage-B", "review", "consent-forged", "start-B")] * 2
                + [("seed", "stage-B", "review", "consent-cancel", "start-B")] * 2
                + [("seed", "stage-B", "review", "consent-missing", "start-B")] * 2
                + [("seed", "stage-B", "review", "bump-grant", "consent", "start-B")] * 2
                + [("seed", "stage-B", "review", "consent", "delete-target", "start-B")] * 2
            )
            for index, sequence in enumerate(sequences):
                root = parent / f"reauth-process-{index:02d}"
                root.mkdir()
                for number, phase in enumerate(sequence, 1):
                    prefix = f"{number:02d}-{phase}"
                    (root / f"{prefix}.pid").write_text(str(1000 + index), encoding="utf-8")
                    (root / f"{prefix}.stderr").write_text("", encoding="utf-8")
                    value = {
                        "Pid": 2000 + index,
                        "SourceSha": SHA,
                        "Phase": phase,
                        "HttpRequests": 0,
                        "StoreHash": HASH,
                        "TargetExecution": HASH,
                        "TargetAssemblyHash": HASH,
                        "OldExecution": HASH,
                        "ProbeHash": HASH,
                        "WorkerHash": HASH,
                        "WorkerPid": None,
                    }
                    if phase.startswith("start-") and index % 2 == 0:
                        value["WorkerPid"] = 3000 + index
                        (root / f"{phase}.worker-pid").write_text(str(value["WorkerPid"]), encoding="utf-8")
                    (root / f"{prefix}.json").write_text(json.dumps(value), encoding="utf-8")
            result = RUNNER.validate_process_evidence(parent, SHA, HASH, HASH)
            self.assertEqual(RUNNER.EXPECTED_MATRIX_CASES, result["matrixRoots"])
            self.assertEqual(RUNNER.EXPECTED_PROCESS_INVOCATIONS, result["processInvocations"])
            self.assertGreater(result["workerEvidenceFiles"], 0)

            missing = parent / "reauth-process-00" / "01-seed.stderr"
            missing.unlink()
            with self.assertRaisesRegex(RUNNER.ProbeError, "stderr"):
                RUNNER.validate_process_evidence(parent, SHA, HASH, HASH)

    def test_network_namespace_and_environment_are_explicitly_private(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            evidence = root / "evidence"
            work = evidence / "work"
            work.mkdir(parents=True)
            feed = root / "feed"
            feed.mkdir()
            environment = RUNNER._private_environment(work, evidence, SHA, "/usr/share/dotnet/dotnet", work / "packages")
            with mock.patch.dict(RUNNER.os.environ, {
                "DISPLAY": ":99", "MEOWCHAN_PROFILE": "/personal/profile",
                "OCHAN_AUTHSTORE": "/personal/authstore", "NUGET_FALLBACK_PACKAGES": "/personal/packages",
            }):
                environment = RUNNER._private_environment(work, evidence, SHA, "/usr/share/dotnet/dotnet", work / "packages-2")
            command = RUNNER._bwrap_command(
                ["/usr/share/dotnet/dotnet", "test"], work, evidence, feed, work,
                environment, "/usr/bin/bwrap",
            )
            self.assertIn("--unshare-net", command)
            self.assertIn("--clearenv", command)
            self.assertNotIn("DISPLAY", environment)
            self.assertNotIn("MEOWCHAN_PROFILE", environment)
            self.assertNotIn("OCHAN_AUTHSTORE", environment)
            self.assertTrue(Path(environment["HOME"]).is_relative_to(evidence))
            self.assertTrue(Path(environment["TMPDIR"]).is_relative_to(evidence))
            self.assertEqual(SHA, environment["OCHAN_TEST_SOURCE_SHA"])

    def test_fresh_mock_execution_pins_artifacts_and_uses_no_real_child(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            evidence = base / "evidence"
            evidence.mkdir()
            feed = base / "feed"
            feed.mkdir()
            args = SimpleNamespace(timeout_seconds=30, source_commit=SHA)
            source_pin = {"commit": SHA, "headExact": True, "clean": True}
            paths = {
                "test": evidence / "test-output",
                "worker": evidence / "worker-output",
                "probe": evidence / "probe-output",
            }
            for directory in paths.values():
                directory.mkdir()
            required = {
                "test": ("MeowChan.PluginRuntime.Tests.dll", "MeowChan.PluginRuntime.Tests.deps.json"),
                "worker": ("MeowChan.PluginWorker.dll", "MeowChan.PluginWorker.deps.json", "MeowChan.PluginWorker.runtimeconfig.json"),
                "probe": ("PluginReauthorizationProbe.dll", "PluginReauthorizationProbe.deps.json", "PluginReauthorizationProbe.runtimeconfig.json"),
            }
            for label, names in required.items():
                for name in names:
                    (paths[label] / name).write_bytes(f"{label}:{name}".encode())
            trx = evidence / "fake.trx"
            names = [
                f"{RUNNER.TEST_CLASS}.{method}(case: {index})"
                for method, count in RUNNER.EXPECTED_METHODS.items()
                for index in range(count)
            ]
            trx.write_text(
                '<TestRun><ResultSummary><Counters total="14" passed="14" failed="0" skipped="0" /></ResultSummary>'
                + "<Results>"
                + "".join(f'<UnitTestResult testName="{name}" outcome="Passed" />' for name in names)
                + "</Results></TestRun>", encoding="utf-8",
            )
            fake_build = {
                "project": str(evidence / "source/tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj"),
                "projectReferences": [], "lockedRestore": True, "filter": RUNNER.TEST_FILTER,
                "configuration": RUNNER.CONFIGURATION, "sourceStage": str(evidence / "source"),
                "outputPaths": {key: str(value) for key, value in paths.items()},
            }
            fake_build["pins"] = {
                "directories": {label: RUNNER.inventory_tree(path, label) for label, path in paths.items()},
                "required": {label: {name: {"path": str(path / name), "sha256": RUNNER.sha256_file(path / name),
                                               "bytes": (path / name).stat().st_size} for name in names}
                             for label, path in paths.items() for names in [required[label]]},
            }
            fake_test = {"command": ["bwrap", "--unshare-net", "dotnet", "test"],
                         "stdoutTailSha256": HASH, "trx": str(trx)}
            with mock.patch.object(RUNNER, "_dotnet_host", return_value="/usr/share/dotnet/dotnet"), \
                 mock.patch.object(RUNNER, "_bwrap_host", return_value="/usr/bin/bwrap"), \
                 mock.patch.object(RUNNER, "copy_source_stage", return_value=evidence / "source"), \
                 mock.patch.object(RUNNER, "_private_environment", return_value={"HOME": str(evidence / "private")}), \
                 mock.patch.object(RUNNER, "build_fresh", return_value=fake_build) as build, \
                 mock.patch.object(RUNNER, "run_filtered_test", return_value=fake_test) as test, \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value=source_pin), \
                 mock.patch.object(RUNNER, "validate_process_evidence", return_value={"persistent": True}):
                result = RUNNER.execute(args, evidence, feed, source_pin, {})
            self.assertTrue(result["artifactsUnchanged"])
            build.assert_called_once()
            test.assert_called_once()
            self.assertEqual(source_pin, result["sourceAfter"])

    def test_timeout_and_lock_wait_limits_are_rejected_before_side_effects(self):
        common = [
            "--source-commit", SHA, "--evidence-dir", "/tmp/evidence",
            "--nuget-feed", "/tmp/feed",
        ]
        for flag, value in (
            ("--timeout-seconds", RUNNER.MAX_TIMEOUT_SECONDS + 1),
            ("--lock-wait-seconds", RUNNER.MAX_LOCK_WAIT_SECONDS + 1),
        ):
            with self.subTest(flag=flag), self.assertRaises(SystemExit) as raised:
                RUNNER.main([*common, flag, str(value)])
            self.assertEqual(2, raised.exception.code)


if __name__ == "__main__":
    unittest.main()
