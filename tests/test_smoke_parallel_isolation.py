"""Concurrent capture ownership regression: real child processes, no GUI/runtime dependency."""
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "run-desktop-smoke.py"
SPEC = importlib.util.spec_from_file_location("parallel_smoke_runner", SCRIPT)
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)

# The child uses the same AppContext.BaseDirectory-style output contract as the smoke executable.
# Both children must write before either exits: this proves overlap, not just green serialized runs.
CHILD = r'''
import os, sys, time
from pathlib import Path
assembly = Path(sys.argv[1])
owner = os.environ["FIXTURE_OWNER"]
barrier = Path(os.environ["FIXTURE_BARRIER"])
assert (assembly.parent / "runtimes" / "native.fixture").read_text() == "dependency"
(assembly.parent / "smoke-shared.png").write_text(owner)
(assembly.parent / ("smoke-" + owner + ".png")).write_text(owner)
(barrier / owner).write_text(str(assembly))
deadline = time.monotonic() + 10
while not all((barrier / name).exists() for name in ("alpha", "beta")):
    if time.monotonic() > deadline:
        raise RuntimeError("children did not overlap")
    time.sleep(0.01)
print("SMOKE SCENARIO: normal", flush=True)
print("SMOKE PASS: concurrent fixture", flush=True)
'''


class ParallelCaptureIsolationTests(unittest.TestCase):
    def test_concurrent_children_own_their_captures_without_platform_lock(self):
        with tempfile.TemporaryDirectory(prefix="parallel-smoke-") as directory:
            root = Path(directory)
            binaries = root / "bin"
            (binaries / "runtimes").mkdir(parents=True)
            assembly = binaries / "fixture.dll"
            assembly.touch()
            (binaries / "runtimes" / "native.fixture").write_text("dependency")
            (binaries / "smoke-stale.png").write_text("old capture")
            child = root / "child.py"
            child.write_text(CHILD)
            barrier = root / "barrier"
            barrier.mkdir()
            popen = subprocess.Popen

            def launch(command, **kwargs):
                self.assertEqual("dotnet", command[0])
                return popen([sys.executable, str(child), *command[1:]], **kwargs)

            def run(owner):
                environment = {**os.environ, "FIXTURE_OWNER": owner, "FIXTURE_BARRIER": str(barrier)}
                return runner.run_mode("normal", assembly, root / owner, environment, False, 15, "normal")

            # Exercise the no-fcntl contract (Windows) on every OS, with actual simultaneous children.
            with mock.patch.dict(sys.modules, {"fcntl": None}), \
                    mock.patch.object(runner.subprocess, "Popen", side_effect=launch), \
                    ThreadPoolExecutor(max_workers=2) as workers:
                results = list(workers.map(run, ("alpha", "beta")))
            self.assertTrue(all(result["passed"] for result in results), results)
            for owner in ("alpha", "beta"):
                captures = {path.name: path.read_text() for path in (root / owner).glob("*.png")}
                self.assertEqual({"smoke-shared.png": owner, "smoke-" + owner + ".png": owner}, captures)
            self.assertEqual(["smoke-stale.png"], sorted(path.name for path in binaries.glob("*.png")))
            staged = [Path((barrier / owner).read_text()) for owner in ("alpha", "beta")]
            self.assertNotEqual(staged[0].parent, staged[1].parent)
            self.assertTrue(all(not path.parent.exists() for path in staged), "staging leaked after completion")

    def test_failed_child_keeps_its_evidence_even_with_old_timestamp(self):
        with tempfile.TemporaryDirectory(prefix="failed-smoke-") as directory:
            root = Path(directory)
            assembly = root / "fixture.dll"
            assembly.touch()
            stage = []

            def launch(command, **kwargs):
                stage.append(Path(command[1]).parent)
                capture = stage[0] / "smoke-failure.json"
                capture.write_text('{"failure": true}')
                os.utime(capture, ns=(0, 0))
                kwargs["stdout"].write("SMOKE FAILED: fixture\n")
                return mock.Mock(wait=mock.Mock(return_value=1))

            with mock.patch.object(runner.subprocess, "Popen", side_effect=launch), \
                    mock.patch.object(runner, "stop_owned_processes"):
                result = runner.run_mode("normal", assembly, root / "evidence", {}, False, 1)
            self.assertFalse(result["passed"])
            self.assertEqual({"failure": True}, json.loads((root / "evidence/smoke-failure.json").read_text()))
            self.assertEqual({"scenario": "normal", "files": ["smoke-failure.json"]},
                             json.loads((root / "evidence/captures.json").read_text()))
            self.assertFalse(stage[0].exists())
            self.assertFalse((root / "smoke-failure.json").exists())


if __name__ == "__main__":
    unittest.main()
