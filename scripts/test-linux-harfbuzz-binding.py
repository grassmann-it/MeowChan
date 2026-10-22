#!/usr/bin/env python3
"""Reproduce host/bundled HarfBuzz ABI collision and verify local deep binding.

Run against a delivered libHarfBuzzSharp.so, in a private namespace when used as
release evidence. No .NET build, font files, display or network is needed. The
baseline may abort on incompatible hosts; only child processes call native APIs.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import resource
import subprocess
import sys


def child(library, protected):
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    # This is deliberately the difficult order, independent of video/GUI timing.
    ctypes.CDLL('libharfbuzz.so.0', mode=os.RTLD_NOW | os.RTLD_GLOBAL)
    flags = os.RTLD_NOW | os.RTLD_DEEPBIND if protected else os.RTLD_LAZY
    native = ctypes.CDLL(str(library), mode=flags | os.RTLD_LOCAL)
    native.hb_face_get_empty.restype = ctypes.c_void_p
    native.hb_font_create.argtypes = [ctypes.c_void_p]
    native.hb_font_create.restype = ctypes.c_void_p
    native.hb_font_destroy.argtypes = [ctypes.c_void_p]
    font = native.hb_font_create(native.hb_face_get_empty())
    if not font:
        raise RuntimeError('Native HarfBuzz could not create the empty font')
    native.hb_font_destroy(font)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('library', type=Path)
    parser.add_argument('--require-baseline-abort', action='store_true')
    parser.add_argument('--child', choices=('baseline', 'protected'), help=argparse.SUPPRESS)
    args = parser.parse_args()
    library = args.library.resolve(strict=True)
    if not sys.platform.startswith('linux') or not hasattr(os, 'RTLD_DEEPBIND'):
        parser.error('This regression requires Linux/glibc dynamic binding')
    if args.child:
        child(library, args.child == 'protected')
        return 0
    outcomes = {}
    for mode in ('baseline', 'protected'):
        process = subprocess.run([sys.executable, str(Path(__file__).resolve()), str(library), '--child', mode],
                                 capture_output=True, timeout=10, text=True)
        outcomes[mode] = {'exitCode': process.returncode, 'stderr': process.stderr[-4096:]}
    passed = outcomes['protected']['exitCode'] == 0
    if args.require_baseline_abort:
        passed = passed and outcomes['baseline']['exitCode'] == -6
    print(json.dumps({'passed': passed, 'library': str(library),
                      'sha256': hashlib.sha256(library.read_bytes()).hexdigest(), 'outcomes': outcomes}, indent=2))
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
