#!/usr/bin/env python3
"""Run an already published linux-x64 compiler with no SDK, NuGet home or network.

Usage: sdk_free_probe.py <self-contained-compiler-directory> <result-directory> [source.cs ...]
Requires bubblewrap. This checks the compiler delivery, not desktop/worker activation.
"""
import base64
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys


def hash_parts(*parts):
    digest = hashlib.sha256()
    for part in parts:
        value = part.encode("utf-8")
        digest.update(struct.pack("!i", len(value)))
        digest.update(value)
    return digest.hexdigest()


def main():
    bundle = Path(sys.argv[1]).resolve()
    output = Path(sys.argv[2]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not shutil.which("bwrap"):
        raise SystemExit("bubblewrap is required for an SDK-inaccessible delivery proof")
    manifest = json.loads((bundle / "toolchain.json").read_text())
    for entry in manifest["Files"]:
        actual = hashlib.sha256((bundle / entry["Path"]).read_bytes()).hexdigest()
        assert actual == entry["Sha256"], entry["Path"]
    for license_file in ("licenses/framework/LICENSE.TXT", "licenses/framework/THIRD-PARTY-NOTICES.TXT",
                         "licenses/roslyn/LICENSE-Roslyn.txt", "licenses/roslyn/ThirdPartyNotices.rtf", "licenses/roslyn/provenance.json"):
        assert (bundle / license_file).stat().st_size > 0, license_file
    toolchain = hash_parts(manifest["TargetFramework"], manifest["Options"], manifest["CompilerFingerprint"],
                           manifest["SdkFingerprint"], manifest["ReferenceFingerprint"])
    sources = [Path(p).resolve() for p in sys.argv[3:]]
    if not sources:
        fixture = output / "Plugin with spaces.cs"
        fixture.write_text("public sealed class LocalPlugin : MeowChan.PluginSdk.IPlugin { public MeowChan.PluginSdk.PluginDescription Describe() => throw new System.NotSupportedException(); }\n")
        sources = [fixture]
    summary = []
    for index, source in enumerate(sources):
        snapshot = source.read_bytes()
        source_hash = hashlib.sha256(snapshot).hexdigest()
        work = output / (str(index) + " isolated work with spaces")
        work.mkdir()
        source_directory = work / "source with spaces"
        source_directory.mkdir()
        (source_directory / source.name).write_bytes(snapshot)
        request = {
            "Source": base64.b64encode(snapshot).decode(),
            "OriginalPath": "/work/source with spaces/" + source.name,
            "CompilationFingerprint": hash_parts(source_hash, toolchain),
            "ToolchainFingerprint": toolchain,
            "OutputPath": "/work/plugin.dll",
        }
        args = ["bwrap", "--die-with-parent", "--unshare-net", "--clearenv",
                "--ro-bind", "/usr", "/usr", "--ro-bind", "/lib", "/lib", "--ro-bind", "/lib64", "/lib64",
                "--tmpfs", "/usr/share/dotnet", "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
                "--ro-bind", str(bundle), "/compiler", "--bind", str(work), "/work", "--chdir", "/work",
                "--setenv", "PATH", "/no-sdk", "--setenv", "HOME", "/no-home", "--setenv", "DOTNET_ROOT", "/no-sdk",
                "--setenv", "DOTNET_HOST_TRACE", "1", "--setenv", "DOTNET_HOST_TRACEFILE", "/work/host-trace.txt",
                "/compiler/MeowChan.PluginCompiler", "--compile", "/compiler"]
        result = subprocess.run(args, input=json.dumps(request).encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        (work / "response.json").write_bytes(result.stdout)
        (work / "stderr.txt").write_bytes(result.stderr)
        response = json.loads(result.stdout)
        assert result.returncode == 0 and response["Success"], response
        assembly = work / "plugin.dll"
        assert assembly.stat().st_size > 0
        summary.append({"source": str(source), "sourceSha256": source_hash, "assemblySha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
                        "compilationFingerprint": request["CompilationFingerprint"], "diagnostics": response["Diagnostics"]})
    report = {"compilerBundle": str(bundle), "toolchainFingerprint": toolchain, "sdkAccessible": False,
              "nugetHomeAccessible": False, "networkAvailable": False, "installationReadOnly": True,
              "foreignWorkingDirectory": True, "nativePlatform": "linux-x64", "results": summary}
    (output / "RESULT.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"passed": len(summary), "report": str(output / "RESULT.json")}))


if __name__ == "__main__":
    main()
