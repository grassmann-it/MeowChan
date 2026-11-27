#!/usr/bin/env python3
"""Extract managed MeowChan assemblies from the checksum-verified 1.1.3 AppImage payload."""

import argparse
import hashlib
from pathlib import Path
import struct
import zlib

MARKER = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def extract(bundle, destination):
    payload = bundle.read_bytes()
    marker = payload.index(MARKER)
    offset, = struct.unpack_from("<Q", payload, marker - 8)
    major, minor, count = struct.unpack_from("<III", payload, offset)
    if (major, minor) != (6, 0):
        raise ValueError("Unexpected .NET bundle format")
    cursor = offset + 12

    def read_string():
        nonlocal cursor
        count = 0
        shift = 0
        while True:
            byte = payload[cursor]
            cursor += 1
            count |= (byte & 127) << shift
            if byte < 128:
                break
            shift += 7
        value = payload[cursor:cursor + count].decode()
        cursor += count
        return value

    read_string()
    cursor += 40
    for _ in range(count):
        start, length, compressed, kind = struct.unpack_from("<QQQB", payload, cursor)
        cursor += 25
        name = read_string()
        if kind != 1 or not name.startswith(("MeowChan", "libmeowchan")):
            continue
        if Path(name).name != name:
            raise ValueError("Unexpected assembly path in .NET bundle")
        data = payload[start:start + (compressed or length)]
        if compressed:
            data = zlib.decompress(data, wbits=-15)
        if len(data) != length:
            raise ValueError(f"Unexpected length of {name}")
        (destination / name).write_bytes(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("appimage", type=Path)
    parser.add_argument("checksums", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    expected, = [line.split()[0] for line in args.checksums.read_text().splitlines()
                 if line.split()[-1].lstrip("*") == args.appimage.name]
    with args.appimage.open("rb") as asset:
        if hashlib.file_digest(asset, "sha256").hexdigest() != expected:
            raise ValueError("The published AppImage checksum does not match")
    from subprocess import run
    from tempfile import TemporaryDirectory

    args.destination.mkdir(mode=0o700)
    with TemporaryDirectory() as temporary:
        run([str(args.appimage.resolve()), "--appimage-extract", "usr/lib/meowchan/MeowChan"],
            cwd=temporary, stdout=-3, check=True)
        extract(Path(temporary, "squashfs-root/usr/lib/meowchan/MeowChan"), args.destination)
    if not (args.destination / "MeowChan.dll").is_file():
        raise ValueError("The desktop writer is missing from the bundle")
