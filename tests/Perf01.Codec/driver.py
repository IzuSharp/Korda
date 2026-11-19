#!/usr/bin/env python3
"""Perf-01 codec package producer and external open-loop measurement controller.

Preparation/functional checks are separate from timing. Before/after use this
same committed driver, separate exact package feeds and the same hashed corpus.
The A21 percentile, process-observer and independent-process noise definitions
are reused without changing the shared controller.
"""
from __future__ import annotations

import argparse
from array import array
import asyncio
from collections import Counter, defaultdict
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import random
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SEED = 0x9E3779B9
RAW = 4194304
ENCODED = RAW + 65536


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


A21 = load_module('perf01_codec_a21', ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py')
AFFINITY = load_module('perf01_codec_affinity', HERE / 'affinity.py')
sha = A21.sha256


def dump(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def utc():
    return datetime.now(timezone.utc).isoformat()


def driver_inventory():
    return {str(path.relative_to(HERE)): sha(path) for path in HERE.rglob('*')
            if path.is_file() and not set(path.parts).intersection({'target', 'bin', 'obj', '__pycache__'})
            and path.suffix != '.pyc'}


def cycle(interface='mixed', action='mixed', case=None):
    rows = []
    for size, repeats in enumerate([10, 5, 4, 1]):
        for entropy in range(2):
            for mode in ['c', 'd']:
                for ownership in ['a', 'o']:
                    rows.extend([(size * 2 + entropy, mode, ownership)] * repeats)
    random.Random(SEED).shuffle(rows)
    return [row for row in rows if (interface == 'mixed' or row[2] == {'array': 'a', 'owned': 'o'}[interface])
            and (action == 'mixed' or row[1] == {'compress': 'c', 'decompress': 'd'}[action])
            and (case is None or row[0] == case)]


def reference_raw(size, compressible):
    state = SEED
    output = bytearray(size)
    pattern = bytearray(64)
    for i in range(size if not compressible else 64):
        state ^= (state << 13) & 0xFFFFFFFF
        state ^= state >> 17
        state ^= (state << 5) & 0xFFFFFFFF
        if compressible:
            pattern[i] = state & 255
        else:
            output[i] = state & 255
    if compressible:
        output = (pattern * ((size + 63) // 64))[:size]
    return bytes(output)


def prepare(args):
    product = args.product_root.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    logs = output / 'logs'; logs.mkdir()
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1')
    environment['CARGO_TARGET_DIR'] = str(product / 'target')
    commands = []

    def run(label, command, cwd=product, environment=environment, expected=0, contains=None):
        argv = list(map(str, command))
        start = time.monotonic()
        result = subprocess.run(argv, cwd=cwd, env=environment, capture_output=True, timeout=900)
        for suffix, data in [('stdout', result.stdout), ('stderr', result.stderr)]:
            (logs / (label + '.' + suffix)).write_bytes(data)
        commands.append({'label': label, 'argv': argv, 'exit': result.returncode,
                         'elapsed_seconds': time.monotonic()-start,
                         'stdout_sha256': hashlib.sha256(result.stdout).hexdigest(),
                         'stderr_sha256': hashlib.sha256(result.stderr).hexdigest()})
        dump(output / 'commands.json', commands)
        text = result.stdout.decode(errors='replace')
        if result.returncode != expected or contains is not None and contains not in text + result.stderr.decode(errors='replace'):
            raise RuntimeError(label + ': ' + text[-2500:] + result.stderr.decode(errors='replace')[-2500:])
        print(label, result.returncode, flush=True)
        return text

    source_commit = run('source-commit', ['git', 'rev-parse', 'HEAD']).strip()
    dirty = run('source-status', ['git', 'status', '--porcelain']).strip()
    if dirty:
        raise RuntimeError('Commit the product snapshot before package preparation; keep the driver in its own checkout.')
    if run('dotnet-version', ['dotnet', '--version']).strip() != '11.0.100':
        raise RuntimeError('Exact SDK 11.0.100 required.')
    rust = run('rust-version', ['rustc', '-vV'])
    if not rust.startswith('rustc 1.100.0 '):
        raise RuntimeError('Exact Rust 1.100.0 required.')
    run('native-build', ['cargo', 'build', '--locked', '--release', '-p', 'nativeweave-engine-syncproof'])
    native = product / 'target/release/libnativeweave_engine_syncproof.so'
    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    project = product / 'src/Engines.SyncProof/NativeWeave.Engines.SyncProof.csproj'
    run('facade-restore', ['dotnet', 'restore', project, '--locked-mode', *common])
    run('facade-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common])
    # Reuse A17's actual two-package producer and its JIT/AOT, ownership, bounded
    # decode, ordinary-edit and Rust-invocation-trap conformance consumer.
    package = load_module('perf01_a17_package', product / 'tests/Engines.SyncProof/package_probe.py')
    receipt = package.consume(product, output, native, product, run, environment, False)
    consumer = output / 'consumer'; consumer.mkdir()
    for name in ['Program.cs', 'Consumer.csproj']:
        shutil.copy2(HERE / name, consumer / name)
    shutil.copy2(product / 'global.json', consumer)
    config = ET.Element('configuration')
    sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='candidate', value=str(output / 'packages'))
    ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
    ET.ElementTree(config).write(consumer / 'NuGet.Config')
    env = environment.copy(); env['NUGET_PACKAGES'] = str(output / 'consumer-cache')
    # Fresh exact feeds isolate the same experimental package IDs between variants.
    run('perf-consumer-restore', ['dotnet', 'restore', consumer / 'Consumer.csproj', '-r', 'linux-x64', '-p:PublishAot=true', *common], cwd=consumer, environment=env)
    run('perf-consumer-publish', ['dotnet', 'publish', consumer / 'Consumer.csproj', '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '--no-restore', '-o', output / 'aot', *common], cwd=consumer, environment=env)
    direct_env = environment.copy(); direct_env['CARGO_TARGET_DIR'] = str(output / 'direct-target')
    run('direct-build', ['cargo', 'build', '--locked', '--release', '--manifest-path', HERE / 'native/Cargo.toml'], cwd=HERE, environment=direct_env)
    direct = output / 'direct-target/release/perf01-codec-direct'
    executable = output / 'aot/NativeWeave.Engines.SyncProof.Tests'
    manifest = {'schema_version': 1, 'task': 'Perf-01', 'source_commit': source_commit,
                'source_dirty': False, 'driver_commit': subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip(),
                'driver_sha256': driver_inventory(),
                'a21_sha256': sha(ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py'),
                'managed_executable': str(executable), 'direct_executable': str(direct),
                'native_sha256': sha(native), 'package_receipt': receipt,
                'toolchain': {'dotnet': '11.0.100', 'rust': rust.strip()},
                'build_environment': {name: environment.get(name) for name in ['RUSTFLAGS', 'CFLAGS', 'CXXFLAGS', 'CC', 'DOTNET_GCHeapCount', 'DOTNET_GCServer']},
                'artifacts': {str(path): sha(path) for path in [executable, direct, *sorted((output / 'aot').rglob('*.so')), *sorted((output / 'packages').glob('*.nupkg'))]},
                'consumer_inputs': {str(path): sha(path) for path in consumer.iterdir() if path.is_file() and path.suffix in {'.cs', '.csproj', '.json', '.Config'}},
                'product_locks': {name: sha(product / name) for name in ['global.json', 'rust-toolchain.toml', 'Cargo.lock']},
                'completed_at_utc': utc(), 'executed_performance': False}
    dump(output / 'manifest.json', manifest)
    print(output / 'manifest.json')


def corpus(args):
    output = args.output.resolve()
    subprocess.run([str(args.direct.resolve()), '--make-corpus', '--corpus', str(output)], check=True)
    rows = []
    for index in range(8):
        raw, encoded = output / f'{index}.raw', output / f'{index}.zlib'
        size = [256, 4096, 1048576, 4194304][index // 2]
        if raw.read_bytes() != reference_raw(size, index % 2 == 0):
            raise RuntimeError('Independent Python dataset oracle mismatch.')
        rows.append({'id': index, 'size': size, 'entropy': 'compressible' if index % 2 == 0 else 'pseudorandom',
                     'raw_sha256': sha(raw), 'encoded_sha256': sha(encoded), 'encoded_bytes': encoded.stat().st_size})
    dump(output / 'manifest.json', {'schema_version': 1, 'seed': SEED, 'format': 'zlib', 'level': 6,
                                  'checksum': 'Adler32', 'raw_limit': RAW, 'encoded_limit': ENCODED,
                                  'decoded_limit': RAW, 'producer_sha256': sha(args.direct), 'cases': rows})


def process_sample(pid):
    result = A21.observe_process(pid)
    try:
        fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        result['cpu_seconds'] = (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK')
        result['open_file_descriptors'] = len(list(Path(f'/proc/{pid}/fd').iterdir()))
    except (OSError, ValueError, IndexError):
        result['cpu_seconds'] = None; result['open_file_descriptors'] = None
    try:
        result['affinity'] = AFFINITY.observe(pid)
    except (OSError, ValueError, IndexError):
        result['affinity'] = None
    return result


def loaded_native(pid, expected):
    paths = set()
    for line in Path(f'/proc/{pid}/maps').read_text().splitlines():
        fields = line.split(maxsplit=5)
        if len(fields) == 6 and 'libnativeweave_engine_syncproof.so' in fields[5]:
            paths.add(fields[5])
    observations = [{'path': path, 'sha256': sha(Path(path))} for path in sorted(paths)]
    if len(observations) != 1 or observations[0]['sha256'] != expected:
        raise RuntimeError('Actual mapped codec module differs from the package manifest.')
    return observations


def validate_completion(value, row, corpus_manifest, route):
    if value.get('status') not in {'ok', 'rejected', 'failed'}:
        raise ValueError('unknown completion status')
    if value['status'] != 'ok':
        return
    item = corpus_manifest['cases'][row['case']]
    if value.get('raw_bytes') != item['size']:
        raise ValueError('raw byte-count oracle mismatch')
    if route != 'managed-zlib' and value.get('encoded_bytes') != item['encoded_bytes']:
        raise ValueError('encoded byte-count oracle mismatch')
    if type(value.get('encoded_bytes')) is not int or not 0 < value['encoded_bytes'] <= ENCODED:
        raise ValueError('encoded bound mismatch')
    for key in ['call_ns', 'consume_ns', 'release_ns', 'operation_ns']:
        if type(value.get(key)) is not int or value[key] < 0:
            raise ValueError('invalid timing observation: ' + key)
    if value['operation_ns'] != value['call_ns'] + value['consume_ns'] + value['release_ns']:
        # Independent floating tick-to-nanosecond conversions can differ by two ns.
        if abs(value['operation_ns'] - value['call_ns'] - value['consume_ns'] - value['release_ns']) > 2:
            raise ValueError('inconsistent timing decomposition')


async def measure(args):
    finite = [args.duration_seconds, args.rate, args.warmup_seconds, args.deadline_ms,
              args.max_lag_ms, args.recovery_seconds, args.recovery_rate]
    if not all(math.isfinite(value) for value in finite):
        raise ValueError('Timing/rate values must be finite.')
    if args.duration_seconds <= 0 or args.rate <= 0 or not 1 <= args.workers <= 256 or args.queue <= 0 or args.inflight <= 0 or args.deadline_ms <= 0 or args.max_lag_ms <= 0 or args.recovery_rate <= 0 or args.recovery_seconds < 0 or args.warmup_seconds < 0 or args.idle_seconds < 0:
        raise ValueError('Positive rate/duration/resources/thresholds and 1..256 workers required.')
    if round(1e9 / args.rate) < 1 or round(1e9 / args.recovery_rate) < 1:
        raise ValueError('Arrival periods must be at least one nanosecond.')
    original_cpus = sorted(os.sched_getaffinity(0))
    generator_cpus = args.generator_cpus if args.generator_cpus is not None else original_cpus
    if args.generator_cpus is not None and (len(original_cpus) != 2 or len(generator_cpus) != 2 or set(original_cpus) & set(generator_cpus)):
        raise ValueError('Launch on two target CPUs and select two distinct, disjoint generator CPUs.')
    manifest = json.loads(args.manifest.read_text())
    if driver_inventory() != manifest['driver_sha256'] or sha(ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py') != manifest['a21_sha256']:
        raise RuntimeError('Driver/A21 source changed after artifact preparation; rebuild both variants with the same frozen driver.')
    for name, digest in manifest['artifacts'].items():
        if sha(Path(name)) != digest: raise RuntimeError('Build artifact changed: ' + name)
    for name, digest in manifest['consumer_inputs'].items():
        if sha(Path(name)) != digest: raise RuntimeError('Resolved consumer input/lock changed: ' + name)
    corpus_manifest = json.loads((args.corpus / 'manifest.json').read_text())
    for item in corpus_manifest['cases']:
        for suffix, key in [('raw', 'raw_sha256'), ('zlib', 'encoded_sha256')]:
            if sha(args.corpus / f"{item['id']}.{suffix}") != item[key]: raise RuntimeError('Corpus hash mismatch.')
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    plan = {'schema_version': 1, 'task': 'Perf-01', 'domain': 'codec',
            'source_commit': manifest['source_commit'], 'manifest_sha256': sha(args.manifest),
            'corpus_manifest_sha256': sha(args.corpus / 'manifest.json'),
            'controller_sha256': sha(Path(__file__)), 'a21_sha256': manifest['a21_sha256'],
            'host': A21.host_metadata(), 'seed': SEED, 'route': args.route,
            'affinity_policy': {'mode': 'split-generator-diagnostic' if args.generator_cpus is not None else 'inherited',
                                'target_cpus': original_cpus, 'generator_cpus': generator_cpus,
                                'initial_controller': AFFINITY.observe(os.getpid())},
            'interface': args.interface, 'action': args.action, 'case': args.case,
            'workers': args.workers, 'queue_capacity': args.queue, 'max_in_flight': args.inflight,
            'planned_rate': args.rate, 'period_ns': round(1e9 / args.rate),
            'duration_seconds': args.duration_seconds, 'warmup_seconds': args.warmup_seconds,
            'dispatch_span_guard_seconds': args.max_lag_ms / 1000,
            'idle_seconds': args.idle_seconds, 'deadline_ms': args.deadline_ms, 'max_lag_ms': args.max_lag_ms,
            'recovery_seconds': args.recovery_seconds, 'recovery_rate': args.recovery_rate,
            'sequence': cycle(args.interface, args.action, args.case), 'began_at_utc': utc(),
            'limits': {key: corpus_manifest[key] for key in ['raw_limit', 'encoded_limit', 'decoded_limit', 'level', 'format', 'checksum']},
            'comparison': 'strict bounded single-stream equivalence' if args.route != 'managed-zlib' else 'NOT_COMPARABLE: ZLibStream buffered input does not expose strict trailing-data consumption; valid-corpus algorithm timing only',
            'actual_buffer_kind': 'per-action array or NativeBuffer' if args.route == 'nativeweave' else 'Rust Vec for both requested interface partitions' if args.route == 'direct-rust' else 'managed-array for both requested partitions; owned is an alias, not a NativeBuffer implementation',
            'copied_bytes_scope': 'derived C#/Rust output payload copy only; excludes Vec growth/zeroing, compressor internals, allocator and runtime copies',
            'timing_scope': 'arrival includes IPC/queue/output observation; call isolates public codec call; consume verifies every output byte; release retires ownership; no hashing/file I/O within call'}
    dump(output / 'plan.json', plan)
    executable = manifest['direct_executable'] if args.route == 'direct-rust' else manifest['managed_executable']
    command = [executable, '--corpus', str(args.corpus.resolve()), '--workers', str(args.workers), '--queue', str(args.queue)]
    if args.route != 'direct-rust': command += ['--route', args.route, '--require-aot']
    dump(output / 'command.json', command)
    process = await asyncio.create_subprocess_exec(*command, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    pending, failures = {}, []
    drained = asyncio.Event(); stopped = asyncio.Event()
    raw = (output / 'events.jsonl').open('w'); records = (output / 'operations.jsonl').open('w')
    resources = (output / 'resources.jsonl').open('w')
    stderr_task = asyncio.create_task(process.stderr.read())
    next_id = 0
    measured_start = measured_end = 0
    last_measurement_completion = 0
    last_measurement_completion_utc = None
    start_utc = end_utc = None
    stats = Counter()
    phase_counts = defaultdict(Counter)
    phase_progress = {}

    def check_affinity(phase):
        observed = {'target': AFFINITY.observe(process.pid), 'generator': AFFINITY.observe(os.getpid())}
        plan.setdefault('affinity_observations', {})[phase] = observed
        dump(output / 'plan.json', plan)
        AFFINITY.require(observed['target'], original_cpus)
        AFFINITY.require(observed['generator'], generator_cpus)

    def event(value):
        raw.write(json.dumps({'observer_ns': time.monotonic_ns(), **value}, separators=(',', ':')) + '\n'); raw.flush()

    async def reader():
        nonlocal last_measurement_completion, last_measurement_completion_utc
        try:
            while line := await process.stdout.readline():
                now = time.monotonic_ns()
                value = json.loads(line)
                event(value)
                kind = value.get('kind')
                if kind in ['admitted', 'completed']:
                    identifier = value['id']
                    if identifier not in pending: raise ValueError('unknown/duplicate request ID')
                    row = pending[identifier]
                    if kind == 'admitted':
                        if 'admitted_ns' in row: raise ValueError('duplicate admission')
                        row['admitted_ns'] = now
                        phase_counts[row['phase']]['admitted'] += 1
                    else:
                        validate_completion(value, row, corpus_manifest, args.route)
                        status = value['status']
                        if status == 'ok' and 'admitted_ns' not in row: raise ValueError('success without admission')
                        row.update(target=value, completion_ns=now, terminal_status=status,
                                   outcome='timeout' if now > row['scheduled_ns'] + int(args.deadline_ms * 1e6) else status)
                        records.write(json.dumps(row, separators=(',', ':')) + '\n'); records.flush()
                        phase_counts[row['phase']]['completed'] += 1
                        phase_counts[row['phase']][row['outcome']] += 1
                        if row['phase'] == 'measurement':
                            stats[row['outcome']] += 1
                            last_measurement_completion = now
                            last_measurement_completion_utc = utc()
                        if status == 'failed': failures.append('correctness_failure')
                        del pending[identifier]
                elif kind == 'drained':
                    if pending: failures.append('drained_with_unfinished_requests')
                    native = value.get('native')
                    if native and any(native[key] != 0 for key in ['live', 'capacity', 'active']): failures.append('native_owners_not_drained')
                    if value.get('errors', 0): failures.append('target_reported_correctness_failure')
                    drained.set()
                elif kind not in {'telemetry', 'snapshot', 'idle'}:
                    raise ValueError('unknown target event kind')
        except (ValueError, KeyError, TypeError) as error:
            failures.append('event_integrity:' + str(error)); stopped.set()

    async def sample():
        while not stopped.is_set():
            observation = {'observer_ns': time.monotonic_ns(), 'target': process_sample(process.pid), 'generator': process_sample(os.getpid())}
            resources.write(json.dumps(observation, separators=(',', ':')) + '\n'); resources.flush()
            for name, expected in [('target', original_cpus), ('generator', generator_cpus)]:
                snapshot = observation[name]['affinity']
                if snapshot is None:
                    if name == 'generator' or process.returncode is None:
                        failures.append('affinity_unavailable:' + name); stopped.set()
                    continue
                try:
                    AFFINITY.require(snapshot, expected)
                except ValueError as error:
                    failures.append('affinity_mismatch:' + str(error)); stopped.set()
            try: await asyncio.wait_for(stopped.wait(), 1)
            except asyncio.TimeoutError: pass

    async def schedule(phase, seconds, rate=None):
        nonlocal next_id, measured_start, measured_end, start_utc
        period = plan['period_ns'] if rate is None else round(1e9 / rate)
        # Extra declared dispatch guard makes the actual first-to-last sent span
        # reach the requested duration even at the allowed initial dispatch lag.
        count = math.ceil((seconds + args.max_lag_ms / 1000) * 1e9 / period) + 1
        origin = time.monotonic_ns() + 20_000_000
        phase_counts[phase]['planned'] = count
        phase_progress[phase] = {'planned_origin_ns': origin, 'planned_end_ns': origin + (count-1)*period,
                                 'first_dispatch_ns': None, 'last_dispatch_ns': None, 'completed': False}
        if phase == 'measurement': measured_start, measured_end = origin, origin + (count-1)*period
        dump(output / 'phase-progress.json', phase_progress)
        first_dispatch = last_dispatch = None
        began = None
        for index in range(count):
            scheduled = origin + index * period
            delay = scheduled - time.monotonic_ns()
            if delay > 0: await asyncio.sleep(delay / 1e9)
            now = time.monotonic_ns()
            if stopped.is_set(): raise RuntimeError('Target integrity failed during scheduled load.')
            item = plan['sequence'][index % len(plan['sequence'])]
            row = {'id': next_id, 'phase': phase, 'case': item[0], 'action': item[1], 'interface': item[2], 'scheduled_ns': scheduled, 'dispatch_ns': now}
            next_id += 1
            if len(pending) >= args.inflight:
                row.update(outcome='generator_capacity', completion_ns=now)
                records.write(json.dumps(row) + '\n'); records.flush()
                if phase == 'measurement': failures.append('generator_capacity')
                phase_counts[phase]['generator_capacity'] += 1
                continue
            if phase == 'measurement' and now-scheduled > args.max_lag_ms * 1e6: failures.append('generator_dispatch_lag')
            pending[row['id']] = row
            event({'kind': 'dispatch', **row})
            phase_counts[phase]['dispatch_attempted'] += 1
            process.stdin.write(f"{row['id']}\t{item[0]}\t{item[1]}\t{item[2]}\n".encode())
            await asyncio.wait_for(process.stdin.drain(), args.deadline_ms / 1000)
            phase_counts[phase]['sent'] += 1
            if first_dispatch is None:
                first_dispatch = now; began = utc()
                phase_progress[phase]['first_dispatch_ns'] = now
                phase_progress[phase]['first_dispatch_utc'] = began
                if phase == 'measurement': start_utc = began
            last_dispatch = now
            phase_progress[phase]['last_dispatch_ns'] = now
        timeout = time.monotonic() + max(30, args.deadline_ms / 1000)
        while pending and time.monotonic() < timeout and not stopped.is_set(): await asyncio.sleep(.01)
        if pending: raise RuntimeError('Scheduled load failed to retire before drain budget.')
        dispatch_span = (last_dispatch-first_dispatch)/1e9 if first_dispatch is not None else 0
        if phase == 'measurement' and dispatch_span < seconds: failures.append('insufficient_effective_dispatch_span')
        phase_progress[phase].update(completed=True, dispatch_span_seconds=dispatch_span)
        dump(output / 'phase-progress.json', phase_progress)
        return origin, origin + (count-1)*period, began, dispatch_span

    reader_task = sample_task = None
    try:
        # The target was spawned on the original mask. Move only this Python
        # controller after spawn, including any already-created child watcher.
        # A rejected mask remains inside this try so the target is always reaped.
        if args.generator_cpus is not None:
            plan['affinity_policy']['controller_after_move'] = AFFINITY.pin_current_threads(generator_cpus)
            dump(output / 'plan.json', plan)
        ready = json.loads(await asyncio.wait_for(process.stdout.readline(), 60)); event(ready)
        if ready.get('kind') != 'ready' or ready.get('dynamic_code_supported') is not False:
            raise RuntimeError('Target did not establish real AOT/direct readiness.')
        if ready.get('workers') != args.workers or ready.get('queue_capacity') != args.queue:
            raise RuntimeError('Target resource configuration differs from frozen plan.')
        if args.route == 'nativeweave':
            plan['actual_loaded_native'] = loaded_native(process.pid, manifest['native_sha256'])
        check_affinity('ready')
        reader_task = asyncio.create_task(reader()); sample_task = asyncio.create_task(sample())
        if args.warmup_seconds: await schedule('warmup', args.warmup_seconds)
        process.stdin.write(b'snapshot\n'); await process.stdin.drain()
        resource_before = process_sample(process.pid)
        measured_start, measured_end, start_utc, dispatch_span = await schedule('measurement', args.duration_seconds)
        resource_after = process_sample(process.pid)
        check_affinity('after_measurement')
        end_utc = last_measurement_completion_utc
        if args.recovery_seconds:
            await schedule('recovery', args.recovery_seconds, args.recovery_rate)
        process.stdin.write(b'drain\n'); await process.stdin.drain()
        await asyncio.wait_for(drained.wait(), 30)
        drain_utc = utc()
        check_affinity('drained')
        # The whole idle interval is separately timestamped and never receives load credit.
        idle_start = time.monotonic_ns()
        for _ in range(args.idle_seconds):
            process.stdin.write(b'snapshot\n'); await process.stdin.drain(); await asyncio.sleep(1)
        idle_end = time.monotonic_ns()
        check_affinity('after_idle')
        # Sampling ends while the target still waits on stdin. Its expected
        # subsequent exit must not be mistaken for an unobservable live mask.
        stopped.set()
        process.stdin.close(); await asyncio.wait_for(process.wait(), 30)
        await reader_task
        if process.returncode != 0: failures.append('target_exit_' + str(process.returncode))
        outcome = {'valid': not failures, 'invalid_reasons': sorted(set(failures)), 'measurement_start_utc': start_utc,
                   'measurement_end_utc': end_utc, 'scheduled_start_ns': measured_start, 'scheduled_end_ns': measured_end,
                   'effective_scheduled_load_seconds': (measured_end-measured_start)/1e9,
                   'actual_dispatch_span_seconds': dispatch_span,
                   'measurement_observation_seconds': (max(measured_end, last_measurement_completion)-measured_start)/1e9,
                   'measurement_resources_before': resource_before, 'measurement_resources_after': resource_after,
                   'drained_at_utc': drain_utc, 'idle_observation_seconds': (idle_end-idle_start)/1e9,
                   'operation_outcomes': dict(stats), 'service_standard_met': not failures and all(stats[k] == 0 for k in ['failed', 'timeout', 'rejected']),
                   'phase_counts': {key: dict(value) for key, value in phase_counts.items()},
                   'recovery_standard_met': bool(phase_counts['recovery']['ok']) and all(phase_counts['recovery'][key] == 0 for key in ['failed', 'timeout', 'rejected', 'generator_capacity']) if args.recovery_seconds else None,
                   'exit_code': process.returncode, 'complete': True}
    except BaseException as error:
        stopped.set()
        if process.returncode is None:
            try: process.kill()
            except ProcessLookupError: pass
            await process.wait()
        if reader_task and not reader_task.done(): await reader_task
        for row in pending.values():
            records.write(json.dumps({**row, 'outcome': 'interrupted', 'completion_ns': None}, separators=(',', ':')) + '\n')
        records.flush()
        outcome = {'valid': False, 'complete': False, 'invalid_reasons': sorted(set(failures + [type(error).__name__ + ': ' + str(error)])),
                   'measurement_start_utc': start_utc, 'measurement_end_utc': last_measurement_completion_utc,
                   'operation_outcomes': dict(stats), 'phase_counts': {key: dict(value) for key, value in phase_counts.items()},
                   'pending_request_ids': sorted(pending), 'phase_progress': phase_progress,
                   'duration_credit_seconds': 0}
    finally:
        stopped.set()
        if sample_task: await sample_task
        if reader_task and not reader_task.done(): reader_task.cancel()
        raw.close(); records.close(); resources.close()
        (output / 'target.stderr').write_bytes(await stderr_task)
        dump(output / 'phase-progress.json', phase_progress)
        if args.generator_cpus is not None:
            try:
                restored = AFFINITY.pin_current_threads(original_cpus)
                dump(output / 'controller-affinity-restored.json', restored)
            except BaseException as error:
                outcome['valid'] = False
                outcome['invalid_reasons'].append('affinity_restore:' + type(error).__name__ + ': ' + str(error))
    outcome['closed_at_utc'] = utc()
    dump(output / 'result.json', outcome)
    summarize(output)
    print(json.dumps(outcome, indent=2))
    if not outcome['valid']: raise SystemExit(1)


def summarize(output):
    plan = json.loads((output / 'plan.json').read_text())
    result = json.loads((output / 'result.json').read_text())
    def new_group():
        return {'offered': 0, 'successes': 0, 'outcomes': Counter(), 'raw': 0, 'encoded': 0, 'call': 0,
                'allocated': 0, 'allocated_known': True, 'copied': 0, 'copied_known': True,
                'all_arrivals': array('Q'),
                'latency': {key: array('Q') for key in ['arrival_ns', 'call_ns', 'consume_ns', 'release_ns', 'operation_ns']}}
    groups = defaultdict(new_group)
    all_success_arrivals = array('Q')
    total = 0; successes = 0
    with (output / 'operations.jsonl').open() as observations:
        for line in observations:
            row = json.loads(line)
            if row['phase'] != 'measurement': continue
            total += 1
            group = groups[f"{row['action']}/{row['interface']}/case-{row['case']}"]
            group['offered'] += 1; group['outcomes'][row['outcome']] += 1
            if row['completion_ns'] is not None: group['all_arrivals'].append(row['completion_ns']-row['scheduled_ns'])
            if row['outcome'] != 'ok': continue
            successes += 1; group['successes'] += 1
            target = row['target']
            for key in ['raw', 'encoded']: group[key] += target[key + '_bytes']
            group['call'] += target['call_ns']
            group['latency']['arrival_ns'].append(row['completion_ns']-row['scheduled_ns'])
            all_success_arrivals.append(row['completion_ns']-row['scheduled_ns'])
            for key in ['call_ns', 'consume_ns', 'release_ns', 'operation_ns']: group['latency'][key].append(target[key])
            for key, source in [('allocated', 'managed_allocated_bytes'), ('copied', 'copied_bytes_derived')]:
                if target[source] is None: group[key + '_known'] = False
                else: group[key] += target[source]
    span = result.get('measurement_observation_seconds')
    metrics = {}
    for key, group in groups.items():
        call, raw, encoded = group['call'], group['raw'], group['encoded']
        metrics[key] = {'offered': group['offered'], 'successes': group['successes'], 'outcomes': dict(group['outcomes']),
                        'successes_per_second': group['successes'] / span if span else None,
                        'raw_mib_per_second_wall': raw / 1048576 / span if span else None,
                        'raw_mib_per_second_summed_codec_time': raw / 1048576 / (call / 1e9) if call else None,
                        'raw_bytes': raw, 'encoded_bytes': encoded, 'compression_ratio_raw_over_encoded': raw / encoded if encoded else None,
                        **{metric: {label: A21.percentile(values, q) for label, q in [('p50', .5), ('p95', .95), ('p99', .99)]} for metric, values in group['latency'].items()},
                        'arrival_all_outcomes_ns': {label: A21.percentile(group['all_arrivals'], q) for label, q in [('p50', .5), ('p95', .95), ('p99', .99)]},
                        'managed_allocated_bytes': group['allocated'] if group['successes'] and group['allocated_known'] else None,
                        'copied_bytes_derived': group['copied'] if group['successes'] and group['copied_known'] else None,
                        'native_allocations': None, 'native_allocation_reason': 'No allocator observer; result-owner counters exclude upstream workspace and allocator retention.'}
    before, after = result.get('measurement_resources_before', {}), result.get('measurement_resources_after', {})
    cpu = after.get('cpu_seconds') - before.get('cpu_seconds') if before.get('cpu_seconds') is not None and after.get('cpu_seconds') is not None else None
    dump(output / 'summary.json', {'source_commit': plan['source_commit'], 'route': plan['route'], 'comparison': plan['comparison'],
                                  'valid': result['valid'], 'offered': total, 'successes': successes,
                                  'successful_operations_per_second': successes / span if span else None,
                                  'arrival_success_ns': {label: A21.percentile(all_success_arrivals, q) for label, q in [('p50', .5), ('p95', .95), ('p99', .99)]},
                                  'target_cpu_seconds': cpu, 'target_cpu_percent_one_core': cpu/span*100 if cpu is not None and span else None,
                                  'rss_before_bytes': before.get('rss_bytes', {}).get('value'), 'rss_after_bytes': after.get('rss_bytes', {}).get('value'),
                                  'cpu_scope': 'procfs process CPU bracket, includes bounded driver/oracle/telemetry work; per-action codec CPU is unavailable',
                                  'groups': metrics, 'files': {name: sha(output / name) for name in ['plan.json', 'result.json', 'events.jsonl', 'operations.jsonl', 'resources.jsonl']}})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    build = commands.add_parser('prepare'); build.add_argument('--product-root', type=Path, required=True); build.add_argument('--output', type=Path, required=True)
    data = commands.add_parser('corpus'); data.add_argument('--direct', type=Path, required=True); data.add_argument('--output', type=Path, required=True)
    run = commands.add_parser('run')
    run.add_argument('--manifest', type=Path, required=True); run.add_argument('--corpus', type=Path, required=True)
    run.add_argument('--route', choices=['nativeweave', 'direct-rust', 'managed-zlib'], required=True)
    run.add_argument('--interface', choices=['mixed', 'array', 'owned'], default='mixed')
    run.add_argument('--action', choices=['mixed', 'compress', 'decompress'], default='mixed')
    run.add_argument('--case', type=int, choices=range(8))
    run.add_argument('--workers', type=int, required=True); run.add_argument('--queue', type=int, default=64)
    run.add_argument('--inflight', type=int, default=256); run.add_argument('--rate', type=float, required=True)
    run.add_argument('--duration-seconds', type=float, required=True); run.add_argument('--warmup-seconds', type=float, default=20)
    run.add_argument('--idle-seconds', type=int, default=60); run.add_argument('--deadline-ms', type=float, default=1000)
    run.add_argument('--recovery-seconds', type=float, default=10); run.add_argument('--recovery-rate', type=float, default=1)
    run.add_argument('--max-lag-ms', type=float, default=50); run.add_argument('--output', type=Path, required=True)
    run.add_argument('--generator-cpus', type=AFFINITY.cpu_pair,
                     help='Optional two-CPU generator diagnostic, e.g. 0,1; launch on two disjoint target CPUs.')
    args = parser.parse_args()
    if args.command == 'prepare': prepare(args)
    elif args.command == 'corpus': corpus(args)
    elif args.command == 'run': asyncio.run(measure(args))


if __name__ == '__main__': main()
