#!/usr/bin/env python3
"""Inspect a real cross-published compiler without executing target architecture code.

Usage: cross_publish_probe.py <published-directory> <RID> [--report result.json]
Run after an actual self-contained publish, which must itself generate toolchain.json.
This validates package architecture and integrity, not native target execution.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct


TARGETS = {
    "linux-x64": ("elf", 62, "MeowChan.PluginCompiler", "libhostfxr.so", "libcoreclr.so"),
    "linux-arm64": ("elf", 183, "MeowChan.PluginCompiler", "libhostfxr.so", "libcoreclr.so"),
    "osx-x64": ("macho", 0x01000007, "MeowChan.PluginCompiler", "libhostfxr.dylib", "libcoreclr.dylib"),
    "osx-arm64": ("macho", 0x0100000C, "MeowChan.PluginCompiler", "libhostfxr.dylib", "libcoreclr.dylib"),
    "win-x64": ("pe", 0x8664, "MeowChan.PluginCompiler.exe", "hostfxr.dll", "coreclr.dll"),
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def pe_headers(data):
    require(data[:2] == b"MZ", "Expected a PE image")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    require(data[pe:pe + 4] == b"PE\0\0", "Invalid PE signature")
    machine, section_count = struct.unpack_from("<HH", data, pe + 4)
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    return machine, section_count, optional, optional_size, magic


def inspect_anycpu(path):
    data = path.read_bytes()
    machine, section_count, optional, optional_size, magic = pe_headers(data)
    require(machine == 0x14C and magic == 0x10B, "Compiler managed DLL must be AnyCPU PE32/I386")
    require(optional_size >= 96 + 15 * 8, "PE optional header has no CLI directory")
    cli_rva, cli_size = struct.unpack_from("<II", data, optional + 96 + 14 * 8)
    require(cli_rva != 0 and cli_size >= 72, "Compiler must have a managed CLI header")
    cli = None
    for index in range(section_count):
        section = optional + optional_size + index * 40
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, section + 8)
        if virtual_address <= cli_rva < virtual_address + max(virtual_size, raw_size):
            delta = cli_rva - virtual_address
            require(delta + 72 <= raw_size, "CLI header lies outside section file bytes")
            cli = raw_pointer + delta
            break
    require(cli is not None, "CLI header cannot be resolved into file bytes")
    flags = struct.unpack_from("<I", data, cli + 16)[0]
    native_rva, native_size = struct.unpack_from("<II", data, cli + 64)
    require(flags & 1 and not flags & (2 | 0x10 | 0x20000), "Compiler IL must not require/prefer 32-bit or have a native entrypoint")
    require(native_rva == 0 and native_size == 0, "Expected neutral IL, not architecture-specific ReadyToRun")
    return {"machine": hex(machine), "optionalMagic": hex(magic), "corFlags": hex(flags), "anyCpuIl": True}


def inspect_native(path, kind, expected):
    with path.open("rb") as stream:
        data = stream.read(65536)
    if kind == "elf":
        require(data[:4] == b"\x7fELF" and data[4] == 2, f"Expected ELF64: {path.name}")
        require(data[5] in (1, 2), "Unsupported ELF endianness")
        actual = struct.unpack_from("<H" if data[5] == 1 else ">H", data, 18)[0]
    elif kind == "macho":
        require(data[:4] in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"), f"Expected thin Mach-O64: {path.name}")
        actual = struct.unpack_from("<I" if data[:4] == b"\xcf\xfa\xed\xfe" else ">I", data, 4)[0]
    else:
        actual = pe_headers(data)[0]
    require(actual == expected, f"Wrong native architecture in {path.name}: {hex(actual)} != {hex(expected)}")
    return {"format": kind, "machine": hex(actual), "sha256": file_hash(path)}


def file_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def hash_parts(*parts):
    digest = hashlib.sha256()
    for part in parts:
        encoded = part.encode("utf-8")
        digest.update(struct.pack("!i", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest()


def file_set_hash(files):
    parts = []
    for entry in sorted(files, key=lambda item: item["Path"].encode("utf-16-be")):
        parts.extend((entry["Role"], entry["Path"], entry["Sha256"]))
    return hash_parts(*parts)


def inspect_manifest(root):
    manifest = json.loads((root / "toolchain.json").read_text(encoding="utf-8"))
    require(manifest["SchemaVersion"] == 1 and manifest["TargetFramework"] == "net10.0", "Unexpected toolchain schema/TFM")
    files = manifest["Files"]
    require(4 <= len(files) <= 512, "Unexpected manifest file count")
    paths = set()
    for entry in files:
        relative = entry["Path"]
        require(relative and not relative.startswith("/") and not any(c in relative for c in ("\\", ":")), "Invalid manifest path")
        require(all(part not in ("", ".", "..") for part in relative.split("/")), "Unnormalized manifest path")
        require(relative.casefold() not in paths, "Duplicate manifest path")
        paths.add(relative.casefold())
        target = root / relative
        require(target.resolve().is_relative_to(root), "Manifest file escapes delivery root")
        require(entry["Role"] in ("compiler", "framework", "sdk"), "Unknown manifest file role")
        require(file_hash(target) == entry["Sha256"], f"Delivered bytes do not match manifest: {relative}")
    compilers = [entry for entry in files if entry["Role"] == "compiler"]
    sdks = [entry for entry in files if entry["Role"] == "sdk"]
    frameworks = [entry for entry in files if entry["Role"] == "framework"]
    require({entry["Path"] for entry in compilers} >= {"MeowChan.PluginCompiler.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"}, "Compiler inputs absent from manifest")
    require(len(sdks) == 1 and sdks[0]["Path"] == "MeowChan.PluginSdk.dll" and frameworks, "SDK/framework inputs absent")
    require(manifest["CompilerFingerprint"] == file_set_hash(compilers), "Compiler fingerprint mismatch")
    require(manifest["SdkFingerprint"] == sdks[0]["Sha256"], "SDK fingerprint mismatch")
    require(manifest["ReferenceFingerprint"] == file_set_hash(sdks + frameworks), "Reference fingerprint mismatch")
    return {"fileCount": len(files), "compilerDllSha256": file_hash(root / "MeowChan.PluginCompiler.dll"),
            "compilerFingerprint": manifest["CompilerFingerprint"], "referenceFingerprint": manifest["ReferenceFingerprint"],
            "toolchainFingerprint": hash_parts(manifest["TargetFramework"], manifest["Options"], manifest["CompilerFingerprint"], manifest["SdkFingerprint"], manifest["ReferenceFingerprint"]),
            "manifestSha256": file_hash(root / "toolchain.json")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("rid", choices=TARGETS)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    root = args.directory.resolve()
    kind, machine, *native_files = TARGETS[args.rid]
    report = {"rid": args.rid, "directory": str(root), "targetCodeExecuted": False,
              "managedCompiler": inspect_anycpu(root / "MeowChan.PluginCompiler.dll"),
              "nativeFiles": {name: inspect_native(root / name, kind, machine) for name in native_files},
              "integrity": inspect_manifest(root)}
    rendered = json.dumps(report, indent=2) + "\n"
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
