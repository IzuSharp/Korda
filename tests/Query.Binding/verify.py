#!/usr/bin/env python3
"""Linux x64 B09 component verification; every run gets an immutable evidence directory."""
import argparse,datetime,hashlib,json,os,platform,shutil,subprocess,sys,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--design',type=Path,required=True)
parser.add_argument('--nuget-config',type=Path)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
if sys.platform!='linux' or platform.machine()!='x86_64':raise SystemExit('B09 verifier supports this authorized Linux x64 row only.')
out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
env=os.environ.copy();env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDUSESERVER='0')
commands=[]
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def write(name,value):(out/name).write_text(json.dumps(value,indent=2)+'\n')
def run(name,argv):
    number=len(commands)+1;log=out/f'{number:02d}-{name}.log';start=time.monotonic();print(f'RUN {number:02d} {name}',flush=True)
    with log.open('w') as stream:
        stream.write('$ '+json.dumps([str(a) for a in argv])+'\n');stream.flush()
        result=subprocess.run([str(a) for a in argv],cwd=ROOT,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=600)
    record={'name':name,'argv':[str(a) for a in argv],'exit_code':result.returncode,'seconds':round(time.monotonic()-start,3),'log':log.name};commands.append(record);write('commands.json',commands)
    if result.returncode:raise RuntimeError(f'{name} exited {result.returncode}; retained {log}')
    print(f'PASS {number:02d} {name}',flush=True)
