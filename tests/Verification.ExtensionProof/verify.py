#!/usr/bin/env python3
"""Verify the available Linux x64 owner-thread conformance slice on exact source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
OWNED = ['src/Verification.ExtensionProof', 'tests/Verification.ExtensionProof',
    'native/engines/extension-proof', 'samples/api/verification-extensionproof',
    'docs/guides/verification-extensionproof.md']
INPUTS = OWNED + ['src/NativeWeave.Core', 'src/Interop.Abi', 'src/Interop.Negotiation',
    'src/Interop.Lifetime', 'src/Interop.Memory', 'src/Interop.Errors', 'src/Interop.Callbacks',
    'native/interop/abi', 'native/interop/errors', 'native/interop/callbacks',
    'src/Build.Integration/ApiReference', 'Cargo.toml', 'Cargo.lock', 'global.json',
    'rust-toolchain.toml', 'Directory.Build.props', 'Directory.Build.targets']

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prepare', action='store_true')
    args = parser.parse_args()
    if platform.system() != 'Linux' or platform.machine() != 'x86_64': parser.error('Linux x64 required.')
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy(); env.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    commands = []
    receipt = dict(task='A23', platform='linux-x64', passed=False, preparation_only=args.prepare,
        scope='Actual owner-thread Rc/Cell, A03/A06 control/callback leases and explicit bounded pump; no device certification.',
        design_commit='c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9', commands=commands,
        environment={'display_configured': bool(env.get('DISPLAY')), 'wayland_configured': bool(env.get('WAYLAND_DISPLAY')),
                     'dri_path_present': Path('/dev/dri').exists()},
        unsupported=['window/device/surface-loss', 'GPU/fences', 'mobile', 'static-link', 'wasm'],
        deferred=['win-x64'], public_runtime_symbols=0,
        independent_package='Inapplicable: internal verification assembly, no new distributable package.',
        independent_review='pending', common_changes=['A13 optional Cargo member/lock', 'Core/Abi internal friend only; no source added to mandatory Core'])
    def run(label, argv):
        start = time.monotonic()
        result = subprocess.run(list(map(str, argv)), cwd=ROOT, env=env, capture_output=True, timeout=600)
        for suffix, data in [('stdout', result.stdout), ('stderr', result.stderr)]: (output / (label + '.' + suffix)).write_bytes(data)
        commands.append(dict(label=label, argv=list(map(str, argv)), exit=result.returncode,
            seconds=round(time.monotonic() - start, 3), stdout_sha256=sha(output / (label + '.stdout')), stderr_sha256=sha(output / (label + '.stderr'))))
        (output / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        print(label, result.returncode, flush=True)
        if result.returncode: raise RuntimeError(label + '\n' + result.stdout.decode(errors='replace')[-3000:] + result.stderr.decode(errors='replace')[-3000:])
        return result.stdout.decode()
    try:
        receipt['source_commit'] = run('source-commit', ['git', 'rev-parse', 'HEAD']).strip()
        receipt['source_dirty'] = bool(run('source-status', ['git', 'status', '--porcelain']).strip())
        paths = run('source-files', ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '--', *INPUTS]).splitlines()
        receipt['source_sha256'] = {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}
        if not args.prepare and receipt['source_dirty']: raise RuntimeError('Commit the component before formal execution.')
        if run('sdk', ['dotnet', '--version']).strip() != '11.0.100': raise RuntimeError('Pinned SDK required.')
        if not run('rustc', ['rustc', '-vV']).startswith('rustc 1.100.0 '): raise RuntimeError('Pinned Rust required.')
        run('native-format', ['cargo', 'fmt', '-p', 'nativeweave-engine-extensionproof', '--', '--check'])
        run('native-clippy', ['cargo', 'clippy', '--locked', '-p', 'nativeweave-engine-extensionproof', '--all-targets', '--', '-D', 'warnings'])
        run('native-tests', ['cargo', 'test', '--locked', '--release', '-p', 'nativeweave-engine-extensionproof', '--', '--nocapture'])
        run('native-build', ['cargo', 'build', '--locked', '--release', '-p', 'nativeweave-engine-extensionproof'])
        native = ROOT / 'target/release/libnativeweave_engine_extensionproof.so'; native_hash = sha(native)
        exports = {line.split()[-1] for line in run('native-exports', ['nm', '-D', '--defined-only', native]).splitlines() if line.strip() and line.split()[-1].startswith('nw_')}
        expected = set(re.findall(r'pub (?:unsafe )?extern "C" fn (nw_extension_\w+)', (ROOT / 'native/engines/extension-proof/src/lib.rs').read_text()))
        if exports != expected: raise RuntimeError('Unexpected conformance fixture or missing Engine export.')
        receipt['exports'] = sorted(exports)
        tree = run('native-dependencies', ['cargo', 'tree', '--locked', '-p', 'nativeweave-engine-extensionproof', '-e', 'normal,features'])
        if any(x in tree.lower() for x in ['tokio', 'winit', 'wgpu', 'conformance']): raise RuntimeError('Probe acquired an unselected runtime/device/conformance dependency.')
        common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
        locked = [] if args.prepare else ['--locked-mode']
        artifacts = {'native': {'sha256': native_hash, 'bytes': native.stat().st_size}}
        for label, path, assembly in [('tests', 'tests/Verification.ExtensionProof/NativeWeave.Verification.ExtensionProof.Tests.csproj', 'NativeWeave.Verification.ExtensionProof.Tests'),
                                      ('sample', 'samples/api/verification-extensionproof/ExtensionProof.Sample.csproj', 'NativeWeave.Verification.ExtensionProof.Sample')]:
            run(label + '-restore', ['dotnet', 'restore', path, *locked, *common])
            run(label + '-build', ['dotnet', 'build', path, '-c', 'Release', '--no-restore', *common])
            dll = ROOT / Path(path).parent / 'bin/Release/net11.0' / (assembly + '.dll')
            run(label + '-jit', ['dotnet', dll])
            run(label + '-aot-restore', ['dotnet', 'restore', path, '-r', 'linux-x64', '-p:PublishAot=true', *locked, *common])
            aot = output / (label + '-aot')
            run(label + '-aot-publish', ['dotnet', 'publish', path, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '--no-restore', '-o', aot, *common])
            executable = aot / assembly
            run(label + '-aot-execute', [executable, '--require-aot'])
            if (aot / (assembly + '.dll')).exists(): raise RuntimeError('Expected actual Native AOT executable.')
            artifacts[label + '_jit'] = {'sha256': sha(dll), 'bytes': dll.stat().st_size}
            artifacts[label + '_aot'] = {'sha256': sha(executable), 'bytes': executable.stat().st_size}
        project = 'src/Build.Integration/ApiReference/ApiReference.csproj'
        run('api-restore', ['dotnet', 'restore', project, '--locked-mode', *common])
        run('api-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
        run('api-xml-reference', ['dotnet', ROOT / 'src/Build.Integration/ApiReference/bin/Release/net11.0/NativeWeave.Build.ApiReference.dll', ROOT, env['DOTNET_ROOT'], 'src/Verification.ExtensionProof/api.json', *(['--write'] if args.prepare else [])])
        if sha(native) != native_hash: raise RuntimeError('Managed builds changed the native asset.')
        run('diff-check', ['git', 'diff', '--check'])
        receipt['artifacts'] = artifacts
        receipt['source_stable'] = all(sha(ROOT / p) == digest for p, digest in receipt['source_sha256'].items())
        receipt['passed'] = not args.prepare and not receipt['source_dirty'] and receipt['source_stable']
        if not args.prepare and not receipt['passed']: raise RuntimeError('Formal source changed during verification.')
    finally:
        (output / 'result.json').write_text(json.dumps(receipt, indent=2) + '\n')
        print('Evidence:', output, flush=True)

if __name__ == '__main__': main()
