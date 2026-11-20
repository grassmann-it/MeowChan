"""Bash discovery for tests that run GitHub Actions shell snippets."""
import os
from pathlib import Path
import shutil


def workflow_bash():
    """Use native Bash on POSIX and Git for Windows Bash, never the WSL shim."""
    if os.name != 'nt':
        bash = shutil.which('bash')
        if bash:
            return bash
    else:
        candidates = []
        git = shutil.which('git')
        if git:
            # git.exe is normally in Git/cmd or Git/bin (also Git/mingw64/bin).
            for parent in Path(git).resolve().parents:
                candidates.extend((parent / 'bin/bash.exe', parent / 'usr/bin/bash.exe'))
        for variable in ('ProgramW6432', 'ProgramFiles', 'ProgramFiles(x86)', 'LOCALAPPDATA'):
            base = os.environ.get(variable)
            if base:
                root = Path(base) / ('Programs/Git' if variable == 'LOCALAPPDATA' else 'Git')
                candidates.extend((root / 'bin/bash.exe', root / 'usr/bin/bash.exe'))
        for candidate in candidates:
            if candidate.is_file():
                return str(candidate)
    raise RuntimeError('Workflow guard tests require Bash (Git for Windows Bash on Windows)')
