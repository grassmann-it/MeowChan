#!/usr/bin/env python3
"""Offline, deterministic fixtures; outputs only to the explicitly supplied directory."""
import argparse
import base64
import hashlib
import json
import pathlib
import struct
import zlib
from PIL import Image


def png(path, width, height, alpha=False):
    def chunk(kind, value):
        return struct.pack('>I', len(value)) + kind + value + struct.pack('>I', zlib.crc32(kind + value))
    compressor = zlib.compressobj(1)
    with path.open('wb') as out:
        out.write(b'\x89PNG\r\n\x1a\n')
        out.write(chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6 if alpha else 2, 0, 0, 0)))
        rows = {}
        for y in range(height):
            # One-pixel grid and larger blocks make fit/physical 1:1 inspectable.
            key = y % 256
            if key not in rows:
                row = bytearray()
                for x in range(width):
                    c = 30 if x % 16 == 0 or y % 16 == 0 else (210 if (x // 64 + y // 64) % 2 else 110)
                    row.extend((c, (x % 256), (y % 256)))
                    if alpha:
                        row.append(96 if (x // 16 + y // 16) % 2 else 255)
                rows[key] = b'\x00' + row
            data = compressor.compress(rows[key])
            if data:
                out.write(chunk(b'IDAT', data))
        out.write(chunk(b'IDAT', compressor.flush()))
        out.write(chunk(b'IEND', b''))


def digest(path, kind):
    h = hashlib.new(kind)
    with path.open('rb') as inp:
        for block in iter(lambda: inp.read(1024 * 1024), b''):
            h.update(block)
    return h.digest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)  # Never overwrite an existing archive.
    png(root / 'transparent-grid.png', 128, 96, True)
    photo = Image.open(pathlib.Path(__file__).parent / 'fixtures/nasa-PIA18033.jpg').convert('RGB')
    photo.crop((0, 120, 640, 520)).save(root / 'photo-landscape.jpg', quality=92, dpi=(96, 96))
    photo.crop((120, 0, 520, 640)).save(root / 'photo-portrait.jpg', quality=92, dpi=(96, 96))
    png(root / 'very-long.png', 64, 16000)
    png(root / 'near-budget.png', 10000, 7900)  # 79M < unchanged 80M inspection budget.
    png(root / 'over-pixels.png', 9001, 9000)  # 81.009M > inspection budget.
    (root / 'damaged.png').write_bytes(b'not an image\x00\xff')
    (root / 'missing.png').write_bytes(b'declared then removed')
    # Sparse physical allocation; hash/manifest length still represent all bytes.
    with (root / 'over-bytes.png').open('wb') as out:
        out.truncate(64 * 1024 * 1024 + 1)
    entries = {}
    cases = []
    for path in sorted(root.iterdir()):
        entries[path.name] = {
            'MediaIdentifier': path.stem, 'Variant': 'original',
            'SourceUrl': 'https://offline.invalid/' + path.name,
            'FileName': path.name, 'OriginalFileName': path.name,
            'Length': path.stat().st_size, 'Sha256': digest(path, 'sha256').hex(),
            'Md5': base64.b64encode(digest(path, 'md5')).decode('ascii')}
        cases.append({'name': path.name, 'expectedError': path.stem in
                      {'damaged', 'missing', 'over-pixels', 'over-bytes'},
                      'sha256': entries[path.name]['Sha256'], 'length': path.stat().st_size})
    (root / '.meowchan-manifest.json').write_text(json.dumps({'Version': 1, 'Entries': entries}, indent=2))
    (root / 'missing.png').unlink()
    (root / 'comparison-fixtures.json').write_text(json.dumps(cases, indent=2))
    print(root)


if __name__ == '__main__':
    main()
