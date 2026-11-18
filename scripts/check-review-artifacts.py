#!/usr/bin/env python3
"""Validate one scene's review-artifacts directory; summarize evidence, not UX acceptance."""
from __future__ import annotations
import argparse
import json
from pathlib import Path

TREE_FIELDS = {"id", "parent_id", "name", "type", "bounds", "visible", "effectively_visible",
               "is_enabled", "is_pointer_over", "is_focused", "is_keyboard_focus_within",
               "visual_state_classes", "accessible_name", "accessible_help_text", "text", "clipped_or_overflow"}
STATE_FIELDS = {"selection_sets", "counter_labels", "view_modes", "filters", "panel_widths",
                "open_popovers_overlays", "background_work_tokens"}
FILES = {"screenshot.png", "grayscale.png", "control-tree.json", "state.json", "timing.json"}


def validate(directory: Path) -> dict:
    bundles = sorted(p for p in directory.iterdir() if p.is_dir())
    if not bundles:
        raise ValueError("No artifact bundles")
    gestures = checkpoints = stalls = unobserved = 0
    for bundle in bundles:
        def read(name):
            value = json.loads((bundle / name).read_text(encoding="utf-8"))
            if value.get("schema_version") != 1:
                raise ValueError(f"{bundle / name}: unsupported schema")
            return value
        manifest = read("bundle.json")
        if manifest.get("complete") is not True or set(manifest["files"]) != FILES:
            raise ValueError(f"{bundle}: incomplete manifest")
        for name in FILES:
            if not (bundle / name).is_file() or not (bundle / name).stat().st_size:
                raise ValueError(f"{bundle / name}: missing/empty")
        for name in ("screenshot.png", "grayscale.png"):
            with (bundle / name).open("rb") as image:
                if image.read(8) != b"\x89PNG\r\n\x1a\n":
                    raise ValueError(f"{bundle / name}: not PNG")
        tree, state, timing = read("control-tree.json"), read("state.json"), read("timing.json")
        if not tree["elements"] or any(not TREE_FIELDS <= e.keys() for e in tree["elements"]):
            raise ValueError(f"{bundle}: incomplete control tree")
        ids = [e["id"] for e in tree["elements"]]
        if len(ids) != len(set(ids)):
            raise ValueError(f"{bundle}: duplicate tree ids")
        if not STATE_FIELDS <= state.keys():
            raise ValueError(f"{bundle}: incomplete state")
        if timing["kind"] == "gesture":
            gestures += 1
            start, ack, final = (timing[k] for k in ("t_input", "t_first_visual_ack", "t_final"))
            if not start <= final or ack is not None and not start <= ack <= final:
                raise ValueError(f"{bundle}: nonmonotonic timing")
            unobserved += ack is None
            samples = timing["heartbeat"]["samples"]
            if len(samples) < 2 or samples != sorted(samples):
                raise ValueError(f"{bundle}: invalid heartbeat")
            expected = [b-a for a, b in zip(samples, samples[1:]) if b-a > 100]
            actual = [gap["gap_ms"] for gap in timing["heartbeat"]["stalls"]]
            if expected != actual:
                raise ValueError(f"{bundle}: heartbeat omitted/misclassified a >100 ms stall")
            stalls += len(actual)
        elif timing["kind"] == "checkpoint":
            checkpoints += 1
            if timing["t_input"] is not None or timing["t_first_visual_ack"] is not None:
                raise ValueError(f"{bundle}: checkpoint fabricated input/ack timing")
        else:
            raise ValueError(f"{bundle}: unknown timing kind")
    return dict(bundles=len(bundles), gestures=gestures, checkpoints=checkpoints,
                unobserved_ack=unobserved, stalls_over_100_ms=stalls)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(validate(args.directory), indent=2))


if __name__ == "__main__":
    main()
