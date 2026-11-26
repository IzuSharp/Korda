#!/usr/bin/env python3
"""Build a separate ABI-counting overlay of an external AOT phase or workload consumer.

This never edits the provider binary or participates in CI. Timings from the
overlay are instrumented and must not be used as performance acceptance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

HERE = Path(__file__).resolve().parent


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError('The bound consumer changed: ' + old[:100])
    return text.replace(old, new)


def generate(source):
    binding = source / 'native/database/binding'
    records = [binding / 'signatures-v1.json', binding / 'query-command-v1.json']
    for name in ('cursor-pull-v1.json', 'result-batch-v1.json', 'command-completion-v1.json', 'find-shape-v1.json'):
        if (binding / name).exists():
            records.append(binding / name)
    declarations = [line for path in records for line in json.loads(path.read_text())['signatures']]
    headers = ['korda_database_binding_v1.h'] + [json.loads(path.read_text())['header'].split('/')[-1]
                                               for path in records[1:]]
    names, bodies, resolve = [], [], []
    for index, line in enumerate(declarations):
        result, name, parameters = re.fullmatch(r'(\w+) NW_CDECL (\w+)\((.*)\);', line).groups()
        args = ', '.join(re.search(r'(\w+)$', item.strip()).group(1) for item in parameters.split(','))
        names.append(name)
        status_name = 'probe_status'
        while status_name in args.split(', '):
            status_name += '_'
        prefix = '' if result == 'void' else result + ' ' + status_name + ' = '
        suffix = '' if result == 'void' else f'return {status_name};'
        if result == 'nw_status':
            suffix = (f'if ({status_name} == NW_PENDING) atomic_fetch_add(&pending[{index}], 1); '
                      f'if ({status_name} == NW_OK) atomic_fetch_add(&ok[{index}], 1); ' + suffix)
        bodies.append(f'''static {result} NW_CDECL probe_{name}({parameters})
{{
    atomic_fetch_add(&calls[{index}], 1);
    {prefix}(({result} (NW_CDECL*)({parameters}))atomic_load(&targets[{index}]))({args});
    {suffix}
}}''')
        resolve.append(f'''    if (!strcmp(name, "{name}")) {{ atomic_store(&targets[{index}], address); return (void*)&probe_{name}; }}''')
    preamble = '\n'.join('#include "' + name + '"' for name in headers)
    code = (HERE / 'probe.c.in').read_text()
    for marker, value in {'@HEADERS@': preamble, '@COUNT@': str(len(names)),
                          '@BODIES@': '\n'.join(bodies), '@RESOLVE@': '\n'.join(resolve)}.items():
        code = replace_once(code, marker, value)
    return code, names, records


def instrument(consumer, output, kind):
    path = 'Program.cs' if kind == 'query-phases' else 'Measurement.cs'
    program = (consumer / path).read_text()
    if 'ulong? CommandCompletions);' in program:
        program = replace_once(program, 'ulong? CommandCompletions);', 'ulong? CommandCompletions, FfiCounters Ffi);')
        program = replace_once(program, '_commandsSupported ? count(9) : null);',
                               '_commandsSupported ? count(9) : null, FfiProbe.Read());')
    else:
        program = replace_once(program, 'ulong? PullChannels);', 'ulong? PullChannels, FfiCounters Ffi);')
        program = replace_once(program, '_pullSupported ? count(8) : null);',
                               '_pullSupported ? count(8) : null, FfiProbe.Read());')
    if kind == 'query-phases':
        program = replace_once(program, '"Local phase diagnostic. Construction',
                               '"Instrumented ABI diagnostic; no timing or allocation acceptance credit. Construction')
    else:
        program = replace_once(program, 'allocationProfile is null, allocationProfile?.CreateReceipt())',
                               'false, allocationProfile?.CreateReceipt())')
        program = replace_once(program, '    public long OracleFailuresBefore { get; init; }',
                               '    public bool Instrumented => true;\n'
                               '    public bool TimingCredit => false;\n'
                               '    public bool AllocationCredit => false;\n'
                               '    public long OracleFailuresBefore { get; init; }')
        entry = replace_once((consumer / 'Program.cs').read_text(), 'var options = Options.Parse(args);',
                             'var options = Options.Parse(args);\n'
                             'if (options.Initialize || options.LifecycleChecks || options.RecoveryChecks\n'
                             '    || options.EmptyDriver || options.AllocationProfile)\n'
                             '    throw new ArgumentException("The ABI diagnostic requires a real steady workload; '
                             'use the ordinary consumer for initialization and other diagnostics.");')
        (output / 'Program.cs').write_text(entry)
    (output / path).write_text(program)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--consumer', type=Path, required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    consumer, source, output = args.consumer.resolve(), args.source.resolve(), args.output.resolve()
    if output.exists():
        raise ValueError('Use a fresh output directory; retained diagnostics are immutable')
    prior = json.loads((consumer / 'build-receipt.json').read_text())
    kind = prior.get('kind')
    if (kind not in ('query-phases', 'workload') or prior.get('exit_code') != 0
            or prior.get('project_references') != 0 or not prior.get('package', '').startswith('Korda.Database.')):
        raise ValueError('A successful external Korda PackageReference phase or workload consumer is required')
    executable = 'DbPerf.QueryPhases' if kind == 'query-phases' else 'DbPerf.Korda'
    if sha(consumer / 'out' / executable) != prior['executable_sha256']:
        raise ValueError('The source consumer executable changed')
    inputs = {p.name: sha(p) for p in consumer.glob('*.cs')}
    if inputs != prior['sources']:
        raise ValueError('The ordinary consumer sources differ from its build receipt')
    input_files = {p.name: sha(p) for p in consumer.iterdir() if p.is_file()
                   and p.suffix in ('.cs', '.csproj', '.json', '.props', '.Config')}
    project = Path(prior['command'][2])
    if project.parent != consumer or sha(project) != prior['project_sha256']:
        raise ValueError('The ordinary consumer project differs from its build receipt')
    package_id, version = prior['package'].lower(), prior['version'].lower()
    package = consumer / 'packages' / package_id / version / f'{package_id}.{version}.nupkg'
    if sha(package) != prior['package_sha256']:
        raise ValueError('The ordinary consumer cached package changed')
    original_native = {p.name: sha(p) for p in (consumer / 'out').rglob('libnw_database_*.so')}
    if original_native != prior['native_images']:
        raise ValueError('The ordinary consumer native images changed')
    output.mkdir(parents=True)
    for path in consumer.iterdir():
        if path.is_file() and path.suffix in ('.cs', '.csproj', '.json', '.props', '.Config'):
            if path.name != 'build-receipt.json':
                shutil.copyfile(path, output / path.name)
    shutil.copyfile(consumer / 'build-receipt.json', output / 'source-build-receipt.json')
    code, names, inventory = generate(source)
    (output / 'probe.c').write_text(code)
    instrument(consumer, output, kind)
    (output / 'FfiProbe.cs').write_text('''using System.Runtime.InteropServices;
internal sealed record FfiCounters(ulong[] Calls, ulong[] PendingReturns, ulong[] OkReturns,
    ulong ProviderPollCalls, ulong ProviderPollTimeouts, ulong ProviderPollErrors, ulong ProviderPollNanoseconds);
internal static unsafe class FfiProbe
{
    private static readonly nint Module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libcursor_probe.so"));
    private static readonly delegate* unmanaged[Cdecl]<uint, uint, ulong> Count =
        (delegate* unmanaged[Cdecl]<uint, uint, ulong>)NativeLibrary.GetExport(Module, "db_perf_ffi_count");
    internal static FfiCounters Read()
    {
        var calls = new ulong[@COUNT@]; var pending = new ulong[@COUNT@]; var ok = new ulong[@COUNT@];
        for (uint i = 0; i < calls.Length; i++)
        { calls[i] = Count(0, i); pending[i] = Count(1, i); ok[i] = Count(3, i); }
        if (calls[@SOURCE_CREATE@] == 0)
            throw new InvalidOperationException("Launch this diagnostic with its LD_PRELOAD before ABI resolution.");
        return new(calls, pending, ok, Count(2, 0), Count(2, 1), Count(2, 2), Count(2, 3));
    }
}
'''.replace('@COUNT@', str(len(names))).replace('@SOURCE_CREATE@', str(names.index('nw_db_source_create_v1'))))
    compiler = Path(shutil.which(os.environ.get('CC', 'clang'))).resolve()
    lock = json.loads((source / 'native/dependencies.lock.json').read_text())
    if sha(compiler) != lock['toolchain']['llvm']['executables']['clang']:
        raise ValueError('The diagnostic compiler must match the locked product compiler')
    out = output / 'out'
    out.mkdir()
    compile_command = [str(compiler), '-std=c17', '-O2', '-g', '-fPIC', '-shared', '-Wall', '-Wextra',
                       '-Werror', '-I' + str(source / 'native/database/binding'), str(output / 'probe.c'),
                       '-ldl', '-o', str(out / 'libcursor_probe.so')]
    command = [value.replace(str(consumer), str(output)) for value in prior['command']]
    env = os.environ | {'NUGET_PACKAGES': str(consumer / 'packages')}
    results = []
    for label, argv in [('shim', compile_command), ('consumer', command)]:
        with (output / (label + '.log')).open('w') as log:
            status = subprocess.run(argv, cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
        results.append({'label': label, 'argv': argv, 'exitCode': status, 'logSha256': sha(output / (label + '.log'))})
        if status:
            break
    record = {'schemaVersion': 1, 'diagnostic': 'actual-managed-to-native-ABI-counts', 'consumerKind': kind,
              'instrumented': True, 'TimingComparisonEligible': False, 'timingCredit': False,
              'allocationCredit': False, 'sourceConsumer': str(consumer), 'sourceReceiptSha256': sha(consumer / 'build-receipt.json'),
              'sourceExecutableSha256': prior['executable_sha256'], 'sourcePackageSha256': prior['package_sha256'],
              'sourceNativeImages': prior['native_images'], 'symbols': names,
              'abiSource': str(source),
              'abiSourceCommit': subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip(),
              'scope': 'Wrappers returned during actual dlsym resolution count nw_db_* calls and exact Pending/OK returns; already-resolved pointers are not rebound. Provider-originated poll calls are OS waits, not physical RTT or counts of mandatory protocol replies. Native-to-managed notifications are not intercepted.',
              'inventories': {str(path.relative_to(source)): sha(path) for path in inventory},
              'tools': {path.name: sha(path) for path in (Path(__file__), HERE / 'probe.c.in')},
              'compilerSha256': sha(compiler), 'commands': results,
              'inputSources': inputs,
              'inputFiles': input_files,
              'overlaySources': {p.name: sha(p) for p in output.glob('*.cs')},
              'outputs': {p.name: sha(p) for p in out.iterdir() if p.is_file()},
              'executable': str(out / executable),
              'runEnvironment': {'LD_PRELOAD': str(out / 'libcursor_probe.so')}}
    actual_native = {p.name: sha(p) for p in out.rglob('libnw_database_*.so')}
    record['nativeImagesUnchanged'] = actual_native == prior['native_images']
    record['packageUnchanged'] = sha(package) == prior['package_sha256']
    record['ordinaryConsumerUnchanged'] = (
        all(sha(consumer / name) == digest for name, digest in input_files.items())
        and sha(consumer / 'out' / executable) == prior['executable_sha256']
        and {p.name: sha(p) for p in (consumer / 'out').rglob('libnw_database_*.so')} == original_native)
    (output / 'diagnostic-build.json').write_text(json.dumps(record, indent=2) + '\n')
    if (any(item['exitCode'] for item in results) or not record['nativeImagesUnchanged']
            or not record['packageUnchanged'] or not record['ordinaryConsumerUnchanged']):
        raise SystemExit('Diagnostic build failed; logs and receipt retained')
    print(json.dumps({'output': str(output), 'symbols': len(names), 'nativeImagesUnchanged': True, 'timingCredit': False}))


if __name__ == '__main__':
    main()
