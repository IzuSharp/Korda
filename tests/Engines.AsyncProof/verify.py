#!/usr/bin/env python3
"""Run the Linux A18 real HTTP service component and independent precompiled consumer."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time
from package_probe import consume

ROOT = Path(__file__).resolve().parents[2]
OWNED = ['src/Engines.AsyncProof', 'tests/Engines.AsyncProof', 'native/engines/async-proof',
         'samples/api/engines-asyncproof', 'docs/guides/engines-asyncproof.md']
INPUTS = OWNED + ['src/NativeWeave.Core', 'src/Interop.Abi', 'src/Interop.Lifetime', 'src/Interop.Memory',
    'src/Interop.Errors', 'src/Interop.Negotiation', 'src/Interop.Callbacks', 'src/Interop.Async',
    'native/interop/abi', 'native/interop/errors', 'native/interop/callbacks', 'native/interop/async',
    'src/Generators.Model', 'src/Generators.CSharp', 'src/Generators.Rust', 'src/Build.Integration/ApiReference',
    'Cargo.toml', 'Cargo.lock', 'global.json', 'rust-toolchain.toml', 'Directory.Build.props', 'Directory.Build.targets']

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--packaging-root', type=Path, default=ROOT)
    parser.add_argument('--prepare', action='store_true')
    parser.add_argument('--a17-result', type=Path, required=True)
    args = parser.parse_args()
    if platform.system() != 'Linux' or platform.machine() != 'x86_64': parser.error('Linux x64 execution required.')
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy(); env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    receipt = dict(task='A18', platform='linux-x64', passed=False, preparation_only=args.prepare, commands=commands,
        design_commit='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9', independent_review='pending',
        scope='Real Hyper/Tokio HTTP service, static foreign Handler, A06/A07 ownership, generated boundary and independent local package.',
        limitations=['Windows not executed', 'No final Server API/production soak/performance-superiority claim',
                    'Rust invocation traps are not a pristine no-Rust machine', 'No public package release'],
        common_changes=['A13 workspace member', 'Core internal friend for generated A03 adapter; no new common lifecycle/capability'])
    def run(label, command, cwd=ROOT, environment=env, expected=0, contains=None):
        start = time.monotonic()
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=environment, capture_output=True, timeout=600)
        for suffix, data in [('stdout', result.stdout), ('stderr', result.stderr)]: (output / (label + '.' + suffix)).write_bytes(data)
        commands.append(dict(label=label, argv=list(map(str, command)), exit=result.returncode, expected_exit=expected,
            seconds=round(time.monotonic() - start, 3), stdout_sha256=hashlib.sha256(result.stdout).hexdigest(), stderr_sha256=hashlib.sha256(result.stderr).hexdigest()))
        (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        print(label, result.returncode, flush=True)
        text = result.stdout.decode(errors='replace')
        error = result.stderr.decode(errors='replace')
        if result.returncode != expected or contains is not None and contains not in text + error:
            raise RuntimeError(label + '\n' + text[-3000:] + error[-3000:])
        return text
    try:
        receipt['source_commit'] = run('source-commit', ['git', 'rev-parse', 'HEAD']).strip()
        receipt['source_dirty'] = bool(run('source-status', ['git', 'status', '--porcelain']).strip())
        paths = run('source-files', ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '--', *INPUTS]).splitlines()
        receipt['source_sha256'] = {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}
        if not args.prepare and receipt['source_dirty']: raise RuntimeError('Commit the component before formal verification.')
        if run('sdk', ['dotnet', '--version']).strip() != '11.0.100': raise RuntimeError('Pinned SDK required.')
        if not run('rustc', ['rustc', '-vV']).startswith('rustc 1.100.0 '): raise RuntimeError('Pinned rustc required.')
        run('native-format', ['cargo', 'fmt', '-p', 'nativeweave-engine-asyncproof', '--', '--check'])
        run('native-tests', ['cargo', 'test', '--offline', '--locked', '--release', '-p', 'nativeweave-engine-asyncproof', '--', '--nocapture'])
        run('native-build', ['cargo', 'build', '--offline', '--locked', '--release', '-p', 'nativeweave-engine-asyncproof'])
        run('native-clippy', ['cargo', 'clippy', '--offline', '--locked', '-p', 'nativeweave-engine-asyncproof', '--all-targets', '--', '-D', 'warnings'])
        native = ROOT / 'target/release/libnativeweave_engine_asyncproof.so'
        native_before = sha(native)
        direct = ROOT / 'target/release/nativeweave-asyncproof-direct'
        tree = run('native-dependency-tree', ['cargo', 'tree', '--offline', '--locked', '-p', 'nativeweave-engine-asyncproof', '-e', 'normal,features'])
        if not all(name in tree.lower() for name in ['tokio', 'hyper', 'bytes']): raise RuntimeError('Real native async dependencies missing.')
        if 'conformance' in tree: raise RuntimeError('Proof engine must not link Core fixture features.')
        run('inventory-build', ['cargo', 'build', '--offline', '--locked', '--manifest-path', 'src/Generators.Model/native-inventory/Cargo.toml'])
        parsed = output / 'parsed.json'
        run('parse-adapter', [ROOT / 'src/Generators.Model/native-inventory/target/debug/nativeweave-binding-inventory', 'native/engines/async-proof/src/lib.rs', parsed])
        linked = output / 'linked.txt'
        linked.write_text('\n'.join(line.split()[-1] for line in run('linked-exports', ['nm', '-D', '--defined-only', native]).splitlines() if line.strip()) + '\n')
        common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
        for label, project in [('csharp-generator', 'src/Generators.CSharp/NativeWeave.Generators.CSharp.csproj'), ('rust-generator', 'src/Generators.Rust/NativeWeave.Generators.Rust.csproj')]:
            run(label + '-restore', ['dotnet', 'restore', project, '--locked-mode', *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
        generation_args = ['native/engines/async-proof/binding.json', parsed, 'native/engines/async-proof/src/lib.rs', linked]
        generated_cs = output / 'generated-csharp'; generated_rs = output / 'generated-rust'
        run('generate-csharp', ['dotnet', ROOT / 'src/Generators.CSharp/bin/Release/net11.0/NativeWeave.Generators.CSharp.dll', *generation_args, 'nativeweave_engine_asyncproof', generated_cs, 'NativeWeave.Engines.AsyncProof.Generated'])
        run('generate-rust', ['dotnet', ROOT / 'src/Generators.Rust/bin/Release/net11.0/NativeWeave.Generators.Rust.dll', *generation_args, generated_rs])
        for generated, destination in [(generated_cs, ROOT / 'src/Engines.AsyncProof/Generated'), (generated_rs, ROOT / 'native/engines/async-proof/generated')]:
            for path in generated.iterdir():
                target = destination / path.name
                if args.prepare: shutil.copy2(path, target)
                elif path.read_bytes() != target.read_bytes(): raise RuntimeError('Generated binding is stale: ' + str(target))
        run('c-layout-compile', ['cc', '-std=c11', '-Wall', '-Wextra', '-Werror', 'tests/Engines.AsyncProof/probe.c', '-I', generated_rs, '-L', native.parent,
            '-lnativeweave_engine_asyncproof', '-Wl,-rpath,' + str(native.parent), '-o', output / 'c-probe'])
        run('c-layout-execute', [output / 'c-probe'])
        corpus = output / 'direct-corpus'; corpus.mkdir()
        for route in [7, 99]:
            direct_result = run('direct-http-' + str(route), [direct, route])
            document = json.loads(direct_result)
            if document['live_resources'] != 0: raise RuntimeError('Direct native oracle did not retire.')
            (corpus / (str(route) + '.json')).write_text(direct_result)
        restore = [] if args.prepare else ['--locked-mode']
        artifacts = {'native': {'sha256': native_before, 'bytes': native.stat().st_size}, 'direct_rust': {'sha256': sha(direct), 'bytes': direct.stat().st_size}}
        for label, project, assembly in [
            ('tests', 'tests/Engines.AsyncProof/NativeWeave.Engines.AsyncProof.Tests.csproj', 'NativeWeave.Engines.AsyncProof.Tests'),
            ('sample', 'samples/api/engines-asyncproof/Engines.AsyncProof.Sample.csproj', 'NativeWeave.Engines.AsyncProof.Sample')]:
            run(label + '-restore', ['dotnet', 'restore', project, *restore, *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
            dll = ROOT / Path(project).parent / 'bin/Release/net11.0' / (assembly + '.dll')
            extra = ['--direct-corpus=' + str(corpus)] if label == 'tests' else []
            run(label + '-jit', ['dotnet', dll, *extra])
            run(label + '-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', *restore, *common])
            destination = output / (label + '-aot')
            run(label + '-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '--no-restore', '-o', destination, *common])
            executable = destination / assembly
            run(label + '-aot-execute', [executable, '--require-aot', *extra])
            if (destination / (assembly + '.dll')).exists(): raise RuntimeError('Expected actual AOT executable.')
            artifacts[label + '_jit'] = {'sha256': sha(dll), 'bytes': dll.stat().st_size}
            artifacts[label + '_aot'] = {'sha256': sha(executable), 'bytes': executable.stat().st_size}
        run('api-restore', ['dotnet', 'restore', 'src/Build.Integration/ApiReference/ApiReference.csproj', '--locked-mode', *common])
        run('api-build', ['dotnet', 'build', 'src/Build.Integration/ApiReference/ApiReference.csproj', '-c', 'Release', '--no-restore', *common])
        run('api-xml-reference', ['dotnet', ROOT / 'src/Build.Integration/ApiReference/bin/Release/net11.0/NativeWeave.Build.ApiReference.dll', ROOT, env['DOTNET_ROOT'], 'src/Engines.AsyncProof/api.json'])
        receipt['package'] = consume(ROOT, output, native, args.packaging_root.resolve(), run, env, args.prepare)
        if sha(native) != native_before: raise RuntimeError('Consumption changed the native asset.')
        run('diff-check', ['git', 'diff', '--check'])
        receipt['artifacts'] = artifacts
        receipt['preparation_regressions'] = 'A18-R1/R2/R3 failure-before-fix logs/source hashes retained in the component acceptance receipt; normal native tests reproduce all corrected boundaries.'
        prior_path = args.a17_result.resolve()
        prior = json.loads(prior_path.read_text())
        if not prior['passed'] or prior['task'] != 'A17': raise RuntimeError('Accepted A17 result required for static incremental-cost comparison.')
        shutil.copy2(prior_path, output / 'accepted-a17-result.json')
        prior_artifacts = prior['artifacts']
        receipt['incremental_cost'] = {
            'a17_receipt_sha256': sha(prior_path), 'a17_source_commit': prior['source_commit'],
            'a17_native_sha256': prior_artifacts['native']['sha256'],
            'native_bytes': {'a17': prior_artifacts['native']['bytes'], 'a18': artifacts['native']['bytes']},
            'native_bytes_delta': artifacts['native']['bytes'] - prior_artifacts['native']['bytes'],
            'package_bytes': {'a17': prior['package']['package_bytes'], 'a18': receipt['package']['package_bytes']},
            'native_dependencies': json.loads((ROOT / 'native/engines/async-proof/upstream.json').read_text())['dependencies'],
            'handwritten_native_lines': sum(len(p.read_text().splitlines()) for p in (ROOT / 'native/engines/async-proof/src').glob('*.rs')),
            'handwritten_managed_lines': sum(len(p.read_text().splitlines()) for p in (ROOT / 'src/Engines.AsyncProof').glob('*.cs')),
            'generated_managed_lines': len((ROOT / 'src/Engines.AsyncProof/Generated/Bindings.g.cs').read_text().splitlines()),
            'incremental_common_public_symbols': 0, 'native_os_workers_per_host': 1,
            'managed_cost_per_operation': 'one Task/root and one bounded retirement-observer Task; no managed HTTP engine',
            'ffi_per_operation': 'create/start/one typed take/eventual release, plus cooperative cancel and bounded periodic native-retire polls',
            'payload_copies': '12-byte bounded notification; one managed response-to-native backing copy; one native received-frame-to-accounted-chunk copy; optional explicit application copy',
            'permits': 'independent operation, response/chunk item, stream and retained-backing quotas',
            'comparison_scope': 'Static artifact/code/dependency costs against the frozen accepted A17 result. Different engine behavior; no comparable runtime benchmark implied.',
            'whole_rss_bound_claimed': False, 'performance_superiority_claimed': False}
        receipt['source_stable'] = all(sha(ROOT / p) == digest for p, digest in receipt['source_sha256'].items())
        receipt['passed'] = not args.prepare and not receipt['source_dirty'] and receipt['source_stable']
        if not args.prepare and not receipt['passed']: raise RuntimeError('Formal source snapshot changed during verification.')
    finally:
        (output / 'result.json').write_text(json.dumps(receipt, indent=2) + '\n')
        print('Evidence:', output, flush=True)

if __name__ == '__main__': main()
