"""B23-specific integrity checks; A21 timing/statistics tests are not repeated."""
import importlib.util
import json
import hashlib
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/Benchmarks.Domain'
sys.path.insert(0, str(SOURCE))
from common import Database, check_files, digest, expected_value, payload_hash
from http_target import IntegrityFailure, Target

spec = importlib.util.spec_from_file_location('b23_controller', SOURCE / 'run.py')
controller = importlib.util.module_from_spec(spec)
spec.loader.exec_module(controller)


class DomainIntegrity(unittest.TestCase):
    def test_artifact_mutation_fails_before_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'binary'
            path.write_bytes(b'accepted artifact')
            expected = {str(path): digest(path)}
            check_files(expected)
            path.write_bytes(b'changed artifact')
            with self.assertRaisesRegex(ValueError, 'Immutable input changed'):
                check_files(expected)

    def test_dataset_covers_every_declared_input_with_independent_values(self):
        profile = json.loads((SOURCE / 'profile-v1.json').read_text())
        for case in profile['workloads']:
            selected = dict(case, requests=3)
            values = controller.dataset(selected, profile['seed'])
            self.assertEqual(3, len(values))
            self.assertTrue(all('\n' not in value['input'] and '\t' not in value['input'] for value in values))
            self.assertTrue(all(isinstance(value['expected'], dict) for value in values))
        stream = controller.operation({'kind': 'stream', 'bytes': 65537}, 41, 0)
        self.assertEqual(payload_hash(41, 65537), expected_value(stream)['sha256'])

    def test_failed_calibration_route_cannot_freeze_a_partial_noise_band(self):
        rows = []
        for route in ('direct-rust', 'nativeweave-aot-rust'):
            for repetition in range(3):
                rows.append({'case': 'cell', 'route': route, 'valid': route == 'direct-rust',
                    'records': [{'scheduled_ns': 0, 'completion_ns': 100 + repetition}]})
        self.assertEqual({}, controller.calibration_bands(rows, 1))

    def test_one_failed_calibration_repetition_cannot_be_dropped_from_the_band(self):
        rows = [{'case': 'cell', 'route': 'direct-rust', 'valid': repetition != 2,
            'records': [{'scheduled_ns': 0, 'completion_ns': 100 + repetition}]} for repetition in range(3)]
        self.assertEqual({}, controller.calibration_bands(rows, 1, 3))
        rows[-1]['valid'] = True
        self.assertIn('cell', controller.calibration_bands(rows, 1, 3))

    def test_declared_unsupported_route_is_not_an_executed_comparison(self):
        route = {'id': 'managed-aot', 'supported': {'providers': ['sqlite'], 'protocols': ['http1'], 'kinds': ['create']}}
        self.assertIsNone(controller.unsupported(route, {'provider': 'sqlite', 'protocol': 'http1', 'kind': 'create'}))
        self.assertIn('postgresql', controller.unsupported(route, {'provider': 'postgresql', 'protocol': 'http1', 'kind': 'create'}))
        self.assertIn('resource/diagnostic', controller.unsupported(route, {'provider': 'sqlite', 'protocol': 'http1', 'kind': 'create', 'product_only': True}))

    def test_repository_relative_comparator_pins_are_resolved_and_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'source').mkdir()
            lock = root / 'source/lock.json'; lock.write_text('{}')
            binary = root / 'app'; binary.write_bytes(b'immutable app')
            manifest = root / 'source/comparators/manifest.json'; manifest.parent.mkdir()
            route = {'command': ['app'], 'artifacts': {'app': digest(binary)},
                     'lock_hashes': {'source/lock.json': digest(lock)},
                     'source_identity': {'files': {'source/lock.json': digest(lock)}}}
            actual = controller.comparator_route('managed-aot', route, manifest)
            self.assertEqual([str(binary)], actual['command'])
            self.assertIn(str(lock), actual['lock_hashes'])
            lock.write_text('{"changed":true}')
            with self.assertRaisesRegex(ValueError, 'Immutable input changed'):
                controller.comparator_route('managed-aot', route, manifest)

    def oracle_fixture(self, directory):
        """Independent SQL/file fixture tests the observer, not product behavior."""
        target = Target.__new__(Target)
        target.prefix = 'b23-fixture-'
        target.config = {'blobDirectory': str(directory / 'content')}
        database = directory / 'fixture.sqlite'
        blob = b'independently persisted fixture bytes'
        content_hash = hashlib.sha256(blob).hexdigest()
        blob_path = directory / 'content/blobs' / content_hash
        blob_path.parent.mkdir(parents=True); blob_path.write_bytes(blob)
        target.mutations = []
        with sqlite3.connect(database) as connection:
            connection.executescript((ROOT / 'src/Reference.TaskService/Migrations/sqlite.sql').read_text())
            for index, kind in enumerate(('create', 'attachment')):
                key, operation = target.prefix + str(index), 'operation-' + str(index)
                response = json.dumps({'fixture_operation': operation}).encode()
                status = 201 if kind == 'create' else 200
                connection.execute('INSERT INTO operations VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                    ('scope-' + kind, key, 'hash-' + kind, operation, 'tenant', 'principal', kind, 1, status, response, 1))
                connection.execute('INSERT INTO outbox VALUES(?,?,?,?,?,?,?,?,?,?)',
                    ('event-' + str(index), 'tenant', operation, 1, response, 0, 0, 1, 0, 0))
                target.mutations.append({'key': key, 'kind': kind, 'operation_id': operation,
                    'scope': 'scope-' + kind, 'hash': 'hash-' + kind, 'status': status,
                    'response_hex': response.hex(), 'item_id': 'item', 'content_hash': content_hash,
                    'content_length': len(blob)})
            connection.execute('INSERT INTO items VALUES(?,?,?,?,?,?)', ('tenant', 'item', 'operation-0', 'fixture', 1, 2))
            connection.execute('INSERT INTO attachments VALUES(?,?,?,?,?,?)',
                ('tenant', 'item', content_hash, len(blob), 'operation-1', 2))
        target.database = Database('sqlite', str(database))
        return target, blob_path

    def test_durable_oracle_rejects_independent_response_scope_hash_and_outbox_corruption(self):
        mutations = ["UPDATE operations SET response_bytes=x'00' WHERE operation_id='operation-0'",
                     "UPDATE operations SET scope='wrong' WHERE operation_id='operation-0'",
                     "UPDATE operations SET request_hash='wrong' WHERE operation_id='operation-0'",
                     "UPDATE operations SET response_version=2 WHERE operation_id='operation-0'",
                     "DELETE FROM outbox WHERE operation_id='operation-1'"]
        for mutation in mutations:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                target, _ = self.oracle_fixture(Path(directory))
                self.assertTrue(target.durable_oracle()['passed'])
                target.database.sql(mutation)
                with self.assertRaises(IntegrityFailure):
                    target.durable_oracle()

    def test_durable_oracle_rejects_corrupted_blob_behind_confirmed_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            target, blob = self.oracle_fixture(Path(directory))
            self.assertEqual(1, target.durable_oracle()['unique_blobs_verified'])
            blob.write_bytes(b'incomplete')
            with self.assertRaisesRegex(IntegrityFailure, 'incomplete content'):
                target.durable_oracle()


if __name__ == '__main__':
    unittest.main()
