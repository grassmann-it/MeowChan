#!/usr/bin/env python3
"""Export old code and generator only; deliberately does not invoke dotnet."""
import argparse
import pathlib
import shutil
import subprocess
import tarfile
import tempfile

REVISION = '8ec0cf50b270e0551a06d3ad61eff0f448bd70ff'
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('destination', type=pathlib.Path)
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parents[3]
destination = a.destination.resolve()
if destination.exists():
    p.error('destination must not exist')
destination.mkdir(parents=True)
with tempfile.TemporaryFile() as archive:
    subprocess.run(['git', '-C', str(root), 'archive', REVISION], stdout=archive, check=True)
    archive.seek(0)
    with tarfile.open(fileobj=archive) as source:
        source.extractall(destination, filter='data')
fixture = destination / 'tests' / 'UpgradeFixtureGenerator'
fixture.mkdir()
for name in ['Program.cs', 'UpgradeFixtureGenerator.csproj']:
    shutil.copyfile(pathlib.Path(__file__).with_name(name), fixture / name)
(destination / 'UPGRADE-SOURCE-REVISION').write_text(REVISION + '\n')
print(fixture / 'UpgradeFixtureGenerator.csproj')
