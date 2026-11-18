#!/usr/bin/env python3
"""External B21 protocol/durable-data oracle over an actual packaged executable.

This controller imports no NativeWeave assemblies or service implementation. It
observes real HTTP and separately queries SQLite/PostgreSQL. It is not B22's
fault/soak acceptance and never turns an unexecuted matrix cell into a pass.
"""
import argparse
import concurrent.futures
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import queue
import secrets
import signal
import sqlite3
import ssl
import subprocess
import sys
import threading
import time
import uuid
from urllib.parse import quote

import httpx
from golden import create_hash, scope_hash, verify_fixture

HERE = Path(__file__).resolve().parent


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


class Database:
    """Independent durable-data access; never call a product diagnostic endpoint."""
    def __init__(self, provider, database, psql=None):
        self.provider, self.database, self.psql = provider, database, psql

    def sql(self, query):
        if self.provider == 'sqlite':
            with sqlite3.connect(self.database, timeout=30) as connection:
                cursor = connection.execute(query)
                return cursor.fetchall() if cursor.description else []
        result = subprocess.run([self.psql or 'psql', self.database, '-X',
            '--csv', '-t', '-v', 'ON_ERROR_STOP=1', '-c', query],
            capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError('Independent PostgreSQL query failed: ' + result.stderr[-2000:])
        return [tuple(row) for row in csv.reader(io.StringIO(result.stdout)) if row]

    def scalar(self, query):
        rows = self.sql(query)
        if len(rows) != 1 or len(rows[0]) != 1:
            raise AssertionError('Expected one independent SQL scalar.')
        return rows[0][0]


class Service:
    def __init__(self, command, config, output, *, http2=False, certificate=None):
        self.command, self.config = command, config
        self.output = Path(output)
        self.http2, self.certificate = http2, certificate
        self.protocols = set()
        self._protocol_lock = threading.Lock()
        self.process = None

    def __enter__(self):
        self.output.mkdir(parents=True, exist_ok=False)
        self.config_path = self.output / 'config.private.json'
        self.config_path.write_text(json.dumps(self.config, indent=2) + '\n')
        self.config_path.chmod(0o600)
        self.log = (self.output / 'service.log').open('w')
        self.ready = queue.Queue()
        self.process = subprocess.Popen([*self.command, '--config', str(self.config_path)],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            bufsize=1, start_new_session=True)

        def consume():
            for line in self.process.stdout:
                self.log.write(line)
                self.log.flush()
                if line.startswith('READY '):
                    self.ready.put(line[6:].strip())
            self.ready.put(None)

        self.reader = threading.Thread(target=consume, daemon=True)
        self.reader.start()
        try:
            self.url = self.ready.get(timeout=60)
            if not self.url:
                raise RuntimeError('Service exited before READY; inspect its retained log.')
            return self
        except BaseException:
            self.__exit__(*sys.exc_info())
            raise

    def request(self, method, path, body=None, headers=None, token=None):
        selected = dict(headers or {})
        if token is not None:
            selected['Authorization'] = 'Bearer ' + token
        verify = ssl.create_default_context(cafile=str(self.certificate)) if self.certificate else True
        # Independent clients avoid sharing controller state between concurrent requests.
        with httpx.Client(http2=self.http2, verify=verify, timeout=60, trust_env=False) as client:
            response = client.request(method, self.url.rstrip('/') + path, content=body, headers=selected)
            with self._protocol_lock:
                self.protocols.add(response.http_version)
            return response.status_code, response.content, dict(response.headers)

    def admin(self, option):
        result = subprocess.run([*self.command, '--config', str(self.config_path), option],
            capture_output=True, timeout=90)
        (self.output / (option.lstrip('-') + '.log')).write_bytes(result.stdout + result.stderr)
        if result.returncode:
            raise AssertionError('Administrative command failed: ' + option)
        return result.stdout

    def __exit__(self, exc_type, exc, traceback):
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=40)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=10)
                if exc_type is None:
                    raise AssertionError('Service did not complete its bounded shutdown.')
        if hasattr(self, 'reader'):
            self.reader.join(timeout=5)
        if hasattr(self, 'log'):
            self.log.close()


class Oracle:
    def __init__(self, output):
        self.output = output
        self.results = []

    def check(self, name, predicate, detail=None):
        result = {'case': name, 'passed': bool(predicate)}
        if detail is not None:
            result['detail'] = detail
        self.results.append(result)
        (self.output / 'cases.json').write_text(json.dumps(self.results, indent=2) + '\n')
        if not predicate:
            raise AssertionError(name)
        print('PASS ' + name, flush=True)


