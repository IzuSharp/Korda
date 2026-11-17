#!/usr/bin/env python3
"""B18 Linux x64 assembly receipt over an explicitly frozen native asset.

Native component tests are referenced by their accepted immutable receipt. This
runner checks the assembled ABI, managed API, actual JIT/NativeAOT transports and
ordinary package consumer without rebuilding Rust or repeating earlier tasks.
"""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess
import time


ROOT = Path(__file__).resolve().parents[2]
DESIGN_COMMIT = 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
SHARED_INPUTS = ('global.json', 'Directory.Build.props', 'Directory.Build.targets',
                 'NuGet.Config', 'LICENSE')
PRODUCT_PREFIXES = (
    'src/Protocols.Grpc/', 'src/Server.Application/', 'src/Server.Native/',
    'src/Server.Streaming/', 'src/Server.Diagnostics/', 'src/NativeWeave.Core/',
    'src/Interop.Abi/', 'src/Interop.Negotiation/', 'src/Interop.Errors/',
    'src/Interop.Lifetime/', 'src/Interop.Memory/', 'src/Interop.Callbacks/',
    'src/Interop.Async/', 'src/Interop.Execution/', 'src/Build.Integration/',
    'src/Packaging.Linux/', 'src/Packaging.Release/')
OWNED_PREFIXES = ('tests/Protocols.Grpc/', 'samples/api/protocols-grpc/',
                  'tests/Server.Native/')
NATIVE_PATHS = ('native', '.cargo', 'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml')
FLAGS = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + '\n')


def git(*arguments):
    return subprocess.check_output(['git', *arguments], cwd=ROOT)


def commit(value):
    return git('rev-parse', value + '^{commit}').decode().strip()


def committed_inputs(revision, paths, select=lambda name: True):
    names = git('ls-tree', '-rz', '--name-only', revision, '--', *paths).decode().split('\0')
    return {name: hashlib.sha256(git('show', revision + ':' + name)).hexdigest()
            for name in names if name and select(name)}


def current_inputs():
    names = git('ls-files', '-z').decode().split('\0')
    return {name: sha(ROOT / name) for name in names if name and (
        name.startswith(PRODUCT_PREFIXES + OWNED_PREFIXES)
        or name in SHARED_INPUTS
        or name == 'docs/guides/protocols-grpc.md')}


def package_input(name):
    # Producer packages are built in JIT Release; new maintainer AOT lockfiles
    # do not change those packages. The independent consumer has its own locks.
    if name.endswith('.aot.lock.json') or name.startswith('src/Server.Application/Hosting/'):
        return False
    if name in SHARED_INPUTS:
        return True
    if name.startswith('samples/api/protocols-grpc/'):
        return Path(name).suffix in ('.cs', '.proto')
    return Path(name).suffix in ('.cs', '.csproj', '.props', '.targets', '.json', '.py')


def binary_inputs(folder):
    return {str(path.relative_to(folder)): sha(path)
            for path in sorted(folder.rglob('*')) if path.is_file()}


class Receipt:
    def __init__(self, output):
        self.output = output
        self.logs = output / 'logs'
        self.logs.mkdir()
        self.commands = []

    def run(self, label, command, timeout=900):
        command = list(map(str, command))
        path = self.logs / (label + '.log')
        started = time.monotonic()
        print(label, flush=True)
        with path.open('w') as log:
            try:
                code = subprocess.run(command, cwd=ROOT, stdout=log,
                                      stderr=subprocess.STDOUT, timeout=timeout).returncode
            except subprocess.TimeoutExpired:
                log.write('\nVerification command exceeded its finite timeout.\n')
                code = 124
        self.commands.append(dict(label=label, command=command, exit_code=code,
                                  seconds=round(time.monotonic() - started, 3),
                                  log=str(path.relative_to(self.output)), log_sha256=sha(path)))
        write_json(self.output / 'commands.json', self.commands)
        require(code == 0, label + ' failed; inspect ' + str(path))
        return path.read_text()


