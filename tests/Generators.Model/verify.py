#!/usr/bin/env python3
"""A09 Linux component evidence: actual parser, omission gate, JIT and NativeAOT."""
import argparse, datetime, hashlib, json, os, re, subprocess, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path);parser.add_argument('--offline-config',type=Path);parser.add_argument('--prepare',action='store_true');parser.add_argument('--design-root',type=Path)
    args=parser.parse_args();out=(args.output or ROOT/'artifacts/A09'/datetime.datetime.now(datetime.timezone.utc).strftime('run-%Y%m%dT%H%M%SZ')).resolve();out.mkdir(parents=True,exist_ok=False)
    env=os.environ.copy();env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDDISABLENODEREUSE='1')
    commands=[];passed=False
    scopes=['src/Generators.Model','tests/Generators.Model','samples/api/generators-model','docs/guides/generators-model.md','native/interop/abi','src/Interop.Abi','src/NativeWeave.Core','src/Interop.Negotiation','src/Interop.Errors']
    tracked=subprocess.check_output(['git','ls-files','--',*scopes],cwd=ROOT,text=True).splitlines()
    tracked += ['Cargo.toml','Cargo.lock','global.json','Directory.Build.props','rust-toolchain.toml']
    sources={p:sha(ROOT/p) for p in sorted(set(tracked)) if (ROOT/p).is_file()}
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    dirty=bool(subprocess.check_output(['git','status','--porcelain'],cwd=ROOT,text=True).strip())
    def run(label,cmd):
        start=time.monotonic();stdout=out/(label+'.stdout');stderr=out/(label+'.stderr')
        with stdout.open('w') as o,stderr.open('w') as e: result=subprocess.run(list(map(str,cmd)),cwd=ROOT,env=env,stdout=o,stderr=e)
        commands.append({'label':label,'command':list(map(str,cmd)),'returncode':result.returncode,'seconds':round(time.monotonic()-start,3),'stdout_sha256':sha(stdout),'stderr_sha256':sha(stderr)})
        (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n');print(label,result.returncode,flush=True)
        if result.returncode:raise RuntimeError(label+' failed; see '+str(stderr))
        return stdout.read_text()
    try:
        if args.design_root:
            design=args.design_root.resolve();inputs=json.loads((ROOT/'src/Generators.Model/design-inputs.json').read_text())
            design_head=run('design-commit',['git','-C',design,'rev-parse','HEAD']).strip();assert design_head==inputs['design_commit']
            for path,digest in inputs['api_design_input_sha256'].items():assert sha(design/path.split(':',1)[1])==digest,path
            assert sha(design/'docs/generators/binding-pipeline.md')==inputs['binding_contract_sha256']
        for label,cmd in [('dotnet' ,['dotnet','--version']),('rustc',['rustc','-Vv']),('cargo',['cargo','--version']),('cc',['cc','--version'])]:run(label,cmd)
        cs_sources=[p for p in tracked if p.endswith('.cs') and p.startswith(('src/Generators.Model/','tests/Generators.Model/','samples/api/generators-model/'))]
        if cs_sources:run('managed-whitespace',['dotnet','format','whitespace','.', '--folder','--verify-no-changes','--include',*cs_sources,'--verbosity','quiet'])
        for name,path in [('inventory' ,'src/Generators.Model/native-inventory/Cargo.toml'),('raw-extractor','tests/Generators.Model/raw-extractor/Cargo.toml')]:
            run(name+'-format',['cargo','fmt','--manifest-path',path,'--','--check'])
            run(name+'-clippy',['cargo','clippy','--manifest-path',path,'--locked','--offline','--all-targets','--','-D','warnings'])
            run(name+'-build',['cargo','build','--manifest-path',path,'--locked','--offline'])
        run('native-abi-dependency',['cargo','build','--locked','--offline','-p','nativeweave-interop-abi'])
        fixture=out/'libnativeweave_model_fixture.so'
        run('native-fixture',['rustc','--edition','2024','--crate-name','nativeweave_model_fixture','--crate-type','cdylib','tests/Generators.Model/native/fixture.rs','--extern','nativeweave_interop_abi=target/debug/libnativeweave_interop_abi.rlib','-L','dependency=target/debug/deps','-o',fixture])
        linked=run('native-linked-symbols',['nm','-D','--defined-only',fixture]);symbols=[line.split()[-1] for line in linked.splitlines() if line.strip()];(out/'linked.txt').write_text('\n'.join(sorted(symbols))+'\n')
        inventory=ROOT/'src/Generators.Model/native-inventory/target/debug/nativeweave-binding-inventory'
        raw=ROOT/'tests/Generators.Model/raw-extractor/target/debug/nativeweave-model-extraction-probe'
        for label,path in [('valid','tests/Generators.Model/native/fixture.rs'),('unsupported','tests/Generators.Model/fixtures/unsupported.rs')]:
            run(label+'-parsed',[inventory,path,out/(label+'-inventory.json')]);run(label+'-raw',[raw,path,out/(label+'.g.cs')])
        raw_text=(out/'valid.g.cs').read_text();raw_symbols=sorted(set(re.findall(r'\b(nw_model_[A-Za-z0-9_]+)\s*\(',raw_text)))
        assert raw_symbols==sorted(symbols),(raw_symbols,symbols)
        assert 'unsupported_tuple' not in (out/'unsupported.g.cs').read_text()
        assert 'ignore generate' in (out/'unsupported-raw.stdout').read_text()
        model_path=ROOT/'tests/Generators.Model/fixtures/model.json';sidecar=json.loads(model_path.read_text())
        assert sidecar['provenance']['adapter_sha256']==sha(ROOT/'tests/Generators.Model/native/fixture.rs')
        canonical_abi=json.loads((ROOT/'native/interop/abi/abi-v1.json').read_text());abi=canonical_abi['records'][0]
        assert [(x['name'],x['type']) for x in sidecar['records'][0]['fields']]==[tuple(x) for x in abi['fields']]
        mapping=json.loads((ROOT/'src/Generators.Model/api-bindings.json').read_text());assert sorted(x['id'] for x in mapping['symbols'])==sorted(sidecar['managed_symbols'])
        common=['-p:UseSharedCompilation=false','-nr:false','-m:1'];restore=['--locked-mode'] if not args.prepare else []
        if args.offline_config:restore+=['--configfile',args.offline_config.resolve(),'-p:NuGetAudit=false']
        projects=[('tests','tests/Generators.Model/NativeWeave.Generators.Model.Tests.csproj','NativeWeave.Generators.Model.Tests'),('sample','samples/api/generators-model/Generators.Model.Sample.csproj','NativeWeave.Generators.Model.Sample'),('api','tests/Generators.Model/ApiCheck/ApiCheck.csproj','NativeWeave.Generators.Model.ApiCheck')]
        for name,project,_ in projects:
            run(name+'-restore',['dotnet','restore',project,*restore,*common]);run(name+'-build',['dotnet','build',project,'--no-restore','-c','Release',*common])
        api=ROOT/'tests/Generators.Model/ApiCheck/bin/Release/net11.0/NativeWeave.Generators.Model.ApiCheck.dll';assembly=ROOT/'src/Generators.Model/bin/Release/net11.0/NativeWeave.Generators.Model.dll'
        run('compiled-api-xml',['dotnet',api,assembly,assembly.with_suffix('.xml'),ROOT/'src/Generators.Model/api-reference.md',*(['--write'] if args.prepare else [])])
        test=ROOT/'tests/Generators.Model/bin/Release/net11.0/NativeWeave.Generators.Model.Tests.dll';sample=ROOT/'samples/api/generators-model/bin/Release/net11.0/NativeWeave.Generators.Model.Sample.dll'
        test_args=[model_path,out/'valid-inventory.json',out/'linked.txt',fixture,'--omission',out/'unsupported-inventory.json']
        run('jit-native-and-negatives',['dotnet',test,*test_args]);run('sample-jit',['dotnet',sample,model_path,sha(fixture)])
        for i in [1,2]:run('determinism-'+str(i),['dotnet',test,'--emit',model_path,out/f'canonical-{i}.json'])
        assert (out/'canonical-1.json').read_bytes()==(out/'canonical-2.json').read_bytes()
        reordered=dict(reversed(list(sidecar.items())));reordered['exports']=list(reversed(sidecar['exports']));reordered['managed_symbols']=list(reversed(sidecar['managed_symbols']));(out/'reordered.json').write_text(json.dumps(reordered))
        run('determinism-order',['dotnet',test,'--emit',out/'reordered.json',out/'canonical-reordered.json']);assert (out/'canonical-1.json').read_bytes()==(out/'canonical-reordered.json').read_bytes()
        binary_before=sha(fixture)
        for name,project,executable in projects[:2]:
            destination=out/(name+'-aot');run(name+'-aot-restore',['dotnet','restore',project,'-r','linux-x64','-p:PublishAot=true',*restore,*common])
            run(name+'-aot-publish',['dotnet','publish',project,'--no-restore','-c','Release','-r','linux-x64','-p:PublishAot=true','-o',destination,*common])
            run(name+'-aot-execute',[destination/executable,*([*test_args,'--require-aot'] if name=='tests' else [model_path,sha(fixture)])])
        assert sha(fixture)==binary_before
        artifacts=[fixture,inventory,raw,assembly,test,sample,out/'tests-aot/NativeWeave.Generators.Model.Tests',out/'sample-aot/NativeWeave.Generators.Model.Sample']
        (out/'artifacts.json').write_text(json.dumps({str(p):sha(p) for p in artifacts},indent=2)+'\n')
        (out/'determinism.json').write_text(json.dumps({'independent_processes':3,'canonical_sha256':sha(out/'canonical-1.json'),'reordered_equal':True,'native_asset_unchanged_during_managed_consumption':True,'managed_symbol_count':len(mapping['symbols']),'raw_extractor_actual_omission_rejected':True},indent=2)+'\n')
        stable=all((ROOT/p).is_file() and sha(ROOT/p)==digest for p,digest in sources.items())
        passed=stable and not dirty and not args.prepare
        if not args.prepare and not passed:raise RuntimeError('Formal evidence requires a clean source checkpoint and unchanged input bytes.')
    finally:
        result={'task':'A09','platform':'linux-x64','passed':passed,'preparation_only':args.prepare,'source_commit':head,'source_dirty':dirty,'source_sha256':sources,'source_stable':all((ROOT/p).is_file() and sha(ROOT/p)==digest for p,digest in sources.items()),'commands':commands,'scope':'internal maintainer model and actual native JIT/AOT fixture','independent_review':'pending','limitations':['Linux x64 only','No A10/A11 generator or pristine distributed package claim','Local offline audit refresh disabled only when the explicit offline config is supplied','Local full solution formatting is deferred to unchanged CI gate']}
        (out/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    return 0
if __name__=='__main__':sys.exit(main())
