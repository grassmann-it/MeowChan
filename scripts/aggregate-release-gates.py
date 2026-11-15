#!/usr/bin/env python3
"""Fail-closed aggregation for CI validation and release gates.

The validation matrix deliberately lets independent checks continue after a
failure.  This script is the non-optional decision at the end of that flow:
missing, skipped, failed, or malformed evidence is never treated as success.
It also verifies the job results used by candidate and release workflows.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
from typing import Any

SUCCESS = "success"


def read_json(value: str | None, env_name: str | None, path: str | None) -> dict[str, Any]:
    if path:
        raw = Path(path).read_text(encoding="utf-8")
    elif env_name:
        raw = os.environ.get(env_name, "")
    else:
        raw = value or ""
    if not raw.strip():
        raise ValueError("JSON input is missing")
    parsed = json.loads(raw)
    if not isinstance(parsed, dict):
        raise ValueError("JSON input must be an object")
    return parsed


def split_names(value: str) -> list[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def step_gate(steps: dict[str, Any], required: list[str]) -> dict[str, Any]:
    failures: dict[str, str] = {}
    for name, step in steps.items():
        if not isinstance(step, dict):
            failures[name] = "malformed step record"
            continue
        outcome = step.get("outcome")
        if outcome not in (SUCCESS, "skipped"):
            failures[name] = str(outcome or "missing outcome")

    missing_or_not_success: dict[str, str] = {}
    for name in required:
        step = steps.get(name)
        outcome = step.get("outcome") if isinstance(step, dict) else None
        if outcome != SUCCESS:
            missing_or_not_success[name] = str(outcome or "missing")

    return {
        "passed": not failures and not missing_or_not_success,
        "failures": failures,
        "requiredNotSuccessful": missing_or_not_success,
        "required": required,
        "observed": sorted(steps),
    }


def needs_gate(needs: dict[str, Any], required: list[str]) -> dict[str, Any]:
    failures: dict[str, str] = {}
    for name in required:
        job = needs.get(name)
        result = job.get("result") if isinstance(job, dict) else None
        if result != SUCCESS:
            failures[name] = str(result or "missing")
    return {
        "passed": not failures,
        "failures": failures,
        "required": required,
        "observed": sorted(needs),
    }


def job_name_matches(actual: str, required: str) -> bool:
    return actual == required or actual.startswith(required + " (")


def run_gate(run: dict[str, Any], expected_sha: str, required_jobs: list[str]) -> dict[str, Any]:
    failures: list[str] = []
    if run.get("headSha") != expected_sha:
        failures.append(f"headSha={run.get('headSha')!r}, expected {expected_sha!r}")
    if run.get("status") != "completed":
        failures.append(f"status={run.get('status')!r}")
    if run.get("conclusion") != SUCCESS:
        failures.append(f"conclusion={run.get('conclusion')!r}")

    jobs = run.get("jobs")
    if not isinstance(jobs, list):
        failures.append("jobs is missing or not a list")
        jobs = []
    for required in required_jobs:
        matching = [job for job in jobs if isinstance(job, dict)
                    and job_name_matches(str(job.get("name", "")), required)]
        if not matching:
            failures.append(f"required job {required!r} is missing")
            continue
        if not all(job.get("status") == "completed" and job.get("conclusion") == SUCCESS
                   for job in matching):
            failures.append(f"required job {required!r} did not succeed")

    return {
        "passed": not failures,
        "failures": failures,
        "expectedHeadSha": expected_sha,
        "requiredJobs": required_jobs,
        "observedJobs": [job.get("name") for job in jobs if isinstance(job, dict)],
    }


def write_report(report: dict[str, Any], path: str | None) -> None:
    encoded = json.dumps(report, indent=2, sort_keys=True) + "\n"
    print(encoded, end="")
    if path:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(encoded, encoding="utf-8")


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(description=__doc__)
    subparsers = root.add_subparsers(dest="command", required=True)

    for command in ("steps", "needs"):
        sub = subparsers.add_parser(command)
        sub.add_argument("--json-env")
        sub.add_argument("--json-file")
        sub.add_argument("--required", default="")
        sub.add_argument("--report")

    run = subparsers.add_parser("run")
    run.add_argument("--json-file", required=True)
    run.add_argument("--expected-sha", required=True)
    run.add_argument("--required-job", action="append", default=[])
    run.add_argument("--report")
    return root


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "steps":
            payload = read_json(None, args.json_env, args.json_file)
            report = step_gate(payload, split_names(args.required))
        elif args.command == "needs":
            payload = read_json(None, args.json_env, args.json_file)
            report = needs_gate(payload, split_names(args.required))
        else:
            payload = read_json(None, None, args.json_file)
            report = run_gate(payload, args.expected_sha, args.required_job)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        report = {"passed": False, "failures": [str(error)]}

    write_report(report, args.report)
    if not report.get("passed"):
        print("Release gate rejected the run.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