def accepted_native(receipt_path, native_inputs, output):
    receipt_path = receipt_path.resolve()
    value = json.loads(receipt_path.read_text())
    require(value['task'] == 'B18' and value['platform'] == 'linux-x64'
            and value['status'] == 'passed', 'The native component receipt has not passed B18 Linux x64.')
    leaf = {name: digest for name, digest in native_inputs.items()
            if name.startswith('native/protocols/grpc/')
            and (name.endswith('.rs') or name.endswith('/Cargo.toml'))}
    require(leaf and all(value['source_sha256'].get(name) == digest
                        for name, digest in leaf.items()),
            'The supplied native build differs from the accepted gRPC component source.')
    require(len(value['server_exports']) == 48, 'The native component export inventory is incomplete.')
    destination = output / 'accepted-native-component'
    destination.mkdir()
    shutil.copy2(receipt_path, destination / 'result.json')
    for entry in value['commands']:
        require(entry['exit_code'] == 0, 'The native receipt contains a failed command.')
        log = receipt_path.parent / entry['log']
        require(sha(log) == entry['sha256'], 'The native component log changed: ' + str(log))
        shutil.copy2(log, destination / log.name)
    return dict(path=str(receipt_path), sha256=sha(receipt_path),
                retained_path=str(destination / 'result.json'),
                source_commit=value['commit'], native_sha256=value['native']['sha256'],
                matching_grpc_source_files=len(leaf), commands=len(value['commands']),
                scope='Accepted native gRPC component evidence is reused. Its whole Server binary '
                      'can have a different MCP composition; the supplied product asset is checked separately.')


