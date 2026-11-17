#!/usr/bin/env python3
"""B20 public-only diagnostic consumer, using the accepted A14 package writer/SDK."""
import argparse, hashlib, importlib.util, json, os, pathlib, shutil, subprocess, xml.etree.ElementTree as ET
R = pathlib.Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(); p.add_argument('--output', type=pathlib.Path, required=True); p.add_argument('--native', type=pathlib.Path, required=True); a = p.parse_args()
O = a.output.resolve(); O.mkdir(parents=True, exist_ok=False); (O/'logs').mkdir(); records=[]
sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
def run(label, argv, cwd=R, env=None):
 result=subprocess.run(list(map(str,argv)),cwd=cwd,env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=900)
 log=O/'logs'/(label+'.txt');log.write_bytes(result.stdout);records.append(dict(label=label,command=list(map(str,argv)),exit_code=result.returncode,log_sha256=sha(log)))
 (O/'commands.json').write_text(json.dumps(records,indent=2)+'\n');print(label,result.returncode,flush=True)
 if result.returncode:raise RuntimeError(result.stdout.decode(errors='replace')[-5000:])
 return result.stdout.decode()
flags=['-m:1','-nr:false','-p:UseSharedCompilation=false'];native=a.native.resolve();native_hash=sha(native)
source=run('source',['git','rev-parse','HEAD']).strip()
run('product-build',['dotnet','build','src/Server.Application/NativeWeave.Server.csproj','-c','Release','-p:RestoreLockedMode=true','-p:NativeWeaveServerAssetPath='+str(native),*flags])
task=R/'src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj'
run('sdk-task-build',['dotnet','build',task,'-c','Release','-p:RestoreLockedMode=true',*flags])
spec=importlib.util.spec_from_file_location('accepted_a14_packages',R/'src/Packaging.Linux/packages.py');packer=importlib.util.module_from_spec(spec);spec.loader.exec_module(packer)
version='0.1.0-preview.1'
external={node.attrib['Include']:node.attrib['Version'] for node in ET.parse(R/'src/Server.Application/NativeWeave.Server.csproj').iter('PackageReference')}
registry={
 'NativeWeave.Interop.Abi':('src/Interop.Abi',{}),
 'NativeWeave.Core':('src/NativeWeave.Core',{'NativeWeave.Interop.Abi':version}),
 'NativeWeave.Server.Generator':('src/Server.Application/Generator',{}),
 'NativeWeave.Server':('src/Server.Application',{'NativeWeave.Core':version,'NativeWeave.Server.Generator':version,**external})}
closure=[]
for identity,(_,dependencies) in registry.items():
 entry=dict(id=identity,version=version,dependencies={name:value for name,value in dependencies.items() if name.startswith('NativeWeave.')})
 if identity=='NativeWeave.Server':entry['nativeAssets']=[dict(engineId='server',facadePackageId=identity,facadePackageVersion=version,rid='linux-x64',sourcePath='runtimes/linux-x64/native/libnativeweave_server.so',sha256=native_hash,abiMajor=1,abiMinor=0,capabilities={name:bit for bit,name in enumerate(['http1','http2','tls','trailers','grpc','mcp','sse'])},cpuBaseline='x86-64')]
 closure.append(entry)
