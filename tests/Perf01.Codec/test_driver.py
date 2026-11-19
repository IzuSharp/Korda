"""Finite accounting/negative controls; never product performance evidence."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from collections import Counter

spec = importlib.util.spec_from_file_location('codec_driver', Path(__file__).with_name('driver.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class ControllerContract(unittest.TestCase):
    def test_frozen_cycle_balances_every_dimension(self):
        rows = driver.cycle()
        self.assertEqual(len(rows), 160)
        self.assertEqual(Counter(x[0] // 2 for x in rows), {0: 80, 1: 40, 2: 32, 3: 8})
        self.assertEqual(Counter(x[0] % 2 for x in rows), {0: 80, 1: 80})
        self.assertEqual(Counter(x[1] for x in rows), {'c': 80, 'd': 80})
        self.assertEqual(Counter(x[2] for x in rows), {'a': 80, 'o': 80})
        self.assertEqual(rows, driver.cycle())

    def test_rejects_tampered_byte_count_and_timing(self):
        corpus = {'cases': [{'size': 256, 'encoded_bytes': 67}]}
        row = {'case': 0}
        event = {'status': 'ok', 'raw_bytes': 256, 'encoded_bytes': 67,
                 'call_ns': 10, 'consume_ns': 2, 'release_ns': 1, 'operation_ns': 13}
        driver.validate_completion(event, row, corpus, 'nativeweave')
        for key, value in [('raw_bytes', 255), ('encoded_bytes', 66), ('call_ns', -1),
                           ('operation_ns', 100), ('status', 'invented')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                driver.validate_completion({**event, key: value}, row, corpus, 'nativeweave')

    def test_late_completion_window_prevents_inflated_throughput(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            driver.dump(path / 'plan.json', {'source_commit': 'finite-test', 'route': 'direct-rust', 'comparison': 'accounting-only'})
            driver.dump(path / 'result.json', {'valid': True, 'effective_scheduled_load_seconds': 1,
                                             'measurement_observation_seconds': 2})
            row = {'phase': 'measurement', 'case': 0, 'action': 'c', 'interface': 'o',
                   'outcome': 'ok', 'scheduled_ns': 0, 'completion_ns': 2_000_000_000,
                   'target': {'call_ns': 1, 'consume_ns': 0, 'release_ns': 0, 'operation_ns': 1, 'raw_bytes': 256, 'encoded_bytes': 67,
                              'managed_allocated_bytes': None, 'copied_bytes_derived': 0}}
            (path / 'operations.jsonl').write_text(json.dumps(row) + '\n')
            (path / 'events.jsonl').write_text('')
            (path / 'resources.jsonl').write_text('')
            driver.summarize(path)
            summary = json.loads((path / 'summary.json').read_text())
            self.assertEqual(summary['successful_operations_per_second'], .5)
            metric = summary['groups']['c/o/case-0']
            self.assertIsNone(metric['arrival_ns']['p99'])
            self.assertIsNone(metric['managed_allocated_bytes'])


if __name__ == '__main__': unittest.main()
