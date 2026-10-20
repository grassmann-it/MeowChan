#!/usr/bin/env python3
"""Regenerate tiny synthetic CC0 video-only VP8/VP9 WebM acceptance inputs.

Requires ffmpeg with libvpx/libvpx-vp9 only when regenerating, never on CI.
No downloaded media, audio, photographs, fonts, or third-party artwork is used.
Bit-exact output is reproducible with the same ffmpeg/libvpx build recorded here.
"""
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'tests/Fixtures'


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    version = subprocess.run(['ffmpeg', '-version'], check=True, capture_output=True, text=True).stdout.splitlines()[0]
    files = []
    for codec, encoder in [('vp8', 'libvpx'), ('vp9', 'libvpx-vp9')]:
        path = OUTPUT / f'gallery-playback-{codec}.webm'
        command = ['ffmpeg', '-nostdin', '-y', '-hide_banner', '-loglevel', 'error',
                   '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=15', '-t', '12',
                   '-an', '-c:v', encoder, '-threads', '1', '-b:v', '48k',
                   '-g', '15', '-deadline', 'good', '-cpu-used', '4',
                   '-pix_fmt', 'yuv420p', '-fflags', '+bitexact', '-flags:v', '+bitexact',
                   '-map_metadata', '-1', '-metadata', 'encoder=', str(path)]
        subprocess.run(command, check=True)
        files.append({'file': path.name, 'codec': codec, 'seconds': 12, 'width': 160, 'height': 90,
                      'fps': 15, 'audio': False, 'bytes': path.stat().st_size,
                      'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                      'command': command[:-1] + [path.name]})
    manifest = {'license': 'CC0-1.0', 'provenance': 'Synthetic moving testsrc2 pattern generated locally; no external media inputs',
                'generator': 'scripts/generate-playback-fixtures.py', 'generator_version': version,
                'reproduction': 'Exact bytes require the same ffmpeg/libvpx build; CI uses checked-in pinned bytes', 'files': files}
    (OUTPUT / 'gallery-playback-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    main()
