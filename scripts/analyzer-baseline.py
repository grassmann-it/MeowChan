#!/usr/bin/env python3
"""Prune (never expand) the frozen UX-LEDGER-22 baseline using unsuppressed SARIF."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.parse import unquote, urlparse

ROOT = Path(__file__).resolve().parent.parent
BASELINE = ROOT / "build/analyzer-baseline-2026-11-17.tsv"
RULES = {"CA2000", "CA2213", "CA1001", "CA2016"}
HEADER = "# UX-LEDGER-22 temporary debt frozen 2026-11-17; remove with UX-LEDGER-23.\n# Assembly\tRepo path\tSHA256(LF source)\tRule\tStart line\tStart column\tEnd line\tEnd column\tSHA256(message)\n"


def digest(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def entries(directory):
    found = set()
    for log in sorted(Path(directory).glob("*.sarif")):
        for run in json.loads(log.read_text(encoding="utf-8"))["runs"]:
            for result in run["results"]:
                if result["ruleId"] not in RULES:
                    continue
                location = result["locations"][0]["physicalLocation"]
                uri = location["artifactLocation"]["uri"]
                source = Path(unquote(urlparse(uri).path))
                relative = source.resolve().relative_to(ROOT).as_posix()
                region = location["region"]
                found.add("\t".join([
                    log.stem, relative, digest(source.read_text(encoding="utf-8-sig")),
                    result["ruleId"], str(region["startLine"]), str(region["startColumn"]),
                    str(region["endLine"]), str(region["endColumn"]),
                    digest(result["message"]["text"]),
                ]))
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sarif_directory", type=Path)
    args = parser.parse_args()
    logs = list(args.sarif_directory.glob("*.sarif"))
    if not logs:
        parser.error("No SARIF files; refusing to prune from an empty capture")
    current = entries(args.sarif_directory)
    old = {line for line in BASELINE.read_text().splitlines() if line and not line.startswith("#")}
    # Only prune assemblies actually captured; an unrelated targeted build must not erase their debt.
    captured = {log.stem for log in logs}
    retained = {line for line in old if line.split("\t")[0] not in captured or line in current}
    new = current - old
    if new:
        parser.error(f"{len(new)} unbaselined/changed diagnostics: fix the debt, do not rebaseline it")
    BASELINE.write_text(HEADER + "\n".join(sorted(retained)) + "\n")
    print(f"Baseline: {len(old)} -> {len(retained)} ({len(old - retained)} removed; no additions)")


if __name__ == "__main__":
    main()
