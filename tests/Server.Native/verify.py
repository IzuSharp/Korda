#!/usr/bin/env python3
"""Current-platform B17 product verifier; executes the actual native Host in JIT and AOT."""
import argparse,hashlib,json,os,pathlib,subprocess,time
R=pathlib.Path(__file__).resolve().parents[2];p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,default=R/'artifacts/task06/B17/final');args=p.parse_args();O=args.output.resolve();O.mkdir(parents=True,exist_ok=True)
def hashfile(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def sources():
 names=subprocess.check_output(['git','ls-files','-z'],cwd=R).decode().split('\0');return {n:hashfile(R/n)for n in names if n and (R/n).is_file() and (n.startswith(('native/server/','src/Server.Native/','src/Server.Application/','tests/Server.Native/','samples/api/server-native/')) or n in ['Cargo.lock','Cargo.toml','global.json','rust-toolchain.toml','Directory.Build.props','Directory.Build.targets','docs/guides/server-native.md'])}
commands=[]
def run(name,cmd,timeout=300,env=None):
 started=time.monotonic();print(name,flush=True)
 with (O/(name+'.txt')).open('w')as log:
  x=subprocess.run(cmd,cwd=R,stdout=log,stderr=subprocess.STDOUT,timeout=timeout,env=env)
 row=dict(name=name,command=cmd,exit_code=x.returncode,seconds=time.monotonic()-started,log_sha256=hashfile(O/(name+'.txt')));commands.append(row)
 if x.returncode:raise RuntimeError(f'{name} failed; see {O/(name+".txt")}')
 return (O/(name+'.txt')).read_text()
initial=sources();result=dict(schema_version=1,task='B17',platform='linux-x64',scope='Actual required Rust Host component and static dispatcher; no Windows or final pristine-package claim.',commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=R,text=True).strip(),source_sha256=initial,commands=commands)
try:
 run('pinned-wire',['python3','tests/Server.Native/generate.py','--check'])
 run('exact-layout',['python3','tests/Server.Native/layout.py','--check'])
 run('native-clippy',['cargo','clippy','-p','nativeweave-server','--all-targets','--locked','--','-D','warnings'])
 run('native-tests',['cargo','test','-p','nativeweave-server','--lib','--locked'])
 run('native-build',['cargo','build','-p','nativeweave-server','--release','--locked'])
 run('inventory',['python3','tests/Server.Native/inventory.py'])
 run('parsed-inventory',['cargo','run','--quiet','--manifest-path','src/Generators.Model/native-inventory/Cargo.toml','--locked','--','tests/Server.Native/Bindings/adapter-projection.rs','tests/Server.Native/Bindings/parsed-inventory.json'])
 common=['tests/Server.Native/Bindings/binding.json','tests/Server.Native/Bindings/parsed-inventory.json','tests/Server.Native/Bindings/adapter-projection.rs','tests/Server.Native/Bindings/linked-symbols.txt']
 run('a10',['dotnet','run','--project','src/Generators.CSharp','-p:UseSharedCompilation=false','--',*common,'nativeweave_server','tests/Server.Native/Bindings/CSharp','NativeWeave.Server.Native.Generated'])
 run('a11',['dotnet','run','--project','src/Generators.Rust','-p:UseSharedCompilation=false','--',*common,'native/server/host/generated'])
 flags=['-p:UseSharedCompilation=false','-nr:false','-m:1']
 run('managed-build',['dotnet','build','tests/Server.Native/Server.Native.Tests.csproj',*flags])
 env=os.environ|{'NW_SERVER_EVIDENCE':str(O/'jit-fixture')}
 run('jit',['dotnet','tests/Server.Native/bin/Debug/net11.0/NativeWeave.Server.Native.Tests.dll'],90,env)
 run('aot-publish',['dotnet','publish','tests/Server.Native/Server.Native.Tests.csproj','-c','Release','-r','linux-x64','-p:PublishAot=true',*flags,'-o',str(O/'aot')],900)
 run('aot',[str(O/'aot/NativeWeave.Server.Native.Tests')],90,os.environ|{'NW_SERVER_EVIDENCE':str(O/'aot-fixture')})
 run('sample-build',['dotnet','build','samples/api/server-native/server-native.csproj',*flags])
 run('sample-minimal',['dotnet','samples/api/server-native/bin/Debug/net11.0/server-native.dll'],60)
 run('sample-advanced',['dotnet','samples/api/server-native/bin/Debug/net11.0/server-native.dll','--advanced'],60)
 run('direct-build',['cargo','build','-p','nativeweave-server','--release','--example','direct','--locked'])
 run('comparison-build',['dotnet','build','tests/Server.Native/Benchmarks/Benchmarks.csproj','-c','Release',*flags])
 run('costs',['python3','tests/Server.Native/benchmark.py'],120)
 final=sources();assert final==initial,'Verification changed committed source/generated input.'
 result['artifacts_sha256']={str(path.relative_to(R)):hashfile(path)for path in [R/'target/release/libnativeweave_server.so',R/'tests/Server.Native/bin/Debug/net11.0/NativeWeave.Server.Native.Tests.dll',O/'aot/NativeWeave.Server.Native.Tests',R/'artifacts/task06/B17/costs.json']}
 result['passed']=True
except Exception as error:
 result['passed']=False;result['error']=str(error);raise
finally:(O/'result.json').write_text(json.dumps(result,indent=2)+'\n')
print('PASS current Linux x64 B17 product verification',flush=True)