def package_receipt(path, source, native_source, native_hash, native_inputs, output):
    path = path.resolve()
    value = json.loads(path.read_text())
    require(value['task'] == 'B18' and value['platform'] == 'linux-x64', 'Wrong package receipt scope.')
    require(value['native_sha256'] == native_hash and value['native_source_commit'] == native_source,
            'Package consumption used a different native binary or native build source.')
    require(value['native_input_sha256'] == native_inputs,
            'The package receipt native source inventory differs from the requested build source.')
    for key in ('package_reference_only', 'no_parent_repository_imports', 'isolated_nuget_cache',
                'no_accessible_rust_toolchain', 'all_four_cardinalities_jit_aot_http2_tls',
                'whole_call_deadline_after_headers', 'binary_metadata_and_status_trailers',
                'explicit_aot_no_dynamic_code', 'no_root_descriptors_or_trim_suppressions',
                'ordinary_proto_dto_handler_edit_jit_aot_same_native'):
        require(value[key] is True, 'Incomplete package proof: ' + key)
    require(value['rust_trap_fired'] is False and value['static_protobuf_services'] == 2,
            'The ordinary two-service/no-Rust proof is incomplete.')
    paths = PRODUCT_PREFIXES + SHARED_INPUTS + ('samples/api/protocols-grpc/',)
    before = committed_inputs(value['source_commit'], paths, package_input)
    now = committed_inputs(source, paths, package_input)
    changed = sorted(name for name in before.keys() | now.keys() if before.get(name) != now.get(name))
    require(not changed, 'Package producer or sample inputs changed since consumption: ' + ', '.join(changed))
    command_path = path.parent / 'commands.json'
    require(sha(command_path) == value['command_receipt_sha256'], 'The package command receipt changed.')
    commands = json.loads(command_path.read_text())
    required = {'consumer-locked-restore', 'consumer-build', 'consumer-jit-minimal',
                'consumer-jit-advanced-clear', 'consumer-jit-advanced-tls',
                'consumer-aot-locked-restore', 'consumer-aot-publish', 'consumer-aot-minimal',
                'consumer-aot-advanced-clear', 'consumer-aot-advanced-tls', 'edited-jit-restore',
                'edited-jit-build', 'edited-jit-execute', 'edited-aot-restore',
                'edited-aot-publish', 'edited-aot-execute'}
    require(required <= {entry['label'] for entry in commands}, 'The package execution matrix is incomplete.')
    destination = output / 'accepted-package'
    destination.mkdir()
    (destination / 'logs').mkdir()
    shutil.copy2(path, destination / 'summary.json')
    shutil.copy2(command_path, destination / 'commands.json')
    for entry in commands:
        log = path.parent / 'logs' / (entry['label'] + '.log')
        require(entry['exit_code'] == 0 and sha(log) == entry['log_sha256'],
                'A package execution or log is invalid: ' + entry['label'])
        shutil.copy2(log, destination / 'logs' / log.name)
    for package in value['packages'].values():
        require(sha(package['path']) == package['sha256'], 'A consumed package changed.')
        require(all(version.startswith('[') and version.endswith(']')
                    for version in package['exact_dependencies'].values()),
                'A package dependency is not an exact A14 edge.')
    for record in value['selected_output_records'].values():
        require(record['sha256'] == native_hash and record['rid'] == 'linux-x64',
                'The SDK-selected native output identity differs.')
    for mode, executable in (('jit', 'Consumer.dll'), ('aot', 'Consumer'),
                             ('edited_jit', 'Consumer.dll'), ('edited_aot', 'Consumer')):
        snapshot = path.parent / 'snapshots' / mode.replace('_', '-')
        binary = json.loads((snapshot / 'binary-inputs.json').read_text())
        require(binary[executable] == value['executable_sha256'][mode],
                'The package executable identity differs from its before-execution snapshot.')
        shutil.copytree(snapshot, destination / 'snapshots' / mode.replace('_', '-'))
        retained = {'aot': 'publish', 'edited_jit': 'bin/Release/net11.0',
                    'edited_aot': 'publish-edited'}.get(mode)
        if retained is not None:
            current = binary_inputs(path.parent / 'consumer' / retained)
            if mode == 'edited_jit':
                # A later AOT publish rewrites its sibling RID build directory
                # beneath net11.0. These files are not JIT executable inputs;
                # the full before/after-JIT inventory remains in its receipt.
                current = {name: digest for name, digest in current.items()
                           if not name.startswith('linux-x64/')}
                binary = {name: digest for name, digest in binary.items()
                          if not name.startswith('linux-x64/')}
            require(current == binary,
                    'The retained package consumer inputs changed: ' + mode)
    require(value['executable_sha256']['jit'] != value['executable_sha256']['edited_jit']
            and value['executable_sha256']['aot'] != value['executable_sha256']['edited_aot'],
            'The ordinary DTO/handler edit did not change both consumer executables.')
    return dict(path=str(path), sha256=sha(path), retained_path=str(destination / 'summary.json'),
                source_commit=value['source_commit'], matching_producer_inputs=len(now),
                commands=len(commands), executable_sha256=value['executable_sha256'],
                baseline_jit_retention='The intentional edited build replaced its output; '
                    'its immutable source/lock snapshot and before-execution hash receipt are retained.',
                edited_jit_retention='JIT executable/dependencies/native inputs remain byte-identical. '
                    'The sibling linux-x64 AOT compilation directory was updated by the later edited publish.',
                scope=value['scope'])


