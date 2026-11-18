"""Window-free bundle contract checks; real GUI proof uses the three registry scenes."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("review", Path(__file__).resolve().parents[1] / "scripts/check-review-artifacts.py")
review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(review)


class ReviewArtifactsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bundle = self.root / "0001-test"
        self.bundle.mkdir()
        self.write("bundle.json", dict(schema_version=1, complete=True, files=sorted(review.FILES)))
        self.write("control-tree.json", dict(schema_version=1, elements=[dict.fromkeys(review.TREE_FIELDS) | {"id": "0"}]))
        self.write("state.json", dict(schema_version=1) | dict.fromkeys(review.STATE_FIELDS, []))
        self.timing = dict(schema_version=1, kind="gesture", t_input=1, t_first_visual_ack=25, t_final=180,
                           heartbeat=dict(samples=[0, 25, 160, 180], stalls=[dict(gap_ms=135)]))
        self.write("timing.json", self.timing)
        for name in ("screenshot.png", "grayscale.png"):
            (self.bundle / name).write_bytes(b"\x89PNG\r\n\x1a\n")

    def write(self, name, value):
        (self.bundle / name).write_text(json.dumps(value), encoding="utf-8")

    def test_gesture_and_stall(self):
        self.assertEqual(review.validate(self.root)["stalls_over_100_ms"], 1)

    def test_exact_threshold_is_not_stall(self):
        self.timing["heartbeat"] = dict(samples=[0, 100, 180], stalls=[])
        self.write("timing.json", self.timing)
        self.assertEqual(review.validate(self.root)["stalls_over_100_ms"], 0)

    def test_omitted_stall_is_rejected(self):
        self.timing["heartbeat"]["stalls"] = []
        self.write("timing.json", self.timing)
        with self.assertRaises(ValueError):
            review.validate(self.root)

    def test_partial_bundle_is_rejected(self):
        (self.bundle / "bundle.json").unlink()
        with self.assertRaises(FileNotFoundError):
            review.validate(self.root)

    def test_nonmonotonic_ack_is_rejected(self):
        self.timing["t_first_visual_ack"] = 200
        self.write("timing.json", self.timing)
        with self.assertRaises(ValueError):
            review.validate(self.root)

    def test_checkpoint_has_no_fake_latency(self):
        self.timing.update(kind="checkpoint", t_input=None, t_first_visual_ack=None)
        self.write("timing.json", self.timing)
        self.assertEqual(review.validate(self.root)["checkpoints"], 1)
        self.timing["t_input"] = 0
        self.write("timing.json", self.timing)
        with self.assertRaises(ValueError):
            review.validate(self.root)


if __name__ == "__main__":
    unittest.main()