feed=O/'feed';packages={}
for identity,(folder,dependencies) in registry.items():
 directory=R/folder;entries={'LICENSE':R/'LICENSE'}
 if identity.endswith('.Generator'):
  entries['tools/roslyn/'+identity+'.dll']=directory/'bin/Release/netstandard2.0'/(identity+'.dll');entries['buildTransitive/'+identity+'.targets']=directory/'buildTransitive'/(identity+'.targets')
 else:
  for suffix in ['.dll','.xml']:entries['lib/net11.0/'+identity+suffix]=directory/'bin/Release/net11.0'/(identity+suffix)
 if identity=='NativeWeave.Server':
  entries['runtimes/linux-x64/native/libnativeweave_server.so']=native
  entries['buildTransitive/'+identity+'.targets']=R/'src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets'
  entries['nativeweave/closure.json']=json.dumps(dict(schemaVersion=1,packages=closure),sort_keys=True).encode()
  for suffix in ['.dll','.xml']:entries['tools/net11.0/NativeWeave.Packaging.Tasks'+suffix]=task.parent/'bin/Release/net11.0'/('NativeWeave.Packaging.Tasks'+suffix)
 file=packer.pack(feed,identity,version,entries,dependencies,('https://github.com/IzuSharp/NativeWeave',source));packages[identity]=dict(path=str(file),sha256=sha(file))
C=O/'consumer';C.mkdir();shutil.copy2(R/'samples/api/server-diagnostics/Program.cs',C/'Program.cs');shutil.copy2(R/'global.json',C/'global.json')
# Stop parent repo imports: this is a standalone package consumer, not a source project.
(C/'Directory.Build.props').write_text('<Project />\n');(C/'Directory.Build.targets').write_text('<Project />\n')
(C/'Consumer.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsAotCompatible>true</IsAotCompatible><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">$(MSBuildProjectDirectory)/packages.aot.lock.json</NuGetLockFilePath></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Server" Version="[0.1.0-preview.1]" /></ItemGroup></Project>\n''')
config=ET.Element('configuration');sources=ET.SubElement(config,'packageSources');ET.SubElement(sources,'clear');ET.SubElement(sources,'add',key='candidate',value=str(feed));ET.SubElement(sources,'add',key='nuget.org',value='https://api.nuget.org/v3/index.json')
mapping=ET.SubElement(config,'packageSourceMapping');ET.SubElement(mapping,'clear');ET.SubElement(ET.SubElement(mapping,'packageSource',key='candidate'),'package',pattern='NativeWeave.*');ET.SubElement(ET.SubElement(mapping,'packageSource',key='nuget.org'),'package',pattern='*');ET.ElementTree(config).write(C/'NuGet.Config')
env=os.environ.copy();env['NUGET_PACKAGES']=str(C/'packages');env['PATH']=os.pathsep.join(entry for entry in env['PATH'].split(os.pathsep) if not any((pathlib.Path(entry)/name).exists() for name in ['cargo','rustc','rustup']))
assert all(shutil.which(name,path=env['PATH']) is None for name in ['cargo','rustc','rustup'])
run('consumer-build',['dotnet','build','Consumer.csproj','-c','Release',*flags],C,env)
dll=C/'bin/Release/net11.0/Consumer.dll'
for mode in ['disabled','local']:run('consumer-jit-'+mode,['dotnet',dll,*(['--local'] if mode=='local' else [])],C,env)
run('consumer-aot-publish',['dotnet','publish','Consumer.csproj','-c','Release','-r','linux-x64','-p:PublishAot=true','-o',C/'publish',*flags],C,env)
exe=C/'publish/Consumer'
for mode in ['disabled','local']:run('consumer-aot-'+mode,[exe,*(['--local'] if mode=='local' else [])],C,env)
outputs={}
for name,folder in [('jit',dll.parent),('aot',exe.parent)]:
 manifest=json.loads((folder/'NativeWeave.server.assets.json').read_text());assert manifest['sha256']==native_hash==sha(folder/manifest['outputPath']);outputs[name]=manifest
(O/'summary.json').write_text(json.dumps(dict(task='B20',platform='linux-x64',source_commit=source,native_sha256=native_hash,packages=packages,no_consumer_rust=True,no_parent_repository_imports=True,jit_disabled_local=True,aot_disabled_local=True,aot_executable_sha256=sha(exe),sdk_output_records=outputs),indent=2)+'\n')
print('PASS B20 independent public NuGet diagnostics consumer in JIT/NativeAOT, no Rust tools available.',flush=True)
