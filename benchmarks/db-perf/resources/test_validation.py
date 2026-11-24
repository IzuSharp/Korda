#!/usr/bin/env python3
"""Synthetic admission controls; these records grant no measurement credit."""
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("resource_inventory_validator", Path(__file__).with_name("run.py"))
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)
PACKAGE = {"source_commit": "1" * 40, "package_sha256": "2" * 64, "native_sha256": "3" * 64}
BUILD = {"executableSha256": "4" * 64}


def count(value=100):
    return {"AllocatedBytes": value, "Gen0": 0, "Gen1": 0, "Gen2": 0, "Timestamp": value}


def synthetic():
    def window(name, operations=1, rows=0, checksum=0, *, burst=False, failed=0):
        pending = [True] * operations if burst else []
        return {"Name": name, "Start": count(), "End": count(107), "RawAllocatedBytes": 7,
                "FrameworkAllocatedBytes": None, "Operations": operations, "PlannedOperations": operations,
                "Succeeded": operations - failed, "Failed": failed, "Rows": rows, "Checksum": checksum,
                "BoundaryError": None, "PendingAtReturn": pending.copy(), "PendingBeforeConsumption": pending.copy(),
                "PendingAtReturnCount": len(pending), "PendingFirstMovesBeforeAnyConsumption": len(pending),
                "SingleIssuingThread": burst, "IssuingThread": 7 if burst else 0,
                "RuntimePoolCacheCapacityForIssuingThread": 5 if burst else 0,
                "UnconsumedSuspensionsExceedCacheCapacity": len(pending) > 5}
    windows = [window("cold-source-build"), window("cold-open-sessions", 16),
               window("cold-fluent-prepare-and-query-dispose"),
               window("cold-raw-suspended-burst", 16, 16, 16 * 200000 * 200001 // 2, burst=True)]
    for repeat in range(3):
        windows += [window("warm-raw-suspended-burst-" + str(repeat), 16, 16, 16 * 200000 * 200001 // 2, burst=True),
                    window("prepared-fluent-c1-" + str(repeat), 1, 8, 36, burst=True),
                    window("prepared-fluent-concurrent-" + str(repeat), 16, 128, 576, burst=True)]
    windows += [window("pool-exhaustion-and-cancel", 3, failed=3), window("dispose-prepared"),
                window("return-all-sessions", 16), window("close-source-including-public-cleanup-observation")]
    warm = [1, 16, 0, 0, 0, 19, 0, 0]
    phases = ["initial-open-session-baseline"] + [w["Name"] for w in windows if w["PendingAtReturn"]]
    phases += ["after-exhaustion", "final-source-close"]
    settlements = [{"Phase": phase, "Immediate": warm.copy(),
                    "Settled": [0] * 8 if phase == "final-source-close" else warm.copy(),
                    "Expected": None if phase == "initial-open-session-baseline" else
                        [0] * 8 if phase == "final-source-close" else warm.copy(),
                    "Start": count(200), "End": count(201), "Polls": 2, "Matched": True} for phase in phases]
    memory = [{"Phase": phase, "BeforeForcedGc": count(300), "AfterForcedGc": count(301),
               "ManagedLiveBytesAfterForcedGc": 100, "RssBytes": 1000, "PeakRssBytes": 2000}
              for phase in ("before-source", "sessions-open-before-first-stream", "after-cold-burst",
                            "after-warm-concurrent-bursts", "after-exhaustion", "idle-start", "idle-end",
                            "after-source-close", "after-close-idle")]
    return {"Schema": "DB-Perf-01.public-resource-inventory.v1", "Passed": True, "Error": None,
            "CleanupErrors": [], "NativeAot": True, "ProcessId": 42, "SourceCommit": PACKAGE["source_commit"],
            "PackageSha256": PACKAGE["package_sha256"], "ExecutableSha256": BUILD["executableSha256"],
            "NativeSha256": PACKAGE["native_sha256"], "Provider": "Sqlite", "ProcessorCount": 4,
            "ProcessorCountOverride": "4", "Concurrency": 16, "SuspendedQuerySteps": 200000,
            "Windows": windows, "WarmOwners": warm, "OwnerSettlements": settlements,
            "NativeObservations": [{"Phase": w["Name"], "Counters": warm.copy()} for w in windows if w["PendingAtReturn"]],
            "SourceReleased": True, "FinalOwners": [0] * 8, "MatchingCanceledOpenResults": 1,
            "ResourceLimitOpenResults": 2, "PendingCancellationObserved": True, "PendingOpensBeforeCancellation": 1,
            "IdleElapsedSeconds": 1.01, "IdleUserSeconds": 0.01, "IdleSystemSeconds": 0.01, "Memory": memory}


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        self.raw = synthetic()

    def validate(self):
        return V.validate(self.raw, "sqlite", PACKAGE, BUILD, 16, 4)

    def test_complete_synthetic_record(self):
        result = self.validate()
        self.assertTrue(result["qualified"])
        self.assertIsNone(result["frameworkAllocatedBytes"])
        self.assertFalse(result["fullFrameworkZeroAllocation"])

    def test_one_disposal_failure_rejects_whole_result(self):
        w = self.raw["Windows"][3]
        w.update(Succeeded=15, Failed=1, BoundaryError="disposal failed after row validation")
        with self.assertRaises(ValueError): self.validate()

    def test_raw_integer_bytes_cannot_round_to_zero(self):
        self.raw["Windows"][3]["RawAllocatedBytes"] = 0
        with self.assertRaises(ValueError): self.validate()

    def test_zero_collection_count_does_not_prove_zero_bytes(self):
        self.assertEqual(self.validate()["windowTotals"]["cold-raw-suspended-burst"]["rawAllocatedBytes"], 7)

    def test_requested_concurrency_does_not_prove_cache_miss(self):
        w = self.raw["Windows"][3]
        w.update(PendingAtReturn=[True] * 5 + [False] * 11, PendingBeforeConsumption=[False] * 16,
                 PendingAtReturnCount=5, PendingFirstMovesBeforeAnyConsumption=0,
                 UnconsumedSuspensionsExceedCacheCapacity=False)
        self.assertFalse(self.validate()["qualified"])

    def test_completed_value_task_cannot_become_pending(self):
        w = self.raw["Windows"][3]
        w["PendingAtReturn"][0] = False
        w["PendingAtReturnCount"] -= 1
        with self.assertRaises(ValueError): self.validate()

    def test_immediate_and_settled_owners_remain_distinct(self):
        self.raw["OwnerSettlements"][-1]["Immediate"][2] = 1
        self.assertTrue(self.validate()["qualified"])
        self.raw["OwnerSettlements"][-1]["Settled"][2] = 1
        with self.assertRaises(ValueError): self.validate()

    def test_inconsistent_immediate_owner_record_is_rejected(self):
        self.raw["NativeObservations"][0]["Counters"][2] = 1
        with self.assertRaises(ValueError): self.validate()

    def test_transient_initial_baseline_is_rejected(self):
        self.raw["WarmOwners"][2] = 1
        with self.assertRaises(ValueError): self.validate()

    def test_no_pending_cancellation_is_unqualified(self):
        self.raw.update(MatchingCanceledOpenResults=0, ResourceLimitOpenResults=3,
                        PendingOpensBeforeCancellation=0, PendingCancellationObserved=False)
        self.assertFalse(self.validate()["qualified"])

    def test_wrong_runtime_capacity_is_rejected(self):
        self.raw["ProcessorCount"] = 16
        with self.assertRaises(ValueError): self.validate()

    def test_wrong_loaded_native_is_rejected(self):
        self.raw["NativeSha256"] = "5" * 64
        with self.assertRaises(ValueError): self.validate()

    def test_missing_cleanup_window_is_rejected(self):
        self.raw["Windows"].pop()
        with self.assertRaises(ValueError): self.validate()

    def test_decreasing_os_peak_is_disclosed_not_promoted(self):
        self.raw["Memory"][-1]["PeakRssBytes"] = 1500
        result = self.validate()
        self.assertTrue(result["qualified"])
        self.assertTrue(result["osReportedPeakRssIsNonMonotonic"])


if __name__ == "__main__":
    unittest.main()
