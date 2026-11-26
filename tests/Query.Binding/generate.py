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
cs=['// Generated from exact pinned signatures-v1.json; do not edit.','using Korda.Interop.Abi;','namespace Korda.Query.Internal;','/// <summary>Exact module-local direct calls; the creating engine supplies process-pinned exports.</summary>','internal sealed unsafe class NativeCalls','{','    internal readonly string EngineId;']
constructor=[]
for line in data['signatures']:
    ret,name,params=re.match(r'(\w+) NW_CDECL (\w+)\((.*)\);',line).groups()
    types=[re.sub(r'\s+\w+$','',p.strip()) for p in params.split(',')]
    prototype='delegate* unmanaged[Cdecl]<'+', '.join([*(ctypes[t] for t in types),ctypes[ret]])+'>'
    cs+=['    /// <summary>Exact Cdecl '+name+'; borrowed input spans end at return and outputs transfer only on OK.</summary>',f'    internal readonly {prototype} {name};']
    constructor.append(f'        {name} = ({prototype})resolve("{name}");')
cs+=['    /// <summary>Captures already negotiated creating-module exports; zero means unsupported until required by a call.</summary>','    internal NativeCalls(string engineId, Func<string, nint> resolve)','    {','        EngineId = engineId;','        ArgumentNullException.ThrowIfNull(resolve);',*constructor,'    }','}']
header=['/* Generated exact normative Database declarations; shared records come from A01. */','#ifndef KORDA_DATABASE_BINDING_V1_H','#define KORDA_DATABASE_BINDING_V1_H','#include "../../interop/abi/include/korda_abi_v1.h"','typedef struct nw_db_owner* nw_db_ref;','typedef struct nw_db_buffer nw_db_buffer;','typedef uint64_t nw_db_token;',*data['signatures'],'#endif']
outputs={'src/Query.Binding/NativeCalls.g.cs':'\n'.join(cs)+'\n','native/database/binding/korda_database_binding_v1.h':'\n'.join(header)+'\n'}
for name,value in outputs.items():
    path=ROOT/name
    if args.check: assert path.read_text()==value,'Generated signature drift: '+name
    else:path.write_text(value)
extension=json.loads((ROOT/'native/database/binding/query-command-v1.json').read_text())
assert extension['version']==1 and extension['capability']=='db.query.command.v1' and extension['capability_bit']==26
declarations=re.findall(r'\bnw_status\s+NW_CDECL\s+nw_db_query_command_\w+_v1\([^;]+;', (ROOT/extension['header']).read_text())
declarations=[re.sub(r'\s+', ' ', value) for value in declarations]
assert declarations==extension['signatures'],'Additive query command signatures differ from the reviewed extension'
assert len(declarations)==3 and not set(declarations).intersection(data['signatures'])
batch=json.loads((ROOT/'native/database/binding/cursor-pull-v1.json').read_text())
assert batch['version']==1 and batch['capability']=='db.cursor.pull.v1' and batch['capability_bit']==27
batch_declarations=re.findall(r'\bnw_status\s+NW_CDECL\s+nw_db_cursor_pull_\w+_v1\([^;]+;', (ROOT/batch['header']).read_text())
batch_declarations=[re.sub(r'\s+', ' ', value) for value in batch_declarations]
assert batch_declarations==batch['signatures'],'Additive cursor pull signatures differ from the reviewed extension'
assert len(batch_declarations)==10 and not set(batch_declarations).intersection(data['signatures'])
assert batch['record']=={'name':'nw_db_batch_result_v1','linux_x64_size':32,
    'offsets':{'owner':0,'bytes':8,'known_end':24,'reserved':28}},'Additive batch record layout differs'
assert batch['callback_record']=={'name':'nw_db_cursor_callbacks_v1','linux_x64_size':24,
    'offsets':{'struct_size':0,'reserved':4,'context':8,'ready':16}},'Cursor ready callback layout differs'
completion=json.loads((ROOT/'native/database/binding/command-completion-v1.json').read_text())
assert completion['version']==1 and completion['capability']=='db.command.completion.v1' and completion['capability_bit']==28
completion_declarations=re.findall(r'\bnw_status\s+NW_CDECL\s+nw_db_command_\w+_v1\([^;]+;', (ROOT/completion['header']).read_text())
completion_declarations=[re.sub(r'\s+', ' ', value) for value in completion_declarations]
assert completion_declarations==completion['signatures'],'Additive one-shot completion signatures differ from the reviewed extension'
assert len(completion_declarations)==4 and not set(completion_declarations).intersection(data['signatures'])
assert completion['request_record']=={'name':'nw_db_command_request_args_v1','linux_x64_size':48,
    'offsets':{'struct_size':0,'kind':4,'target':8,'input':16,'payload':24,'action':40,'reserved':44}},'Command request layout differs'
assert completion['result_record']=={'name':'nw_db_command_result_v1','linux_x64_size':32,
    'offsets':{'struct_size':0,'kind':4,'count':8,'owner':16,'buffer':24}},'Command result layout differs'
assert completion['callback_record']=='nw_db_cursor_callbacks_v1' and completion['callback_generation']==0
find_shape=json.loads((ROOT/'native/database/binding/find-shape-v1.json').read_text())
assert find_shape['version']==1 and find_shape['capability']=='db.find.shape.v1' and find_shape['capability_bit']==29
find_declarations=re.findall(r'\bnw_status\s+NW_CDECL\s+nw_db_find_\w+_v1\([^;]+;', (ROOT/find_shape['header']).read_text())
find_declarations=[re.sub(r'\s+', ' ', value) for value in find_declarations]
assert find_declarations==find_shape['signatures'],'Additive Find shape signatures differ from the reviewed extension'
assert len(find_declarations)==1 and not set(find_declarations).intersection(data['signatures'])
print(f'PASS exact frozen signatures={len(data["signatures"])}; outputs={len(outputs)}; additive query command signatures={len(declarations)}; additive batch signatures={len(batch_declarations)}; additive completion signatures={len(completion_declarations)}; additive Find shape signatures={len(find_declarations)}')
