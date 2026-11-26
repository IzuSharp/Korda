#!/usr/bin/env python3
"""Build a separate ordinary buffered AOT consumer from an exact phase build; never execute it."""
import argparse
import difflib
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent


def sha(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError('The bound phase consumer changed: ' + old[:100])
    return text.replace(old, new)


def section(text, start, end):
    if text.count(start) != 1 or text.count(end) != 1:
        raise ValueError('The bound phase consumer has an unknown structure')
    return text[text.index(start):text.index(end)]


def buffered_program(original):
    start = '    internal async ValueTask<Sample> Run(int lane, long tenant)\n'
    end = '    public void Dispose() => _prepared.Dispose();'
    text = replace_once(original, section(original, start, end), (HERE / 'Run.cs.in').read_text() + '\n')
    text = replace_once(text,
        'internal readonly record struct Sample(long ConstructTicks, long SubmitToFirstRowTicks, long ReadCompleteTicks, long ReleaseTicks, int Rows);',
        'internal readonly record struct Sample(long ConstructTicks, long SubmitToListTicks, long ReleaseTicks, int Rows);')
    text = replace_once(text, 'var receipt = new Receipt(3,', 'var receipt = new Receipt(4,')
    text = replace_once(text, 'new FailureReceipt(1, 3, Provider,', 'new FailureReceipt(1, 4, Provider,')
    text = replace_once(text, 'Setup, Construct, First, Read, Release, Verify, Receipt',
                        'Setup, Construct, SubmitToList, Release, Verify, Receipt')
    text = replace_once(text, 'ordered full List<PhaseRow> with', 'direct returned IReadOnlyList<PhaseRow> with')
    scope_start = '    "Local phase diagnostic. Construction'
    scope_end = '\ndiagnostics.Locate("receipt", -1, -1, -1);'
    scope = '''    "Local ordinary buffered query phases; no ABI wrappers or instrumentation shim. Construction covers dynamic query/selection or typed parameter/command construction. " +
    "SubmitToList starts immediately before ToListAsync/QueryAsync and ends when the complete IReadOnlyList is returned, including result allocation, all DTO reads and cursor retirement. " +
    "First-row latency is not observable through these public buffered terminals and is not reported. Release covers only explicit dynamic query-owner disposal after the terminal; returned DTOs remain managed results. " +
    "All routes inspect the actual returned IReadOnlyList without a second list or streaming adapter and verify every ordered Id/Note/null outside phase times but inside each full window. " +
    "Fluent and prepared SQL text are identical; SQLite raw SQL numbers its equivalent ordered Int64 markers. Fixture, SQL renderer, parameters, generated projection and expected values are inherited unchanged from the source phase consumer. " +
    "Preparation and first executions are separate observations, not independent process-cold runs. Window allocation and all GC counts are whole process; business list/DTO/string and harness costs remain included. " +
    "CPU/RSS and native owner/SQL/protocol counters are unchanged local process observations; protocol submissions do not measure physical network round trips. " +
    "Success performs no per-operation logging or native counter reads. Failure evidence preserves the original exception and any available counters without partial timing acceptance.");'''
    text = replace_once(text, section(text, scope_start, scope_end), scope)
    text = replace_once(text, 'long TeardownTicks, Counts Drained, string Scope);',
                        'long TeardownTicks, Counts Drained, string Scope)\n'
                        '{\n    public bool Buffered => true;\n    public bool FirstRowObservable => false;\n'
                        '    public bool AbiWrapperInstrumentation => false;\n}')
    text = replace_once(text, 'var resultRows = 100;',
                        'if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LD_PRELOAD")))\n'
                        '    throw new InvalidOperationException("Run this ordinary buffered consumer without LD_PRELOAD.");\n'
                        'var resultRows = 100;')
    text = replace_once(text, 'Console.WriteLine("DB_PERF_QUERY_PHASES " + args[1]);',
                        'Console.WriteLine("DB_PERF_BUFFERED_QUERY_PHASES " + args[1]);')
    # Refuse to replace a retained successful or failed attempt.
    text = replace_once(text, 'var diagnostics = new FailureDiagnostics(args[1]);',
                        'if (File.Exists(args[1]) || File.Exists(args[1] + ".failure.json"))\n'
                        '    throw new IOException("Use a fresh receipt path; earlier evidence is immutable.");\n'
                        'var diagnostics = new FailureDiagnostics(args[1]);')
    return text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--consumer', type=Path, required=True)
    parser.add_argument('--expect-source', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    consumer, output = args.consumer.resolve(), args.output.resolve()
    if output.exists() or consumer == output or consumer in output.parents:
        raise ValueError('Use a fresh independent output directory')
    if os.environ.get('LD_PRELOAD'):
        raise ValueError('The ordinary consumer build must not preload instrumentation')
    prior = json.loads((consumer / 'build-receipt.json').read_text())
    if (prior.get('kind') != 'query-phases' or prior.get('exit_code') != 0
            or prior.get('project_references') != 0 or not prior.get('package', '').startswith('Korda.Database.')):
        raise ValueError('A successful external Korda PackageReference phase consumer is required')
    project = Path(prior['command'][2])
    if project.parent != consumer or ET.parse(project).getroot().findall('.//ProjectReference'):
        raise ValueError('The source project must use external PackageReference only')
    if sha(project) != prior['project_sha256'] or sha(consumer / 'out/DbPerf.QueryPhases') != prior['executable_sha256']:
        raise ValueError('The ordinary project or executable differs from its receipt')
    sources = {p.name: sha(p) for p in consumer.glob('*.cs')}
    if sources != prior['sources'] or set(sources) != {'Program.cs'}:
        raise ValueError('The ordinary phase sources differ from their build receipt')
    package_id, version = prior['package'].lower(), prior['version'].lower()
    package = consumer / 'packages' / package_id / version / f'{package_id}.{version}.nupkg'
    if sha(package) != prior['package_sha256']:
        raise ValueError('The exact source package changed')
    with zipfile.ZipFile(package) as archive:
        metadata = ET.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith('.nuspec'))))
        source_commit = metadata.find('.//{*}repository').attrib['commit']
        package_inputs = json.loads(archive.read('korda/public.json'))
    if not re.fullmatch('[0-9a-f]{40}', args.expect_source) or source_commit != args.expect_source:
        raise ValueError('The actual package repository commit does not match --expect-source')
    native = {str(p.relative_to(consumer / 'out')): sha(p) for p in (consumer / 'out').rglob('libnw_database_*.so')}
    if native != prior['native_images']:
        raise ValueError('The ordinary native images changed')
    names = ['Program.cs', project.name, 'global.json', 'NuGet.Config', 'Directory.Build.props', 'workload-manifest.json']
    inputs = {name: sha(consumer / name) for name in names}
    for name, key in [('global.json', 'global_json_sha256'), ('NuGet.Config', 'config_sha256'),
                      ('Directory.Build.props', 'directory_build_props_sha256'), ('workload-manifest.json', 'workloadManifestSha256')]:
        if inputs[name] != prior[key]:
            raise ValueError('The ordinary build input changed: ' + name)
    original = (consumer / 'Program.cs').read_text()
    program = buffered_program(original)
    contracts = {}
    for name, start, end in [
        ('fixture_sql_parameters_expected_values', 'static async Task Initialize(', '    internal async ValueTask<Sample> Run('),
        ('dto_projection', '[DbTable("query_phases"', 'internal readonly record struct Counts('),
    ]:
        before, after = section(original, start, end), section(program, start, end)
        if before != after:
            raise ValueError('The buffered consumer changed a shared business contract: ' + name)
        contracts[name] = hashlib.sha256(before.encode()).hexdigest()
    output.mkdir(parents=True)
    for name in names:
        shutil.copyfile(consumer / name, output / name)
    shutil.copyfile(consumer / 'build-receipt.json', output / 'source-build-receipt.json')
    (output / 'Program.cs').write_text(program)
    (output / 'program.diff').write_text(''.join(difflib.unified_diff(original.splitlines(True), program.splitlines(True), 'source/Program.cs', 'buffered/Program.cs')))
    command = [value.replace(str(consumer), str(output)) for value in prior['command']]
    env = os.environ | {'NUGET_PACKAGES': str(consumer / 'packages')}
    with (output / 'publish.log').open('w') as log:
        status = subprocess.run(command, cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
    record = {'schema_version': 1, 'kind': 'buffered-query-phases', 'provider': prior['provider'],
              'package': prior['package'], 'version': prior['version'], 'package_sha256': prior['package_sha256'],
              'package_source_commit': source_commit, 'package_inputs': package_inputs,
              'source_consumer': str(consumer), 'source_receipt_sha256': sha(consumer / 'build-receipt.json'),
              'source_executable_sha256': prior['executable_sha256'], 'source_inputs': inputs,
              'business_contract_sha256': contracts, 'abi_wrapper_instrumentation': False,
              'first_row_observable': False, 'execution_performed': False, 'command': command, 'exit_code': status,
              'publish_log_sha256': sha(output / 'publish.log'), 'project_references': 0,
              'sources': {'Program.cs': sha(output / 'Program.cs')}, 'project_sha256': sha(output / project.name),
              'program_diff_sha256': sha(output / 'program.diff'),
              'tools': {p.name: sha(p) for p in (Path(__file__), HERE / 'Run.cs.in')}}
    try:
        if status == 0:
            assets = json.loads((output / 'obj/project.assets.json').read_text())
            if {key for key in assets['libraries'] if key.lower().startswith('korda.')} != {prior['package'] + '/' + prior['version']}:
                raise ValueError('Unexpected Korda package graph')
            executable = output / 'out/DbPerf.QueryPhases'
            if executable.read_bytes()[:4] != b'\x7fELF' or executable.with_suffix('.dll').exists():
                raise ValueError('Expected a standalone Native AOT ELF')
            record['executable_sha256'] = sha(executable)
            record['assets_sha256'] = sha(output / 'obj/project.assets.json')
            shutil.copyfile(output / 'obj/project.assets.json', output / 'project.assets.json')
            generated = output / 'obj/Release/net11.0/linux-x64/generated'
            if generated.is_dir():
                shutil.copytree(generated, output / 'generated')
            record['native_images'] = {str(p.relative_to(output / 'out')): sha(p) for p in (output / 'out').rglob('libnw_database_*.so')}
            if record['native_images'] != native:
                raise ValueError('The buffered consumer resolved different native images')
        record['ordinary_consumer_unchanged'] = (all(sha(consumer / name) == digest for name, digest in inputs.items())
            and sha(consumer / 'out/DbPerf.QueryPhases') == prior['executable_sha256']
            and {str(p.relative_to(consumer / 'out')): sha(p) for p in (consumer / 'out').rglob('libnw_database_*.so')} == native)
        record['package_unchanged'] = sha(package) == prior['package_sha256']
        if not record['ordinary_consumer_unchanged'] or not record['package_unchanged']:
            raise ValueError('The retained source consumer or package changed during the build')
    except Exception as error:
        record['post_validation_error'] = {'type': type(error).__name__, 'message': str(error)}
        raise
    finally:
        (output / 'build-receipt.json').write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps({'output': str(output), 'exit_code': status, 'source_commit': source_commit,
                      'execution_performed': False, 'abi_wrapper_instrumentation': False}))
    raise SystemExit(status)


if __name__ == '__main__':
    main()
