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
from campaign_build import request_record, validate_heavy_workload


class ControllerTests(unittest.TestCase):
    @staticmethod
    def clean_oracle():
        return {"oracleFailuresBefore": 0, "oracleFailuresAfter": 0,
                "oracleFailures": 0, "firstOracleFailure": None}

    @staticmethod
    def group_snapshot():
        return {"path": "/sys/fs/cgroup/runner/db-perf-01-pg-" + "a" * 24,
                "deviceInode": [10, 20], "controls": {"cpu.max": {"unavailableErrno": 2}},
                "serverIdentity": {"pid": 100, "startTicks": 10}, "membersVerified": True,
                "members": [{"pid": 100, "parentPid": 50, "startTicks": 10}], "exitedDuringMemberAcquisition": [],
                "cpu": {"usage_usec": 300000, "user_usec": 200000, "system_usec": 100000}}

    def test_task_cgroup_requires_complete_monotonic_cpu_and_identity(self):
        self.assertEqual(run.parse_cpu_group_stat("usage_usec 3\nuser_usec 2\nsystem_usec 1\n")["user_usec"], 2)
        for invalid in ("user_usec 2\nsystem_usec 1\n", "usage_usec -3\nuser_usec 2\nsystem_usec 1\n",
                        "usage_usec 3\nuser_usec 2\nsystem_usec 1\nuser_usec 3\n"):
            with self.assertRaises(ValueError):
                run.parse_cpu_group_stat(invalid)
        first = self.group_snapshot()
        last = copy.deepcopy(first)
        last["cpu"] = {"usage_usec": 900000, "user_usec": 600000, "system_usec": 300000}
        delta = run.server_cpu_group_delta(first, last)
        self.assertEqual(delta["userCpuSeconds"], 0.4)
        self.assertEqual(delta["systemCpuSeconds"], 0.2)
        for key, value in (("deviceInode", [10, 21]), ("membersVerified", False),
                           ("controls", {"cpu.max": "1 100000"}), ("serverIdentity", {"pid": 100, "startTicks": 11})):
            changed = copy.deepcopy(last); changed[key] = value
            with self.assertRaises(ValueError):
                run.server_cpu_group_delta(first, changed)
        for bad in (None, -1, 100000):
            changed = copy.deepcopy(last); changed["cpu"]["user_usec"] = bad
            with self.assertRaises(ValueError):
                run.server_cpu_group_delta(first, changed)
        first["controls"]["cpu.max"] = last["controls"]["cpu.max"] = "max 100000"
        with self.assertRaises(ValueError):
            run.server_cpu_group_delta(first, last)
        first["cpu"]["nr_throttled"] = 0; last["cpu"]["nr_throttled"] = 1
        self.assertEqual(run.server_cpu_group_delta(first, last)["taskGroupThrottleEvents"], 1)

    def test_task_cgroup_rejects_foreign_and_reused_process_identity(self):
        root = {"pid": 100, "startTicks": 10}
        identities = {100: dict(root, parentPid=50), 101: {"pid": 101, "parentPid": 100, "startTicks": 11},
                      999: {"pid": 999, "parentPid": 1, "startTicks": 1}}
        live, exited = run.validate_server_group_members(root, [100, 101, 102], identities.get)
        self.assertEqual([item["pid"] for item in live], [100, 101])
        self.assertEqual(exited, [102])
        with self.assertRaisesRegex(ValueError, "foreign"):
            run.validate_server_group_members(root, [999], identities.get)
        identities[100]["startTicks"] += 1
        with self.assertRaisesRegex(ValueError, "reused"):
            run.validate_server_group_members(root, [101], identities.get)

    def test_group_launcher_enters_before_exec_and_aborts_on_entry_failure(self):
        with tempfile.TemporaryDirectory(prefix="db-perf-launch fixture-") as temporary:
            directory = Path(temporary)
            group = directory / ("db-perf-01-pg-" + "b" * 24); group.mkdir()
            (group / "cgroup.procs").write_text("")
            fake_sudo = directory / "sudo"
            fake_sudo.write_text('#!/bin/sh\nset -eu\n[ "$1" = "-n" ]\nshift\nexec "$@"\n')
            fake_sudo.chmod(0o755)
            owner = run.PostgresCpuGroup(); owner.created = True; owner.descriptor = {"path": str(group)}
            marker = directory / "executed.json"
            child = [sys.executable, "-c", "import os,pathlib,sys; p=pathlib.Path(sys.argv[1]); assert int(p.read_text())==os.getpid(); pathlib.Path(sys.argv[2]).write_text('entered-before-exec')",
                     str(group / "cgroup.procs"), str(marker)]
            env = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ["PATH"])
            result = subprocess.run(owner.launch_command(child), env=env, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(marker.read_text(), "entered-before-exec")
            marker.unlink(); fake_sudo.write_text("#!/bin/sh\nexit 41\n")
            rejected = subprocess.run(owner.launch_command(child), env=env, capture_output=True, text=True, timeout=5)
            self.assertEqual(rejected.returncode, 41)
            self.assertFalse(marker.exists())

    def test_group_owner_only_creates_and_removes_its_fresh_child(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            node = {"path": str(parent), "cpu.max": "max 100000", "memory.max": "max",
                    "cpuset.cpus.effective": "0-3", "cgroup.subtree_control": ""}
            snapshot = {"available": True, "ancestors": [node]}
            calls = []
            owner = run.PostgresCpuGroup()
            def privileged(argv):
                calls.append(list(map(str, argv)))
                target = Path(argv[-1])
                self.assertEqual(target.parent, parent)
                self.assertTrue(target.name.startswith("db-perf-01-pg-"))
                if argv[0] == "mkdir":
                    target.mkdir()
                    (target / "cpu.stat").write_text("usage_usec 0\nuser_usec 0\nsystem_usec 0\n")
                    (target / "cgroup.procs").write_text("")
                else:
                    for file in target.iterdir(): file.unlink()
                    target.rmdir()
            with patch.object(run, "cgroup_snapshot", return_value=snapshot), patch.object(owner, "privileged", side_effect=privileged):
                owner.create(); owner.close()
            self.assertEqual([argv[0] for argv in calls], ["mkdir", "rmdir"])
            self.assertEqual(calls[0][-1], calls[1][-1])
            self.assertTrue(owner.record["parentControlsUnchanged"])
            self.assertTrue(owner.record["removed"])
            changed = copy.deepcopy(snapshot); changed["ancestors"][0]["cpu.max"] = "100000 100000"
            with patch.object(run, "cgroup_snapshot", return_value=changed):
                with self.assertRaisesRegex(ValueError, "Parent cgroup"):
                    owner.check_parent()

    def test_occupied_task_group_cannot_be_removed(self):
        owner = run.PostgresCpuGroup(); owner.created = True; owner.descriptor = {"path": "/unused"}
        with patch.object(run, "server_cpu_group_snapshot", side_effect=ValueError("remains populated")), patch.object(owner, "privileged") as mutate:
            with self.assertRaisesRegex(ValueError, "populated"):
                owner.close()
            mutate.assert_not_called()
            self.assertFalse(owner.record["removed"])

    def test_task_group_replacement_controls_and_populated_cleanup_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            path = parent / ("db-perf-01-pg-" + "c" * 24); path.mkdir()
            (path / "cpu.stat").write_text("usage_usec 0\nuser_usec 0\nsystem_usec 0\n")
            (path / "cgroup.procs").write_text("")
            info = path.stat()
            descriptor = {"path": str(path), "parentPath": str(parent), "deviceInode": [info.st_dev, info.st_ino],
                          "controls": run.cpu_group_controls(path)}
            self.assertEqual(run.server_cpu_group_snapshot(descriptor, empty=True)["members"], [])
            replaced = copy.deepcopy(descriptor); replaced["deviceInode"][1] += 1
            with self.assertRaisesRegex(ValueError, "identity changed"):
                run.server_cpu_group_snapshot(replaced, empty=True)
            (path / "cpu.weight").write_text("200\n")
            with self.assertRaisesRegex(ValueError, "controls"):
                run.server_cpu_group_snapshot(descriptor, empty=True)
            (path / "cpu.weight").unlink(); (path / "cgroup.procs").write_text("999\n")
            with self.assertRaisesRegex(ValueError, "populated"):
                run.server_cpu_group_snapshot(descriptor, empty=True)

    def test_owner_receipt_binds_empty_group_cpu_and_wait4_raw_counts(self):
        first = self.group_snapshot(); final = copy.deepcopy(first)
        final["members"] = []
        final["cpu"] = {"usage_usec": 900000, "user_usec": 600000, "system_usec": 300000}
        descriptor = {key: first[key] for key in ("path", "deviceInode", "controls", "serverIdentity")}
        group = {"required": True, "created": True, "removed": True, "parentControlsUnchanged": True,
                 "descriptor": descriptor, "beforeClients": first, "emptyAfterOwnerJoin": final,
                 "parentControlsBefore": [], "clientsThroughShutdownCpu": run.server_cpu_group_delta(first, final),
                 "wait4CrossCheck": {"groupLifetimeUserSeconds": 0.6, "groupLifetimeSystemSeconds": 0.3,
                                     "wait4ProcessLifetimeUserSeconds": 0.61, "wait4ProcessLifetimeSystemSeconds": 0.31}}
        record = {"exitCode": 0, "identity": first["serverIdentity"], "serverCpuGroup": group,
                  "processLifetimeUserCpuSeconds": 0.61, "processLifetimeSystemCpuSeconds": 0.31}
        self.assertEqual(campaign.server_owner_accounting(record)["groupControls"], descriptor["controls"])
        bad = copy.deepcopy(record); bad["serverCpuGroup"]["emptyAfterOwnerJoin"]["members"] = first["members"]
        with self.assertRaisesRegex(ValueError, "observable owners"):
            campaign.server_owner_accounting(bad)
        bad = copy.deepcopy(record); bad["serverCpuGroup"]["wait4CrossCheck"]["groupLifetimeUserSeconds"] = 0.5
        with self.assertRaisesRegex(ValueError, "cross-check"):
            campaign.server_owner_accounting(bad)
        bad = copy.deepcopy(record); bad["serverCpuGroup"]["removed"] = False
        with self.assertRaises(ValueError):
            campaign.server_owner_accounting(bad)
        bad = copy.deepcopy(record); bad["serverCpuGroup"]["clientsThroughShutdownCpu"]["userCpuSeconds"] = 0.1
        with self.assertRaisesRegex(ValueError, "raw cumulative"):
            campaign.server_owner_accounting(bad)

    def test_group_cpu_is_primary_with_exited_backend_cross_check_retained(self):
        with tempfile.TemporaryDirectory() as temporary:
            first, last = self.group_snapshot(), self.group_snapshot()
            last["cpu"] = {"usage_usec": 900000, "user_usec": 600000, "system_usec": 300000}
            totals = {"processCount": 1, "rssComplete": True, "pssComplete": True, "sampledRssBytes": 1000,
                      "sampledPssBytes": 900, "ownAndReapedUserTicks": 20, "ownAndReapedSystemTicks": 10}
            samples = []
            for index, group in enumerate((first, last)):
                samples.append({"monotonicNs": index * 1000000000, "acquiredThroughMonotonicNs": index * 1000000000 + 100,
                    "clientTotals": dict(totals, ownAndReapedUserTicks=20 + index),
                    "serverTotals": dict(totals, ownAndReapedUserTicks=20 - index), "serverCpuGroup": group,
                    "hostCpuTicks": {"user": 10 + index, "nice": 0, "system": 1, "idle": 100 + index * 100,
                                     "iowait": 0, "irq": 0, "softirq": 0, "steal": 0}, "guestQuota": {}})
            path = Path(temporary) / "series.jsonl"
            path.write_text("".join(json.dumps(item) + "\n" for item in samples))
            with patch.object(campaign, "quota_assessment", return_value={"quotaSignature": [], "localThrottleCounters": {}}):
                result = campaign.summarize_resources(path, {"steadyStartMonotonicNs": 0, "drainedMonotonicNs": 2000000000})
            self.assertEqual(result["server"]["userCpuSeconds"], 0.4)
            self.assertEqual(result["server"]["systemCpuSeconds"], 0.2)
            self.assertLess(result["server"]["procTreeUserCpuSeconds"], 0)
            self.assertTrue(result["server"]["counterMonotonic"])
            self.assertFalse(result["server"]["procTreeCounterMonotonic"])
            self.assertTrue(campaign.resource_quality(result, "postgresql", {"maximumStealFraction": 0.005})["passed"])

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
        return {**ControllerTests.clean_oracle(), "actualCaseIds": ["one"], "cases": [{"name": "one", "started": 4, "succeeded": 3, "failed": 1, "rows": 96}], "ownersReturnedToZero": True,
                "offeredLoad": {**ControllerTests.clean_oracle(), "planned": 5, "attempted": 5, "sent": 4, "started": 4, "completed": 4,
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

    def test_oracle_failures_cannot_be_diluted_by_the_error_budget(self):
        raw = self.offered()
        normalized = campaign.normalized_result(raw, ["one"], 5, 1)
        permissive_slo = {"maximumErrorAndRejectionRate": 0.5, "maximumP99Microseconds": 100000}
        self.assertTrue(campaign.meets_slo(normalized, permissive_slo))
        for where in ("result", "offered", "cleanup", "prior-phase"):
            value = copy.deepcopy(raw)
            target = value["offeredLoad"] if where == "offered" else value
            target.update(oracleFailures=1, firstOracleFailure={"phase": where})
            target["oracleFailuresAfter"] = 0 if where == "cleanup" else 1
            target["oracleFailuresBefore"] = 1 if where == "prior-phase" else 0
            with self.subTest(where=where), self.assertRaisesRegex(ValueError, "oracle violation"):
                campaign.normalized_result(value, ["one"], 5, 1)
        normalized["oracleFailures"] = 1
        self.assertFalse(campaign.meets_slo(normalized, permissive_slo))
        normalized["oracleFailures"] = 0
        del normalized["correctnessPassed"]
        self.assertFalse(campaign.meets_slo(normalized, permissive_slo))

    def test_missing_or_malformed_oracle_evidence_is_not_zero(self):
        for where in ("result", "offered"):
            for field in self.clean_oracle():
                value = self.offered()
                target = value["offeredLoad"] if where == "offered" else value
                del target[field]
                with self.subTest(where=where, missing=field), self.assertRaises(ValueError):
                    campaign.normalized_result(value, ["one"], 5, 1)
        for field in ("oracleFailuresBefore", "oracleFailuresAfter", "oracleFailures"):
            for invalid in (None, True, -1, 0.0):
                value = self.offered(); value[field] = invalid
                with self.subTest(field=field, value=invalid), self.assertRaises(ValueError):
                    campaign.normalized_result(value, ["one"], 5, 1)
        value = self.offered(); value["firstOracleFailure"] = {"phase": "warmup"}
        with self.assertRaises(ValueError): campaign.normalized_result(value, ["one"], 5, 1)

    def test_closed_loop_cannot_credit_an_early_exit_as_the_requested_duration(self):
        value = self.offered()
        offered = value.pop("offeredLoad")
        value.update(started=4, succeeded=3, failed=1, rows=96,
                     latencies={"histogram": offered["successServiceHistogram"]},
                     steadyStartMonotonicNs=1000000000)
        for duration, accepted in ((1, False), (14.94, False), (14.96, True), (15, True), (20, True)):
            value.update(effectiveSeconds=duration, steadyEndMonotonicNs=int((1 + duration) * 1000000000))
            if accepted:
                result = campaign.normalized_result(value, ["one"], 15)
                self.assertEqual(result["elapsedThroughDrainSeconds"], duration)
            else:
                with self.assertRaisesRegex(ValueError, "before the requested duration"):
                    campaign.normalized_result(value, ["one"], 15)

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

    def test_direct_namespace_read_does_not_launch_privileged_helper(self):
        with patch.object(run.os, "readlink", side_effect=["cgroup:[7]"] * 3), \
                patch.object(run.subprocess, "run") as helper:
            result = run.cgroup_namespace_observation()
        self.assertIs(result["sameAsInit"], True)
        self.assertEqual(result["method"], "direct_readlink")
        helper.assert_not_called()

    def test_restricted_namespace_read_requires_real_stable_hosted_identity(self):
        child = subprocess.CompletedProcess([], 0, "cgroup:[7]\ncgroup:[7]\n", "")
        denied = PermissionError(13, "PID 1 namespace read restricted")
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                patch.object(run.os, "readlink", side_effect=["cgroup:[7]", denied, "cgroup:[7]", "cgroup:[7]"]), \
                patch.object(run.subprocess, "run", return_value=child) as helper:
            result = run.cgroup_namespace_observation()
        self.assertIs(result["sameAsInit"], True)
        self.assertEqual(result["directError"]["errno"], 13)
        self.assertTrue(result["privilegedProbe"]["validIdentityRead"])
        self.assertGreaterEqual(result["privilegedProbe"]["elapsedNs"], 0)
        self.assertEqual(helper.call_args.args[0], ["/usr/bin/sudo", "-n", "/usr/bin/readlink", "--",
                         f"/proc/{os.getpid()}/ns/cgroup", "/proc/1/ns/cgroup"])
        self.assertEqual(helper.call_args.kwargs["timeout"], 5)
        self.assertNotIn("shell", helper.call_args.kwargs)

    def test_privileged_namespace_probe_fails_closed_for_changed_or_invalid_reads(self):
        cases = [(0, "cgroup:[7]\ncgroup:[8]\n", "", "cgroup:[7]"),
                 (0, "cgroup:[7]\ncgroup:[7]\n", "", "cgroup:[8]"),
                 (1, "cgroup:[7]\ncgroup:[7]\n", "", "cgroup:[7]"),
                 (0, "cgroup:[7]\ncgroup:[7]\nextra\n", "", "cgroup:[7]"),
                 (0, "cgroup:[7]\ninvalid\n", "", "cgroup:[7]"),
                 (0, "cgroup:[7]\ncgroup:[7]\n", "unexpected stderr", "cgroup:[7]")]
        for code, stdout, stderr, after in cases:
            with self.subTest(code=code, stdout=stdout, stderr=stderr, after=after), \
                    patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(run.os, "readlink", side_effect=["cgroup:[7]", PermissionError(13, "restricted"), "cgroup:[7]", after]), \
                    patch.object(run.subprocess, "run", return_value=subprocess.CompletedProcess([], code, stdout, stderr)):
                result = run.cgroup_namespace_observation()
                self.assertIsNot(result["sameAsInit"], True)
                self.assertEqual(result["privilegedProbe"]["stdout"], stdout)

    def test_namespace_probe_retains_timeout_and_does_not_escalate_other_errors(self):
        for environment, error in [({"GITHUB_ACTIONS": "false", "RUNNER_ENVIRONMENT": "github-hosted"}, PermissionError(13, "restricted")),
                                   ({"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "self-hosted"}, PermissionError(13, "restricted")),
                                   ({"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}, OSError(5, "IO error"))]:
            with patch.dict(os.environ, environment), patch.object(run.os, "readlink", side_effect=error), \
                    patch.object(run.subprocess, "run") as helper:
                result = run.cgroup_namespace_observation()
            self.assertIsNot(result["sameAsInit"], True)
            helper.assert_not_called()
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                patch.object(run.os, "readlink", side_effect=["cgroup:[7]", PermissionError(1, "restricted"), "cgroup:[7]"]), \
                patch.object(run.subprocess, "run", side_effect=subprocess.TimeoutExpired("readlink", 5)):
            result = run.cgroup_namespace_observation()
        self.assertIsNone(result["sameAsInit"])
        self.assertEqual(result["privilegedProbe"]["error"]["type"], "TimeoutExpired")
        self.assertEqual(result["directError"]["errno"], 1)

    def test_failed_quota_admission_preserves_unqualified_snapshot(self):
        settings = {"clientCpus": [0, 1], "serverCpus": [2], "observerCpus": [3], "minimumFreeDiskBytes": 1}
        snapshot = {"available": False, "namespaceError": 13, "ancestors": []}
        with tempfile.TemporaryDirectory() as tmp, \
                patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                patch.object(campaign.os, "sched_getaffinity", return_value={0, 1, 2, 3}), \
                patch.object(campaign.platform, "system", return_value="Linux"), \
                patch.object(campaign.platform, "machine", return_value="x86_64"), \
                patch.object(campaign, "cgroup_snapshot", return_value=snapshot), \
                patch.object(campaign, "environment_identity", return_value={"bootId": "test"}):
            with self.assertRaisesRegex(ValueError, "quota ancestry"):
                campaign.admission({"admission": settings}, Path(tmp))
            record = json.loads((Path(tmp) / "quota-observation.json").read_text())
            self.assertEqual(record["limits"], snapshot)
            self.assertIs(record["qualified"], False)
            self.assertEqual(record["longSegmentsCredited"], 0)
            self.assertFalse((Path(tmp) / "environment.json").exists())

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

    def test_heavy_context_preserves_primary_and_scopes_sqlite_driver_argument(self):
        value = object.__new__(campaign.Campaign)
        value.bundle = Path("/frozen-bundle")
        value.request = json.loads((HERE / "campaign-request.json").read_text())
        value.settings = value.request["measurement"]
        primary = json.loads((HERE / "workload/queries-v3.json").read_text())
        heavy = json.loads((HERE / "workload/large-stream-8192-v1.json").read_text())
        value.cases = value.request["workload"]["cases"]
        value.workload = primary
        value.workloads = {"primary": {"cases": value.cases, "manifest": primary, "path": value.bundle / "workload.json"},
                           "wide8192": {"cases": ["wide"], "manifest": heavy, "path": value.bundle / "heavy-workload.json", "warmupOperations": 8}}
        value.bundle_manifest = {"sqliteLibrary": "native/libsqlite3.so", "applications":
                                {f"{provider}/{route}": route for provider in ("sqlite", "postgresql") for route in campaign.ROUTES}}
        before = copy.deepcopy((value.cases, value.workload))
        for provider in ("sqlite", "postgresql"):
            argv = value.argv(provider, "independent_csharp_orm", 15, 4, None, Path("/out/result.json"), workload="wide8192")
            self.assertEqual("--native-library" in argv, provider == "sqlite")
            self.assertEqual(argv[argv.index("--rows") + 1], "8192")
            self.assertEqual(argv[argv.index("--cases") + 1], "wide")
        native = value.argv("postgresql", "direct_cpp", 15, 4, None, Path("/out/result.json"), workload="wide8192")
        self.assertEqual(native[native.index("--manifest") + 1], "/frozen-bundle/heavy-workload.json")
        short = value.request["shortQueries"]["cases"]
        for mode, physical in (("prepared", "reuse"), ("dynamic", "per-operation")):
            native = value.argv("postgresql", "direct_cpp", 15, 1, None, Path("/out/result.json"), construction_mode=mode, case_ids=short)
            self.assertEqual(native[native.index("--mode") + 1], mode)
            self.assertEqual(native[native.index("--physical-prepare") + 1], physical)
            self.assertEqual(native[native.index("--protocol-confirmation") + 1], "drain-only")
            self.assertEqual(native[native.index("--cases") + 1], ",".join(short))
            self.assertEqual(native[native.index("--manifest") + 1], "/frozen-bundle/workload.json")
        with self.assertRaisesRegex(ValueError, "Dynamic native"):
            value.argv("sqlite", "direct_cpp", 15, 1, None, Path("/out/result.json"),
                       native_contract={"physicalPrepare": "reuse", "protocolConfirmation": "drain-only"}, construction_mode="dynamic")
        with self.assertRaisesRegex(ValueError, "case subset"):
            value.cell_context("primary", case_ids=["not-in-manifest"])
        self.assertEqual((value.cases, value.workload), before)

    def test_heavy_contract_rejects_changed_sql_and_short_consumption(self):
        primary = json.loads((HERE / "workload/queries-v3.json").read_text())
        heavy = json.loads((HERE / "workload/large-stream-8192-v1.json").read_text())
        digest = run.sha(HERE / "workload/queries-v3.json")
        validate_heavy_workload(primary, heavy, digest)
        invalid = copy.deepcopy(heavy)
        invalid["queries"][0]["expectedRows"] = 3
        with self.assertRaisesRegex(ValueError, "consumption"):
            validate_heavy_workload(primary, invalid, digest)
        invalid = copy.deepcopy(heavy)
        invalid["queries"][0]["sql"]["sqlite"] += " LIMIT 3"
        with self.assertRaisesRegex(ValueError, "compiled query contract"):
            validate_heavy_workload(primary, invalid, digest)

    def test_provider_binding_is_exact_for_each_route(self):
        for provider, managed, independent in (("sqlite", "Sqlite", "Microsoft.Data.Sqlite.Core"), ("postgresql", "PostgreSql", "Npgsql")):
            campaign.validate_provider_identity({"Provider": independent}, provider, "independent_csharp_orm")
            campaign.validate_provider_identity({"Provider": managed}, provider, "korda_before")
            campaign.validate_provider_identity({"provider": provider}, provider, "direct_cpp")
            with self.assertRaisesRegex(ValueError, "exact route-specific driver"):
                campaign.validate_provider_identity({"Provider": managed}, provider, "independent_csharp_orm")

    def test_instrumentation_cannot_enter_a_timing_comparison(self):
        campaign.validate_uninstrumented_timing({})
        campaign.validate_uninstrumented_timing({"TimingComparisonEligible": True, "NativeAllocationProfile": None})
        for raw in ({"TimingComparisonEligible": False}, {"nativeAllocationProfile": {"observedAllocationCoverageComplete": True}}):
            with self.assertRaisesRegex(ValueError, "instrumented or timing-ineligible"):
                campaign.validate_uninstrumented_timing(raw)

    def test_short_subset_requires_exact_per_case_consumption(self):
        raw = {"caseCounts": {"one": {"succeeded": 3, "rows": 96}}}
        manifest = {"queries": [{"name": "one", "expectedRows": 32}]}
        campaign.validate_subset_consumption(raw, manifest, ["one"])
        raw["caseCounts"]["one"]["rows"] = 95
        with self.assertRaisesRegex(ValueError, "every expected row"):
            campaign.validate_subset_consumption(raw, manifest, ["one"])
        raw["caseCounts"].clear()
        with self.assertRaisesRegex(ValueError, "per-case consumption"):
            campaign.validate_subset_consumption(raw, manifest, ["one"])

    def test_capacity_refinement_preserves_failure_brackets_and_stops(self):
        def point(name, rate, passed):
            return {"name": name, "concurrency": 4, "rate": rate, "sloPassed": passed,
                    "rawPath": name + ".json", "rawSha256": "a" * 64}
        points = [point("low", 100, True), point("high", 125, False)]
        original = copy.deepcopy(points)
        next_point = campaign.capacity_refinement(points, 4, 0.05)
        self.assertTrue(next_point["execute"])
        self.assertEqual(next_point["proposedRate"], 112.5)
        self.assertEqual(next_point["lower"]["name"], "low")
        self.assertEqual(next_point["upper"]["name"], "high")
        self.assertEqual(points, original)
        points += [point("earlier-failure", 110, False), point("higher-success", 120, True)]
        final = campaign.capacity_refinement(points, 4, 0.05)
        self.assertFalse(final["execute"])
        self.assertTrue(final["nonMonotonicSloObserved"])
        self.assertEqual(len(final["selectionInputs"]), 4)
        unbounded = campaign.capacity_refinement([point("only-success", 100, True)], 4, 0.05)
        self.assertFalse(unbounded["execute"])
        self.assertEqual(unbounded["status"], "upper_failure_unobserved")

    def test_capacity_search_can_find_a_lower_scheduled_rate_without_retrying_failures(self):
        def point(name, rate, passed, concurrency=4):
            return {"name": name, "concurrency": concurrency, "rate": rate, "sloPassed": passed,
                    "rawPath": name + ".json", "rawSha256": "a" * 64}
        # The observed PostgreSQL before grid had no passing C4 point. A C8
        # success must not satisfy the frozen C4 comparison or prevent backoff.
        points = [point("grid-low", 427.032, False), point("grid-high", 640.548, False),
                  point("other-concurrency", 1065.88, True, 8)]
        original = copy.deepcopy(points)
        selection = campaign.capacity_refinement(points, 4, 0.05)
        self.assertEqual(selection["status"], "next_bounded_halving")
        self.assertTrue(selection["execute"])
        self.assertEqual(selection["proposedRate"], 213.516)
        self.assertIsNone(selection["lower"])
        self.assertEqual(selection["upper"]["name"], "grid-low")
        self.assertEqual(len(selection["selectionInputs"]), 2)
        self.assertEqual(points, original)
        points.append(point("backoff-0", selection["proposedRate"], False))
        selection = campaign.capacity_refinement(points, 4, 0.05)
        self.assertEqual(selection["proposedRate"], 106.758)
        points.append(point("backoff-1", selection["proposedRate"], True))
        selection = campaign.capacity_refinement(points, 4, 0.05)
        self.assertEqual(selection["status"], "next_bounded_midpoint")
        self.assertEqual(selection["proposedRate"], 160.137)
        self.assertEqual(selection["lower"]["name"], "backoff-1")
        self.assertEqual(selection["upper"]["name"], "backoff-0")
        self.assertEqual(len(selection["selectionInputs"]), 4)

    def test_capacity_backoff_stops_at_the_same_finite_iteration_and_rate_limits(self):
        def failed(rate):
            return {"name": str(rate), "concurrency": 4, "rate": rate, "sloPassed": False,
                    "rawPath": str(rate) + ".json", "rawSha256": "b" * 64}
        request = json.loads((HERE / "campaign-request.json").read_text())
        self.assertEqual(request["measurement"]["capacityRefinementSteps"], 3)
        points = [failed(427.032)]
        rates = []
        for _ in range(request["measurement"]["capacityRefinementSteps"]):
            selection = campaign.capacity_refinement(points, 4, 0.05)
            self.assertTrue(selection["execute"])
            rates.append(selection["proposedRate"])
            points.append(failed(selection["proposedRate"]))
        self.assertEqual(rates, [213.516, 106.758, 53.379])
        self.assertFalse(any(point["sloPassed"] for point in points))
        self.assertEqual(campaign.capacity_refinement([], 4, 0.05)["status"], "no_observed_rate")
        minimum = campaign.capacity_refinement([failed(0.001)], 4, 0.05)
        self.assertFalse(minimum["execute"])
        self.assertEqual(minimum["status"], "rate_resolution_or_existing_observation_limit")

    def test_resource_noise_cannot_become_a_capacity_failure_bracket(self):
        admission = {"maximumStealFraction": 0.005, "maximumIntervalStealFraction": 0.05}
        resources = {"quotaStableAndAvailable": True, "guestThrottleEvents": 0,
                     "hostCpu": {"stealFraction": 0.001}, "maximumIntervalStealFraction": 0.01,
                     "client": {"counterMonotonic": True}, "server": {"counterMonotonic": True,
                     "cpuAccounting": "task_owned_cgroup_v2", "taskGroupThrottleEvents": 0}}
        self.assertTrue(campaign.resource_quality(resources, "postgresql", admission)["passed"])
        altered = copy.deepcopy(resources)
        altered["guestThrottleEvents"] = 1
        self.assertFalse(campaign.resource_quality(altered, "postgresql", admission)["passed"])
        altered = copy.deepcopy(resources)
        altered["hostCpu"]["stealFraction"] = 0.1
        self.assertFalse(campaign.resource_quality(altered, "sqlite", admission)["passed"])
        altered = copy.deepcopy(resources)
        altered["server"]["counterMonotonic"] = False
        self.assertFalse(campaign.resource_quality(altered, "postgresql", admission)["passed"])
        self.assertTrue(campaign.resource_quality(altered, "sqlite", admission)["passed"])
        for key, value in (("cpuAccounting", "unavailable_task_group"), ("taskGroupThrottleEvents", 1)):
            altered = copy.deepcopy(resources); altered["server"][key] = value
            self.assertFalse(campaign.resource_quality(altered, "postgresql", admission)["passed"])
        for quality, should_retry in ((False, True), (True, False)):
            value = object.__new__(campaign.Campaign)
            value.state = {"completedSteps": {"sqlite/capacity": {"path": "fixture"}}}
            value.settings = {"maximumAttemptsPerStep": 2}
            value.output = Path("/unused")
            value.persist = lambda: None
            with patch.object(campaign, "verify_observation", return_value={"sloPassed": True, "resourceQualityPassed": quality}):
                if should_retry:
                    value.retry("sqlite/capacity", "Investigated and retained quota-throttle event")
                    self.assertEqual(len(value.state["reviewedRetries"]), 1)
                else:
                    with self.assertRaisesRegex(ValueError, "preferred sample"):
                        value.retry("sqlite/capacity", "Attempted unnecessary repeat")

    def test_duration_budget_keeps_all_eight_full_long_segments(self):
        request = json.loads((HERE / "campaign-request.json").read_text())
        budget = campaign.duration_budget(request)
        self.assertEqual(budget["maximumConfiguredPreflightSeconds"], 4324)
        self.assertEqual(budget["configuredLongAndIdleSeconds"], 14450)
        self.assertEqual(budget["configuredTotalSeconds"], 18774)
        self.assertEqual(budget["preflightOverheadAllowanceSeconds"], 776)
        self.assertEqual(budget["controllerOverheadAllowanceSeconds"], 1626)

    def test_recovery_cannot_hide_overload_business_failures_or_rebuild_pool(self):
        normal = self.offered()
        counts = normal["offeredLoad"]
        counts.update(sent=5, started=5, completed=5, succeeded=5, failed=0, rejected=0, rows=160)
        counts["seconds"] = [{"second": 0, "planned": 5, "sent": 5, "started": 5, "succeeded": 5, "failed": 0, "rejected": 0, "rows": 160}]
        for name in ("successScheduledHistogram", "successServiceHistogram", "allCompletedScheduledHistogram", "startDelayHistogram"):
            counts[name] = [0] * 8192
            counts[name][1280] = 5
        normal["cases"] = [{"name": "one", "started": 5, "succeeded": 5, "failed": 0, "rows": 160}]
        phases = [dict(copy.deepcopy(value), name=name, returnedToWarmOwnerBaseline=True)
                  for name, value in (("reference", normal), ("overload", self.offered()), ("recovery", normal))]
        warm = {"sources": 1, "connections": 1, "operations": 0, "cursors": 0, "buffers": 0, "owners": 2}
        for index, phase in enumerate(phases):
            phase.update(beforeOwners=dict(warm), afterOwners=dict(warm))
            for name in ("startedTicks", "deadlineTicks", "offeredEndTicks", "drainedTicks"):
                phase["offeredLoad"][name] += index * 6 * phase["offeredLoad"]["timerFrequency"]
        raw = {**self.clean_oracle(), "phases": phases, "actualCaseIds": ["one"], "sameSourceInstance": True,
               "sameWorkerAndSessionInstances": True, "poolRebuiltBetweenPhases": False,
               "concurrency": 1, "warmOwners": warm, "drained": dict.fromkeys(warm, 0),
               "trackedResourcesReturnedToZero": True, "passed": True}
        settings = {"seconds": 5, "overloadSeconds": 5, "overloadRate": 1}
        measurement = campaign.normalized_recovery(raw, ["one"], settings, 1)
        self.assertTrue(measurement["consumerPassed"])
        prior = dict(raw, oracleFailures=1, firstOracleFailure={"phase": "prior-command-phase"})
        with self.assertRaisesRegex(ValueError, "oracle violation"):
            campaign.normalized_recovery(prior, ["one"], settings, 1)
        self.assertEqual(measurement["counts"]["failed"], 1)
        self.assertFalse(campaign.recovery_meets_slo(measurement, {"maximumErrorAndRejectionRate": 0.001, "maximumP99Microseconds": 100000}))
        for snapshot in (raw["drained"], phases[1]["afterOwners"]):
            original = snapshot["connections"]
            snapshot["connections"] += 1
            with self.assertRaisesRegex(ValueError, "Raw .*owners"):
                campaign.normalized_recovery(raw, ["one"], settings, 1)
            snapshot["connections"] = original
        phases[2]["offeredLoad"]["startedTicks"] -= 6 * phases[2]["offeredLoad"]["timerFrequency"]
        phases[2]["offeredLoad"]["deadlineTicks"] -= 6 * phases[2]["offeredLoad"]["timerFrequency"]
        with self.assertRaisesRegex(ValueError, "out of order"):
            campaign.normalized_recovery(raw, ["one"], settings, 1)
        phases[2]["offeredLoad"]["startedTicks"] += 6 * phases[2]["offeredLoad"]["timerFrequency"]
        phases[2]["offeredLoad"]["deadlineTicks"] += 6 * phases[2]["offeredLoad"]["timerFrequency"]
        raw["poolRebuiltBetweenPhases"] = True
        with self.assertRaisesRegex(ValueError, "rebuilt"):
            campaign.normalized_recovery(raw, ["one"], settings, 1)

    def test_allocator_profile_requires_raw_coverage_and_conservation(self):
        fields = ("mallocCalls", "callocCalls", "reallocCalls", "alignedCalls", "freeCalls", "nullFrees",
                  "successfulAllocations", "failedAllocations", "deallocations", "allocatedRequestedBytes",
                  "releasedRequestedBytes", "allocatedUsableBytes", "releasedUsableBytes", "liveRequestedBytes", "liveUsableBytes",
                  "liveAllocations", "peakLiveAllocations", "peakRequestedBytes", "peakUsableBytes",
                  "unknownFrees", "unknownReallocs", "droppedAllocations", "duplicateAddresses", "initializationFailures")
        before = dict.fromkeys(fields, 0)
        before.update(version=1, tableCapacity=1048576, tableBytes=25165824)
        after = dict(before, mallocCalls=1, successfulAllocations=1, allocatedRequestedBytes=16,
                     allocatedUsableBytes=24, liveRequestedBytes=16, liveUsableBytes=24, liveAllocations=1,
                     peakLiveAllocations=1, peakRequestedBytes=16, peakUsableBytes=24)
        drained = dict(after, freeCalls=1, deallocations=1, releasedRequestedBytes=16, releasedUsableBytes=24,
                       liveRequestedBytes=0, liveUsableBytes=0, liveAllocations=0)
        profile = {"librarySha256": "a" * 64, "observedAllocationCoverageComplete": True, "requestedByteConservation": True,
                   "timingComparisonEligible": False, "fullAddressSpaceHeapPeakProven": False,
                   "trackedRequestedHeapPeakBytes": 16, "trackedUsableHeapPeakBytes": 24,
                   "before": before, "after": after, "afterDrain": drained, "allocationCalls": 1, "deallocationEvents": 0,
                   "allocatedRequestedBytes": 16, "releasedRequestedBytes": 0, "allocatedUsableBytes": 24, "releasedUsableBytes": 0}
        campaign.validate_native_profile(profile, "a" * 64, 1048576)
        broken = copy.deepcopy(profile)
        broken["after"]["unknownFrees"] = 1
        with self.assertRaisesRegex(ValueError, "coverage failures"):
            campaign.validate_native_profile(broken, "a" * 64, 1048576)
        broken = copy.deepcopy(profile)
        broken["afterDrain"]["liveRequestedBytes"] = 1
        with self.assertRaisesRegex(ValueError, "conservation"):
            campaign.validate_native_profile(broken, "a" * 64, 1048576)
        broken = copy.deepcopy(profile)
        broken["after"]["liveAllocations"] = 0
        with self.assertRaisesRegex(ValueError, "live allocation count"):
            campaign.validate_native_profile(broken, "a" * 64, 1048576)
        broken = copy.deepcopy(profile)
        broken["trackedRequestedHeapPeakBytes"] = 15
        with self.assertRaisesRegex(ValueError, "reported heap peak"):
            campaign.validate_native_profile(broken, "a" * 64, 1048576)


if __name__ == "__main__":
    unittest.main(verbosity=2)
