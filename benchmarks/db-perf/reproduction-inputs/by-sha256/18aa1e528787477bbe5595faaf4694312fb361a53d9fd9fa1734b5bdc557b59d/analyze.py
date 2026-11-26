"""Read-only verification of the completed six-process Native AOT microprobe."""
from pathlib import Path
from collections import defaultdict
from datetime import datetime, timezone
import hashlib, json, statistics, struct, sys
WORK=Path(__file__).resolve().parent
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
require=lambda value,message: None if value else (_ for _ in ()).throw(AssertionError(message))
expected_native={'sqlite':'970a8868290cec37c353bbd8e48bb7bf4ef18cfa1da9e495fc06d7eb08d71d40','postgresql':'73f725104fb1bee5fc86a9bc371e556ea8358ac3a2d774c4823185e6da8984c6'}
expected_source={'baseline':'9684c9baf344e35068469a30beadfee6d032e42779cc6c8f9bc37ff6a2e2c39b','candidate':'b77baea49554226f7f31c9564c0bcc4dab19817823d58ce3554dd1a5ff5aa3de'}
expected_elf={'baseline':'71a5d97a03a00bf465583b785c34d4eba89a51d640487430b3c3cd03e36d2fa7','candidate':'1a4b8284a8b62aa5d6a6851fd8e55383dc0779f29a211cc75949c5fc0e57a30e'}
expected_order=[(0,'baseline'),(1,'candidate'),(2,'baseline'),(3,'candidate'),(4,'baseline'),(5,'candidate')]
frames={};exception_reference=None;all_observations=defaultdict(list);rows=[];identities=set();builds={}
for variant in ['baseline','candidate']:
 build=json.loads((WORK/variant/'build-receipt.json').read_text());builds[variant]=build
 require(build['sourceSha256']==expected_source[variant] and sha(WORK/variant/'QueryContext.cs')==expected_source[variant],'Source mismatch')
 require(build['executableSha256']==expected_elf[variant] and sha(WORK/variant/'publish/Korda.Query.Fluent.Tests')==expected_elf[variant],'ELF mismatch')
 for relative,digest in build['files'].items():require(sha(WORK/variant/'publish'/relative)==digest,'Published file changed: '+relative)
require(builds['baseline']['probeSha256']==builds['candidate']['probeSha256'],'Probe differs between baseline and candidate')
require(builds['baseline']['projectSha256']==builds['candidate']['projectSha256'],'Project differs between baseline and candidate')
require(any('-t:Rebuild,Publish' in command for command in builds['candidate']['commands']),'Final candidate was not forcibly rebuilt')
for index,variant in expected_order:
 path=WORK/variant/'results'/f'process-{index}.json';raw=json.loads(path.read_text());build=builds[variant]
 require(raw['passed'] and raw['nativeAot'] and raw['businessResultAllocatedBytesWithinSeam']==0,'Result status/scope')
 require(raw['sourceBaseCommit']=='456140129c3ef3ff1ea0412b2b8cb3da82d099fd','Wrong source base')
 require(raw['queryContextSha256']==expected_source[variant] and raw['executableSha256']==expected_elf[variant],'Wrong measured source/ELF')
 identity=(raw['processId'],raw['startedUtc']);require(identity not in identities,'Duplicate process identity');identities.add(identity)
 require(len(raw['observations'])==10 and len(raw['exceptions'])==10,'Missing case or exception')
 if exception_reference is None:exception_reference=raw['exceptions']
 require(raw['exceptions']==exception_reference,'Exception type, message or parameter precedence changed')
 require(raw['nativeImages']==expected_native,'Native SHA mismatch')
 for provider in expected_native:
  live=raw['nativeLiveCounts'][provider];require(live==[1,1,0,0,0,2],'Actual source/session not observed live')
  native=WORK/variant/'publish'/f'libnw_database_{provider}.so';require(sha(native)==expected_native[provider],'Loaded native file changed')
  mapped=raw['nativeLoadedMappings'][provider];require(mapped and all(line.endswith(' '+str(native)) for line in mapped),'Diagnostic and engine mapping differ')
  require(provider+'/all-six-native-owner-counters-zero=0,0,0,0,0,0' in raw['checks'],'Native owners not drained')
  reference_expected=str(variant=='candidate')
  require(provider+'/same-frame-reference-across-contexts='+reference_expected in raw['checks'],'Wrong sharing semantics')
  for check in ['distinct-context-concurrent-full-byte-validation','real-dynamic-driver-result-and-delayed-stream-captures']:require(provider+'/'+check in raw['checks'],'Missing correctness: '+check)
 for observation in raw['observations']:
  provider=observation['provider'];profile=1 if provider=='sqlite' else 2;name=observation['name'];require(observation['semanticProfile']==profile,'Wrong profile')
  require(observation['allocatedBefore']>=0 and observation['allocatedAfter']>=observation['allocatedBefore'],'Allocation endpoints')
  require(observation['allocatedDelta']==observation['allocatedAfter']-observation['allocatedBefore'],'Allocation arithmetic')
  require(observation['bytesPerOperation']==observation['allocatedDelta']/observation['operations'],'B/op arithmetic')
  require(observation['rows']==0 and observation['endTicks']>=observation['startTicks'],'Scope/time endpoints')
  frame=bytes.fromhex(observation['fullFrameHex']);require(len(frame)==observation['frameBytes'],'Frame length')
  require(observation['checksum']==sum(frame)*observation['operations'],'Checksum/operations mismatch')
  for generation in range(3):require(observation[f'gc{generation}After']>=observation[f'gc{generation}Before'],'GC endpoints')
  if name=='dynamic-null-cold-first-access':require(observation['operations']==1 and observation['warmup']==0,'Cold semantics')
  else:require(observation['operations']==16384 and observation['warmup']==1024,'Frozen loop changed')
  if name!='dynamic-one-parameter-unchanged':
   golden=bytearray(96);golden[:4]=b'NWDB';struct.pack_into('<HHI',golden,4,1,1,96);struct.pack_into('<Q',golden,16,96)
   golden[24:56]=hashlib.sha256(b'{"version":1,"parameters":[]}').digest();struct.pack_into('<I',golden,56,1);struct.pack_into('<I',golden,76,profile);struct.pack_into('<QQ',golden,80,96,96)
   require(frame==golden,'Full 96-byte empty frame mismatch')
  key=(provider,name)
  if key not in frames:frames[key]=frame
  require(frames[key]==frame,'Encoded bytes changed across processes/variants')
  if name in ('prepared-empty-unchanged',) or (variant=='candidate' and name=='dynamic-null-warm'):
   require(observation['allocatedDelta']==0,'Measured warmed seam did not remain zero')
   require(all(observation[f'gc{generation}Before']==observation[f'gc{generation}After'] for generation in range(3)),'GC changed in measured zero seam')
  all_observations[(provider,variant,name)].append(observation)
 rows.append({'index':index,'variant':variant,'path':str(path),'sha256':sha(path),'processId':raw['processId'],'startedUtc':raw['startedUtc'],'finishedUtc':raw['finishedUtc'],'warm':[{k:v for k,v in item.items() if k not in ('fullFrameHex',)} for item in raw['observations'] if item['name']=='dynamic-null-warm'],'cold':[{k:v for k,v in item.items() if k not in ('fullFrameHex',)} for item in raw['observations'] if item['name']=='dynamic-null-cold-first-access']})
