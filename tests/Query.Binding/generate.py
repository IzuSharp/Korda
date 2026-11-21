#!/usr/bin/env python3
"""Generate exact internal prototypes; --check compares all outputs to the pinned inventory."""
import argparse,json,re
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');parser.add_argument('--design',type=Path);args=parser.parse_args()
data=json.loads((ROOT/'native/database/binding/signatures-v1.json').read_text())
if args.design:
    import hashlib
    source=(args.design/data['source']).read_bytes()
    assert hashlib.sha256(source).hexdigest()==data['source_sha256'],'Pinned Design content changed'
    current=re.findall(r'^(?:nw_status|void) NW_CDECL nw_db_.*?;$',source.decode(),re.M)
    assert current==data['signatures'],'Normative signature inventory differs'
ctypes={'nw_db_ref':'nint','nw_db_ref*':'nint*','nw_db_buffer*':'nint','nw_db_buffer**':'nint*','nw_db_token':'ulong','nw_db_token*':'ulong*','uint32_t':'uint','uint32_t*':'uint*','uint64_t':'ulong','int64_t*':'long*','nw_bytes_view_v1':'NwBytesViewV1','nw_bytes_view_v1*':'NwBytesViewV1*','nw_error_info_v1*':'NwErrorInfoV1*','const nw_operation_callbacks_v1*':'NwOperationCallbacksV1*','nw_status':'uint','void':'void'}
cs=['// Generated from exact pinned signatures-v1.json; do not edit.','using NativeWeave.Interop.Abi;','namespace NativeWeave.Query.Internal;','/// <summary>Exact module-local direct calls; the creating engine supplies process-pinned exports.</summary>','internal sealed unsafe class NativeCalls','{','    internal readonly string EngineId;']
constructor=[]
for line in data['signatures']:
    ret,name,params=re.match(r'(\w+) NW_CDECL (\w+)\((.*)\);',line).groups()
    types=[re.sub(r'\s+\w+$','',p.strip()) for p in params.split(',')]
    prototype='delegate* unmanaged[Cdecl]<'+', '.join([*(ctypes[t] for t in types),ctypes[ret]])+'>'
    cs+=['    /// <summary>Exact Cdecl '+name+'; borrowed input spans end at return and outputs transfer only on OK.</summary>',f'    internal readonly {prototype} {name};']
    constructor.append(f'        {name} = ({prototype})resolve("{name}");')
cs+=['    /// <summary>Captures already negotiated creating-module exports; zero means unsupported until required by a call.</summary>','    internal NativeCalls(string engineId, Func<string, nint> resolve)','    {','        EngineId = engineId;','        ArgumentNullException.ThrowIfNull(resolve);',*constructor,'    }','}']
header=['/* Generated exact normative Database declarations; shared records come from A01. */','#ifndef NATIVEWEAVE_DATABASE_BINDING_V1_H','#define NATIVEWEAVE_DATABASE_BINDING_V1_H','#include "../../interop/abi/include/nativeweave_abi_v1.h"','typedef struct nw_db_owner* nw_db_ref;','typedef struct nw_db_buffer nw_db_buffer;','typedef uint64_t nw_db_token;',*data['signatures'],'#endif']
outputs={'src/Query.Binding/NativeCalls.g.cs':'\n'.join(cs)+'\n','native/database/binding/nativeweave_database_binding_v1.h':'\n'.join(header)+'\n'}
for name,value in outputs.items():
    path=ROOT/name
    if args.check: assert path.read_text()==value,'Generated signature drift: '+name
    else:path.write_text(value)
print(f'PASS exact signatures={len(data["signatures"])}; outputs={len(outputs)}')
