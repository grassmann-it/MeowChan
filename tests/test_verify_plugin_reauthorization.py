"""Offline safety checks for the synthetic reauthorization matrix runner.

These tests use stdlib fixtures/fakes only.  They never invoke dotnet, bwrap,
restore, build, testhost, a worker, a provider, or a network namespace.
"""
from __future__ import annotations

from contextlib import contextmanager, redirect_stderr, redirect_stdout
import importlib.util
import io
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
PROBE_HASH = "c" * 64
WORKER_HASH = "d" * 64


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def monotonic(self):
        return self.now

    def advance(self, seconds):
        self.now += seconds


class PluginReauthorizationRunnerTests(unittest.TestCase):
    def setUp(self):
        # Even a broken mock arrangement must never start a real runner child.
        guard = mock.patch.object(RUNNER, "run_bounded_command", side_effect=AssertionError("real child forbidden"))
        guard.start()
        self.addCleanup(guard.stop)

    @staticmethod
    def matrix_xml():
        names = [
            f"{RUNNER.TEST_CLASS}.{method}(case: {index})"
            for method, count in RUNNER.EXPECTED_METHODS.items()
            for index in range(count)
        ]
        return (
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '<ResultSummary outcome="Completed"><Counters total="14" executed="14" passed="14" failed="0" '
            'skipped="0" error="0" timeout="0" aborted="0" notExecuted="0" /></ResultSummary><Results>'
            + "".join(f'<UnitTestResult testName="{name}" outcome="Passed" />' for name in names)
            + '</Results></TestRun>'
        )

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
                '<TestRun><ResultSummary outcome="Completed"><Counters total="14" passed="14" failed="0" skipped="0" /></ResultSummary>'
                f"<Results>{results}</Results></TestRun>",
                encoding="utf-8",
            )
            result = RUNNER.validate_trx(trx, RUNNER.Deadline(30))
            self.assertEqual(14, result["total"])
            self.assertEqual(RUNNER.EXPECTED_METHODS, result["methods"])

            skipped = trx.read_text(encoding="utf-8").replace('skipped="0"', 'skipped="1"', 1)
            trx.write_text(skipped, encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.ProbeError, "skipped"):
                RUNNER.validate_trx(trx, RUNNER.Deadline(30))

    def test_trx_summary_errors_cannot_hide_behind_fourteen_passed_cases(self):
        valid = self.matrix_xml()
        mutations = {
            "aborted": valid.replace('outcome="Completed"', 'outcome="Aborted"', 1),
            "failed": valid.replace('outcome="Completed"', 'outcome="Failed"', 1),
            "error": valid.replace('outcome="Completed"', 'outcome="Error"', 1),
            "missing outcome": valid.replace(' outcome="Completed"', '', 1),
            "missing summary": valid.replace('ResultSummary', 'NotASummary'),
            "duplicate summary": valid.replace('</ResultSummary>', '</ResultSummary><ResultSummary outcome="Completed" />'),
            "duplicate counters": valid.replace('</ResultSummary>', '<Counters total="14" passed="14" failed="0" /></ResultSummary>'),
            "misplaced counters": valid.replace('<ResultSummary outcome="Completed">', '<ResultSummary outcome="Completed" /><Unrelated>')
                                       .replace('</ResultSummary>', '</Unrelated>'),
            "run error": valid.replace('</ResultSummary>', '<RunInfos><RunInfo outcome="Error"><Text>host crash</Text></RunInfo></RunInfos></ResultSummary>'),
            "missing run outcome": valid.replace('</ResultSummary>', '<RunInfos><RunInfo /></RunInfos></ResultSummary>'),
            "error details": valid.replace('</ResultSummary>', '<Output><ErrorInfo><Message>aborted</Message></ErrorInfo></Output></ResultSummary>'),
            "malformed XML": valid[:-1],
        }
        for counter in ("aborted", "error", "timeout", "skipped", "notExecuted"):
            mutations[counter + " counter"] = valid.replace(f'{counter}="0"', f'{counter}="1"')
        mutations["executed mismatch"] = valid.replace('executed="14"', 'executed="13"')
        mutations["malformed counter"] = valid.replace('error="0"', 'error="NaN"')
        with tempfile.TemporaryDirectory() as temporary:
            trx = Path(temporary) / "matrix.trx"
            trx.write_text(valid, encoding="utf-8")
            self.assertEqual(14, RUNNER.validate_trx(trx, RUNNER.Deadline(30))["passed"])
            for label, text in mutations.items():
                with self.subTest(label=label):
                    trx.write_text(text, encoding="utf-8")
                    with self.assertRaises(RUNNER.ProbeError):
                        RUNNER.validate_trx(trx, RUNNER.Deadline(30))

    def test_trx_exact_method_counts_unique_cases_and_pass_outcomes_are_preserved(self):
        valid = self.matrix_xml()
        first, second = list(RUNNER.EXPECTED_METHODS)[:2]
        mutations = {
            "duplicate case": valid.replace(f'{first}(case: 1)', f'{first}(case: 0)'),
            "wrong distribution": valid.replace(f'{first}(case: 0)', f'{second}(case: 99)'),
            "foreign method": valid.replace(f'{first}(case: 0)', 'UnexpectedMethod(case: 0)'),
            "foreign class": valid.replace(RUNNER.TEST_CLASS, 'UnrelatedClass', 1),
            "skipped result": valid.replace('outcome="Passed"', 'outcome="NotExecuted"', 1),
            "failed result": valid.replace('outcome="Passed"', 'outcome="Failed"', 1),
            "missing result": valid.replace(f'<UnitTestResult testName="{RUNNER.TEST_CLASS}.{first}(case: 0)" outcome="Passed" />', ''),
        }
        with tempfile.TemporaryDirectory() as temporary:
            trx = Path(temporary) / "matrix.trx"
            for label, text in mutations.items():
                with self.subTest(label=label):
                    trx.write_text(text, encoding="utf-8")
                    with self.assertRaises(RUNNER.ProbeError):
                        RUNNER.validate_trx(trx, RUNNER.Deadline(30))

    @staticmethod
    def write_process_fixture(parent):
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
                host_pid = 10000 + index * 100 + number
                (root / f"{prefix}.pid").write_text(str(host_pid), encoding="utf-8")
                (root / f"{prefix}.stderr").write_text("", encoding="utf-8")
                value = {
                    "Pid": host_pid, "Sequence": number, "SourceSha": SHA, "Phase": phase,
                    "HttpRequests": 0, "StoreHash": HASH, "TargetExecution": HASH,
                    "TargetAssemblyHash": HASH, "OldExecution": HASH,
                    "ProbeHash": PROBE_HASH, "WorkerHash": WORKER_HASH, "WorkerPid": None,
                }
                # The two enabled success cases have an early denied start-B,
                # then two fresh worker processes. Disabled/invalid cases have none.
                if index < 2 and number in (7, 8):
                    value["WorkerPid"] = 20000 + index * 100 + number
                    (root / f"{prefix}.worker-pid").write_text(str(value["WorkerPid"]), encoding="utf-8")
                (root / f"{prefix}.json").write_text(json.dumps(value), encoding="utf-8")

    def test_process_evidence_requires_all_persistent_phase_and_worker_records(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary) / "process-evidence"
            self.write_process_fixture(parent)
            result = RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))
            self.assertEqual(14, result["matrixRoots"])
            self.assertEqual(98, result["processInvocations"])
            self.assertEqual(4, result["workerEvidenceFiles"])
            root = parent / "reauth-process-00"
            self.assertFalse((root / "05-start-B.worker-pid").exists())
            first = json.loads((root / "07-start-B.json").read_text())
            second = json.loads((root / "08-start-B.json").read_text())
            self.assertNotEqual(first["Pid"], second["Pid"])
            self.assertNotEqual(first["WorkerPid"], second["WorkerPid"])
            for number, value in ((7, first), (8, second)):
                self.assertEqual(number, value["Sequence"])
                self.assertEqual(str(value["WorkerPid"]), (root / f"{number:02d}-start-B.worker-pid").read_text())
            (root / "01-seed.stderr").unlink()
            with self.assertRaisesRegex(RUNNER.ProbeError, "stderr"):
                RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))

    def test_process_json_is_bound_to_exact_invocation_pid_phase_hash_and_source(self):
        mutations = (
            ("Pid", 99999, "PID"), ("Pid", True, "integer"),
            ("Sequence", 8, "sequence"), ("Sequence", True, "integer"),
            ("Phase", "start-C", "phase"), ("SourceSha", "e" * 40, "source SHA"),
            ("ProbeHash", WORKER_HASH, "artifact hash"), ("WorkerHash", PROBE_HASH, "artifact hash"),
            ("HttpRequests", 1, "HTTP"), ("StoreHash", "not-a-hash", "SHA-like"),
        )
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary) / "process-evidence"
            self.write_process_fixture(parent)
            path = parent / "reauth-process-00" / "07-start-B.json"
            original = path.read_text(encoding="utf-8")
            for field, invalid, message in mutations:
                with self.subTest(field=field, invalid=invalid):
                    value = json.loads(original)
                    value[field] = invalid
                    path.write_text(json.dumps(value), encoding="utf-8")
                    with self.assertRaisesRegex(RUNNER.ProbeError, message):
                        RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))
            path.write_text(original, encoding="utf-8")
            # Same-named phase, but a different invocation: copying the entire
            # previous JSON must not turn a reused process into a fresh restart.
            (path.parent / "08-start-B.json").write_text(original, encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.ProbeError, "sequence"):
                RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))

    def test_fresh_invocations_cannot_reuse_host_or_worker_pid(self):
        for role in ("host", "worker", "worker-is-host", "host-was-worker"):
            with self.subTest(role=role), tempfile.TemporaryDirectory() as temporary:
                parent = Path(temporary) / "process-evidence"
                self.write_process_fixture(parent)
                root = parent / "reauth-process-00"
                first = json.loads((root / "07-start-B.json").read_text())
                path = root / "08-start-B.json"
                second = json.loads(path.read_text())
                if role in ("host", "host-was-worker"):
                    second["Pid"] = first["Pid"] if role == "host" else first["WorkerPid"]
                    (root / "08-start-B.pid").write_text(str(second["Pid"]))
                else:
                    second["WorkerPid"] = first["WorkerPid"] if role == "worker" else second["Pid"]
                    (root / "08-start-B.worker-pid").write_text(str(second["WorkerPid"]))
                path.write_text(json.dumps(second))
                with self.assertRaisesRegex(RUNNER.ProbeError, "PID.*reused|reused.*PID"):
                    RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))

    def test_worker_pid_evidence_cannot_be_missing_swapped_legacy_or_aliased(self):
        for mutation in ("missing", "wrong", "swapped", "legacy-only", "legacy-extra", "unknown-sequence",
                         "non-worker", "symlink"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                parent = Path(temporary) / "process-evidence"
                self.write_process_fixture(parent)
                root = parent / "reauth-process-00"
                first, second = root / "07-start-B.worker-pid", root / "08-start-B.worker-pid"
                if mutation == "missing":
                    first.unlink()
                elif mutation == "wrong":
                    first.write_text("99999")
                elif mutation == "swapped":
                    a, b = first.read_text(), second.read_text()
                    first.write_text(b)
                    second.write_text(a)
                elif mutation == "legacy-only":
                    first.rename(root / "start-B.worker-pid")
                elif mutation == "legacy-extra":
                    (root / "start-B.worker-pid").write_text(second.read_text())
                elif mutation == "unknown-sequence":
                    (root / "99-start-B.worker-pid").write_text(second.read_text())
                elif mutation == "non-worker":
                    (root / "05-start-B.worker-pid").write_text(first.read_text())
                else:
                    first.unlink()
                    first.symlink_to(second)
                with self.assertRaises(RUNNER.ProbeError):
                    RUNNER.validate_process_evidence(parent, SHA, PROBE_HASH, WORKER_HASH, RUNNER.Deadline(30))

    def test_csharp_parent_and_producer_share_the_sequenced_invocation_contract(self):
        # Source-contract check only, not a C# compile/runtime claim.
        parent = (ROOT / "tests/MeowChan.PluginRuntime.Tests/PluginReauthorizationProcessTests.cs").read_text()
        producer = (ROOT / "tests/PluginReauthorizationProbe/Program.cs").read_text()
        self.assertIn('invocation.ToString(CultureInfo.InvariantCulture)', parent)
        self.assertIn('int.Parse(args[3], CultureInfo.InvariantCulture)', producer)
        self.assertIn('bool.Parse(args[4]), enabled = bool.Parse(args[5])', producer)
        self.assertIn('$"{invocation:D2}-{phase}"', parent)
        self.assertIn('$"{sequence:D2}-{phase}.worker-pid"', producer)
        self.assertIn('FileMode.CreateNew', producer)
        self.assertIn('Sequence = sequence', producer)
        self.assertIn('Assert.Equal(invocation, result.Sequence)', parent)
        self.assertIn('Assert.Equal(phase, result.Phase)', parent)
        self.assertIn('ProbeResult(int Pid, int Sequence, string Phase,', parent)
        self.assertIn('string workerEvidence = prefix + ".worker-pid"', parent)
        self.assertNotIn('phase + ".worker-pid"', producer)

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
                '<TestRun><ResultSummary outcome="Completed"><Counters total="14" passed="14" failed="0" skipped="0" /></ResultSummary>'
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
                "directories": {label: RUNNER.inventory_tree(path, label, RUNNER.Deadline(30)) for label, path in paths.items()},
                "required": {label: {name: {"path": str(path / name), "sha256": RUNNER.sha256_file(path / name, RUNNER.Deadline(30)),
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
                deadline = RUNNER.Deadline(30)
                with mock.patch.object(RUNNER, "Deadline", side_effect=AssertionError("deadline restart")):
                    result = RUNNER.execute(args, evidence, feed, source_pin, {}, deadline)
            self.assertTrue(result["artifactsUnchanged"])
            build.assert_called_once()
            test.assert_called_once()
            self.assertIs(deadline, build.call_args.args[-1])
            self.assertIs(deadline, test.call_args.args[-1])
            self.assertEqual(source_pin, result["sourceAfter"])

    def test_missing_or_relative_heavy_lock_is_rejected_before_any_side_effect(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            evidence = root / "must-not-exist"
            common = ["--source-commit", SHA, "--evidence-dir", str(evidence), "--nuget-feed", str(root / "feed")]
            for env, flags in (
                ({}, []),
                ({"OCHAN_HEAVY_LOCK": ""}, []),
                ({"OCHAN_HEAVY_LOCK": "relative.lock"}, []),
                ({}, ["--heavy-lock", "relative.lock"]),
            ):
                with self.subTest(env=env, flags=flags), mock.patch.dict(RUNNER.os.environ, env, clear=True), \
                     mock.patch.object(RUNNER, "_ensure_evidence") as ensure, \
                     mock.patch.object(RUNNER, "inventory_tree") as inventory, \
                     mock.patch.object(RUNNER, "acquire_lock") as lock, \
                     redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
                    RUNNER.main([*common, *flags])
                self.assertEqual(2, raised.exception.code)
                ensure.assert_not_called()
                inventory.assert_not_called()
                lock.assert_not_called()
                self.assertFalse(evidence.exists())

    def test_explicit_lock_or_environment_selects_shared_lock_without_username_default(self):
        common = ["--source-commit", SHA, "--evidence-dir", "/tmp/evidence", "--nuget-feed", "/tmp/feed"]
        with mock.patch.dict(RUNNER.os.environ, {}, clear=True):
            self.assertIsNone(RUNNER._parser().parse_args(common).heavy_lock)
        with mock.patch.dict(RUNNER.os.environ, {"OCHAN_HEAVY_LOCK": "/shared/parent/heavy.lock"}):
            self.assertEqual(Path("/shared/parent/heavy.lock"), RUNNER._parser().parse_args(common).heavy_lock)
            self.assertEqual(Path("/shared/explicit.lock"), RUNNER._parser().parse_args([
                *common, "--heavy-lock", "/shared/explicit.lock",
            ]).heavy_lock)

    def test_main_uses_one_budget_including_preflight_lock_and_both_inventories(self):
        # Every fake advances finite virtual time. Timeouts must still write a
        # fail-closed RESULT without restarting work during failure inventory.
        for expire_at in (None, "before", "source-before", "lock", "source-locked", "execute", "after"):
            with self.subTest(expire_at=expire_at), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                evidence, feed, lock_path = root / "evidence", root / "feed", root / "parent-heavy.lock"
                feed.mkdir()
                clock = FakeClock()
                seen = []
                source = {"commit": SHA, "headExact": True, "clean": True}

                def step(label, deadline):
                    seen.append((label, deadline, deadline.remaining()))
                    clock.advance(30 if label == expire_at else 1)
                    deadline.remaining()

                def inventory(path, label, deadline):
                    phase = "failure" if "failure" in label else "after" if "after" in label else "before"
                    step(phase, deadline)
                    return {"files": [], "root": str(path)}

                def pin(commit, deadline, log_dir):
                    self.assertEqual(SHA, commit)
                    step(log_dir.name, deadline)
                    return source

                @contextmanager
                def acquire(path, deadline, wait):
                    self.assertEqual(lock_path, path)
                    step("lock", deadline)
                    yield {"path": str(path), "acquired": True}

                def execute(args, evidence, feed, source_before, result, deadline):
                    step("execute", deadline)
                    return result

                with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                     mock.patch.object(RUNNER, "Deadline", wraps=RUNNER.Deadline) as constructor, \
                     mock.patch.object(RUNNER, "inventory_tree", side_effect=inventory), \
                     mock.patch.object(RUNNER, "verify_source_pin", side_effect=pin), \
                     mock.patch.object(RUNNER, "acquire_lock", side_effect=acquire), \
                     mock.patch.object(RUNNER, "execute", side_effect=execute), \
                     redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                    code = RUNNER.main([
                        "--source-commit", SHA, "--evidence-dir", str(evidence), "--nuget-feed", str(feed),
                        "--heavy-lock", str(lock_path), "--timeout-seconds", "30",
                    ])
                constructor.assert_called_once_with(30)
                self.assertEqual(1, len({id(item[1]) for item in seen}))
                self.assertFalse(lock_path.exists())  # Only the fake was used.
                result = json.loads((evidence / "RESULT.json").read_text(encoding="utf-8"))
                self.assertEqual(expire_at is None, result["passed"])
                self.assertEqual(0 if expire_at is None else 1, code)
                if expire_at is None:
                    self.assertEqual(["before", "source-before", "lock", "source-locked", "execute", "after"],
                                     [item[0] for item in seen])
                    self.assertEqual([30, 29, 28, 27, 26, 25], [item[2] for item in seen])
                else:
                    self.assertEqual("DeadlineExceeded", result["error"]["type"])
                    self.assertEqual(expire_at, seen[-1][0])
                    if expire_at != "before":
                        self.assertIn("DeadlineExceeded", result["nugetFeed"]["afterError"])

    def test_git_is_bounded_by_remaining_budget_and_keeps_exact_clean_pin(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            clock = FakeClock()
            outputs = iter((str(ROOT) + "\n", SHA + "\n", ""))
            budgets = []

            def child(arguments, cwd, environment, log, timeout, label):
                self.assertEqual("git", arguments[0])
                self.assertEqual(ROOT, cwd)
                self.assertTrue(log.is_relative_to(root))
                budgets.append(timeout)
                clock.advance(2)
                return next(outputs)

            with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=child) as helper:
                deadline = RUNNER.Deadline(10)
                self.assertTrue(RUNNER.verify_source_pin(SHA, deadline, root)["clean"])
                self.assertEqual([10, 8, 6], budgets)
                self.assertEqual(["git", "status", "--porcelain=v1", "--untracked-files=all"], helper.call_args.args[0])
            for outputs in ((str(ROOT), "c" * 40), (str(ROOT), SHA, " M tracked-file")):
                with self.subTest(outputs=outputs), \
                     mock.patch.object(RUNNER, "run_bounded_command", side_effect=outputs), \
                     self.assertRaises(RUNNER.ProbeError):
                    RUNNER.verify_source_pin(SHA, RUNNER.Deadline(10), root)

    def test_bounded_child_timeout_mapping_and_expired_budget_never_starts_git(self):
        with tempfile.TemporaryDirectory() as temporary:
            log = Path(temporary) / "git.log"
            failures = (
                (RUNNER.CommandTimedOut("git", 1, log, ""), RUNNER.DeadlineExceeded),
                (RUNNER.CommandFailed("git", 3, log, ""), RUNNER.ChildFailed),
                (RUNNER.CommandUnavailable("git absent"), RUNNER.UnsupportedEnvironment),
            )
            for failure, expected in failures:
                with self.subTest(failure=failure), \
                     mock.patch.object(RUNNER, "run_bounded_command", side_effect=failure), \
                     self.assertRaises(expected):
                    RUNNER._run_git(["status"], RUNNER.Deadline(10), log)
            clock = FakeClock()
            with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                 mock.patch.object(RUNNER, "run_bounded_command") as child:
                deadline = RUNNER.Deadline(1)
                clock.advance(1)
                with self.assertRaises(RUNNER.DeadlineExceeded):
                    RUNNER._run_git(["status"], deadline, log)
                child.assert_not_called()

    def test_restore_build_and_test_consume_one_remaining_child_budget(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            work, stage, feed = root / "work", root / "source", root / "feed"
            work.mkdir()
            clock = FakeClock()
            budgets = []

            def child(arguments, cwd, environment, log, timeout, label):
                budgets.append(timeout)
                clock.advance(2)
                if "test" in arguments:
                    (work / "test-results" / RUNNER.TRX_NAME).write_text(self.matrix_xml(), encoding="utf-8")
                return "fake output"

            with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                 mock.patch.object(RUNNER, "_project_contract", return_value={}), \
                 mock.patch.object(RUNNER, "_pin_artifacts", return_value={}) as pin, \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=child) as helper:
                deadline = RUNNER.Deadline(20)
                clock.advance(5)  # Preflight/lock/copy already consumed time.
                env = {"NUGET_PACKAGES": str(work / "packages")}
                RUNNER.build_fresh(stage, work, feed, "/fake/dotnet", "/fake/bwrap", env, deadline)
                RUNNER.run_filtered_test(stage, work, feed, "/fake/dotnet", "/fake/bwrap", env, deadline)
                self.assertEqual([15, 13, 11], budgets)
                self.assertIs(deadline, pin.call_args.args[-1])
                for call in helper.call_args_list:
                    self.assertIn("--unshare-net", call.args[0])
                self.assertIn("--locked-mode", helper.call_args_list[0].args[0])
                self.assertIn("--no-restore", helper.call_args_list[1].args[0])
                self.assertIn(RUNNER.TEST_FILTER, helper.call_args_list[2].args[0])

    def test_hash_and_copy_check_budget_between_finite_one_mib_chunks(self):
        for operation in ("hash", "copy"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source, target = root / "source", root / "target"
                source.write_bytes(b"placeholder")
                clock = FakeClock()
                reads = []

                class Reader(io.BytesIO):
                    def read(self, size=-1):
                        reads.append(size)
                        clock.advance(2)
                        return super().read(size)

                reader = Reader(b"x" * (4 * 1024 * 1024))
                real_open = Path.open

                def open_file(path, *args, **kwargs):
                    return reader if path == source else real_open(path, *args, **kwargs)

                with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                     mock.patch.object(Path, "open", open_file), \
                     mock.patch.object(RUNNER.shutil, "copy2", side_effect=AssertionError("unbounded fast-copy")):
                    deadline = RUNNER.Deadline(3)
                    with self.assertRaises(RUNNER.DeadlineExceeded):
                        if operation == "hash":
                            RUNNER.sha256_file(source, deadline)
                        else:
                            RUNNER._copy_file(source, target, "fixture", deadline)
                self.assertEqual([1024 * 1024, 1024 * 1024], reads)
                if operation == "copy":
                    self.assertEqual(1024 * 1024, target.stat().st_size)

    def test_tree_traversal_and_copy_stop_between_entries(self):
        for operation in ("walk", "copy"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                clock = FakeClock()
                visited = []

                def info(*, follow_symlinks):
                    self.assertFalse(follow_symlinks)
                    visited.append(True)
                    clock.advance(1)
                    return SimpleNamespace(st_mode=0o100644)

                entries = [SimpleNamespace(name=str(index), path=str(root / str(index)), stat=info) for index in range(50)]
                scan = mock.MagicMock()
                scan.__enter__.return_value = iter(entries)
                with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                     mock.patch.object(RUNNER.os, "scandir", return_value=scan), \
                     mock.patch.object(RUNNER, "_copy_file") as copy:
                    with self.assertRaises(RUNNER.DeadlineExceeded):
                        if operation == "walk":
                            list(RUNNER._walk_tree(root, "fixture", RUNNER.Deadline(2)))
                        else:
                            RUNNER._copy_tree(root, root / "stage", "fixture", RUNNER.Deadline(2))
                    self.assertEqual(2, len(visited))
                    if operation == "copy":
                        self.assertLessEqual(copy.call_count, 2)

    def test_copy_and_inventory_retain_content_metadata_hashes_and_bin_obj_exclusion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, target = root / "source", root / "target"
            source.mkdir()
            (source / "z").write_bytes(b"z")
            (source / "a").write_bytes(b"a")
            (source / "a").chmod(0o640)
            for name in ("bin", "obj"):
                (source / name).mkdir()
                (source / name / "stale").write_bytes(b"must not copy")
            deadline = RUNNER.Deadline(30)
            RUNNER._copy_tree(source, target, "fixture", deadline)
            self.assertEqual(["a", "z"], sorted(path.name for path in target.iterdir()))
            self.assertEqual((source / "a").stat().st_mode, (target / "a").stat().st_mode)
            self.assertEqual((source / "a").stat().st_mtime_ns, (target / "a").stat().st_mtime_ns)
            result = RUNNER.inventory_tree(target, "fixture", deadline)
            self.assertEqual(["a", "z"], [item["path"] for item in result["files"]])
            expected = "\n".join(f"{RUNNER.sha256_bytes(name.encode())}  {name}" for name in ("a", "z"))
            self.assertEqual(RUNNER.sha256_bytes(expected.encode()), result["treeSha256"])

    def test_lock_checks_total_budget_before_even_an_uncontended_acquire(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            clock = FakeClock()
            with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                 mock.patch.object(RUNNER.fcntl, "flock") as flock:
                deadline = RUNNER.Deadline(1)
                clock.advance(1)
                with self.assertRaises(RUNNER.DeadlineExceeded):
                    with RUNNER.acquire_lock(root / "not-created.lock", deadline, 0):
                        self.fail("Expired budget acquired lock")
                flock.assert_not_called()
                self.assertFalse((root / "not-created.lock").exists())

    def test_contended_lock_wait_is_capped_without_real_sleeps(self):
        for total, wait in ((0.15, 100), (100, 0.15), (100, 0)):
            with self.subTest(total=total, wait=wait), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                clock = FakeClock()
                sleeps = []

                def sleep(seconds):
                    self.assertGreater(seconds, 0)
                    sleeps.append(seconds)
                    clock.advance(seconds)

                with mock.patch.object(RUNNER.time, "monotonic", side_effect=clock.monotonic), \
                     mock.patch.object(RUNNER.time, "sleep", side_effect=sleep), \
                     mock.patch.object(RUNNER.fcntl, "flock", side_effect=BlockingIOError):
                    with self.assertRaises(RUNNER.DeadlineExceeded):
                        with RUNNER.acquire_lock(root / "fixture.lock", RUNNER.Deadline(total), wait):
                            self.fail("Contended lock acquired")
                self.assertLessEqual(sum(sleeps), min(total, wait) + 1e-10)
                self.assertLessEqual(len(sleeps), 2)

    def test_result_write_or_success_output_failure_is_persisted_fail_closed(self):
        cases = (
            ("write", OSError("first RESULT write failed"), 1),
            ("write", ValueError("first RESULT encoding failed"), 1),
            ("write", KeyboardInterrupt(), 130),
            ("output", OSError("success output failed"), 1),
            ("output", KeyboardInterrupt(), 130),
        )
        for stage, failure, expected_code in cases:
            with self.subTest(stage=stage, failure=type(failure).__name__), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                feed, evidence = root / "feed", root / "evidence"
                feed.mkdir()
                lock = mock.MagicMock()
                lock.__enter__.return_value = {"acquired": True}
                original_writer = RUNNER._atomic_json
                attempts, messages = [], []

                def writer(path, value):
                    attempts.append(json.loads(json.dumps(value)))
                    if stage == "write" and len(attempts) == 1:
                        raise failure
                    original_writer(path, value)

                def output(message, **kwargs):
                    messages.append(json.loads(message))
                    if stage == "output" and len(messages) == 1:
                        raise failure

                with mock.patch.object(RUNNER, "verify_source_pin", return_value={"commit": SHA}), \
                     mock.patch.object(RUNNER, "acquire_lock", return_value=lock), \
                     mock.patch.object(RUNNER, "execute", side_effect=lambda *args: args[4]), \
                     mock.patch.object(RUNNER, "_atomic_json", side_effect=writer), \
                     mock.patch.object(RUNNER, "print", create=True, side_effect=output):
                    code = RUNNER.main([
                        "--source-commit", SHA, "--evidence-dir", str(evidence), "--nuget-feed", str(feed),
                        "--heavy-lock", str(root / "fake-parent.lock"),
                    ])
                result = json.loads((evidence / "RESULT.json").read_text())
                self.assertEqual(expected_code, code)
                self.assertFalse(result["passed"])
                self.assertEqual(expected_code, result["exitCode"])
                self.assertEqual(type(failure).__name__, result["error"]["type"])
                self.assertTrue(attempts[0]["passed"])
                self.assertFalse(attempts[-1]["passed"])
                self.assertEqual(2, len(attempts))
                self.assertFalse(messages[-1]["passed"])
                self.assertEqual(expected_code, messages[-1]["exitCode"])

    def test_error_and_interrupt_report_write_failures_still_return_nonzero_and_false(self):
        cases = (
            (RUNNER.ProbeError("execution failed"), 1, OSError("evidence disk unavailable")),
            (KeyboardInterrupt(), 130, OSError("evidence disk unavailable")),
            (RUNNER.ProbeError("execution failed"), 1, ValueError("evidence encoding failed")),
            (KeyboardInterrupt(), 130, ValueError("evidence encoding failed")),
            (RUNNER.UnsupportedEnvironment("tool unavailable"), 2, OSError("evidence disk unavailable")),
        )
        for failure, expected_code, report_error in cases:
            with self.subTest(failure=type(failure).__name__, report_error=type(report_error).__name__), \
                 tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                feed, evidence = root / "feed", root / "evidence"
                feed.mkdir()
                lock = mock.MagicMock()
                lock.__enter__.return_value = {"acquired": True}
                stderr = io.StringIO()
                attempts = []

                def failing_writer(path, value):
                    attempts.append(json.loads(json.dumps(value)))
                    raise report_error

                with mock.patch.object(RUNNER, "verify_source_pin", return_value={"commit": SHA}), \
                     mock.patch.object(RUNNER, "acquire_lock", return_value=lock), \
                     mock.patch.object(RUNNER, "execute", side_effect=failure), \
                     mock.patch.object(RUNNER, "_atomic_json", side_effect=failing_writer), \
                     redirect_stderr(stderr), redirect_stdout(io.StringIO()):
                    code = RUNNER.main([
                        "--source-commit", SHA, "--evidence-dir", str(evidence), "--nuget-feed", str(feed),
                        "--heavy-lock", str(root / "fake-parent.lock"),
                    ])
                self.assertEqual(expected_code, code)
                self.assertEqual(1, len(attempts))  # No endless report retries.
                self.assertFalse(attempts[0]["passed"])
                self.assertEqual(expected_code, attempts[0]["exitCode"])
                summary = json.loads(stderr.getvalue())
                self.assertFalse(summary["passed"])
                self.assertEqual(expected_code, summary["exitCode"])
                self.assertIn(str(report_error), summary["reportWriteError"])

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
