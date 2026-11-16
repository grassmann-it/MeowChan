#!/usr/bin/env python3
"""Focused orchestration contracts; compiles extracted production entrypoints only.
Run: python3 tests/gallery-error-decomposition/check_contract.py
No application build, native input, decoder or GUI claim.
"""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'tests/MeowChan.DesktopSmoke/GalleryErrorInteractionChecks.cs'
BOUNDARY = '    private static async Task CheckNativeClipFeedbackAsync(MainWindow owner)'
# Provenance and intentional-change procedure: see README.md, Pinned reference.
# UTF-8 text with universal-newline normalization, from BOUNDARY through EOF.
CHECK_IMPLEMENTATIONS_SHA256 = 'e31752624558bbc02785e79db6dd666540ae899761c2b6a79aa99995009ef6a2'
BUILD_TIMEOUT_SECONDS = 60
EXECUTION_TIMEOUT_SECONDS = 15


class GalleryErrorDecompositionContracts(unittest.TestCase):
    def test_all_existing_checks_including_native_pointer_are_byte_identical(self):
        source = SOURCE.read_text(encoding='utf-8')
        actual = hashlib.sha256(source[source.index(BOUNDARY):].encode('utf-8')).hexdigest()
        self.assertEqual(CHECK_IMPLEMENTATIONS_SHA256, actual,
                         'Gallery check implementation changed; review coverage and follow the '
                         'pinned-reference maintenance procedure in README.md. Do not blindly re-pin.')

    def run_bounded(self, command, *, cwd, env, timeout):
        try:
            result = subprocess.run(command, cwd=cwd, env=env, text=True,
                                    capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            self.fail(f'Contract subprocess exceeded {timeout}s: {command}')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result

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
            # No NuGet dependencies: use the installed SDK, not network feeds.
            (path / 'NuGet.Config').write_text(
                '<configuration><packageSources><clear /></packageSources></configuration>')
            env = dict(os.environ, MSBUILDDISABLENODEREUSE='1',
                       DOTNET_CLI_USE_MSBUILD_SERVER='0',
                       DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true')
            self.run_bounded(
                ['dotnet', 'build', str(path / 'Contract.csproj'), '--verbosity', 'quiet',
                 '--disable-build-servers', '-p:UseSharedCompilation=false', '-nodeReuse:false',
                 '--configfile', str(path / 'NuGet.Config')],
                cwd=path, env=env, timeout=BUILD_TIMEOUT_SECONDS)
            result = self.run_bounded(
                ['dotnet', str(path / 'bin/Debug/net10.0/Contract.dll')],
                cwd=path, env=env, timeout=EXECUTION_TIMEOUT_SECONDS)
            self.assertIn('PASS: 5 schedules, fixture failure, check failure, authorization, invalid group', result.stdout)
            print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main(verbosity=2)
