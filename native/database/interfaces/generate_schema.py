#!/usr/bin/env python3
"""Generate the shared constants; --check verifies committed output without modifying it."""
import argparse
import json
from pathlib import Path

root = Path(__file__).resolve().parents[3]
base = Path(__file__).resolve().parent
schema = json.loads((base / 'schema-v1.json').read_text())
values = {k: v for k, v in schema.items() if isinstance(v, int)}
def pascal(s):
    return ''.join(p.title() for p in s.split('_'))
cs = '// Generated from native/database/interfaces/schema-v1.json; do not edit.\nnamespace NativeWeave.Data;\ninternal static class WireSchema\n{\n'
rs = '// Generated from schema-v1.json; do not edit.\n'
for key, value in values.items():
    cs += f'    internal const int {pascal(key)} = {value};\n'
    rs += f'pub const {key.upper()}: usize = {value};\n'
for key in ('model_keys', 'column_keys', 'projection_keys', 'projection_field_keys', 'relation_keys'):
    items = ', '.join(json.dumps(s) for s in schema[key])
    cs += f'    internal static readonly string[] {pascal(key)} = [{items}];\n'
    rs += f'#[rustfmt::skip]\npub const {key.upper()}: &[&str] = &[{items}];\n'
cs += '}\n'
outputs = {root / 'src/Data.Abstractions/WireSchema.g.cs': cs, base / 'src/schema.rs': rs}
check = argparse.ArgumentParser()
check.add_argument('--check', action='store_true')
args = check.parse_args()
for path, text in outputs.items():
    if args.check:
        if not path.exists() or path.read_text() != text:
            raise SystemExit(f'Schema output differs: {path}')
    else:
        path.write_text(text)
print('Shared schema outputs agree.')
