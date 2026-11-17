#!/usr/bin/env python3
"""Run the Linux A17 real-codec component and independent precompiled consumer."""
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
OWNED = ['src/Engines.SyncProof', 'tests/Engines.SyncProof', 'native/engines/sync-proof',
         'samples/api/engines-syncproof', 'docs/guides/engines-syncproof.md']
INPUTS = OWNED + ['src/NativeWeave.Core', 'src/Interop.Abi', 'src/Interop.Lifetime', 'src/Interop.Memory',
    'src/Interop.Errors', 'src/Interop.Negotiation', 'native/interop/abi', 'native/interop/errors',
    'src/Generators.Model', 'src/Generators.CSharp', 'src/Generators.Rust', 'src/Build.Integration/ApiReference',
    'Cargo.toml', 'Cargo.lock', 'global.json', 'rust-toolchain.toml', 'Directory.Build.props', 'Directory.Build.targets']

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--packaging-root', type=Path, default=ROOT)
    parser.add_argument('--prepare', action='store_true')
    args = parser.parse_args()
    if platform.system() != 'Linux' or platform.machine() != 'x86_64': parser.error('Linux x64 execution required.')
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy(); env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    receipt = dict(task='A17', platform='linux-x64', passed=False, preparation_only=args.prepare, commands=commands,
        design_commit='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9', independent_review='pending',
        scope='Real miniz_oxide synchronous engine, Core adoption, generated static boundary and independent local package.',
        limitations=['Windows not executed', 'No full production codec/soak/performance-superiority claim',
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
        run('native-format', ['cargo', 'fmt', '-p', 'nativeweave-engine-syncproof', '--', '--check'])
        run('native-tests', ['cargo', 'test', '--locked', '--release', '-p', 'nativeweave-engine-syncproof', '--', '--nocapture'])
        run('native-build', ['cargo', 'build', '--locked', '--release', '-p', 'nativeweave-engine-syncproof'])
        run('native-clippy', ['cargo', 'clippy', '--locked', '-p', 'nativeweave-engine-syncproof', '--all-targets', '--', '-D', 'warnings'])
        native = ROOT / 'target/release/libnativeweave_engine_syncproof.so'
        native_before = sha(native)
        direct = ROOT / 'target/release/nativeweave-syncproof-direct'
        tree = run('native-dependency-tree', ['cargo', 'tree', '--locked', '-p', 'nativeweave-engine-syncproof', '-e', 'normal,features'])
        if any(name in tree.lower() for name in ['tokio', 'hyper', 'axum']): raise RuntimeError('Synchronous engine acquired a runtime dependency.')
        run('inventory-build', ['cargo', 'build', '--locked', '--manifest-path', 'src/Generators.Model/native-inventory/Cargo.toml'])
        parsed = output / 'parsed.json'
        run('parse-adapter', [ROOT / 'src/Generators.Model/native-inventory/target/debug/nativeweave-binding-inventory', 'native/engines/sync-proof/src/lib.rs', parsed])
        linked = output / 'linked.txt'
        linked.write_text('\n'.join(line.split()[-1] for line in run('linked-exports', ['nm', '-D', '--defined-only', native]).splitlines() if line.strip()) + '\n')
        common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
        for label, project in [('csharp-generator', 'src/Generators.CSharp/NativeWeave.Generators.CSharp.csproj'), ('rust-generator', 'src/Generators.Rust/NativeWeave.Generators.Rust.csproj')]:
            run(label + '-restore', ['dotnet', 'restore', project, '--locked-mode', *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
        generation_args = ['native/engines/sync-proof/binding.json', parsed, 'native/engines/sync-proof/src/lib.rs', linked]
        generated_cs = output / 'generated-csharp'; generated_rs = output / 'generated-rust'
        run('generate-csharp', ['dotnet', ROOT / 'src/Generators.CSharp/bin/Release/net11.0/NativeWeave.Generators.CSharp.dll', *generation_args, 'nativeweave_engine_syncproof', generated_cs, 'NativeWeave.Engines.SyncProof.Generated'])
        run('generate-rust', ['dotnet', ROOT / 'src/Generators.Rust/bin/Release/net11.0/NativeWeave.Generators.Rust.dll', *generation_args, generated_rs])
        for generated, destination in [(generated_cs, ROOT / 'src/Engines.SyncProof/Generated'), (generated_rs, ROOT / 'native/engines/sync-proof/generated')]:
            for path in generated.iterdir():
                target = destination / path.name
                if args.prepare: shutil.copy2(path, target)
                elif path.read_bytes() != target.read_bytes(): raise RuntimeError('Generated binding is stale: ' + str(target))
        run('c-layout-compile', ['cc', '-std=c11', '-Wall', '-Wextra', '-Werror', 'tests/Engines.SyncProof/probe.c', '-I', generated_rs, '-L', native.parent,
            '-lnativeweave_engine_syncproof', '-Wl,-rpath,' + str(native.parent), '-o', output / 'c-probe'])
        run('c-layout-execute', [output / 'c-probe'])
        corpus = output / 'direct-corpus'; corpus.mkdir()
        for size in [0, 1, 7, 31, 256, 4096, 65536, 4194304]:
            seed = 0x9e3779b9; data = bytearray(size)
            for i in range(size):
                seed ^= (seed << 13) & 0xffffffff; seed ^= seed >> 17; seed ^= (seed << 5) & 0xffffffff; data[i] = seed & 255
            raw, encoded, decoded = [corpus / (str(size) + ext) for ext in ['.raw', '.zlib', '.decoded']]
            raw.write_bytes(data)
            run('direct-encode-' + str(size), [direct, 'encode', raw, encoded])
            run('direct-decode-' + str(size), [direct, 'decode', encoded, decoded])
            if decoded.read_bytes() != data: raise RuntimeError('Direct Rust behavioral oracle failed.')
        restore = [] if args.prepare else ['--locked-mode']
        artifacts = {'native': {'sha256': native_before, 'bytes': native.stat().st_size}, 'direct_rust': {'sha256': sha(direct), 'bytes': direct.stat().st_size}}
        for label, project, assembly in [
            ('tests', 'tests/Engines.SyncProof/NativeWeave.Engines.SyncProof.Tests.csproj', 'NativeWeave.Engines.SyncProof.Tests'),
            ('sample', 'samples/api/engines-syncproof/Engines.SyncProof.Sample.csproj', 'NativeWeave.Engines.SyncProof.Sample')]:
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
        run('api-xml-reference', ['dotnet', ROOT / 'src/Build.Integration/ApiReference/bin/Release/net11.0/NativeWeave.Build.ApiReference.dll', ROOT, env['DOTNET_ROOT'], 'src/Engines.SyncProof/api.json'])
        receipt['package'] = consume(ROOT, output, native, args.packaging_root.resolve(), run, env, args.prepare)
        if sha(native) != native_before: raise RuntimeError('Consumption changed the native asset.')
        run('diff-check', ['git', 'diff', '--check'])
        receipt['artifacts'] = artifacts
        receipt['incremental_cost'] = {'native_dependencies': ['miniz_oxide 0.9.1 with-alloc', 'adler2 2.0.1'],
            'no_tokio_dependency': True, 'handwritten_native_lines': sum(len(p.read_text().splitlines()) for p in (ROOT / 'native/engines/sync-proof/src').glob('*.rs')),
            'handwritten_managed_lines': len((ROOT / 'src/Engines.SyncProof/ZlibCodec.cs').read_text().splitlines()),
            'generated_managed_lines': len((ROOT / 'src/Engines.SyncProof/Generated/Bindings.g.cs').read_text().splitlines()),
            'native_calls_per_owned_result': 'one run and eventual one release; first use adds bootstrap',
            'array_extra_copies': 1, 'owned_result_managed_payload_copies': 0,
            'bounded_output_growth': 'fallible doubling capped by independent encoded/decoded limit; transient old+new allocation <= 2*cap before allocator overhead',
            'whole_rss_bound_claimed': False, 'performance_superiority_claimed': False}
        receipt['source_stable'] = all(sha(ROOT / p) == digest for p, digest in receipt['source_sha256'].items())
        receipt['passed'] = not args.prepare and not receipt['source_dirty'] and receipt['source_stable']
        if not args.prepare and not receipt['passed']: raise RuntimeError('Formal source snapshot changed during verification.')
    finally:
        (output / 'result.json').write_text(json.dumps(receipt, indent=2) + '\n')
        print('Evidence:', output, flush=True)

if __name__ == '__main__': main()
