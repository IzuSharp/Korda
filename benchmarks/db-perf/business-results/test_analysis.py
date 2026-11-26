#!/usr/bin/env python3
"""Finite identity/count rejection controls; synthetic identities grant no measurement credit."""
import copy
from pathlib import Path
import unittest

from analyze import validate_independent_group


class IndependentExecutionControls(unittest.TestCase):
    def test_copied_receipt_at_three_paths_is_rejected(self):
        identity = {"ProcessId": 1000, "StartedUtc": "2026-11-24T00:00:00+00:00", "ExecutableSha256": "a" * 64}
        records = [(Path(f"synthetic-copy-{number}.json"), copy.deepcopy(identity)) for number in range(3)]
        with self.assertRaisesRegex(ValueError, "Copied receipts"):
            validate_independent_group(records)

    def test_two_distinct_executions_do_not_fill_formal_cell(self):
        records = [(Path(f"synthetic-{number}.json"), {"ProcessId": 1000 + number,
                    "StartedUtc": "2026-11-24T00:00:00+00:00", "ExecutableSha256": "a" * 64}) for number in range(2)]
        with self.assertRaisesRegex(ValueError, "at least three"):
            validate_independent_group(records)

    def test_natural_pid_reuse_with_distinct_start_times_is_allowed(self):
        records = [(Path(f"synthetic-{number}.json"), {"ProcessId": 1000,
                    "StartedUtc": f"2026-11-24T00:00:0{number}+00:00", "ExecutableSha256": "a" * 64}) for number in range(3)]
        validate_independent_group(records)


if __name__ == "__main__":
    unittest.main()
