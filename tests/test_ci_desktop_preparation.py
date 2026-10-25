import argparse
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("ci_desktop", Path(__file__).resolve().parents[1] / "scripts/prepare-ci-desktop.py")
DESKTOP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DESKTOP)


def state(width=1920, height=1080, work_width=1920, work_height=1040):
    return dict(physical_width=width, physical_height=height,
                work_width_dip=work_width, work_height_dip=work_height)


def mode(identity, physical=(1920, 1080), logical=(1920, 1080), usable=True):
    return dict(id=identity, physical_width=physical[0], physical_height=physical[1],
                logical_width=logical[0], logical_height=logical[1], usable=usable)


class FakeDisplay:
    def __init__(self, before, modes, outcomes):
        self.current, self.available, self.outcomes = before, modes, outcomes
        self.applied = []

    def state(self):
        return self.current

    def modes(self):
        return self.available

    def apply(self, candidate):
        self.applied.append(candidate["id"])
        outcome = self.outcomes[candidate["id"]]
        if outcome is None:
            return dict(applied=False, apply_result=-2)
        self.current = outcome
        return dict(applied=True, apply_result=0)


class DesktopPreparationTests(unittest.TestCase):
    def setUp(self):
        self.args = argparse.Namespace(min_width=1920, min_height=1080,
                                       min_work_width=1600, min_work_height=1000)

    def test_sufficient_desktop_is_only_measured(self):
        backend = FakeDisplay(state(), [mode(0)], {})
        report = {}
        DESKTOP.prepare(backend, self.args, report)
        self.assertEqual("already_sufficient", report["status"])
        self.assertEqual([], backend.applied)
        self.assertEqual([mode(0)], report["supported_modes"])

    def test_mode_selection_rejects_insufficient_dip_and_preserves_real_pixel_requirement(self):
        modes = [mode(0, physical=(3840, 2160), logical=(1280, 720)),
                 mode(1, physical=(1600, 1000), logical=(1600, 1000)),
                 mode(2, usable=False), mode(3, physical=(2560, 1440), logical=(2560, 1440)), mode(4)]
        self.assertEqual([4, 3], [item["id"] for item in DESKTOP.candidates(modes, self.args)])

    def test_rejected_native_mode_falls_back_to_another_supported_mode(self):
        backend = FakeDisplay(state(1024, 768, 1024, 728),
                              [mode(0), mode(1, physical=(2560, 1440), logical=(2560, 1440))],
                              {0: None, 1: state(2560, 1440, 2560, 1400)})
        report = {}
        DESKTOP.prepare(backend, self.args, report)
        self.assertEqual([0, 1], backend.applied)
        self.assertEqual("prepared", report["status"])
        self.assertEqual(2560, report["after"]["physical_width"])

    def test_success_return_without_sufficient_geometry_is_not_success(self):
        backend = FakeDisplay(state(1024, 768, 1024, 728), [mode(0)], {0: state(1024, 768, 1024, 728)})
        report = {}
        with patch.object(DESKTOP.time, "monotonic", side_effect=[0, 6]):
            with self.assertRaisesRegex(RuntimeError, "No supported"):
                DESKTOP.prepare(backend, self.args, report)
        self.assertEqual(1024, report["attempts"][0]["observed"]["physical_width"])

    def test_unavailable_large_mode_fails_with_full_inventory(self):
        backend = FakeDisplay(state(1024, 768, 1024, 728), [mode(0, physical=(1024, 768), logical=(1024, 768))], {})
        report = {}
        with self.assertRaisesRegex(RuntimeError, "No supported"):
            DESKTOP.prepare(backend, self.args, report)
        self.assertEqual(1, len(report["supported_modes"]))
        self.assertEqual([], backend.applied)

    def test_no_opt_in_cannot_instantiate_native_backend(self):
        with patch.object(DESKTOP, "WindowsDisplay") as windows, patch.object(DESKTOP, "MacDisplay") as mac:
            with contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    DESKTOP.main(["--report", "unused.json"])
            windows.assert_not_called()
            mac.assert_not_called()

    def test_unsupported_platform_writes_honest_failure_without_native_access(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "report.json"
            with patch.object(DESKTOP.sys, "platform", "linux"), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, DESKTOP.main(["--allow-disposable-desktop", "--report", str(target)]))
            self.assertEqual("failed", json.loads(target.read_text())["status"])


if __name__ == "__main__":
    unittest.main()
