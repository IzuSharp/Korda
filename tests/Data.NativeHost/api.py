#!/usr/bin/env python3
"""Compare the compiled B04 composition to the commit-pinned Design, then generate XML reference."""
import argparse
import json
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--design', type=Path, required=True)
parser.add_argument('--tool', type=Path, default=root / 'src/Build.Integration/ApiReference/bin/Release/net11.0/Korda.Build.ApiReference.dll')
parser.add_argument('--configuration', default='Debug')
args = parser.parse_args()
pin = 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
if subprocess.check_output(['git', '-C', str(args.design), 'rev-parse', 'HEAD'], text=True).strip() != pin:
    raise SystemExit('Design checkout does not match the Plan-locked commit')
expected = {entry['id']: entry for entry in json.loads((args.design / 'docs/api/public-api.json').read_text().replace('NativeWeave.', 'Korda.'))['symbols']}
output = root / 'artifacts/B04/api'
output.mkdir(parents=True, exist_ok=True)
core_types = {'Korda.Database.' + value for value in ['DatabaseProvider', 'DatabaseLimits', 'DatabaseCapabilities', 'DataSourceBuilder', 'DataSource', 'DataSession']}
session_members = {'Source', 'Transaction', 'Cleanup', 'ExecuteAsync', 'ScalarAsync', 'QueryAsync', 'StreamAsync', 'BeginTransactionAsync', 'CloseAsync', 'Dispose', 'DisposeAsync'}

def required(entry):
    owner = entry['containing_type'] or (entry['id'][2:] if entry['kind'] == 'NamedType' else '')
    if owner not in core_types:
        return False
    if owner.endswith('.DataSource') and entry['name'] == 'Administration':
        return False  # the original task assigns the actual implementation to B08
    if owner.endswith('.DataSession') and entry['kind'] != 'NamedType' and entry['name'].split('<')[0] not in session_members:
        return False  # Fluent/relationship members are independently implemented by B13/B14
    return True

compositions = [
    ('database', 'src/Data.NativeHost', 'Korda.Database', ['src/Data.NativeHost', 'src/Query.Native', 'src/Query.Fluent', 'src/Data.Migrations', 'src/Orm.Relations']),
    ('sqlite', 'src/Data.Sqlite', 'Korda.Database.Sqlite', ['src/Data.Sqlite', 'src/Data.NativeHost/Providers/Sqlite']),
    ('postgresql', 'src/Data.Postgres', 'Korda.Database.PostgreSql', ['src/Data.Postgres', 'src/Data.NativeHost/Providers/PostgreSql']),
    ('dependency-injection', 'src/Data.NativeHost/DependencyInjection', 'Korda.Database.DependencyInjection', ['src/Data.NativeHost/DependencyInjection']),
]
summary = {}
for name, directory, assembly, sources in compositions:
    reference = root / 'src/Data.NativeHost/reference' / (name + '.md')
    config = {'assembly': str(root / directory / 'bin' / args.configuration / 'net11.0' / (assembly + '.dll')),
              'sources': [str(root / source) for source in sources if (root / source).exists()],
              'baselines': [], 'title': assembly + ' compiled API',
              'scope': 'Current product declarations compared with Design commit ' + pin + '. Ownership, costs and Linux applicability are described in docs/guides/data-nativehost.md.',
              'output': str(reference)}
    config_path = output / (name + '.json')
    config_path.write_text(json.dumps(config, indent=2) + '\n')
    command = ['dotnet', str(args.tool), str(root), os.environ['DOTNET_ROOT'], str(config_path)]
    subprocess.run([*command, '--snapshot'], check=True, cwd=root)
    actual = json.loads(reference.with_suffix('.json').read_text())['symbols']
    missing = []
    for entry in actual:
        baseline = expected.get(entry['id'])
        if baseline is None or baseline['signature'] != entry['signature']:
            raise SystemExit('Pinned declaration differs: ' + entry['id'] + '\nactual=' + entry['signature'] + '\npinned=' + str(baseline and baseline['signature']))
    ids = {entry['id'] for entry in actual}
    if name == 'database':
        missing = [entry['id'] for entry in expected.values() if required(entry) and entry['id'] not in ids]
    elif name == 'dependency-injection':
        missing = [entry['id'] for entry in expected.values() if entry['semantic_policy'] == 'Microsoft.Extensions.DependencyInjection.KordaDatabaseServiceCollectionExtensions' and entry['id'] not in ids]
    if missing:
        raise SystemExit('Required B04 declarations missing: ' + '\n'.join(missing))
    # Run the shared full-signature/XML checker again with independently pinned entries.
    baseline_path = output / (name + '-pinned.json')
    baseline_path.write_text(json.dumps({'design_commit': pin, 'symbols': [expected[entry['id']] for entry in actual]}, indent=2) + '\n')
    config['baselines'] = [str(baseline_path)]
    config_path.write_text(json.dumps(config, indent=2) + '\n')
    subprocess.run([*command, '--write'], check=True, cwd=root)
    summary[name] = {'exact_pinned_signatures': len(ids), 'xml_complete': True, 'reference_complete': True}
(output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
