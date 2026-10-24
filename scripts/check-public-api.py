#!/usr/bin/env python3
"""Audit declared API inventories; require reviewed Unshipped files for a release.

This does not analyze assemblies or promise binary compatibility. The normal
RS0016/RS0017 build remains necessary. No inventory is modified by this command.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

PROJECTS = ("libmeowchan", "MeowChan.PluginSdk")


def entries(data: bytes, path: str = "<inventory>") -> list[dict]:
    # Analyzer 3.3.4 recognizes only this exact marker, at the first nonblank
    # position. It treats // comments and #nullable disable as API symbols.
    declared = []
    seen_nonblank = False
    for number, line in enumerate(data.decode("utf-8-sig").splitlines(), 1):
        if not line.strip():
            continue
        if line == "#nullable enable":
            if seen_nonblank:
                raise ValueError(f"{path}:{number}: #nullable enable must be the first nonblank line.")
        else:
            if line.lstrip().startswith("//"):
                raise ValueError(f"{path}:{number}: // comments are not supported by PublicApiAnalyzers 3.3.4.")
            if line.lstrip().startswith("#"):
                raise ValueError(f"{path}:{number}: Unsupported API inventory directive: {line!r}.")
            declared.append({"line": number, "api": line})
        seen_nonblank = True
    return declared


def audit(repository: Path, source_commit: str | None = None) -> dict:
    if source_commit is not None and re.fullmatch(r"[0-9a-f]{40}", source_commit) is None:
        raise ValueError("Expected a full lowercase source commit SHA.")
    def read(name: str) -> bytes:
        if source_commit is None:
            return (repository / name).read_bytes()
        return subprocess.check_output(["git", "-C", str(repository), "show", source_commit + ":" + name])
    projects = {}
    for project in PROJECTS:
        inventories = {}
        for kind in ("Shipped", "Unshipped"):
            name = project + "/PublicAPI." + kind + ".txt"
            data = read(name)
            declared = entries(data, name)
            inventories[kind.lower()] = {"path": name, "sha256": hashlib.sha256(data).hexdigest(),
                                          "entryCount": len(declared)}
            if kind == "Unshipped":
                inventories["unshipped"]["entries"] = declared
        projects[project] = inventories
    return {"schema": "meowchan.public-api-review.v1", "sourceCommit": source_commit,
            "scope": "Declared API inventory and release readiness; no binary compatibility or compilation claim",
            "projects": projects,
            "releaseReady": all(value["unshipped"]["entryCount"] == 0 for value in projects.values())}


def require_release(report: dict) -> None:
    pending = [project + "=" + str(value["unshipped"]["entryCount"])
               for project, value in report["projects"].items() if value["unshipped"]["entryCount"]]
    if pending:
        raise ValueError("Unshipped public API review is incomplete: " + ", ".join(pending)
                         + ". Review the API explicitly before moving entries to Shipped.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--source-commit", help="Read exact committed inventories, ignoring working-copy edits.")
    parser.add_argument("--release", action="store_true", help="Reject any unreviewed API entries; ordinary builds may retain them.")
    parser.add_argument("--output", type=Path, help="New JSON evidence file; an existing file is rejected.")
    args = parser.parse_args()
    try:
        report = audit(args.repository.resolve(strict=True), args.source_commit)
        report["releaseCheckRequested"] = args.release
        report["passed"] = not args.release or report["releaseReady"]
        serialized = json.dumps(report, indent=2) + "\n"
        if args.output is not None:
            with args.output.open("x", encoding="utf-8") as output:
                output.write(serialized)
        print(serialized, end="")
        if args.release:
            require_release(report)
    except (OSError, UnicodeError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, "Public API check rejected: " + str(error) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
