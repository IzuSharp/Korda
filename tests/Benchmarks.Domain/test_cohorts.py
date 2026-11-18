"""Focused regressions for retained malformed observations and outer cleanup isolation."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[2] / 'src/Benchmarks.Domain'
sys.path.insert(0, str(SOURCE))
import cohorts
from report import workload_concurrency


class CohortIntegrity(unittest.TestCase):
    def test_invalid_timestamps_remain_partial_without_zero_durations(self):
        records = [None, {'admission_ns': None, 'completion_ns': None, 'scheduled_ns': 1},
                   {'admission_ns': 2, 'completion_ns': 5, 'scheduled_ns': 1}]
        value = workload_concurrency(records)
        self.assertEqual('partial', value['availability'])
        self.assertEqual(2, value['excluded_latency_records'])
        self.assertEqual(1, value['peak_admitted_logical_operations'])
        self.assertIsNone(value['first_half_mean_scheduled_arrival_ns'])
        self.assertEqual(4, value['last_half_mean_scheduled_arrival_ns'])
        self.assertEqual('unavailable', workload_concurrency(None)['availability'])

    def test_half_windows_follow_scheduled_arrivals_not_completion_order(self):
        value = workload_concurrency([
            {'admission_ns': 12, 'completion_ns': 40, 'scheduled_ns': 10},
            {'admission_ns': 1, 'completion_ns': 10, 'scheduled_ns': 0}])
        self.assertEqual(10, value['first_half_mean_scheduled_arrival_ns'])
        self.assertEqual(30, value['last_half_mean_scheduled_arrival_ns'])

    def test_inner_cleanup_blocker_prevents_next_search_invocation(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            state = {'inputs': {}, 'search': {}, 'matrix': {}, 'expected_cases': ['case'],
                'search_units': [{'id': name, 'profile': 'profile.json', 'select': 'case'} for name in ('first','second')]}
            blocker = {'path': str(output / 'search/first'), 'state': 'stopped-incomplete',
                'valid': False, 'execution_blocker': 'Owned process remains after cleanup'}
            with patch.object(cohorts, 'execute', return_value=blocker) as execute:
                cohorts.search(output, state)
            execute.assert_called_once()
            self.assertEqual('blocked-further-invocations', state['state'])
            self.assertEqual(['second'], state['not_run']['search_units'])
            self.assertEqual(['case'], state['not_run']['confirmation_cases'])

    def test_interrupted_existing_directory_cannot_authorize_more_load(self):
        with tempfile.TemporaryDirectory() as directory:
            value = cohorts.execute({'frozen_files': {}}, Path(directory), ['never-start'])
            self.assertFalse(value['valid'])
            self.assertIn('not established', value['execution_blocker'])

    def test_preflight_attempt_is_not_completed_measurement(self):
        with tempfile.TemporaryDirectory() as directory:
            state = {'selected_profile': 'unused.json', 'matrix_order': ['case'],
                'matrix': {'case': {'state': 'retained-adverse-calibration',
                    'calibration': {'state': 'preflight-failed', 'valid': False},
                    'confirmation': {'state': 'not-run'}}}}
            cohorts.matrix(Path(directory), state)
            self.assertEqual('incomplete-attempts-retained', state['state'])
            self.assertEqual(1, state['counts']['calibrations_attempted'])
            self.assertEqual(0, state['counts']['calibrations_completed'])


if __name__ == '__main__':
    unittest.main()