def run_managed(receipt, label, command, directory, native_hash, aot):
    before = binary_inputs(directory)
    write_json(receipt.output / (label + '-inputs.json'), before)
    text = receipt.run(label, command, timeout=240)
    match = re.search(r'B18 actual Rust/Tonic: assertions=(\d+); managed retirements=(\d+); '
                      r'dynamic=(True|False); native=([0-9a-f]{64})', text)
    require(match is not None, 'The actual gRPC test completion record is absent.')
    require(match[4] == native_hash, 'The managed test loaded another native asset.')
    require(match[3] == ('False' if aot else 'True'), 'The requested execution mode was not exercised.')
    require(binary_inputs(directory) == before, 'The executable inputs changed during actual execution.')
    executable = 'NativeWeave.Server.Grpc.Tests' + ('' if aot else '.dll')
    return dict(assertions=int(match[1]), managed_retirements=int(match[2]),
                dynamic_code_supported=match[3] == 'True', executable_sha256=before[executable],
                binary_inventory_sha256=sha(receipt.output / (label + '-inputs.json')))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--design', type=Path, required=True)
    parser.add_argument('--sdk', type=Path, required=True,
                        help='Pinned DOTNET_ROOT containing packs/ and sdk/.')
    parser.add_argument('--native', type=Path, required=True)
    parser.add_argument('--expected-native-sha256', required=True)
    parser.add_argument('--native-source-commit', required=True,
                        help='The exact commit used to build the supplied native asset.')
    parser.add_argument('--native-component-receipt', type=Path, required=True)
    parser.add_argument('--package-summary', type=Path,
                        help='Reuse immutable ordinary-consumer proof after validating unchanged producer inputs.')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    receipt = Receipt(output)
    result = dict(task='B18', platform='linux-x64', global_acceptance=False, status='running',
                  scope='Assembled Linux x64 gRPC component; accepted native tests are reused. '
                        'No other-platform, pristine-machine or public-release claim.',
                  commands=receipt.commands)
    try:
        require(platform.system() == 'Linux' and platform.machine() == 'x86_64', 'Linux x64 is required.')
        require(re.fullmatch('[0-9a-fA-F]{64}', args.expected_native_sha256), 'Invalid native SHA256.')
        source = receipt.run('source', ['git', 'rev-parse', 'HEAD']).strip()
        require(not receipt.run('clean-source', ['git', 'status', '--porcelain', '--untracked-files=all']).strip(),
                'Commit source before formal acceptance.')
        initial = current_inputs()
        native_source = commit(args.native_source_commit)
        native_inputs = committed_inputs(native_source, NATIVE_PATHS)
        result.update(source_commit=source, source_sha256=initial,
                      native_source_commit=native_source, native_input_sha256=native_inputs,
                      native_cargo_lock_sha256=native_inputs['Cargo.lock'])
        design = receipt.run('design', ['git', '-C', args.design, 'rev-parse', 'HEAD']).strip()
        require(design == DESIGN_COMMIT, 'Design must match the Plan source-driven baseline.')
        require(not subprocess.check_output(['git', '-C', args.design, 'status', '--porcelain'], text=True).strip(),
                'The pinned Design checkout is modified.')
        result['design_commit'] = design
        dotnet = receipt.run('dotnet-version', ['dotnet', '--version']).strip()
        rust = receipt.run('rust-version', ['rustc', '--version']).strip()
        require(dotnet == '11.0.100' and rust.startswith('rustc 1.100.0 '), 'Use the fixed repository toolchains.')
        result['toolchains'] = dict(dotnet=dotnet, rust=rust,
                                   sdk=str(args.sdk.resolve()), python=platform.python_version())
        supplied = args.native.resolve()
        native_hash = sha(supplied)
        require(native_hash == args.expected_native_sha256.lower(), 'The supplied native asset is not the reviewed asset.')
        (output / 'native').mkdir()
        native = output / 'native/libnativeweave_server.so'
        shutil.copy2(supplied, native)
        require(sha(native) == native_hash, 'Native asset copy changed.')
        result['native'] = dict(path=str(native), sha256=native_hash, bytes=native.stat().st_size)
        result['accepted_native_component'] = accepted_native(args.native_component_receipt, native_inputs, output)

        receipt.run('pinned-native-wire', ['python3', 'tests/Server.Native/generate.py', '--check'])
        receipt.run('exact-native-layout', ['python3', 'tests/Server.Native/layout.py', '--check'])
        header = ROOT / 'native/server/host/include/nativeweave_server_api_v1.h'
        require(sha(header) == native_inputs[str(header.relative_to(ROOT))], 'The checked header differs from the native build input.')
        expected = set(re.findall(r'(?:uint32_t|void) NW_CDECL (nw_server_\w+)\(', header.read_text()))
        symbols = receipt.run('native-exports', ['nm', '-D', '--defined-only', native])
        linked = {line.split()[-1] for line in symbols.splitlines()
                  if line.split() and line.split()[-1].startswith('nw_server_')}
        require(len(expected) == 48 and linked == expected, 'The complete frozen Server export surface differs.')
        layout = json.loads((ROOT / 'tests/Server.Native/Bindings/exact-layout.json').read_text())
        records = sum(name.endswith('.size') for name in layout)
        offsets = sum(not name.endswith(('.size', '.align')) for name in layout)
        require(records == 8 and offsets == 103, 'The pinned record/offset inventory differs.')
        binding = json.loads((ROOT / 'tests/Server.Native/Bindings/binding.json').read_text())
        coverage = json.loads((ROOT / 'tests/Server.Native/Bindings/coverage.json').read_text())
        require(binding['provenance']['adapter_sha256'] == sha(ROOT / 'tests/Server.Native/Bindings/adapter-projection.rs')
                and coverage['header_sha256'] == sha(header)
                and coverage['source_sha256'] == native_inputs['native/server/host/src/exports.rs']
                and set(coverage['full_exact_exports']) == expected, 'The recorded binding provenance is stale.')
        result['abi'] = dict(exports=sorted(linked), records=records, offsets=offsets)

        project = ROOT / 'tests/Protocols.Grpc/NativeWeave.Server.Grpc.Tests.csproj'
        asset = '-p:NativeWeaveServerAssetPath=' + str(native)
        restore = ['--locked-mode', '--ignore-failed-sources', '--disable-parallel', *FLAGS]
        receipt.run('managed-jit-restore', ['dotnet', 'restore', project, *restore])
        receipt.run('managed-jit-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', asset, *FLAGS])
        jit = output / 'jit'
        shutil.copytree(project.parent / 'bin/Release/net11.0', jit)
        result['jit'] = run_managed(receipt, 'actual-jit', ['dotnet', jit / 'NativeWeave.Server.Grpc.Tests.dll'], jit, native_hash, False)
        api = ROOT / 'tests/Protocols.Grpc/ApiCheck/ApiCheck.csproj'
        receipt.run('api-restore', ['dotnet', 'restore', api, *restore])
        receipt.run('api-build', ['dotnet', 'build', api, '-c', 'Release', '--no-restore', *FLAGS])
        api_text = receipt.run('public-api-xml', ['dotnet', api.parent / 'bin/Release/net11.0/NativeWeave.Protocols.Grpc.ApiCheck.dll', ROOT, args.design, args.sdk])
        match = re.search(r'PASS B18 (\d+) exact compiled declarations; public API, XML, reference and (\d+) negative consumers match the pinned Design\.', api_text)
        require(match is not None and (int(match[1]), int(match[2])) == (71, 5), 'The B18 API/XML/negative consumer oracle did not pass.')
        result['public_api'] = dict(symbols=int(match[1]), compile_negative_fixtures=int(match[2]))
        receipt.run('managed-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', *restore])
        aot = output / 'aot'
        receipt.run('managed-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', aot, asset, *FLAGS])
        result['aot'] = run_managed(receipt, 'actual-aot', [aot / 'NativeWeave.Server.Grpc.Tests', '--require-aot'], aot, native_hash, True)

        package_summary = args.package_summary
        if package_summary is None:
            receipt.run('independent-package', ['python3', 'tests/Protocols.Grpc/package_probe.py',
                        '--output', output / 'package', '--native', native,
                        '--expected-native-sha256', native_hash, '--native-source-commit', native_source], timeout=1800)
            package_summary = output / 'package/summary.json'
        result['package'] = package_receipt(package_summary, source, native_source, native_hash, native_inputs, output)
        require(current_inputs() == initial and commit('HEAD') == source, 'Committed verification inputs changed.')
        require(not git('status', '--porcelain', '--untracked-files=all').decode().strip(), 'Verification mutated source.')
        require(sha(native) == sha(supplied) == native_hash, 'The frozen/supplied native asset changed.')
        result.update(status='passed', source_clean=True, command_receipt_sha256=sha(output / 'commands.json'))
    except Exception as error:
        result.update(status='failed', failure=str(error))
        raise
    finally:
        result['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        write_json(output / 'result.json', result)
    print('PASS immutable B18 Linux x64 assembled JIT/NativeAOT, API/XML and ordinary NuGet receipt.', flush=True)


if __name__ == '__main__':
    main()