for phase in ['first-baseline','interleaved-rest']:
 receipt=json.loads((WORK/phase/'receipt.json').read_text());require(receipt['status']=='completed' and receipt['runtimeOverrides'] is None,'Controller phase incomplete')
 owner=json.loads((WORK/phase/'postgres-owner.json').read_text());require(owner['exitCode']==0 and not owner.get('shutdownError'),'Database not joined')
 for row in receipt['results']:
  path=Path(row['result']);require(sha(path)==row['resultSha256'] and row['success'],'Controller binding mismatch')
  command=json.loads((WORK/phase/f"process-{row['index']}-{row['variant']}.command.json").read_text());require(command['success'] and not command['timedOut'] and not command['observerErrors'],'Failed process accepted')
for left,right in zip(rows,rows[1:]):require(left['finishedUtc']<=right['startedUtc'],'Consumers overlapped or ordering changed')
summary=[]
for (provider,variant,name),values in sorted(all_observations.items()):
 summary.append({'provider':provider,'variant':variant,'name':name,'processes':len(values),'operationsEach':values[0]['operations'],'allocatedDelta':[v['allocatedDelta'] for v in values],'bytesPerOperation':[v['bytesPerOperation'] for v in values],'medianBytesPerOperation':statistics.median(v['bytesPerOperation'] for v in values),'gcDeltas':[[v[f'gc{i}After']-v[f'gc{i}Before'] for i in range(3)] for v in values]})
output={'schema':'db-perf-empty-parameters-verification-v2','status':'verified-isolated-microprobe-not-package-or-long-campaign','generatedUtc':datetime.now(timezone.utc).isoformat(),'validatorSha256':sha(__file__),'sourceSha256':expected_source,'executableSha256':expected_elf,'probeSha256':builds['baseline']['probeSha256'],'projectSha256':builds['baseline']['projectSha256'],'nativeSha256':expected_native,'sixProcesses':rows,'summary':summary,'fullEmptyFrameSha256':{provider:hashlib.sha256(frames[(provider,'dynamic-null-warm')]).hexdigest() for provider in expected_native},'businessAllocatedBytesWithinEncodingSeam':0,'endToEndAllocationMeasured':False,'gcClassification':'Whole process; never split counts into framework and business','qualifiedCpuLatencyThroughputMeasured':False,'excludedEvidence':['v1 owner-zero checks observed a separately loaded DSO and are not accepted as owner retirement proof. v1 raw allocations remain historical, not merged with v2.','Initial v0 provider loader rejection NWPKG002 occurred before allocation measurement and its failed process/clean PostgreSQL shutdown remain preserved.','v2 candidate-stale-build reused baseline ELF due preserved source mtime. Rejected before any execution; final candidate used Rebuild,Publish with a distinct verified ELF.']}
if len(sys.argv)>1:
 target=Path(sys.argv[1]);require(not target.exists(),'Refusing to overwrite prior analysis output');target.write_text(json.dumps(output,indent=2)+'\n');print(json.dumps({'status':output['status'],'path':str(target),'sha256':sha(target),'processes':6}))
else:print(json.dumps({'status':output['status'],'processes':6,'candidateWarmBothProfilesAllZero':True}))
