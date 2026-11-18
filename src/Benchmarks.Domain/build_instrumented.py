#!/usr/bin/env python3
"""Build B23-owned diagnostic/DI copies of the accepted B21 packed consumer.

Only application source is copied/edited. Native bytes and exact package locks
are reused. The immutable reference artifacts remain the primary benchmark route.
"""
import argparse
from datetime import datetime, timezone
import os
from pathlib import Path
import shutil
import subprocess
import sys

from common import check_files, digest, inventory, load, load_a21, save

HERE = Path(__file__).resolve().parent
ASSEMBLY = 'NativeWeave.Reference.TaskService'
MODES = ['release-disabled', 'debug-default', 'debug-local', 'sampled', 'full',
         'meter-only', 'log-only', 'exporter-saturation', 'exporter-failure']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package-probe', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    source = args.package_probe.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    progress = load(source / 'progress.json')
    consumer = output / 'consumer'
    consumer.mkdir()
    for name, expected in progress['consumer_sources']['baseline'].items():
        original = source / 'consumers/baseline' / name
        if digest(original) != expected:
            raise ValueError('Accepted application source changed: ' + name)
        target = consumer / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
    for lock in (source / 'consumers/baseline').glob('packages*.lock.json'):
        shutil.copy2(lock, consumer / lock.name)
    instrumentation = HERE / 'instrumentation/DomainInstrumentation.cs'
    shutil.copy2(instrumentation, consumer / instrumentation.name)
    program = consumer / 'Program.cs'
    text = program.read_text()
    marker = '        await using var application = builder.Build();'
    if text.count(marker) != 1:
        raise ValueError('Accepted B21 application hook changed; review the source delta')
    text = text.replace(marker, '        using var domainProbe = DomainInstrumentation.Configure(builder, service);\n'
        + marker + '\n        domainProbe.Map(application, service);')
    program.write_text(text)
    (consumer / 'B23-source-delta.patch').write_text('B23-only copy: add internal instrumentation source and explicit Configure/Map hooks before/after builder.Build().\n')
    a21 = load_a21()
    commands = []
    receipt = {'schema_version': 1, 'task': 'B23', 'source_repository': progress['source_repository'],
        'baseline_application': progress['consumer_sources']['baseline'], 'copied_sources': inventory(consumer),
        'instrumentation_sha256': digest(instrumentation), 'commands': commands, 'builds': {}, 'profiles': {},
        'scope': 'Benchmark-only copied application with standard Activity/Meter/ILogger and process GC observations',
        'configuration_note': 'Debug variants are actual Native AOT Debug application builds; product NuGet/native inputs stay at the accepted Release bytes',
        'isa_note': 'Same portable SDK x64 default as frozen B21 application; no host-native ISA option or BenchmarkDotNet native default',
        'started_utc': datetime.now(timezone.utc).isoformat()}

    def run(label, arguments, timeout=1800):
        command = [str(value) for value in arguments]
        log = output / (label + '.log')
        started = a21.time.monotonic_ns()
        with log.open('w') as stream:
            result = subprocess.run(command, cwd=consumer, stdout=stream, stderr=subprocess.STDOUT,
                                    timeout=timeout, env=dict(os.environ, DOTNET_PROCESSOR_COUNT='2'))
        commands.append({'label': label, 'argv': command, 'cwd': str(consumer), 'exit_code': result.returncode,
            'elapsed_ns': a21.time.monotonic_ns() - started, 'log': log.name, 'sha256': digest(log),
            'timing_scope': 'Observed developer build command with available caches; not an isolated runtime benchmark'})
        save(output / 'manifest.json', receipt)
        if result.returncode:
            raise RuntimeError('Build failed; inspect ' + str(log))

    executable = shutil.which(args.dotnet) or args.dotnet
    receipt['toolchain'] = {'dotnet': subprocess.check_output([executable, '--version'], text=True).strip(),
                            'dotnet_sha256': digest(Path(executable).resolve())}
    project = consumer / 'Reference.TaskService.csproj'
    common = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-r', 'linux-x64', '-p:PublishAot=true']
    run('restore', [executable, 'restore', project, '--locked-mode', *common])
    for configuration in ('Release', 'Debug'):
        artifacts = output / configuration.lower()
        run(configuration.lower() + '-publish', [executable, 'publish', project, '-c', configuration,
            '--no-restore', *common, '-o', artifacts])
        pins = {str(path): digest(path) for path in sorted(artifacts.rglob('*')) if path.is_file()}
        native = {}
        for name, expected in progress['inputs']['native_sha256'].items():
            candidates = list(artifacts.rglob(name))
            if not candidates or any(digest(path) != expected for path in candidates):
                raise ValueError('B23 instrumentation changed a frozen native module: ' + name)
            native[name] = expected
        compiler_inputs = {str(path): digest(path) for path in consumer.rglob('*.ilc.rsp')}
        receipt['builds'][configuration] = {'artifacts': pins, 'native_sha256': native, 'compiler_response_files': compiler_inputs}
        for mode in MODES:
            selected = 'Debug' if mode.startswith('debug-') else 'Release'
            if selected != configuration:
                continue
            receipt['profiles'][mode] = {'id': 'nativeweave-aot-rust', 'command': [str(artifacts / ASSEMBLY)],
                'artifacts': pins, 'lock_hashes': {str(path): digest(path) for path in consumer.glob('packages*.lock.json')},
                'toolchain': receipt['toolchain'], 'source': {'commit': progress['source_repository'].get('commit'),
                    'baseline': str(source), 'benchmark_copy': str(consumer), 'source_hashes': receipt['copied_sources']},
                'supported': {'providers': ['sqlite', 'postgresql'], 'protocols': ['http1', 'http1-tls', 'http2-tls'],
                              'kinds': ['create', 'cycle', 'list', 'stream', 'diagnostic-failure']},
                'instrumented': True, 'environment': {'B23_DIAGNOSTICS': mode},
                'limitations': ['Benchmark-only observation endpoint and controlled failure route',
                                'Native allocator and per-operation copy/FFI counters remain unavailable']}
        save(output / 'manifest.json', receipt)
    receipt['status'] = 'built'
    save(output / 'manifest.json', receipt)
    print(output / 'manifest.json')


if __name__ == '__main__':
    main()