def json_body(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode()


def create(service, token, key, title):
    return service.request('POST', '/v1/items', json_body({'title': title}),
        {'Content-Type': 'application/json', 'Idempotency-Key': key}, token)


def run_cases(service, database, oracle, identities):
    alice, bob, carol, reader = [identities[name] for name in ['alice', 'bob', 'carol', 'reader']]
    oracle.check('authentication-before-business', service.request('GET', '/v1/items')[0] == 401)
    oracle.check('write-authorization', create(service, reader, 'reader-denied', 'denied')[0] == 403)

    status, original, _ = create(service, alice, 'one', 'hello')
    oracle.check('create-confirmed', status == 201, {'status': status})
    first = json.loads(original)
    oracle.check('stable-response-version', first['responseVersion'] == 1 and first['version'] == 1)
    oracle.check('stable-response-title', first['title'] == 'hello')
    oracle.check('canonical-request-hash', database.scalar("SELECT request_hash FROM operations WHERE request_key='one'") == create_hash('tenant-a', 'alice', 'hello'))
    oracle.check('authenticated-scope-hash', database.scalar("SELECT scope FROM operations WHERE request_key='one'") == scope_hash('tenant-a', 'alice', 'POST /v1/items'))
    for attempt in range(3):
        s, body, _ = create(service, alice, 'one', 'hello')
        oracle.check('exact-replay-' + str(attempt), s == 201 and body == original)
    oracle.check('different-hash-conflict', create(service, alice, 'one', 'changed')[0] == 409)
    oracle.check('conflict-preserves-winner', create(service, alice, 'one', 'hello')[1] == original)

    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as executor:
        replies = list(executor.map(lambda _: create(service, alice, 'concurrent', 'same payload'), range(12)))
    oracle.check('concurrent-same-key-identical', all(s == 201 and b == replies[0][1] for s, b, _ in replies))
    winner = json.loads(replies[0][1])
    operation_id = winner['operationId']
    for table in ['operations', 'items', 'outbox']:
        oracle.check('single-concurrent-effect-' + table,
            int(database.scalar(f"SELECT COUNT(*) FROM {table} WHERE operation_id='{operation_id}'")) == 1)

    foreign = create(service, carol, 'one', 'foreign tenant')
    peer = create(service, bob, 'one', 'another principal')
    oracle.check('same-key-independent-tenants', foreign[0] == 201 and json.loads(foreign[1])['operationId'] != first['operationId'])
    oracle.check('same-key-independent-principals', peer[0] == 201 and json.loads(peer[1])['operationId'] != first['operationId'])
    path = '/v1/operations/' + first['operationId']
    recovered = service.request('GET', path, token=alice)
    oracle.check('authorized-operation-recovery', recovered[0] in (200, 201) and recovered[1] == original)
    for label, token in [('tenant', carol), ('principal', bob)]:
        oracle.check('operation-isolation-' + label, service.request('GET', path, token=token)[0] in (403, 404))
    forged = service.request('GET', path, headers={'X-Tenant': 'tenant-a', 'X-Principal': 'alice'}, token=carol)
    oracle.check('forged-identity-headers-ignored', forged[0] in (403, 404))
    missing = service.request('GET', '/v1/operations/00000000-0000-0000-0000-000000000000', token=alice)
    oracle.check('missing-recovery-is-unconfirmed', missing[0] == 404 and json.loads(missing[1])['code'] == 'outcome_unconfirmed')

    before = int(database.scalar('SELECT COUNT(*) FROM operations'))
    for name, body, key in [
        ('empty-title', {'title': ''}, 'invalid-1'),
        ('oversize-title', {'title': 'x' * 1025}, 'invalid-2'),
        ('unknown-json-field', {'title': 'ok', 'surprise': True}, 'invalid-3'),
        ('oversize-key', {'title': 'ok'}, 'k' * 129),
        ('empty-key', {'title': 'ok'}, ''),
    ]:
        s, _, _ = service.request('POST', '/v1/items', json_body(body),
            {'Content-Type': 'application/json', 'Idempotency-Key': key}, alice)
        oracle.check('reject-' + name, s in (400, 413))
    oracle.check('invalid-input-no-side-effects', int(database.scalar('SELECT COUNT(*) FROM operations')) == before)
    s, _, _ = service.request('POST', '/v1/items', b'{"title":"' + b'x' * (1024 * 1024) + b'"}',
        {'Content-Type': 'application/json', 'Idempotency-Key': 'oversize-json'}, alice)
    oracle.check('json-one-mib-route-limit', s == 413)

    for index in range(6):
        oracle.check('page-data-' + str(index), create(service, alice, 'page-' + str(index), 'page ' + str(index))[0] == 201)
    # Force a real tie group so ordering cannot accidentally pass on timestamps alone.
    database.sql("UPDATE items SET created_at=1790000000000 WHERE tenant_id='tenant-a'")
    expected = [row[0] for row in database.sql("SELECT item_id FROM items WHERE tenant_id='tenant-a' ORDER BY created_at,item_id")]
    collected, cursor, first_cursor = [], None, None
    for _ in range(len(expected) + 1):
        page_path = '/v1/items?limit=2' + ('&after=' + quote(cursor, safe='') if cursor else '')
        s, body, _ = service.request('GET', page_path, token=alice)
        oracle.check('page-status-' + str(len(collected)), s == 200)
        page = json.loads(body)
        oracle.check('page-bounded-' + str(len(collected)), len(page['items']) <= 2)
        collected += [item['itemId'] for item in page['items']]
        cursor = page['nextCursor']
        first_cursor = first_cursor or cursor
        if cursor is None:
            break
    oracle.check('keyset-ties-no-loss-or-duplicates', collected == expected)
    if first_cursor:
        position = len(first_cursor) // 2
        tampered = first_cursor[:position] + ('A' if first_cursor[position] != 'A' else 'B') + first_cursor[position+1:]
        oracle.check('tampered-cursor-rejected', service.request('GET', '/v1/items?after=' + quote(tampered, safe=''), token=alice)[0] == 400)
        oracle.check('cross-tenant-cursor-rejected', service.request('GET', '/v1/items?after=' + quote(first_cursor, safe=''), token=carol)[0] in (400, 403))
    oversized_page = service.request('GET', '/v1/items?limit=101', token=alice)
    oracle.check('maximum-one-hundred-rows', oversized_page[0] == 400 or (oversized_page[0] == 200 and len(json.loads(oversized_page[1])['items']) <= 100))

    data = bytes(range(256)) * 401
    attachment = '/v1/items/' + first['itemId'] + '/attachment'
    headers = {'Idempotency-Key': 'attachment-1', 'If-Match': '1', 'Content-Type': 'application/octet-stream', 'Content-Length': str(len(data))}
    s, attachment_result, _ = service.request('PUT', attachment, data, headers, alice)
    oracle.check('attachment-confirmed', s in (200, 201), {'status': s})
    s, replay, _ = service.request('PUT', attachment, data, headers, alice)
    oracle.check('attachment-exact-replay', s in (200, 201) and replay == attachment_result)
    s, received, _ = service.request('GET', attachment, token=alice)
    oracle.check('attachment-roundtrip', s == 200 and received == data)
    oracle.check('attachment-independent-digest', database.scalar(f"SELECT content_hash FROM attachments WHERE tenant_id='tenant-a' AND item_id='{first['itemId']}'") == hashlib.sha256(data).hexdigest())
    oracle.check('attachment-cross-tenant-denial', service.request('GET', attachment, token=carol)[0] in (403, 404))
    bad_version = dict(headers, **{'Idempotency-Key': 'attachment-wrong-version', 'If-Match': '0'})
    oracle.check('attachment-version-conflict', service.request('PUT', attachment, data, bad_version, alice)[0] in (409, 412))
    changed = bytearray(data)
    changed[-1] ^= 1
    oracle.check('attachment-same-key-different-content', service.request('PUT', attachment, bytes(changed), headers, alice)[0] == 409)
    oracle.check('attachment-conflict-keeps-durable-content', service.request('GET', attachment, token=alice)[1] == data)

    # The client may hold a whole fixture; the product must stream it under its
    # separate 4MiB request credit, in chunks no larger than 32KiB.
    large = b'0123456789abcdef' * ((64 * 1024 * 1024) // 16)
    large_headers = dict(headers, **{'Idempotency-Key': 'attachment-at-limit', 'If-Match': '2', 'Content-Length': str(len(large))})
    s, _, _ = service.request('PUT', attachment, large, large_headers, alice)
    oracle.check('attachment-exact-sixty-four-mib', s in (200, 201), {'status': s, 'bytes': len(large)})
    s, received, _ = service.request('GET', attachment, token=alice)
    oracle.check('attachment-large-streamed-roundtrip', s == 200 and hashlib.sha256(received).digest() == hashlib.sha256(large).digest())
    del received, large

    retention = create(service, alice, 'retention-expiry', 'retention example')
    oracle.check('retention-example-confirmed', retention[0] == 201)
    return {'item_id': first['itemId'], 'operation_id': first['operationId'],
            'attachment_sha256': database.scalar(f"SELECT content_hash FROM attachments WHERE tenant_id='tenant-a' AND item_id='{first['itemId']}'"),
            'retention_operation_id': json.loads(retention[1])['operationId']}


def run_operator_cases(command, config, config_path, output, database, oracle, result):
    """Deterministic operator-driven redelivery/retention/GC, not crash evidence."""
    def admin(option, label):
        process = subprocess.run([*command, '--config', str(config_path), option],
            capture_output=True, timeout=120)
        (output / (label + '.log')).write_bytes(process.stdout + process.stderr)
        oracle.check(label, process.returncode == 0, {'exit_code': process.returncode})

    for batch in range(4):
        admin('--outbox-once', 'outbox-batch-' + str(batch))
        if int(database.scalar('SELECT COUNT(*) FROM outbox WHERE delivery_state<>2')) == 0:
            break
    oracle.check('outbox-acknowledged', int(database.scalar('SELECT COUNT(*) FROM outbox WHERE delivery_state<>2')) == 0)
    event = database.scalar(f"SELECT event_id FROM outbox WHERE operation_id='{result['operation_id']}'")
    consumer = Database('sqlite', config['consumerDatabase'])
    before = int(consumer.scalar(f"SELECT delivery_attempts FROM receipts WHERE event_id='{event}'"))
    database.sql(f"UPDATE outbox SET delivery_state=0,available_at=0,lease_until=0,completed_at=0 WHERE event_id='{event}'")
    admin('--outbox-once', 'outbox-explicit-redelivery')
    oracle.check('persistent-consumer-counts-duplicate', int(consumer.scalar(f"SELECT delivery_attempts FROM receipts WHERE event_id='{event}'")) == before + 1)
    oracle.check('persistent-consumer-one-effect', int(consumer.scalar(f"SELECT effects FROM receipts WHERE event_id='{event}'")) == 1)
    oracle.check('consumer-every-event-one-effect', int(consumer.scalar('SELECT COUNT(*) FROM receipts WHERE effects<>1')) == 0)

    old = int(time.time() * 1000) - 8 * 24 * 60 * 60 * 1000
    operation = result['retention_operation_id']
    database.sql(f"UPDATE operations SET completed_at={old} WHERE operation_id='{operation}'")
    database.sql(f"UPDATE outbox SET delivery_state=0,completed_at=0 WHERE operation_id='{operation}'")
    admin('--maintenance-once', 'retention-with-pending-outbox')
    oracle.check('pending-outbox-protects-old-operation', int(database.scalar(f"SELECT COUNT(*) FROM operations WHERE operation_id='{operation}'")) == 1)
    database.sql(f"UPDATE outbox SET delivery_state=2,completed_at={old} WHERE operation_id='{operation}'")
    recovery = Path(config['blobDirectory']) / 'recovery' / (operation + '.json')
    recovery.write_text(json.dumps({'version': 1, 'operationId': operation,
        'scope': scope_hash('tenant-a', 'alice', 'POST /v1/items'), 'requestKey': 'retention-expiry',
        'requestHash': create_hash('tenant-a', 'alice', 'retention example'), 'createdAt': old}))
    admin('--maintenance-once', 'retention-with-recovery-manifest')
    oracle.check('recovery-manifest-protects-old-operation', int(database.scalar(f"SELECT COUNT(*) FROM operations WHERE operation_id='{operation}'")) == 1)
    recovery.unlink()
    admin('--maintenance-once', 'retention-after-protection-resolved')
    oracle.check('expired-unreferenced-operation-removed', int(database.scalar(f"SELECT COUNT(*) FROM operations WHERE operation_id='{operation}'")) == 0)

    root = Path(config['blobDirectory'])
    fixtures = {}
    for name in ['old-orphan', 'recent-orphan', 'protected-recovery']:
        payload = ('independent-' + name).encode()
        digest = hashlib.sha256(payload).hexdigest()
        path = root / 'blobs' / digest
        path.write_bytes(payload)
        fixtures[name] = (path, payload, digest)
        if name != 'recent-orphan':
            os.utime(path, (old / 1000, old / 1000))
    protected_id = str(uuid.uuid4())
    protected = fixtures['protected-recovery']
    manifest = {'version': 1, 'operationId': protected_id, 'tenant': 'tenant-a', 'principal': 'alice',
        'itemId': result['item_id'], 'requestKey': 'protected-fixture', 'expectedVersion': 3,
        'contentHash': protected[2], 'contentLength': len(protected[1]), 'state': 'prepared', 'createdAt': old}
    (root / 'staging' / (protected_id + '.json')).write_text(json.dumps(manifest))
    admin('--maintenance-once', 'orphan-grace-and-protection')
    oracle.check('old-unreferenced-blob-collected', not fixtures['old-orphan'][0].exists())
    oracle.check('recent-blob-grace-preserved', fixtures['recent-orphan'][0].exists())
    oracle.check('prepared-recovery-blob-preserved', protected[0].exists())
    oracle.check('authorized-reference-blob-preserved', (root / 'blobs' / result['attachment_sha256']).exists())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--provider', choices=['sqlite', 'postgresql'], required=True)
    parser.add_argument('--database', required=True)
    parser.add_argument('--psql')
    parser.add_argument('--http2', action='store_true')
    parser.add_argument('--tls-certificate', type=Path)
    parser.add_argument('--tls-private-key', type=Path)
    parser.add_argument('--postgres-oracles', action='store_true')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('Specify the actual product executable after --.')
    if args.http2 and not args.tls_certificate:
        parser.error('This HTTP2 oracle negotiates TLS ALPN and needs --tls-certificate.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    oracle = Oracle(output)
    golden = verify_fixture(HERE / 'golden-v1.json')
    product = subprocess.run([*command, '--golden-vectors'], capture_output=True, timeout=30)
    (output / 'golden-product.log').write_bytes(product.stdout + product.stderr)
    oracle.check('independent-golden-vectors', product.returncode == 0 and json.loads(product.stdout) == golden)
    tokens = {name: secrets.token_urlsafe(32) for name in ['alice', 'bob', 'carol', 'reader']}
    config = {'provider': args.provider, 'database': args.database,
        'blobDirectory': str(output / 'blobs'), 'consumerDatabase': str(output / 'consumer.sqlite'),
        'listenAddress': '127.0.0.1', 'port': 0,
        'allowLocalDatabaseTlsDisable': args.provider == 'postgresql', 'runWorkers': False,
        'cursorKey': __import__('base64').b64encode(secrets.token_bytes(32)).decode(),
        'principals': [{'token': token, 'tenant': 'tenant-b' if name == 'carol' else 'tenant-a',
            'principal': name, 'canWrite': name != 'reader'} for name, token in tokens.items()]}
    if args.tls_certificate:
        config['tlsCertificatePem'] = str(args.tls_certificate.resolve())
        config['tlsPrivateKeyPem'] = str(args.tls_private_key.resolve())
    database = Database(args.provider, args.database, args.psql)
    started = time.monotonic()

    def factory(overrides, path):
        return Service(command, overrides, path, http2=args.http2, certificate=args.tls_certificate)

    with factory(config, output / 'service') as service:
        result = run_cases(service, database, oracle, tokens)
        expected_protocol = 'HTTP/2' if args.http2 else 'HTTP/1.1'
        oracle.check('actual-http-protocol', service.protocols == {expected_protocol}, sorted(service.protocols))
    run_operator_cases(command, config, service.config_path, output, database, oracle, result)
    with factory(config, output / 'restart') as restarted:
        retry = create(restarted, tokens['alice'], 'retention-expiry', 'retention example')
        oracle.check('expired-key-no-permanent-replay-promise', retry[0] == 201 and json.loads(retry[1])['operationId'] != result['retention_operation_id'])
        download = restarted.request('GET', '/v1/items/' + result['item_id'] + '/attachment', token=tokens['alice'])
        oracle.check('referenced-attachment-survives-restart', download[0] == 200 and hashlib.sha256(download[1]).hexdigest() == result['attachment_sha256'])
    if args.postgres_oracles:
        if args.provider != 'postgresql':
            parser.error('--postgres-oracles requires PostgreSQL.')
        from postgres_oracles import run_postgres_oracles
        run_postgres_oracles(command, config, output / 'postgres-oracles', factory, database.sql)
    summary = {'task': 'B21', 'provider': args.provider, 'platform': 'linux-x64',
        'http_protocol': expected_protocol, 'tls': bool(args.tls_certificate),
        'duration_seconds': round(time.monotonic() - started, 3),
        'cases_passed': len(oracle.results), 'result': result,
        'controller_sha256': sha(__file__), 'golden_sha256': sha(HERE / 'golden-v1.json'),
        'postgres_fault_oracles': args.postgres_oracles,
        'b22_soak_acceptance': False}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
