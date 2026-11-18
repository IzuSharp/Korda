"""B23-specific integrity checks; A21 timing/statistics tests are not repeated."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/Benchmarks.Domain'
sys.path.insert(0, str(SOURCE))
from common import check_files, digest, expected_value, payload_hash

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

    def test_declared_unsupported_route_is_not_an_executed_comparison(self):
        route = {'id': 'managed-aot', 'supported': {'providers': ['sqlite'], 'protocols': ['http1'], 'kinds': ['create']}}
        self.assertIsNone(controller.unsupported(route, {'provider': 'sqlite', 'protocol': 'http1', 'kind': 'create'}))
        self.assertIn('postgresql', controller.unsupported(route, {'provider': 'postgresql', 'protocol': 'http1', 'kind': 'create'}))
        self.assertIn('resource/diagnostic', controller.unsupported(route, {'provider': 'sqlite', 'protocol': 'http1', 'kind': 'create', 'product_only': True}))


if __name__ == '__main__':
    unittest.main()
