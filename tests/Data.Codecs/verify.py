#!/usr/bin/env python3
"""Run B07's actual Linux component gates; no missing-service success path."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "native/cpp/tests"))
from component_build import prepare

OWNED = ('src/Data.Codecs', 'tests/Data.Codecs', 'native/cpp', 'samples/api/data-codecs')

def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--offline-config', type=Path)
    parser.add_argument('--native-build', type=Path, default=ROOT / 'artifacts/cmake/linux-x64-release')
    args = parser.parse_args()
    if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
        parser.error('This evidence runner currently targets Linux x64 only.')
    output = (args.output or ROOT / 'artifacts/B07' / datetime.now(timezone.utc).strftime('run-%Y%m%dT%H%M%SZ')).resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    records = []
    inputs = []
    for scope in OWNED:
        for path in sorted((ROOT / scope).rglob('*')):
            if path.is_file() and not any(p in ('bin', 'obj', '__pycache__') for p in path.relative_to(ROOT).parts):
                inputs.append({'path': str(path.relative_to(ROOT)), 'sha256': sha(path)})
    for name in ('CMakeLists.txt', 'CMakePresets.json', '.clang-format', '.clang-tidy', 'global.json', 'src/Data.Abstractions/NativeWeave.Data.csproj', 'docs/guides/data-codecs.md'):
        path = ROOT / name
        if path.exists(): inputs.append({'path': name, 'sha256': sha(path)})
    source = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), 'files': inputs, 'tree_dirty': bool(subprocess.check_output(['git','status','--porcelain'],cwd=ROOT,text=True).strip())}
    (output / 'source-inputs.json').write_text(json.dumps(source, indent=2) + '\n')

    def run(name: str, command: list[str], use_env=None) -> str:
        log = output / (name + '.log')
        started = time.monotonic()
        with log.open('w') as stream:
            result = subprocess.run(command, cwd=ROOT, env=use_env or env, stdout=stream, stderr=subprocess.STDOUT)
        records.append({'name': name, 'command': command, 'exit_code': result.returncode, 'seconds': round(time.monotonic() - started, 3), 'log': log.name, 'sha256': sha(log)})
        (output / 'commands.json').write_text(json.dumps(records, indent=2) + '\n')
        print(name + ': ' + str(result.returncode), flush=True)
        if result.returncode:
            print(log.read_text()[-12000:], flush=True)
            raise RuntimeError('Gate failed: ' + name)
        return log.read_text()

    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    restore = ['--locked-mode'] + (['--configfile', str(args.offline_config.resolve()), '-p:NuGetAudit=false'] if args.offline_config else [])
    project = 'tests/Data.Codecs/NativeWeave.Data.Codecs.Tests.csproj'
    api = 'tests/Data.Codecs/ApiCheck/ApiCheck.csproj'
    sample = 'samples/api/data-codecs/DataCodecs.csproj'
    assembly = ROOT / 'tests/Data.Codecs/bin/Release/net11.0/NativeWeave.Data.Codecs.Tests.dll'
    server = None
    server_log = None
    status = 'failed'
    try:
        run('toolchain-dotnet', ['dotnet', '--info'])
        provenance = prepare(run, args.native_build, ["nw_codec_roundtrip", "nw_scalar_tests"],
                             r"^native\.scalar$", [ROOT / "native/cpp/src/scalar.cpp", ROOT / "native/cpp/tests/scalar_tests.cpp",
                                                    ROOT / "native/cpp/tests/codec_roundtrip.cpp"])
        (output / "compiler-provenance.txt").write_text(provenance)
        for label, p in [('consumer', project), ('api-check', api), ('sample', sample)]:
            run(label + '-restore', ['dotnet', 'restore', p, *restore, *common])
            run(label + '-build', ['dotnet', 'build', p, '-c', 'Release', '--no-restore', *common])
        jit = output / 'jit'
        aot = output / 'aot-vectors'
        run('jit-encode', ['dotnet', str(assembly), str(jit)])
        publish = output / 'aot'
        run('aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:RestoreLockedMode=true', *restore[1:], *common, '-o', str(publish)])
        executable = publish / 'NativeWeave.Data.Codecs.Tests'
        run('aot-encode', [str(executable), str(aot), '--require-aot'])
        run('compiled-api-reference', ['dotnet', str(ROOT / 'tests/Data.Codecs/ApiCheck/bin/Release/net11.0/NativeWeave.Data.Codecs.ApiCheck.dll'), str(ROOT / 'src/Data.Codecs/bin/Release/net11.0/NativeWeave.Data.Codecs.dll'), str(ROOT / 'src/Data.Codecs/bin/Release/net11.0/NativeWeave.Data.Codecs.xml'), str(ROOT / 'src/Data.Codecs/api-reference.md')])
        run('sample-execution', ['dotnet', str(ROOT / 'samples/api/data-codecs/bin/Release/net11.0/NativeWeave.Data.Codecs.Sample.dll')])
        native_env = dict(env)
        if args.postgres_bin:
            pg = args.postgres_bin.resolve()
            data = output / 'postgres-data'
            run('postgres-init', [str(pg / 'initdb'), '-D', str(data), '-U', 'nativeweave', '--auth=trust', '--no-locale', '--encoding=UTF8'])
            with socket.socket() as listener:
                listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
            server_log = (output / 'postgres.log').open('w')
            server = subprocess.Popen([str(pg/'postgres'), '-D', str(data), '-h', '127.0.0.1', '-p', str(port), '-k', ''], stdout=server_log, stderr=subprocess.STDOUT, env=env)
            for _ in range(300):
                if subprocess.run([str(pg / 'pg_isready'), '-h', '127.0.0.1', '-p', str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0: break
                if server.poll() is not None: raise RuntimeError('PostgreSQL exited during startup.')
                time.sleep(.1)
            else: raise RuntimeError('PostgreSQL startup deadline exceeded.')
            native_env['NATIVEWEAVE_TEST_POSTGRES_URL'] = f'postgresql://nativeweave@127.0.0.1:{port}/postgres'
        elif not native_env.get('NATIVEWEAVE_TEST_POSTGRES_URL'):
            raise RuntimeError('PostgreSQL binaries or explicit NATIVEWEAVE_TEST_POSTGRES_URL are required.')
        fixture = args.native_build.resolve() / 'tests/nw_codec_roundtrip'
        for label, vector_dir in [('jit', jit), ('aot', aot)]:
            run(label + '-providers', [str(fixture), str(vector_dir)], native_env)
        run('jit-returned-values', ['dotnet', str(assembly), str(jit)])
        run('aot-returned-values', [str(executable), str(aot), '--require-aot'])
        artifact_paths = [assembly, executable, fixture, ROOT / 'src/Data.Codecs/bin/Release/net11.0/NativeWeave.Data.Codecs.dll']
        (output / 'artifacts.json').write_text(json.dumps([{'path': str(p.relative_to(ROOT)) if p.is_relative_to(ROOT) else str(p), 'sha256': sha(p), 'size': p.stat().st_size} for p in artifact_paths], indent=2) + '\n')
        # Source binding must still match the bytes at verification start.
        for item in inputs:
            if sha(ROOT / item['path']) != item['sha256']: raise RuntimeError('Input changed during verification: ' + item['path'])
        status = 'passed'
    except Exception as exc:
        (output / 'failure.txt').write_text(str(exc) + '\n')
    finally:
        if server is not None:
            subprocess.run([str(args.postgres_bin.resolve()/'pg_ctl'), '-D', str(output/'postgres-data'), 'stop', '-m', 'fast'], stdout=server_log, stderr=subprocess.STDOUT)
            try: server.wait(timeout=30)
            except subprocess.TimeoutExpired: server.kill(); server.wait()
        if server_log is not None: server_log.close()
        summary = {'task': 'B07', 'platform': 'linux-x64', 'status': status, 'commands': len(records), 'source_manifest_sha256': sha(output/'source-inputs.json'), 'offline_restore': args.offline_config is not None, 'scope': 'Internal managed/native codecs; actual C++ libpq/SQLite and JIT/AOT value roundtrips; no B04 host, pristine package or other-platform acceptance.'}
        (output/'summary.json').write_text(json.dumps(summary, indent=2)+'\n')
        print(json.dumps(summary), flush=True)
    return 0 if status == 'passed' else 1

if __name__ == '__main__': raise SystemExit(main())
