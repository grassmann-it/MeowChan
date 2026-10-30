#!/usr/bin/env python3
"""Build the Linux system-VLC tar, private-VLC tar and pinned AppImage.

prepare-runtime operates on a native Ubuntu 24.04 package installation. Downloads
are explicit CI operations; package is offline and verifies every selected input.
"""
from __future__ import annotations

import argparse
import ctypes
import gzip
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import tarfile
import tempfile
import urllib.request

RIDS = {"linux-x64": (62, "x86_64", "amd64"), "linux-arm64": (183, "aarch64", "arm64")}
TOOL_VERSION = "1.9.1"
RUNTIME_VERSION = "20251108"
RUNTIME_COMMIT = "dd6cebedcbddde9c82f89b011e8e1d40b6e43868"
PINS = {
    "linux-x64": {"appimagetool": "ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0",
                  "runtime": "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"},
    "linux-arm64": {"appimagetool": "f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158",
                    "runtime": "00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444"},
}
# Host ABI and graphics drivers are deliberately not transported between distros.
# Every other DT_NEEDED edge is copied, including dependencies of ALL selected plugins.
HOST_LIBRARIES = re.compile(r"^(?:ld-linux[^/]*|lib(?:c|m|dl|rt|pthread|resolv|util|anl)\.so\.[0-9]+|"
                            r"lib(?:GL|GLX|EGL|GLESv[12]|GLdispatch|OpenGL|vulkan|drm|gbm)\.so\.[0-9]+)$")
PLUGIN_FILES = {
    "access": ("access_imem", "imem", "filesystem", "attachment"),
    "demux": ("mkv", "mp4", "avformat", "avi", "ogg", "asf", "wav", "aiff", "au", "rawvid", "image", "es", "h26x"),
    "codec": ("avcodec", "vpx", "vorbis", "opus", "png", "jpeg", "rawvideo", "araw", "flac", "theora", "a52", "faad"),
    # xcb_window supplies the separate "vout window" / embed-xid capability;
    # xcb_x11 alone is only a display and cannot embed into Avalonia's XID.
    "video_output": ("vmem", "xcb_window", "xcb_x11", "xcb_xv"),
    "audio_output": ("alsa", "pulse", "amem"),
    "video_filter": ("swscale", "scale", "transform", "croppadd", "deinterlace", "blend"),
}
PLUGIN_CATEGORIES = ("audio_filter", "audio_mixer", "video_chroma", "packetizer")
REQUIRED_PLUGINS = ("access_imem", "mkv", "mp4", "avformat", "avi", "avcodec", "vorbis", "opus", "png", "vmem", "xcb_window", "xcb_x11", "alsa", "pulse")


