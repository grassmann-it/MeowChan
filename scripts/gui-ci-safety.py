#!/usr/bin/env python3
"""Quarantine a GUI worker after an explicitly failed process-tree cleanup."""
import json
from pathlib import Path
import sys


def cleanup_failures(root):
    failures = [str(path) for path in root.rglob('unsafe-cleanup')]
    for path in root.rglob('results.json'):
        try:
            report = json.loads(path.read_text())
        except (OSError, ValueError):
            continue  # Missing evidence is rejected by its owning step, not a cleanup claim.
        if any('Process cleanup failed' in str(row.get('error', '')) for row in report.get('results', [])):
            failures.append(str(path))
    return failures


if __name__ == '__main__':
    failures = cleanup_failures(Path('artifacts'))
    if failures:
        print('GUI worker quarantined: process-tree cleanup failed in ' + ', '.join(failures), file=sys.stderr)
        sys.exit(1)
