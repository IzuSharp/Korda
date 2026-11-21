#!/usr/bin/env python3
"""Exercise the actual compiled API/XML gate and pinned DocFX composition."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    logs = out / 'logs'; logs.mkdir()
    records = []
    receipt = {'task':'A13', 'rid':'linux-x64', 'status':'running', 'checks':[]}
    def run(label, command, contains=None):
        result = subprocess.run([str(x) for x in command], cwd=ROOT, capture_output=True)
        stdout, stderr = logs/(label+'.stdout'), logs/(label+'.stderr')
        stdout.write_bytes(result.stdout); stderr.write_bytes(result.stderr)
        records.append({'label':label, 'command':[str(x) for x in command], 'exit_code':result.returncode,
                        'expected':'nonzero with '+contains if contains else 'zero',
                        'stdout_sha256':digest(stdout), 'stderr_sha256':digest(stderr)})
        unexpected = (result.returncode == 0 or contains.encode() not in result.stdout+result.stderr) if contains else result.returncode != 0
        if unexpected:
            for stream in (result.stdout, result.stderr):
                if stream:
                    print(stream.decode(errors='replace')[-16000:], file=sys.stderr)
            raise AssertionError(label+(' did not reject the intended malformed contract' if contains else ' failed'))
        return result.stdout.decode()
    try:
        receipt['source_commit'] = run('source', ['git','rev-parse','HEAD']).strip()
        receipt['source_dirty'] = bool(run('source-status',['git','status','--porcelain']).strip())
        flags = ['-c','Release','-p:UseSharedCompilation=false','-m:1','-nodeReuse:false']
        for label, project in [('fixture','tests/Build.Integration/ApiFixture/ApiFixture.csproj'),
                               ('tool','src/Build.Integration/ApiReference/ApiReference.csproj')]:
            run(label+'-restore',['dotnet','restore',project,'--locked-mode'])
            run(label+'-build',['dotnet','build',project,'--no-restore']+flags)
        project = ROOT/'src/Build.Integration/ApiReference/ApiReference.csproj'
        config = json.loads((ROOT/'tests/Build.Integration/api-fixture.json').read_text())
        inputs = out/'inputs'; inputs.mkdir()
        assembly = ROOT/config['assembly']
        for path in assembly.parent.glob('*.dll'): shutil.copy2(path,inputs/path.name)
        xml = inputs/assembly.with_suffix('.xml').name
        shutil.copy2(assembly.with_suffix('.xml'),xml)
        original_xml = xml.read_bytes()
        baseline = json.loads((ROOT/config['baselines'][0]).read_text())
        reviewed = out/'expected.json'; reviewed.write_text(json.dumps(baseline))
        config.update(assembly=str(inputs/assembly.name),baselines=[str(reviewed)],output=str(out/'reference.md'))
        file = out/'config.json'; file.write_text(json.dumps(config))
        command = ['dotnet','run','--project',project,'-c','Release','--no-build','--',ROOT,os.environ['DOTNET_ROOT'],file,'--write']
        run('compiled-positive', command)
        for label, old, new in [('default-drift','offset = 37','offset = 38'),('nullability-drift','string? value','string value'),('constraint-drift','where T : unmanaged','where T : struct')]:
            text = json.dumps(baseline)
            if old not in text: raise AssertionError('Negative setup did not match expected signature')
            reviewed.write_text(text.replace(old,new))
            run(label, command, 'expected:')
        reviewed.write_text(json.dumps(baseline))
        tree=ET.fromstring(original_xml)
        member=tree.find('.//member/param'); member.set('name','wrong_parameter')
        xml.write_bytes(ET.tostring(tree))
        run('xml-parameter',command,'XML parameter names differ')
        tree=ET.fromstring(original_xml)
        for member in tree.findall('.//member'):
            returns=member.find('returns')
            if returns is not None: member.remove(returns); break
        xml.write_bytes(ET.tostring(tree))
        run('xml-returns',command,'Missing XML returns')
        tree=ET.fromstring(original_xml)
        member=tree.find('.//member'); member.remove(member.find('summary'))
        xml.write_bytes(ET.tostring(tree))
        run('xml-summary',command,'Missing XML summary')
        for tag, diagnostic in [('param','XML parameter names differ'),('typeparam','Missing XML type parameter'),('returns','Missing XML returns')]:
            tree=ET.fromstring(original_xml)
            for element in tree.findall('.//'+tag):
                for child in list(element): element.remove(child)
                element.text=' '
            xml.write_bytes(ET.tostring(tree))
            run('empty-'+tag,command,diagnostic)
        xml.write_bytes(original_xml)
        receipt['checks'].extend(['exact compiled defaults/nullability/generics fail closed','missing XML summary/parameter/returns fail closed'])
        run('docfx',['python3','src/Build.Integration/compose_docs.py','--config','tests/Build.Integration/api-fixture.json','--output',out/'html'])
        receipt['documentation'] = json.loads((out/'html/result.json').read_text())
        if (receipt['documentation']['guide_sha256']['docs/guides/cpp-native.md'] != digest(ROOT/'docs/guides/cpp-native.md')
                or not (out/'html/site/cpp-native.html').is_file()):
            raise AssertionError('Standalone guide composition omitted the linked native implementation guide')
        receipt['checks'].append('actual pinned DocFX assembly/XML/guide composition, warnings as errors')
        receipt['status']='passed'
    except BaseException as error:
        receipt['status']='failed'; receipt['failure']=str(error); raise
    finally:
        receipt['commands']=records
        receipt['source_sha256']={str(p.relative_to(ROOT)):digest(p) for p in sorted((ROOT/'src/Build.Integration').rglob('*')) if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(ROOT).parts)}
        (out/'result.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps({'status':'passed','commands':len(records),'receipt':str(out/'result.json')}))

if __name__=='__main__': main()
