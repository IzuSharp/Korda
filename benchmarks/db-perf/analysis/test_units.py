#!/usr/bin/env python3
"""Finite offline CI checks; synthetic counters never count as measurements."""
from __future__ import annotations

import copy
import gzip
import hashlib
from pathlib import Path
import tempfile
import unittest

from campaign_admission import selection
from diagnostic_metrics import allocation_profile
from raw_metrics import histogram, oracle
from strict_io import EvidenceError, Resolver, field, natural, strict_json


def profile_fixture():
    """Small synthetic allocator ledger with independently chosen exact totals."""
    zero = dict(mallocCalls=0, callocCalls=0, reallocCalls=0, alignedCalls=0,
                freeCalls=0, nullFrees=0, failedAllocations=0,
                unknownFrees=0, unknownReallocs=0, droppedAllocations=0,
                duplicateAddresses=0, initializationFailures=0)

    def snapshot(allocations, frees, allocated, released, usable, usable_released,
                 peak_count, peak_requested, peak_usable):
        return dict(zero, version=1, tableCapacity=256, tableBytes=6144,
                    mallocCalls=allocations, freeCalls=frees,
                    successfulAllocations=allocations, deallocations=frees,
                    allocatedRequestedBytes=allocated, releasedRequestedBytes=released,
                    allocatedUsableBytes=usable, releasedUsableBytes=usable_released,
                    liveAllocations=allocations-frees, liveRequestedBytes=allocated-released,
                    liveUsableBytes=usable-usable_released, peakLiveAllocations=peak_count,
                    peakRequestedBytes=peak_requested, peakUsableBytes=peak_usable)

    return dict(librarySha256='1' * 64, timingComparisonEligible=False,
                fullAddressSpaceHeapPeakProven=False, observedAllocationCoverageComplete=True,
                requestedByteConservation=True,
                before=snapshot(10, 6, 100, 60, 128, 80, 4, 40, 48),
                after=snapshot(16, 10, 180, 100, 224, 128, 6, 80, 96),
                afterDrain=snapshot(17, 15, 200, 180, 256, 232, 7, 90, 112),
                allocationCalls=6, deallocationEvents=4, allocatedRequestedBytes=80,
                releasedRequestedBytes=40, allocatedUsableBytes=96, releasedUsableBytes=48,
                trackedRequestedHeapPeakBytes=80, trackedUsableHeapPeakBytes=96)


