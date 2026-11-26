from pathlib import Path
from datetime import datetime,timezone
import importlib.util,sys,os,json,hashlib,subprocess
ROOT=Path('/workspace/scratch/37c4f8cc3a91');WT=ROOT/'worktrees/Korda-DB-Perf-01-empty-parameters';WORK=ROOT/'recovery-20261125/empty-parameters-v2'
phase=sys.argv[1];assert phase in ('first-baseline','interleaved-rest')
assert 'DOTNET_PROCESSOR_COUNT' not in os.environ
for key in os.environ:
 if key.startswith(('DOTNET_ThreadPool','COMPlus_ThreadPool')):raise RuntimeError('Runtime pool overrides are not part of the probe')
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
spec=importlib.util.spec_from_file_location('dbperf_observer',WT/'benchmarks/db-perf/run.py');observer=importlib.util.module_from_spec(spec);spec.loader.exec_module(observer)
folder=WORK/phase;folder.mkdir(exist_ok=False)
sequence=[(0,'baseline')] if phase=='first-baseline' else [(1,'candidate'),(2,'baseline'),(3,'candidate'),(4,'baseline'),(5,'candidate')]
plan={'schemaVersion':1,'provider':'postgresql','postgres':{'bin':str(ROOT/'tooling/postgres/install/bin'),'data':str(WORK/'postgres-data'),'maxConnections':10,'maxClientConcurrency':1}}
commands=[]
for index,variant in sequence:
 result=WORK/variant/'results'/f'process-{index}.json';elf=WORK/variant/'publish/Korda.Query.Fluent.Tests'
 command={'name':f'process-{index}-{variant}','phase':'isolated-dynamic-empty-parameters','timeoutSeconds':120,'argv':[str(elf),str(result),str(WORK/variant/'QueryContext.cs'),'456140129c3ef3ff1ea0412b2b8cb3da82d099fd','{connection}',str(WORK/f'sqlite-process-{index}.db')]}
 commands.append(command)
plan['commands']=commands;observer.validate_plan(plan,folder);observer.write_json(folder/'plan.json',plan)
receipt={'schema':1,'phase':phase,'startedUtc':datetime.now(timezone.utc).isoformat(),'observerSha256':sha(WT/'benchmarks/db-perf/run.py'),'controllerSha256':sha(__file__),'warmup':1024,'operations':16384,'sequence':sequence,'runtimeOverrides':None,'results':[]};observer.write_json(folder/'receipt.json',receipt)
with observer.database(plan,folder) as (connection,server_pid):
 for (index,variant),command in zip(sequence,commands):
  elf=Path(command['argv'][0]);build=json.loads((WORK/variant/'build-receipt.json').read_text());assert sha(elf)==build['executableSha256'];assert sha(WORK/variant/'QueryContext.cs')==build['sourceSha256']
  row={'index':index,'variant':variant,'executableSha256':sha(elf),'startedUtc':datetime.now(timezone.utc).isoformat()};print(json.dumps({'phase':'run','index':index,'variant':variant}),flush=True)
  observed=observer.execute(command,connection,server_pid,folder,0.1,check=True)
  result=Path(command['argv'][1]);raw=json.loads(result.read_text());assert raw['passed'] and raw['nativeAot'];assert raw['executableSha256']==build['executableSha256'];assert raw['queryContextSha256']==build['sourceSha256'];assert len(raw['observations'])==10
  row.update(result=str(result),resultSha256=sha(result),finishedUtc=datetime.now(timezone.utc).isoformat(),success=observed['success']);receipt['results'].append(row);observer.write_json(folder/'receipt.json',receipt)
  print(json.dumps({'completed':index,'variant':variant,'warmEmpty':[(x['provider'],x['allocatedDelta'],x['bytesPerOperation']) for x in raw['observations'] if x['name']=='dynamic-null-warm']}),flush=True)
receipt.update(finishedUtc=datetime.now(timezone.utc).isoformat(),status='completed');observer.write_json(folder/'receipt.json',receipt)
