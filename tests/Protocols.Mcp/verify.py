#!/usr/bin/env python3
"""Immutable Linux B19 actual Host, exact API and independent package receipt."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--native', type=Path, required=True)
p.add_argument('--design', type=Path, required=True)
p.add_argument('--sdk', type=Path, required=True)
a = p.parse_args()
out = a.output.resolve(); out.mkdir(parents=True, exist_ok=False)
logs = out/'logs'; logs.mkdir(); commands = []
def sha(path):
    with Path(path).open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()
def run(label, argv):
    result = subprocess.run(list(map(str, argv)), cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    log = logs/(label+'.log'); log.write_bytes(result.stdout)
    commands.append({'label':label,'argv':list(map(str,argv)),'exit_code':result.returncode,'log':str(log.relative_to(out)),'sha256':sha(log)})
    (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
    print(label,result.returncode,flush=True)
    if result.returncode: raise RuntimeError(result.stdout.decode(errors='replace')[-5000:])
    return result.stdout.decode()
result={'task':'B19','platform':'linux-x64','status':'running','global_acceptance':False}
try:
    result['source_commit']=run('source',['git','rev-parse','HEAD']).strip()
    if run('worktree',['git','status','--porcelain','--untracked-files=no']).strip(): raise RuntimeError('Acceptance inputs must be committed.')
    result['design_commit']=run('design',['git','-C',a.design,'rev-parse','HEAD']).strip()
    assert result['design_commit']=='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
    result['dotnet']=run('dotnet-version',['dotnet','--version']).strip()
    result['rustc']=run('rustc-version',['rustc','-Vv']).strip()
    inputs=subprocess.check_output(['git','ls-files','src/Protocols.Mcp','native/protocols/mcp','src/Server.Application','src/Server.Native','src/Server.Streaming','native/server','src/Interop.Async','src/Interop.Abi','src/NativeWeave.Core','tests/Protocols.Mcp','samples/api/protocols-mcp','docs/guides/protocols-mcp.md','Cargo.lock','global.json','Directory.Build.props'],cwd=ROOT,text=True).splitlines()
    result['input_sha256']={name:sha(ROOT/name) for name in inputs}
    assetdir=out/'input'; assetdir.mkdir(); native=assetdir/'libnativeweave_server.so'; shutil.copy2(a.native,native)
    result['native_sha256']=sha(native)
    run('native-features',['cargo','tree','-p','nativeweave-protocol-mcp','-e','features','--locked','--offline'])
    run('native-format',['cargo','fmt','-p','nativeweave-protocol-mcp','-p','nativeweave-server','--','--check'])
    run('native-clippy',['cargo','clippy','-p','nativeweave-protocol-mcp','-p','nativeweave-server','--all-targets','--locked','--offline','--','-D','warnings'])
    run('native-layout-sdk-peak',['cargo','test','-p','nativeweave-protocol-mcp','--locked','--offline','--','--nocapture','--test-threads=1'])
    preparse=run('native-preparse-ownership',['cargo','test','-p','nativeweave-server','engine::mcp::tests::actual_host_denial_and_full_peak_failures_never_enter_public_sdk_or_handler','--locked','--offline','--','--nocapture'])
    assert preparse.count('sdk_admissions=0 handlers=0')==7
    common=['-m:1','-nr:false','-p:UseSharedCompilation=false']; asset='-p:NativeWeaveServerAssetPath='+str(native)
    project=ROOT/'tests/Protocols.Mcp/Protocols.Mcp.Tests.csproj'
    run('restore-jit',['dotnet','restore',project,'--locked-mode','--ignore-failed-sources',*common])
    run('build-jit',['dotnet','build',project,'-c','Release','--no-restore',asset,*common])
    exe=project.parent/'bin/Release/net11.0/NativeWeave.Server.Mcp.Tests.dll'
    jit=run('actual-jit',['dotnet',exe]); match=re.search(r'assertions=(\d+), managed_retirements=(\d+), dynamic=True',jit); assert match
    jit_count=int(match[1]); assert jit_count>=230
    api=ROOT/'tests/Protocols.Mcp/ApiCheck/ApiCheck.csproj'
    run('api-restore',['dotnet','restore',api,'--locked-mode','--ignore-failed-sources',*common])
    run('api-build',['dotnet','build',api,'-c','Release','--no-restore',*common])
    api_result=run('api-exact',['dotnet',api.parent/'bin/Release/net11.0/NativeWeave.Server.Mcp.ApiCheck.dll',ROOT,a.design,a.sdk]); assert '65 exact symbols' in api_result and '5 exact static consumer cases passed' in api_result
    run('restore-aot',['dotnet','restore',project,'-r','linux-x64','-p:PublishAot=true','--locked-mode','--ignore-failed-sources',*common])
    run('publish-aot',['dotnet','publish',project,'-c','Release','-r','linux-x64','-p:PublishAot=true','--no-restore',asset,'-o',out/'aot',*common])
    aotexe=out/'aot/NativeWeave.Server.Mcp.Tests'; aot=run('actual-aot',[aotexe]); match=re.search(r'assertions=(\d+), managed_retirements=(\d+), dynamic=False',aot); assert match and int(match[1])==jit_count
    run('package-consumer',['python3',ROOT/'tests/Protocols.Mcp/package_probe.py','--output',out/'package','--native',native])
    result.update(status='passed',jit_assertions=jit_count,aot_assertions=int(match[1]),aot_managed_retirement_observations=int(match[2]),public_symbols=65,consumer_compile_fixtures=5,preparse_rejected_cases=7,preparse_success_cases=1,source_dirty=False,aot_executable_sha256=sha(aotexe),package_summary_sha256=sha(out/'package/summary.json'))
except Exception as error:
    result.update(status='failed',failure=str(error)); raise
finally:
    result['commands']=commands
    (out/'result.json').write_text(json.dumps(result,indent=2)+'\n')
