#!/usr/bin/env python3
"""A11 actual Linux C/Rust/C# boundary, deterministic generation and negative evidence."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prepare', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    passed = False
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip())
    paths = subprocess.check_output(['git', 'ls-files', '--', 'src/Generators.Rust', 'tests/Generators.Rust',
        'samples/api/generators-rust', 'docs/guides/generators-rust.md', 'native/interop/exports',
        'src/Generators.Model', 'native/interop/abi', 'Cargo.toml', 'Cargo.lock', 'global.json',
        'Directory.Build.props', 'rust-toolchain.toml'], cwd=ROOT, text=True).splitlines()
    sources = {p: sha(ROOT / p) for p in paths}

    def run(label, command, expected=0, contains=None):
        start = time.monotonic()
        stdout, stderr = output / (label + '.stdout'), output / (label + '.stderr')
        with stdout.open('w') as out, stderr.open('w') as err:
            result = subprocess.run(list(map(str, command)), cwd=ROOT, env=env, stdout=out, stderr=err)
        commands.append(dict(label=label, command=list(map(str, command)), returncode=result.returncode,
            expected_exit=expected, seconds=round(time.monotonic()-start, 3),
            stdout_sha256=sha(stdout), stderr_sha256=sha(stderr)))
        (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        print(label, result.returncode, flush=True)
        assert result.returncode == expected, (label, result.returncode, expected)
        if contains:
            assert contains in stdout.read_text() + stderr.read_text(), (label, contains)
        return stdout.read_text()

    try:
        for name, cmd in [('dotnet', ['dotnet', '--version']), ('rustc', ['rustc', '-Vv']), ('cc', ['cc', '--version'])]:
            run(name, cmd)
        run('rust-format', ['rustfmt', '--edition', '2024', '--check', 'native/interop/exports/src/lib.rs'])
        build_records = run('native-build', ['cargo', 'build', '--locked', '-p', 'nativeweave-interop-exports', '--release', '--message-format=json'])
        artifacts_by_target = {x['target']['name']: x['filenames'] for x in map(json.loads, build_records.splitlines()) if x.get('reason') == 'compiler-artifact'}
        run('native-test', ['cargo', 'test', '--locked', '-p', 'nativeweave-interop-exports', '--release'])
        run('native-clippy', ['cargo', 'clippy', '--locked', '-p', 'nativeweave-interop-exports', '--all-targets', '--', '-D', 'warnings'])
        run('inventory-build', ['cargo', 'build', '--locked', '--manifest-path', 'src/Generators.Model/native-inventory/Cargo.toml'])
        native = next(Path(p) for p in artifacts_by_target['nativeweave_interop_exports'] if p.endswith('.so'))
        parser_exe = ROOT / 'src/Generators.Model/native-inventory/target/debug/nativeweave-binding-inventory'
        adapter = ROOT / 'native/interop/exports/src/lib.rs'
        sidecar = ROOT / 'native/interop/exports/binding.json'
        parsed = output / 'parsed.json'
        run('parse-adapter', [parser_exe, adapter, parsed])
        linked = run('linked-symbols', ['nm', '-D', '--defined-only', native])
        names = sorted(line.split()[-1] for line in linked.splitlines() if line.strip())
        symbols = output / 'linked.txt'
        symbols.write_text('\n'.join(names) + '\n')
        common = ['-p:UseSharedCompilation=false', '-m:1', '-nr:false']
        restore = [] if args.prepare else ['--locked-mode']
        projects = [('tool', 'src/Generators.Rust/NativeWeave.Generators.Rust.csproj', 'NativeWeave.Generators.Rust'),
            ('tests', 'tests/Generators.Rust/NativeWeave.Generators.Rust.Tests.csproj', 'NativeWeave.Generators.Rust.Tests'),
            ('consumer', 'tests/Generators.Rust/Consumer/Consumer.csproj', 'NativeWeave.Generators.Rust.Consumer'),
            ('sample', 'samples/api/generators-rust/Generators.Rust.Sample.csproj', 'NativeWeave.Generators.Rust.Sample')]
        binaries = {}
        for label, project, assembly in projects:
            run(label + '-restore', ['dotnet', 'restore', project, *restore, *common])
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
            binaries[label] = ROOT / Path(project).parent / 'bin/Release/net11.0' / (assembly + '.dll')
        generated = output / 'generated'
        run('generate', ['dotnet', binaries['tool'], sidecar, parsed, adapter, symbols, generated])
        run('unit-negative-and-api-xml', ['dotnet', binaries['tests'], sidecar, adapter, parsed, symbols])
        initial = {p.name: (sha(p), p.stat().st_mtime_ns) for p in generated.iterdir()}
        run('generate-incremental', ['dotnet', binaries['tool'], sidecar, parsed, adapter, symbols, generated])
        assert initial == {p.name: (sha(p), p.stat().st_mtime_ns) for p in generated.iterdir()}
        run('c-compile', ['cc', '-std=c11', '-Wall', '-Wextra', '-Werror', 'tests/Generators.Rust/probe.c', '-I', generated,
            '-L', native.parent, '-lnativeweave_interop_exports', '-Wl,-rpath,' + str(native.parent), '-o', output / 'c-probe'])
        run('c-execute', [output / 'c-probe'])
        rlib = [Path(p) for p in artifacts_by_target['nativeweave_interop_abi'] if p.endswith('.rlib')]
        assert len(rlib) == 1, 'Use a clean per-worktree target with one A01 source identity.'
        # Include the actual source, not a mirrored representation. Item doc comments remain legal
        # after the crate-level inner docs are removed from this temporary verification unit.
        source = '\n'.join(line for line in adapter.read_text().splitlines() if not line.startswith('//!')) + '\n'
        assertions = (generated / 'layout_assertions.rs').read_text()
        check = output / 'checked.rs'
        check.write_text(source + assertions)
        compile_base = ['rustc', '--edition', '2024', '--crate-name', 'checked_exports', '--crate-type', 'cdylib',
            '--extern', 'nativeweave_interop_abi=' + str(rlib[0]), '-L', 'dependency=' + str(rlib[0].parent)]
        run('rust-layout-signatures', [*compile_base, check, '-o', output / 'libchecked_exports.so'])
        broken = output / 'broken-layout.rs'
        broken.write_text(source + assertions.replace('size_of::<NwAbiInfoV1>() == 32', 'size_of::<NwAbiInfoV1>() == 40'))
        run('negative-layout-drift', [*compile_base, broken, '-o', output / 'never-layout.so'], 1, 'error[E0080]')
        broken.write_text(source + assertions.replace('fn(u32, u32, *mut NwAbiInfoV1)', 'fn(u64, u32, *mut NwAbiInfoV1)'))
        run('negative-signature-drift', [*compile_base, broken, '-o', output / 'never-signature.so'], 1, 'mismatched types')
        # Unsupported declarations remain in the real syn inventory; they cannot disappear in extraction.
        bad_adapter = output / 'tuple.rs'
        bad_adapter.write_text(adapter.read_text().replace('major: u32,', 'major: (u32, u32),', 1))
        bad_sidecar = json.loads(sidecar.read_text())
        bad_sidecar['provenance']['adapter_sha256'] = sha(bad_adapter)
        (output / 'tuple.json').write_text(json.dumps(bad_sidecar))
        run('parse-unsupported-tuple', [parser_exe, bad_adapter, output / 'tuple-parsed.json'])
        run('negative-unsupported-tuple', ['dotnet', binaries['tool'], output / 'tuple.json', output / 'tuple-parsed.json', bad_adapter, symbols, output / 'never-generated'], 1, 'NWABI001')
        assert not (output / 'never-generated').exists()
        before_native = sha(native)
        for label, project, assembly in projects[2:]:
            run(label + '-jit', ['dotnet', binaries[label], native])
            run(label + '-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', *restore, *common])
            destination = output / (label + '-aot')
            run(label + '-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', destination, *common])
            run(label + '-aot-execute', [destination / assembly, native, '--require-aot'])
        assert sha(native) == before_native
        artifacts = [native, output / 'c-probe', output / 'libchecked_exports.so', *generated.iterdir(), *binaries.values(),
            output / 'consumer-aot/NativeWeave.Generators.Rust.Consumer', output / 'sample-aot/NativeWeave.Generators.Rust.Sample']
        (output / 'artifacts.json').write_text(json.dumps({str(p): sha(p) for p in artifacts}, indent=2) + '\n')
        passed = not args.prepare and not dirty and all(sha(ROOT / p) == digest for p, digest in sources.items())
        assert args.prepare or passed, 'Formal evidence needs a clean unchanged source checkpoint.'
    finally:
        (output / 'result.json').write_text(json.dumps(dict(task='A11', platform='linux-x64', passed=passed,
            source_commit=head, source_dirty=dirty, preparation_only=args.prepare, source_sha256=sources,
            source_stable=all(sha(ROOT / p) == digest for p, digest in sources.items()), commands=commands,
            scope='Maintainer generation and actual C/Rust/C# conformance component; A01 bootstrap is reused.',
            limitations=['Windows unexecuted', 'Fixed synchronous owner/callback fixture is not A07 or a domain Host',
                'No A11 application API or new release package', 'A16/B24 own pristine distribution composition'],
            independent_review='pending'), indent=2) + '\n')


if __name__ == '__main__':
    main()