class OfflineEvidenceTests(unittest.TestCase):
    def test_json_cannot_hide_duplicate_or_nonfinite_values(self):
        for text in ('{"count":1,"count":2}', '{"count":NaN}',
                     '{"count":Infinity}', '{"count":1e309}'):
            with self.subTest(text=text), self.assertRaises(EvidenceError):
                strict_json(text)
        self.assertEqual(strict_json('{"count":9007199254740993}')['count'], 9007199254740993)

    def test_counter_types_and_field_aliases_are_unambiguous(self):
        for value in (True, -1, 0.0, '0'):
            with self.subTest(value=value), self.assertRaises(EvidenceError):
                natural(value, 'counter')
        with self.assertRaises(EvidenceError):
            field({'AllocatedBytes': 1, 'allocated_bytes': 2}, 'allocatedBytes')
        self.assertEqual(field({'allocated_bytes': 3}, 'AllocatedBytes'), 3)

    def test_histogram_population_and_golden_upper_bounds(self):
        bins = [0] * 8192
        bins[1280], bins[1407], bins[1535] = 50, 45, 5
        self.assertEqual(histogram(bins, 100, 'synthetic'),
                         dict(samples=100, p50_us_upper=1.032, p95_us_upper=2.048, p99_us_upper=4.096))
        with self.assertRaises(EvidenceError):
            histogram(bins, 99, 'synthetic')
        bins[0] = -1
        with self.assertRaises(EvidenceError):
            histogram(bins, 99, 'synthetic')

    def test_sticky_oracle_failure_cannot_be_hidden_by_a_quiet_interval(self):
        oracle(dict(oracleFailures=0, oracleFailuresBefore=0, oracleFailuresAfter=0,
                    firstOracleFailure=None))
        with self.assertRaises(EvidenceError):
            oracle(dict(oracleFailures=1, oracleFailuresBefore=1, oracleFailuresAfter=1,
                        firstOracleFailure={'phase': 'warmup'}))

    def test_gzip_must_be_one_complete_hash_bound_member(self):
        raw = b'{"count":17}\n'
        with tempfile.TemporaryDirectory(prefix='db-perf-evidence-unit-') as directory:
            path = Path(directory) / 'raw.json.gz'
            reference = dict(path=path.name, encoding='gzip', decoded_bytes=len(raw),
                             decoded_sha256=hashlib.sha256(raw).hexdigest())
            for suffix in (b'', b'trailing', gzip.compress(b'another member', mtime=0)):
                stored = gzip.compress(raw, mtime=0) + suffix
                path.write_bytes(stored)
                bound = dict(reference, bytes=len(stored), sha256=hashlib.sha256(stored).hexdigest())
                if suffix:
                    with self.assertRaises(EvidenceError):
                        Resolver(directory).resolve(bound)
                else:
                    self.assertEqual(Resolver(directory).resolve(bound).data, raw)

    def test_native_allocator_snapshot_and_interval_conservation(self):
        profile = profile_fixture()
        result = allocation_profile(profile, '1' * 64, 256)
        self.assertEqual(result['deltas']['operations']['allocatedRequestedBytes'], 80)
        self.assertEqual(result['deltas']['operations']['releasedRequestedBytes'], 40)
        self.assertEqual(result['snapshots']['afterDrain']['liveRequestedBytes'], 20)
        self.assertFalse(result['full_address_space_heap_peak_proven'])

    def test_common_native_live_offset_is_not_a_valid_absolute_snapshot(self):
        profile = profile_fixture()
        allocation_profile(profile, '1' * 64, 256)
        for label in ('before', 'after', 'afterDrain'):
            for unit in ('Requested', 'Usable'):
                profile[label]['live' + unit + 'Bytes'] += 1
                profile[label]['peak' + unit + 'Bytes'] += 1
        for unit in ('Requested', 'Usable'):
            profile['tracked' + unit + 'HeapPeakBytes'] += 1
        with self.assertRaises(EvidenceError):
            allocation_profile(profile, '1' * 64, 256)

    def test_allocator_coverage_failure_is_not_repaired_by_pass_booleans(self):
        profile = profile_fixture()
        allocation_profile(profile, '1' * 64, 256)
        profile['after']['unknownFrees'] = 1
        profile['passed'] = True
        with self.assertRaises(EvidenceError):
            allocation_profile(profile, '1' * 64, 256)

    def test_finite_rate_search_preserves_failures_and_brackets(self):
        def point(name, rate, passed):
            return dict(name=name, rate=rate, concurrency=4, sloPassed=passed,
                        rawPath='synthetic/' + name, rawSha256='2' * 64)
        failed = [point('five', 5.0, False), point('ten', 10.0, False)]
        lower = selection(failed, 4, 0.1)
        self.assertEqual((lower['status'], lower['proposedRate']), ('next_bounded_halving', 2.5))
        self.assertEqual(len(lower['selectionInputs']), 2)
        bracket = selection([point('low', 5.0, True), point('high', 10.0, False)], 4, 0.1)
        self.assertEqual((bracket['status'], bracket['proposedRate']), ('next_bounded_midpoint', 7.5))
        nonmonotonic = selection([point('low', 4.0, False), point('middle', 5.0, True),
                                 point('high', 6.0, False)], 4, 0.1)
        self.assertTrue(nonmonotonic['nonMonotonicSloObserved'])


if __name__ == '__main__':
    unittest.main()
