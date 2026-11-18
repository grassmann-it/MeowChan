"""Package 9 verification-audit guardrails.

The module deliberately uses only the standard library so the audit remains
runnable in the repository's minimal preflight environment.
"""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REPORT = ROOT / "review/package9/test-verification-audit-2026-11-18.md"
EXPECTED = [f"CX-{index:02d}" for index in range(1, 8)]
FORBIDDEN_PREFIXES = (
    ".github/workflows/",
    "tests/MeowChan.DesktopSmoke/",
)
FORBIDDEN_SUFFIXES = (
    ".lock.json",
)
EVIDENCE_PATHS = {
    "CX-01": (
        "tests/MeowChan.DesktopTests/ApphostStartupSmokeTests.cs",
        "tests/MeowChan.DesktopSmoke/StartupFirstFrameChecks.cs",
        "tests/MeowChan.DesktopSmoke/TrayLifecycleChecks.cs",
    ),
    "CX-02": (
        "tests/MeowChan.DesktopTests/ApphostSourceActivationStartupTests.cs",
        "tests/MeowChan.DesktopSmoke/StartupPreviewChecks.cs",
        "tests/MeowChan.DesktopSmoke/InterruptedStartupChecks.cs",
        "tests/MeowChan.DesktopSmoke/Cx02GalleryExportClosureChecks.cs",
    ),
    "CX-03": (
        "tests/MeowChan.DesktopSmoke/DownloadSearchStartupTruthChecks.cs",
        "tests/MeowChan.DesktopSmoke/SourceFilterWaveChecks.cs",
        "tests/MeowChan.DesktopTests/DownloadSearchReconciliationTests.cs",
    ),
    "CX-04": (
        "tests/MeowChan.DesktopSmoke/ReaderViewChecks.cs",
        "tests/MeowChan.DesktopSmoke/ReaderUxChecks.cs",
        "tests/MeowChan.DesktopSmoke/ReaderToolbarChecks.cs",
        "tests/MeowChan.DesktopTests/ReaderNavigationLayoutTests.cs",
    ),
    "CX-05": (
        "tests/MeowChan.DesktopSmoke/MediaPageChecks.BooruCaptures.cs",
        "tests/MeowChan.DesktopSmoke/MediaFilterSurfaceChecks.cs",
        "tests/MeowChan.DesktopSmoke/MediaInteractionChecks.cs",
        "tests/MeowChan.DesktopTests/MediaPageSelectionLayoutTests.cs",
    ),
    "CX-06": (
        "tests/MeowChan.DesktopSmoke/Cx06DirectGalleryOpenChecks.cs",
        "tests/MeowChan.DesktopSmoke/MediaGalleryReturnChecks.cs",
        "tests/MeowChan.DesktopSmoke/GalleryCloseChecks.cs",
        "tests/MeowChan.DesktopTests/ApphostMediaSmokeTests.cs",
    ),
    "CX-07": (
        "tests/MeowChan.DesktopSmoke/LocalMediaTagChecks.cs",
        "tests/MeowChan.DesktopSmoke/GalleryMediaTagChecks.cs",
        "tests/MeowChan.DesktopSmoke/PrivateTagBarChecks.cs",
        "tests/MeowChan.DesktopTests/GalleryMediaTagTests.cs",
    ),
}


def _report() -> str:
    return REPORT.read_text(encoding="utf-8")


def test_report_contains_exactly_the_seven_audited_packages() -> None:
    package_ids = re.findall(r"^\| (CX-\d{2}) \|", _report(), re.MULTILINE)
    assert package_ids == EXPECTED


def test_report_maps_existing_evidence_and_declares_spec_boundary() -> None:
    report = _report()
    assert "Keine native Abnahme behauptet" in report
    assert "Paketdefinition" in report
    assert "nicht vorhanden" in report
    for package_id in EXPECTED:
        assert f"| {package_id} |" in report, package_id
        for relative_path in EVIDENCE_PATHS[package_id]:
            assert (ROOT / relative_path).is_file(), relative_path


def test_report_records_ci_and_duplicate_pr_boundaries() -> None:
    report = _report()
    for marker in ("PR #72", "PR #74", "keine Checks", "CI-Nachweis"):
        assert marker in report, marker


def test_audit_changes_stay_outside_reserved_areas() -> None:
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", "--", "."], cwd=ROOT, text=True
    ).splitlines()
    untracked = subprocess.check_output(
        ["git", "ls-files", "--others", "--exclude-standard"], cwd=ROOT, text=True
    ).splitlines()
    paths = set(changed) | set(untracked)
    allowed = {
        "tests/package9_audit/test_package9_inventory.py",
        "review/package9/test-verification-audit-2026-11-18.md",
    }
    unexpected = sorted(
        path
        for path in paths
        if path not in allowed
        and (
            any(path.startswith(prefix) for prefix in FORBIDDEN_PREFIXES)
            or any(path.endswith(suffix) for suffix in FORBIDDEN_SUFFIXES)
        )
    )
    assert not unexpected, unexpected


def _run_without_pytest() -> None:
    checks = (
        test_report_contains_exactly_the_seven_audited_packages,
        test_report_maps_existing_evidence_and_declares_spec_boundary,
        test_report_records_ci_and_duplicate_pr_boundaries,
        test_audit_changes_stay_outside_reserved_areas,
    )
    for check in checks:
        check()
        print(f"PASS {check.__name__}")


if __name__ == "__main__":
    _run_without_pytest()
