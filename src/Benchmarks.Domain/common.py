"""B23 file/identity helpers; A21 remains the timing/statistics authority."""
from __future__ import annotations

import csv
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess


def load_a21(path=None):
    path = Path(path) if path else Path(__file__).resolve().parent.parent / 'Benchmarks.Harness/nativeweave_bench.py'
    spec = importlib.util.spec_from_file_location('nativeweave_a21', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')
    temporary.replace(path)


def load(path):
    return json.loads(Path(path).read_text())


def inventory(directory):
    directory = Path(directory)
    return {str(path.relative_to(directory)): digest(path) for path in sorted(directory.rglob('*'))
            if path.is_file() and '__pycache__' not in path.parts}


def check_files(files):
    for file, expected in files.items():
        if digest(file) != expected:
            raise ValueError('Immutable input changed: ' + file)


def source_identity(path):
    path = Path(path).resolve()
    process = subprocess.run(['git', '-C', str(path), 'rev-parse', 'HEAD'], capture_output=True, text=True)
    if process.returncode:
        return {'commit': None, 'path': str(path), 'state': 'exported files; SHA-256 manifest is authoritative'}
    return {'commit': process.stdout.strip(), 'path': str(path),
            'scope_status': subprocess.check_output(['git', '-C', str(path), 'status', '--porcelain'], text=True).strip()}


class Database:
    """Separate SQLite/psql observer, never a product diagnostic endpoint."""
    def __init__(self, provider, connection, psql=None):
        self.provider, self.connection, self.psql = provider, connection, psql

    def sql(self, query):
        if self.provider == 'sqlite':
            with sqlite3.connect(self.connection, timeout=30) as connection:
                cursor = connection.execute(query)
                return cursor.fetchall() if cursor.description else []
        result = subprocess.run([self.psql, self.connection, '-X', '--csv', '-t',
            '-v', 'ON_ERROR_STOP=1', '-c', query], capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError('Independent PostgreSQL query failed; retained subprocess status ' + str(result.returncode))
        return [tuple(row) for row in csv.reader(io.StringIO(result.stdout)) if row]

    def scalar(self, query):
        rows = self.sql(query)
        if len(rows) != 1 or len(rows[0]) != 1:
            raise AssertionError('Expected one independent SQL scalar.')
        return rows[0][0]

    def operation_rows(self, prefix):
        if not prefix.isascii() or any(value not in 'abcdefghijklmnopqrstuvwxyz0123456789-_' for value in prefix):
            raise ValueError('Invalid observer-owned key prefix.')
        encoded = 'hex(response_bytes)' if self.provider == 'sqlite' else "encode(response_bytes,'hex')"
        return self.sql('SELECT operation_id,scope,request_key,request_hash,response_version,response_status,'
            + encoded + f" FROM operations WHERE request_key LIKE '{prefix}%' ORDER BY request_key")


def process_sample(a21, pid):
    """Augment A21 procfs observations with CPU/fd/socket counts for this PID."""
    value = {'pid': pid, 'time_ns': a21.time.monotonic_ns(), 'counters': a21.observe_process(pid)}
    try:
        stat = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        value['cpu_ticks'] = int(stat[11]) + int(stat[12])
        value['clock_ticks_per_second'] = os.sysconf('SC_CLK_TCK')
        descriptors = list(Path(f'/proc/{pid}/fd').iterdir())
        value['fd_count'] = len(descriptors)
        value['socket_count'] = sum(os.readlink(path).startswith('socket:') for path in descriptors)
        value['method'] = 'Linux procfs; child application PID, excluding benchmark adapter/client and database server'
    except (OSError, ValueError):
        value['cpu_ticks'] = None
        value['fd_count'] = value['socket_count'] = None
    return value


def expected_value(operation):
    """Independent domain normalization excludes generated identities and wall time."""
    kind = operation['kind']
    if kind == 'create':
        return {'kind': kind, 'status': 201, 'title': operation['title'], 'responseVersion': 1, 'version': 1}
    if kind == 'cycle':
        return {'kind': kind, 'create': 201, 'replay': 201, 'recovery': 200,
                'title': operation['title'], 'responseVersion': 1, 'version': 1,
                'replay_equal': True, 'recovery_equal': True}
    if kind == 'list':
        return {'kind': kind, 'status': 200,
                'titles': ['seed-' + str(index).zfill(4) for index in range(operation['limit'])],
                'versions': [1] * operation['limit'], 'has_cursor': True}
    if kind == 'stream':
        return {'kind': kind, 'create': 201, 'upload': 200, 'download': 200,
                'bytes': operation['bytes'], 'sha256': operation['sha256'], 'version': 2}
    if kind == 'diagnostic-failure':
        return {'kind': kind, 'status': 500}
    raise ValueError('Unknown domain operation: ' + kind)


def payload_chunk(seed, count=32768):
    block = hashlib.sha256(('NativeWeave B23 payload ' + str(seed)).encode()).digest()
    return (block * ((count + 31) // 32))[:count]


def payload_hash(seed, length):
    chunk = payload_chunk(seed)
    result = hashlib.sha256()
    remaining = length
    while remaining:
        count = min(len(chunk), remaining)
        result.update(chunk[:count])
        remaining -= count
    return result.hexdigest()
