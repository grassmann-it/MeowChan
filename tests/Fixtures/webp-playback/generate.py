#!/usr/bin/env python3
"""Generate original CC0 WebP inputs and independent Pillow pixel goldens offline."""
import hashlib
import json
from pathlib import Path

from PIL import Image, __version__ as pillow_version

ROOT = Path(__file__).resolve().parent
SIZE = (12, 8)
DURATIONS = [17, 67, 143]
PASSES = 2


def bgra(rgba):
    result = bytearray()
    for red, green, blue, alpha in rgba.get_flattened_data():
        result.extend(((blue * alpha + 127) // 255, (green * alpha + 127) // 255,
                       (red * alpha + 127) // 255, alpha))
    return bytes(result)


def main():
    red = Image.new("RGBA", SIZE)
    red.paste((255, 0, 0, 128), (0, 0, 6, 8))
    green = red.copy()
    green.paste((0, 255, 0, 255), (6, 0, 12, 4))
    blue = Image.new("RGBA", SIZE)
    blue.paste((0, 0, 255, 192), (6, 4, 12, 8))
    animated = ROOT / "animated.webp"
    red.save(animated, format="WEBP", lossless=True, save_all=True, append_images=[green, blue],
             duration=DURATIONS, loop=PASSES, background=(0, 0, 0, 0))
    red.save(ROOT / "still.webp", format="WEBP", lossless=True)
    encoded = animated.read_bytes()
    (ROOT / "truncated.webp").write_bytes(encoded[:-8])

    # Only the encoded canvas dimensions change. The three frame rectangles still fit.
    oversized = bytearray(encoded)
    assert oversized[12:16] == b"VP8X"
    oversized[24:30] = (4095).to_bytes(3, "little") * 2
    (ROOT / "oversized-canvas.webp").write_bytes(oversized)

    colors = [Image.new("RGB", (1, 1), color) for color in ("red", "blue")]
    colors[0].save(ROOT / "too-many-frames.webp", format="WEBP", lossless=True, save_all=True,
                   append_images=[colors[index % 2] for index in range(1, 1025)], duration=40, loop=0)

    frames = []
    with Image.open(animated) as image:
        assert image.n_frames == len(DURATIONS)
        assert image.info["loop"] == PASSES
        for index, duration in enumerate(DURATIONS):
            image.seek(index)
            pixels = bgra(image.convert("RGBA"))
            assert image.info["duration"] == duration
            frames.append(dict(duration_ms=duration, bgra_premul_sha256=hashlib.sha256(pixels).hexdigest()))

    manifest = dict(license="CC0-1.0", pillow_version=pillow_version, width=SIZE[0], height=SIZE[1],
                    total_passes=PASSES, frames=frames)
    (ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
