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
import contextvars
from contextlib import contextmanager
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
import h11

from common import Database, check_files, digest, expected_value, load, load_a21, mapping_sample, payload_chunk, process_sample, save
from supervision import register_process


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
        self.resource_sampling_policy = None

    async def start(self):
        self.directory.mkdir(parents=True, exist_ok=False)
        config = self.directory / 'config.private.json'
        save(config, self.configuration)
        config.chmod(0o600)
        self.configuration_sha256 = digest(config)
        command = [*self.descriptor['command'], '--config', str(config)]
        environment = dict(os.environ, DOTNET_PROCESSOR_COUNT='2')
        environment.update(self.descriptor.get('environment', {}))
        if self.descriptor.get('b22_observation'):
            environment['NW_B22_OBSERVATIONS'] = str(self.directory / 'b22-observations.jsonl')
        self.started_ns = self.a21.time.monotonic_ns()
        self.process = await asyncio.create_subprocess_exec(*command, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, env=environment, start_new_session=True)
        register_process('application', self.process.pid, self.directory)
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

        def publish_sample(row):
            if self.latest_probe is not None:
                row['managed_probe'] = self.latest_probe
            self.samples.append(row)
            self.sample_file.write(json.dumps(row, separators=(',', ':')) + '\n')
            self.sample_file.flush()

        async def sample():
            threaded = self.resource_sampling_policy is not None and self.a21.threaded_observation(self.resource_sampling_policy)
            while not self.stop_sampling.is_set():
                if threaded:
                    pid = self.process.pid
                    await self.a21.sample_in_worker(self.stop_sampling,
                        lambda: process_sample(self.a21, pid), publish_sample, lambda: self.phase)
                else:
                    row = process_sample(self.a21, self.process.pid)
                    row['phase'] = self.phase
                    publish_sample(row)
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
            try:
                await self.sampler
            finally:
                self.sample_file.close()
        for name in ('stdout_log', 'stderr_log'):
            if hasattr(self, name):
                getattr(self, name).close()
        if self.process is not None:
            require(self.process.returncode == 0, 'Application did not shut down successfully')
        if self.descriptor['id'] == 'nativeweave-aot-rust':
            require(self.closed is not None, 'Native product did not emit its mandatory CLOSED receipt')
            require(self.closed.get('serverState') == 'Disposed' and self.closed.get('databaseStatus') == 'Succeeded'
                    and self.closed.get('consumerStatus') == 'Succeeded', 'Native product CLOSED states did not complete')
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
        check_files(self.descriptor['artifacts'])
        check_files(self.descriptor['lock_hashes'])
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
        self.http_error_codes = {}
        self.overload_uploads = []
        self.active_identifier = contextvars.ContextVar('b23_logical_request', default=None)
        self.last_response = contextvars.ContextVar('b23_last_response', default=None)
        self.failure_capture_count = self.failure_capture_omitted = 0
        self.error_capture_count = 0
        self.mutations, self.operations = [], []
        self.errors = []
        self.halt_dispatch = False
        self.counters = {name: self.a21.unknown(unit, 'No observation of this application/engine counter in this profile')
                         for name, unit in self.a21.COUNTERS.items()}
        self.semaphore = asyncio.Semaphore(self.profile['client_concurrency'])
        spec = importlib.util.spec_from_file_location('b23_independent_hash_oracle', self.manifest['golden'])
        self.golden = importlib.util.module_from_spec(spec); spec.loader.exec_module(self.golden)

    async def request(self, method, path, *, body=None, headers=None, stage=None):
        response = await self.client.request(method, path, content=body, headers=headers)
        return self.observe_response(response, stage=stage, method=method, path=path)

    def observe_response(self, response, *, stage=None, method=None, path=None):
        self.last_response.set((response, stage, method, path))
        self.protocols.add(response.http_version)
        expected = 'HTTP/2' if self.manifest['protocol'] == 'http2-tls' else 'HTTP/1.1'
        if response.http_version != expected:
            self.capture_failure(response, stage=stage, method=method, path=path)
            raise IntegrityFailure('HTTP response used an unexpected actual protocol')
        key = str(response.status_code)
        self.http_outcomes[key] = self.http_outcomes.get(key, 0) + 1
        if response.status_code >= 400:
            self.capture_failure(response, stage=stage, method=method, path=path)
            try:
                code = response.json().get('code', 'unstructured_error')
            except (ValueError, AttributeError):
                code = 'unstructured_error'
            self.http_error_codes[code] = self.http_error_codes.get(code, 0) + 1
        return response

    def capture_failure(self, response, body=None, body_length=None, body_sha256=None,
                        *, stage=None, method=None, path=None, body_complete=True):
        """Bounded exact parsed header/body evidence; credentials are never copied from requests."""
        if response.extensions.get('nw_b23_failure_captured'):
            return
        response.extensions['nw_b23_failure_captured'] = True
        if self.failure_capture_count >= 32:
            self.failure_capture_omitted += 1
            return
        data = response.content if body is None else body
        observed_length = len(data) if body_length is None else body_length
        headers, header_bytes, remaining, header_hash = [], 0, 16384, hashlib.sha256()
        for name, value in response.headers.raw:
            header_bytes += len(name) + len(value)
            for component in (name, value):
                header_hash.update(len(component).to_bytes(8, 'big')); header_hash.update(component)
            if remaining:
                prefix_name = name[:remaining]; remaining -= len(prefix_name)
                prefix_value = value[:remaining]; remaining -= len(prefix_value)
                headers.append([base64.b64encode(prefix_name).decode(), base64.b64encode(prefix_value).decode()])
        row = {'logical_id': self.active_identifier.get(), 'observed_ns': self.a21.time.monotonic_ns(),
            'stage': stage, 'method': method, 'path': path,
            'status': response.status_code, 'http_version': response.http_version,
            'headers_base64': headers, 'header_name_value_bytes': header_bytes,
            'headers_truncated': header_bytes > 16384,
            'headers_sha256': header_hash.hexdigest(),
            'header_hash_encoding': 'Ordered name/value pairs, each prefixed by its unsigned 64-bit big-endian byte length',
            'body_base64': base64.b64encode(data[:65536]).decode(),
            'body_bytes': observed_length if body_complete else None,
            'body_bytes_observed': observed_length, 'body_complete': body_complete,
            'body_sha256': body_sha256 or hashlib.sha256(data).hexdigest(),
            'body_sha256_scope': 'complete decoded body' if body_complete else 'decoded bytes yielded before interruption',
            'body_truncated': observed_length > 65536,
            'scope': 'Exact parsed header-value bytes and decoded body; no response content coding is configured in this profile'}
        path = self.directory / 'http-failures.jsonl'
        with path.open('a') as stream:
            stream.write(json.dumps(row, separators=(',', ':')) + '\n')
        self.failure_capture_count += 1

    def complete(self, operation, value, responses=()):
        """A success event may be emitted only after the entire logical oracle passes."""
        if value != expected_value(operation):
            for response, stage, method, path in responses:
                self.capture_failure(response, stage=stage, method=method, path=path)
            raise IntegrityFailure('Normalized HTTP/domain result differs: ' + json.dumps(value, sort_keys=True)[:1024])
        return 'ok', value

    @contextmanager
    def response_contract(self, response, stage, method, path):
        """Keep unexpected successful-status DTOs as well as HTTP error bodies."""
        try:
            yield
        except Exception:
            self.capture_failure(response, stage=stage, method=method, path=path)
            raise

    def capture_error(self, identifier, error):
        # Retain malformed successful DTOs as well as HTTP error statuses. The
        # context-local response cannot be confused with another in-flight call.
        observed = self.last_response.get()
        if observed is not None:
            response, stage, method, path = observed
            self.capture_failure(response, stage=stage, method=method, path=path)
        self.errors.append(type(error).__name__ + ': ' + str(error)[:512])
        if self.error_capture_count < 32:
            with (self.directory / 'adapter-errors.jsonl').open('a') as stream:
                stream.write(json.dumps({'logical_id': identifier, 'type': type(error).__name__,
                    'message': str(error)[:512], 'traceback': traceback.format_exc()[:16384]}) + '\n')
            self.error_capture_count += 1
        # A21's stderr observer is bounded. Detailed failures belong to our
        # bounded file, never an undrained pipe that can block event processing.
        if len(self.errors) == 1:
            sys.stderr.write('B23 request failed; see adapter-errors.jsonl and http-failures.jsonl\n')
            sys.stderr.flush()

    async def overload_upload(self, path, upload, headers):
        """Read a real early HTTP/1 response concurrently with bounded body writes.

        The product-only overload profile uses a fresh HTTP/1 connection for its
        PUT. A reset without a complete response is still a failure, never 503.
        """
        require(self.manifest['protocol'] == 'http1', 'This declared overload transport is HTTP/1 plaintext only')
        address = urlsplit(self.application.url)
        reader, writer = await asyncio.open_connection(address.hostname, address.port)
        protocol = h11.Connection(h11.CLIENT, max_incomplete_event_size=32768)
        record = {'transport': 'HTTP/1 concurrent response reader; fresh PUT connection; Expect: 100-continue', 'body_bytes_sent': 0}
        self.overload_uploads.append(record)
        request_headers = [('Host', address.netloc), ('Authorization', 'Bearer ' + self.token),
                           ('Connection', 'close'), ('Expect', '100-continue'), *headers.items()]
        writer.write(protocol.send(h11.Request(method='PUT', target=path, headers=request_headers)))
        await writer.drain()

        continue_upload = asyncio.Event()
        async def produce():
            await continue_upload.wait()
            async for chunk in upload:
                writer.write(protocol.send(h11.Data(data=chunk)))
                await writer.drain()
                record['body_bytes_sent'] += len(chunk)
            writer.write(protocol.send(h11.EndOfMessage()))
            await writer.drain()

        producer = asyncio.create_task(produce())
        async def receive():
            status, response_headers, version = None, None, None
            body = bytearray()
            while True:
                event = protocol.next_event()
                if event is h11.NEED_DATA:
                    data = await reader.read(32768)
                    record['response_wire_bytes_read'] = record.get('response_wire_bytes_read', 0) + len(data)
                    protocol.receive_data(data)
                elif isinstance(event, h11.InformationalResponse):
                    record.setdefault('informational_statuses', []).append(event.status_code)
                    if event.status_code == 100:
                        continue_upload.set()
                elif isinstance(event, h11.Response):
                    status, response_headers, version = event.status_code, list(event.headers), event.http_version
                    record['observed_status'] = status
                elif isinstance(event, h11.Data):
                    body.extend(event.data)
                    require(len(body) <= 1_048_576, 'Overload response exceeded bounded JSON limit')
                elif isinstance(event, h11.EndOfMessage):
                    require(status is not None, 'Overload response body arrived without a status')
                    record.update(status=status, response_bytes=len(body), complete_response=True)
                    return self.observe_response(httpx.Response(status, headers=response_headers, content=bytes(body),
                        extensions={'http_version': b'HTTP/' + version}), stage='upload', method='PUT', path=path)
                elif isinstance(event, h11.ConnectionClosed):
                    raise IntegrityFailure('Overload connection closed without a complete observed HTTP response')
        try:
            return await asyncio.wait_for(receive(), 30)
        finally:
            producer.cancel()
            outcomes = await asyncio.gather(producer, return_exceptions=True)
            if isinstance(outcomes[0], Exception):
                record['producer_error'] = type(outcomes[0]).__name__
            writer.close()
            try:
                await writer.wait_closed()
            except (ConnectionError, OSError):
                pass

    async def create(self, identifier, title):
        key = self.prefix + str(identifier)
        response = await self.request('POST', '/v1/items',
            body=json.dumps({'title': title}, separators=(',', ':'), ensure_ascii=False).encode(),
            headers={'Content-Type': 'application/json', 'Idempotency-Key': key}, stage='create')
        with self.response_contract(response, 'create', 'POST', '/v1/items'):
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
                return self.complete(item, {'kind': kind, 'status': response.status_code,
                    'title': value['title'], 'responseVersion': value['responseVersion'], 'version': value['version']},
                    [(response, 'create', 'POST', '/v1/items')])
            key = self.prefix + str(identifier)
            replay = await self.request('POST', '/v1/items', body=json.dumps({'title': item['title']}).encode(),
                headers={'Content-Type': 'application/json', 'Idempotency-Key': key}, stage='replay')
            recovery_path = '/v1/operations/' + value['operationId']
            recovered = await self.request('GET', recovery_path, stage='recovery')
            return self.complete(item, {'kind': kind, 'create': response.status_code, 'replay': replay.status_code,
                'recovery': recovered.status_code, 'title': value['title'], 'responseVersion': value['responseVersion'],
                'version': value['version'], 'replay_equal': replay.content == response.content,
                'recovery_equal': recovered.content == response.content},
                [(response, 'create', 'POST', '/v1/items'), (replay, 'replay', 'POST', '/v1/items'),
                 (recovered, 'recovery', 'GET', recovery_path)])
        if kind == 'list':
            path = '/__b23/di-list' if self.manifest.get('di') else '/v1/items'
            path += '?limit=' + str(item['limit'])
            response = await self.request('GET', path, stage='list')
            with self.response_contract(response, 'list', 'GET', path):
                require(response.status_code == 200, 'Keyset query failed')
                page = response.json()
                return self.complete(item, {'kind': kind, 'status': response.status_code,
                    'titles': [row['title'] for row in page['items']],
                    'versions': [row['version'] for row in page['items']], 'has_cursor': page['nextCursor'] is not None})
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
            headers = {
                'Idempotency-Key': key, 'If-Match': '1', 'Content-Type': 'application/octet-stream',
                'Content-Length': str(item['bytes'])}
            response = (await self.overload_upload(path, upload(), headers) if self.profile.get('product_only') else
                        await self.request('PUT', path, body=upload(), headers=headers, stage='upload'))
            if response.status_code == 503 and self.profile.get('product_only'):
                require(response.json().get('code') == 'upload_capacity', 'Overload returned an unrelated 503')
                return 'rejected', {'stage': 'upload', 'code': 'upload_capacity'}
            with self.response_contract(response, 'upload', 'PUT', path):
                require(response.status_code == 200, 'Attachment binding did not confirm')
                attached = response.json(); canonical_id(attached['operationId'])
                require(attached['itemId'] == value['itemId'], 'Attachment identity changed')
                require(attached['responseVersion'] == 1 and attached['version'] == 2
                        and attached['contentHash'] == item['sha256'] and attached['contentLength'] == item['bytes'],
                        'Confirmed attachment DTO differs from the independent v1/hash/length/version oracle')
            self.mutations.append({'operation_id': attached['operationId'], 'item_id': value['itemId'],
                'key': key, 'scope': self.golden.scope_hash('tenant-a', 'alice', 'PUT /v1/items/{id}/attachment'),
                'hash': self.golden.attachment_hash('tenant-a', 'alice', value['itemId'], item['sha256'], item['bytes'], 1),
                'status': response.status_code, 'response_hex': response.content.hex(), 'kind': 'attachment',
                'content_hash': item['sha256'], 'content_length': item['bytes']})
            actual = hashlib.sha256(); length = 0
            failure_body = bytearray()
            downloaded, body_complete = None, False
            # A GET that fails before response headers must not inherit the
            # prior successful PUT as its failure response.
            self.last_response.set(None)
            try:
                async with self.client.stream('GET', path) as downloaded:
                    self.last_response.set((downloaded, 'download', 'GET', path))
                    self.protocols.add(downloaded.http_version)
                    self.http_outcomes[str(downloaded.status_code)] = self.http_outcomes.get(str(downloaded.status_code), 0) + 1
                    require(downloaded.http_version == ('HTTP/2' if self.manifest['protocol'] == 'http2-tls' else 'HTTP/1.1'),
                            'Streaming download used an unexpected protocol')
                    async for data in downloaded.aiter_bytes(32768):
                        actual.update(data); length += len(data)
                        if len(failure_body) < 65536:
                            failure_body.extend(data[:65536 - len(failure_body)])
                    body_complete = True
            except BaseException:
                if downloaded is not None:
                    self.capture_failure(downloaded, bytes(failure_body), length, actual.hexdigest(),
                        stage='download', method='GET', path=path, body_complete=body_complete)
                raise
            if downloaded.status_code != 200 or length != item['bytes'] or actual.hexdigest() != item['sha256']:
                self.capture_failure(downloaded, bytes(failure_body), length, actual.hexdigest(),
                    stage='download', method='GET', path=path)
            return self.complete(item, {'kind': kind, 'create': created.status_code, 'upload': response.status_code,
                'download': downloaded.status_code, 'bytes': length, 'sha256': actual.hexdigest(), 'version': attached['version']})
        if kind == 'diagnostic-failure':
            response = await self.request('GET', '/__b23/failure', stage='intentional-diagnostic-failure')
            return self.complete(item, {'kind': kind, 'status': response.status_code},
                [(response, 'intentional-diagnostic-failure', 'GET', '/__b23/failure')])
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
        mode = self.descriptor['environment']['B23_DIAGNOSTICS']
        require(observation['mode'] == mode and observation['configuration'] == ('Debug' if mode.startswith('debug-') else 'Release'),
                'Application diagnostic/build configuration witness differs from its manifest')
        require(observation['observerFailures'] == 0, 'Configured observation callback failed')
        self.application.latest_probe = observation
        self.counters['managed_allocated_bytes'] = self.a21.measured(observation['allocatedBytes'], 'bytes',
            'GC.GetTotalAllocatedBytes(true), whole child process since launch; includes observation overhead')
        self.counters['gc_heap_live_bytes'] = self.a21.measured(observation['heapLiveBytes'], 'bytes', 'GC.GetTotalMemory(false); whole application process')
        self.counters['gc_heap_committed_bytes'] = self.a21.measured(observation['heapCommittedBytes'], 'bytes', 'GC.GetGCMemoryInfo().TotalCommittedBytes')
        return observation

    def diagnostics_oracle(self, observation):
        if observation is None:
            return {'applicability': 'No benchmark instrumentation in the accepted product/comparator executable'}
        mode = observation['mode']
        tracing = mode in ('debug-local', 'sampled', 'full', 'exporter-saturation', 'exporter-failure')
        metering = tracing or mode == 'meter-only'
        logging = mode in ('debug-local', 'sampled', 'full', 'log-only', 'exporter-saturation', 'exporter-failure')
        require((observation['activities'] > 0) == tracing, 'Observed Activity configuration differs')
        require((observation['measurements'] > 0) == metering and (observation['metrics'] is not None) == metering,
                'Observed Meter configuration differs')
        if self.profile['kind'] == 'diagnostic-failure':
            require((observation['logCalls'] > 0) == logging, 'Configured logger was not exercised as declared')
        if mode == 'exporter-failure':
            require(observation['exportFailures'] > 0 and observation['logExports'] == 0,
                    'Controlled exporter failure was not observed')
        saturated = mode == 'exporter-saturation' and self.manifest['requests'] > 2048
        if saturated:
            require(observation['logExports'] == 0 and observation['metrics'].get('nativeweave.server.diagnostics.dropped', 0) > 0,
                    'Declared exporter saturation did not actually fill the bounded queue and drop metadata')
        return {'passed': True, 'mode': mode, 'observed_listeners': {'Activity': tracing, 'Meter': metering, 'ILogger': logging},
            'queue_saturation_positive_control': 'passed' if saturated else 'not requested by this reduced/request-count profile',
            'scope': 'Observed configuration/effect; disabled observer does not establish whole-product zero cost'}

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
            receipt['configuration_private_sha256'] = self.application.configuration_sha256
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
                receipt['image_mappings_before'] = mapping_sample(self.application.process.pid)
                emit({'kind': 'hello', 'route': self.args.route,
                    'dynamic_code_supported': self.application.dynamic,
                    'application_pid': self.application.process.pid, 'adapter_pid': os.getpid(),
                    'application_uri_scheme': urlsplit(self.application.url).scheme,
                    'receipt_directory': str(self.directory), 'admission_scope': receipt['admission_scope']})
                tasks = set()

                async def execute(identifier, token, mode):
                    self.active_identifier.set(identifier)
                    self.last_response.set(None)
                    async with self.semaphore:
                        if self.halt_dispatch:
                            value = {'reason': 'Earlier integrity/transport failure stopped new application HTTP dispatch'}
                            self.operations.append({'id': identifier, 'status': 'cancelled', 'value': value})
                            emit({'kind': 'completed', 'id': identifier, 'status': 'cancelled', 'value': value, 'counters': self.counters})
                            return
                        if identifier < self.manifest['requests'] and 'measurement_resources_before' not in receipt:
                            self.application.phase = 'measurement'
                            receipt['measurement_resources_before'] = process_sample(self.a21, self.application.process.pid)
                        emit({'kind': 'admitted', 'id': identifier})
                        try:
                            require(mode == 'ok', 'Fault injections cannot enter a throughput cell')
                            operation = json.loads(base64.urlsafe_b64decode(token.encode()))
                            outcome, value = await self.operation(identifier, operation)
                            if outcome == 'ok':
                                require(value == expected_value(operation),
                                    'Normalized HTTP/domain result failed the independent expected-value oracle')
                            # A21 finishes warmup before its measured arrivals.
                            # Observation failures are ordinary failed outcomes,
                            # never unobserved background-task exceptions.
                            if self.descriptor.get('instrumented') and identifier == self.manifest['requests'] + self.profile['warmup'] - 1:
                                receipt['probe_before'] = await self.probe()
                        except Exception as error:
                            self.halt_dispatch = True
                            self.capture_error(identifier, error)
                            outcome, value = 'failed', {'error': type(error).__name__}
                        self.operations.append({'id': identifier, 'status': outcome, 'value': value})
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
                receipt['image_mappings_after'] = mapping_sample(self.application.process.pid)
                self.application.phase = 'reconciliation-and-shutdown'
                if self.descriptor.get('instrumented'):
                    # This is after A21 request completion and CPU/RSS boundaries.
                    # Let the accepted one-second Meter cache refresh and finite
                    # exporters drain before inspecting final cumulative losses.
                    await asyncio.sleep(self.manifest['diagnostic_snapshot_settle_ms'] / 1000)
                receipt['probe_after'] = await self.probe()
                receipt['diagnostics_oracle'] = self.diagnostics_oracle(receipt['probe_after'])
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
                http_error_codes=self.http_error_codes,
                overload_upload_observations=self.overload_uploads,
                closed=self.application.closed, last_probe=self.application.latest_probe,
                resources='application/resources.jsonl', source_manifest_sha256=digest(self.args.manifest))
            receipt['failed_http_capture'] = {'captured': self.failure_capture_count, 'omitted_after_bound': self.failure_capture_omitted,
                'maximum_records': 32, 'maximum_body_bytes_per_record': 65536, 'maximum_header_bytes_per_record': 16384,
                'path': 'http-failures.jsonl' if self.failure_capture_count else None,
                'sha256': digest(self.directory / 'http-failures.jsonl') if self.failure_capture_count else None}
            receipt['adapter_error_capture'] = {'records': self.error_capture_count,
                'path': 'adapter-errors.jsonl' if self.error_capture_count else None,
                'sha256': digest(self.directory / 'adapter-errors.jsonl') if self.error_capture_count else None}
            save(self.directory / 'receipt.json', receipt)
        return 0 if receipt['status'] == 'passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--route', required=True, choices=sorted(load_a21().ROUTES))
    parser.add_argument('--a21', type=Path)
    args = parser.parse_args()
    register_process('http-adapter', os.getpid())
    return asyncio.run(Target(args).run())


if __name__ == '__main__':
    raise SystemExit(main())
