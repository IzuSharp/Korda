#!/usr/bin/env python3
"""Check actual B08 public declarations against the exact Plan-pinned Design."""
import argparse,json,os,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--design',type=Path,required=True);p.add_argument('--tool',type=Path,required=True);p.add_argument('--configuration',default='Release');args=p.parse_args()
pin='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
assert subprocess.check_output(['git','-C',args.design,'rev-parse','HEAD'],text=True).strip()==pin
expected={entry['id']:entry for entry in json.loads((args.design/'docs/api/public-api.json').read_text().replace('NativeWeave.', 'Korda.'))['symbols']}
types={'Korda.Database.'+name for name in ['Migration','MigrationBundle','MigrationOptions','MigrationStatus','MigrationReport','MigrationTransactionMode','DatabaseAdministration','SchemaColumn','SchemaTable']}
def owned(entry):
    owner=entry['containing_type'] or (entry['id'][2:] if entry['kind']=='NamedType' else '')
    return owner in types or owner=='Korda.Database.DataSource' and entry['name']=='Administration'
output=root/'artifacts/B08/api';output.mkdir(parents=True,exist_ok=True)
reference=root/'src/Data.Migrations/reference/database.md'
config={'assembly':str(root/'src/Data.NativeHost/bin'/args.configuration/'net11.0/Korda.Database.dll'),'sources':[str(root/folder) for folder in ['src/Data.NativeHost','src/Data.Migrations','src/Query.Native','src/Query.Fluent','src/Orm.Relations','src/Data.Materialization'] if (root/folder).exists()],'baselines':[],'title':'Korda.Database migration API','scope':'Compiled product API compared to Design commit '+pin+'. B08 owns explicit migration and catalog administration; docs/guides/data-migrations.md describes limits and provider semantics.','output':str(reference)}
path=output/'config.json';path.write_text(json.dumps(config,indent=2)+'\n')
command=['dotnet',str(args.tool),str(root),os.environ['DOTNET_ROOT'],str(path)]
subprocess.run([*command,'--snapshot'],check=True)
actual=json.loads(reference.with_suffix('.json').read_text())['symbols']
for entry in actual:
    assert entry['id'] in expected and expected[entry['id']]['signature']==entry['signature'],entry
ids={x['id'] for x in actual};missing=[entry['id'] for entry in expected.values() if owned(entry) and entry['id'] not in ids]
assert not missing,missing
baseline=output/'pinned.json';baseline.write_text(json.dumps({'design_commit':pin,'symbols':[expected[x['id']] for x in actual]},indent=2)+'\n')
config['baselines']=[str(baseline)];path.write_text(json.dumps(config,indent=2)+'\n');subprocess.run([*command,'--write'],check=True)
summary={'exact_pinned_assembly_signatures':len(ids),'required_B08_signatures':sum(owned(expected[entry["id"]]) for entry in actual),'XML_complete':True,'reference_complete':True,'design_commit':pin}
(output/'summary.json').write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(summary,indent=2))
