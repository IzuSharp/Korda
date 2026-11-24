#!/usr/bin/env python3
"""Short controller failure/accounting checks; no benchmark execution or host admission."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import run
import campaign
from campaign_build import request_record


class ControllerTests(unittest.TestCase):
    def test_duplicate_command_and_output_escape_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            item = {"name": "one", "argv": [sys.executable, "-c", "pass"]}
            with self.assertRaises(ValueError):
                run.validate_plan({"provider": "sqlite", "commands": [item, item]}, output)
            with self.assertRaises(ValueError):
                run.validate_plan({"provider": "sqlite", "commands": [{"name": "escape", "argv": ["tool", "--output", "/outside.json"]}]}, output)
            with self.assertRaises(ValueError):
                run.validate_plan({"provider": "sqlite", "commands": [dict(item, timeoutSeconds=float("nan"))]}, output)
            shared = [dict(item, name=name, argv=["tool", "--output", str(output / "same.json")]) for name in ("one", "two")]
            with self.assertRaises(ValueError):
                run.validate_plan({"provider": "sqlite", "commands": shared}, output)
            with self.assertRaises(ValueError):
                run.validate_plan({"provider": "sqlite", "commands": [dict(item, argv=["tool", "--output", str(output / "plan.json")])]}, output)

    def test_nonzero_exit_retains_os_usage_and_error_status(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            result = run.execute({"name": "failure", "argv": [sys.executable, "-c", "print('fixture failure');raise SystemExit(7)"]}, "", None, output, 0.05, check=False)
            self.assertEqual(result["exitCode"], 7)
            self.assertFalse(result["success"])
            self.assertIsNotNone(result["processLifetimeUserCpuSeconds"])
            self.assertTrue((output / "failure.command.json").is_file())
            self.assertIn("fixture failure", (output / "failure.stdout.log").read_text())
            with self.assertRaises(ValueError):
                run.execute({"name": "failure", "argv": [sys.executable, "-c", "pass"]}, "", None, output, 0.05)

    def test_launch_failure_retains_receipt(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            result = run.execute({"name": "missing", "argv": [str(output / "no-such-binary")]}, "", None, output, 0.05, check=False)
            self.assertIsNone(result["exitCode"])
            self.assertEqual(result["observerErrors"][0]["type"], "FileNotFoundError")
            self.assertTrue((output / "missing.command.json").exists())

    def test_timeout_joins_child_and_preserves_last_sample(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = run.execute({"name": "timeout", "argv": [sys.executable, "-c", "import time;time.sleep(30)"], "timeoutSeconds": 0.08}, "", None, Path(tmp), 0.05, check=False)
            self.assertTrue(result["timedOut"])
            self.assertFalse(result["success"])
            self.assertIn("lastSample", result)
            self.assertIsNotNone(result["exitCode"])
            with self.assertRaises(ChildProcessError):
                os.waitpid(result["clientIdentity"]["pid"], os.WNOHANG)

    def test_observer_exception_still_joins_and_writes_receipt(self):
        with tempfile.TemporaryDirectory() as tmp:
            with patch.object(run, "sample", side_effect=OSError("controlled observer error")):
                result = run.execute({"name": "observer", "argv": [sys.executable, "-c", "import time;time.sleep(30)"]}, "", None, Path(tmp), 0.05, check=False)
            self.assertEqual(result["observerErrors"][0]["message"], "controlled observer error")
            self.assertIsNotNone(result["exitCode"])
            self.assertTrue((Path(tmp) / "observer.command.json").exists())

    def test_process_identity_and_unavailable_memory_are_explicit(self):
        reading = run.process(os.getpid())
        self.assertGreater(reading["startTicks"], 0)
        self.assertEqual(reading["pid"], os.getpid())
        self.assertIn("state", reading)
        self.assertIn("memoryErrors", reading)
        incomplete = run.tree_totals([dict(reading, memoryBytes={})])
        self.assertFalse(incomplete["rssComplete"])
        self.assertFalse(incomplete["pssComplete"])
        self.assertEqual(incomplete["memoryAvailableProcessCount"], 0)

    @staticmethod
    def offered():
        success, all_completed = [0] * 8192, [0] * 8192
        success[1280] = 3
        all_completed[1280] = 4
        return {"actualCaseIds": ["one"], "cases": [{"name": "one", "started": 4, "succeeded": 3, "failed": 1, "rows": 96}], "ownersReturnedToZero": True,
                "offeredLoad": {"planned": 5, "attempted": 5, "sent": 4, "started": 4, "completed": 4,
                    "succeeded": 3, "failed": 1, "rejected": 1, "retries": 0, "rows": 96,
                    "timerFrequency": 1000000000, "startedTicks": 1000000000, "deadlineTicks": 6000000000,
                    "offeredEndTicks": 6000000000, "drainedTicks": 6100000000, "successScheduledHistogram": success,
                    "successServiceHistogram": success.copy(), "startDelayHistogram": all_completed.copy(),
                    "allCompletedScheduledHistogram": all_completed, "timeSeriesTruncated": False,
                    "seconds": [{"second": 0, "planned": 5, "sent": 4, "started": 4, "succeeded": 3, "failed": 1, "rejected": 1, "rows": 96}]}}

    def test_failed_and_rejected_arrivals_do_not_disappear(self):
        value = campaign.normalized_result(self.offered(), ["one"], 5, 1)
        self.assertEqual(value["counts"]["completed"], 4)
        self.assertEqual(value["counts"]["succeeded"], 3)
        self.assertEqual(value["counts"]["rejected"], 1)
        self.assertEqual(value["errorAndRejectionRate"], 0.4)
        self.assertFalse(campaign.meets_slo(value, {"maximumErrorAndRejectionRate": 0.1, "maximumP99Microseconds": 100000}))

    def test_missing_arrivals_or_unexecuted_cases_are_rejected(self):
        value = self.offered()
        value["offeredLoad"]["attempted"] = 4
        with self.assertRaises(ValueError):
            campaign.normalized_result(value, ["one"], 5, 1)
        value = self.offered()
        value["cases"][0]["started"] = 0
        with self.assertRaises(ValueError):
            campaign.normalized_result(value, ["one"], 5, 1)

    def test_per_case_and_time_series_cannot_hide_global_mismatches(self):
        value = self.offered()
        value["cases"][0]["rows"] = 95
        with self.assertRaises(ValueError):
            campaign.normalized_result(value, ["one"], 5, 1)
        value = self.offered()
        value["offeredLoad"]["seconds"][0]["rejected"] = 0
        with self.assertRaises(ValueError):
            campaign.normalized_result(value, ["one"], 5, 1)

    def test_overload_missing_case_coverage_is_retained_but_cannot_qualify(self):
        value = self.offered()
        value["actualCaseIds"].append("rejected-only-case")
        observed = campaign.normalized_result(value, value["actualCaseIds"], 5, 1, require_coverage=False)
        self.assertFalse(observed["fullCaseCoverage"])
        self.assertEqual(observed["missingExecutedCaseIds"], ["rejected-only-case"])
        with self.assertRaises(ValueError):
            campaign.normalized_result(value, value["actualCaseIds"], 5, 1)

    def test_failed_admission_cannot_be_bypassed_on_resume(self):
        record = {"identity": {"bootId": "fixture"}, "idleAccepted": False,
                  "idle": {"stealFraction": 0.8, "busyFraction": 0.8}}
        request = {"admission": {"maximumStealFraction": 0.01, "maximumIdleBusyFraction": 0.1}}
        with self.assertRaisesRegex(ValueError, "failed idle admission"):
            campaign.validate_admission(record, request, record["identity"])
        record["idleAccepted"] = True
        with self.assertRaisesRegex(ValueError, "failed idle admission"):
            campaign.validate_admission(record, request, record["identity"])

    def test_root_quota_absence_and_limited_ancestor_are_distinguished(self):
        counters = "usage_usec 12\nuser_usec 8\nsystem_usec 4"
        root = {"path": "/sys/fs/cgroup", "root": True, "quotaStatus": "root_has_no_cpu_max",
                "cpu.max": {"unavailableErrno": 2}, "cpu.stat": counters}
        child = {"path": "/sys/fs/cgroup/runner", "root": False, "quotaStatus": "read",
                 "cpu.max": "100000 100000", "cpu.stat": counters + "\nnr_throttled 7"}
        snapshot = {"available": True, "mountRoot": "/", "sameCgroupNamespaceAsInit": True,
                    "hostInit": "systemd", "ancestors": [child, root]}
        assessment = campaign.quota_assessment(snapshot)
        self.assertEqual(assessment["maximumCpuCapacity"], 1)
        self.assertEqual(assessment["localThrottleCounters"], {child["path"]: 7})
        snapshot["ancestors"] = [root]
        self.assertIsNone(campaign.quota_assessment(snapshot)["maximumCpuCapacity"])
        snapshot["mountRoot"] = "/hidden-ancestor"
        with self.assertRaisesRegex(ValueError, "quota ancestry"):
            campaign.quota_assessment(snapshot)

    def test_resume_verifies_bound_raw_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            files = {}
            for name in ("result.json", "client.command.json", "client.resources.jsonl", "runner.json", "plan.json"):
                (root / name).write_text("fixture\n")
                files[name] = run.sha(root / name)
            value = {"rawPath": "result.json", "rawSha256": files["result.json"], "evidenceFiles": files}
            run.write_json(root / "normalized.json", value)
            stored = {"path": "normalized.json", "sha256": run.sha(root / "normalized.json")}
            self.assertEqual(campaign.verify_observation(root, stored), value)
            (root / "client.resources.jsonl").write_text("altered\n")
            with self.assertRaisesRegex(ValueError, "Bound checkpoint evidence changed"):
                campaign.verify_observation(root, stored)
    def test_disabled_request_cannot_start_preparation(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "disabled.json"
            path.write_text(json.dumps({"schemaVersion": 1, "task": "DB-Perf-01", "enabled": False}))
            self.assertFalse(request_record(path, enabled=False)["enabled"])
            with self.assertRaises(ValueError):
                request_record(path)

    def test_primary_native_contract_cannot_hide_framework_protocol_cost(self):
        request = json.loads((HERE / "campaign-request.json").read_text())
        request.update(enabled=True, harnessCommit="a" * 40)
        request["after"].update(sourceCommit="b" * 40, runId=1)
        request["workload"]["sha256"] = "c" * 64
        for package in request["after"]["providers"].values():
            package.update(version="fixture", artifactId=1, artifactZipSha256="d" * 64,
                           nupkgSha256="e" * 64, nativeSha256="f" * 64)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "request.json"
            run.write_json(path, request)
            self.assertTrue(request_record(path)["enabled"])
            request["nativeBaseline"]["protocolConfirmation"] = "product"
            run.write_json(path, request)
            with self.assertRaisesRegex(ValueError, "Primary native column"):
                request_record(path)


if __name__ == "__main__":
    unittest.main(verbosity=2)
