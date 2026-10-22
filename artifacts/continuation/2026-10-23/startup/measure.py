#!/usr/bin/env python3
"""Frozen Linux helper phase probe; never substitutes for Desktop PluginSession evidence.

All delivery reads are read-only; each candidate fully hashes all toolchain files.
Python SHA timings are a lower-level comparison, not .NET validation timings.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repetitions", type=int, default=3)
    args = parser.parse_args()
    repo, payload, output = args.repo.resolve(), args.payload.resolve(), args.output.resolve()
    if output.exists():
        raise SystemExit("Output must be a new private directory")
    if not 1 <= args.repetitions <= 10 or not shutil.which("bwrap"):
        raise SystemExit("Requires bubblewrap and 1..10 repetitions")
    output.mkdir(parents=True)
    spec = importlib.util.spec_from_file_location("probe", repo / "tests/PluginDeliverySmoke/sdk_free_worker_probe.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
    toolchain_root = payload / "plugin-compiler"
    toolchain_path = toolchain_root / "toolchain.json"
    toolchain = json.loads(toolchain_path.read_bytes())
    manifests = sorted((payload / "plugins").glob("*.plugin.json"))
    assert len(manifests) == 5
    results = []
    cpu_count = os.cpu_count()
    for repetition in range(args.repetitions):
        for index, manifest_path in enumerate(manifests):
            manifest = json.loads(manifest_path.read_bytes())
            started = time.monotonic()
            bytes_hashed = 0
            cpu_started = time.process_time()
            for file in toolchain["Files"]:
                relative = Path(file["Path"])
                assert not relative.is_absolute() and ".." not in relative.parts
                path = toolchain_root / relative
                assert not path.is_symlink()
                data = path.read_bytes()
                assert hashlib.sha256(data).hexdigest() == file["Sha256"], str(path)
                bytes_hashed += len(data)
            hash_ms = (time.monotonic() - started) * 1000
            hash_cpu_ms = (time.process_time() - cpu_started) * 1000
            assembly = payload / "plugins" / manifest["AssemblyRelativePath"]
            assert hashlib.sha256(assembly.read_bytes()).hexdigest() == manifest["AssemblySha256"]
            work = output / f"r{repetition}-p{index}"
            work.mkdir()
            command = ["bwrap", "--die-with-parent", "--unshare-net", "--unshare-pid", "--clearenv",
                       "--ro-bind", "/usr", "/usr", "--ro-bind", "/lib", "/lib", "--ro-bind", "/lib64", "/lib64",
                       "--tmpfs", "/usr/share/dotnet", "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
                       "--ro-bind", str(payload), "/application", "--bind", str(work), "/work", "--chdir", "/work",
                       "--setenv", "PATH", "/no-sdk", "--setenv", "HOME", "/no-home", "--setenv", "DOTNET_ROOT", "/no-sdk",
                       "--setenv", "DOTNET_GCHeapHardLimit", "20000000", "--setenv", "DOTNET_EnableDiagnostics", "0",
                       "/application/plugin-worker/MeowChan.PluginWorker"]
            worker_started = time.monotonic()
            with (work / "stderr.txt").open("wb") as stderr:
                process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, start_new_session=True)
                try:
                    hello = probe.send(process, probe.envelope("hello", "hello-1", {"requiredOperations": ["describe"]}))
                    assert "describe" in hello["operations"]
                    hello_done = time.monotonic()
                    description = probe.send(process, probe.envelope("describe", "describe-1", {
                        "assemblyPath": "/application/plugins/" + manifest["AssemblyRelativePath"], "resourcePaths": {},
                        "configuration": {"revision": "delivery-config", "pluginSettings": {"schemaVersion": "1", "values": []}, "sourceSettings": {}},
                        "validateConfiguration": False}))
                    describe_done = time.monotonic()
                    assert description["sources"] or description["filters"]
                    process.stdin.close()
                    assert process.wait(timeout=5) == 0
                    exit_done = time.monotonic()
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=5)
                    if not process.stdin.closed:
                        process.stdin.close()
                    process.stdout.close()
            results.append({"repetition": repetition, "manifest": manifest_path.name, "pluginId": description["pluginId"],
                            "hashFiles": len(toolchain["Files"]), "hashBytes": bytes_hashed,
                            "pythonFullHashMs": hash_ms, "pythonHashCpuMs": hash_cpu_ms,
                            "spawnThroughHelloMs": (hello_done-worker_started)*1000,
                            "describeMs": (describe_done-hello_done)*1000, "gracefulExitMs": (exit_done-describe_done)*1000,
                            "workerTotalMs": (exit_done-worker_started)*1000,
                            "loadavg": os.getloadavg()})
    tracked = [toolchain_path, payload / "plugin-worker/MeowChan.PluginWorker", *manifests]
    report = {"payload": str(payload), "cpuCount": cpu_count, "sdkAccessible": False, "networkAvailable": False,
              "installationReadOnly": True, "doesNotExecuteDesktopPluginSession": True,
              "doesNotMeasureDotNetToolchainValidation": True,
              "sha256": {str(path.relative_to(payload)): hashlib.sha256(path.read_bytes()).hexdigest() for path in tracked},
              "results": results}
    (output / "RESULT.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"samples": len(results), "report": str(output / "RESULT.json")}))


if __name__ == "__main__":
    main()
