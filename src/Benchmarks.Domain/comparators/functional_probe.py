#!/usr/bin/env python3
"""Untimed comparator readiness, reusing B21's independent HTTP/durable oracle.

This deliberately does not invoke B21's maintenance/fault controller or claim
its acceptance. The B23 adapter owns measured workloads and timing. Every run
keeps real output and hashes the executable files it actually launches.
"""
import argparse
import base64
import contextlib
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'tests/Reference.TaskService'))
from run import Database, Service, Oracle, create, run_cases, sha
from golden import verify_fixture


@contextlib.contextmanager
def postgres(binary, output):
    directory = output / 'postgres'
    directory.mkdir()
    data = directory / 'data'
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    def run(name, command):
        result = subprocess.run(list(map(str, command)), capture_output=True, text=True, timeout=60)
        (directory / (name + '.log')).write_text(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError('PostgreSQL setup failed: ' + name)
        return result.stdout
    version = run('version', [binary / 'postgres', '--version']).strip()
    run('init', [binary / 'initdb', '-D', data, '-U', 'nativeweave', '--no-locale', '--encoding=UTF8', '--auth=trust'])
    run('start', [binary / 'pg_ctl', '-D', data, '-l', directory / 'server.log', '-o',
                 f'-h 127.0.0.1 -p {port} -c unix_socket_directories=', '-w', 'start'])
    try:
        yield f'postgresql://nativeweave@127.0.0.1:{port}/postgres', binary / 'psql', {
            'version': version, 'binary_sha256': sha(binary / 'postgres'),
            'durability': 'fsync=on,synchronous_commit=on,full_page_writes=on (initdb defaults)'}
    finally:
        run('stop', [binary / 'pg_ctl', '-D', data, '-m', 'fast', '-w', 'stop'])


def probe(args, command, output, database_uri, psql, database_identity):
    oracle = Oracle(output)
    golden = verify_fixture(ROOT / 'tests/Reference.TaskService/golden-v1.json')
    actual = subprocess.run([*command, '--golden-vectors'], capture_output=True, timeout=30)
    (output / 'golden.stdout.json').write_bytes(actual.stdout)
    (output / 'golden.stderr.log').write_bytes(actual.stderr)
    oracle.check('independent-golden-vectors', actual.returncode == 0 and json.loads(actual.stdout) == golden)
    if args.expect_aot:
        oracle.check('nativeaot-runtime-witness', b'B23_RUNTIME dynamic=False' in actual.stderr)
    tokens = {name: secrets.token_urlsafe(32) for name in ['alice', 'bob', 'carol', 'reader']}
    config = {'provider': args.provider, 'database': database_uri,
        'blobDirectory': str(output / 'blobs'), 'consumerDatabase': str(output / 'consumer.sqlite'),
        'listenAddress': '127.0.0.1', 'port': 0,
        'allowLocalDatabaseTlsDisable': args.provider == 'postgresql', 'runWorkers': False,
        'cursorKey': base64.b64encode(secrets.token_bytes(32)).decode(),
        'principals': [{'token': token, 'tenant': 'tenant-b' if name == 'carol' else 'tenant-a',
            'principal': name, 'canWrite': name != 'reader'} for name, token in tokens.items()]}
    if args.certificate:
        config['tlsCertificatePem'] = str(args.certificate.resolve())
        config['tlsPrivateKeyPem'] = str(args.private_key.resolve())
    database = Database(args.provider, database_uri, str(psql) if psql else None)
    started = time.monotonic()
    with Service(command, config, output / 'service', http2=args.http2, certificate=args.certificate) as service:
        result = run_cases(service, database, oracle, tokens)
        expected = 'HTTP/2' if args.http2 else 'HTTP/1.1'
        oracle.check('actual-http-protocol', service.protocols == {expected}, sorted(service.protocols))
        oracle.check('actual-http-scheme', service.observed_scheme == ('https' if args.certificate else 'http'))
    total = int(database.scalar('SELECT COUNT(*) FROM outbox'))
    deliveries = []
    for index in range((total + 15) // 16 + 2):
        pending = int(database.scalar('SELECT COUNT(*) FROM outbox WHERE delivery_state<2'))
        if pending == 0:
            break
        result_run = subprocess.run([*command, '--config', str(service.config_path), '--outbox-once'], capture_output=True, text=True, timeout=90)
        (output / f'outbox-{index:03d}.log').write_text(result_run.stdout + result_run.stderr)
        oracle.check('explicit-outbox-drain-' + str(index), result_run.returncode == 0)
        deliveries.append(json.loads(result_run.stdout)['delivered'])
    oracle.check('all-source-outbox-delivered', int(database.scalar('SELECT COUNT(*) FROM outbox WHERE delivery_state=2')) == total)
    with sqlite3.connect(config['consumerDatabase']) as connection:
        rows = connection.execute('SELECT event_id,payload_hash,delivery_attempts,effects FROM receipts ORDER BY event_id').fetchall()
    oracle.check('independent-persistent-consumer-effects', len(rows) == total and all(row[2:] == (1, 1) for row in rows))
    encoded = 'hex(payload)' if args.provider == 'sqlite' else "encode(payload,'hex')"
    payloads = {row[0]: hashlib.sha256(bytes.fromhex(row[1])).hexdigest() for row in database.sql('SELECT event_id,' + encoded + ' FROM outbox')}
    oracle.check('consumer-content-identity', all(payloads[row[0]] == row[1] for row in rows))
    event = rows[0][0]
    database.sql(f"UPDATE outbox SET delivery_state=0,available_at=0,lease_until=0 WHERE event_id='{event}'")
    duplicate = subprocess.run([*command, '--config', str(service.config_path), '--outbox-once'], capture_output=True, text=True, timeout=90)
    (output / 'outbox-redelivery.log').write_text(duplicate.stdout + duplicate.stderr)
    with sqlite3.connect(config['consumerDatabase']) as connection:
        duplicate_row = connection.execute('SELECT delivery_attempts,effects FROM receipts WHERE event_id=?', (event,)).fetchone()
    oracle.check('persistent-redelivery-exactly-one-effect', duplicate.returncode == 0 and duplicate_row == (2, 1))
    with Service(command, config, output / 'restart', http2=args.http2, certificate=args.certificate) as restarted:
        replay = create(restarted, tokens['alice'], 'one', 'hello')
        oracle.check('exact-durable-replay-after-restart', replay[0] == 201 and replay[1].hex() == result['original_response_hex'])
        recovered = restarted.request('GET', '/v1/operations/' + result['operation_id'], token=tokens['alice'])
        oracle.check('exact-recovery-after-restart', recovered[0] == 200 and recovered[1] == replay[1])
        download = restarted.request('GET', '/v1/items/' + result['item_id'] + '/attachment', token=tokens['alice'])
        oracle.check('immutable-attachment-after-restart', download[0] == 200 and hashlib.sha256(download[1]).hexdigest() == result['attachment_sha256'])
    artifacts = {str(Path(value).resolve()): sha(value) for value in command if Path(value).is_file()}
    for value in args.artifact:
        artifacts[str(value.resolve())] = sha(value)
    summary = {'task': 'B23-comparator-readiness', 'platform': 'linux-x64', 'provider': args.provider,
        'scheme': service.observed_scheme, 'protocol': expected, 'actual_artifacts': artifacts,
        'database_identity': database_identity, 'cases_passed': len(oracle.results),
        'elapsed_seconds': round(time.monotonic() - started, 3), 'performance_measurement': False,
        'fault_soak_acceptance': False, 'maintenance_acceptance': False,
        'controller_sha256': sha(__file__), 'b21_oracle_sha256': sha(ROOT / 'tests/Reference.TaskService/run.py'),
        'golden_fixture_sha256': sha(ROOT / 'tests/Reference.TaskService/golden-v1.json')}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--provider', choices=['sqlite', 'postgresql'], required=True)
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--certificate', type=Path)
    parser.add_argument('--private-key', type=Path)
    parser.add_argument('--http2', action='store_true')
    parser.add_argument('--expect-aot', action='store_true')
    parser.add_argument('--artifact', type=Path, action='append', default=[])
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command or bool(args.certificate) != bool(args.private_key) or args.http2 and not args.certificate:
        parser.error('An executable and complete selected TLS inputs are required.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    if args.provider == 'postgresql':
        if args.postgres_bin is None:
            parser.error('--postgres-bin is required for an isolated actual PostgreSQL server.')
        with postgres(args.postgres_bin.resolve(), output) as (uri, psql, identity):
            probe(args, command, output, uri, psql, identity)
    else:
        probe(args, command, output, str(output / 'source.sqlite'), None, {'required_sqlite_version': '3.51.3'})


if __name__ == '__main__':
    main()
