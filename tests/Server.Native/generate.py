#!/usr/bin/env python3
"""Exact pinned Server wire inventory; private names do not create a public managed API."""
import argparse, hashlib, re, subprocess
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(); p.add_argument('--check', action='store_true'); args = p.parse_args()
raw = (ROOT/'native/server/host/include/nativeweave_server_api_v1.h').read_bytes()
assert hashlib.sha256(raw).hexdigest() == 'f7316b35bfb8cce5cb4fded82a0907e602626c4e9e54ff18d99fe242f2b4b681'
source = re.sub(r'/\*.*?\*/', '', raw.decode(), flags=re.S)
def name(t): return ''.join(x[0].upper()+x[1:] for x in t.split('_'))
opaque = set(re.findall(r'typedef struct (nw_server\w*) \1;', source))
records = re.findall(r'typedef struct (\w+)\s*\{(.*?)\}\s*\1;', source, re.S)
ct = {'uint32_t':'uint', 'uint64_t':'ulong', 'uint8_t':'byte', 'uintptr_t':'nuint', 'void':'void'}
rt = {'uint32_t':'u32', 'uint64_t':'u64', 'uint8_t':'u8', 'uintptr_t':'usize', 'void':'()'}
def typ(t, rust=False):
    t=t.strip(); const=t.startswith('const '); t=t.removeprefix('const ').strip(); stars=t.count('*'); t=t.replace('*','').strip()
    if t in opaque:
        if rust: return ('*mut '*(stars or 1))+name(t)
        return 'nint'+'*'*max(0,stars-1)
    base=(rt if rust else ct).get(t, 'usize' if rust and t.startswith('nw_server_on_') else 'nint' if t.startswith('nw_server_on_') else name(t))
    return (('*const ' if const else '*mut ')*stars+base) if rust else base+'*'*stars
rs=['// Generated from exact pinned nativeweave_server_api_v1.h; do not edit.', '#![allow(dead_code)]','use nativeweave_interop_abi::*;','use crate::exports::*;']
cs=['// Generated from exact pinned nativeweave_server_api_v1.h; do not edit.','using System.Runtime.InteropServices;','using NativeWeave.Interop.Abi;','namespace NativeWeave.Server.Native;']
for tag,body in records:
    fields=[]
    for statement in body.split(';'):
        statement=statement.strip()
        if not statement: continue
        m=re.match(r'(.+?)\s+([*\w].*)$', statement)
        base,rest=m.groups()
        if base=='const':
            base,rest=statement.rsplit(' ',1)
        for item in rest.split(','):
            item=item.strip(); stars=len(item)-len(item.lstrip('*')); item=item.lstrip('*'); arr=re.match(r'(\w+)\[(\d+)\]',item)
            if arr: field,count=arr.groups();count=int(count)
            else: field,count=item,0
            fields.append((field,base+'*'*stars,count))
    rs += ['#[repr(C)]', '#[derive(Clone, Copy)]',f'pub struct {name(tag)} {{']
    cs += ['[StructLayout(LayoutKind.Sequential)]',f'internal unsafe struct {name(tag)}','{']
    for field,t,count in fields:
        rs += [f'    pub {field}: '+(f'[{typ(t,True)}; {count}]' if count else typ(t,True))+',']
        cs += [f'    internal fixed {typ(t)} {field}[{count}];' if count else f'    internal {typ(t)} {field};']
    rs+=['}'];cs+=['}']
cs += ['internal sealed unsafe class ServerNativeCalls','{']
assign=[]
for ret,symbol,params in re.findall(r'(uint32_t|void) NW_CDECL (nw_server_\w+)\((.*?)\);',source,re.S):
    types=[]; rust_types=[]
    for param in params.split(','):
        param=param.strip(); param=re.sub(r'(?<=[\s*])\b(?:requested_major|out_capacity|out_info|out_server|routes|count|out_op|out_addresses|out_snapshot|wait_timeout_ns|listener_name|tls_snapshot|out_reservation|out_request|out_metadata|ticket|application_status|capacity|out_credit|kind|native_capacity|max_bytes|out_frame_kind|out_buffer|out_trailers|out_length|destination|out_written|http_status|headers|bytes|trailers|message|metadata|grpc_status|detail|decision|out_call_kind|out_arguments|reply_kind|result_json|out_headers)\b$', '',param).strip()
        types.append(typ(param)); rust_types.append(typ(param,True))
    rs.append('const _: unsafe extern \"C\" fn('+', '.join(rust_types)+') -> '+typ(ret,True)+' = crate::exports::'+symbol+';')
    prototype='delegate* unmanaged[Cdecl]<'+', '.join(types+[typ(ret)])+'>'
    cs += [f'    internal readonly {prototype} {symbol};']
    assign += [f'        {symbol} = ({prototype})resolve("{symbol}");']
cs += ['    internal ServerNativeCalls(Func<string,nint> resolve)','    {',*assign,'    }','}']
rs.append('include!("../generated/pinned_layout_assertions.rs");')
for path,content in {'native/server/host/src/wire.rs':'\n'.join(rs)+'\n','src/Server.Native/Wire.g.cs':'\n'.join(cs)+'\n'}.items():
    target=ROOT/path
    if path.endswith('.rs'): content=subprocess.run(['rustfmt','--edition','2024','--emit','stdout'],input=content,text=True,check=True,capture_output=True).stdout
    if args.check: assert target.read_text()==content, 'Generated signature drift: '+path
    else: target.write_text(content)
print('PASS exact pinned Server wire records='+str(len(records)))
