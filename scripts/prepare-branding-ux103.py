#!/usr/bin/env python3
"""Prepare, never adopt, the explicitly selected UX103 branding source.

Python standard library only; Git is used read-only to inventory tracked consumers.
Requires --source and --output (a NEW directory OUTSIDE this checkout, with an
existing parent). No install, product writes, notice edits, visual/rights claims,
ICNS generation or packaging. Existing package-macos.py owns ICO -> ICNS.

Only non-interlaced RGBA8 PNG input is supported, as used by the pinned source.
Derivatives use alpha-weighted area sampling, contain/transparent padding, no
crop, recolour or matte. The master is copied byte-for-byte, metadata included.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import sys
import zlib


REPO_ROOT = Path(__file__).resolve().parents[1]
SELECTED_SOURCE_SHA256 = "6c9024b7220b6e015eeeb36aec9763fa5d804224b2b9e260caa7c4947c8436b1"
MASTER = "Assets/branding/meowchan-source.png"
PNG = "Assets/meowchan.png"
ICO = "Assets/meowchan.ico"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
MAX_PIXELS = 16 * 1024 * 1024
MAX_FILE_BYTES = 64 * 1024 * 1024
SELF_PATHS = {"scripts/prepare-branding-ux103.py", "tests/test_branding_prepare.py"}
ICON_REFERENCE = re.compile(r"(?<![\w.-])meowchan(?:-source)?\.(?:png|ico|icns)(?![\w.-])", re.I)


class PreparationError(ValueError):
    """The prepare-only contract cannot be satisfied."""


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_input(path: Path) -> bytes:
    if not path.is_file() or not 0 < path.stat().st_size <= MAX_FILE_BYTES:
        raise PreparationError(f"Expected a bounded, nonempty input file: {path}")
    return path.read_bytes()


def png_chunks(data: bytes) -> list[tuple[bytes, bytes]]:
    if not data.startswith(PNG_SIGNATURE):
        raise PreparationError("Invalid PNG signature")
    chunks = []
    position = len(PNG_SIGNATURE)
    while position < len(data):
        if position + 12 > len(data):
            raise PreparationError("Truncated PNG chunk")
        length, kind = struct.unpack_from(">I4s", data, position)
        end = position + 12 + length
        if end > len(data):
            raise PreparationError("Invalid PNG chunk length")
        payload = data[position + 8:end - 4]
        if zlib.crc32(kind + payload) & 0xFFFFFFFF != struct.unpack_from(">I", data, end - 4)[0]:
            raise PreparationError("Invalid PNG checksum")
        if not chunks and (kind != b"IHDR" or length != 13):
            raise PreparationError("Missing PNG IHDR")
        if chunks and kind == b"IHDR":
            raise PreparationError("Duplicate PNG IHDR")
        chunks.append((kind, payload))
        if kind == b"IEND":
            if payload or end != len(data) or not any(k == b"IDAT" for k, _ in chunks):
                raise PreparationError("Invalid PNG end or missing image data")
            return chunks
        position = end
    raise PreparationError("Missing PNG IEND")


def png_size(data: bytes) -> tuple[int, int]:
    chunks = png_chunks(data)
    width, height = struct.unpack_from(">II", chunks[0][1])
    if not width or not height or width * height > MAX_PIXELS:
        raise PreparationError("PNG dimensions exceed supported bounds")
    return width, height


def decode_rgba(data: bytes) -> tuple[int, int, bytes]:
    chunks = png_chunks(data)
    width, height, depth, colour, compression, filtering, interlace = struct.unpack(">IIBBBBB", chunks[0][1])
    if not width or not height or width * height > MAX_PIXELS:
        raise PreparationError("PNG dimensions exceed supported bounds")
    if (depth, colour, compression, filtering, interlace) != (8, 6, 0, 0, 0):
        raise PreparationError("Only non-interlaced RGBA8 PNG is supported")
    idat_ended = False
    idat_seen = False
    for kind, _ in chunks[1:]:
        if kind == b"acTL" or (kind[0] & 32 == 0 and kind not in (b"PLTE", b"IDAT", b"IEND")):
            raise PreparationError("Unsupported PNG critical chunk or animation")
        if kind == b"IDAT":
            if idat_ended:
                raise PreparationError("Non-contiguous PNG image data")
            idat_seen = True
        elif idat_seen:
            idat_ended = True
    stride = width * 4
    expected = height * (stride + 1)
    inflater = zlib.decompressobj()
    try:
        raw = inflater.decompress(b"".join(p for k, p in chunks if k == b"IDAT"), expected + 1)
    except zlib.error as error:
        raise PreparationError("Invalid PNG compressed data") from error
    if len(raw) != expected or not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
        raise PreparationError("Invalid PNG decompressed length or stream")
    pixels = bytearray()
    previous = bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        filter_type = raw[start]
        if filter_type > 4:
            raise PreparationError("Unsupported PNG row filter")
        row = bytearray(raw[start + 1:start + 1 + stride])
        for x in range(stride):
            left = row[x - 4] if x >= 4 else 0
            above = previous[x]
            upper_left = previous[x - 4] if x >= 4 else 0
            predictor = 0
            if filter_type == 1:
                predictor = left
            elif filter_type == 2:
                predictor = above
            elif filter_type == 3:
                predictor = (left + above) // 2
            elif filter_type == 4:
                p = left + above - upper_left
                distances = (abs(p - left), abs(p - above), abs(p - upper_left))
                predictor = (left, above, upper_left)[distances.index(min(distances))]
            row[x] = (row[x] + predictor) & 255
        pixels.extend(row)
        previous = row
    return width, height, bytes(pixels)


def png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def encode_rgba(width: int, height: int, pixels: bytes) -> bytes:
    if width <= 0 or height <= 0 or len(pixels) != width * height * 4:
        raise PreparationError("Invalid RGBA buffer dimensions")
    stride = width * 4
    raw = b"".join(b"\x00" + pixels[y * stride:(y + 1) * stride] for y in range(height))
    return (PNG_SIGNATURE + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + png_chunk(b"IDAT", zlib.compress(raw, 9)) + png_chunk(b"IEND", b""))


def area_weights(source: int, target: int) -> list[list[tuple[int, int]]]:
    """Integer overlap weights; each destination's weights sum to source."""
    return [[(i, min((i + 1) * target, (d + 1) * source) - max(i * target, d * source))
             for i in range(d * source // target, ((d + 1) * source + target - 1) // target)]
            for d in range(target)]


def resize_contain(width: int, height: int, pixels: bytes, target_width: int, target_height: int) -> bytes:
    if min(width, height, target_width, target_height) <= 0 or len(pixels) != width * height * 4:
        raise PreparationError("Invalid resize dimensions or RGBA buffer")
    if target_width * height <= target_height * width:
        fitted_width = target_width
        fitted_height = max(1, (height * target_width + width // 2) // width)
    else:
        fitted_height = target_height
        fitted_width = max(1, (width * target_height + height // 2) // height)
    x_weights = area_weights(width, fitted_width)
    y_weights = area_weights(height, fitted_height)
    result = bytearray(target_width * target_height * 4)
    x_offset = (target_width - fitted_width) // 2
    y_offset = (target_height - fitted_height) // 2
    area = width * height
    for y, rows in enumerate(y_weights):
        for x, columns in enumerate(x_weights):
            alpha = red = green = blue = 0
            for sy, wy in rows:
                for sx, wx in columns:
                    index = (sy * width + sx) * 4
                    weight_alpha = pixels[index + 3] * wx * wy
                    alpha += weight_alpha
                    red += pixels[index] * weight_alpha
                    green += pixels[index + 1] * weight_alpha
                    blue += pixels[index + 2] * weight_alpha
            index = ((y + y_offset) * target_width + x + x_offset) * 4
            if alpha:
                result[index:index + 4] = bytes(((red + alpha // 2) // alpha,
                                               (green + alpha // 2) // alpha,
                                               (blue + alpha // 2) // alpha,
                                               (alpha + area // 2) // area))
    return bytes(result)


def ico_frames(data: bytes) -> list[dict]:
    if len(data) < 6:
        raise PreparationError("Truncated ICO header")
    reserved, kind, count = struct.unpack_from("<HHH", data)
    directory_end = 6 + 16 * count
    if reserved or kind != 1 or not 0 < count <= 256 or directory_end > len(data):
        raise PreparationError("Invalid ICO directory")
    frames = []
    ranges = []
    for i in range(count):
        w, h, colours, entry_reserved, planes, bits, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + i * 16)
        w, h = w or 256, h or 256
        if (colours or entry_reserved or planes != 1 or bits != 32 or not size
                or offset < directory_end or offset + size > len(data)
                or any(offset < end and start < offset + size for start, end in ranges)
                or any((frame["width"], frame["height"]) == (w, h) for frame in frames)):
            raise PreparationError("Invalid or duplicate ICO frame")
        payload = data[offset:offset + size]
        encoding = "png" if payload.startswith(PNG_SIGNATURE) else "dib"
        if encoding == "png":
            if png_size(payload) != (w, h):
                raise PreparationError("ICO PNG dimensions disagree with directory")
        else:
            if len(payload) < 40:
                raise PreparationError("Truncated ICO DIB")
            header, dw, dh, dp, db, compression = struct.unpack_from("<IiiHHI", payload)
            expected_size = 40 + w * h * 4 + ((w + 31) // 32) * 4 * h
            if (header, dw, dh, dp, db, compression) != (40, w, h * 2, 1, 32, 0) or len(payload) != expected_size:
                raise PreparationError("Unsupported ICO DIB layout")
        frames.append({"width": w, "height": h, "encoding": encoding})
        ranges.append((offset, offset + size))
    return frames


def encode_dib(width: int, height: int, pixels: bytes) -> bytes:
    stride = ((width + 31) // 32) * 4
    xor = bytearray()
    mask = bytearray(stride * height)
    for y in range(height - 1, -1, -1):
        for x in range(width):
            index = (y * width + x) * 4
            r, g, b, a = pixels[index:index + 4]
            xor.extend((b, g, r, a))
            if a == 0:
                mask[(height - 1 - y) * stride + x // 8] |= 1 << (7 - x % 8)
    return struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, len(xor), 0, 0, 0, 0) + xor + mask


def encode_ico(width: int, height: int, pixels: bytes, frames: list[dict]) -> bytes:
    entries = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    payloads = bytearray()
    offset = 6 + 16 * len(frames)
    for frame in frames:
        w, h = frame["width"], frame["height"]
        scaled = resize_contain(width, height, pixels, w, h)
        payload = encode_rgba(w, h, scaled) if frame["encoding"] == "png" else encode_dib(w, h, scaled)
        entries.extend(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(payload), offset))
        payloads.extend(payload)
        offset += len(payload)
    return bytes(entries + payloads)


def git_output(repo: Path, *args: str) -> bytes:
    try:
        return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise PreparationError("Read-only Git inventory failed") from error


def adoption_inventory(repo: Path) -> dict:
    """Tracked working-copy text references, not a runtime/dependency proof."""
    consumers = []
    skipped = []
    for name in sorted(git_output(repo, "ls-files", "-z").decode("utf-8").split("\0")):
        if not name or name in SELF_PATHS:
            continue
        path = repo / name
        if path.is_symlink() or not path.is_file():
            skipped.append(name)
            continue
        data = path.read_bytes()
        if b"\0" in data:
            continue
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            continue
        references = []
        for number, line in enumerate(text.splitlines(), 1):
            assets = sorted(set(match.group(0) for match in ICON_REFERENCE.finditer(line)))
            wildcard = path.suffix in (".csproj", ".props", ".targets") and bool(re.search(r'\b(?:Include|Update)=["\']Assets/\*\*', line))
            if assets or wildcard:
                references.append({"line": number, "assets": assets, "kind": "resource-glob" if wildcard else "literal",
                                   "text": line.strip()})
        if references:
            category = ("test" if name.startswith("tests/") else "packaging" if name.startswith(("scripts/", "packaging/", ".github/"))
                        else "documentation" if name.startswith("docs/") or path.suffix == ".md" else "application")
            consumers.append({"path": name, "sha256": digest(data), "category": category, "references": references})
    return {"method": "git ls-files; UTF-8 working-copy literal image references plus Assets/** project resource globs",
            "limitations": ["Ignored/untracked files, binaries, symlinks and computed references are not analysed.",
                            "References include tests, documentation and generated destination names, not just product reads.",
                            "The converter and its tests are excluded to avoid self-references."],
            "skipped_missing_or_symlink": skipped, "consumers": consumers}


def prepare(source: Path, output: Path) -> Path:
    repo = REPO_ROOT.resolve(strict=True)
    source = source.resolve(strict=True)
    # Check the lexical leaf too: resolve() would otherwise hide a dangling link.
    if output.exists() or output.is_symlink():
        raise PreparationError("Refusing to overwrite an existing output path")
    output = output.resolve(strict=False)
    if output == repo or output.is_relative_to(repo):
        raise PreparationError("Output must be outside the checkout; product inputs are read-only")
    if not output.parent.is_dir():
        raise PreparationError("Output parent must already exist")
    source_data = read_input(source)
    if digest(source_data) != SELECTED_SOURCE_SHA256:
        raise PreparationError(f"Source SHA-256 mismatch; expected {SELECTED_SOURCE_SHA256}")
    width, height, pixels = decode_rgba(source_data)
    inputs = {name: read_input(repo / name) for name in (MASTER, PNG, ICO)}
    png_width, png_height = png_size(inputs[PNG])
    frames = ico_frames(inputs[ICO])
    png_pixels = resize_contain(width, height, pixels, png_width, png_height)
    generated = {MASTER: source_data, PNG: encode_rgba(png_width, png_height, png_pixels),
                 ICO: encode_ico(width, height, pixels, frames)}
    inventory = adoption_inventory(repo)
    manifest = {
        "schema": "meowchan.branding-prepare.v1", "mode": "prepare-only", "adoption_authorized": False,
        "source": {"path": str(source), "sha256": digest(source_data), "width": width, "height": height, "format": "RGBA8"},
        "repository": {"head": git_output(repo, "rev-parse", "HEAD").decode("ascii").strip(),
                       "inventory_basis": "current tracked working-copy bytes, not a clean-tree assertion"},
        "rendering": {"method": "alpha-weighted integer area sampling in encoded RGB; contain; centred transparent padding",
                      "crop": False, "matte": False, "master": "byte-identical source including metadata",
                      "derivative_metadata": "IHDR/IDAT/IEND only; no colour-space conversion"},
        "existing_inputs": [{"path": name, "sha256": digest(data), "bytes": len(data)} for name, data in inputs.items()],
        "sizes": {"png": {"width": png_width, "height": png_height}, "ico_frames": frames},
        "prepared_files": [{"path": name, "candidate_product_path": name, "sha256": digest(data), "bytes": len(data)}
                           for name, data in generated.items()],
        "inventory": inventory,
        "gates": {"global_both_themes_all_surfaces": "open user decision; not authorized by preparation",
                  "visual_small_sizes_light_dark": "not assessed", "rights_and_provenance": "not assessed",
                  "platform_package_validation": "not run; existing package scripts remain authoritative"},
    }
    # All validation/rendering happens before directory creation. Exclusive mkdir
    # and xb writes never reuse existing files, including hardlinks and symlinks.
    # A write failure leaves a visibly incomplete directory; it is never reused.
    output.mkdir()
    for name, data in generated.items():
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(data)
    manifest_path = output / "adoption-inventory.json"
    with manifest_path.open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2, sort_keys=True, ensure_ascii=False)
        stream.write("\n")
    return manifest_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, type=Path, help="Explicit selected PNG; SHA-256 is pinned, not overridable")
    parser.add_argument("--output", required=True, type=Path, help="New directory outside checkout, existing parent; never an adoption path")
    args = parser.parse_args(argv)
    try:
        manifest = prepare(args.source, args.output)
    except (PreparationError, OSError, RuntimeError) as error:
        print(f"Branding preparation refused: {error}", file=sys.stderr)
        return 1
    print(json.dumps({"mode": "prepare-only", "inventory": str(manifest), "adoption_authorized": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
