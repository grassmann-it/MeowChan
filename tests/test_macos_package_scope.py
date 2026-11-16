"""Focused macOS package dispatch must exercise the installed app, not just build it."""
from pathlib import Path
import unittest


class MacosPackageScopeTests(unittest.TestCase):
    def test_focused_package_mode_includes_playback_workflow_and_evidence(self):
        workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/dotnet-desktop.yml").read_text()
        job = workflow.split("  macos-package:\n", 1)[1].split("\n  package-media:", 1)[0]
        for name in (
            "Exercise native gallery playback from the DMG-installed apphost",
            "Exercise the DMG-installed apphost download, restore, export and desktop workflow",
            "Retain playback-verified native DMG candidate, inventory ZIP and evidence",
        ):
            with self.subTest(step=name):
                # Step selection is explicit: a packaging-only green result is not playback proof.
                step = job.split("      - name: " + name + "\n", 1)[1].split("      - ", 1)[0]
                condition = next(line for line in step.splitlines() if line.strip().startswith("if:"))
                self.assertIn("inputs.mode == 'macos-package'", condition)
                self.assertNotIn("continue-on-error: true", step)


if __name__ == "__main__":
    unittest.main()
