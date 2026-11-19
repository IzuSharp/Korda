#!/usr/bin/env python3
"""Prepare immutable protocol loads and run one exclusively scheduled finite cell.

The compact adapter wire avoids repeating nineteen unavailable counters per operation.
Oracle, RunState, status validation, histogram/nearest-rank rules and noise calculations
come from A21. No measured result is produced by `prepare`.
"""
import argparse
import asyncio
from array import array
from collections import Counter, defaultdict
import datetime
import fcntl
import hashlib
import heapq
import importlib.util
import json
import math
import os
from pathlib import Path
import random
import signal
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')
    temporary.replace(path)


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def utc(ns):
    return datetime.datetime.fromtimestamp(ns / 1e9, datetime.timezone.utc).isoformat()


def fixture_hashes():
    return {size: hashlib.sha256(bytes((i * 31 + (i >> 8) * 17 + 20261119) & 255 for i in range(size))).hexdigest()
            for size in (64, 4096, 65536, 1048576, 8388608)}


def prepare(args):
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    profile = json.loads(args.profile.read_text())
    build = json.loads(args.build.read_text())
    route = build.get('route', 'nativeweave-aot-rust')
    client_build_path = (args.client_build or args.build).resolve()
    client_build = json.loads(client_build_path.read_text())
    if not build['eligible_for_measurement'] or not client_build['eligible_for_measurement']:
        raise ValueError('Compilation-only outputs cannot be measured')
    expected_server_isa = 'x86-64-default' if route == 'direct-rust' else 'x86-64-v2'
    if build['toolchain'].get('isa') != expected_server_isa or client_build['toolchain'].get('isa') != 'x86-64-v2':
        raise ValueError('Pinned Native AOT x86-64-v2 / generic x86-64 Rust producers are required')
    if not 0 < args.rate <= 10000 or not 0 < args.duration <= 1800 or not 1 <= args.concurrency <= 512:
        raise ValueError('Invalid finite rate, duration or concurrency')
    if route not in {'nativeweave-aot-rust', 'direct-rust'}: raise ValueError('Unknown route')
    period = round(1e9 / args.rate)
    # The first dispatch may lag while the final one catches up. Preserve an
    # extra lag allowance, then still require the ACTUAL dispatch span below.
    count = math.ceil((args.duration * 1e9 + profile['timing']['max_dispatch_lag_ns']) / period) + 1
    if count > 1_000_000: raise ValueError('A21 finite observation bound is one million operations')
    if bool(args.certificate) != bool(args.private_key): raise ValueError('Provide both TLS files or neither')
    if args.certificate:
        certificate, key = args.certificate.resolve(), args.private_key.resolve()
    else:
        certificate, key = out / 'certificate.pem', out / 'private-key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key), '-out', str(certificate),
                        '-days', '7', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        key.chmod(0o600)
    pattern, warmup = [], []
    for item in profile[args.group + '_mix']:
        protocols = ['http1-tls', 'http2-tls'] if args.group == 'http' else ['http2-tls']
        for protocol in protocols:
            token = '|'.join(map(str, (item['scenario'], protocol, item['bytes'], item['messages'])))
            pattern.extend([token] * item['weight']); warmup.append(token)
    random.Random(profile['seed']).shuffle(pattern)
    dataset = [{'input': pattern[index % len(pattern)], 'expected': pattern[index % len(pattern)]} for index in range(count)]
    save(out / 'oracle.json', dataset)
    save(out / 'profile.json', profile)
    server_config = dict(profile['server'], certificate=str(certificate), private_key=str(key))
    save(out / 'server-config.json', server_config)
    client_config = dict(profile['client'], certificate=str(certificate), concurrency=args.concurrency, expected_hashes=fixture_hashes())
    a21 = ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py'
    common = ROOT / 'src/Benchmarks.Domain/common.py'
    artifacts = {}
    for inputs in (build, client_build):
        for name in ['artifacts_sha256', 'lock_hashes', 'source_files_sha256', 'package_archives_sha256', 'resolved_package_archives_sha256', 'producer_manifests_sha256']:
            artifacts.update(inputs.get(name, {}))
    for path in [HERE / 'run.py', HERE / 'adapter.py', a21, common, ROOT / 'src/Benchmarks.Domain/supervision.py',
                 args.build.resolve(), client_build_path, certificate, key, out / 'oracle.json', out / 'profile.json', out / 'server-config.json']:
        artifacts[str(path)] = sha(path)
    for path, digest in artifacts.items():
        if sha(path) != digest: raise ValueError('Build artifact no longer matches: ' + path)
    timing = profile['timing']
    manifest = {'schema_version': 1, 'task': 'Perf-01', 'route': route, 'group': args.group,
                'a21': str(a21), 'domain_common': str(common), 'run_directory': str(out / 'execution'),
                'server': build['server'], 'client': client_build['client'], 'server_config': str(out / 'server-config.json'),
                'client_config': client_config, 'client_timeout_seconds': client_config['timeout_seconds'],
                'native_sha256': build.get('server_native_sha256', {}), 'process_cpu_count': profile['process_cpu_count'],
                'idle_seconds': timing['idle_seconds'], 'requests': count, 'artifacts': artifacts}
    save(out / 'adapter.json', manifest)
    schedule = {'group': args.group, 'rate': args.rate, 'period_ns': period, 'duration_seconds': args.duration,
                'concurrency': args.concurrency, 'profile_sha256': sha(out / 'profile.json'), 'oracle_sha256': sha(out / 'oracle.json'),
                'certificate_sha256': sha(certificate), 'client_executable_sha256': sha(client_build['client']),
                'application_sources_sha256': {name: digest for name, digest in client_build.get('driver_source_sha256', {}).items() if name.endswith(('.cs', '.proto', '.csproj'))}}
    if route == 'nativeweave-aot-rust':
        for name, digest in build['driver_source_sha256'].items():
            if name.endswith(('.cs', '.proto', '.csproj')) and schedule['application_sources_sha256'].get(name) != digest:
                raise ValueError('Before/after application/fixture source differs from the selected common client build: ' + name)
    identity = hashlib.sha256(json.dumps(schedule, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    plan = {'schema_version': 1, 'task': 'Perf-01', 'route': route, 'workload_kind': 'protocol-' + args.group,
            'schedule': schedule, 'comparison_identity_sha256': identity, 'build_manifest': str(args.build.resolve()),
            'client_build_manifest': str(client_build_path), 'profile_sha256': sha(out / 'profile.json'),
            'manifest': str(out / 'adapter.json'), 'manifest_sha256': sha(out / 'adapter.json'),
            'oracle': {'kind': 'expected-values', 'dataset': str(out / 'oracle.json'), 'sha256': sha(out / 'oracle.json')},
            'settings': {'warmup_requests': timing['warmup_requests'], 'warmup_inputs': warmup, 'isa_profile': 'x86-64-v2',
                         'recovery_requests': timing['recovery_requests'], 'recovery_period_seconds': timing['recovery_period_seconds'],
                         'instrumentation': '1 Hz separate process/GC/native gauge observations, raw client outcomes and A21 arrival events'},
            'timing': {'requests': count, 'period_ns': period, **{k: timing[k] for k in ['timeout_ns', 'max_dispatch_lag_ns', 'max_in_flight', 'cleanup_timeout_ns']}},
            'histogram': {'resolution_ns': timing['histogram_resolution_ns'], 'maximum_ns': timing['histogram_maximum_ns']},
            'service_standard': profile['service_standard'], 'artifacts': artifacts,
            'notice': 'A single route cell; does not assert the A21 three-comparator plan gate. Same immutable comparison identity is required before/after.'}
    save(out / 'plan.json', plan)
    print(out / 'plan.json')


async def run_cell(plan_path):
    if len(os.sched_getaffinity(0)) != 2: raise ValueError('Run with the frozen two-CPU affinity, using taskset')
    loop = asyncio.get_running_loop(); current = asyncio.current_task()
    loop.add_signal_handler(signal.SIGTERM, current.cancel)
    plan = json.loads(plan_path.read_text()); base = plan_path.parent
    manifest = Path(plan['manifest'])
    if sha(manifest) != plan['manifest_sha256']: raise ValueError('Adapter manifest changed')
    for path, digest in plan['artifacts'].items():
        if sha(path) != digest: raise ValueError('Frozen input/artifact changed: ' + path)
    if (base / 'result.json').exists() or (base / 'execution').exists():
        raise ValueError('Results and partial attempts are immutable; prepare a new attempt directory')
    adapter = json.loads(manifest.read_text())
    a21 = load_module('perf01_measurement_a21', adapter['a21'])
    oracle = a21.Oracle(plan, base)
    timing = plan['timing']; state = a21.RunState(timing['requests'], oracle)
    counters = {name: a21.unknown(unit, 'Not observed per operation; separate process/GC/native-gauge time series') for name, unit in a21.COUNTERS.items()}
    result = {'status': 'incomplete', 'valid': False, 'plan_sha256': sha(plan_path), 'comparison_identity_sha256': plan['comparison_identity_sha256'],
              'controller_pid': os.getpid(), 'controller_affinity': sorted(os.sched_getaffinity(0)), 'host': a21.host_metadata(),
              'route': plan['route'], 'workload_kind': plan['workload_kind'], 'unavailable_per_operation_counters': counters}
    save(base / 'started.json', result)
    process = await asyncio.create_subprocess_exec(sys.executable, str(HERE / 'adapter.py'), '--manifest', str(manifest),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, start_new_session=True)
    stderr_task = asyncio.create_task(process.stderr.read())
    raw = (base / 'events.jsonl').open('w')
    dispatch_log = (base / 'dispatches.jsonl').open('w', buffering=1)
    stopped = asyncio.Event(); reader = None; origin = None
    recovery_waiters = {}; recovery_inputs = {}; recovery_admitted = set(); recovery_records = []
    async def read_events():
        try:
            while line := await process.stdout.readline():
                observed = time.monotonic_ns()
                # The exact wire JSON is preserved verbatim; valid JSON strings cannot contain literal line feeds.
                raw.write('{"observed_ns":' + str(observed) + ',"wire":' + json.dumps(line.decode('utf8', errors='replace')) + '}\n')
                event = json.loads(line)
                if event.get('id') in recovery_waiters:
                    identifier = event['id']
                    recovery_records.append({'observed_ns': observed, **event})
                    if event.get('kind') == 'admitted':
                        if identifier in recovery_admitted: raise ValueError('Duplicate recovery admission')
                        recovery_admitted.add(identifier)
                    elif event.get('kind') == 'completed':
                        if identifier not in recovery_admitted or recovery_waiters[identifier].done(): raise ValueError('Out-of-order recovery completion')
                        if event.get('status') not in a21.OUTCOMES or event.get('value') != recovery_inputs[identifier]:
                            raise ValueError('Recovery wire status/oracle mismatch')
                        recovery_waiters[identifier].set_result(event)
                    else: raise ValueError('Unknown recovery event')
                    continue
                if event.get('kind') == 'completed': event['counters'] = counters
                state.event(event, observed, len(line))
        except (ValueError, KeyError, TypeError, UnicodeError) as error:
            state.errors.append('integrity: ' + str(error)); stopped.set()
        finally:
            raw.flush()
            if process.returncode not in (None, 0): stopped.set()
    try:
        hello = json.loads(await asyncio.wait_for(process.stdout.readline(), 20))
        if hello.get('kind') != 'hello' or hello.get('route') != plan['route'] or hello.get('dynamic_code_supported') is not False:
            raise ValueError('Invalid target hello')
        result['hello'] = hello
        inputs = plan['settings']['warmup_inputs']
        for index in range(plan['settings']['warmup_requests']):
            identifier = timing['requests'] + index; token = inputs[index % len(inputs)]
            process.stdin.write(f'{identifier}\t{token}\tok\n'.encode()); await process.stdin.drain()
            admitted = json.loads(await asyncio.wait_for(process.stdout.readline(), 25))
            complete = json.loads(await asyncio.wait_for(process.stdout.readline(), 25))
            if admitted != {'kind': 'admitted', 'id': identifier} or complete != {'kind': 'completed', 'id': identifier, 'status': 'ok', 'value': token}:
                raise ValueError('Warmup correctness failed: ' + repr((admitted, complete)))
        origin = time.monotonic_ns() + 20_000_000
        anchor_monotonic, anchor_utc = time.monotonic_ns(), time.time_ns()
        reader = asyncio.create_task(read_events())
        for row in state.records:
            row['scheduled_ns'] = origin + row['id'] * timing['period_ns']
            row['deadline_ns'] = row['scheduled_ns'] + timing['timeout_ns']
            remaining = row['scheduled_ns'] - time.monotonic_ns()
            if remaining > 0: await asyncio.sleep(remaining / 1e9)
            now = time.monotonic_ns(); state.expire(now)
            if stopped.is_set():
                row['completion_ns'], row['status'] = now, 'cancelled'; continue
            if now >= row['deadline_ns']:
                row.update(completion_ns=now, status='timeout', reason='generator_missed_deadline')
                state.errors.append('generator saturation: missed deadline'); continue
            if len(state.in_flight) >= timing['max_in_flight']:
                row.update(completion_ns=now, status='rejected', reason='generator_capacity')
                state.errors.append('generator saturation: in-flight capacity'); continue
            row['dispatch_ns'], row['attempts'] = now, 1
            dispatch_log.write(json.dumps({'id': row['id'], 'scheduled_ns': row['scheduled_ns'], 'dispatch_ns': now,
                                          'dispatch_utc_ns': anchor_utc + now - anchor_monotonic}, separators=(',', ':')) + '\n')
            state.in_flight.add(row['id']); heapq.heappush(state.deadlines, (row['deadline_ns'], row['id']))
            if now - row['scheduled_ns'] > timing['max_dispatch_lag_ns']: state.errors.append('generator saturation: dispatch lag')
            request = f"{row['id']}\t{oracle.input(row['id'])}\tok\n".encode(); row['request_wire_bytes'] = len(request)
            process.stdin.write(request)
            try: await asyncio.wait_for(process.stdin.drain(), timing['timeout_ns'] / 1e9)
            except (BrokenPipeError, ConnectionResetError, asyncio.TimeoutError):
                state.errors.append('generator/target input stalled or closed'); stopped.set()
        while any(row['status'] is None for row in state.records) and not stopped.is_set():
            state.expire(time.monotonic_ns()); await asyncio.sleep(.001)
        for row in state.records:
            if row['status'] is None: row['completion_ns'], row['status'] = time.monotonic_ns(), 'cancelled'
        result['recovery'] = {'status': 'incomplete', 'start_utc': utc(time.time_ns()), 'calls': 0, 'failures': []}
        async def recover():
            # The original server/client remain alive. Each request is admitted
            # only after the previous one completes; recovery is not timed load.
            for index in range(plan['settings']['recovery_requests']):
                token = inputs[index % len(inputs)]
                identifier = timing['requests'] + plan['settings']['warmup_requests'] + index
                recovery_waiters[identifier] = asyncio.get_running_loop().create_future()
                recovery_inputs[identifier] = token
                process.stdin.write(f'{identifier}\t{token}\tok\n'.encode()); await process.stdin.drain()
                complete = await asyncio.wait_for(recovery_waiters[identifier], 25)
                result['recovery']['calls'] += 1
                if complete.get('status') != 'ok' or complete.get('value') != token:
                    result['recovery']['failures'].append(complete)
                await asyncio.sleep(plan['settings']['recovery_period_seconds'])
        try:
            await asyncio.wait_for(recover(), 60)
            result['recovery']['status'] = 'passed' if not result['recovery']['failures'] else 'failed'
        except asyncio.TimeoutError:
            result['recovery']['status'] = 'failed'; result['recovery']['failures'].append('Recovery did not complete within 60 seconds')
        result['recovery']['end_utc'] = utc(time.time_ns()); result['recovery']['events'] = recovery_records
        process.stdin.close()
        try: await asyncio.wait_for(process.wait(), timing['cleanup_timeout_ns'] / 1e9)
        except asyncio.TimeoutError:
            state.errors.append('cleanup deadline exceeded; owner retirement unknown')
            os.killpg(process.pid, signal.SIGKILL); await process.wait()
        await reader
        if process.returncode != 0: state.errors.append('adapter exited with ' + str(process.returncode))
        end = max(max(row['completion_ns'] for row in state.records), state.last_completion_observed_ns or origin)
        elapsed = end - origin
        histograms = {}
        for name, start, finish, successful in [('arrival_all_outcomes', 'scheduled_ns', 'completion_ns', False),
            ('arrival_success', 'scheduled_ns', 'completion_ns', True), ('dispatch_delay', 'scheduled_ns', 'dispatch_ns', False),
            ('queue_and_ipc', 'dispatch_ns', 'admission_ns', False), ('service_observer', 'admission_ns', 'completion_ns', True)]:
            values = [row[finish] - row[start] for row in state.records if row[start] is not None and row[finish] is not None and (not successful or row['status'] == 'ok')]
            histograms[name] = a21.histogram(values, plan['histogram']['resolution_ns'], plan['histogram']['maximum_ns'])
        result.update(status='completed', invalid_reasons=sorted(set(state.errors)), outcome_counts=dict(Counter(row['status'] for row in state.records)),
            offered=len(state.records), actually_dispatched=sum(row['dispatch_ns'] is not None for row in state.records),
            admitted=sum(row['admission_ns'] is not None for row in state.records), completed=len(state.seen_completion),
            successful=sum(row['status'] == 'ok' for row in state.records), histograms=histograms,
            scheduled_arrival_rate_per_second=1e9 / timing['period_ns'],
            success_per_second=sum(row['status'] == 'ok' for row in state.records) * 1e9 / max(1, elapsed),
            measurement_window={'start_monotonic_ns': origin, 'last_scheduled_ns': state.records[-1]['scheduled_ns'],
                'end_monotonic_ns': end, 'start_utc': utc(anchor_utc + origin - anchor_monotonic),
                'end_utc': utc(anchor_utc + end - anchor_monotonic), 'elapsed_ns': elapsed,
                'scheduled_active_ns': state.records[-1]['scheduled_ns'] - origin}, late_events=state.late_events)
        dispatched = [row['dispatch_ns'] for row in state.records if row['dispatch_ns'] is not None]
        if dispatched:
            result['measurement_window'].update(first_dispatch_ns=min(dispatched), last_dispatch_ns=max(dispatched),
                actual_dispatch_span_ns=max(dispatched) - min(dispatched), first_dispatch_utc=utc(anchor_utc + min(dispatched) - anchor_monotonic),
                last_dispatch_utc=utc(anchor_utc + max(dispatched) - anchor_monotonic))
        # Every field remains available without duplicating the identical unavailable-counter dictionary N times.
        with (base / 'requests.jsonl').open('w') as records:
            for row in state.records:
                row.pop('target_counters', None)
                records.write(json.dumps(row, separators=(',', ':')) + '\n')
        summarize(plan, result, state.records, a21, base)
        result['valid'] = not result['invalid_reasons']
    except BaseException as error:
        result.update(status='failed', valid=False, failure=type(error).__name__ + ': ' + str(error)[:2000])
        raise
    finally:
        if process.returncode is None:
            os.killpg(process.pid, signal.SIGTERM)
            try: await asyncio.wait_for(process.wait(), 5)
            except asyncio.TimeoutError: os.killpg(process.pid, signal.SIGKILL); await process.wait()
        # Process group includes server and client; do not orphan workloads on controller interruption.
        try: os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError: pass
        if reader and not reader.done(): reader.cancel()
        if reader: await asyncio.gather(reader, return_exceptions=True)
        raw.close(); dispatch_log.close()
        if result['status'] != 'completed':
            with (base / 'requests.partial.jsonl').open('w') as records:
                for row in state.records:
                    row.pop('target_counters', None); records.write(json.dumps(row, separators=(',', ':')) + '\n')
            dispatched = [row['dispatch_ns'] for row in state.records if row['dispatch_ns'] is not None]
            result['partial_load'] = {'dispatched': len(dispatched), 'completed': len(state.seen_completion),
                'pending': len(state.in_flight), 'outcomes': dict(Counter(row['status'] for row in state.records)),
                'actual_dispatch_span_ns': max(dispatched) - min(dispatched) if dispatched else 0,
                'first_dispatch_ns': min(dispatched) if dispatched else None, 'last_dispatch_ns': max(dispatched) if dispatched else None,
                'scheduled_origin_ns': origin, 'errors': sorted(set(state.errors)), 'recovery_events': recovery_records}
        (base / 'adapter-stderr.log').write_bytes(await stderr_task)
        result['exit_code'] = process.returncode
        result['evidence_sha256'] = {str(path.relative_to(base)): sha(path) for path in sorted(base.rglob('*'))
                                     if path.is_file() and path.name not in {'result.json', 'result.json.tmp'}}
        save(base / 'result.json', result)
        loop.remove_signal_handler(signal.SIGTERM)
    return result


def summarize(plan, result, records, a21, base):
    out = base / 'execution'
    receipt = json.loads((out / 'receipt.json').read_text())
    result['retirement'] = receipt
    if receipt['status'] != 'passed': result['invalid_reasons'].append('server/adapter retirement validation failed')
    sums = ['request_messages', 'response_messages', 'request_payload_bytes', 'response_payload_bytes', 'request_encoded_bytes', 'response_encoded_bytes']
    seen = bytearray(plan['timing']['requests']); observed = 0
    scenarios = {}; latencies = defaultdict(lambda: array('q')); frequency = receipt['client_hello']['timestamp_frequency']
    concurrency_events = array('q'); business_success = intentional_cancel = intentional_deadline = 0
    with (out / 'client/operations.jsonl').open() as stream:
        for line in stream:
            row = json.loads(line); identifier = row['id']
            if not 0 <= identifier < len(seen):
                recovery_start = plan['timing']['requests'] + plan['settings']['warmup_requests']
                if recovery_start <= identifier < recovery_start + plan['settings']['recovery_requests'] and row['correctness_failure']:
                    result['invalid_reasons'].append('response/protobuf/hash correctness failure during recovery')
                continue
            if seen[identifier]: result['invalid_reasons'].append('duplicate independent client result')
            seen[identifier] = 1; observed += 1
            if row['correctness_failure']: result['invalid_reasons'].append('response/protobuf/hash correctness failure')
            key = row['scenario'] + '/' + row['protocol'] + ('/' + str(row['request_payload_bytes']) if row['scenario'] == 'grpc-unary' else '')
            summary = scenarios.setdefault(key, {'calls': 0, 'outcomes': Counter(), 'wire_statuses': Counter(),
                'intentional_cancel': 0, 'intentional_deadline': 0, 'correctness_failures': 0, 'totals': Counter()})
            summary['calls'] += 1; summary['outcomes'][row['logical_status']] += 1; summary['wire_statuses'][row['wire_status']] += 1
            summary['intentional_cancel'] += row['intentional_cancellation']; summary['intentional_deadline'] += row['intentional_deadline']
            summary['correctness_failures'] += row['correctness_failure']
            for name in sums: summary['totals'][name] += row[name]
            measured = records[identifier]
            if measured['status'] == 'ok': latencies[key].append(measured['completion_ns'] - measured['scheduled_ns'])
            intentional_cancel += row['intentional_cancellation']; intentional_deadline += row['intentional_deadline']
            business_success += row['logical_status'] == 'ok' and not row['intentional_cancellation'] and not row['intentional_deadline']
            # Encode start/end tie ordering without retaining one Python dict per operation.
            concurrency_events.append(row['started_timestamp'] * 2 + 1); concurrency_events.append(row['completed_timestamp'] * 2)
    if observed != result['actually_dispatched']: result['invalid_reasons'].append('missing independent client results')
    concurrency = maximum = 0
    for event in sorted(concurrency_events):
        concurrency += 1 if event & 1 else -1; maximum = max(maximum, concurrency)
    result['independent_client'] = {'operations': observed, 'maximum_in_progress_calls': maximum, 'timestamp_frequency': frequency,
        'business_successful_calls': business_success, 'intentional_cancel_calls': intentional_cancel, 'intentional_deadline_calls': intentional_deadline,
        'business_success_per_second': business_success * 1e9 / result['measurement_window']['elapsed_ns']}
    result['scenarios'] = {}
    for key, summary in scenarios.items():
        summary['arrival_success'] = a21.histogram(latencies[key], plan['histogram']['resolution_ns'], plan['histogram']['maximum_ns'])
        result['scenarios'][key] = summary
    samples = [json.loads(line) for line in (out / 'process-resources.jsonl').read_text().splitlines()]
    window = result['measurement_window']
    active = [row for row in samples if window['start_monotonic_ns'] <= row['monotonic_ns'] <= window['end_monotonic_ns']]
    resource = {}
    for process in ('server', 'client', 'adapter', 'controller'):
        observed = [(row['monotonic_ns'], row[process]) for row in active if row.get(process) and row[process].get('cpu_ticks') is not None]
        values = {}
        if len(observed) >= 2:
            first, last = observed[0], observed[-1]
            cpu = (last[1]['cpu_ticks'] - first[1]['cpu_ticks']) / first[1]['clock_ticks_per_second']
            values['cpu_seconds'] = cpu; values['cpu_percent_one_core'] = cpu * 1e11 / (last[0] - first[0])
        else: values['cpu_seconds'] = values['cpu_percent_one_core'] = None
        for name in ('rss_bytes', 'threads'):
            measured = [(timestamp, row['counters'].get(name, {}).get('value')) for timestamp, row in observed]
            measured = [(timestamp, value) for timestamp, value in measured if value is not None]
            values[name + '_maximum'] = max((value for _, value in measured), default=None)
            values[name + '_first'] = measured[0][1] if measured else None
            values[name + '_last'] = measured[-1][1] if measured else None
        values['fd_count_maximum'] = max((row['fd_count'] for _, row in observed if row['fd_count'] is not None), default=None)
        values['socket_count_maximum'] = max((row['socket_count'] for _, row in observed if row['socket_count'] is not None), default=None)
        resource[process] = values
    result['resources'] = resource
    gauges = {}; idle = {}; gc = None; active_managed = []
    managed_path = out / 'server/managed-resources.jsonl'
    if managed_path.exists():
        with managed_path.open() as stream:
            for line in stream:
                row = json.loads(line)
                if row['phase'] == 'measurement':
                    active_managed.append(row)
                    for name, value in row['native_gauges'].items(): gauges[name] = max(value, gauges.get(name, 0))
                if row['phase'] == 'idle': idle = row['native_gauges']
        if len(active_managed) >= 2:
            gc = {name + '_delta': active_managed[-1][name] - active_managed[0][name] for name in ['managed_allocated_bytes', 'gen0', 'gen1', 'gen2']}
            gc.update(managed_heap_bytes_maximum=max(row['managed_heap_bytes'] for row in active_managed),
                      managed_committed_bytes_maximum=max(row['managed_committed_bytes'] for row in active_managed))
        payload_managed = gauges.get('nativeweave.server.payload.managed.bytes')
        payload_native = gauges.get('nativeweave.server.payload.native.bytes')
        keys = ['nativeweave.server.requests.active', 'nativeweave.server.requests.queued', 'nativeweave.server.requests.retiring',
                'nativeweave.server.payload.managed.bytes', 'nativeweave.server.payload.native.bytes']
        idle_receded = all(idle.get(key) == 0 for key in keys)
    else:
        with (out / 'server/native-resources.jsonl').open() as stream:
            for line in stream:
                row = json.loads(line)
                if row['phase'] == 'measurement':
                    for name in ['retained_native_bytes', 'pending_requests', 'byte_owners', 'connections']:
                        gauges[name] = max(row[name], gauges.get(name, 0))
                if row['phase'] == 'idle': idle = row
        payload_managed = 0; payload_native = gauges.get('retained_native_bytes')
        idle_receded = all(idle.get(key) == 0 for key in ['retained_native_bytes', 'pending_requests', 'byte_owners'])
    result['managed_gc'] = gc
    result['native_gauges_maximum'] = gauges
    result['after_idle_gauges'] = idle
    result['idle_owner_and_request_gauges_receded'] = idle_receded
    standard = plan['service_standard']
    error_count = sum(row['status'] != 'ok' for row in records)
    p99 = result['histograms']['arrival_success']['percentiles_from_exact_samples_ns']['p99']
    rss = resource['server']['rss_bytes_maximum']
    result['service_standard_met'] = (error_count / len(records) <= standard['unexpected_error_rate_maximum'] and p99 is not None
        and p99 <= standard['arrival_p99_maximum_ms'] * 1e6 and rss is not None and rss <= standard['whole_process_rss_bytes_maximum']
        and payload_managed is not None and payload_managed <= standard['native_managed_payload_bytes_maximum']
        and payload_native is not None and payload_native <= standard['native_payload_bytes_maximum']
        and idle_receded and result['recovery']['status'] == 'passed')
    result['full_30_minute_completed_duration'] = window.get('actual_dispatch_span_ns', 0) >= 1800_000_000_000
    result['full_30_minute_valid_load'] = not result['invalid_reasons'] and result['full_30_minute_completed_duration'] and result['service_standard_met']
    result['limitations'] = ['Encoded bytes are protobuf payload bytes, excluding the 5-byte gRPC envelope, HTTP/2 framing and TLS overhead.',
        'Messages submitted by a client are not asserted to be received after cancellation; successful response/hash acknowledgement proves the completed transfers.',
        'HTTP/2 stream/connection limits are configured; kernel socket descriptor count is not an active-stream measurement.',
        'Native allocation, exact memcpy/FFI counts and scheduler switches are unavailable in timed cells; collect profiler observations separately.',
        'A21 timestamps share one controller clock and include queue/IPC in arrival latency; client Stopwatch frequency is explicit in the receipt.']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('prepare')
    p.add_argument('--build', type=Path, required=True); p.add_argument('--client-build', type=Path)
    p.add_argument('--profile', type=Path, default=HERE / 'profile-v1.json')
    p.add_argument('--group', choices=['http', 'grpc'], required=True)
    p.add_argument('--rate', type=float, required=True); p.add_argument('--duration', type=float, required=True)
    p.add_argument('--concurrency', type=int, required=True); p.add_argument('--output', type=Path, required=True)
    p.add_argument('--certificate', type=Path); p.add_argument('--private-key', type=Path)
    p = sub.add_parser('run'); p.add_argument('--plan', type=Path, required=True)
    p.add_argument('--exclusive-lock', type=Path, required=True, help='Shared root-owned timing resource lock; coordinate all domains')
    args = parser.parse_args()
    if args.command == 'prepare': prepare(args); return
    with args.exclusive_lock.open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = asyncio.run(run_cell(args.plan.resolve()))
    print(json.dumps({key: result[key] for key in ('status', 'valid', 'route', 'successful', 'success_per_second', 'full_30_minute_valid_load', 'service_standard_met') if key in result}, indent=2))
    if not result['valid']: raise SystemExit(2)


if __name__ == '__main__':
    main()
