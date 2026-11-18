#!/usr/bin/env python3
"""B23 HTTP adapter for A21's existing external observer protocol.

This process is the HTTP client, not the product. It forwards the actual app's
AOT witness and samples the child app separately. A21 owns all arrival clocks,
latency histograms and statistical comparisons. Admission means client dispatch
under the declared concurrency bound; it is not an internal DB/Server timestamp.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import signal
import ssl
import sys
import traceback
import uuid
from urllib.parse import urlsplit

import httpx

from common import Database, check_files, digest, load, load_a21, payload_chunk, process_sample, save


class IntegrityFailure(AssertionError):
    pass


def require(condition, message):
    if not condition:
        raise IntegrityFailure(message)


def emit(value):
    print(json.dumps(value, separators=(',', ':'), ensure_ascii=True), flush=True)


def canonical_id(value):
    require(isinstance(value, str) and str(uuid.UUID(value)) == value, 'Response identity is not a canonical UUID')
    return value


class Application:
    def __init__(self, descriptor, configuration, directory, a21):
        self.descriptor, self.configuration, self.directory, self.a21 = descriptor, configuration, directory, a21
        self.dynamic = None
        self.latest_probe = None
        self.closed = None
        self.process = None
        self.phase = 'startup-and-seed'

    async def start(self):
        self.directory.mkdir(parents=True, exist_ok=False)
        config = self.directory / 'config.private.json'
        save(config, self.configuration)
        config.chmod(0o600)
        command = [*self.descriptor['command'], '--config', str(config)]
        environment = dict(os.environ, DOTNET_PROCESSOR_COUNT='2')
        environment.update(self.descriptor.get('environment', {}))
        self.started_ns = self.a21.time.monotonic_ns()
        self.process = await asyncio.create_subprocess_exec(*command, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, env=environment, start_new_session=True)
        self.ready = asyncio.get_running_loop().create_future()
        self.stdout_log = (self.directory / 'stdout.log').open('w')
        self.stderr_log = (self.directory / 'stderr.log').open('w')

        async def consume(stream, log):
            while line := await stream.readline():
                text = line.decode('utf-8', errors='replace').rstrip('\n')
                log.write(text + '\n'); log.flush()
                if text.startswith('READY ') and not self.ready.done():
                    self.ready.set_result(text[6:].strip())
                witness = re.search(r'\b(?:B21|B23)_RUNTIME dynamic=(True|False)\b', text)
                if witness:
                    self.dynamic = witness.group(1) == 'True'
                if text.startswith('B23_OBSERVATION '):
                    self.latest_probe = json.loads(text[16:])
                if text.startswith('CLOSED '):
                    self.closed = json.loads(text[7:])
            if stream is self.process.stdout and not self.ready.done():
                self.ready.set_exception(IntegrityFailure('Application exited before READY'))

        self.readers = [asyncio.create_task(consume(self.process.stdout, self.stdout_log)),
                        asyncio.create_task(consume(self.process.stderr, self.stderr_log))]
        self.url = await asyncio.wait_for(self.ready, 9)
        self.ready_ns = self.a21.time.monotonic_ns()
        scheme = 'https' if self.configuration.get('tlsCertificatePem') else 'http'
        require(urlsplit(self.url).scheme == scheme, 'Observed HTTP/TLS scheme differs from the frozen profile')
        if self.descriptor['id'] != 'direct-rust':
            await asyncio.sleep(0)
            require(self.dynamic is False, 'Actual managed application did not emit a Native AOT witness')
        self.samples = []
        self.stop_sampling = asyncio.Event()
        self.sample_file = (self.directory / 'resources.jsonl').open('w')

        async def sample():
            while not self.stop_sampling.is_set():
                row = process_sample(self.a21, self.process.pid)
                row['phase'] = self.phase
                if self.latest_probe is not None:
                    row['managed_probe'] = self.latest_probe
                self.samples.append(row)
                self.sample_file.write(json.dumps(row, separators=(',', ':')) + '\n')
                self.sample_file.flush()
                try:
                    await asyncio.wait_for(self.stop_sampling.wait(), .05)
                except asyncio.TimeoutError:
                    pass

        self.sampler = asyncio.create_task(sample())

    async def stop(self):
        if self.process is not None and self.process.returncode is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                await asyncio.wait_for(self.process.wait(), 40)
            except asyncio.TimeoutError:
                os.killpg(self.process.pid, signal.SIGKILL)
                await self.process.wait()
                raise IntegrityFailure('Application exceeded the finite shutdown budget')
        if hasattr(self, 'readers'):
            await asyncio.gather(*self.readers)
        if hasattr(self, 'stop_sampling'):
            self.stop_sampling.set()
            await self.sampler
            self.sample_file.close()
        for name in ('stdout_log', 'stderr_log'):
            if hasattr(self, name):
                getattr(self, name).close()
        if self.process is not None:
            require(self.process.returncode == 0, 'Application did not shut down successfully')
        if self.descriptor['id'] == 'nativeweave-aot-rust' and self.closed is not None:
            require(self.closed.get('databaseResourcesReleased') and self.closed.get('consumerResourcesReleased'),
                    'Native database resources did not join at process shutdown')


class Target:
    def __init__(self, arguments):
        self.args = arguments
        self.a21 = load_a21(arguments.a21)
        self.manifest = load(arguments.manifest)
        self.descriptor = self.manifest['routes'][arguments.route]
        self.profile = self.manifest['profile']
        check_files(self.manifest['files'])
        self.directory = Path(self.manifest['run_directory']) / (arguments.route + '-' + uuid.uuid4().hex)
        self.directory.mkdir(parents=True, exist_ok=False)
        self.prefix = 'b23-' + uuid.uuid4().hex[:16] + '-'
        self.token = secrets.token_urlsafe(32)
        self.config = {
            'provider': self.manifest['provider'],
            'database': str(self.directory / 'application.sqlite'),
            'blobDirectory': str(self.directory / 'blobs'),
            'consumerDatabase': str(self.directory / 'consumer.sqlite'),
            'listenAddress': '127.0.0.1', 'port': 0,
            'cursorKey': base64.b64encode(hashlib.sha256(b'B23 fixed cursor key').digest()).decode(),
            'principals': [{'token': self.token, 'tenant': 'tenant-a', 'principal': 'alice', 'canWrite': True}],
            'runWorkers': False, 'maxUploads': self.profile.get('max_uploads', 4),
            'maxBlobBytes': 268435456, 'requestTimeoutSeconds': 120,
        }
        if self.manifest['provider'] == 'postgresql':
            # The owning controller starts the PG cluster in this same process
            # tree and provisions a distinct database for every target launch.
            connection = self.manifest['postgres_base']
            name = 'b23_' + uuid.uuid4().hex
            Database('postgresql', connection, self.manifest['psql']).sql('CREATE DATABASE ' + name)
            self.config.update(database=connection.rsplit('/', 1)[0] + '/' + name,
                               allowLocalDatabaseTlsDisable=True)
        if self.manifest['protocol'] != 'http1':
            self.config.update(tlsCertificatePem=self.manifest['tls_certificate'],
                               tlsPrivateKeyPem=self.manifest['tls_private_key'])
        self.database = Database(self.config['provider'], self.config['database'], self.manifest.get('psql'))
        self.application = Application(self.descriptor, self.config, self.directory / 'application', self.a21)
        self.protocols, self.http_outcomes = set(), {}
        self.mutations, self.operations = [], []
        self.errors = []
        self.counters = {name: self.a21.unknown(unit, 'No observation of this application/engine counter in this profile')
                         for name, unit in self.a21.COUNTERS.items()}
        self.semaphore = asyncio.Semaphore(self.profile['client_concurrency'])
        spec = importlib.util.spec_from_file_location('b23_independent_hash_oracle', self.manifest['golden'])
        self.golden = importlib.util.module_from_spec(spec); spec.loader.exec_module(self.golden)

    async def request(self, method, path, *, body=None, headers=None):
        response = await self.client.request(method, path, content=body, headers=headers)
        self.protocols.add(response.http_version)
        expected = 'HTTP/2' if self.manifest['protocol'] == 'http2-tls' else 'HTTP/1.1'
        require(response.http_version == expected, 'HTTP response used an unexpected actual protocol')
        key = str(response.status_code)
        self.http_outcomes[key] = self.http_outcomes.get(key, 0) + 1
        return response

    async def create(self, identifier, title):
        key = self.prefix + str(identifier)
        response = await self.request('POST', '/v1/items',
            body=json.dumps({'title': title}, separators=(',', ':'), ensure_ascii=False).encode(),
            headers={'Content-Type': 'application/json', 'Idempotency-Key': key})
        require(response.status_code == 201, 'Create did not confirm the expected durable mutation')
        value = response.json()
        canonical_id(value['operationId']); canonical_id(value['itemId'])
        require(value['title'] == title and value['responseVersion'] == 1 and value['version'] == 1,
                'Created DTO differs from the independently supplied request')
        self.mutations.append({'operation_id': value['operationId'], 'item_id': value['itemId'], 'key': key,
            'scope': self.golden.scope_hash('tenant-a', 'alice', 'POST /v1/items'),
            'hash': self.golden.create_hash('tenant-a', 'alice', title), 'status': 201,
            'response_hex': response.content.hex(), 'kind': 'create'})
        return response, value

    async def operation(self, identifier, item):
        kind = item['kind']
        if kind in ('create', 'cycle'):
            response, value = await self.create(identifier, item['title'])
            if kind == 'create':
                return 'ok', {'kind': kind, 'status': response.status_code,
                    'title': value['title'], 'responseVersion': value['responseVersion'], 'version': value['version']}
            key = self.prefix + str(identifier)
            replay = await self.request('POST', '/v1/items', body=json.dumps({'title': item['title']}).encode(),
                headers={'Content-Type': 'application/json', 'Idempotency-Key': key})
            recovered = await self.request('GET', '/v1/operations/' + value['operationId'])
            return 'ok', {'kind': kind, 'create': response.status_code, 'replay': replay.status_code,
                'recovery': recovered.status_code, 'title': value['title'], 'responseVersion': value['responseVersion'],
                'version': value['version'], 'replay_equal': replay.content == response.content,
                'recovery_equal': recovered.content == response.content}
        if kind == 'list':
            path = '/__b23/di-list' if self.manifest.get('di') else '/v1/items'
            response = await self.request('GET', path + '?limit=' + str(item['limit']))
            require(response.status_code == 200, 'Keyset query failed')
            page = response.json()
            return 'ok', {'kind': kind, 'status': response.status_code,
                'titles': [row['title'] for row in page['items']],
                'versions': [row['version'] for row in page['items']], 'has_cursor': page['nextCursor'] is not None}
        if kind == 'stream':
            created, value = await self.create(identifier, 'attachment-' + str(identifier))
            key = self.prefix + str(identifier) + '-attachment'
            chunk = payload_chunk(item['seed'])

            async def upload():
                remaining = item['bytes']
                while remaining:
                    count = min(remaining, len(chunk)); yield chunk[:count]; remaining -= count
                    if self.profile.get('chunk_delay_ms'):
                        await asyncio.sleep(self.profile['chunk_delay_ms'] / 1000)

            path = '/v1/items/' + value['itemId'] + '/attachment'
            response = await self.request('PUT', path, body=upload(), headers={
                'Idempotency-Key': key, 'If-Match': '1', 'Content-Type': 'application/octet-stream',
                'Content-Length': str(item['bytes'])})
            if response.status_code == 503 and self.profile.get('product_only'):
                require(response.json().get('code') == 'upload_capacity', 'Overload returned an unrelated 503')
                return 'rejected', {'stage': 'upload', 'code': 'upload_capacity'}
            require(response.status_code == 200, 'Attachment binding did not confirm')
            attached = response.json(); canonical_id(attached['operationId'])
            require(attached['itemId'] == value['itemId'], 'Attachment identity changed')
            self.mutations.append({'operation_id': attached['operationId'], 'item_id': value['itemId'],
                'key': key, 'scope': self.golden.scope_hash('tenant-a', 'alice', 'PUT /v1/items/{id}/attachment'),
                'hash': self.golden.attachment_hash('tenant-a', 'alice', value['itemId'], item['sha256'], item['bytes'], 1),
                'status': response.status_code, 'response_hex': response.content.hex(), 'kind': 'attachment',
                'content_hash': item['sha256'], 'content_length': item['bytes']})
            actual = hashlib.sha256(); length = 0
            async with self.client.stream('GET', path) as downloaded:
                self.protocols.add(downloaded.http_version)
                require(downloaded.http_version == ('HTTP/2' if self.manifest['protocol'] == 'http2-tls' else 'HTTP/1.1'),
                        'Streaming download used an unexpected protocol')
                self.http_outcomes[str(downloaded.status_code)] = self.http_outcomes.get(str(downloaded.status_code), 0) + 1
                async for data in downloaded.aiter_bytes(32768):
                    actual.update(data); length += len(data)
            return 'ok', {'kind': kind, 'create': created.status_code, 'upload': response.status_code,
                'download': downloaded.status_code, 'bytes': length, 'sha256': actual.hexdigest(), 'version': attached['version']}
        if kind == 'diagnostic-failure':
            response = await self.request('GET', '/__b23/failure')
            return 'ok', {'kind': kind, 'status': response.status_code}
        raise IntegrityFailure('Unsupported operation token')

    async def seed(self):
        if self.profile['kind'] not in ('list', 'diagnostic-failure'):
            return
        async def create_row(index):
            async with self.semaphore:
                _, value = await self.create('seed-' + str(index), 'seed-' + str(index).zfill(4))
                return value['itemId'], index
        rows = await asyncio.gather(*(create_row(index) for index in range(128)))
        # Explicit independent fixture setup: preserve real HTTP-created rows,
        # but freeze their ordering across routes with independently generated IDs.
        for item, index in rows:
            self.database.sql(f"UPDATE items SET created_at={1790000000000 + index} WHERE item_id='{item}'")

    async def probe(self):
        if not self.descriptor.get('instrumented'):
            return None
        response = await self.request('GET', '/__b23/observe')
        require(response.status_code == 200, 'Application counter observation failed')
        observation = response.json()
        self.application.latest_probe = observation
        self.counters['managed_allocated_bytes'] = self.a21.measured(observation['allocatedBytes'], 'bytes',
            'GC.GetTotalAllocatedBytes(true), whole child process since launch; includes observation overhead')
        self.counters['gc_heap_live_bytes'] = self.a21.measured(observation['heapLiveBytes'], 'bytes', 'GC.GetTotalMemory(false); whole application process')
        self.counters['gc_heap_committed_bytes'] = self.a21.measured(observation['heapCommittedBytes'], 'bytes', 'GC.GetGCMemoryInfo().TotalCommittedBytes')
        return observation

    def durable_oracle(self):
        rows = self.database.operation_rows(self.prefix)
        expected = {item['key']: item for item in self.mutations}
        require(len(rows) == len(expected), 'Independent durable operation cardinality differs from observed confirmed responses')
        for row in rows:
            operation, scope, key, request_hash, version, status, encoded = row
            wanted = expected.get(key)
            require(wanted is not None and operation == wanted['operation_id'] and scope == wanted['scope']
                and request_hash == wanted['hash'] and int(version) == 1 and int(status) == wanted['status']
                and bytes.fromhex(encoded).hex() == wanted['response_hex'], 'Independent durable response/scope/hash differs')
        outbox = int(self.database.scalar(f"SELECT COUNT(*) FROM outbox WHERE operation_id IN (SELECT operation_id FROM operations WHERE request_key LIKE '{self.prefix}%')"))
        items = int(self.database.scalar(f"SELECT COUNT(*) FROM items WHERE operation_id IN (SELECT operation_id FROM operations WHERE request_key LIKE '{self.prefix}%')"))
        require(outbox == len(expected), 'Every confirmed mutation must have exactly one transactional outbox row')
        require(items == sum(item['kind'] == 'create' for item in expected.values()), 'Create effects were lost or duplicated')
        attachments = self.database.sql(f"SELECT item_id,content_hash,content_length,operation_id,version FROM attachments WHERE operation_id IN (SELECT operation_id FROM operations WHERE request_key LIKE '{self.prefix}%')")
        references = {item['item_id']: item for item in expected.values() if item['kind'] == 'attachment'}
        require(len(attachments) == len(references), 'Attachment reference cardinality differs')
        checked = set()
        for item, content_hash, length, operation, version in attachments:
            wanted = references[item]
            require(content_hash == wanted['content_hash'] and int(length) == wanted['content_length']
                and operation == wanted['operation_id'] and int(version) == 2, 'Authorized attachment reference differs')
            if content_hash not in checked:
                require(digest(Path(self.config['blobDirectory']) / 'blobs' / content_hash) == content_hash,
                        'A confirmed attachment reference names incomplete content')
                checked.add(content_hash)
        return {'passed': True, 'operations': len(rows), 'items': items, 'outbox': outbox,
                'attachments': len(attachments), 'unique_blobs_verified': len(checked),
                'observer': 'Separate SQLite/psql queries and direct immutable-file SHA-256'}

    async def run(self):
        receipt = {'schema_version': 1, 'route': self.args.route, 'profile': self.profile,
                   'status': 'running', 'source': self.descriptor.get('source'), 'application_artifacts': self.descriptor['artifacts'],
                   'clock_scope': 'Latency is recorded only by A21; these timestamps identify startup/resource samples',
                   'admission_scope': 'Bounded HTTP-client dispatch, not an internal Server/DB admission timestamp'}
        save(self.directory / 'receipt.json', receipt)
        try:
            await self.application.start()
            verify = ssl.create_default_context(cafile=self.config['tlsCertificatePem']) if self.config.get('tlsCertificatePem') else True
            async with httpx.AsyncClient(base_url=self.application.url, http2=self.manifest['protocol'] == 'http2-tls',
                    verify=verify, trust_env=False, timeout=30,
                    headers={'Authorization': 'Bearer ' + self.token},
                    limits=httpx.Limits(max_connections=self.profile['client_concurrency'],
                                       max_keepalive_connections=self.profile['client_concurrency'])) as self.client:
                await self.seed()
                receipt['ready_to_seed_ns'] = self.a21.time.monotonic_ns() - self.application.ready_ns
                receipt['startup_to_ready_ns'] = self.application.ready_ns - self.application.started_ns
                receipt['probe_before'] = await self.probe()
                emit({'kind': 'hello', 'route': self.args.route,
                    'dynamic_code_supported': self.application.dynamic,
                    'application_pid': self.application.process.pid, 'adapter_pid': os.getpid(),
                    'application_uri_scheme': urlsplit(self.application.url).scheme,
                    'receipt_directory': str(self.directory), 'admission_scope': receipt['admission_scope']})
                tasks = set()

                async def execute(identifier, token, mode):
                    async with self.semaphore:
                        if identifier < self.manifest['requests'] and 'measurement_resources_before' not in receipt:
                            self.application.phase = 'measurement'
                            receipt['measurement_resources_before'] = process_sample(self.a21, self.application.process.pid)
                        emit({'kind': 'admitted', 'id': identifier})
                        try:
                            require(mode == 'ok', 'Fault injections cannot enter a throughput cell')
                            operation = json.loads(base64.urlsafe_b64decode(token.encode()))
                            outcome, value = await self.operation(identifier, operation)
                        except Exception as error:
                            self.errors.append(type(error).__name__ + ': ' + str(error))
                            outcome, value = 'failed', {'error': type(error).__name__}
                            traceback.print_exc(file=sys.stderr)
                        self.operations.append({'id': identifier, 'status': outcome, 'value': value})
                        # End the warmup before the observation baseline. The
                        # counter query is outside A21's measured arrivals.
                        if self.descriptor.get('instrumented') and identifier == self.manifest['requests'] + self.profile['warmup'] - 1:
                            receipt['probe_before'] = await self.probe()
                        emit({'kind': 'completed', 'id': identifier, 'status': outcome, 'value': value, 'counters': self.counters})

                reader = asyncio.StreamReader()
                protocol = asyncio.StreamReaderProtocol(reader)
                await asyncio.get_running_loop().connect_read_pipe(lambda: protocol, sys.stdin)
                while line := await reader.readline():
                    identifier, token, mode = line.decode().rstrip('\n').split('\t')
                    task = asyncio.create_task(execute(int(identifier), token, mode))
                    tasks.add(task); task.add_done_callback(tasks.discard)
                    require(len(tasks) <= self.manifest['max_in_flight'] + 1, 'Adapter in-flight bound exceeded')
                if tasks:
                    await asyncio.gather(*tasks)
                receipt['measurement_resources_after'] = process_sample(self.a21, self.application.process.pid)
                self.application.phase = 'reconciliation-and-shutdown'
                receipt['probe_after'] = await self.probe()
                receipt['durable_oracle'] = self.durable_oracle()
                receipt['status'] = 'passed' if not self.errors else 'failed'
        except BaseException as error:
            receipt['status'] = 'failed'
            self.errors.append(type(error).__name__ + ': ' + str(error))
            raise
        finally:
            try:
                await self.application.stop()
            except Exception as error:
                receipt['status'] = 'failed'; self.errors.append(type(error).__name__ + ': ' + str(error))
            receipt.update(errors=self.errors, observed_http_protocols=sorted(self.protocols),
                http_status_counts=self.http_outcomes, operation_outcomes=self.operations,
                closed=self.application.closed, last_probe=self.application.latest_probe,
                resources='application/resources.jsonl', source_manifest_sha256=digest(self.args.manifest))
            save(self.directory / 'receipt.json', receipt)
        return 0 if receipt['status'] == 'passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--route', required=True, choices=sorted(load_a21().ROUTES))
    parser.add_argument('--a21', type=Path)
    args = parser.parse_args()
    return asyncio.run(Target(args).run())


if __name__ == '__main__':
    raise SystemExit(main())
