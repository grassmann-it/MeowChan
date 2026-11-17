"""Package 9 audit guardrails."""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REPORT = ROOT / "review/package9/test-verification-audit-2026-11-17.md"
EXPECTED = [f"CX-{index:02d}" for index in range(1, 8)]
FORBIDDEN_PREFIXES = (".github/", "tests/MeowChan.DesktopSmoke/", "build/", "packages")


def test_report_contains_exactly_the_seven_audited_packages() -> None:
    report = REPORT.read_text(encoding="utf-8")
    package_ids = re.findall(r"^\| (CX-\d{2}) \|", report, re.MULTILINE)
    assert package_ids == EXPECTED


def test_report_maps_each_package_to_evidence_and_gap() -> None:
    report = REPORT.read_text(encoding="utf-8")
    for package_id in EXPECTED:
        assert f"| {package_id} |" in report, package_id
    assert "Keine native Abnahme behauptet" in report
    assert "offen" in report.lower()


def test_audit_changes_stay_outside_reserved_areas() -> None:
    changed = subprocess.check_output(["git", "diff", "--name-only", "--", "."], cwd=ROOT, text=True).splitlines()
    untracked = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard"], cwd=ROOT, text=True).splitlines()
    paths = set(changed) | set(untracked)
    allowed = {"tests/package9_audit/test_package9_inventory.py", "review/package9/test-verification-audit-2026-11-17.md"}
    unexpected = sorted(path for path in paths if path not in allowed and any(path.startswith(prefix) for prefix in FORBIDDEN_PREFIXES))
    assert not unexpected, unexpected
