#!/usr/bin/env python3
"""Explicit supported A09 projection plus complete exact-header ABI inventory.
Unsupported record/by-value forms stay in the pinned direct binding; nothing is retyped.
"""
from pathlib import Path
import hashlib,json,re,ctypes,subprocess,os
R=Path(__file__).resolve().parents[2]; D=R/'tests/Server.Native/Bindings'; A=R/'native/server/host/src/exports.rs'
sha=lambda b:hashlib.sha256(b).hexdigest()
source=A.read_text(); wire=(R/'native/server/host/src/wire.rs').read_text()
record=[]
scalars={'u8':(1,1),'u32':(4,4),'i32':(4,4),'u64':(8,8),'usize':(8,8)}
def rec(name,fields):
 offset=0; alignment=1; fs=[]
 for field,ty in fields:
  size,a=scalars[ty];alignment=max(alignment,a);offset=(offset+a-1)//a*a;fs.append(dict(name=field,type=ty,offset=offset,reserved=field in ('reserved','flags')));offset+=size
 record.append(dict(name=name,size=(offset+alignment-1)//alignment*alignment,alignment=alignment,fields=fs))
rec('NwAbiInfoV1',[(n,'u32')for n in ['struct_size','abi_major','abi_minor','pointer_width_bits']]+[('capabilities','u64'),('reserved','u64')])
rec('NwErrorInfoV1',[('struct_size','u32'),('domain','u32'),('code','i32'),('flags','u32'),('correlation_id','u64')])
rec('NwOperationCallbacksV1',[('struct_size','u32'),('flags','u32'),('cookie','usize'),('on_terminal','usize')])
for name,body in re.findall(r'pub struct (\w+) \{(.*?)\n\}',wire,re.S):
 fields=re.findall(r'pub (\w+): (.*?),',body)
 if fields and all(t in scalars for _,t in fields):rec(name,fields)
records={r['name']:r for r in record}
owners=['NwServer','NwServerRequest','NwServerOperation','NwServerMetadata','NwServerBuffer','NwServerManagedCredit','NwServerWriteReservation']
release={'NwServer':'nw_server_release',**{n:'nw_server_'+re.sub(r'(?<!^)(?=[A-Z])','_',n.removeprefix('NwServer')).lower()+'_release' for n in owners[1:]}}
# Exact source projection: retain supported complete function bodies; syn independently parses them.
matches=list(re.finditer(r'pub unsafe extern "C" fn (nw_server_\w+)\((.*?)\)(?: -> (\w+))? \{',source,re.S));exports=[];parts=[];excluded=[]
for idx,m in enumerate(matches):
 symbol,params,ret=m.groups();ps=[];valid=True
 for item in filter(None,(p.strip() for p in params.split(','))):
  name,t=item.split(':',1);t=t.strip(); ty=t.replace('*mut ','mut:').replace('*const ','const:');target=ty.split(':')[-1];pointer=':'in ty
  if target not in scalars and not(pointer and target in records.keys()|set(owners)):
   valid=False;break
  owner=target if target in owners else '';transfer=ty.startswith('mut:mut:') and bool(owner)
  direction='out' if pointer and name.startswith('out_') else 'in';direction='inout' if name=='error' else direction
  ps.append(dict(name=name,type=ty,direction=direction,nullable=pointer and (name=='error' or symbol.endswith('_release')),borrow_end='transfer-success' if transfer else 'call' if pointer else 'none',owner=owner,mutable=ty.startswith('mut:'),copy_before_accept=pointer and not owner and direction=='in',maximum_bytes=(8 if owner or ty.count(':')>1 else records.get(target,{}).get('size',scalars.get(target,(8,))[0])) if pointer else 0))
 if not valid:excluded.append(symbol);continue
 # End at the next exact entry documentation; the source has no omitted exported data.
 end=source.find('\n/// Exact Server ABI entry',m.end());end=len(source) if end<0 else end
 fragment='#[unsafe(no_mangle)]\n'+source[m.start():end].strip()+'\n';parts.append(fragment)
 exports.append(dict(symbol=symbol,calling_convention='C',capability='server.lifecycle.v1',error='cleanup-no-fail' if ret is None else 'status-v1',thread='free-threaded',reentrancy='forbidden',targets=['linux-x64'],parameters=ps,**{'return':ret or 'void'}))
projection='// Generated exact source projection of A09-supported primitive/pointer forms.\n'+'\n'.join(parts)
(D/'adapter-projection.rs').write_text(projection)
crates=[('tokio','1.54.1','https://github.com/tokio-rs/tokio',['rt','net','sync','time','macros','io-util']),('hyper','1.12.0','https://github.com/hyperium/hyper',['server','client','http1','http2']),('axum','0.8.9','https://github.com/tokio-rs/axum',['tokio','http1','http2'])]
up=[]
for crate,version,url,features in crates:
 vcs=next((Path(os.environ['CARGO_HOME'])/'registry/src').glob(f'*/{crate}-{version}/.cargo_vcs_info.json'))
 up.append(dict(repository=url,commit=json.loads(vcs.read_text())['git']['sha1'],features=features))
model=dict(schema_version=1,module=dict(id='nativeweave.server',abi_major=1,abi_minor=0,rid='linux-x64',target='x86_64-unknown-linux-gnu',pointer_width_bits=64),provenance=dict(adapter_sha256=sha(projection.encode()),tool='nativeweave-binding-inventory',tool_version='0.1.0',upstream=up),capabilities=['server.lifecycle.v1'],records=record,exports=exports,owners=[dict(kind=n,module='nativeweave.server',release=release[n],clone='',parent='',release_executor='calling-thread',close_progress='runtime-drives' if n in owners[:3] else 'none')for n in owners],operations=[],callbacks=[],managed_symbols=[])
(D/'binding.json').write_text(json.dumps(model,indent=2)+'\n')
all_symbols=set(re.findall(r'(?:uint32_t|void) NW_CDECL (nw_server_\w+)\(', (R/'native/server/host/include/nativeweave_server_api_v1.h').read_text()))
linked=set(line.split()[-1]for line in subprocess.check_output(['nm','-D','--defined-only',str(R/'target/release/libnativeweave_server.so')],text=True).splitlines()if line.split()[-1].startswith('nw_server_'))
assert linked==all_symbols,(sorted(linked-all_symbols),sorted(all_symbols-linked))
selected={x['symbol']for x in exports};assert selected|set(excluded)==all_symbols
(D/'linked-symbols.txt').write_text('\n'.join(sorted(selected))+'\n')
(D/'coverage.json').write_text(json.dumps(dict(schema_version=1,source_sha256=sha(A.read_bytes()),header_sha256=sha((R/'native/server/host/include/nativeweave_server_api_v1.h').read_bytes()),full_exact_exports=sorted(all_symbols),a10_a11_exports=sorted(selected),explicit_exact_bindings=sorted(excluded),reason='A09 scalar-record profile cannot describe nested records, fixed arrays or by-value NwBytesViewV1. Exact pinned signatures remain in Wire.g.cs and wire.rs, verified independently; no ABI substitution. A07 owns operation callbacks and retirement; generated owners are raw compilation coverage, not a replacement for the existing protocol.'),indent=2)+'\n')
print(f'PASS full exact exports={len(all_symbols)} A10/A11 pointer projection={len(selected)} explicit exact forms={len(excluded)}')
