#!/usr/bin/env python3
"""Verify local Core/SDK/Host packages with isolated PackageReference consumers.

Owns the global build lock: do not wrap this entire runner in another flock of
that same lock. The optional application probe executes delivered helper apphosts;
consumer compilation still uses the developer SDK and is not SDK-free product E2E.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = {
    "meowchan.pluginsdk": ("MeowChan.PluginSdk", "MeowChan.PluginSdk.dll"),
    "meowchan.pluginhost": ("MeowChan.PluginHost", "MeowChan.PluginHost.dll"),
    "libmeowchan": ("libmeowchan", "libmeowchan.dll"),
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def child_text(element: ET.Element, name: str) -> str:
    for child in element:
        if child.tag.rsplit("}", 1)[-1] == name:
            return child.text or ""
    raise ValueError(f"NuGet metadata is missing {name}.")


def forbidden_dependency(name: str) -> bool:
    name = name.lower()
    return name.startswith(("avalonia", "semi.avalonia", "irihi.", "microsoft.codeanalysis")) or name in {
        "meowchan", "ochan", "meowchan.plugincompiler", "meowchan.pluginworker"
    }


def inspect_package(path: Path) -> dict:
    package_bytes = path.read_bytes()
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError(f"Duplicate ZIP entries in {path.name}.")
        nuspecs = [name for name in names if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError(f"Expected one NuSpec in {path.name}.")
        root = ET.fromstring(archive.read(nuspecs[0]))
        metadata = next((item for item in root if item.tag.rsplit("}", 1)[-1] == "metadata"), None)
        if metadata is None:
            raise ValueError("Missing NuGet metadata.")
        package_id, version = child_text(metadata, "id"), child_text(metadata, "version")
        key = package_id.lower()
        if key not in PACKAGES:
            raise ValueError(f"Unexpected candidate package ID: {package_id}.")
        expected_dll = "lib/net10.0/" + PACKAGES[key][1]
        dlls = sorted(name for name in names if name.lower().endswith(".dll"))
        if dlls != [expected_dll]:
            raise ValueError(f"Unexpected assemblies in {package_id}: {dlls}.")
        dependencies = sorted({item.attrib["id"] for item in metadata.iter()
                               if item.tag.rsplit("}", 1)[-1] == "dependency" and "id" in item.attrib})
        if any(forbidden_dependency(item) for item in dependencies):
            raise ValueError(f"Desktop/Roslyn/helper dependency leaked into {package_id}: {dependencies}.")
        if key == "meowchan.pluginsdk" and dependencies:
            raise ValueError(f"SDK must be independent of host/Core/runtime packages: {dependencies}.")
        if key == "meowchan.pluginhost" and not {"meowchan.pluginsdk", "libmeowchan"}.issubset({item.lower() for item in dependencies}):
            raise ValueError("Host package must declare its SDK and Core dependencies.")
        readme = child_text(metadata, "readme")
        if not readme or readme not in names:
            raise ValueError(f"Missing included README in {package_id}.")
        license_items = [item for item in metadata if item.tag.rsplit("}", 1)[-1] == "license"]
        if len(license_items) != 1 or license_items[0].attrib.get("type") != "file" or license_items[0].text not in names:
            raise ValueError(f"Package must identify its included license file: {package_id}.")
        return {"id": package_id, "version": version, "path": str(path.resolve()), "sha256": sha256(package_bytes),
                "assembly": expected_dll, "assemblySha256": sha256(archive.read(expected_dll)), "dependencies": dependencies}


def candidates_from_feed(feed: Path) -> dict[str, dict]:
    found: dict[str, dict] = {}
    for path in sorted(feed.glob("*.nupkg")):
        # Ignore external dependency packages if an offline feed contains them too.
        with zipfile.ZipFile(path) as archive:
            specs = [name for name in archive.namelist() if name.endswith(".nuspec")]
            if len(specs) != 1:
                raise ValueError(f"Invalid feed candidate: {path.name}.")
            root = ET.fromstring(archive.read(specs[0]))
            metadata = next(item for item in root if item.tag.rsplit("}", 1)[-1] == "metadata")
            package_id = child_text(metadata, "id").lower()
        if package_id not in PACKAGES:
            continue
        if package_id in found:
            raise ValueError(f"Ambiguous feed: more than one {package_id} candidate. Use a fresh feed.")
        found[package_id] = inspect_package(path)
    if set(found) != set(PACKAGES):
        raise ValueError(f"Feed must contain one SDK, Host and Core package; missing {sorted(set(PACKAGES) - set(found))}.")
    return found


def write_config(path: Path, feed: Path, dependency_sources: list[str]) -> None:
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(mapping, "clear")
    local = ET.SubElement(mapping, "packageSource", key="candidate")
    for package_id, _ in PACKAGES.values():
        ET.SubElement(local, "package", pattern=package_id)
    for index, source in enumerate(dependency_sources):
        key = f"dependencies-{index}"
        ET.SubElement(sources, "add", key=key, value=source)
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=key), "package", pattern="*")
    ET.ElementTree(config).write(path, encoding="utf-8", xml_declaration=True)


def copy_consumer(project: Path, target: Path) -> Path:
    tree = ET.parse(project)
    if any(item.tag.rsplit("}", 1)[-1] == "ProjectReference" for item in tree.iter()):
        raise ValueError(f"Consumer has a forbidden ProjectReference: {project}.")
    shutil.copytree(project.parent, target, ignore=shutil.ignore_patterns("bin", "obj", ".git", "packages*.lock.json"))
    return target / project.name


def verify_assets(assets: dict, cache: Path, candidates: dict[str, dict], required: set[str], sdk_only: bool = False, core_only: bool = False) -> dict:
    cache = cache.resolve(strict=True)
    if {Path(path).resolve() for path in assets["packageFolders"]} != {cache}:
        raise ValueError("Restore did not use only the isolated package cache.")
    resolved: dict[str, dict] = {}
    dependency_ids: list[str] = []
    for name, entry in assets["libraries"].items():
        package_id, version = name.rsplit("/", 1)
        key = package_id.lower()
        if entry["type"] != "package":
            raise ValueError(f"Consumer has a forbidden non-package dependency: {name} ({entry['type']}).")
        if forbidden_dependency(key):
            raise ValueError(f"Desktop/Roslyn/helper dependency leaked into resolved graph: {name}.")
        if core_only and key in {"meowchan.pluginhost", "meowchan.pluginsdk"}:
            raise ValueError("Core-only consumer acquired Host/SDK dependencies.")
        if sdk_only and key in {"meowchan.pluginhost", "libmeowchan"}:
            raise ValueError("SDK-only consumer acquired Host/Core dependencies.")
        dependency_ids.append(name)
        directory = (cache / entry["path"]).resolve(strict=True)
        if not directory.is_relative_to(cache):
            raise ValueError("Package resolved outside the isolated cache.")
        if key not in candidates:
            continue
        candidate = candidates[key]
        if version.lower() != candidate["version"].lower():
            raise ValueError(f"Resolved a different candidate version: {name}.")
        packages = list(directory.glob("*.nupkg"))
        if len(packages) != 1 or sha256(packages[0].read_bytes()) != candidate["sha256"]:
            raise ValueError(f"Restored package bytes differ from local candidate: {name}.")
        assembly = (directory / candidate["assembly"]).resolve(strict=True)
        if not assembly.is_relative_to(directory) or sha256(assembly.read_bytes()) != candidate["assemblySha256"]:
            raise ValueError(f"Restored assembly bytes differ from candidate: {name}.")
        resolved[key] = {"id": package_id, "version": version, "packageSha256": candidate["sha256"],
                         "assemblySha256": candidate["assemblySha256"], "restoredPath": str(directory)}
    if not required.issubset(resolved):
        raise ValueError(f"Consumer did not resolve required packages: {sorted(required - set(resolved))}.")
    return {"packages": resolved, "dependencyGraph": sorted(dependency_ids)}


def verify_output(output: Path, resolved: dict, candidates: dict[str, dict]) -> dict[str, str]:
    hashes = {}
    for key in resolved["packages"]:
        candidate = candidates[key]
        assembly = output / PACKAGES[key][1]
        actual = sha256(assembly.read_bytes())
        if actual != candidate["assemblySha256"]:
            raise ValueError(f"Consumer output DLL differs from restored package: {assembly.name}.")
        hashes[assembly.name] = actual
    return hashes


@contextmanager
def build_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as handle:
        print(f"Waiting for global build lock: {path}", flush=True)
        if os.name == "nt":
            import msvcrt
            if handle.tell() == 0:
                handle.write(b"\0")
                handle.flush()
            handle.seek(0)
            while True:
                try:
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(0.1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_UN)


def run_process(arguments: list[str], cwd: Path, environment: dict, log: Path, timeout: int) -> None:
    print("Running: " + " ".join(arguments[:3]), flush=True)
    with subprocess.Popen(arguments, cwd=cwd, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, start_new_session=os.name != "nt") as process:
        try:
            output, _ = process.communicate(timeout=timeout)
        except (subprocess.TimeoutExpired, KeyboardInterrupt):
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False, capture_output=True, timeout=30)
            else:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass  # The child may have exited between the timeout and cleanup.
            output, _ = process.communicate()
            log.write_text(output[-2 * 1024 * 1024:], encoding="utf-8")
            raise
    log.write_text(output[-2 * 1024 * 1024:], encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"Command failed ({process.returncode}); see {log}.\n{output[-4000:]}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New evidence directory; an existing directory is rejected.")
    parser.add_argument("--application-directory", type=Path, help="Optional delivered app containing plugin-compiler/ and plugin-worker/.")
    parser.add_argument("--dependency-source", action="append", help="NuGet source for external dependencies; defaults to nuget.org. Repeatable.")
    parser.add_argument("--core-consumer", type=Path, default=ROOT / "samples/LibMeowChan.Consumer/LibMeowChan.Consumer.csproj")
    parser.add_argument("--sdk-consumer", type=Path, default=ROOT / "samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj")
    parser.add_argument("--host-consumer", type=Path, default=ROOT / "samples/MeowChan.PluginHost.Consumer/MeowChan.PluginHost.Consumer.csproj")
    parser.add_argument("--build-lock", type=Path, default=Path(tempfile.gettempdir()) / "meowchan-ui-1.0-build.lock")
    parser.add_argument("--timeout", type=int, default=600, help="Per-command deadline in seconds.")
    args = parser.parse_args()
    if args.timeout < 1 or args.timeout > 1800:
        parser.error("--timeout must be between 1 and 1800 seconds.")
    feed = args.feed.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    evidence: dict = {"passed": False, "scope": "PackageReference consumers; not SDK-free product E2E",
                      "freshCache": True, "developerSdkUsed": True, "providerNetworkRequests": 0,
                      "applicationProbe": "requested" if args.application_directory else "not_requested"}
    try:
        candidates = candidates_from_feed(feed)
        evidence["candidates"] = candidates
        application = args.application_directory.resolve(strict=True) if args.application_directory else None
        with tempfile.TemporaryDirectory(prefix="meowchan package consumers ") as temporary_directory:
            temporary = Path(temporary_directory)
            cache = temporary / "fresh packages"
            config = temporary / "NuGet.Config"
            write_config(config, feed, args.dependency_source or ["https://api.nuget.org/v3/index.json"])
            environment = dict(os.environ)
            for key in ("MEOWCHAN_PROFILE", "OCHAN_PROFILE", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "NUGET_FALLBACK_PACKAGES"):
                environment.pop(key, None)
            environment.update({"NUGET_PACKAGES": str(cache), "NUGET_HTTP_CACHE_PATH": str(temporary / "http-cache"),
                                "DOTNET_CLI_HOME": str(temporary / "dotnet-home"), "MSBUILDDISABLENODEREUSE": "1",
                                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
            # Preserve the repository's explicit SDK selection without inheriting its project graph/props.
            if (ROOT / "global.json").exists():
                shutil.copyfile(ROOT / "global.json", temporary / "global.json")
            evidence["consumers"] = {}
            with build_lock(args.build_lock):
                for label, original, required in (
                    ("core", args.core_consumer, {"libmeowchan"}),
                    ("sdk", args.sdk_consumer, {"meowchan.pluginsdk"}),
                    ("host", args.host_consumer, set(PACKAGES)),
                ):
                    project = copy_consumer(original.resolve(strict=True), temporary / (label + " consumer"))
                    consumer_sources = {path.relative_to(project.parent).as_posix(): sha256(path.read_bytes())
                                        for path in sorted(project.parent.rglob("*")) if path.is_file()}
                    properties = (["-p:LibMeowChanVersion=" + candidates["libmeowchan"]["version"]] if label == "core" else
                                  ["-p:MeowChanPluginSdkVersion=" + candidates["meowchan.pluginsdk"]["version"]])
                    if label == "host":
                        properties.append("-p:MeowChanPluginHostVersion=" + candidates["meowchan.pluginhost"]["version"])
                    run_process(["dotnet", "restore", str(project), "--configfile", str(config), "--packages", str(cache),
                                 "--use-lock-file", "-p:Configuration=Release", "-warnaserror:NU1900", "-nodeReuse:false", "-m:1", *properties],
                                temporary, environment, output / (label + "-restore.log"), args.timeout)
                    assets = json.loads((project.parent / "obj/project.assets.json").read_text())
                    resolved = verify_assets(assets, cache, candidates, required, sdk_only=label == "sdk", core_only=label == "core")
                    run_process(["dotnet", "build", str(project), "-c", "Release", "--no-restore", "-nodeReuse:false", "-m:1", *properties],
                                temporary, environment, output / (label + "-build.log"), args.timeout)
                    binary_directory = project.parent / "bin/Release/net10.0"
                    resolved["outputAssemblyHashes"] = verify_output(binary_directory, resolved, candidates)
                    resolved["lockSha256"] = sha256((project.parent / "packages.lock.json").read_bytes())
                    assembly = binary_directory / (project.stem + ".dll")
                    arguments = ["dotnet", str(assembly)]
                    if label == "host":
                        arguments.extend(["--workspace", str(temporary / "new isolated host profile"), "--report", str(output / "host-probe.json")])
                        if application:
                            arguments.extend(["--application-directory", str(application)])
                    run_process(arguments, temporary, environment, output / (label + "-run.log"), args.timeout)
                    resolved["consumerExitCode"] = 0
                    resolved["consumerSourceSha256"] = consumer_sources
                    evidence["consumers"][label] = resolved
                    (output / (label + "-consumer-evidence.json")).write_text(json.dumps(resolved, indent=2) + "\n", encoding="utf-8")
            probe = json.loads((output / "host-probe.json").read_text())
            if not probe.get("passed") or not probe.get("profileStorePassed") or application and probe.get("compilerWorkerStatus") != "passed":
                raise ValueError("The host probe did not establish the requested checks.")
            evidence["applicationProbe"] = probe["compilerWorkerStatus"]
            evidence["passed"] = True
    except (Exception, KeyboardInterrupt) as error:
        evidence["error"] = type(error).__name__ + ": " + str(error)
        raise
    finally:
        (output / "RESULT.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"passed": evidence["passed"], "evidence": str(output / "RESULT.json"),
                          "applicationProbe": evidence["applicationProbe"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
