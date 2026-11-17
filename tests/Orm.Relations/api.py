#!/usr/bin/env python3
"""Compare compiled B14 declarations/XML to the exact Plan-pinned inventory."""
import argparse
import json
import os
import re
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--design', type=Path, required=True)
parser.add_argument('--tool', type=Path, required=True)
parser.add_argument('--configuration', default='Release')
args = parser.parse_args()
pin = 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
assert subprocess.check_output(['git', '-C', args.design, 'rev-parse', 'HEAD'], text=True).strip() == pin
expected = {entry['id']: entry for entry in json.loads((args.design / 'docs/api/public-api.json').read_text())['symbols']}
types = {
    'NativeWeave.Data.' + name for name in ['Changes', 'ConcurrencyCheck', 'ConcurrencyConflictException', 'EntityKey', 'Relation']
} | {
    'NativeWeave.Database.' + name for name in ['BulkAtomicity', 'BulkFailure', 'BulkOptions', 'BulkProgress', 'BulkResult', 'ConflictTarget']
} | {'NativeWeave.Query.UpdateSetters'}
session_members = {'FindAsync', 'LoadRelatedAsync', 'InsertAsync', 'InsertReturningAsync', 'UpdateAsync', 'UpdateCheckedAsync', 'DeleteAsync', 'DeleteCheckedAsync', 'UpsertAsync', 'InsertManyAsync'}


def required(entry):
    owner = entry['containing_type'] or (entry['id'][2:] if entry['kind'] == 'NamedType' else '')
    owner = re.split(r'[<`]', owner)[0]
    return owner in types or owner == 'NativeWeave.Database.DataSession' and entry['name'] in session_members


output = root / 'artifacts/B14/api'
output.mkdir(parents=True, exist_ok=True)
summaries = []
all_ids = set()
for name, project, assembly, folders in [
    ('database', 'Data.NativeHost', 'NativeWeave.Database', ['Data.NativeHost', 'Data.Migrations', 'Query.Native', 'Query.Fluent', 'Orm.Relations', 'Data.Materialization']),
    ('data', 'Data.Abstractions', 'NativeWeave.Data', ['Data.Abstractions', 'Query.Generator/Runtime', 'Orm.Relations']),
]:
    reference = root / 'src/Orm.Relations/reference' / (name + '.md')
    config = {
        'assembly': str(root / 'src' / project / 'bin' / args.configuration / 'net11.0' / (assembly + '.dll')),
        'sources': [str(root / 'src' / folder) for folder in folders],
        'baselines': [],
        'title': assembly + ' explicit relationship and mutation API',
        'scope': 'Compiled product declarations compared to Design commit ' + pin + '. B14 owns explicit relation loading, native CRUD, optimistic concurrency and bounded bulk; docs/guides/orm-relations.md explains ownership, limits and cost.',
        'output': str(reference),
    }
    path = output / (name + '-config.json')
    path.write_text(json.dumps(config, indent=2) + '\n')
    command = ['dotnet', str(args.tool), str(root), os.environ['DOTNET_ROOT'], str(path)]
    subprocess.run([*command, '--snapshot'], check=True)
    actual = json.loads(reference.with_suffix('.json').read_text())['symbols']
    for entry in actual:
        assert entry['id'] in expected and expected[entry['id']]['signature'] == entry['signature'], entry
    all_ids.update(entry['id'] for entry in actual)
    baseline = output / (name + '-pinned.json')
    baseline.write_text(json.dumps({'design_commit': pin, 'symbols': [expected[entry['id']] for entry in actual]}, indent=2) + '\n')
    config['baselines'] = [str(baseline)]
    path.write_text(json.dumps(config, indent=2) + '\n')
    subprocess.run([*command, '--write'], check=True)
    summaries.append({'assembly': assembly, 'exact_signatures': len(actual), 'B14_signatures': sum(required(expected[entry['id']]) for entry in actual), 'XML_complete': True, 'reference_complete': True})
missing = [entry['id'] for entry in expected.values() if required(entry) and entry['id'] not in all_ids]
assert not missing, missing
summary = {'design_commit': pin, 'assemblies': summaries, 'required_B14_symbols': sum(required(entry) for entry in expected.values()), 'missing': missing}
(output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
