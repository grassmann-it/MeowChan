#!/usr/bin/env python3
"""Offline original CC0 GIFs + Pillow-composited canonical premultiplied BGRA goldens.
Only writes the exact five GIFs and manifest.json alongside this script.
"""
import hashlib
import json
from pathlib import Path
from PIL import Image, __version__ as pillow_version

ROOT = Path(__file__).resolve().parent
# Fixed palette: transparent, red, green, blue, white, yellow. No quantization or artwork.
PALETTE = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 0] + [0] * (768 - 18)


def solid(index):
    frame = Image.new("P", (12, 8), index)
    frame.putpalette(PALETTE)
    return frame


def bgra(rgba):
    result = bytearray()
    for red, green, blue, alpha in getattr(rgba, "get_flattened_data", rgba.getdata)():
        result.extend(((blue * alpha + 127) // 255, (green * alpha + 127) // 255,
                       (red * alpha + 127) // 255, alpha))
    return bytes(result)


def emit(name, frames, durations, disposal, loop=None, transparent=False):
    args = dict(save_all=True, append_images=frames[1:], duration=durations,
                disposal=disposal, optimize=False)
    if loop is not None:
        args["loop"] = loop
    if transparent:
        args["transparency"] = 0
        args["background"] = 0
    frames[0].save(ROOT / name, format="GIF", **args)


def main():
    emit("animated-loop.gif", [solid(1), solid(2), solid(3)], [10, 60, 140], [1, 1, 1], loop=0)
    emit("finite-loop.gif", [solid(3), solid(1), solid(2)], [40, 80, 120], [1, 1, 1], loop=2)
    # Transparent full canvas then spatially separated colored islands. Frame 1 uses
    # RestorePrevious, frame 2 RestoreBackground, frame 3 exposes both disposal paths.
    frames = [solid(0) for _ in range(4)]
    frames[0].paste(1, (0, 0, 4, 8))
    frames[1].paste(2, (4, 0, 8, 8))
    frames[2].paste(3, (8, 0, 12, 8))
    frames[3].paste(5, (4, 2, 8, 6))
    emit("disposal-transparency.gif", frames, [50, 70, 90, 110], [1, 3, 2, 1], loop=0, transparent=True)
    emit("single-frame.gif", [solid(4)], 100, 1)
    (ROOT / "truncated.gif").write_bytes((ROOT / "animated-loop.gif").read_bytes()[:8])
    entries = []
    for name in ("animated-loop.gif", "finite-loop.gif", "disposal-transparency.gif", "single-frame.gif", "truncated.gif"):
        data = (ROOT / name).read_bytes()
        entry = dict(file=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        if name == "truncated.gif":
            entry["expected_error"] = "InvalidDataException: incomplete GIF header; no frame may be published"
        else:
            with Image.open(ROOT / name) as image:
                encoded_loop = image.info.get("loop")
                entry.update(width=image.width, height=image.height, frame_count=image.n_frames,
                             encoded_loop=encoded_loop,
                             repetition_count=-1 if encoded_loop == 0 else (encoded_loop or 0), frames=[])
                for index in range(image.n_frames):
                    image.seek(index)
                    raw_duration = image.info.get("duration", 0)
                    pixels = bgra(image.convert("RGBA"))
                    entry["frames"].append(dict(index=index, duration_ms=raw_duration,
                        effective_duration_ms=100 if raw_duration < 20 else raw_duration,
                        disposal=image.disposal_method, bgra_premul_sha256=hashlib.sha256(pixels).hexdigest()))
        entries.append(entry)
    manifest = dict(schema_version=1, license="CC0-1.0", generator="generate.py",
                    pillow_version=pillow_version, pixel_format="BGRA8888 premultiplied; rows top-to-bottom, tightly packed",
                    repetition_semantics="Skia count: -1 infinite, 0 one pass, positive N repeats after first pass",
                    fixtures=entries)
    (ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
