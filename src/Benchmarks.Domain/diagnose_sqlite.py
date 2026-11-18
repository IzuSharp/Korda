#!/usr/bin/env python3
"""Bounded diagnostic replay of failed frozen SQLite profiles, never a benchmark.

Reuse the exact original request dataset, arrival rate, warmup and concurrency.
New process/database identities and exception observation are explicitly separate
from the original calibration. The immutable original artifacts are not edited.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import contextvars
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import ssl
import subprocess
import sys
import time
from types import SimpleNamespace

import httpx

from common import check_files, digest, expected_value, inventory, load, save
from http_target import Target

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
CURRENT = contextvars.ContextVar('diagnostic_operation', default=None)


def stamp():
    return datetime.now(timezone.utc).isoformat()


def build_observer(probe: Path, output: Path, dotnet: str):
    """Copy accepted source and add only exception observation then rethrow."""
    output.mkdir(parents=True, exist_ok=False)
    progress = load(probe / 'progress.json')
    consumer = output / 'consumer'
    for name, expected in progress['consumer_sources']['baseline'].items():
        original = probe / 'consumers/baseline' / name
        if digest(original) != expected:
            raise ValueError('Accepted source changed: ' + name)
        target = consumer / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
    for path in (probe / 'consumers/baseline').glob('packages*.lock.json'):
        shutil.copy2(path, consumer / path.name)
    program = consumer / 'Program.cs'
    original = program.read_text()
    marker = '            catch (JsonException) { await ProblemAsync(context, 400, "invalid_json", token).ConfigureAwait(false); }'
    if original.count(marker) != 1:
        raise ValueError('Accepted middleware changed; inspect the observation hook')
    added = '''
            catch (Exception error)
            {
                string key = context.Request.Headers.GetSingle("Idempotency-Key") ?? "";
                string keyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
                var db = error as DatabaseException;
                string message = error.Message.Length > 1024 ? error.Message[..1024] : error.Message;
                Console.Error.WriteLine("B23_ADVERSE_EXCEPTION type=" + error.GetType().Name + " kind=" + db?.Kind
                    + " provider=" + db?.ProviderCode + " correlation=" + db?.CorrelationId + " key_sha256=" + keyHash
                    + " message_base64=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message)));
                throw;
            }'''
    program.write_text(original.replace(marker, marker + added))
    receipt = {'schema_version': 1, 'state': 'building', 'started_utc': stamp(),
        'scope': 'B23-only exception catch/log/rethrow observer; no changed response, DB/native configuration or business logic',
        'original_source': progress['source_repository'],
        'original_source_sha256': progress['consumer_sources']['baseline'],
        'copied_source_sha256': inventory(consumer), 'commands': [],
        'observation_hook': added, 'expected_native_sha256': progress['inputs']['native_sha256']}
    save(output / 'manifest.json', receipt)
    project = consumer / 'Reference.TaskService.csproj'
    arguments = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-r', 'linux-x64', '-p:PublishAot=true']
    for label, command in [
        ('restore', [dotnet, 'restore', str(project), '--locked-mode', *arguments]),
        ('publish', [dotnet, 'publish', str(project), '-c', 'Release', '--no-restore', *arguments, '-o', str(output / 'artifacts')])]:
        log = output / (label + '.log')
        started = time.monotonic_ns()
        with log.open('w') as stream:
            result = subprocess.run(command, cwd=consumer, env=dict(os.environ, DOTNET_PROCESSOR_COUNT='2'),
                stdout=stream, stderr=subprocess.STDOUT, timeout=600)
        receipt['commands'].append({'command': command, 'exit_code': result.returncode,
            'duration_ns': time.monotonic_ns() - started, 'log': log.name, 'sha256': digest(log)})
        save(output / 'manifest.json', receipt)
        if result.returncode:
            raise RuntimeError('Observer build failed: ' + str(log))
    native = {}
    for name, expected in progress['inputs']['native_sha256'].items():
        path = output / 'artifacts' / name
        if digest(path) != expected:
            raise ValueError('Observer changed native module: ' + name)
        native[name] = expected
    receipt.update(state='built', finished_utc=stamp(), native_sha256=native,
        artifacts={str(path): digest(path) for path in (output / 'artifacts').iterdir() if path.is_file()},
        executable=str(output / 'artifacts/NativeWeave.Reference.TaskService'))
    save(output / 'manifest.json', receipt)


async def worker(manifest: Path, dataset_path: Path, directory: Path):
    target = Target(SimpleNamespace(a21=ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py',
        manifest=manifest, route='nativeweave-aot-rust'))
    dataset = load(dataset_path)
    events = (directory / 'http.jsonl').open('w')
    outcomes = (directory / 'operations.jsonl').open('w')
    errors = []
    halt = asyncio.Event()
    original_request = target.request
    counters = {'offered': 0, 'dispatched': 0, 'cancelled_after_failure': 0}
    receipt = {'state': 'running', 'started_utc': stamp(), 'case': target.profile,
        'application_directory': str(target.directory), 'manifest_sha256': digest(manifest),
        'dataset_sha256': digest(dataset_path), 'diagnostic_only': True,
        'key_prefix': target.prefix,
        'scheduling_note': 'Direct diagnostic open-loop reproduction with the original offered rate/concurrency; this is not an A21 timing cohort'}
    save(directory / 'receipt.json', receipt)

    async def capture(method, path, **kwargs):
        started = time.monotonic_ns()
        response = await original_request(method, path, **kwargs)
        record = {'logical_id': CURRENT.get(), 'method': method, 'path': path,
            'status': response.status_code, 'elapsed_ns': time.monotonic_ns() - started,
            'completed_monotonic_ns': time.monotonic_ns(), 'http_version': response.http_version}
        key = (kwargs.get('headers') or {}).get('Idempotency-Key', '')
        record['key_sha256'] = hashlib.sha256(key.encode()).hexdigest()
        if response.status_code >= 400:
            body = response.content
            record.update(body_length=len(body), body_sha256=hashlib.sha256(body).hexdigest(),
                body_base64=base64.b64encode(body[:16384]).decode(), body_truncated=len(body) > 16384,
                headers=[(k[:256], v[:2048]) for k, v in response.headers.multi_items()
                    if k.lower() not in ('set-cookie', 'authorization', 'proxy-authorization')][:64])
        events.write(json.dumps(record, separators=(',', ':')) + '\n'); events.flush()
        return response

    target.request = capture

    async def execute(identifier, data, scheduled):
        async with target.semaphore:
            if halt.is_set():
                counters['cancelled_after_failure'] += 1
                return
            token = CURRENT.set(identifier)
            counters['dispatched'] += 1
            record = {'logical_id': identifier, 'scheduled_monotonic_ns': scheduled,
                'dispatch_monotonic_ns': time.monotonic_ns()}
            try:
                status, value = await target.operation(identifier, data)
                expected = expected_value(data)
                if status != 'ok' or value != expected:
                    raise AssertionError('Complete business outcome differs: ' + json.dumps({'status': status, 'actual': value, 'expected': expected}))
                record.update(status=status, value=value)
            except Exception as error:
                halt.set()
                record.update(status='failed', error=type(error).__name__ + ': ' + str(error))
                errors.append(record)
            finally:
                record['completion_monotonic_ns'] = time.monotonic_ns()
                outcomes.write(json.dumps(record, separators=(',', ':')) + '\n'); outcomes.flush()
                CURRENT.reset(token)

    try:
        await target.application.start()
        (directory / 'application.pid').write_text(str(target.application.process.pid))
        verify = ssl.create_default_context(cafile=target.config['tlsCertificatePem']) if target.config.get('tlsCertificatePem') else True
        async with httpx.AsyncClient(base_url=target.application.url,
                http2=target.manifest['protocol'] == 'http2-tls', verify=verify, trust_env=False, timeout=30,
                headers={'Authorization': 'Bearer ' + target.token},
                limits=httpx.Limits(max_connections=target.profile['client_concurrency'],
                    max_keepalive_connections=target.profile['client_concurrency'])) as target.client:
            for index in range(target.profile['warmup']):
                data = json.loads(base64.urlsafe_b64decode(dataset[index % len(dataset)]['input']))
                await execute(len(dataset) + index, data, time.monotonic_ns())
            started = time.monotonic_ns()
            tasks = set()
            for index, row in enumerate(dataset):
                scheduled = started + int(index * 1_000_000_000 / target.profile['rate'])
                await asyncio.sleep(max(0, (scheduled - time.monotonic_ns()) / 1_000_000_000))
                if halt.is_set():
                    break
                counters['offered'] += 1
                data = json.loads(base64.urlsafe_b64decode(row['input']))
                task = asyncio.create_task(execute(index, data, scheduled))
                tasks.add(task); task.add_done_callback(tasks.discard)
                if len(tasks) >= target.manifest['max_in_flight']:
                    receipt['generator_capacity_reached'] = True
                    break
            if tasks:
                await asyncio.wait_for(asyncio.gather(*tasks), 40)
            receipt['durable_oracle'] = target.durable_oracle()
            failed_keys = {target.prefix + str(row['logical_id']) for row in errors if 'logical_id' in row}
            receipt['durable_failed_keys'] = [row for row in target.database.operation_rows(target.prefix) if row[2] in failed_keys]
    except Exception as error:
        errors.append({'error': type(error).__name__ + ': ' + str(error)})
    finally:
        try:
            await asyncio.wait_for(target.application.stop(), 45)
            receipt['shutdown'] = 'joined'
        except Exception as error:
            receipt['shutdown'] = 'failed: ' + type(error).__name__ + ': ' + str(error)
            process = target.application.process
            if process and process.returncode is None:
                os.killpg(process.pid, signal.SIGKILL)
                try:
                    await asyncio.wait_for(process.wait(), 5)
                except asyncio.TimeoutError:
                    receipt['forced_process_exit_unconfirmed'] = True
        events.close(); outcomes.close()
        receipt.update(state='completed-adverse' if errors else 'completed-without-reproduction',
            finished_utc=stamp(), errors=errors, counters=counters, http_status_counts=target.http_outcomes,
            closed=target.application.closed, application_artifacts=target.descriptor['artifacts'])
        check_files(target.descriptor['artifacts'])
        receipt['final_artifact_recheck'] = True
        save(directory / 'receipt.json', receipt)


def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    progress = {'state': 'running', 'started_utc': stamp(), 'diagnostic_only': True, 'rows': [],
        'source_sha256': {str(path): digest(path) for path in [Path(__file__), HERE / 'http_target.py', HERE / 'common.py']},
        'policy': 'Original datasets/config/rates retained. Baseline and observer are separate. No retrospective performance acceptance.'}
    save(args.output / 'progress.json', progress)
    for case in args.cases:
        original = args.calibration / 'plans' / case
        for repetition in range(args.attempts):
            directory = args.output / (case + '-' + str(repetition))
            directory.mkdir()
            target = load(original / 'target.json')
            if target['provider'] != 'sqlite' or target['profile']['kind'] not in ('create', 'cycle'):
                raise ValueError('This diagnostic owns only the failed SQLite create/cycle profiles')
            old_root = str(Path(target['golden']).parents[2])
            target['files'] = {path.replace(old_root, str(ROOT)): value for path, value in target['files'].items()}
            target['golden'] = target['golden'].replace(old_root, str(ROOT))
            target['run_directory'] = str(directory / 'applications')
            target['routes'] = {'nativeweave-aot-rust': target['routes']['nativeweave-aot-rust']}
            if args.observer:
                observer = load(args.observer / 'manifest.json')
                descriptor = target['routes']['nativeweave-aot-rust']
                descriptor.update(command=[observer['executable']], artifacts=observer['artifacts'],
                    source={'kind': 'B23 catch/rethrow observation copy', 'manifest_sha256': digest(args.observer / 'manifest.json'),
                        'baseline': descriptor['source']})
            save(directory / 'target.json', target)
            shutil.copy2(original / 'dataset.json', directory / 'dataset.json')
            command = [sys.executable, str(Path(__file__)), '--worker', str(directory)]
            with (directory / 'worker.log').open('w') as stream:
                process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    process.wait(timeout=150)
                except subprocess.TimeoutExpired:
                    pid_file = directory / 'application.pid'
                    if pid_file.exists():
                        try:
                            os.killpg(int(pid_file.read_text()), signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                    save(directory / 'external-timeout.json', {'at_utc': stamp(), 'seconds': 150,
                        'native_retirement': 'not established by process termination'})
            row = {'case': case, 'repetition': repetition, 'exit_code': process.returncode,
                'directory': str(directory), 'original_manifest_sha256': digest(original / 'target.json')}
            if (directory / 'receipt.json').exists():
                row['receipt_sha256'] = digest(directory / 'receipt.json')
                row['state'] = load(directory / 'receipt.json')['state']
            progress['rows'].append(row)
            save(args.output / 'progress.json', progress)
            print(json.dumps(row), flush=True)
    progress.update(state='completed', finished_utc=stamp())
    save(args.output / 'progress.json', progress)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', type=Path)
    parser.add_argument('--calibration', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--observer', type=Path)
    parser.add_argument('--build-observer', type=Path, metavar='PACKAGE_PROBE')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--attempts', type=int, default=2)
    parser.add_argument('--cases', nargs='+', default=['warm-create-large-sqlite-http1', 'warm-cycle-high-sqlite-http1-tls'])
    args = parser.parse_args()
    if args.worker:
        asyncio.run(worker(args.worker / 'target.json', args.worker / 'dataset.json', args.worker))
    elif args.build_observer:
        build_observer(args.build_observer, args.output, args.dotnet)
    else:
        run(args)


if __name__ == '__main__':
    main()
