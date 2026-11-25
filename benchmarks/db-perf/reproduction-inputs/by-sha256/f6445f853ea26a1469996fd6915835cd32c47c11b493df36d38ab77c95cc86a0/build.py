from pathlib import Path
from datetime import datetime,timezone
import subprocess,os,json,hashlib,sys
ROOT=Path('/workspace/scratch/37c4f8cc3a91'); WT=ROOT/'worktrees/Korda-DB-Perf-01-empty-parameters'; WORK=ROOT/'recovery-20261125/empty-parameters-v2'
variant=sys.argv[1]; assert variant in ('baseline','candidate'); target=WORK/variant
project=WT/'benchmarks/db-perf/empty-parameters/EmptyParameters.csproj'
source=WT/'src/Query.Fluent/QueryContext.cs'; expected=(target/'QueryContext.cs').read_bytes(); assert source.read_bytes()==expected
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
env=os.environ.copy();env.update(DOTNET_PROCESSOR_COUNT='2',NUGET_PACKAGES=str(WORK/'packages'),DOTNET_CLI_HOME=str(WORK/'dotnet-cli-home'),TMPDIR=str(WORK/'tmp'),NUGET_HTTP_CACHE_PATH=str(WORK/'http-cache'),DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDDISABLENODEREUSE='1',UseSharedCompilation='false')
for key in ('NUGET_PACKAGES','DOTNET_CLI_HOME','TMPDIR','NUGET_HTTP_CACHE_PATH'):Path(env[key]).mkdir(exist_ok=True)
dotnet=ROOT/'tooling/dotnet/dotnet'
common=[f'-p:EmptyParameterInputs={WORK}/inputs','-p:PublishAot=true','-p:UseSharedCompilation=false','-m:1','-nr:false']
commands=[]
if variant=='baseline':commands.append([str(dotnet),'restore',str(project),'-r','linux-x64','--configfile',str(WORK/'NuGet.Config'),'--disable-parallel',*common])
commands.append([str(dotnet),'publish',str(project),'-c','Release','-r','linux-x64','--no-restore',*common,'-o',str(target/'publish')])
record={'schema':1,'variant':variant,'createdUtc':datetime.now(timezone.utc).isoformat(),'sourceSha256':sha(source),'probeSha256':sha(project.parent/'Program.cs'),'projectSha256':sha(project),'commands':commands,'cwd':str(WT),'environmentOverrides':{k:env[k] for k in ('DOTNET_PROCESSOR_COUNT','NUGET_PACKAGES','DOTNET_CLI_HOME','TMPDIR','NUGET_HTTP_CACHE_PATH','DOTNET_CLI_USE_MSBUILD_SERVER','MSBUILDDISABLENODEREUSE','UseSharedCompilation')},'scope':'Build-only processor count 2; no runtime spin or processor overrides in measured ELF processes.'}
(target/'build-commands.json').write_text(json.dumps(record,indent=2)+'\n')
results=[]
for index,command in enumerate(commands):
 row={'argv':command,'startedUtc':datetime.now(timezone.utc).isoformat()}; print(json.dumps({'phase':'build','variant':variant,'index':index}),flush=True)
 with (target/f'build-{index}.log').open('xb') as log:
  try:completed=subprocess.run(command,cwd=WT,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=300);row.update(exitCode=completed.returncode)
  except subprocess.TimeoutExpired:row.update(exitCode=None,status='timeout');results.append(row);(target/'build-execution.json').write_text(json.dumps(results,indent=2)+'\n');raise
 row.update(finishedUtc=datetime.now(timezone.utc).isoformat(),logSha256=sha(target/f'build-{index}.log'));results.append(row);(target/'build-execution.json').write_text(json.dumps(results,indent=2)+'\n')
 if row['exitCode']: print((target/f'build-{index}.log').read_text()[-16000:]);raise SystemExit(row['exitCode'])
assert source.read_bytes()==expected
elf=target/'publish/Korda.Query.Fluent.Tests'
record.update(finishedUtc=datetime.now(timezone.utc).isoformat(),executableSha256=sha(elf),files={str(p.relative_to(target/'publish')):sha(p) for p in sorted((target/'publish').rglob('*')) if p.is_file()})
(target/'build-receipt.json').write_text(json.dumps(record,indent=2)+'\n');print(json.dumps({'status':'built','variant':variant,'executableSha256':record['executableSha256']}))
