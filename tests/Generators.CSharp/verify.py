#!/usr/bin/env python3
"""A10 Linux generator/component receipt; full A07/Host/pristine release is separate."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

from package_probe import consume

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prepare', action='store_true')
    parser.add_argument('--analyzer', type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    passed = False
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip())
    files = subprocess.check_output(['git', 'ls-files', '--', 'src/Generators.CSharp', 'tests/Generators.CSharp',
        'samples/api/generators-csharp', 'docs/guides/generators-csharp.md', 'src/Generators.Model', 'src/NativeWeave.Core',
        'src/Interop.Abi', 'src/Interop.Lifetime', 'src/Interop.Errors', 'native/interop/exports', 'native/interop/abi',
        'Cargo.toml', 'Cargo.lock', 'Directory.Build.props', 'global.json', 'rust-toolchain.toml'], cwd=ROOT, text=True).splitlines()
    sources = {p: sha(ROOT / p) for p in files}

    def run(label, command, expected=0, cwd=ROOT, environment=env):
        begin = time.monotonic()
        stdout, stderr = output / (label + '.stdout'), output / (label + '.stderr')
        with stdout.open('w') as out, stderr.open('w') as err:
            result = subprocess.run(list(map(str, command)), cwd=cwd, env=environment, stdout=out, stderr=err)
        commands.append(dict(label=label, command=list(map(str, command)), cwd=str(cwd), returncode=result.returncode,
            expected_exit=expected, seconds=round(time.monotonic()-begin, 3), stdout_sha256=sha(stdout), stderr_sha256=sha(stderr)))
        (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        print(label, result.returncode, flush=True)
        assert result.returncode == expected, (label, result.returncode, expected)
        return stdout.read_text()

    try:
        run('dotnet', ['dotnet', '--version'])
        run('rustc', ['rustc', '-Vv'])
        records = run('native-build', ['cargo', 'build', '--locked', '-p', 'nativeweave-interop-exports', '--release', '--message-format=json'])
        artifacts = [x for x in map(json.loads, records.splitlines()) if x.get('reason') == 'compiler-artifact' and x['target']['name'] == 'nativeweave_interop_exports']
        assert len(artifacts) == 1
        native = next(Path(x) for x in artifacts[0]['filenames'] if x.endswith('.so'))
        run('inventory-build', ['cargo', 'build', '--locked', '--manifest-path', 'src/Generators.Model/native-inventory/Cargo.toml'])
        inventory_tool = ROOT / 'src/Generators.Model/native-inventory/target/debug/nativeweave-binding-inventory'
        parsed = output / 'parsed.json'
        adapter = ROOT / 'native/interop/exports/src/lib.rs'
        sidecar = ROOT / 'native/interop/exports/binding.json'
        run('parse-adapter', [inventory_tool, adapter, parsed])
        linked = run('linked-symbols', ['nm', '-D', '--defined-only', native])
        symbols = output / 'linked.txt'
        symbols.write_text('\n'.join(sorted(line.split()[-1] for line in linked.splitlines() if line.strip())) + '\n')
        common = ['-p:UseSharedCompilation=false', '-m:1', '-nr:false']
        restore = [] if args.prepare else ['--locked-mode']
        projects = [('tool', 'src/Generators.CSharp/NativeWeave.Generators.CSharp.csproj', 'NativeWeave.Generators.CSharp', 'net11.0'),
            ('compiler', 'src/Generators.CSharp/Metadata/NativeWeave.Generators.CSharp.Metadata.csproj', 'NativeWeave.Generators.CSharp.Metadata', 'netstandard2.0'),
            ('tests', 'tests/Generators.CSharp/NativeWeave.Generators.CSharp.Tests.csproj', 'NativeWeave.Generators.CSharp.Tests', 'net11.0'),
            ('consumer', 'tests/Generators.CSharp/Consumer/Consumer.csproj', 'NativeWeave.Generators.CSharp.Consumer', 'net11.0'),
            ('sample', 'samples/api/generators-csharp/Generators.CSharp.Sample.csproj', 'NativeWeave.Generators.CSharp.Sample', 'net11.0')]
        binaries = {}
        for label, project, assembly, framework in projects[:3]:
            run(label + '-restore', ['dotnet', 'restore', project, *restore, *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
            binaries[label] = ROOT / Path(project).parent / 'bin/Release' / framework / (assembly + '.dll')
        generated = output / 'generated'
        generate = ['dotnet', binaries['tool'], sidecar, parsed, adapter, symbols, 'nativeweave_exports_conformance', generated, 'NativeWeave.Generated.Conformance']
        run('emit-before-compilation', generate)
        initial = {p.name: (sha(p), p.stat().st_mtime_ns) for p in generated.iterdir()}
        run('emit-incremental', generate)
        assert initial == {p.name: (sha(p), p.stat().st_mtime_ns) for p in generated.iterdir()}
        for p in generated.iterdir():
            checked_in = ROOT / 'tests/Generators.CSharp/Consumer/generated' / p.name
            if args.prepare:
                checked_in.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(p, checked_in)
            assert p.read_bytes() == checked_in.read_bytes(), 'Generated binding snapshot drift: ' + p.name
        analyzer = ['-p:NativeWeaveA12AnalyzerPath=' + str(args.analyzer.resolve())] if args.analyzer else []
        for label, project, assembly, framework in projects[3:]:
            run(label + '-restore', ['dotnet', 'restore', project, *restore, *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common, *analyzer])
            binaries[label] = ROOT / Path(project).parent / 'bin/Release' / framework / (assembly + '.dll')
        run('emitter-incremental-api-xml', ['dotnet', binaries['tests'], sidecar, adapter, parsed, symbols, binaries['consumer'], *(['--write-reference'] if args.prepare else [])])
        before_native = sha(native)
        for label, project, assembly, framework in projects[3:]:
            run(label + '-jit', ['dotnet', binaries[label], native])
            run(label + '-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', *restore, *common])
            destination = output / (label + '-aot')
            run(label + '-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', destination, *common, *analyzer])
            run(label + '-aot-execute', [destination / assembly, native, '--require-aot'])
        assert sha(native) == before_native
        run('compiler-pack', ['dotnet', 'pack', projects[1][1], '-c', 'Release', '--no-build', '--no-restore', '-o', output / 'packages', *common])
        package = consume(ROOT, output, native, run, env)
        files = [native, *binaries.values(), *generated.iterdir(),
            output / 'consumer-aot/NativeWeave.Generators.CSharp.Consumer', output / 'sample-aot/NativeWeave.Generators.CSharp.Sample',
            output / 'package-aot/IndependentMetadataConsumer', *list((output / 'packages').glob('*.nupkg'))]
        if args.analyzer: files.append(args.analyzer.resolve())
        (output / 'artifacts.json').write_text(json.dumps({str(p): sha(p) for p in files}, indent=2) + '\n')
        passed = not args.prepare and not dirty and all(sha(ROOT / p) == digest for p, digest in sources.items())
        assert args.prepare or passed, 'Formal evidence requires a clean unchanged source checkpoint.'
    finally:
        (output / 'result.json').write_text(json.dumps(dict(task='A10', platform='linux-x64', passed=passed,
            source_commit=head, source_dirty=dirty, preparation_only=args.prepare, source_sha256=sources,
            source_stable=all(sha(ROOT / p) == digest for p, digest in sources.items()), commands=commands,
            analyzer_sha256=sha(args.analyzer) if args.analyzer else None,
            scope='Generated imports/owners and compiler metadata; actual A03/A11 components plus a fixed D06 coordinator.',
            limitations=['Windows unexecuted', 'A07 full runtime and Host closure not claimed', 'Component tools package only; no new automatic release package',
                'Independent cache and Rust trap are not a pristine machine'], independent_review='pending'), indent=2) + '\n')


if __name__ == '__main__':
    main()