def run(*args: str, cwd: Path | None = None) -> str:
    try:
        result = subprocess.run(args, cwd=cwd, check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as error:
        error.add_note((error.stderr or error.stdout or "No command diagnostics.")[-8000:])
        raise
    return result.stdout


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def file_hashes(root: Path) -> dict[str, str]:
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Unmaterialized payload symlink: {path}")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = digest(path)
        elif not path.is_dir():
            raise ValueError(f"Unsupported payload type: {path}")
    return result


def verify_files(root: Path, expected: dict[str, str]) -> None:
    if file_hashes(root) != expected:
        raise ValueError(f"Payload inventory mismatch: {root}")


def merge_floor(records: list[dict[str, str]]) -> dict[str, str]:
    result = {}
    for record in records:
        for family, value in record.items():
            if family not in result or tuple(map(int, value.split("."))) > tuple(map(int, result[family].split("."))):
                result[family] = value
    return result


def payload_symbols(root: Path, rid: str) -> dict[str, dict[str, str]]:
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_file():
            with path.open("rb") as stream:
                header = stream.read(4)
            if header == b"\x7fELF":
                check_elf(path, rid)
                result[path.relative_to(root).as_posix()] = symbols(path)
    return result


def elf_machine(path: Path) -> int:
    with path.open("rb") as stream:
        header = stream.read(20)
    if len(header) != 20 or header[:6] != b"\x7fELF\x02\x01":
        raise ValueError(f"Expected a little-endian ELF64 file: {path}")
    return struct.unpack_from("<H", header, 18)[0]


def check_elf(path: Path, rid: str) -> None:
    if elf_machine(path) != RIDS[rid][0]:
        raise ValueError(f"ELF architecture mismatch for {rid}: {path}")


def needed(path: Path) -> list[str]:
    return re.findall(r"\(NEEDED\).*?\[([^]]+)\]", run("readelf", "--dynamic", str(path)))


def symbols(path: Path) -> dict[str, str]:
    text = run("readelf", "--version-info", str(path))
    # Definitions exported by a bundled libstdc++ are not host requirements.
    text = text.partition("Version needs section")[2]
    result = {}
    for family in ("GLIBC", "GLIBCXX", "CXXABI"):
        versions = set(re.findall(r"\b" + family + r"_([0-9]+(?:\.[0-9]+)+)\b", text))
        if versions:
            result[family] = max(versions, key=lambda s: tuple(map(int, s.split("."))))
    return result


def dependency_paths(path: Path) -> dict[str, Path]:
    text = run("ldd", str(path))
    if "not found" in text:
        raise ValueError(f"Unresolved distro ELF dependency: {path}\n{text}")
    # ldd paths can contain spaces; the trailing load address delimits the path.
    return {name: Path(location) for name, location in re.findall(
        r"^\s*(\S+)\s+=>\s+(/.+?)\s+\(0x[0-9a-fA-F]+\)\s*$", text, re.M)}


def package_owner(path: Path) -> str:
    # Ubuntu usrmerge ownership metadata can retain either /lib or /usr/lib.
    candidates = [path.resolve(), path]
    for candidate in list(candidates):
        text = str(candidate)
        if text.startswith("/usr/"):
            candidates.append(Path(text[4:]))
        elif text.startswith(("/lib/", "/lib64/")):
            candidates.append(Path("/usr" + text))
    for candidate in candidates:
        try:
            output = run("dpkg-query", "--search", str(candidate))
            matches = [line.rsplit(": ", 1)[0] for line in output.splitlines() if ": " in line]
            if len(set(matches)) == 1:
                return matches[0]
        except subprocess.CalledProcessError:
            pass
    raise ValueError(f"No unambiguous distro package owns {path}")


def package_metadata(package: str) -> dict:
    fields = run("dpkg-query", "-W", "-f=${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\t${Architecture}", package).split("\t")
    binary, version, source, source_version, architecture = fields
    stanza = run("apt-cache", "show", f"{binary}={version}")
    def field(name: str) -> str:
        match = re.search(r"^" + re.escape(name) + r": (.+)$", stanza, re.M)
        if not match:
            raise ValueError(f"Missing {name} provenance for {binary}={version}")
        return match.group(1)
    return {"package": binary, "version": version, "source": source or binary.split(":")[0],
            "source_version": source_version or version, "architecture": architecture,
            "deb_filename": field("Filename"), "deb_sha256": field("SHA256")}


def verify_distro_binary(package: dict, mappings: list[dict], cache: Path) -> None:
    """Bind copied bytes to an exact, checksum-verified distro binary archive."""
    reserve(cache, 256 * 1024 ** 2)
    with tempfile.TemporaryDirectory(prefix="deb-", dir=cache) as temporary:
        directory = Path(temporary)
        run("apt-get", "download", f"{package['package']}={package['version']}", cwd=directory)
        archives = list(directory.glob("*.deb"))
        if len(archives) != 1 or digest(archives[0]) != package["deb_sha256"]:
            raise ValueError(f"Exact distro binary checksum mismatch: {package['package']}")
        expected = [item for item in mappings if item["package"] == package["package"]]
        paths = {}
        for index, item in enumerate(expected):
            name = str(Path(item["original_path"]).resolve()).lstrip("/")
            variants = {name}
            if name.startswith("usr/"):
                variants.add(name[4:])
            elif name.startswith(("lib/", "lib64/")):
                variants.add("usr/" + name)
            for variant in variants:
                paths.setdefault(variant, []).append(index)
        verified = set()
        process = subprocess.Popen(["dpkg-deb", "--fsys-tarfile", str(archives[0])], stdout=subprocess.PIPE)
        try:
            with tarfile.open(fileobj=process.stdout, mode="r|") as archive:
                for entry in archive:
                    name = entry.name.removeprefix("./")
                    if entry.isfile() and name in paths:
                        value = hashlib.sha256()
                        with archive.extractfile(entry) as stream:
                            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                                value.update(chunk)
                        for index in paths[name]:
                            if value.hexdigest() != expected[index]["original_sha256"]:
                                raise ValueError(f"Installed runtime differs from its exact .deb: {name}")
                            verified.add(index)
            if process.wait() != 0:
                raise ValueError("Could not inspect the distro binary archive.")
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait()
        if len(verified) != len(expected):
            raise ValueError(f"Some private runtime files were not mapped to their exact .deb: {package['package']}")
        package["deb_verified"] = True
        package["verified_original_files"] = len(verified)


def selected_plugins(root: Path) -> list[Path]:
    selected = set()
    for category, names in PLUGIN_FILES.items():
        for name in names:
            path = root / category / f"lib{name}_plugin.so"
            if path.is_file():
                selected.add(path)
    for category in PLUGIN_CATEGORIES:
        selected.update((root / category).glob("*.so"))
    actual = {p.name for p in selected}
    missing = [name for name in REQUIRED_PLUGINS if f"lib{name}_plugin.so" not in actual]
    if missing:
        raise ValueError(f"Distro lacks required gallery plugin(s): {missing}")
    return sorted(selected)


def collect_closure(inputs: list[tuple[Path, Path]], rid: str) -> tuple[dict[str, Path], list[dict]]:
    queue = list(inputs)
    sources: dict[str, Path] = {}
    edges = []
    while queue:
        source, relative = queue.pop(0)
        check_elf(source, rid)
        key = relative.as_posix()
        if key in sources:
            if sources[key].resolve() != source.resolve():
                raise ValueError(f"Colliding private SONAME: {relative}")
            continue
        sources[key] = source
        resolved = dependency_paths(source)
        for name in needed(source):
            if "/" in name:
                raise ValueError(f"Absolute/nonrelocatable DT_NEEDED {name} in {source}")
            host = bool(HOST_LIBRARIES.fullmatch(name))
            edges.append({"from": key, "needed": name, "host": host})
            if not host:
                if name not in resolved:
                    raise ValueError(f"Could not resolve {name} from {source}")
                queue.append((resolved[name], Path("lib") / name))
    return sources, edges


def native_host(rid: str) -> None:
    machine = platform.machine().lower()
    if (rid == "linux-x64" and machine not in ("x86_64", "amd64")) or (rid == "linux-arm64" and machine not in ("aarch64", "arm64")):
        raise ValueError("Native Linux runtime packaging requires its matching CPU runner.")
    release = Path("/etc/os-release").read_text()
    if not re.search(r'^ID="?ubuntu"?$', release, re.M) or not re.search(r'^VERSION_ID="?24\.04"?$', release, re.M):
        raise ValueError("The native Linux bundle baseline is Ubuntu 24.04.")


def reserve(path: Path, additional: int = 0) -> None:
    existing = path.resolve()
    while not existing.exists():
        existing = existing.parent
    if shutil.disk_usage(existing).free - additional < 2 * 1024 ** 3:
        raise ValueError("Packaging requires at least 2 GiB free reserve after staging.")


def check_vlc_version(version: str) -> None:
    # Ubuntu 24.04 maintains 3.0.20 with distro security patches. The gallery
    # uses the LibVLC 3 ABI and has no 3.0.21-specific API requirement.
    number = re.match(r"3\.(\d+)\.(\d+)(?=\D|$)", version)
    if not number or (int(number[1]), int(number[2])) < (0, 20):
        raise ValueError(f"Installed VLC is below required 3.0.20 or outside the supported LibVLC 3 ABI: {version}; update the baseline packages.")


def runtime_inputs() -> tuple[list[tuple[Path, Path]], list[Path]]:
    triple = run("dpkg-architecture", "-qDEB_HOST_MULTIARCH").strip()
    system_lib = Path("/usr/lib") / triple
    plugin_root = system_lib / "vlc/plugins"
    inputs = [(system_lib / "libvlc.so.5", Path("lib/libvlc.so.5")),
              (system_lib / "libvlccore.so.9", Path("lib/libvlccore.so.9"))]
    inputs += [(p, Path("lib/vlc/plugins") / p.relative_to(plugin_root)) for p in selected_plugins(plugin_root)]
    data_root = Path("/usr/share/vlc")
    if not data_root.is_dir():
        raise ValueError("vlc-data is required.")
    return inputs, [p for p in data_root.rglob("*") if p.is_file()]


def refresh_runtime_packages(rid: str) -> dict:
    """Update stale runner packages before a separate process loads/copies VLC.

    Runner images can retain superseded binaries whose exact archives vanished
    from current APT indices. Never attribute those bytes to a newer archive.
    """
    native_host(rid)
    updates = []
    for attempt in range(3):
        inputs, data_files = runtime_inputs()
        sources, _ = collect_closure(inputs, rid)
        owners = sorted({package_owner(path) for path in [*sources.values(), *data_files]})
        unavailable = []
        for owner in owners:
            try:
                package_metadata(owner)
            except (ValueError, subprocess.CalledProcessError):
                unavailable.append(owner)
        if not unavailable:
            return {"runtime": rid, "updated_packages": updates, "verified_packages": owners}
        if attempt == 2:
            raise ValueError(f"Runtime packages still lack exact repository provenance after refresh: {unavailable}")
        # APT must actually install its available candidate; no inventory version
        # substitution, downgrade, whole-host upgrade or removal is permitted.
        run("sudo", "apt-get", "install", "--yes", "--only-upgrade", "--no-remove", *unavailable)
        updates.append(unavailable)
    raise AssertionError("Unreachable runtime refresh state")


def prepare_runtime(rid: str, output: Path) -> dict:
    native_host(rid)
    if output.exists():
        raise ValueError("Runtime output must be fresh; never merge native inventories.")
    inputs, data_files = runtime_inputs()
    library = ctypes.CDLL(str(inputs[0][0]))
    library.libvlc_get_version.restype = ctypes.c_char_p
    version = library.libvlc_get_version().decode()
    check_vlc_version(version)
    sources, edges = collect_closure(inputs, rid)
    mappings = []
    data_root = Path("/usr/share/vlc")
    reserve(output, sum(p.stat().st_size for p in sources.values()) + sum(p.stat().st_size for p in data_files) + 64 * 1024 ** 2)
    runtime = output / "runtime"
    runtime.mkdir(parents=True)
    packages = {}
    licenses = output / "licenses"
    licenses.mkdir()
    floors: dict[str, str] = {}
    for relative, source in sorted(sources.items()):
        target = runtime / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        target.chmod(0o755)
        owner = package_owner(source)
        packages.setdefault(owner, None)
        before = digest(target)
        rpath = "$ORIGIN"
        upward = os.path.relpath(runtime / "lib", target.parent)
        if upward != ".":
            rpath += ":$ORIGIN/" + upward
        run("patchelf", "--set-rpath", rpath, str(target))
        check_elf(target, rid)
        requirements = symbols(target)
        for family, value in requirements.items():
            if family not in floors or tuple(map(int, value.split("."))) > tuple(map(int, floors[family].split("."))):
                floors[family] = value
        mappings.append({"file": relative, "original_path": str(source), "package": owner,
                         "original_sha256": before, "sha256": digest(target), "runpath": rpath,
                         "needed": needed(target), "symbol_requirements": requirements})
    for source in sorted(data_files):
        relative = Path("lib/vlc/share") / source.relative_to(data_root)
        target = runtime / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        target.chmod(0o644)
        owner = package_owner(source)
        packages.setdefault(owner, None)
        mappings.append({"file": relative.as_posix(), "original_path": str(source), "package": owner,
                         "original_sha256": digest(source), "sha256": digest(target)})
    for owner in sorted(packages):
        packages[owner] = package_metadata(owner)
        verify_distro_binary(packages[owner], mappings, output)
        copyright_file = Path("/usr/share/doc") / owner.split(":")[0] / "copyright"
        if not copyright_file.is_file():
            raise ValueError(f"Missing distro copyright record: {owner}")
        shutil.copyfile(copyright_file, licenses / (owner.replace(":", "_") + ".copyright"))
    shutil.copytree("/usr/share/common-licenses", licenses / "common-licenses", symlinks=False)
    for relative in sources:
        target = runtime / relative
        resolved = dependency_paths(target)
        for name in needed(target):
            if not HOST_LIBRARIES.fullmatch(name) and resolved.get(name, Path("/unresolved")).resolve() != (runtime / "lib" / name).resolve():
                raise ValueError(f"Patched private ELF still resolves a dependency outside its bundle: {relative} -> {name}")
    result = {"schema": "meowchan.linux-runtime.v1", "runtime": rid, "baseline": "Ubuntu 24.04",
              "vlc_version": version, "files": file_hashes(runtime), "mappings": mappings, "edges": edges,
              "symbol_floor": floors, "packages": list(packages.values()), "licenses": file_hashes(licenses),
              "host_libraries_pattern": HOST_LIBRARIES.pattern, "plugin_profile": "gallery-local-video-v1",
              "supported_containers": ["webm", "mkv", "mp4", "mov", "avi"],
              "source_status": "Exact distro sources must be collected by package-linux-sources.py; native acceptance remains separate."}
    write_json(output / "runtime-manifest.json", result)
    return result


def tool_assets(rid: str) -> dict:
    arch = RIDS[rid][1]
    return {"appimagetool": {"file": f"appimagetool-{arch}.AppImage", "sha256": PINS[rid]["appimagetool"],
                            "url": f"https://github.com/AppImage/appimagetool/releases/download/{TOOL_VERSION}/appimagetool-{arch}.AppImage"},
            "runtime": {"file": f"runtime-{arch}", "sha256": PINS[rid]["runtime"],
                        "url": f"https://github.com/AppImage/type2-runtime/releases/download/{RUNTIME_VERSION}/runtime-{arch}"}}


def download(url: str, destination: Path, expected: str | None = None) -> None:
    if destination.exists():
        if expected and digest(destination) == expected:
            return
        raise ValueError(f"Refusing to overwrite unverified download: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".partial")
    try:
        with urllib.request.urlopen(url, timeout=90) as response, temporary.open("xb") as output:
            for chunk in iter(lambda: response.read(1024 * 1024), b""):
                reserve(destination, len(chunk))
                output.write(chunk)
        if expected and digest(temporary) != expected:
            raise ValueError(f"Download checksum mismatch: {url}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def download_tools(rid: str, output: Path) -> None:
    reserve(output, 40 * 1024 ** 2)
    for item in tool_assets(rid).values():
        target = output / item["file"]
        download(item["url"], target, item["sha256"])
        target.chmod(0o755)


def copy_payload(source: Path, destination: Path) -> None:
    for path in source.rglob("*"):
        if path.is_symlink() and not path.resolve().is_relative_to(source.resolve()):
            raise ValueError(f"Publish symlink escapes its input: {path}")
    shutil.copytree(source, destination, symlinks=False)


def archive_payload(root: Path, output: Path) -> None:
    # Fresh destination, deterministic metadata, no symlinks/devices/hardlinks.
    file_hashes(root)
    with output.open("xb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed, tarfile.open(fileobj=compressed, mode="w|") as archive:
        for path in sorted(root.rglob("*")):
            info = archive.gettarinfo(str(path), arcname=path.relative_to(root).as_posix())
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            info.mtime = 0
            info.mode = 0o755 if path.is_dir() or path.stat().st_mode & 0o111 else 0o644
            if path.is_file():
                with path.open("rb") as stream:
                    archive.addfile(info, stream)
            else:
                archive.addfile(info)


def build_appimage(appdir: Path, output: Path, rid: str, tools: Path) -> None:
    assets = tool_assets(rid)
    for item in assets.values():
        if digest(tools / item["file"]) != item["sha256"]:
            raise ValueError(f"Pinned AppImage input hash mismatch: {item['file']}")
        check_elf(tools / item["file"], rid)
    environment = os.environ.copy()
    environment["ARCH"] = RIDS[rid][1]
    # The builder's own embedded runtime is not the runtime we distribute.
    subprocess.run([str((tools / assets["appimagetool"]["file"]).resolve()), "--appimage-extract-and-run",
                    "--runtime-file", str((tools / assets["runtime"]["file"]).resolve()),
                    str(appdir.resolve()), str(output.resolve())], env=environment, check=True)
    check_elf(output, rid)
    output.chmod(0o755)


def package(publish: Path, rid: str, version: str, commit: str, runtime_dir: Path, runtime_manifest: Path,
            tools: Path, output: Path) -> dict:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.+-]*", version) or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Supply a safe version and exact source commit SHA.")
    if (publish / "libvlc").exists() or (publish / "linux-video-runtime.json").exists():
        raise ValueError("Linux packaging expects an unmodified system-free publish input.")
    check_elf(publish / "MeowChan", rid)
    publish_symbols = payload_symbols(publish, rid)
    inventory = json.loads(runtime_manifest.read_text())
    if inventory.get("schema") != "meowchan.linux-runtime.v1" or inventory.get("runtime") != rid:
        raise ValueError("Native runtime provenance schema/RID mismatch.")
    verify_files(runtime_dir, inventory["files"])
    licenses = runtime_manifest.parent / "licenses"
    verify_files(licenses, inventory["licenses"])
    runtime_license = Path(__file__).resolve().parents[1] / "docs/native-video-appimage-runtime-LICENSE"
    if not runtime_license.is_file():
        raise ValueError("The AppImage static runtime notice is missing.")
    output.mkdir(parents=True, exist_ok=True)
    stem = f"MeowChan-{version}-{rid}"
    names = [stem + "-system-vlc.tar.gz", stem + "-bundled-vlc.tar.gz", stem + ".AppImage", rid + "-package-manifest.json"]
    if any((output / name).exists() for name in names):
        raise ValueError("Refusing to overwrite an existing Linux candidate.")
    size = sum(p.stat().st_size for p in publish.rglob("*") if p.is_file())
    private_size = sum(p.stat().st_size for p in runtime_dir.rglob("*") if p.is_file())
    # Two staged app trees plus all three potentially incompressible outputs.
    legal_size = sum(p.stat().st_size for p in licenses.rglob("*") if p.is_file())
    reserve(output, size * 5 + (private_size + legal_size) * 3 + 64 * 1024 ** 2)
    records = []
    with tempfile.TemporaryDirectory(prefix=".linux-package-", dir=output) as temporary:
        staging = Path(temporary)
        base = staging / "system"
        copy_payload(publish, base)
        (base / "MeowChan").chmod(0o755)
        write_json(base / "linux-video-runtime.json", {"SchemaVersion": 1, "Mode": "system", "RuntimeIdentifier": rid})
        system_file = output / names[0]
        archive_payload(base, system_file)
        records.append({"file": system_file.name, "flavor": "system-vlc", "bundled": False,
                        "apphost_member": "MeowChan", "payload_files": file_hashes(base)})
        bundled = staging / "bundle"
        copy_payload(base, bundled)
        bundle_root = bundled / "libvlc" / rid
        copy_payload(runtime_dir, bundle_root)
        write_json(bundled / "linux-video-runtime.json", {"SchemaVersion": 1, "Mode": "bundled", "RuntimeIdentifier": rid, "Files": inventory["files"]})
        copy_payload(licenses, bundled / "linux-video-licenses")
        shutil.copyfile(runtime_license, bundled / "linux-video-licenses/AppImage-runtime-LICENSE")
        write_json(bundled / "linux-video-runtime-inventory.json", {
            **inventory, "source_commit": commit, "runtime_manifest_sha256": digest(runtime_manifest),
            "license_files": file_hashes(bundled / "linux-video-licenses"),
            "appimage_inputs": tool_assets(rid), "appimage_runtime_source_commit": RUNTIME_COMMIT})
        bundle_file = output / names[1]
        archive_payload(bundled, bundle_file)
        records.append({"file": bundle_file.name, "flavor": "bundled-vlc", "bundled": True,
                        "apphost_member": "MeowChan", "payload_files": file_hashes(bundled)})
        appdir = staging / "AppDir"
        target = appdir / "usr/lib/meowchan"
        target.parent.mkdir(parents=True)
        # Moving the already archived tree avoids another full private-runtime copy.
        bundled.rename(target)
        (appdir / "AppRun").write_text('#!/bin/sh\nset -eu\nHERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)\nexec "$HERE/usr/lib/meowchan/MeowChan" "$@"\n')
        (appdir / "AppRun").chmod(0o755)
        (appdir / "MeowChan.desktop").write_text('[Desktop Entry]\nType=Application\nName=MeowChan\nExec=MeowChan\nIcon=MeowChan\nCategories=Network;\nTerminal=false\n')
        icon = target / "Assets/meowchan.png"
        if not icon.is_file():
            raise ValueError("Published application icon is missing.")
        shutil.copyfile(icon, appdir / "MeowChan.png")
        # appimagetool otherwise creates a symlink, which our strict delivery
        # inventory deliberately forbids. Supply the regular icon explicitly.
        shutil.copyfile(icon, appdir / ".DirIcon")
        image_file = output / names[2]
        build_appimage(appdir, image_file, rid, tools)
        records.append({"file": image_file.name, "flavor": "appimage", "bundled": True,
                        "apphost_member": "usr/lib/meowchan/MeowChan", "payload_files": file_hashes(appdir)})
    for record in records:
        path = output / record["file"]
        record.update(sha256=digest(path), bytes=path.stat().st_size)
    result = {"schema": "meowchan.linux-packages.v1", "runtime": rid, "version": version, "source_commit": commit,
              "apphost_sha256": digest(publish / "MeowChan"), "runtime_manifest_sha256": digest(runtime_manifest),
              "publish_symbol_requirements": publish_symbols,
              "symbol_floor": merge_floor([inventory.get("symbol_floor", {}), *publish_symbols.values()]),
              "symbol_scope": "All private VLC and publish ELF files, including the apphost and externally published .NET native runtime libraries.",
              "dotnet_native_files": [name for name in publish_symbols if Path(name).name in ("libcoreclr.so", "libhostpolicy.so", "libSystem.Native.so")],
              "appimage_inputs": tool_assets(rid), "artifacts": records}
    write_json(output / names[3], result)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="command", required=True)
    for name in ("refresh-runtime-packages", "prepare-runtime", "download-tools", "package"):
        sub = actions.add_parser(name)
        sub.add_argument("--runtime", required=True, choices=RIDS)
        if name != "refresh-runtime-packages":
            sub.add_argument("--output-dir", type=Path, required=True)
        if name == "package":
            for flag in ("publish-dir", "runtime-dir", "runtime-manifest", "tools-dir"):
                sub.add_argument("--" + flag, type=Path, required=True)
            sub.add_argument("--version", required=True)
            sub.add_argument("--source-commit", required=True)
    args = parser.parse_args()
    if args.command == "refresh-runtime-packages":
        print(json.dumps(refresh_runtime_packages(args.runtime)))
    elif args.command == "prepare-runtime":
        result = prepare_runtime(args.runtime, args.output_dir)
        print(json.dumps({"manifest": str(args.output_dir / "runtime-manifest.json"), "files": len(result["files"])}))
    elif args.command == "download-tools":
        download_tools(args.runtime, args.output_dir)
    else:
        result = package(args.publish_dir, args.runtime, args.version, args.source_commit, args.runtime_dir,
                         args.runtime_manifest, args.tools_dir, args.output_dir)
        print(json.dumps({"manifest": str(args.output_dir / (args.runtime + "-package-manifest.json")), "artifacts": len(result["artifacts"])}))


if __name__ == "__main__":
    main()
