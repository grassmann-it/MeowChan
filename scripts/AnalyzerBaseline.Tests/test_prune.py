"""No-dependency tests for the baseline's shrink-only update policy."""
import contextlib
import io
import os
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "analyzer-baseline.py"


class PruneTests(unittest.TestCase):
    def test_shrink_preserves_uncaptured_assembly_and_rejects_additions(self):
        state = runpy.run_path(str(SCRIPT))["main"].__globals__
        with tempfile.TemporaryDirectory(dir=os.environ["COMMANDCODE_SCRATCHPAD"]) as directory:
            root = Path(directory)
            baseline = root / "baseline.tsv"
            baseline.write_text("# test\nCaptured\texisting\nOther\tuntouched\n")
            (root / "Captured.sarif").write_text('{"runs": [{"results": []}]}')
            with patch.dict(state, {"BASELINE": baseline, "entries": lambda _: set()}), \
                    patch.object(sys, "argv", [str(SCRIPT), directory]), \
                    contextlib.redirect_stdout(io.StringIO()):
                state["main"]()
            self.assertNotIn("Captured\texisting", baseline.read_text())
            self.assertIn("Other\tuntouched", baseline.read_text())
            before = baseline.read_bytes()
            with patch.dict(state, {"BASELINE": baseline, "entries": lambda _: {"Captured\tNEW"}}), \
                    patch.object(sys, "argv", [str(SCRIPT), directory]), \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                state["main"]()
            self.assertEqual(2, error.exception.code)
            self.assertEqual(before, baseline.read_bytes(), "A rejected update must not write the baseline")

    def test_empty_capture_cannot_erase_baseline(self):
        state = runpy.run_path(str(SCRIPT))["main"].__globals__
        with tempfile.TemporaryDirectory(dir=os.environ["COMMANDCODE_SCRATCHPAD"]) as directory, \
                patch.object(sys, "argv", [str(SCRIPT), directory]), \
                contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            state["main"]()
        self.assertEqual(2, error.exception.code)


if __name__ == "__main__":
    unittest.main()
