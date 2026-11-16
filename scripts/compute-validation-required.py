#!/usr/bin/env python3
"""Compute the step ids the final validation gate must see succeed.

The workflow predicates decide which steps run; this table decides which of
them are mandatory so a selected scenario can never be silently skipped.
tests/test_validation_gate.py cross-checks both against the workflow file.
"""
from __future__ import annotations

import argparse

FULL_LANE_REQUIRED = [
    "record_source", "public_api", "dev_build", "restore_release", "retain_release_assets", "build_all",
    "package_fixtures", "local_package", "smoke_runner_contract", "smoke_restore", "smoke_assets", "smoke_build",
    "plugin_delivery", "smoke_normal", "upgrade_replay", "upgrade_102_replay", "first_run", "preference_startup",
    "scoped_diagnostics", "gif_playback", "gallery_errors", "reader_media", "gallery_features", "media_input",
    "tray_lifecycle",
]

# Build prerequisites that run on every full lane for any selected scenario.
TARGETED_PREREQUISITES = ["record_source", "smoke_restore", "smoke_assets", "smoke_build", "plugin_delivery"]

# Steps whose workflow predicate selects them for each non-"all" scenario.
TARGETED_STEPS = {
    "selection": ["smoke_normal"],
    "gui-only": ["smoke_normal", "first_run", "preference_startup", "scoped_diagnostics", "gif_playback",
                 "gallery_errors", "reader_media", "gallery_features"],
    "gallery-errors": ["gallery_errors"],
    "gallery-features": ["gallery_features"],
    "gif-playback": ["gif_playback"],
    "media-input": ["package_fixtures", "media_input"],
}


def effective_scenario(event_name: str, scenario: str) -> str:
    """Push/PR (and any empty input) use the full requirements."""
    if event_name != "workflow_dispatch" or not scenario:
        return "all"
    return scenario


OFFLINE_RETEST_LANES = ("lane-a", "lane-b", "lane-c", "lane-d")
OFFLINE_RETEST_REQUIRED = ["offline_retest_guard", "record_source", "restore_release", "build_all", "process_probe", "core_tests"]


def required_steps(event_name: str, mode: str, scenario: str, lane: str, runner_os: str, runner_arch: str) -> list[str]:
    if mode == "offline-retest":
        # Bounded retest: one non-macOS lane, scenario all, never GUI or packaging.
        if lane not in OFFLINE_RETEST_LANES or runner_os == "macOS" or scenario not in ("", "all"):
            raise ValueError(f"offline-retest rejects lane={lane} os={runner_os} scenario={scenario}")
        return list(OFFLINE_RETEST_REQUIRED)
    scenario = effective_scenario(event_name, scenario)
    is_full = runner_os == "macOS" or lane == "lane-a"
    candidate = mode in ("prerelease-candidate", "stable-candidate")
    if scenario == "all":
        full = (["candidate_matrix"] if candidate else []) + FULL_LANE_REQUIRED
        if runner_os == "Linux" and lane == "lane-a":
            required = full + ["linux_setup", "tray_host", "core_tests", "smoke_plugins"]
            if candidate and runner_arch == "X64":
                required.append("smoke_1_1_registry")
            return required
        if runner_os == "Windows" and lane == "lane-a":
            return full + ["prepare_desktop", "core_tests"]
        if runner_os == "macOS":
            return full + ["prepare_desktop", "mac_tests"]
        return ["record_source", "core_tests"]
    if scenario not in TARGETED_STEPS:
        raise ValueError(f"unknown scenario: {scenario}")
    if not is_full:
        return ["record_source"]
    required = list(TARGETED_PREREQUISITES)
    if runner_os in ("Windows", "macOS"):
        required.append("prepare_desktop")
    if runner_os == "Linux":
        required.append("linux_setup")
    for step in TARGETED_STEPS[scenario]:
        if step not in required:
            required.append(step)
    if scenario == "gui-only" and runner_os == "Linux":
        required.append("smoke_plugins")
    return required


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event-name", required=True)
    parser.add_argument("--mode", default="")
    parser.add_argument("--scenario", default="")
    parser.add_argument("--lane", required=True)
    parser.add_argument("--runner-os", required=True)
    parser.add_argument("--runner-arch", default="")
    a = parser.parse_args()
    print(",".join(required_steps(a.event_name, a.mode, a.scenario, a.lane, a.runner_os, a.runner_arch)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
