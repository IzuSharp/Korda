#!/usr/bin/env python3
"""Immutable current-platform B20 product, public consumer and diagnostics receipt."""
import argparse, csv, hashlib, io, json, os, pathlib, re, shutil, subprocess, time
R=pathlib.Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--design',type=pathlib.Path,required=True);p.add_argument('--sdk',type=pathlib.Path,required=True);a=p.parse_args()
O=a.output.resolve();O.mkdir(parents=True,exist_ok=False);logs=O/'logs';logs.mkdir();commands=[]
sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
def inputs():
 names=subprocess.check_output(['git','ls-files','-z'],cwd=R).decode().split('\0')
 prefixes=('native/server/','native/interop/','src/Server.','src/Interop.','src/NativeWeave.Core/','src/Packaging.Linux/','src/Packaging.Release/','tests/Server.Diagnostics/','tests/Server.Native/Bindings/','samples/api/server-diagnostics/')
 return {name:sha(R/name) for name in names if name and (name.startswith(prefixes) or name in ['Cargo.toml','Cargo.lock','global.json','rust-toolchain.toml','Directory.Build.props','Directory.Build.targets','NuGet.Config','docs/guides/server-diagnostics.md'])}
def run(label,argv,timeout=900,env=None):
 started=time.monotonic();print(label,flush=True)
 with (logs/(label+'.txt')).open('w') as log: result=subprocess.run(list(map(str,argv)),cwd=R,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=timeout)
 commands.append(dict(label=label,command=list(map(str,argv)),exit_code=result.returncode,seconds=time.monotonic()-started,log_sha256=sha(logs/(label+'.txt'))))
 (O/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
 if result.returncode:raise RuntimeError(label+' failed; '+str(logs/(label+'.txt')))
 return (logs/(label+'.txt')).read_text()
result=dict(task='B20',platform='linux-x64',global_acceptance=False,status='running',commands=commands)
try:
 result['source_commit']=run('source',['git','rev-parse','HEAD']).strip()
 assert not run('clean-source',['git','status','--porcelain','--untracked-files=all']).strip(),'Commit source before formal acceptance.'
 initial=inputs();result['source_sha256']=initial
 result['design_commit']=run('design',['git','-C',a.design,'rev-parse','HEAD']).strip();assert result['design_commit']=='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
 run('dotnet-version',['dotnet','--version']);run('rust-version',['rustc','--version']);run('native-features',['cargo','tree','-p','nativeweave-server','--locked','-e','features'])
 run('pinned-native-wire',['python3','tests/Server.Native/generate.py','--check']);run('exact-native-layout',['python3','tests/Server.Native/layout.py','--check'])
 binding=json.loads((R/'tests/Server.Native/Bindings/binding.json').read_text());coverage=json.loads((R/'tests/Server.Native/Bindings/coverage.json').read_text())
 assert binding['provenance']['adapter_sha256']==sha(R/'tests/Server.Native/Bindings/adapter-projection.rs')
 assert coverage['source_sha256']==sha(R/'native/server/host/src/exports.rs') and len(coverage['full_exact_exports'])==48
 run('native-clippy',['cargo','clippy','-p','nativeweave-server-diagnostics','-p','nativeweave-server','--all-targets','--locked','--','-D','warnings'])
 run('native-debug',['cargo','test','-p','nativeweave-server-diagnostics','--locked'])
 run('native-release',['cargo','test','-p','nativeweave-server-diagnostics','--release','--locked'])
 run('native-actual-host',['cargo','test','-p','nativeweave-server','--lib','diagnostics','--locked'])
 run('native-build',['cargo','build','-p','nativeweave-server','--release','--locked'])
 assets=O/'input';assets.mkdir();native=assets/'libnativeweave_server.so';shutil.copy2(R/'target/release/libnativeweave_server.so',native);result['native_sha256']=sha(native)
 common=['-p:UseSharedCompilation=false','-nr:false','-m:1','-p:RestoreLockedMode=true','-p:NativeWeaveServerAssetPath='+str(native)]
 project='tests/Server.Diagnostics/Server.Diagnostics.Tests.csproj';folder=R/'tests/Server.Diagnostics/bin'
 run('managed-debug-build',['dotnet','build',project,'-c','Debug',*common])
 run('debug-default',['dotnet',folder/'Debug/net11.0/NativeWeave.Server.Diagnostics.Tests.dll','--defaults'],120)
 run('managed-release-build',['dotnet','build',project,'-c','Release',*common])
 jit=run('actual-jit',['dotnet',folder/'Release/net11.0/NativeWeave.Server.Diagnostics.Tests.dll'],180);result['jit_assertions']=int(re.search(r'B20 actual diagnostics: (\d+) assertions',jit)[1])
 run('api-build',['dotnet','build','tests/Server.Diagnostics/ApiCheck/ApiCheck.csproj','-c','Release',*common])
 run('api-public-xml',['dotnet','tests/Server.Diagnostics/ApiCheck/bin/Release/net11.0/NativeWeave.Server.Diagnostics.ApiCheck.dll',R,a.sdk,a.design])
 run('sample-debug-build',['dotnet','build','samples/api/server-diagnostics/server-diagnostics.csproj','-c','Debug',*common])
 sample='samples/api/server-diagnostics/bin/Debug/net11.0/server-diagnostics.dll'
 run('sample-debug-disabled',['dotnet',sample]);run('sample-debug-explicit-local',['dotnet',sample,'--local'])
 rows=[]
 for mode in ['disabled','sampled','full','exporter-fault']:
  text=run('cost-jit-'+mode,['dotnet',folder/'Release/net11.0/NativeWeave.Server.Diagnostics.Tests.dll','--cost',mode],120)
  rows.append(next(csv.DictReader(io.StringIO(text))))
 run('publish-aot',['dotnet','publish',project,'-c','Release','-r','linux-x64','-p:PublishAot=true',*common,'-o',O/'aot'])
 exe=O/'aot/NativeWeave.Server.Diagnostics.Tests';aot=run('actual-aot',[exe],180);assert 'dynamic=False' in aot
 result['aot_assertions']=int(re.search(r'B20 actual diagnostics: (\d+) assertions',aot)[1]);assert result['jit_assertions']==result['aot_assertions']
 for mode in ['disabled','sampled','full','exporter-fault']:
  text=run('cost-aot-'+mode,[exe,'--cost',mode],120);rows.append(next(csv.DictReader(io.StringIO(text))))
 (O/'costs.json').write_text(json.dumps(dict(scope='Matched 300 failing HTTP requests plus 10 warmups per fresh process. Whole-process CPU/allocation/RSS include the test client and explicit observers. Callback timing is disabled in the default row; enabled rows show the process lifetime callback maximum. Finite observations, no superiority or zero-overhead claim.',rows=rows),indent=2)+'\n')
 run('independent-package',['python3','tests/Server.Diagnostics/package_probe.py','--output',O/'package','--native',native],1800)
 assert inputs()==initial,'Committed inputs changed during verification.'
 assert not subprocess.check_output(['git','status','--porcelain','--untracked-files=all'],cwd=R,text=True).strip(),'Verification mutated source.'
 result.update(status='passed',source_clean=True,native_unit_tests=5,native_actual_host_tests=1,exact_native_exports=48,public_context_symbols=10,default_release_debug=True,exporter_bounds={'entries':2048,'bytes':524288,'per_item_bytes':1024,'flush_milliseconds':250},aot_executable_sha256=sha(exe),jit_executable_sha256=sha(folder/'Release/net11.0/NativeWeave.Server.Diagnostics.Tests.dll'),package_summary_sha256=sha(O/'package/summary.json'),costs_sha256=sha(O/'costs.json'))
except Exception as error:
 result.update(status='failed',failure=str(error));raise
finally:(O/'result.json').write_text(json.dumps(result,indent=2)+'\n')
print('PASS immutable Linux x64 B20 diagnostics product and independent NuGet JIT/NativeAOT receipt.',flush=True)