scopes=['native/database/binding','src/Query.Binding','tests/Query.Binding','samples/api/query-binding','docs/guides/query-binding.md','native/database/interfaces','src/Data.Abstractions','native/interop/abi','src/Interop.Abi','Cargo.toml','Cargo.lock','Directory.Build.props','Directory.Build.targets','global.json','rust-toolchain.toml','NativeWeave.slnx']
files=subprocess.check_output(['git','ls-files','--',*scopes],cwd=ROOT,text=True).splitlines()
before={name:sha(ROOT/name) for name in files}
source={'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'tree':subprocess.check_output(['git','rev-parse','HEAD^{tree}'],cwd=ROOT,text=True).strip(),'dirty':subprocess.check_output(['git','status','--porcelain','--',*scopes],cwd=ROOT,text=True).strip(),'inputs':before}
write('source-inputs.json',source)
try:
    if source['dirty']:raise RuntimeError('Formal B09 evidence requires committed source inputs; dirty status is retained.')
    design_commit=subprocess.check_output(['git','-C',str(args.design),'rev-parse','HEAD'],text=True).strip()
    if design_commit!='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9':raise RuntimeError('Design worktree is not the exact pinned commit.')
    write('environment.json',{'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'platform':platform.platform(),'machine':platform.machine(),'design_commit':design_commit,'tools':{tool:shutil.which(tool) for tool in ['dotnet','cargo','rustc','gcc','ld']},'nuget_config_sha256':sha(args.nuget_config) if args.nuget_config else None})
    run('source-diff-check',['git','diff','--check'])
    run('pinned-signatures',['python3','tests/Query.Binding/generate.py','--check','--design',args.design])
    run('native-format',['cargo','fmt','-p','nativeweave-query-binding','--','--check'])
    manifest='tests/Query.Binding/native/Cargo.toml'
    run('fixture-format',['cargo','fmt','--manifest-path',manifest,'--','--check'])
    run('native-clippy',['cargo','clippy','-p','nativeweave-query-binding','--all-targets','--locked','--offline','--','-D','warnings'])
    run('fixture-clippy',['cargo','clippy','--manifest-path',manifest,'--all-targets','--locked','--offline','--','-D','warnings'])
    run('native-state-properties',['cargo','test','--manifest-path',manifest,'--release','--locked','--offline','--','--nocapture'])
    run('native-fixture',['cargo','build','--manifest-path',manifest,'--release','--locked','--offline'])
    run('c-header-signatures',['gcc','-std=c11','-Wall','-Wextra','-Werror','-fsyntax-only','tests/Query.Binding/abi-probe.c'])
    run('managed-format',['dotnet','format','whitespace','.','--folder','--include','src/Query.Binding','tests/Query.Binding','samples/api/query-binding','--verify-no-changes','--verbosity','minimal'])
    common=['-p:UseSharedCompilation=false','-nr:false','-m:1']
    config=['--configfile',args.nuget_config] if args.nuget_config else []
    tests='tests/Query.Binding/NativeWeave.Query.Binding.Tests.csproj';sample='samples/api/query-binding/QueryBindingSample.csproj';api='tests/Query.Binding/ApiCheck/ApiCheck.csproj'
    run('restore-tests-jit',['dotnet','restore',tests,'--locked-mode',*config,*common])
    run('build-tests-jit',['dotnet','build',tests,'-c','Release','--no-restore',*common])
    test_dll=ROOT/'tests/Query.Binding/bin/Release/net11.0/NativeWeave.Query.Binding.Tests.dll'
    run('managed-golden',['dotnet',test_dll,'--emit',out/'golden'])
    golden={path.name:sha(path) for path in (ROOT/'tests/Query.Binding/golden').iterdir() if path.is_file()}
    actual={path.name:sha(path) for path in (out/'golden').iterdir() if path.is_file()}
    write('golden-comparison.json',{'expected':golden,'actual':actual,'equal':golden==actual})
    if golden!=actual:raise RuntimeError('Cross-language golden inputs drifted.')
    run('restore-api-check',['dotnet','restore',api,'--locked-mode',*config,*common])
    run('build-api-check',['dotnet','build',api,'-c','Release','--no-restore',*common])
    run('compiled-api-xml-reference',['dotnet','tests/Query.Binding/ApiCheck/bin/Release/net11.0/ApiCheck.dll','src/Query.Binding/bin/Release/net11.0/NativeWeave.Query.Binding.dll','src/Query.Binding/bin/Release/net11.0/NativeWeave.Query.Binding.xml','native/database/binding/signatures-v1.json','src/Query.Binding/api-reference.md'])
    asset=ROOT/'tests/Query.Binding/native/target/release/libnativeweave_query_binding_fixture.so'
    run('actual-jit',['dotnet',test_dll,asset,out/'jit'])
    run('restore-sample-jit',['dotnet','restore',sample,'--locked-mode',*config,*common])
    run('build-sample-jit',['dotnet','build',sample,'-c','Release','--no-restore',*common])
    run('actual-sample-jit',['dotnet','samples/api/query-binding/bin/Release/net11.0/NativeWeave.Query.Binding.Sample.dll',asset])
    run('restore-tests-aot',['dotnet','restore',tests,'-r','linux-x64','-p:PublishAot=true','--locked-mode',*config,*common])
    run('publish-tests-aot',['dotnet','publish',tests,'-c','Release','-r','linux-x64','-p:PublishAot=true','--no-restore','-o',out/'aot',*common])
    run('actual-aot',[out/'aot/NativeWeave.Query.Binding.Tests',asset,out/'aot-results','--require-aot'])
    run('restore-sample-aot',['dotnet','restore',sample,'-r','linux-x64','-p:PublishAot=true','--locked-mode',*config,*common])
    run('publish-sample-aot',['dotnet','publish',sample,'-c','Release','-r','linux-x64','-p:PublishAot=true','--no-restore','-o',out/'sample-aot',*common])
    run('actual-sample-aot',[out/'sample-aot/NativeWeave.Query.Binding.Sample',asset,'--require-aot'])
    run('native-features',['cargo','tree','--manifest-path',manifest,'--locked','--offline','-e','features'])
    run('dotnet-identity',['dotnet','--info'])
    run('rustc-identity',['rustc','-vV'])
    run('cc-identity',['gcc','--version'])
    run('linker-identity',['ld','--version'])
    after={name:sha(ROOT/name) for name in files};write('source-inputs-after.json',{'inputs':after,'unchanged':before==after})
    if before!=after:raise RuntimeError('Source inputs changed during acceptance.')
    artifacts=[asset,test_dll,ROOT/'src/Query.Binding/bin/Release/net11.0/NativeWeave.Query.Binding.dll',ROOT/'src/Query.Binding/bin/Release/net11.0/NativeWeave.Query.Binding.xml',out/'aot/NativeWeave.Query.Binding.Tests',out/'sample-aot/NativeWeave.Query.Binding.Sample']
    links=[]
    for base in [ROOT/'tests/Query.Binding/obj',ROOT/'samples/api/query-binding/obj']:
        for path in base.rglob('*.rsp'):
            if 'native' in path.parts or 'ilc' in path.name:links.append({'path':str(path.relative_to(ROOT)),'sha256':sha(path),'bytes':path.stat().st_size})
    write('artifact-identities.json',{'sha256':{str(path.relative_to(ROOT)) if path.is_relative_to(ROOT) else str(path):sha(path) for path in artifacts},'native_aot_response_files':links,'independently_distributed_package':None,'pristine_machine':False})
    write('status.json',{'result':'passed','platform':'linux-x64','candidate':source['commit'],'commands':len(commands),'source_inputs':len(before),'source_inputs_unchanged':True,'jit':json.loads((out/'jit/result.json').read_text()),'aot':json.loads((out/'aot-results/result.json').read_text()),'public_symbols':0,'exact_cdecl_signatures':42,'scope':'B09 native validators and managed direct bindings via explicit test-only fixture; no B04/B11 host claim'})
    print('PASS B09 Linux component verification',flush=True)
except Exception as failure:
    write('status.json',{'result':'failed','candidate':source['commit'],'completed_commands':len(commands),'error':str(failure),'platform':'linux-x64'})
    raise
