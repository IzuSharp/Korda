#!/usr/bin/env python3
"""Compile real type-error consumers against the built product, without a stub."""
import argparse
import json
import os
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--configuration', default='Release')
args = parser.parse_args()
sdk = Path(os.environ['DOTNET_ROOT'])
csc = sdk / 'sdk/11.0.100/Roslyn/bincore/csc.dll'
reference = sdk / 'packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0'
product = root / 'src/Data.NativeHost/bin' / args.configuration / 'net11.0'
output = root / 'artifacts/B14/compile-negative'
output.mkdir(parents=True, exist_ok=True)
header = 'using NativeWeave.Data; using NativeWeave.Database; class Parent {} class Child {} static class Consumer { '
cases = [
    ('wrong_key', 'static void Test(DataSession session, Table<Parent> table, EntityKey<Child> key) { _ = session.FindAsync(table, key); }', ['CS0411']),
    ('wrong_changes', 'static void Test(DataSession session, Table<Parent> table, EntityKey<Parent> key, Changes<Child> changes) { _ = session.UpdateAsync(table, key, changes); }', ['CS0411']),
    ('wrong_relation', 'static void Test(DataSession session, EntityKey<Child> key, Relation<Parent,Child> relation) { _ = session.LoadRelatedAsync(key, relation); }', ['CS0411']),
    ('wrong_conflict', 'static void Test(DataSession session, Table<Parent> table, Parent value, ConflictTarget<Child> target, Changes<Parent> changes) { _ = session.UpsertAsync(table, value, target, changes); }', ['CS0411']),
    ('missing_checks', 'static void Test(DataSession session, Table<Parent> table, EntityKey<Parent> key, Changes<Parent> changes) { _ = session.UpdateCheckedAsync(table, key, changes); }', ['CS7036']),
    ('wrong_fk_value', 'static void Test(Column<Child,long?> foreignKey) { _ = new Changes<Child>().Set(foreignKey, "wrong type"); }', ['CS0411']),
]
references = [*reference.glob('*.dll'), *product.glob('NativeWeave.*.dll')]
results = []
for name, body, codes in cases:
    source = output / (name + '.cs')
    source.write_text(header + body + ' }\n')
    arguments = ['/nologo', '/target:library', '/langversion:15', '/nullable:enable', '/warnaserror+', '/out:' + str(output / (name + '.dll'))]
    arguments += ['/reference:' + str(path) for path in references]
    arguments += [str(source)]
    result = subprocess.run(['dotnet', str(csc), *arguments], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / (name + '.log')).write_text(result.stdout)
    assert result.returncode != 0 and all(code in result.stdout for code in codes), (name, result.returncode, result.stdout)
    assert 'error CS0006' not in result.stdout, result.stdout
    results.append({'case': name, 'expected_diagnostics': codes, 'rejected': True})
(output / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps({'actual_product_compile_negatives': len(results), 'passed': True}))
