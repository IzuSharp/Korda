#!/usr/bin/env python3
"""Immutable Linux B15 actual application/API/package acceptance receipt."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--native', type=Path, required=True)
p.add_argument('--design', type=Path, required=True)
p.add_argument('--sdk', type=Path, required=True)
a = p.parse_args()
out = a.output.resolve(); out.mkdir(parents=True, exist_ok=False)
logs = out / 'logs'; logs.mkdir(); commands = []
def sha(path):
    with Path(path).open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()
def run(label, argv):
    r = subprocess.run(list(map(str, argv)), cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    log = logs / (label + '.log'); log.write_bytes(r.stdout)
    commands.append({'label':label,'argv':list(map(str,argv)),'exit_code':r.returncode,'log':str(log.relative_to(out)),'sha256':sha(log)})
    (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
    print(label,r.returncode,flush=True)
    if r.returncode: raise RuntimeError(r.stdout.decode(errors='replace')[-4000:])
    return r.stdout.decode()
result = {'task':'B15','platform':'linux-x64','status':'running','global_acceptance':False}
try:
    result['source_commit'] = run('source', ['git','rev-parse','HEAD']).strip()
    result['source_dirty'] = bool(run('worktree', ['git','status','--porcelain','--untracked-files=no']).strip())
    if result['source_dirty']: raise RuntimeError('Acceptance source must be committed.')
    design_commit = run('design', ['git','-C',a.design,'rev-parse','HEAD']).strip()
    assert design_commit == 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
    result['design_commit'] = design_commit
    inputs = subprocess.check_output(['git','ls-files','src/Server.Application','src/Server.Native','src/Server.Streaming','src/Interop.Async','src/Interop.Abi','src/NativeWeave.Core','tests/Server.Application','samples/api/server-application','Cargo.lock','global.json','Directory.Build.props'],cwd=ROOT,text=True).splitlines()
    result['input_sha256'] = {name:sha(ROOT/name) for name in inputs}
    assetdir=out/'input';assetdir.mkdir();native=assetdir/'libnativeweave_server.so';shutil.copy2(a.native,native)
    result['native_sha256']=sha(native)
    common=['-m:1','-nr:false','-p:UseSharedCompilation=false']
    asset='-p:NativeWeaveServerAssetPath='+str(native)
    project=ROOT/'tests/Server.Application/Server.Application.Tests.csproj'
    run('restore-jit',['dotnet','restore',project,'--locked-mode','--ignore-failed-sources',*common])
    run('build-jit',['dotnet','build',project,'-c','Release','--no-restore',asset,*common])
    exe=ROOT/'tests/Server.Application/bin/Release/net11.0/NativeWeave.Server.Application.Tests.dll'
    run('managed-component',['dotnet',exe])
    jit=run('actual-jit',['dotnet',exe,'--real']);assert '85 assertions' in jit
    api=ROOT/'tests/Server.Application/ApiCheck/ApiCheck.csproj'
    run('api-restore',['dotnet','restore',api,'--locked-mode','--ignore-failed-sources',*common])
    run('api-build',['dotnet','build',api,'-c','Release','--no-restore',*common])
    apiresult=run('api-exact',['dotnet',api.parent/'bin/Release/net11.0/NativeWeave.Server.Application.ApiCheck.dll',ROOT,a.design,a.sdk]);assert '278 exact symbols' in apiresult
    run('restore-aot',['dotnet','restore',project,'-r','linux-x64','-p:PublishAot=true','--locked-mode','--ignore-failed-sources',*common])
    run('publish-aot',['dotnet','publish',project,'-c','Release','-r','linux-x64','-p:PublishAot=true','--no-restore',asset,'-o',out/'aot',*common])
    aotexe=out/'aot/NativeWeave.Server.Application.Tests';aot=run('actual-aot',[aotexe,'--real']);assert '85 assertions' in aot and 'dynamic=False' in aot
    run('package-consumer',['python3',ROOT/'tests/Server.Application/package_probe.py','--output',out/'package','--native',native])
    result.update(status='passed',jit_assertions=85,aot_assertions=85,public_symbols=278,source_dirty=False,aot_executable_sha256=sha(aotexe),package_summary_sha256=sha(out/'package/summary.json'))
except Exception as error:
    result.update(status='failed',failure=str(error));raise
finally:
    result['commands']=commands
    (out/'result.json').write_text(json.dumps(result,indent=2)+'\n')
