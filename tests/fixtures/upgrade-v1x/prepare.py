#!/usr/bin/env python3
"""Build isolated old-release fixtures; no personal profile and no Git mutations."""
import argparse, hashlib, json, os, shutil, subprocess, tarfile, tempfile
from pathlib import Path
p=argparse.ArgumentParser()
p.add_argument('output',type=Path)
p.add_argument('--version',choices=('1.0.0','1.0.1'),required=True)
a=p.parse_args(); root=Path(__file__).resolve().parents[3]; output=a.output.resolve()
output.mkdir(parents=True,exist_ok=False)
sha=subprocess.check_output(['git','rev-parse','v'+a.version+'^{commit}'],cwd=root,text=True).strip()
source=output/'source';source.mkdir()
with tempfile.TemporaryFile() as archive:
    subprocess.run(['git','archive',sha],cwd=root,stdout=archive,check=True)
    archive.seek(0)
    with tarfile.open(fileobj=archive) as t:t.extractall(source,filter='data')
generator=Path(__file__).with_name('SeedReleaseProfile.cs')
shutil.copy2(generator,source/'tests/MeowChan.DesktopTests/R102SeedReleaseProfile.cs')
env=os.environ.copy()
for k in ('MEOWCHAN_PROFILE','OCHAN_PROFILE'):env.pop(k,None)
env.update(MEOWCHAN_UPGRADE_SEED_ROOT=str(output),MEOWCHAN_UPGRADE_SEED_VERSION=a.version,MEOWCHAN_DISABLE_UPDATE_CHECK='1')
command=['dotnet','test','tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj','-c','Release','--disable-build-servers','-p:UseSharedCompilation=false','-m:2','-p:SourceRevisionId='+sha,'--filter','FullyQualifiedName~R102SeedReleaseProfile','--logger','trx;LogFileName=old-generator.trx','--results-directory',str(output/'results')]
with (output/'generator.log').open('w') as log:
    result=subprocess.run(command,cwd=source,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=600)
meta={'version':a.version,'production_source_commit':sha,'generator_sha256':hashlib.sha256(generator.read_bytes()).hexdigest(),'exit_code':result.returncode,'command':command,'scope':'Real old production code plus isolated test-only seed overlay; not a published-package update test'}
(output/'source-binding.json').write_text(json.dumps(meta,indent=2)+'\n')
raise SystemExit(result.returncode)
