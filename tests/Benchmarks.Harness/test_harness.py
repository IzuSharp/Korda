"""Behavioral and corruption tests for A21, independent of unfinished engines."""
import asyncio
import base64
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bench", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


def counters():
    return {name: bench.unknown(unit, "not instrumented in protocol fixture") for name, unit in bench.COUNTERS.items()}


class AccountingTests(unittest.TestCase):
    def test_finite_omission_counterexample(self):
        evidence = bench.omission_counterexample()
        self.assertEqual(1, evidence["service_p99"])
        self.assertEqual(100, evidence["arrival_p99"])
        self.assertEqual(100, len(evidence["completed"]))

    def test_histogram_preserves_overflow_and_rejects_double_correction(self):
        result = bench.histogram([0, 10, 20, 1001], 10, 1000)
        self.assertEqual(4, result["count"])
        self.assertEqual([1001], result["overflow_ns"])
        self.assertEqual(1, result["bins"]["0"])
        self.assertIsNone(result["percentiles_from_exact_samples_ns"]["p99"])
        with self.assertRaisesRegex(ValueError, "double-correct"):
            bench.histogram([100], 1, 1000, correction="expected-interval")
        with self.assertRaises(ValueError):
            bench.histogram([-1], 1, 1000)

    def test_unknown_counter_is_not_zero(self):
        values = counters()
        bench.validate_counters(values)
        values["copied_bytes"]["value"] = 0
        with self.assertRaisesRegex(ValueError, "not zero"):
            bench.validate_counters(values)

    def test_complete_counter_inventory(self):
        values = counters()
        values.pop("native_allocations")
        with self.assertRaisesRegex(ValueError, "exactly"):
            bench.validate_counters(values)

    def test_malformed_counter_and_event_shapes(self):
        for bad in [None, [], 4, "counter"]:
            with self.assertRaisesRegex(ValueError, "object"):
                bench.validate_counters(bad)
        values = counters()
        values["copied_bytes"] = []
        with self.assertRaisesRegex(ValueError, "object"):
            bench.validate_counters(values)
        for bad in [None, [], 4, "event"]:
            with self.assertRaisesRegex(ValueError, "object"):
                bench.RunState(1).event(bad, 0)

    def test_incomplete_tail_is_not_fabricated(self):
        self.assertIsNone(bench.percentile([1] * 99, .99))
        self.assertEqual(1, bench.percentile([1] * 99 + [100], .99))

    def test_duplicate_completion_and_wrong_result(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1)
        state.event({"kind": "admitted", "id": 0}, 2)
        event = {"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}
        state.event(event, 3)
        with self.assertRaisesRegex(ValueError, "duplicate"):
            state.event(event, 4)
        broken = bench.RunState(1)
        broken.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2)
        with self.assertRaisesRegex(ValueError, "oracle"):
            broken.event(dict(event, value=0), 3)

    def test_timeout_keeps_late_completion(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2, completion_ns=10, status="timeout")
        state.event({"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}, 20)
        self.assertEqual("timeout", state.records[0]["status"])
        self.assertEqual(20, state.late_events[0]["observed_ns"])

    def test_late_event_cannot_win_before_timeout_poller(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2, deadline_ns=10)
        state.event({"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}, 11)
        self.assertEqual("timeout", state.records[0]["status"])
        self.assertEqual(11, state.records[0]["completion_ns"])
        self.assertEqual(10, state.records[0]["deadline_ns"])
        self.assertEqual(1, len(state.late_events))

    def test_domain_oracle_requires_exact_dataset_and_preserves_result_shape(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "expected.json"
            data.write_text(json.dumps([{"input": "query-1", "expected": {"rows": 2, "digest": "abc"}}]))
            plan = {"timing": {"requests": 1}, "oracle": {"kind": "expected-values", "dataset": "expected.json", "sha256": bench.sha256(data)}}
            oracle = bench.Oracle(plan, root)
            self.assertEqual("query-1", oracle.input(4))
            state = bench.RunState(1, oracle)
            state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2)
            state.event({"kind": "completed", "id": 0, "status": "ok", "value": {"rows": 2, "digest": "abc"}, "counters": counters()}, 3)
            self.assertEqual("ok", state.records[0]["status"])
            data.write_text("[]")
            with self.assertRaisesRegex(ValueError, "hash"):
                bench.Oracle(plan, root)

    def test_predeclared_noise_band_and_independent_repetitions(self):
        band = {"baseline_id": "finite-test", "absolute_noise_ns": 2, "relative_noise": .01}
        self.assertEqual("regression", bench.noise_comparison([10, 10, 10], [30, 30, 30], band, 42)["decision"])
        self.assertEqual("within_band", bench.noise_comparison([10, 10], [10, 10], band, 42)["decision"])
        self.assertEqual("inconclusive", bench.noise_comparison([10], [30], band, 42)["decision"])

    def test_process_observer_does_not_invent_unavailable_values(self):
        result = bench.observe_process(2 ** 31 - 1)
        self.assertIsNone(result["rss_bytes"]["value"])
        self.assertIsNone(result["all_thread_stacks_bytes"]["value"])


class ProcessTests(unittest.IsolatedAsyncioTestCase):
    async def fixture(self, fault=None, requests=4, max_in_flight=8, lag=100_000_000):
        with tempfile.TemporaryDirectory(prefix="nw-a21-") as temporary:
            base = Path(temporary)
            source = base / "target.py"
            source.write_text('''import importlib.util,json,sys,time
s=importlib.util.spec_from_file_location("bench",sys.argv[1]);b=importlib.util.module_from_spec(s);s.loader.exec_module(b)
c={k:b.unknown(v,"fixture unavailable") for k,v in b.COUNTERS.items()}
print(json.dumps({"kind":"hello","route":"managed-aot","dynamic_code_supported":False}),flush=True)
for line in sys.stdin:
 i,value,mode=line.strip().split("\\t");i=int(i)
 if mode=="malformed":
  print("[]",flush=True);continue
 print(json.dumps({"kind":"admitted","id":i}),flush=True)
 if mode=="timeout":continue
 if mode=="late":time.sleep(.08)
 c["managed_allocated_bytes"]=b.measured(i+1,"bytes","fixture observation")
 event={"kind":"completed","id":i,"status":"ok" if mode in ["corrupt","late"] else mode,"value":0 if mode=="corrupt" else b.mix64(int(value)),"counters":c}
 print(json.dumps(event),flush=True)
''')
            plan = {"timing": {"requests": requests, "period_ns": 5_000_000, "timeout_ns": 50_000_000,
                               "max_dispatch_lag_ns": lag, "max_in_flight": max_in_flight, "cleanup_timeout_ns": 500_000_000},
                    "settings": {"warmup_requests": 0, "instrumentation": "protocol fixture"},
                    "histogram": {"resolution_ns": 1000, "maximum_ns": 1_000_000_000}, "workload_kind": "harness-smoke",
                    "faults": fault or {}}
            route = {"id": "managed-aot", "command": [sys.executable, str(source), str(ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")], "artifacts": {}}
            result = await bench.run_cell(plan, route, 0, base, base / "result.json")
            self.assertTrue((base / "result.json").exists())
            result["test_raw_lines"] = [json.loads(line) for line in (base / result["raw_events"]["path"]).read_text().splitlines()]
            return result

    async def test_actual_dispatch_and_all_outcomes_are_retained(self):
        result = await self.fixture({"1": "failed", "2": "rejected", "3": "timeout"})
        self.assertTrue(result["valid"], result["invalid_reasons"])
        self.assertEqual({"ok": 1, "failed": 1, "rejected": 1, "timeout": 1}, result["outcome_counts"])
        self.assertEqual(4, result["histograms"]["arrival_all_outcomes"]["count"])
        self.assertEqual(4, result["offered"])
        self.assertEqual(4, result["admitted"])
        self.assertEqual(3, result["completed"])

    async def test_integrity_fault_stops_further_dispatch_and_retains_rows(self):
        result = await self.fixture({"0": "corrupt"})
        self.assertFalse(result["valid"])
        self.assertTrue(any("oracle" in error for error in result["invalid_reasons"]))
        self.assertEqual(4, len(result["records"]))
        self.assertIn("cancelled", result["outcome_counts"])

    async def test_generator_capacity_invalidates_cell(self):
        result = await self.fixture({"0": "timeout"}, max_in_flight=1)
        self.assertFalse(result["valid"])
        self.assertIn("generator saturation: in-flight capacity", result["invalid_reasons"])
        self.assertEqual(4, result["offered"])

    async def test_generator_dispatch_lag_invalidates_cell(self):
        result = await self.fixture(lag=1)
        self.assertFalse(result["valid"])
        self.assertIn("generator saturation: dispatch lag", result["invalid_reasons"])

    async def test_malformed_event_is_persisted_before_rejection(self):
        result = await self.fixture({"0": "malformed"})
        self.assertFalse(result["valid"])
        self.assertEqual(b"[]\n", base64.b64decode(result["test_raw_lines"][0]["wire_base64"]))
        self.assertEqual(4, len(result["records"]))

    async def test_every_completion_counter_is_retained(self):
        result = await self.fixture(requests=2)
        self.assertEqual([1, 2], [row["target_counters"]["managed_allocated_bytes"]["value"] for row in result["records"]])

    async def test_late_completion_throughput_uses_observed_endpoint(self):
        result = await self.fixture({"0": "late"}, requests=1)
        self.assertEqual("timeout", result["records"][0]["status"])
        self.assertEqual(1, result["completed"])
        self.assertGreaterEqual(result["measurement_window_ns"]["end"], result["late_events"][0]["observed_ns"])
        self.assertAlmostEqual(1e9 / result["measurement_window_ns"]["elapsed"], result["rates_per_second"]["completed"])
        self.assertLess(result["rates_per_second"]["completed"], 15)


if __name__ == "__main__":
    unittest.main()
