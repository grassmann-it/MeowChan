#!/usr/bin/env python3
"""Focused orchestration contracts; compiles extracted production entrypoints only.
Run: python3 tests/gallery-error-decomposition/check_contract.py
No application build, native input, decoder or GUI claim.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'tests/MeowChan.DesktopSmoke/GalleryErrorInteractionChecks.cs'
BOUNDARY = '    private static async Task CheckNativeClipFeedbackAsync(MainWindow owner)'
BASE = 'ba1842af4'


class GalleryErrorDecompositionContracts(unittest.TestCase):
    def test_all_existing_checks_including_native_pointer_are_byte_identical(self):
        old = subprocess.check_output(
            ['git', 'show', f'{BASE}:tests/MeowChan.DesktopSmoke/GalleryErrorInteractionChecks.cs'],
            cwd=ROOT, text=True)
        self.assertEqual(old[old.index(BOUNDARY):], SOURCE.read_text()[SOURCE.read_text().index(BOUNDARY):])

    def test_exact_entrypoint_execution_and_fail_fast_contracts(self):
        source = SOURCE.read_text()
        # Compile the real wrapper and runner (not a duplicate scheduling model).
        runner = source[source.index('// Each split entry'):source.index(BOUNDARY)] + '\n}\n'
        enum = (SOURCE.parent / 'GalleryErrorCheckGroup.cs').read_text().split(';', 1)[1]
        harness = (Path(__file__).parent / 'OrchestrationContractHarness.cs').read_text()
        with tempfile.TemporaryDirectory(prefix='gallery-error-contract-') as directory:
            path = Path(directory)
            (path / 'Contract.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>''')
            (path / 'Runner.cs').write_text('using System.Text.Json;\n' + enum + runner)
            (path / 'Harness.cs').write_text(harness)
            result = subprocess.run(['dotnet', 'run', '--project', str(path / 'Contract.csproj'),
                                     '--verbosity', 'quiet'], text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn('PASS: 5 schedules, fixture failure, check failure, authorization, invalid group', result.stdout)
            print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main(verbosity=2)
