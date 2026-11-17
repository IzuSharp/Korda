#!/usr/bin/env python3
"""Compile the pinned C header and bind all exact Server record sizes/offsets in Rust and C#."""
from pathlib import Path
import argparse,json,re,subprocess,tempfile
R=Path(__file__).resolve().parents[2];p=argparse.ArgumentParser();p.add_argument('--check',action='store_true');args=p.parse_args()
source=re.sub(r'/\*.*?\*/','',(R/'native/server/host/include/nativeweave_server_api_v1.h').read_text(),flags=re.S)
records=[]
for tag,body in re.findall(r'typedef struct (\w+)\s*\{(.*?)\}\s*\1;',source,re.S):
 fields=[]
 for statement in body.split(';'):
  statement=statement.strip()
  if not statement:continue
  m=re.match(r'(.+?)\s+([*\w].*)$',statement);base,rest=m.groups()
  if base=='const':base,rest=statement.rsplit(' ',1)
  for part in rest.split(','):fields.append(re.match(r'\**(\w+)',part.strip()).group(1))
 records.append((tag,fields))
probe=['#include <stdio.h>','#include <stddef.h>','#include "nativeweave_server_api_v1.h"','int main(void) {']
for tag,fields in records:
 for name,expr in [('size',f'sizeof({tag})'),('align',f'_Alignof({tag})'),*((field,f'offsetof({tag},{field})')for field in fields)]:probe.append(f'printf("{tag}.{name}=%zu\\n",{expr});')
probe.append('return 0;}');probe='\n'.join(probe)+'\n'
with tempfile.TemporaryDirectory(prefix='nw-server-layout-')as tmp:
 f=Path(tmp)/'probe.c';f.write_text(probe);exe=Path(tmp)/'probe';subprocess.run(['clang-19','-std=c11','-Wall','-Wextra','-Werror','-I',str(R/'native/server/host/include'),str(f),'-o',str(exe)],check=True);out=subprocess.check_output([str(exe)],text=True)
values={a:int(b)for a,b in (line.split('=')for line in out.splitlines())}
rs=['// Generated from the pinned C header with the Linux x64 C compiler.']
cs=['// Generated from the pinned C header with the Linux x64 C compiler.','using System.Runtime.InteropServices;','using NativeWeave.Server.Native;','internal static unsafe class ExactLayout','{','    internal static void Verify()','    {']
for tag,fields in records:
 name=''.join(x[0].upper()+x[1:]for x in tag.split('_'));get=lambda field:values[tag+'.'+field]
 rs.append(f'const _: () = {{ assert!(size_of::<{name}>() == {get("size")}); assert!(align_of::<{name}>() == {get("align")});')
 cs.append(f'        if (sizeof({name}) != {get("size")}) throw new InvalidOperationException("Exact size: {name}");')
 for field in fields:
  rs.append(f'    assert!(core::mem::offset_of!({name}, {field}) == {get(field)});')
  cs.append(f'        if ((int)Marshal.OffsetOf<{name}>("{field}") != {get(field)}) throw new InvalidOperationException("Exact offset: {name}.{field}");')
 rs.append('};')
cs+=['    }','}']
outputs={'tests/Server.Native/abi-probe.c':probe,'tests/Server.Native/Bindings/exact-layout.json':json.dumps(values,indent=2)+'\n','native/server/host/generated/pinned_layout_assertions.rs':'\n'.join(rs)+'\n','tests/Server.Native/ExactLayout.g.cs':'\n'.join(cs)+'\n'}
for path,text in outputs.items():
 file=R/path
 if args.check:assert file.read_text()==text,'Pinned ABI layout drift: '+path
 else:file.write_text(text)
print(f'PASS pinned C/Rust/C# exact layout records={len(records)} fields={sum(len(x[1])for x in records)}')
